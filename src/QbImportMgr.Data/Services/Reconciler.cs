using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Qb;
using QbImportMgr.Core.Rules;
using QbImportMgr.Data.Db;

namespace QbImportMgr.Data.Services;

public sealed record ReconcileSummary(int Matched, int Ambiguous, int AmbiguityCleared, int Paired, int Held, int Released)
{
    public static readonly ReconcileSummary Empty = new(0, 0, 0, 0, 0, 0);
}

/// <summary>Finds the bank account a QB register account name refers to: full name, or the leaf of "Parent:Child".</summary>
internal static class AccountResolver
{
    public static BankAccount? Resolve(string? qbAccountName, IEnumerable<BankAccount> accounts)
    {
        if (string.IsNullOrWhiteSpace(qbAccountName)) return null;
        var name = qbAccountName.Trim();
        var mapped = accounts.Where(a => a.QbAccountName is not null).ToList();
        return mapped.FirstOrDefault(a => a.QbAccountName!.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? mapped.FirstOrDefault(a => Leaf(a.QbAccountName!).Equals(Leaf(name), StringComparison.OrdinalIgnoreCase));
    }

    private static string Leaf(string s) => s.Split(':')[^1].Trim();
}

/// <summary>Runs rules over transactions, including rules that apply a split template, and counts hits.</summary>
internal sealed class RuleRunner(DbSession s, DateTime now)
{
    private readonly RuleEngine _engine = new(s.Rules.GetAll());
    private readonly IReadOnlyList<PayeeAlias> _aliases = s.Aliases.GetAll();
    private readonly Dictionary<long, SplitTemplate> _templates = s.Templates.GetAll().ToDictionary(t => t.Id);
    private readonly Dictionary<long, int> _hits = [];

    public void ApplyAlias(Txn t) => ImportService.ApplyAlias(t, _aliases);

    /// <summary>Applies the first matching rule to a New txn (in memory). Call <see cref="AfterSave"/> once the txn has an Id.</summary>
    public RuleOutcome? Apply(Txn t)
    {
        var o = _engine.Apply(t);
        if (o is not null) _hits[o.Rule.Id] = _hits.GetValueOrDefault(o.Rule.Id) + 1;
        return o;
    }

    /// <summary>For a rule with a split template: writes the splits and marks the txn Categorized.</summary>
    public void AfterSave(Txn t, RuleOutcome? o)
    {
        if (o?.Rule is not { AutoExclude: false, SplitTemplateId: { } tid } || !_templates.TryGetValue(tid, out var template) || template.Lines.Count == 0)
            return;
        s.Splits.Replace(t.Id, SplitTemplateMath.Apply(template.Lines, t.SignedAmountCents).ToList());
        t.CategoryId = null;
        t.Status = TxnStatus.Categorized;
        s.Txns.UpdateEditable(t);
    }

    public void Flush() => s.Rules.AddHits(_hits, now);

    /// <summary>Aliases and rules over every New row. Returns the before-state of rows that changed.</summary>
    public static List<TxnSnapshot> ApplyToAllNew(DbSession s, DateTime now)
    {
        var runner = new RuleRunner(s, now);
        var before = new List<TxnSnapshot>();
        foreach (var t in s.Txns.ListByStatus(TxnStatus.New))
        {
            var snap = TxnService.SnapshotOf(s, t);
            var oldPayee = t.OvrPayee;
            var oldName = t.QbNameId;
            runner.ApplyAlias(t);
            var o = runner.Apply(t);
            if (o is null && t.OvrPayee == oldPayee && t.QbNameId == oldName) continue;
            before.Add(snap);
            t.UpdatedUtc = now;
            s.Txns.UpdateEditable(t);
            runner.AfterSave(t, o);
            s.Audit.Write("Txn", t.Id, o is null ? "AliasApplied" : "RuleApplied", new { RuleId = o?.Rule.Id, Status = t.Status.ToString() }, now);
        }
        runner.Flush();
        return before;
    }
}

/// <summary>Invoiced-customer deposits wait for Receive Payments in QuickBooks.</summary>
internal static class Holds
{
    public const string Note = "Record with Receive Payments in QB, then re-import register.";

    public static bool ShouldHold(Txn t, IReadOnlySet<long> invoicedNameIds) =>
        t.SignedAmountCents > 0 && t.QbNameId is { } n && invoicedNameIds.Contains(n) && t.QbRegisterTxnId is null;

    public static bool IsOpen(TxnStatus s) => s is TxnStatus.New or TxnStatus.Categorized or TxnStatus.Ready or TxnStatus.HoldReceivePayment;
}

/// <summary>
/// After anything that changes what is known about a transaction (bank import, register import,
/// name settings): bind register rows to mapped accounts, match bank rows to the QB register, pair
/// transfers, and apply invoiced holds. All within the caller's transaction.
/// </summary>
internal static class Reconciler
{
    private static readonly HashSet<TxnStatus> Matchable =
        [TxnStatus.New, TxnStatus.Categorized, TxnStatus.Ready, TxnStatus.Ambiguous, TxnStatus.HoldReceivePayment, TxnStatus.Paired];

    public static ReconcileSummary Run(DbSession s, IEnumerable<long>? accountIds, DateTime now)
    {
        var accounts = s.Accounts.GetAll();
        BindRegisterRows(s, accounts);
        var ids = accountIds?.ToHashSet() ?? accounts.Select(a => a.Id).ToHashSet();

        int matched = 0, ambiguous = 0, cleared = 0;
        foreach (var id in ids)
        {
            var (m, a, c) = MatchAccount(s, id, now);
            matched += m; ambiguous += a; cleared += c;
        }
        int paired = PairTransfers(s, accounts, now);
        var (held, released) = ApplyHolds(s, now);
        return new ReconcileSummary(matched, ambiguous, cleared, paired, held, released);
    }

    public static void BindRegisterRows(DbSession s, IReadOnlyList<BankAccount> accounts)
    {
        foreach (var name in s.QbRegister.UnboundAccountNames())
            if (AccountResolver.Resolve(name, accounts) is { } a)
                s.QbRegister.Bind(name, a.Id);
    }

    /// <summary>Category for an open row that just lost a special status.</summary>
    internal static TxnStatus OpenStatus(DbSession s, Txn t) =>
        t.CategoryId is not null || s.Splits.ForTxn(t.Id).Count > 0 ? TxnStatus.Categorized : TxnStatus.New;

    private static (int Matched, int Ambiguous, int Cleared) MatchAccount(DbSession s, long accountId, DateTime now)
    {
        var txns = s.Txns.ListByAccount(accountId).ToDictionary(t => t.Id);
        var qb = s.QbRegister.Unlinked(accountId);
        var bank = txns.Values
            .Where(t => t.QbRegisterTxnId is null && (Matchable.Contains(t.Status) || t.Status is TxnStatus.Exported or TxnStatus.Excluded))
            .Select(t => new MatchBank(t.Id, accountId, t.DtPosted, t.SignedAmountCents, t.EffectiveCheckNum, t.Name ?? t.EffectivePayee, t.Memo,
                LinkOnly: t.Status is TxnStatus.Exported or TxnStatus.Excluded))
            .ToList();

        var result = qb.Count == 0 || bank.Count == 0
            ? new MatchResult(new Dictionary<long, long>(), new Dictionary<long, IReadOnlyList<long>>())
            : QbMatcher.Match(bank, qb.Select(ToMatchQb), s.Rejections.QbRejections(accountId));

        int matched = 0, ambiguous = 0, cleared = 0;
        foreach (var (txnId, qbId) in result.Matches)
        {
            var t = txns[txnId];
            var from = t.Status;
            t.QbRegisterTxnId = qbId;
            if (t.Status is not (TxnStatus.Exported or TxnStatus.Excluded)) t.Status = TxnStatus.InQb;
            Save(s, t, "MatchedInQb", new { From = from.ToString(), QbRegisterTxnId = qbId }, now);
            matched++;
        }
        foreach (var (txnId, _) in result.Ambiguous)
        {
            var t = txns[txnId];
            if (t.Status is TxnStatus.Exported or TxnStatus.Excluded or TxnStatus.Ambiguous) continue;
            var from = t.Status;
            t.Status = TxnStatus.Ambiguous;
            Save(s, t, "AmbiguousQbMatch", new { From = from.ToString() }, now);
            ambiguous++;
        }
        foreach (var t in txns.Values.Where(t => t.Status == TxnStatus.Ambiguous && !result.Matches.ContainsKey(t.Id) && !result.Ambiguous.ContainsKey(t.Id)))
        {
            t.Status = t.PairedTxnId is not null && IsPairedInSide(s, t) ? TxnStatus.Paired : OpenStatus(s, t);
            Save(s, t, "AmbiguityCleared", new { To = t.Status.ToString() }, now);
            cleared++;
        }
        return (matched, ambiguous, cleared);
    }

    private static bool IsPairedInSide(DbSession s, Txn t) => t.SignedAmountCents > 0;

    internal static MatchQb ToMatchQb(QbRegisterTxn q) =>
        new(q.Id, q.BankAccountId ?? 0, q.TxnDate, q.AmountCents, q.Num, q.Name, q.Memo, q.TxnType);

    private static int PairTransfers(DbSession s, IReadOnlyList<BankAccount> accounts, DateTime now)
    {
        var mapped = accounts.Where(a => a.QbAccountName is not null).ToDictionary(a => a.Id);
        if (mapped.Count < 2) return 0;

        var txns = s.Txns.ListByAccounts(mapped.Keys)
            .Where(t => t.PairedTxnId is null && t.Status is TxnStatus.New or TxnStatus.Categorized or TxnStatus.Ready or TxnStatus.InQb or TxnStatus.Exported)
            .ToDictionary(t => t.Id);
        var pairs = TransferPairer.Pair(
            txns.Values.Select(t => new PairCandidate(t.Id, t.BankAccountId, t.DtPosted, t.SignedAmountCents,
                t.Status is TxnStatus.InQb or TxnStatus.Exported, t.Name, t.Memo)),
            s.Rejections.TransferRejections());

        foreach (var p in pairs)
        {
            var o = txns[p.OutTxnId];
            var i = txns[p.InTxnId];
            o.PairedTxnId = i.Id;
            i.PairedTxnId = o.Id;
            bool outInQb = o.Status is TxnStatus.InQb or TxnStatus.Exported;
            bool inInQb = i.Status is TxnStatus.InQb or TxnStatus.Exported;

            if (outInQb) i.Status = TxnStatus.Paired;
            else if (inInQb) o.Status = TxnStatus.Paired;
            else
            {
                // The out side carries the export: offset account = the other account's QB name.
                i.Status = TxnStatus.Paired;
                if (s.Categories.GetByName(mapped[i.BankAccountId].QbAccountName!) is { } cat && o.CategoryId != cat.Id)
                {
                    if (s.Splits.ForTxn(o.Id).Count > 0) s.Splits.Replace(o.Id, []);
                    o.CategoryId = cat.Id;
                    o.Status = TxnStatus.Categorized;
                }
            }
            Save(s, o, "TransferPaired", new { With = i.Id, Status = o.Status.ToString() }, now);
            Save(s, i, "TransferPaired", new { With = o.Id, Status = i.Status.ToString() }, now);
        }
        return pairs.Count;
    }

    private static (int Held, int Released) ApplyHolds(DbSession s, DateTime now)
    {
        var invoiced = s.Names.InvoicedIds();
        int held = 0, released = 0;
        var open = new[] { TxnStatus.New, TxnStatus.Categorized, TxnStatus.Ready, TxnStatus.HoldReceivePayment }
            .SelectMany(st => s.Txns.ListByStatus(st));
        foreach (var t in open)
        {
            bool hold = Holds.ShouldHold(t, invoiced);
            if (hold && t.Status != TxnStatus.HoldReceivePayment)
            {
                var from = t.Status;
                t.Status = TxnStatus.HoldReceivePayment;
                Save(s, t, "HoldReceivePayment", new { From = from.ToString(), Holds.Note }, now);
                held++;
            }
            else if (!hold && t.Status == TxnStatus.HoldReceivePayment)
            {
                t.Status = OpenStatus(s, t);
                Save(s, t, "HoldReleased", new { To = t.Status.ToString() }, now);
                released++;
            }
        }
        return (held, released);
    }

    private static void Save(DbSession s, Txn t, string action, object detail, DateTime now)
    {
        t.UpdatedUtc = now;
        s.Txns.UpdateEditable(t);
        s.Audit.Write("Txn", t.Id, action, detail, now);
    }
}
