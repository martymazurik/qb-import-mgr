namespace QbImportMgr.Core.Domain;

/// <summary>
/// The split editor shows amounts "in the direction of the transaction": for a -1,250.00 check the
/// user types 1,000.00 and 250.00. A negative entry runs against the transaction (e.g. a refund line).
/// </summary>
public static class SplitMath
{
    private static long Direction(long txnSignedCents) => txnSignedCents < 0 ? -1 : 1;

    public static long ToStoredCents(long enteredCents, long txnSignedCents) => enteredCents * Direction(txnSignedCents);

    public static long ToEnteredCents(long storedCents, long txnSignedCents) => storedCents * Direction(txnSignedCents);

    /// <summary>What is left to allocate, in entry terms (0 = balanced).</summary>
    public static long Remainder(long txnSignedCents, IEnumerable<long> enteredCents) =>
        Math.Abs(txnSignedCents) - enteredCents.Sum();

    public static bool Balances(long txnSignedCents, IEnumerable<long> storedCents) => storedCents.Sum() == txnSignedCents;
}
