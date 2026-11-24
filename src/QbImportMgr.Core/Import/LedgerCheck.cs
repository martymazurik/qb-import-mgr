using QbImportMgr.Core.Domain;

namespace QbImportMgr.Core.Import;

public sealed record LedgerCheckResult(
    DateOnly FromDate,
    DateOnly ToDate,
    long ExpectedChangeCents,
    long TxnSumCents,
    int TxnCount)
{
    public long DifferenceCents => ExpectedChangeCents - TxnSumCents;
    public bool Ties => DifferenceCents == 0;
}

public static class LedgerCheck
{
    /// <summary>
    /// Downloads overlap and aren't statement-aligned, so the check runs between two ledger
    /// snapshots A (earlier) and B: LedgerBal(B) - LedgerBal(A) must equal the sum of the
    /// account's non-Excluded, non-Duplicate transactions with DtPosted in (date A, date B].
    /// Amounts use SignedAmountCents; ledger balances get the same InvertSign normalization.
    /// Returns null if either snapshot lacks a balance or date, or A isn't before B.
    /// </summary>
    public static LedgerCheckResult? Compute(QboStatement a, QboStatement b, bool invertSign, IEnumerable<Txn> accountTxns)
    {
        if (a.LedgerBalCents is not { } balA || a.LedgerBalDate is not { } dateA) return null;
        if (b.LedgerBalCents is not { } balB || b.LedgerBalDate is not { } dateB) return null;
        if (dateA >= dateB) return null;

        long sign = invertSign ? -1 : 1;
        var included = accountTxns
            .Where(t => t.BankAccountId == b.BankAccountId)
            .Where(t => t.Status is not (TxnStatus.Excluded or TxnStatus.Duplicate))
            .Where(t => t.DtPosted > dateA && t.DtPosted <= dateB)
            .ToList();

        return new LedgerCheckResult(dateA, dateB, sign * (balB - balA), included.Sum(t => t.SignedAmountCents), included.Count);
    }

    /// <summary>The nearest earlier snapshot for the same account to compare B against.</summary>
    public static QboStatement? PreviousSnapshot(QboStatement b, IEnumerable<QboStatement> accountStatements) =>
        b.LedgerBalDate is not { } dateB
            ? null
            : accountStatements
                .Where(s => s.BankAccountId == b.BankAccountId && s.Id != b.Id)
                .Where(s => s.LedgerBalCents is not null && s.LedgerBalDate is { } d && d < dateB)
                .OrderByDescending(s => s.LedgerBalDate)
                .ThenByDescending(s => s.Id)
                .FirstOrDefault();
}
