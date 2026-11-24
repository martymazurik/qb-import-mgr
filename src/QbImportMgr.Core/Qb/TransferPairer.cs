namespace QbImportMgr.Core.Qb;

/// <summary>A row that can take part in a transfer. InQb: already in QuickBooks (matched to the register, or exported).</summary>
public sealed record PairCandidate(long TxnId, long BankAccountId, DateOnly Date, long SignedCents, bool InQb, string? Payee = null, string? Memo = null);

public sealed record TransferPair(long OutTxnId, long InTxnId);

/// <summary>
/// Pairs money out of one mapped account with money in to another: same absolute amount, dates
/// within ±5 days. Closest date wins; a tie between distinguishable rows is left unpaired (never
/// guessed). Pairs where both sides are already in QuickBooks are skipped, since there's nothing to do.
/// </summary>
public static class TransferPairer
{
    public const int DateWindowDays = 5;

    public static IReadOnlyList<TransferPair> Pair(IEnumerable<PairCandidate> rows, IReadOnlySet<(long, long)>? rejected = null)
    {
        rejected ??= new HashSet<(long, long)>();
        var list = rows.ToList();
        var ins = list.Where(r => r.SignedCents > 0).ToLookup(r => r.SignedCents);

        var edges = (
            from o in list.Where(r => r.SignedCents < 0)
            from i in ins[-o.SignedCents]
            where i.BankAccountId != o.BankAccountId
            where !(o.InQb && i.InQb)
            where !rejected.Contains((o.TxnId, i.TxnId)) && !rejected.Contains((i.TxnId, o.TxnId))
            let days = Math.Abs(o.Date.DayNumber - i.Date.DayNumber)
            where days <= DateWindowDays
            select (Out: o, In: i, Days: days)).ToList();

        var pairs = new List<TransferPair>();
        var used = new HashSet<long>();
        while (true)
        {
            var open = edges.Where(e => !used.Contains(e.Out.TxnId) && !used.Contains(e.In.TxnId)).ToList();
            if (open.Count == 0) break;
            int best = open.Min(e => e.Days);
            var top = open.Where(e => e.Days == best).OrderBy(e => e.Out.Date).ThenBy(e => e.Out.TxnId).ThenBy(e => e.In.TxnId).ToList();
            var pick = top[0];

            var rivals = top.Where(e =>
                (e.Out.TxnId == pick.Out.TxnId && e.In.TxnId != pick.In.TxnId && !Same(e.In, pick.In)) ||
                (e.In.TxnId == pick.In.TxnId && e.Out.TxnId != pick.Out.TxnId && !Same(e.Out, pick.Out))).ToList();

            if (rivals.Count == 0)
            {
                pairs.Add(new TransferPair(pick.Out.TxnId, pick.In.TxnId));
                used.Add(pick.Out.TxnId);
                used.Add(pick.In.TxnId);
            }
            else
            {
                // Ambiguous: leave every row in the tie unpaired.
                foreach (var e in rivals.Append(pick))
                {
                    used.Add(e.Out.TxnId);
                    used.Add(e.In.TxnId);
                }
            }
        }
        return pairs;
    }

    private static bool Same(PairCandidate a, PairCandidate b) =>
        a.BankAccountId == b.BankAccountId && a.Date == b.Date &&
        string.Equals(a.Payee ?? "", b.Payee ?? "", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Memo ?? "", b.Memo ?? "", StringComparison.OrdinalIgnoreCase);
}
