using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Rules;

namespace QbImportMgr.Tests;

public class RuleEngineTests
{
    private static Txn T(string name, long cents, string? memo = null, long acct = 1, string? ovrPayee = null) => new()
    {
        BankAccountId = acct, Name = name, Memo = memo, AmountCents = cents, SignedAmountCents = cents,
        OvrPayee = ovrPayee, DtPosted = new DateOnly(2026, 10, 1), Status = TxnStatus.New,
    };

    private static Rule R(long id, string pattern, int priority = 100, MatchKind kind = MatchKind.Contains,
        RuleField field = RuleField.Payee, long? category = 50) =>
        new() { Id = id, Pattern = pattern, Priority = priority, MatchKind = kind, Field = field, CategoryId = category };

    [Fact]
    public void Lower_priority_number_wins_then_lower_id()
    {
        var engine = new RuleEngine([R(1, "AMAZON", 200, category: 1), R(2, "AMAZON", 10, category: 2), R(3, "AMAZON", 10, category: 3)]);
        var t = T("AMAZON MKTPL*AB12", -100);

        var outcome = engine.Apply(t);
        Assert.NotNull(outcome);
        Assert.Equal(2, t.RuleId);
        Assert.Equal(2, t.CategoryId);
        Assert.Equal(TxnStatus.Categorized, t.Status);
    }

    [Fact]
    public void Disabled_rules_are_ignored()
    {
        var r = R(1, "AMAZON");
        r.IsEnabled = false;
        Assert.Null(new RuleEngine([r]).Apply(T("AMAZON", -1)));
    }

    [Theory]
    [InlineData(MatchKind.Contains, "mktpl", true)]
    [InlineData(MatchKind.StartsWith, "amazon", true)]
    [InlineData(MatchKind.StartsWith, "mktpl", false)]
    [InlineData(MatchKind.Regex, @"^AMAZON\s+MKT", true)]
    [InlineData(MatchKind.Regex, @"\d{5}", false)]
    [InlineData(MatchKind.Regex, @"([unclosed", false)]
    public void Match_kinds_are_case_insensitive(MatchKind kind, string pattern, bool expected)
    {
        var input = RuleInput.From(T("AMAZON MKTPL*AB12", -100));
        Assert.Equal(expected, RuleEngine.Matches(R(1, pattern, kind: kind), input));
    }

    [Fact]
    public void Sign_filter()
    {
        var outRule = R(1, "X"); outRule.Sign = SignFilter.Out;
        var inRule = R(2, "X"); inRule.Sign = SignFilter.In;

        Assert.True(RuleEngine.Matches(outRule, RuleInput.From(T("X", -1))));
        Assert.False(RuleEngine.Matches(outRule, RuleInput.From(T("X", 1))));
        Assert.True(RuleEngine.Matches(inRule, RuleInput.From(T("X", 1))));
        Assert.False(RuleEngine.Matches(inRule, RuleInput.From(T("X", 0))));
    }

    [Fact]
    public void Amount_range_uses_absolute_value()
    {
        var r = R(1, "SHELL"); r.MinCents = 2000; r.MaxCents = 10000;

        Assert.True(RuleEngine.Matches(r, RuleInput.From(T("SHELL OIL", -4500))));
        Assert.True(RuleEngine.Matches(r, RuleInput.From(T("SHELL OIL", -2000))));
        Assert.False(RuleEngine.Matches(r, RuleInput.From(T("SHELL OIL", -1999))));
        Assert.False(RuleEngine.Matches(r, RuleInput.From(T("SHELL OIL", -10001))));
    }

    [Fact]
    public void Account_filter()
    {
        var r = R(1, "FEE"); r.BankAccountId = 7;
        Assert.True(RuleEngine.Matches(r, RuleInput.From(T("FEE", -1, acct: 7))));
        Assert.False(RuleEngine.Matches(r, RuleInput.From(T("FEE", -1, acct: 8))));
    }

    [Fact]
    public void Payee_field_checks_raw_and_effective_payee_memo_field_checks_memo()
    {
        var t = T("SQ *JOES", -100, memo: "INVOICE 77", ovrPayee: "Joe's Coffee");
        var input = RuleInput.From(t);

        Assert.True(RuleEngine.Matches(R(1, "SQ *JOES"), input));
        Assert.True(RuleEngine.Matches(R(1, "Joe's"), input));
        Assert.False(RuleEngine.Matches(R(1, "INVOICE"), input));
        Assert.True(RuleEngine.Matches(R(1, "INVOICE", field: RuleField.Memo), input));
        Assert.True(RuleEngine.Matches(R(1, "INVOICE", field: RuleField.Any), input));
    }

    [Fact]
    public void Auto_exclude_sets_excluded_and_leaves_category_alone()
    {
        var r = R(1, "TRANSFER"); r.AutoExclude = true;
        var t = T("ONLINE TRANSFER", -500);

        new RuleEngine([r]).Apply(t);
        Assert.Equal(TxnStatus.Excluded, t.Status);
        Assert.Null(t.CategoryId);
        Assert.Equal(1, t.RuleId);
    }

    [Fact]
    public void Rule_with_only_name_and_class_keeps_status_new()
    {
        var r = R(1, "ACME", category: null); r.QbNameId = 9; r.Class = "Field";
        var t = T("ACME", 100);

        new RuleEngine([r]).Apply(t);
        Assert.Equal(TxnStatus.New, t.Status);
        Assert.Equal(9, t.QbNameId);
        Assert.Equal("Field", t.Class);
    }

    [Theory]
    [InlineData(TxnStatus.Duplicate)]
    [InlineData(TxnStatus.Ready)]
    [InlineData(TxnStatus.Exported)]
    [InlineData(TxnStatus.Excluded)]
    public void Only_new_transactions_are_touched(TxnStatus status)
    {
        var t = T("AMAZON", -1);
        t.Status = status;
        Assert.Null(new RuleEngine([R(1, "AMAZON")]).Apply(t));
        Assert.Equal(status, t.Status);
        Assert.Null(t.CategoryId);
    }

    [Fact]
    public void Validate_reports_bad_patterns()
    {
        Assert.Null(TextMatcher.Validate(MatchKind.Regex, @"^A\d+"));
        Assert.NotNull(TextMatcher.Validate(MatchKind.Regex, "(oops"));
        Assert.NotNull(TextMatcher.Validate(MatchKind.Contains, " "));
        Assert.Null(TextMatcher.Validate(MatchKind.Contains, "AMAZON"));
    }

    [Theory]
    [InlineData("AMAZON MKTPL*AB12C3", "AMAZON MKTPL")]
    [InlineData("SHELL OIL 57444 AUSTIN TX", "SHELL OIL")]
    [InlineData("SQ #12 JOES", "SQ")]
    [InlineData("PAYROLL ACME CORP", "PAYROLL ACME CORP")]
    [InlineData("7-ELEVEN 33012", "7-ELEVEN")]
    [InlineData("THE HOME DEPOT STORE NUMBER", "THE HOME DEPOT STORE")]
    public void Suggested_pattern_drops_store_numbers_and_reference_suffixes(string raw, string expected) =>
        Assert.Equal(expected, PayeeText.SuggestPattern(raw));

    [Fact]
    public void Suggester_copies_categorization_and_sign()
    {
        var t = T("AMAZON MKTPL*AB12C3", -8417, acct: 3);
        t.CategoryId = 44; t.QbNameId = 5; t.Class = "Admin";

        var r = RuleSuggester.Suggest(t);
        Assert.Equal("AMAZON MKTPL", r.Pattern);
        Assert.Equal(SignFilter.Out, r.Sign);
        Assert.Equal(3, r.BankAccountId);
        Assert.Equal(44, r.CategoryId);
        Assert.Equal(5, r.QbNameId);
        Assert.Equal("Admin", r.Class);
        Assert.False(r.AutoExclude);
    }

    [Fact]
    public void Payee_alias_resolves_first_by_id()
    {
        var aliases = new[]
        {
            new PayeeAlias { Id = 2, RawPattern = "AMAZON", CleanPayee = "Amazon (any)" },
            new PayeeAlias { Id = 1, RawPattern = "AMAZON MKTPL", MatchKind = MatchKind.StartsWith, CleanPayee = "Amazon" },
        };
        Assert.Equal("Amazon", PayeeAliasResolver.Resolve(aliases, "AMAZON MKTPL*X")?.CleanPayee);
        Assert.Null(PayeeAliasResolver.Resolve(aliases, "WALMART"));
    }
}
