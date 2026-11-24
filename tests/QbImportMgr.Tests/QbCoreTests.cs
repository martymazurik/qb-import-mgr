using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Qb;

namespace QbImportMgr.Tests;

public class QbRegisterCsvReaderTests
{
    [Fact]
    public void Debit_credit_layout_skips_title_group_subtotal_and_total_rows()
    {
        var f = QbRegisterCsvReader.Read(TestData.Sample("qb_register_debit_credit.csv"));

        Assert.True(f.HasAccountColumn);
        Assert.True(f.UsesDebitCredit);
        Assert.Empty(f.Warnings);
        Assert.Equal(4, f.Rows.Count);

        var check = f.Rows[0];
        Assert.Equal(("Check", new DateOnly(2026, 10, 5), "1042", "Landlord LLC", "October rent", "Checking", "X", "Rent", -125000L),
            (check.Type, check.Date, check.Num, check.Name, check.Memo, check.Account, check.Clr, check.Split, check.AmountCents));
        Assert.Equal(250000, f.Rows[1].AmountCents);
        Assert.Equal("Fee, monthly", f.Rows[2].Memo);           // quoted comma
        Assert.Equal("Checking", f.Rows[2].Account);             // "1000 · " prefix removed
        Assert.Equal("-SPLIT-", f.Rows[2].Split);
        Assert.Equal(-500, f.Rows[2].AmountCents);
        Assert.Equal(-5423, f.Rows[3].AmountCents);
    }

    [Fact]
    public void Amount_layout_without_account_column()
    {
        var f = QbRegisterCsvReader.Read(TestData.Sample("qb_register_amount.csv"));

        Assert.False(f.HasAccountColumn);
        Assert.False(f.UsesDebitCredit);
        Assert.All(f.Rows, r => Assert.Null(r.Account));
        Assert.Equal([-8417L, -500L, -123456L, 1999L, -21240L], f.Rows.Select(r => r.AmountCents));
        Assert.Equal(new DateOnly(2026, 10, 14), f.Rows[1].Date); // 2-digit year
        Assert.Equal("Supplies, snacks", f.Rows[4].Memo);
    }

    [Theory]
    [InlineData("123.45", 12345)]
    [InlineData("-123.45", -12345)]
    [InlineData("(123.45)", -12345)]
    [InlineData("1,234.56", 123456)]
    [InlineData("(1,234.56)", -123456)]
    [InlineData("$1,234.56", 123456)]
    [InlineData("-$5.00", -500)]
    [InlineData("212.40-", -21240)]
    [InlineData("0.00", 0)]
    [InlineData(" 7 ", 700)]
    public void Amount_formats(string text, long cents)
    {
        Assert.True(QbRegisterCsvReader.TryParseAmount(text, out var c));
        Assert.Equal(cents, c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("()")]
    public void Bad_amounts(string? text) => Assert.False(QbRegisterCsvReader.TryParseAmount(text, out _));

    [Fact]
    public void Missing_header_or_amount_columns_is_an_error()
    {
        Assert.Throws<QbRegisterFormatException>(() => QbRegisterCsvReader.Read("a,b,c\r\n1,2,3\r\n"));
        Assert.Throws<QbRegisterFormatException>(() => QbRegisterCsvReader.Read(",Type,Date,Debit\r\n"[..^7] + "Num\r\n"));
    }

    [Fact]
    public void Rows_without_any_amount_are_warned_and_skipped()
    {
        var f = QbRegisterCsvReader.Read(",Type,Date,Name,Debit,Credit\n,Check,10/01/2026,X,,\n,Check,10/02/2026,Y,,1.00\n");
        Assert.Single(f.Rows);
        Assert.Single(f.Warnings);
    }

    [Fact]
    public void Csv_parser_handles_quotes_doubled_quotes_and_newlines_in_fields()
    {
        var rows = QbRegisterCsvReader.ParseCsv("a,\"b, c\",\"say \"\"hi\"\"\"\r\n\"multi\nline\",2\n");
        Assert.Equal(["a", "b, c", "say \"hi\""], rows[0]);
        Assert.Equal(["multi\nline", "2"], rows[1]);
    }

    [Fact]
    public void Utf8_and_cp1252_files_decode()
    {
        var text = ",Type,Date,Name,Amount\r\n,Check,10/01/2026,Café Roma,-1.00\r\n";
        Assert.Equal("Café Roma", QbRegisterCsvReader.Read(TestData.Utf8(text)).Rows[0].Name);
        Assert.Equal("Café Roma", QbRegisterCsvReader.Read(TestData.Cp1252(text)).Rows[0].Name);
    }
}

public class QbMatcherTests
{
    private static readonly DateOnly D = new(2026, 10, 14);

    private static MatchBank B(long id, int day = 0, long cents = -5000, string? check = null, string payee = "SHELL OIL 123", long acct = 1, bool linkOnly = false) =>
        new(id, acct, D.AddDays(day), cents, check, payee, null, linkOnly);

    private static MatchQb Q(long id, int day = 0, long cents = -5000, string? num = null, string? name = "Shell", long acct = 1, string? memo = null) =>
        new(id, acct, D.AddDays(day), cents, num, name, memo, "Check");

    [Fact]
    public void Exact_amount_and_account_are_required()
    {
        var r = QbMatcher.Match([B(1)], [Q(10, cents: -5001), Q(11, acct: 2)]);
        Assert.Empty(r.Matches);
        Assert.Empty(r.Ambiguous);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(-5, true)]
    [InlineData(6, false)]
    public void Date_window_is_five_days(int offset, bool matches) =>
        Assert.Equal(matches, QbMatcher.Match([B(1)], [Q(10, day: offset)]).Matches.ContainsKey(1));

    [Fact]
    public void Check_number_matches_regardless_of_date_up_to_sixty_days()
    {
        Assert.Equal(10, QbMatcher.Match([B(1, check: "1042")], [Q(10, day: 40, num: "01042")]).Matches[1]);
        Assert.Empty(QbMatcher.Match([B(1, check: "1042")], [Q(10, day: 61, num: "1042")]).Matches);
    }

    [Fact]
    public void Check_number_beats_a_closer_date_and_different_numbers_never_match()
    {
        var r = QbMatcher.Match([B(1, check: "1042")], [Q(10, day: 0, num: "1043"), Q(11, day: 20, num: "1042"), Q(12, day: 1)]);
        Assert.Equal(11, r.Matches[1]);

        var none = QbMatcher.Match([B(1, check: "1042")], [Q(10, num: "1043")]);
        Assert.Empty(none.Matches);
    }

    [Fact]
    public void Non_numeric_qb_num_does_not_count_as_a_check_number()
    {
        Assert.Equal("", QbMatcher.NormalizeCheck("DEBIT"));
        Assert.Equal("", QbMatcher.NormalizeCheck("EFT 12"));
        Assert.Equal("1042", QbMatcher.NormalizeCheck("001042"));
        Assert.Equal(10, QbMatcher.Match([B(1, check: "1042")], [Q(10, num: "DEBIT")]).Matches[1]);
    }

    [Fact]
    public void Closest_date_wins()
    {
        var r = QbMatcher.Match([B(1)], [Q(10, day: 3), Q(11, day: -1), Q(12, day: 4)]);
        Assert.Equal(11, r.Matches[1]);
    }

    [Fact]
    public void Name_similarity_breaks_a_date_tie()
    {
        var r = QbMatcher.Match([B(1, payee: "SHELL OIL 57444 AUSTIN TX")], [Q(10, day: 2, name: "Chevron"), Q(11, day: -2, name: "Shell Oil")]);
        Assert.Equal(11, r.Matches[1]);
    }

    [Fact]
    public void Equal_candidates_are_ambiguous_never_auto_picked()
    {
        var r = QbMatcher.Match([B(1, payee: "TRANSFER")], [Q(10, day: 2, name: "Bob"), Q(11, day: -2, name: "Ann")]);
        Assert.Empty(r.Matches);
        Assert.Equal([10L, 11L], r.Ambiguous[1].Order());
    }

    [Fact]
    public void Two_bank_rows_competing_for_one_qb_row_are_both_ambiguous()
    {
        var r = QbMatcher.Match([B(1, day: -1, payee: "ZELLE A"), B(2, day: 1, payee: "ZELLE B")], [Q(10, name: "Zelle")]);
        Assert.Empty(r.Matches);
        Assert.Equal(2, r.Ambiguous.Count);
    }

    [Fact]
    public void One_to_one_each_qb_row_satisfies_only_one_bank_row()
    {
        // Two identical parking charges, one QB entry: one bank row matches, the other still needs export.
        var r = QbMatcher.Match([B(1, payee: "PARKING"), B(2, payee: "PARKING")], [Q(10, name: "Parking")]);
        Assert.Single(r.Matches);
        Assert.Empty(r.Ambiguous);

        var both = QbMatcher.Match([B(1, payee: "PARKING"), B(2, payee: "PARKING")], [Q(10, name: "Parking"), Q(11, name: "Parking")]);
        Assert.Equal(2, both.Matches.Count);
        Assert.Equal(2, both.Matches.Values.Distinct().Count());
    }

    [Fact]
    public void Unique_matches_resolve_first_then_the_rest()
    {
        // Bank 1 can only be QB 10 (check number); bank 2 then has QB 11 alone.
        var r = QbMatcher.Match([B(1, check: "7"), B(2, day: 2)], [Q(10, num: "7"), Q(11, day: 1)]);
        Assert.Equal(10, r.Matches[1]);
        Assert.Equal(11, r.Matches[2]);
    }

    [Fact]
    public void Rejected_pairs_are_never_matched()
    {
        var r = QbMatcher.Match([B(1)], [Q(10)], new HashSet<(long, long)> { (1, 10) });
        Assert.Empty(r.Matches);
    }

    [Fact]
    public void Candidates_are_ranked_for_the_pick_list()
    {
        var c = QbMatcher.Candidates(B(1), [Q(10, day: 4), Q(11, day: 1), Q(12, cents: -1), Q(13, day: 9)]);
        Assert.Equal([11L, 10L], c.Select(x => x.Qb.Id));
    }

    [Theory]
    [InlineData("SHELL OIL 57444", "Shell Oil", 1.0)]
    [InlineData("AMAZON MKTPL*AB12", "Amazon", 1.0)]
    [InlineData("DELTA AIR LINES ATLANTA", "Delta Air Lines", 1.0)]
    [InlineData("PAYROLL ACME CORP", "Acme Corp", 0.67)]
    [InlineData("SHELL", "Chevron", 0.0)]
    [InlineData("", "X", 0.0)]
    public void Name_similarity(string bank, string qb, double expected) =>
        Assert.Equal(expected, QbMatcher.NameSimilarity(bank, qb), 2);
}

public class TransferPairerTests
{
    private static readonly DateOnly D = new(2026, 10, 20);

    private static PairCandidate P(long id, long acct, long cents, int day = 0, bool inQb = false, string? payee = null) =>
        new(id, acct, D.AddDays(day), cents, inQb, payee);

    [Fact]
    public void Out_and_in_between_accounts_within_five_days_pair()
    {
        var pairs = TransferPairer.Pair([P(1, 1, -50000), P(2, 2, 50000, day: 3)]);
        Assert.Equal(new TransferPair(1, 2), Assert.Single(pairs));
    }

    [Fact]
    public void Same_account_wrong_amount_or_too_far_do_not_pair()
    {
        Assert.Empty(TransferPairer.Pair([P(1, 1, -50000), P(2, 1, 50000)]));
        Assert.Empty(TransferPairer.Pair([P(1, 1, -50000), P(2, 2, 49999)]));
        Assert.Empty(TransferPairer.Pair([P(1, 1, -50000), P(2, 2, 50000, day: 6)]));
    }

    [Fact]
    public void One_side_in_qb_still_pairs_but_both_in_qb_does_not()
    {
        Assert.Single(TransferPairer.Pair([P(1, 1, -50000, inQb: true), P(2, 2, 50000)]));
        Assert.Single(TransferPairer.Pair([P(1, 1, -50000), P(2, 2, 50000, inQb: true)]));
        Assert.Empty(TransferPairer.Pair([P(1, 1, -50000, inQb: true), P(2, 2, 50000, inQb: true)]));
    }

    [Fact]
    public void Closest_date_wins_and_ties_are_left_alone()
    {
        var pairs = TransferPairer.Pair([P(1, 1, -50000), P(2, 2, 50000, day: 4), P(3, 3, 50000, day: 1)]);
        Assert.Equal(new TransferPair(1, 3), Assert.Single(pairs));

        Assert.Empty(TransferPairer.Pair([P(1, 1, -50000), P(2, 2, 50000, day: 2), P(3, 3, 50000, day: -2)]));
    }

    [Fact]
    public void Rejected_pairs_are_skipped()
    {
        Assert.Empty(TransferPairer.Pair([P(1, 1, -50000), P(2, 2, 50000)], new HashSet<(long, long)> { (2, 1) }));
    }
}

public class PayeeLearnerTests
{
    private static readonly DateOnly D = new(2026, 6, 1);

    [Fact]
    public void Profiles_pick_most_used_account_and_ignore_split_rows_for_it()
    {
        var rows = new List<LearnRow>();
        for (int i = 0; i < 9; i++) rows.Add(new("Shell", "Auto:Fuel", D.AddDays(i)));
        rows.Add(new("Shell", "Meals", D.AddDays(20)));
        rows.Add(new("Shell", "-SPLIT-", D.AddDays(30)));
        rows.Add(new("Costco", "-SPLIT-", D));
        rows.Add(new("", "Rent", D));

        var p = Assert.Single(PayeeLearner.BuildProfiles(rows));
        Assert.Equal(("Shell", "Auto:Fuel", 9, 10, 1), (p.Name, p.TopAccount, p.TopCount, p.SingleAccountRows, p.SplitRows));
        Assert.Equal(0.9, p.Agreement, 3);
        Assert.Equal(D.AddDays(30), p.LastUsed);
    }

    [Theory]
    [InlineData("POS PURCHASE SHELL OIL 57444 AUSTIN TX", "SHELL OIL")]
    [InlineData("DEBIT CARD PURCHASE 10/12 STARBUCKS #1234 SEATTLE WA", "STARBUCKS")]
    [InlineData("ACH DEBIT VERIZON WIRELESS PAYMENTS", "VERIZON WIRELESS PAYMENTS")]
    [InlineData("AMAZON MKTPL*AB12C3", "AMAZON MKTPL")]
    [InlineData("SQ *JOES COFFEE", "JOES COFFEE")]
    [InlineData("PAYPAL *NETFLIX.COM", "NETFLIX.COM")]
    [InlineData("CHECKCARD 1012 HOME DEPOT 0602 DENVER CO", "HOME DEPOT")]
    [InlineData("Shell  oil   123", "SHELL OIL")]
    [InlineData("12345", "")]
    public void Bank_text_cleaning(string raw, string expected) => Assert.Equal(expected, PayeeLearner.CleanBankText(raw));

    [Theory]
    [InlineData("Delta Air Lines, Inc.", "DELTA AIR LINES")]
    [InlineData("Acme Corp:Project X", "PROJECT X")]
    [InlineData("The Home Depot", "HOME DEPOT")]
    [InlineData("AT&T", "AT&T")]
    public void Name_to_pattern(string name, string expected) => Assert.Equal(expected, PayeeLearner.NameToPattern(name));

    private static MatchedPair M(string bank, string name = "Shell", string split = "Auto:Fuel") => new(bank, name, split);

    [Fact]
    public void Matched_pairs_group_by_cleaned_text_with_agreement()
    {
        var pairs = new[]
        {
            M("POS PURCHASE SHELL OIL 57444 AUSTIN TX"), M("SHELL OIL 12345 DALLAS TX"), M("SHELL OIL 999 AUSTIN TX"),
            M("SHELL OIL 777 WACO TX", split: "Meals"),
            M("COSTCO WHSE #123", name: "Costco", split: "-SPLIT-"),
        };
        var p = Assert.Single(PayeeLearner.Propose(pairs, []));
        Assert.Equal(("SHELL OIL", "Shell", "Auto:Fuel", 4, ProposalBasis.MatchedBankText), (p.Pattern, p.QbName, p.Account, p.Count, p.Basis));
        Assert.Equal(0.75, p.Agreement, 3);
        Assert.False(p.MeetsThreshold());
        Assert.Equal(4, p.Samples.Count);
    }

    [Fact]
    public void Threshold_is_three_occurrences_and_ninety_percent()
    {
        var three = PayeeLearner.Propose([M("SHELL OIL 1"), M("SHELL OIL 2"), M("SHELL OIL 3")], []).Single();
        Assert.True(three.MeetsThreshold());
        var two = PayeeLearner.Propose([M("SHELL OIL 1"), M("SHELL OIL 2")], []).Single();
        Assert.False(two.MeetsThreshold());
        Assert.True(two.MeetsThreshold(minCount: 2));
    }

    [Fact]
    public void Qb_name_proposals_fill_in_for_names_without_matched_pairs()
    {
        var profiles = new[]
        {
            new PayeeProfile("Shell", "Auto:Fuel", 10, 10, 0, D),
            new PayeeProfile("Delta Air Lines", "Travel", 5, 5, 0, D),
            new PayeeProfile("Bob", "Misc", 4, 4, 0, D), // pattern too short
        };
        var proposals = PayeeLearner.Propose([M("SHELL OIL 1"), M("SHELL OIL 2"), M("SHELL OIL 3")], profiles);

        Assert.Equal(2, proposals.Count);
        var delta = proposals.Single(p => p.Basis == ProposalBasis.QbName);
        Assert.Equal(("DELTA AIR LINES", "Delta Air Lines", "Travel", 5, 1.0), (delta.Pattern, delta.QbName, delta.Account, delta.Count, delta.Agreement));
    }

    [Fact]
    public void Learned_pattern_matches_every_sample_despite_odd_spacing()
    {
        var p = PayeeLearner.Propose([M("POS PURCHASE  SHELL-OIL 1"), M("POS PURCHASE  SHELL-OIL 2"), M("POS PURCHASE SHELL-OIL 3")], []).Single();
        Assert.All(p.Samples, s => Assert.Contains(p.Pattern.Replace(" ", ""), s.Replace(" ", "")));
    }
}

public class SplitTemplateMathTests
{
    [Fact]
    public void Percent_lines_round_and_last_line_takes_remainder()
    {
        var lines = new[]
        {
            new TemplateLine(1, null, null, TemplateLineKind.Percent, 3333, null),
            new TemplateLine(2, null, null, TemplateLineKind.Percent, 3333, null),
            new TemplateLine(3, null, "Admin", TemplateLineKind.Percent, 0, "rest"),
        };
        var splits = SplitTemplateMath.Apply(lines, -10000);
        Assert.Equal([-3333L, -3333L, -3334L], splits.Select(s => s.AmountCents));
        Assert.Equal(-10000, splits.Sum(s => s.AmountCents));
        Assert.Equal(("Admin", "rest", 3), (splits[2].Class, splits[2].Memo, splits[2].Seq));
    }

    [Fact]
    public void Fixed_lines_and_deposits()
    {
        var lines = new[]
        {
            new TemplateLine(1, null, null, TemplateLineKind.Fixed, 2500, null),
            new TemplateLine(2, null, null, TemplateLineKind.Percent, 0, null),
        };
        Assert.Equal([2500L, 7500L], SplitTemplateMath.Apply(lines, 10000).Select(s => s.AmountCents));
        Assert.Equal([-2500L, -7500L], SplitTemplateMath.Apply(lines, -10000).Select(s => s.AmountCents));
    }

    [Fact]
    public void Round_trip_from_splits()
    {
        var splits = new[] { new TxnSplit { CategoryId = 1, AmountCents = -100000 }, new TxnSplit { CategoryId = 2, AmountCents = -25000 } };
        var pct = SplitTemplateMath.FromSplits(splits, -125000, TemplateLineKind.Percent);
        Assert.Equal([8000L, 2000L], pct.Select(l => l.Value));
        Assert.Equal([-40000L, -10000L], SplitTemplateMath.Apply(pct, -50000).Select(s => s.AmountCents));

        var fixedLines = SplitTemplateMath.FromSplits(splits, -125000, TemplateLineKind.Fixed);
        Assert.Equal([100000L, 25000L], fixedLines.Select(l => l.Value));
    }

    [Fact]
    public void Empty_template_is_an_error() =>
        Assert.Throws<ArgumentException>(() => SplitTemplateMath.Apply([], -100));
}
