using QbImportMgr.Core.Rules;

namespace QbImportMgr.Core.Qb;

/// <summary>
/// A bank transaction offered to the matcher. LinkOnly rows (already exported, or excluded) take
/// part so their QB rows are consumed, but keep their status.
/// </summary>
public sealed record MatchBank(long TxnId, long BankAccountId, DateOnly Date, long SignedCents, string? CheckNum, string? Payee, string? Memo, bool LinkOnly = false);

public sealed record MatchQb(long Id, long BankAccountId, DateOnly Date, long SignedCents, string? Num, string? Name, string? Memo, string? Type);

public sealed record MatchCandidate(MatchQb Qb, bool CheckNumberMatch, int DayDiff, double NameSimilarity);

public sealed record MatchResult(IReadOnlyDictionary<long, long> Matches, IReadOnlyDictionary<long, IReadOnlyList<long>> Ambiguous);

/// <summary>
/// Matches bank rows to QB register rows, one-to-one.
/// A candidate needs the exact signed amount on the same account and either the same check number
/// (within ±60 days) or a date within ±5 days. Two different check numbers never match.
/// Candidates rank by check-number match, then closest date, then payee/name similarity. The best
/// candidate is taken only when nothing else ties it. A tie with a different QB row (or a different
/// bank row competing for the same QB row) makes the bank row Ambiguous, and those QB rows are held
/// back from everyone else in this run. Identical rows (same date, num, name, memo) are
/// interchangeable, so a tie between them is not ambiguity.
/// </summary>
public static class QbMatcher
{
    public const int DateWindowDays = 5;
    public const int CheckWindowDays = 60;

    public static MatchResult Match(IEnumerable<MatchBank> bankRows, IEnumerable<MatchQb> qbRows, IReadOnlySet<(long TxnId, long QbId)>? rejected = null)
    {
        rejected ??= new HashSet<(long, long)>();
        var bank = bankRows.ToList();
        var qbByKey = qbRows.ToLookup(q => (q.BankAccountId, q.SignedCents));

        var edges = new List<Edge>();
        foreach (var b in bank)
            foreach (var q in qbByKey[(b.BankAccountId, b.SignedCents)])
                if (!rejected.Contains((b.TxnId, q.Id)) && Candidate(b, q) is { } c)
                    edges.Add(new Edge(b, c));

        var matches = new Dictionary<long, long>();
        var ambiguous = new Dictionary<long, IReadOnlyList<long>>();
        var doneBank = new HashSet<long>();
        var usedQb = new HashSet<long>();

        while (true)
        {
            var open = edges.Where(e => !doneBank.Contains(e.Bank.TxnId) && !usedQb.Contains(e.Cand.Qb.Id)).ToList();
            if (open.Count == 0) break;

            var bestKey = open.Min(e => e.Key);
            var top = open.Where(e => e.Key.Equals(bestKey))
                .OrderBy(e => e.Bank.Date).ThenBy(e => e.Bank.TxnId).ThenBy(e => e.Cand.Qb.Id).ToList();
            var pick = top[0];

            var rivalQb = top.Where(e => e.Bank.TxnId == pick.Bank.TxnId && e.Cand.Qb.Id != pick.Cand.Qb.Id && !SameQb(e.Cand.Qb, pick.Cand.Qb)).ToList();
            var rivalBank = top.Where(e => e.Cand.Qb.Id == pick.Cand.Qb.Id && e.Bank.TxnId != pick.Bank.TxnId && !SameBank(e.Bank, pick.Bank)).ToList();

            if (rivalQb.Count == 0 && rivalBank.Count == 0)
            {
                matches[pick.Bank.TxnId] = pick.Cand.Qb.Id;
                doneBank.Add(pick.Bank.TxnId);
                usedQb.Add(pick.Cand.Qb.Id);
                continue;
            }

            // Ambiguous: every bank row involved in the tie, holding back every QB row they tie on.
            var involvedBank = new[] { pick.Bank }.Concat(rivalBank.Select(e => e.Bank)).DistinctBy(b => b.TxnId).ToList();
            foreach (var b in involvedBank)
            {
                var cands = open.Where(e => e.Bank.TxnId == b.TxnId).OrderBy(e => e.Key).Select(e => e.Cand.Qb.Id).ToList();
                ambiguous[b.TxnId] = cands;
                doneBank.Add(b.TxnId);
            }
            foreach (var e in top.Where(e => involvedBank.Any(b => b.TxnId == e.Bank.TxnId)))
                usedQb.Add(e.Cand.Qb.Id);
        }

        return new MatchResult(matches, ambiguous);
    }

    /// <summary>Ranked candidates for one bank row, for the "Pick match" list.</summary>
    public static IReadOnlyList<MatchCandidate> Candidates(MatchBank b, IEnumerable<MatchQb> available) =>
        available.Where(q => q.BankAccountId == b.BankAccountId && q.SignedCents == b.SignedCents)
            .Select(q => Candidate(b, q))
            .OfType<MatchCandidate>()
            .OrderBy(c => new Key(c))
            .ThenBy(c => c.Qb.Date)
            .ToList();

    private static MatchCandidate? Candidate(MatchBank b, MatchQb q)
    {
        var bn = NormalizeCheck(b.CheckNum);
        var qn = NormalizeCheck(q.Num);
        int days = Math.Abs(b.Date.DayNumber - q.Date.DayNumber);
        bool checkMatch = bn.Length > 0 && bn == qn;
        if (bn.Length > 0 && qn.Length > 0 && !checkMatch) return null; // two different check numbers
        if (checkMatch ? days > CheckWindowDays : days > DateWindowDays) return null;
        return new MatchCandidate(q, checkMatch, days, NameSimilarity(b.Payee, q.Name));
    }

    /// <summary>Digits only, leading zeros dropped; "" if none. Only numeric check/doc numbers count.</summary>
    public static string NormalizeCheck(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var digits = new string(s.Where(char.IsDigit).ToArray()).TrimStart('0');
        return digits.Length > 0 && digits.Length == s.Trim().TrimStart('0').Length ? digits : "";
    }

    /// <summary>Word overlap (Jaccard on words of 3+ letters/digits), 1.0 also when one normalized name starts the other.</summary>
    public static double NameSimilarity(string? bankPayee, string? qbName)
    {
        var a = PayeeText.Normalize(bankPayee);
        var b = PayeeText.Normalize(qbName);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)) return 1;
        var wa = Words(bankPayee);
        var wb = Words(qbName);
        if (wa.Count == 0 || wb.Count == 0) return 0;
        return (double)wa.Intersect(wb).Count() / wa.Union(wb).Count();
    }

    private static HashSet<string> Words(string? s) =>
        (s ?? "").ToUpperInvariant()
            .SplitWhere(c => !char.IsLetterOrDigit(c))
            .Where(w => w.Length >= 3)
            .ToHashSet();

    private static bool SameQb(MatchQb x, MatchQb y) =>
        x.Date == y.Date && Eq(x.Num, y.Num) && Eq(x.Name, y.Name) && Eq(x.Memo, y.Memo) && Eq(x.Type, y.Type);

    private static bool SameBank(MatchBank x, MatchBank y) =>
        x.Date == y.Date && Eq(x.CheckNum, y.CheckNum) && Eq(x.Payee, y.Payee) && Eq(x.Memo, y.Memo);

    private static bool Eq(string? a, string? b) => string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    private static string[] SplitWhere(this string s, Func<char, bool> sep)
    {
        var parts = new List<string>();
        var cur = new System.Text.StringBuilder();
        foreach (var ch in s)
        {
            if (sep(ch)) { if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); } }
            else cur.Append(ch);
        }
        if (cur.Length > 0) parts.Add(cur.ToString());
        return [.. parts];
    }

    /// <summary>Rank: check-number match first, then closest date, then name similarity in quarter steps (lower is better).</summary>
    private readonly record struct Key(int Check, int Days, int Sim) : IComparable<Key>
    {
        public Key(MatchCandidate c) : this(c.CheckNumberMatch ? 0 : 1, c.CheckNumberMatch ? 0 : c.DayDiff, -(int)Math.Floor(c.NameSimilarity * 4)) { }

        public int CompareTo(Key o) =>
            Check != o.Check ? Check.CompareTo(o.Check) : Days != o.Days ? Days.CompareTo(o.Days) : Sim.CompareTo(o.Sim);
    }

    private sealed record Edge(MatchBank Bank, MatchCandidate Cand)
    {
        public Key Key { get; } = new(Cand);
    }
}
