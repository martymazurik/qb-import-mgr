using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Rules;

namespace QbImportMgr.Core.Import;

/// <summary>A transaction as seen by duplicate detection. QboFileId identifies the download it came from.</summary>
public sealed record DupCandidate(
    long TxnId,
    long BankAccountId,
    long QboFileId,
    string? FitId,
    DateOnly DtPosted,
    long SignedAmountCents,
    string? Payee,
    TxnStatus Status);

public enum DupKind { None, ExactFitId, Possible }

public sealed record DupResult(DupKind Kind, long? MatchTxnId)
{
    public static readonly DupResult None = new(DupKind.None, null);
}

public static class DuplicateDetector
{
    public const int DateWindowDays = 3;

    /// <summary>
    /// 1. Same account + same non-empty FITID: an exact duplicate, skipped on import.
    /// 2. Otherwise a possible duplicate (inserted with Status=Duplicate, never dropped) when an
    ///    existing row from a different download has the same account and amount, a date within
    ///    ±3 days, and the same payee. Rows from the same download are never compared, because two
    ///    identical charges in one download are both real. Rows already flagged Duplicate aren't
    ///    used as originals.
    /// </summary>
    public static DupResult Check(DupCandidate incoming, IEnumerable<DupCandidate> existing)
    {
        var sameAccount = existing.Where(e => e.BankAccountId == incoming.BankAccountId).ToList();

        if (!string.IsNullOrEmpty(incoming.FitId))
        {
            var exact = sameAccount.FirstOrDefault(e => e.FitId == incoming.FitId);
            if (exact is not null) return new DupResult(DupKind.ExactFitId, exact.TxnId);
        }

        var best = sameAccount
            .Where(e => e.QboFileId != incoming.QboFileId)
            .Where(e => e.Status != TxnStatus.Duplicate)
            .Where(e => e.SignedAmountCents == incoming.SignedAmountCents)
            .Where(e => Math.Abs(e.DtPosted.DayNumber - incoming.DtPosted.DayNumber) <= DateWindowDays)
            .Where(e => PayeeText.SamePayee(e.Payee, incoming.Payee))
            .OrderBy(e => Math.Abs(e.DtPosted.DayNumber - incoming.DtPosted.DayNumber))
            .ThenBy(e => e.TxnId)
            .FirstOrDefault();

        return best is null ? DupResult.None : new DupResult(DupKind.Possible, best.TxnId);
    }
}
