using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Rules;
using QbImportMgr.Data.Db;

namespace QbImportMgr.Data.Services;

public sealed class TxnLockedException(long txnId)
    : InvalidOperationException($"Transaction {txnId} has been exported and is locked. Void its batch to edit it.");

public sealed class TxnStateException(string message) : InvalidOperationException(message);

/// <summary>Everything an undo needs to put a transaction back: all editable columns plus its splits.</summary>
public sealed record TxnSnapshot(
    long TxnId, TxnStatus Status, long? CategoryId, long? QbNameId, string? Class,
    DateOnly? OvrDate, string? OvrPayee, string? OvrMemo, string? OvrCheckNum,
    long? DuplicateOfTxnId, long? RuleId, IReadOnlyList<TxnSplit> Splits,
    long? QbRegisterTxnId = null, long? PairedTxnId = null);

/// <summary>A QB register row offered in the "Pick match" list.</summary>
public sealed record QbMatchOption(QbRegisterTxn Row, bool CheckNumberMatch, int DayDiff, double NameSimilarity);

public sealed record BulkResult(IReadOnlyList<Txn> Changed, IReadOnlyList<string> Skipped);

public sealed record RuleSaveResult(Rule Rule, IReadOnlyList<TxnSnapshot> ChangedBefore);

/// <summary>Edits to transactions. Every mutation is audited; exported rows are refused (DB triggers are the backstop).</summary>
public sealed class TxnService(Database db, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ single-row edits

    public Txn SetCategory(long txnId, long? categoryId) => SetCategory([txnId], categoryId).Changed.Single();

    public BulkResult SetCategory(IReadOnlyCollection<long> txnIds, long? categoryId) => Bulk(txnIds, "SetCategory", (s, t) =>
    {
        if (categoryId is not null && s.Splits.ForTxn(t.Id).Count > 0) s.Splits.Replace(t.Id, []);
        t.CategoryId = categoryId;
        return new { categoryId };
    });

    public Txn SetName(long txnId, long? qbNameId) =>
        Single(txnId, "SetName", (_, t) => { t.QbNameId = qbNameId; return new { qbNameId }; });

    public Txn SetClass(long txnId, string? cls) =>
        Single(txnId, "SetClass", (_, t) => { t.Class = string.IsNullOrWhiteSpace(cls) ? null : cls.Trim(); return new { t.Class }; });

    /// <summary>Overrides; pass null (or the raw value) to revert a field to the bank value.</summary>
    public Txn SetOverrides(long txnId, DateOnly? date, string? payee, string? memo, string? checkNum) => Single(txnId, "SetOverrides", (_, t) =>
    {
        t.OvrDate = date == t.DtPosted ? null : date;
        t.OvrPayee = Blank(payee) || payee == t.Name ? null : payee!.Trim();
        t.OvrMemo = memo == t.Memo || (memo is "" && t.Memo is null) ? null : memo;
        t.OvrCheckNum = checkNum == t.CheckNum || (checkNum is "" && t.CheckNum is null) ? null : checkNum;
        return new { t.OvrDate, t.OvrPayee, t.OvrMemo, t.OvrCheckNum };
    });

    /// <summary>Replaces the splits; a txn with splits has no CategoryId. An empty list removes all splits.</summary>
    public Txn SetSplits(long txnId, IReadOnlyList<TxnSplit> splits) => Single(txnId, "SetSplits", (s, t) =>
    {
        s.Splits.Replace(t.Id, splits);
        if (splits.Count > 0) t.CategoryId = null;
        return new { Splits = splits.Select(x => new { x.CategoryId, x.AmountCents, x.QbNameId, x.Class }) };
    });

    // ------------------------------------------------------------------ status changes

    /// <summary>Strict: every row must be categorized (with balanced splits) and not Excluded/Duplicate.</summary>
    public IReadOnlyList<Txn> MarkReady(IEnumerable<long> txnIds) => Bulk(txnIds.ToList(), "MarkReady", (s, t) =>
    {
        if (ReadyBlocker(s, t) is { } why) throw new TxnStateException(why);
        t.Status = TxnStatus.Ready;
        return null;
    }, deriveStatus: false).Changed;

    /// <summary>Grid "Enter": Categorized rows become Ready; anything else is skipped with a reason.</summary>
    public BulkResult Approve(IReadOnlyCollection<long> txnIds) => Bulk(txnIds, "Approve", (s, t) =>
    {
        if (t.Status != TxnStatus.Categorized) throw new SkipException(t.Status == TxnStatus.Ready ? null : $"{t.Status}");
        if (ReadyBlocker(s, t) is { } why) throw new SkipException(why);
        t.Status = TxnStatus.Ready;
        return null;
    }, deriveStatus: false, skipLocked: true);

    public Txn Unready(long txnId) => Single(txnId, "Unready", (s, t) =>
    {
        if (t.Status == TxnStatus.Ready) t.Status = IsCategorized(s, t) ? TxnStatus.Categorized : TxnStatus.New;
        return null;
    }, deriveStatus: false);

    public Txn Exclude(long txnId) =>
        Single(txnId, "Exclude", (_, t) => { t.Status = TxnStatus.Excluded; return null; }, deriveStatus: false);

    public BulkResult Exclude(IReadOnlyCollection<long> txnIds) =>
        Bulk(txnIds, "Exclude", (_, t) => { t.Status = TxnStatus.Excluded; return null; }, deriveStatus: false, skipLocked: true);

    /// <summary>Excluded back to New, re-running aliases and rules.</summary>
    public Txn Include(long txnId) => Single(txnId, "Include", (s, t) =>
    {
        if (t.Status != TxnStatus.Excluded) throw new TxnStateException($"Transaction {t.Id} is not Excluded.");
        ResetToNewAndApplyRules(s, t);
        return new { t.Status, t.RuleId };
    }, deriveStatus: false);

    /// <summary>"Not a duplicate": back to New, DuplicateOfTxnId cleared, aliases and rules re-run.</summary>
    public Txn NotADuplicate(long txnId) => Single(txnId, "NotADuplicate", (s, t) =>
    {
        if (t.Status != TxnStatus.Duplicate) throw new TxnStateException($"Transaction {t.Id} is not flagged as a duplicate.");
        var was = t.DuplicateOfTxnId;
        t.DuplicateOfTxnId = null;
        ResetToNewAndApplyRules(s, t);
        return new { WasDuplicateOf = was, t.Status, t.RuleId };
    }, deriveStatus: false);

    /// <summary>"Confirm duplicate": moves to Excluded, keeping DuplicateOfTxnId for the record.</summary>
    public Txn ConfirmDuplicate(long txnId) => Single(txnId, "ConfirmDuplicate", (_, t) =>
    {
        if (t.Status != TxnStatus.Duplicate) throw new TxnStateException($"Transaction {t.Id} is not flagged as a duplicate.");
        t.Status = TxnStatus.Excluded;
        return new { t.DuplicateOfTxnId };
    }, deriveStatus: false);

    // ------------------------------------------------------------------ rules

    /// <summary>Saves a rule and applies all rules to every New transaction. Returns the before-state of rows it changed (for undo).</summary>
    public RuleSaveResult SaveRuleAndApply(Rule rule)
    {
        if (TextMatcher.Validate(rule.MatchKind, rule.Pattern) is { } err) throw new ArgumentException(err, nameof(rule));
        using var s = db.Open();
        return s.InTransaction(() =>
        {
            if (rule.Id == 0) s.Rules.Insert(rule); else s.Rules.Update(rule);
            s.Audit.Write("Rule", rule.Id, "Saved", new { rule.Pattern, rule.MatchKind, rule.Field, rule.CategoryId, rule.SplitTemplateId, rule.Priority }, Now);
            var before = RuleRunner.ApplyToAllNew(s, Now);
            Reconciler.Run(s, [], Now); // a rule can assign an invoiced customer: holds
            return new RuleSaveResult(rule, before);
        });
    }

    public int ApplyRulesToNew()
    {
        using var s = db.Open();
        return s.InTransaction(() => RuleRunner.ApplyToAllNew(s, Now)).Count;
    }

    // ------------------------------------------------------------------ QuickBooks register matches

    /// <summary>Ranked QB register rows this transaction could be (unlinked rows only; rejected ones left out).</summary>
    public IReadOnlyList<QbMatchOption> MatchOptions(long txnId)
    {
        using var s = db.Open();
        var t = s.Txns.Get(txnId) ?? throw new KeyNotFoundException($"Transaction {txnId} not found.");
        var rejected = s.Rejections.QbRejections(t.BankAccountId);
        var available = s.QbRegister.Unlinked(t.BankAccountId).Where(q => !rejected.Contains((t.Id, q.Id))).ToDictionary(q => q.Id);
        var bank = new Core.Qb.MatchBank(t.Id, t.BankAccountId, t.DtPosted, t.SignedAmountCents, t.EffectiveCheckNum, t.Name ?? t.EffectivePayee, t.Memo);
        return Core.Qb.QbMatcher.Candidates(bank, available.Values.Select(Reconciler.ToMatchQb))
            .Select(c => new QbMatchOption(available[c.Qb.Id], c.CheckNumberMatch, c.DayDiff, c.NameSimilarity))
            .ToList();
    }

    /// <summary>"Pick match": link to the chosen QB register row; the transaction is InQb.</summary>
    public Txn PickMatch(long txnId, long qbRegisterTxnId) => Single(txnId, "PickQbMatch", (s, t) =>
    {
        if (t.Status != TxnStatus.Ambiguous && t.QbRegisterTxnId is not null)
            throw new TxnStateException($"Transaction {t.Id} is already matched.");
        var q = s.QbRegister.Get(qbRegisterTxnId) ?? throw new KeyNotFoundException($"QB register row {qbRegisterTxnId} not found.");
        if (q.BankAccountId != t.BankAccountId || q.AmountCents != t.SignedAmountCents)
            throw new TxnStateException("That QB row is for a different account or amount.");
        if (s.Txns.GetByQbRegisterTxn(q.Id) is { } other)
            throw new TxnStateException($"That QB row is already matched to transaction {other.Id}.");
        t.QbRegisterTxnId = q.Id;
        t.Status = TxnStatus.InQb;
        return new { QbRegisterTxnId = q.Id };
    }, deriveStatus: false);

    /// <summary>
    /// "Not in QB": unlink (remembering the rejection so it is not re-matched), back to New, rules re-run.
    /// For an Ambiguous row every current candidate is rejected.
    /// </summary>
    public Txn NotInQb(long txnId)
    {
        var rejectIds = MatchOptions(txnId).Select(o => o.Row.Id).ToList();
        return Single(txnId, "NotInQb", (s, t) =>
        {
            if (t.Status is not (TxnStatus.InQb or TxnStatus.Ambiguous))
                throw new TxnStateException($"Transaction {t.Id} is not matched to QuickBooks.");
            var was = t.QbRegisterTxnId;
            if (was is { } q) s.Rejections.RejectQb(t.Id, q, Now);
            if (t.Status == TxnStatus.Ambiguous) foreach (var id in rejectIds) s.Rejections.RejectQb(t.Id, id, Now);
            t.QbRegisterTxnId = null;
            ResetToNewAndApplyRules(s, t);
            return new { WasQbRegisterTxnId = was, Rejected = rejectIds.Count, t.Status };
        }, deriveStatus: false);
    }

    // ------------------------------------------------------------------ transfers

    /// <summary>Breaks a transfer pair (remembered, so it is not re-paired). Paired sides go back to New with rules re-run.</summary>
    public IReadOnlyList<Txn> Unpair(long txnId)
    {
        using var s = db.Open();
        return s.InTransaction(() =>
        {
            var a = s.Txns.Get(txnId) ?? throw new KeyNotFoundException($"Transaction {txnId} not found.");
            if (a.PairedTxnId is not { } otherId) throw new TxnStateException($"Transaction {txnId} is not part of a transfer.");
            var b = s.Txns.Get(otherId)!;
            var (o, i) = a.SignedAmountCents < 0 ? (a, b) : (b, a);
            s.Rejections.RejectTransfer(o.Id, i.Id, Now);
            foreach (var t in new[] { o, i })
            {
                var from = t.Status;
                t.PairedTxnId = null;
                if (t.Status == TxnStatus.Paired) ResetToNewAndApplyRules(s, t);
                t.UpdatedUtc = Now;
                s.Txns.UpdateEditable(t);
                s.Audit.Write("Txn", t.Id, "Unpair", new { From = from.ToString(), To = t.Status.ToString(), With = t == o ? i.Id : o.Id }, Now);
            }
            return (IReadOnlyList<Txn>)[o, i];
        });
    }

    // ------------------------------------------------------------------ invoiced customers

    /// <summary>Marks a QB name as invoiced (or not) and re-evaluates every open deposit's hold.</summary>
    public ReconcileSummary SetInvoiced(long qbNameId, bool invoiced)
    {
        using var s = db.Open();
        return s.InTransaction(() =>
        {
            s.Names.SetInvoiced(qbNameId, invoiced);
            s.Audit.Write("QbName", qbNameId, invoiced ? "SetInvoiced" : "ClearInvoiced", null, Now);
            return Reconciler.Run(s, [], Now);
        });
    }

    public void DeleteRule(long ruleId)
    {
        using var s = db.Open();
        s.InTransaction(() =>
        {
            s.Rules.Delete(ruleId);
            s.Audit.Write("Rule", ruleId, "Deleted", null, Now);
        });
    }

    // ------------------------------------------------------------------ undo

    public IReadOnlyList<TxnSnapshot> Snapshot(IEnumerable<long> txnIds)
    {
        using var s = db.Open();
        return s.Txns.GetMany(txnIds).Select(t => SnapshotOf(s, t)).ToList();
    }

    /// <summary>Puts rows back exactly as snapshotted (status, categorization, overrides, splits). Audited as "Undo".</summary>
    public IReadOnlyList<Txn> Restore(IReadOnlyList<TxnSnapshot> snapshots)
    {
        using var s = db.Open();
        return s.InTransaction(() => snapshots.Select(snap =>
        {
            var t = s.Txns.Get(snap.TxnId) ?? throw new KeyNotFoundException($"Transaction {snap.TxnId} not found.");
            if (t.IsLocked) throw new TxnLockedException(t.Id);
            var from = t.Status;
            s.Splits.Replace(t.Id, snap.Splits.Select(x => new TxnSplit
            {
                CategoryId = x.CategoryId, QbNameId = x.QbNameId, Class = x.Class, AmountCents = x.AmountCents, Memo = x.Memo,
            }).ToList());
            t.Status = snap.Status; t.CategoryId = snap.CategoryId; t.QbNameId = snap.QbNameId; t.Class = snap.Class;
            t.OvrDate = snap.OvrDate; t.OvrPayee = snap.OvrPayee; t.OvrMemo = snap.OvrMemo; t.OvrCheckNum = snap.OvrCheckNum;
            t.DuplicateOfTxnId = snap.DuplicateOfTxnId; t.RuleId = snap.RuleId;
            t.QbRegisterTxnId = snap.QbRegisterTxnId; t.PairedTxnId = snap.PairedTxnId;
            t.UpdatedUtc = Now;
            s.Txns.UpdateEditable(t);
            s.Audit.Write("Txn", t.Id, "Undo", new { From = from.ToString(), To = t.Status.ToString() }, Now);
            return t;
        }).ToList());
    }

    internal static TxnSnapshot SnapshotOf(DbSession s, Txn t) => new(
        t.Id, t.Status, t.CategoryId, t.QbNameId, t.Class, t.OvrDate, t.OvrPayee, t.OvrMemo, t.OvrCheckNum,
        t.DuplicateOfTxnId, t.RuleId, s.Splits.ForTxn(t.Id), t.QbRegisterTxnId, t.PairedTxnId);

    // ------------------------------------------------------------------ helpers

    private static string? ReadyBlocker(DbSession s, Txn t)
    {
        if (t.Status.IsNonExporting())
            return $"Transaction {t.Id} is {t.Status}; it is never exported" +
                   (t.Status is TxnStatus.Excluded or TxnStatus.Duplicate or TxnStatus.Ambiguous ? " until it is resolved." : ".");
        var splits = s.Splits.ForTxn(t.Id);
        if (t.CategoryId is null && splits.Count == 0) return $"Transaction {t.Id} is uncategorized.";
        if (splits.Count > 0 && !SplitMath.Balances(t.SignedAmountCents, splits.Select(x => x.AmountCents)))
            return $"Transaction {t.Id}: splits total {Money.FormatDisplay(splits.Sum(x => x.AmountCents))}, transaction is {Money.FormatDisplay(t.SignedAmountCents)}.";
        return null;
    }

    /// <summary>Back to New with aliases and rules (split templates too) re-run; a category the user set earlier is kept; holds apply.</summary>
    private void ResetToNewAndApplyRules(DbSession s, Txn t)
    {
        t.Status = TxnStatus.New;
        var runner = new RuleRunner(s, Now);
        runner.ApplyAlias(t);
        var outcome = runner.Apply(t);
        runner.Flush();
        if (outcome is null && IsCategorized(s, t))
            t.Status = TxnStatus.Categorized; // keep a category the user set before it was flagged
        runner.AfterSave(t, outcome);
        if (Holds.ShouldHold(t, s.Names.InvoicedIds()) && Holds.IsOpen(t.Status))
            t.Status = TxnStatus.HoldReceivePayment;
    }

    private static bool IsCategorized(DbSession s, Txn t) => t.CategoryId is not null || s.Splits.ForTxn(t.Id).Count > 0;

    private static bool Blank(string? v) => string.IsNullOrWhiteSpace(v);

    private sealed class SkipException(string? reason) : Exception(reason) { public string? Reason { get; } = reason; }

    private Txn Single(long txnId, string action, Func<DbSession, Txn, object?> change, bool deriveStatus = true) =>
        Bulk([txnId], action, change, deriveStatus).Changed.Single();

    /// <summary>
    /// One transaction for all rows: load, refuse locked rows (or skip them), apply the change,
    /// re-derive New/Categorized (a Ready row that loses its category drops back to New), save, audit.
    /// </summary>
    private BulkResult Bulk(IReadOnlyCollection<long> txnIds, string action, Func<DbSession, Txn, object?> change,
        bool deriveStatus = true, bool skipLocked = false)
    {
        using var s = db.Open();
        return s.InTransaction(() =>
        {
            var changed = new List<Txn>();
            var skipped = new List<string>();
            var invoiced = s.Names.InvoicedIds();
            foreach (var id in txnIds.Distinct())
            {
                var t = s.Txns.Get(id) ?? throw new KeyNotFoundException($"Transaction {id} not found.");
                if (t.IsLocked)
                {
                    if (skipLocked) { skipped.Add($"{t.Id}: exported"); continue; }
                    throw new TxnLockedException(id);
                }
                var before = t.Status;

                object? detail;
                try { detail = change(s, t); }
                catch (SkipException sk)
                {
                    if (sk.Reason is not null) skipped.Add($"{t.Id}: {sk.Reason}");
                    continue;
                }

                if (deriveStatus && Holds.IsOpen(t.Status))
                {
                    bool categorized = IsCategorized(s, t);
                    t.Status = t.Status switch
                    {
                        TxnStatus.Ready when categorized => TxnStatus.Ready,
                        _ when categorized => TxnStatus.Categorized,
                        _ => TxnStatus.New,
                    };
                    if (Holds.ShouldHold(t, invoiced)) t.Status = TxnStatus.HoldReceivePayment;
                }

                t.UpdatedUtc = Now;
                s.Txns.UpdateEditable(t);
                s.Audit.Write("Txn", t.Id, action, new { From = before.ToString(), To = t.Status.ToString(), Detail = detail }, Now);
                changed.Add(t);
            }
            return new BulkResult(changed, skipped);
        });
    }
}
