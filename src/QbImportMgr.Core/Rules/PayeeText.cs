using System.Text;
using System.Text.RegularExpressions;
using QbImportMgr.Core.Domain;

namespace QbImportMgr.Core.Rules;

public static partial class PayeeText
{
    /// <summary>Uppercase alphanumerics only; used for fuzzy duplicate comparison.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// Payees match when their normalized forms are equal, or one is a prefix of the other
    /// (OFX 1.x truncates NAME to 32 chars; some downloads move text between NAME and MEMO).
    /// </summary>
    public static bool SamePayee(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return na.Length == nb.Length;
        if (na == nb) return true;
        var (shorter, longer) = na.Length <= nb.Length ? (na, nb) : (nb, na);
        return shorter.Length >= 8 && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stable part of a bank payee for a suggested rule: "AMAZON MKTPL*1A2B3C SEATTLE" becomes
    /// "AMAZON MKTPL", "SHELL OIL 57444 AUSTIN TX" becomes "SHELL OIL".
    /// </summary>
    public static string SuggestPattern(string? rawPayee)
    {
        if (string.IsNullOrWhiteSpace(rawPayee)) return "";
        var s = rawPayee.Trim();
        int cut = s.IndexOfAny(['*', '#']);
        if (cut > 0) s = s[..cut];
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        foreach (var w in words)
        {
            if (kept.Count > 0 && DigitHeavy().IsMatch(w)) break;
            kept.Add(w);
            if (kept.Count == 4) break;
        }
        return string.Join(' ', kept).Trim();
    }

    [GeneratedRegex(@"\d{3,}|^\d+$")]
    private static partial Regex DigitHeavy();
}

public static class PayeeAliasResolver
{
    /// <summary>First alias (by id) whose pattern matches the raw payee.</summary>
    public static PayeeAlias? Resolve(IEnumerable<PayeeAlias> aliases, string? rawPayee)
    {
        if (string.IsNullOrEmpty(rawPayee)) return null;
        return aliases.OrderBy(a => a.Id).FirstOrDefault(a => TextMatcher.IsMatch(a.MatchKind, a.RawPattern, rawPayee));
    }
}

public static class RuleSuggester
{
    /// <summary>Builds a "Remember as rule" proposal from a categorized transaction. The user edits it before saving.</summary>
    public static Rule Suggest(Txn txn) => new()
    {
        Priority = 100,
        Field = RuleField.Payee,
        MatchKind = MatchKind.Contains,
        Pattern = PayeeText.SuggestPattern(txn.Name ?? txn.EffectivePayee),
        Sign = txn.SignedAmountCents < 0 ? SignFilter.Out : txn.SignedAmountCents > 0 ? SignFilter.In : SignFilter.Any,
        BankAccountId = txn.BankAccountId,
        CategoryId = txn.CategoryId,
        QbNameId = txn.QbNameId,
        Class = txn.Class,
        AutoExclude = txn.Status == TxnStatus.Excluded,
        IsEnabled = true,
    };
}
