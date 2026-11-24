using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Import;

namespace QbImportMgr.Tests;

public class LedgerCheckTests
{
    private static QboStatement S(long id, long? bal, DateOnly? date) =>
        new() { Id = id, BankAccountId = 1, LedgerBalCents = bal, LedgerBalDate = date };

    private static Txn T(int day, long cents, TxnStatus status = TxnStatus.New, long acct = 1) =>
        new() { BankAccountId = acct, DtPosted = new DateOnly(2026, 10, day), SignedAmountCents = cents, AmountCents = cents, Status = status };

    private static readonly QboStatement A = S(1, 100000, new DateOnly(2026, 10, 10));
    private static readonly QboStatement B = S(2, 75000, new DateOnly(2026, 10, 20));

    [Fact]
    public void Ties_when_transactions_after_a_through_b_sum_to_the_balance_change()
    {
        var txns = new[]
        {
            T(10, -99999),                  // on A's date: already in A's balance, not counted
            T(11, -30000),
            T(15, 10000),
            T(20, -5000),                   // on B's date: counted
            T(21, -77777),                  // after B
            T(12, -1234, TxnStatus.Excluded),
            T(12, -30000, TxnStatus.Duplicate),
            T(12, -1, acct: 2),
        };

        var r = LedgerCheck.Compute(A, B, invertSign: false, txns)!;
        Assert.Equal(-25000, r.ExpectedChangeCents);
        Assert.Equal(-25000, r.TxnSumCents);
        Assert.Equal(3, r.TxnCount);
        Assert.True(r.Ties);
    }

    [Fact]
    public void Reports_difference_when_it_does_not_tie()
    {
        var r = LedgerCheck.Compute(A, B, false, [T(11, -20000)])!;
        Assert.False(r.Ties);
        Assert.Equal(-5000, r.DifferenceCents);
    }

    [Fact]
    public void Invert_sign_applies_to_ledger_balances_too()
    {
        // Bank sends charges positive and the balance owed positive; normalized, both flip.
        var a = S(1, 10000, new DateOnly(2026, 10, 10));
        var b = S(2, 15000, new DateOnly(2026, 10, 20));
        var r = LedgerCheck.Compute(a, b, invertSign: true, [T(12, -5000)])!;
        Assert.True(r.Ties);
        Assert.Equal(-5000, r.ExpectedChangeCents);
    }

    [Fact]
    public void Returns_null_without_usable_snapshots()
    {
        Assert.Null(LedgerCheck.Compute(S(1, null, new DateOnly(2026, 10, 1)), B, false, []));
        Assert.Null(LedgerCheck.Compute(S(1, 5, null), B, false, []));
        Assert.Null(LedgerCheck.Compute(B, A, false, []));
    }

    [Fact]
    public void Previous_snapshot_is_the_nearest_earlier_one()
    {
        var all = new[]
        {
            S(1, 1, new DateOnly(2026, 9, 30)),
            S(3, 3, new DateOnly(2026, 10, 15)),
            S(4, null, new DateOnly(2026, 10, 18)),
            S(5, 5, new DateOnly(2026, 10, 20)),
            B,
        };
        Assert.Equal(3, LedgerCheck.PreviousSnapshot(B, all)!.Id);
        Assert.Null(LedgerCheck.PreviousSnapshot(S(9, 1, new DateOnly(2026, 1, 1)), all));
    }
}
