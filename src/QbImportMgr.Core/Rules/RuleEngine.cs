using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using QbImportMgr.Core.Domain;

namespace QbImportMgr.Core.Rules;

/// <summary>The fields a rule can look at. RawPayee is the bank's NAME; Payee is after alias/override.</summary>
public readonly record struct RuleInput(long BankAccountId, string RawPayee, string Payee, string Memo, long SignedAmountCents)
{
    public static RuleInput From(Txn t) =>
        new(t.BankAccountId, t.Name ?? "", t.EffectivePayee, t.EffectiveMemo, t.SignedAmountCents);
}

public static class TextMatcher
{
    private static readonly ConcurrentDictionary<string, Regex?> Cache = new();
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    public static bool IsMatch(MatchKind kind, string pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        switch (kind)
        {
            case MatchKind.Contains: return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            case MatchKind.StartsWith: return text.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
            case MatchKind.Regex:
                var rx = Cache.GetOrAdd(pattern, p => TryCreate(p, out var r) ? r : null);
                if (rx is null) return false;
                try { return rx.IsMatch(text); }
                catch (RegexMatchTimeoutException) { return false; }
            default: return false;
        }
    }

    /// <summary>Returns an error message for an unusable pattern, or null when it is valid.</summary>
    public static string? Validate(MatchKind kind, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "Pattern is empty.";
        if (kind != MatchKind.Regex) return null;
        try { _ = new Regex(pattern, RegexOptions.IgnoreCase, Timeout); return null; }
        catch (ArgumentException ex) { return ex.Message; }
    }

    private static bool TryCreate(string pattern, out Regex? rx)
    {
        try { rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout); return true; }
        catch (ArgumentException) { rx = null; return false; }
    }
}

public sealed record RuleOutcome(Rule Rule, TxnStatus NewStatus);

public sealed class RuleEngine
{
    private readonly IReadOnlyList<Rule> _rules;

    public RuleEngine(IEnumerable<Rule> rules) =>
        _rules = rules.Where(r => r.IsEnabled).OrderBy(r => r.Priority).ThenBy(r => r.Id).ToList();

    public static bool Matches(Rule r, in RuleInput t)
    {
        if (r.BankAccountId is { } acct && acct != t.BankAccountId) return false;
        if (r.Sign == SignFilter.In && t.SignedAmountCents <= 0) return false;
        if (r.Sign == SignFilter.Out && t.SignedAmountCents >= 0) return false;
        var abs = Math.Abs(t.SignedAmountCents);
        if (r.MinCents is { } min && abs < min) return false;
        if (r.MaxCents is { } max && abs > max) return false;

        return r.Field switch
        {
            RuleField.Payee => TextMatcher.IsMatch(r.MatchKind, r.Pattern, t.RawPayee) ||
                               TextMatcher.IsMatch(r.MatchKind, r.Pattern, t.Payee),
            RuleField.Memo => TextMatcher.IsMatch(r.MatchKind, r.Pattern, t.Memo),
            RuleField.Any => TextMatcher.IsMatch(r.MatchKind, r.Pattern, t.RawPayee) ||
                             TextMatcher.IsMatch(r.MatchKind, r.Pattern, t.Payee) ||
                             TextMatcher.IsMatch(r.MatchKind, r.Pattern, t.Memo),
            _ => false,
        };
    }

    /// <summary>First matching rule by priority (lower number first), then id.</summary>
    public Rule? FindMatch(in RuleInput input)
    {
        foreach (var r in _rules)
            if (Matches(r, input)) return r;
        return null;
    }

    /// <summary>
    /// Applies the first matching rule to a New transaction. Other statuses (Duplicate, Ready,
    /// Exported, a user's manual Excluded) are left alone. Caller persists the txn and the rule's hit count.
    /// </summary>
    public RuleOutcome? Apply(Txn txn)
    {
        if (txn.Status != TxnStatus.New) return null;
        var rule = FindMatch(RuleInput.From(txn));
        if (rule is null) return null;

        txn.RuleId = rule.Id;
        if (rule.AutoExclude)
        {
            txn.Status = TxnStatus.Excluded;
        }
        else
        {
            if (rule.CategoryId is not null) txn.CategoryId = rule.CategoryId;
            if (rule.QbNameId is not null) txn.QbNameId = rule.QbNameId;
            if (!string.IsNullOrWhiteSpace(rule.Class)) txn.Class = rule.Class;
            if (txn.CategoryId is not null) txn.Status = TxnStatus.Categorized;
        }
        return new RuleOutcome(rule, txn.Status);
    }
}
