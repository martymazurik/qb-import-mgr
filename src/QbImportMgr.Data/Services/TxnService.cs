using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Rules;
using QbImportMgr.Data.Db;

namespace QbImportMgr.Data.Services;

public sealed class TxnLockedException(long txnId)
    : InvalidOperationException($"Transaction {txnId} has been exported and is locked. Void its batch to edit it.");

public sealed class TxnStateException(string message) : InvalidOperationException(message);

/// <summary>Edits to transactions. Every mutation is audited; exported rows are refused (the DB triggers are the backstop).</summary>
public sealed class TxnService(Database db, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public Txn SetCategory(long txnId, long? categoryId) => Edit(txnId, "SetCategory", (s, t) =>
    {
        if (categoryId is not null && s.Splits.ForTxn(t.Id).Count > 0) s.Splits.Replace(t.Id, []);
        t.CategoryId = categoryId;
        return new { categoryId };
    });

    public Txn SetName(long txnId, long? qbNameId) => Edit(txnId, "SetName", (_, t) => { t.QbNameId = qbNameId; return new { qbNameId }; });

    public Txn SetClass(long txnId, string? cls) =>
        Edit(txnId, "SetClass", (_, t) => { t.Class = string.IsNullOrWhiteSpace(cls) ? null : cls.Trim(); return new { t.Class }; });

    /// <summary>Overrides; pass null to revert a field to the raw bank value.</summary>
    public Txn SetOverrides(long txnId, DateOnly? date, string? payee, string? memo, string? checkNum) => Edit(txnId, "SetOverrides", (_, t) =>
    {
        t.OvrDate = date == t.DtPosted ? null : date;
        t.OvrPayee = payee == t.Name ? null : payee;
        t.OvrMemo = memo == t.Memo ? null : memo;
        t.OvrCheckNum = checkNum == t.CheckNum ? null : checkNum;
        return new { t.OvrDate, t.OvrPayee, t.OvrMemo, t.OvrCheckNum };
    });

    /// <summary>Replaces the splits; a txn with splits has no CategoryId. An empty list removes all splits.</summary>
    public Txn SetSplits(long txnId, IReadOnlyList<TxnSplit> splits) => Edit(txnId, "SetSplits", (s, t) =>
    {
        s.Splits.Replace(t.Id, splits);
        if (splits.Count > 0) t.CategoryId = null;
        return new { Splits = splits.Select(x => new { x.CategoryId, x.AmountCents, x.QbNameId, x.Class }) };
    });

    public IReadOnlyList<Txn> MarkReady(IEnumerable<long> txnIds) => txnIds.Select(id => Edit(id, "MarkReady", (s, t) =>
    {
        if (t.Status is TxnStatus.Excluded or TxnStatus.Duplicate)
            throw new TxnStateException($"Transaction {t.Id} is {t.Status}; resolve it before marking it Ready.");
        if (!IsCategorized(s, t))
            throw new TxnStateException($"Transaction {t.Id} is uncategorized.");
        t.Status = TxnStatus.Ready;
        return null;
    }, deriveStatus: false)).ToList();

    public Txn Unready(long txnId) => Edit(txnId, "Unready", (s, t) =>
    {
        if (t.Status == TxnStatus.Ready) t.Status = IsCategorized(s, t) ? TxnStatus.Categorized : TxnStatus.New;
        return null;
    }, deriveStatus: false);

    public Txn Exclude(long txnId) => Edit(txnId, "Exclude", (_, t) => { t.Status = TxnStatus.Excluded; return null; }, deriveStatus: false);

    /// <summary>Excluded back to New, re-running aliases and rules.</summary>
    public Txn Include(long txnId) => Edit(txnId, "Include", (s, t) =>
    {
        if (t.Status != TxnStatus.Excluded) throw new TxnStateException($"Transaction {t.Id} is not Excluded.");
        ResetToNewAndApplyRules(s, t);
        return new { t.Status, t.RuleId };
    }, deriveStatus: false);

    /// <summary>"Not a duplicate": back to New, DuplicateOfTxnId cleared, aliases and rules re-run.</summary>
    public Txn NotADuplicate(long txnId) => Edit(txnId, "NotADuplicate", (s, t) =>
    {
        if (t.Status != TxnStatus.Duplicate) throw new TxnStateException($"Transaction {t.Id} is not flagged as a duplicate.");
        var was = t.DuplicateOfTxnId;
        t.DuplicateOfTxnId = null;
        ResetToNewAndApplyRules(s, t);
        return new { WasDuplicateOf = was, t.Status, t.RuleId };
    }, deriveStatus: false);

    /// <summary>"Confirm duplicate": moves to Excluded, keeping DuplicateOfTxnId for the record.</summary>
    public Txn ConfirmDuplicate(long txnId) => Edit(txnId, "ConfirmDuplicate", (_, t) =>
    {
        if (t.Status != TxnStatus.Duplicate) throw new TxnStateException($"Transaction {t.Id} is not flagged as a duplicate.");
        t.Status = TxnStatus.Excluded;
        return new { t.DuplicateOfTxnId };
    }, deriveStatus: false);

    /// <summary>Saves a rule and applies it (and all other rules) to every New transaction. Returns how many changed.</summary>
    public (Rule Rule, int Applied) SaveRuleAndApply(Rule rule)
    {
        if (TextMatcher.Validate(rule.MatchKind, rule.Pattern) is { } err) throw new ArgumentException(err, nameof(rule));
        using var s = db.Open();
        return s.InTransaction(() =>
        {
            if (rule.Id == 0) s.Rules.Insert(rule); else s.Rules.Update(rule);
            s.Audit.Write("Rule", rule.Id, "Saved", new { rule.Pattern, rule.MatchKind, rule.Field, rule.CategoryId, rule.Priority }, Now);
            return (rule, ApplyRulesToNew(s));
        });
    }

    public int ApplyRulesToNew()
    {
        using var s = db.Open();
        return s.InTransaction(() => ApplyRulesToNew(s));
    }

    private int ApplyRulesToNew(DbSession s)
    {
        var engine = new RuleEngine(s.Rules.GetAll());
        var hits = new Dictionary<long, int>();
        int changed = 0;
        foreach (var t in s.Txns.ListByStatus(TxnStatus.New))
        {
            if (engine.Apply(t) is not { } outcome) continue;
            t.UpdatedUtc = Now;
            s.Txns.UpdateEditable(t);
            s.Audit.Write("Txn", t.Id, "RuleApplied", new { outcome.Rule.Id, Status = t.Status.ToString() }, Now);
            hits[outcome.Rule.Id] = hits.GetValueOrDefault(outcome.Rule.Id) + 1;
            changed++;
        }
        s.Rules.AddHits(hits, Now);
        return changed;
    }

    private void ResetToNewAndApplyRules(DbSession s, Txn t)
    {
        t.Status = TxnStatus.New;
        ImportService.ApplyAlias(t, s.Aliases.GetAll());
        if (new RuleEngine(s.Rules.GetAll()).Apply(t) is { } outcome)
            s.Rules.AddHits(new Dictionary<long, int> { [outcome.Rule.Id] = 1 }, Now);
        else if (IsCategorized(s, t))
            t.Status = TxnStatus.Categorized; // keep a category the user set before it was flagged
    }

    private static bool IsCategorized(DbSession s, Txn t) => t.CategoryId is not null || s.Splits.ForTxn(t.Id).Count > 0;

    /// <summary>
    /// Loads, refuses locked rows, applies the change, re-derives New/Categorized (a Ready row that
    /// loses its category drops back to New), saves and audits, all in one transaction.
    /// </summary>
    private Txn Edit(long txnId, string action, Func<DbSession, Txn, object?> change, bool deriveStatus = true)
    {
        using var s = db.Open();
        return s.InTransaction(() =>
        {
            var t = s.Txns.Get(txnId) ?? throw new KeyNotFoundException($"Transaction {txnId} not found.");
            if (t.IsLocked) throw new TxnLockedException(txnId);
            var before = t.Status;

            var detail = change(s, t);

            if (deriveStatus && t.Status is TxnStatus.New or TxnStatus.Categorized or TxnStatus.Ready)
            {
                bool categorized = IsCategorized(s, t);
                t.Status = t.Status switch
                {
                    TxnStatus.Ready when categorized => TxnStatus.Ready,
                    _ when categorized => TxnStatus.Categorized,
                    _ => TxnStatus.New,
                };
            }

            t.UpdatedUtc = Now;
            s.Txns.UpdateEditable(t);
            s.Audit.Write("Txn", t.Id, action, new { From = before.ToString(), To = t.Status.ToString(), Detail = detail }, Now);
            return t;
        });
    }
}
