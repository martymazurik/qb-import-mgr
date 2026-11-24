using System.Text.RegularExpressions;
using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Rules;

namespace QbImportMgr.Core.Qb;

/// <summary>A QB register row as history: Name is the payee, Split the account it was assigned to.</summary>
public sealed record LearnRow(string? Name, string? Split, DateOnly Date);

public sealed record PayeeProfile(string Name, string TopAccount, int TopCount, int SingleAccountRows, int SplitRows, DateOnly LastUsed)
{
    /// <summary>Share of the single-account rows that went to TopAccount.</summary>
    public double Agreement => SingleAccountRows == 0 ? 0 : (double)TopCount / SingleAccountRows;
}

/// <summary>A bank row linked to the QB row that recorded it.</summary>
public sealed record MatchedPair(string BankText, string? QbName, string? Split);

public enum ProposalBasis { MatchedBankText, QbName }

public sealed record LearnProposal(
    string Pattern,
    string QbName,
    string Account,
    int Count,
    double Agreement,
    IReadOnlyList<string> Samples,
    ProposalBasis Basis)
{
    public bool MeetsThreshold(int minCount = PayeeLearner.DefaultMinCount, double minAgreement = PayeeLearner.DefaultMinAgreement) =>
        Count >= minCount && Agreement >= minAgreement - 1e-9;
}

public static partial class PayeeLearner
{
    public const string SplitMarker = "-SPLIT-";
    public const int DefaultMinCount = 3;
    public const double DefaultMinAgreement = 0.9;

    /// <summary>
    /// One profile per QB Name: most-used Split account, its count, agreement, last use.
    /// "-SPLIT-" rows are counted separately and never decide the account.
    /// </summary>
    public static IReadOnlyList<PayeeProfile> BuildProfiles(IEnumerable<LearnRow> rows) =>
        rows.Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Split))
            .GroupBy(r => r.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var single = g.Where(r => !IsSplit(r.Split)).ToList();
                var top = single.GroupBy(r => r.Split!.Trim(), StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Count()).ThenByDescending(x => x.Max(r => r.Date)).ThenBy(x => x.Key)
                    .FirstOrDefault();
                return new PayeeProfile(g.Key, top?.Key ?? "", top?.Count() ?? 0, single.Count, g.Count() - single.Count, g.Max(r => r.Date));
            })
            .Where(p => p.SingleAccountRows > 0)
            .OrderByDescending(p => p.SingleAccountRows)
            .ToList();

    public static bool IsSplit(string? split) => string.Equals(split?.Trim(), SplitMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Proposals from matched bank↔QB pairs (cleaned bank text → QB name + account), plus QB-name
    /// proposals for well-used names that no matched pair covers (pattern = the name itself).
    /// </summary>
    public static IReadOnlyList<LearnProposal> Propose(IEnumerable<MatchedPair> pairs, IEnumerable<PayeeProfile> profiles)
    {
        var proposals = new List<LearnProposal>();

        foreach (var g in pairs
                     .Where(p => !string.IsNullOrWhiteSpace(p.QbName) && !string.IsNullOrWhiteSpace(p.Split) && !IsSplit(p.Split))
                     .GroupBy(p => CleanBankText(p.BankText))
                     .Where(g => g.Key.Length >= 3))
        {
            var top = g.GroupBy(p => (Name: p.QbName!.Trim(), Split: p.Split!.Trim()))
                .OrderByDescending(x => x.Count()).ThenBy(x => x.Key.Name).First();
            var samples = g.Select(p => p.BankText.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
            var pattern = g.Key;
            if (!samples.All(sm => TextMatcher.IsMatch(MatchKind.Contains, pattern, sm)))
                pattern = PayeeText.SuggestPattern(samples[0]);
            proposals.Add(new LearnProposal(pattern, top.Key.Name, top.Key.Split, g.Count(), (double)top.Count() / g.Count(), samples, ProposalBasis.MatchedBankText));
        }

        var coveredNames = proposals.Select(p => p.QbName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coveredPatterns = proposals.Select(p => p.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var prof in profiles.Where(p => !coveredNames.Contains(p.Name)))
        {
            var pattern = NameToPattern(prof.Name);
            if (pattern.Length < 4 || coveredPatterns.Contains(pattern)) continue;
            proposals.Add(new LearnProposal(pattern, prof.Name, prof.TopAccount, prof.SingleAccountRows, prof.Agreement, [], ProposalBasis.QbName));
        }

        return proposals.OrderByDescending(p => p.MeetsThreshold()).ThenByDescending(p => p.Count).ThenBy(p => p.Pattern).ToList();
    }

    private static readonly string[] Prefixes =
    [
        "POS PURCHASE", "POS DEBIT", "POS", "DEBIT CARD PURCHASE", "DEBIT CARD", "DEBIT PURCHASE", "DBT CRD", "CHECKCARD", "CHECK CARD",
        "VISA PURCHASE", "PURCHASE AUTHORIZED ON", "PURCHASE", "RECURRING PAYMENT", "RECURRING", "ACH DEBIT", "ACH CREDIT", "ACH WEB", "ACH",
        "ONLINE PAYMENT", "ONLINE", "PREAUTHORIZED DEBIT", "PREAUTHORIZED", "PRE-AUTH", "ELECTRONIC PAYMENT", "BILL PAY", "WEB PMT",
        "SQ *", "SQ*", "TST*", "TST *", "PAYPAL *", "PP*", "IN *",
    ];

    private static readonly HashSet<string> States =
    [
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY","LA","ME","MD","MA","MI","MN","MS","MO","MT",
        "NE","NV","NH","NJ","NM","NY","NC","ND","OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VT","VA","WA","WV","WI","WY","DC",
    ];

    /// <summary>
    /// The stable part of bank payee text: uppercase, transaction-type prefixes removed (POS
    /// PURCHASE, DEBIT CARD, ACH...), cut at a "*" or "#" reference, dates and store numbers
    /// dropped, trailing city/state dropped, at most 3 words.
    /// </summary>
    public static string CleanBankText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = Spaces().Replace(text.ToUpperInvariant().Trim(), " ");

        for (bool again = true; again;)
        {
            again = false;
            foreach (var p in Prefixes)
            {
                if (s.StartsWith(p, StringComparison.Ordinal) && s.Length > p.Length)
                {
                    s = s[p.Length..].TrimStart(' ', '-', ':', '*');
                    again = true;
                    break;
                }
            }
        }

        int cut = s.IndexOfAny(['*', '#']);
        if (cut > 0) s = s[..cut];

        s = Dates().Replace(s, " ");
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('-', ',', '.', ':'))
            .Where(w => w.Length > 0 && !HasManyDigits().IsMatch(w))
            .ToList();
        while (words.Count > 0 && words[^1].All(char.IsDigit)) words.RemoveAt(words.Count - 1);

        // "... AUSTIN TX": drop state and the city word before it.
        if (words.Count >= 3 && States.Contains(words[^1]))
            words.RemoveRange(words.Count - 2, 2);
        else if (words.Count >= 2 && States.Contains(words[^1]))
            words.RemoveAt(words.Count - 1);

        return string.Join(' ', words.Take(3));
    }

    /// <summary>A QB name as a payee pattern: uppercase, punctuation and company suffixes removed.</summary>
    public static string NameToPattern(string name)
    {
        var s = name.Split(':')[^1].ToUpperInvariant();
        s = NonWord().Replace(s, " ");
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("INC" or "LLC" or "LTD" or "CO" or "CORP" or "CORPORATION" or "COMPANY" or "THE"))
            .ToList();
        return string.Join(' ', words.Take(3));
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\b\d{1,2}[/-]\d{1,2}([/-]\d{2,4})?\b")] private static partial Regex Dates();
    [GeneratedRegex(@"\d{3,}|^\d+$")] private static partial Regex HasManyDigits();
    [GeneratedRegex(@"[^A-Z0-9&' ]")] private static partial Regex NonWord();
}
