using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Import;
using QbImportMgr.Core.Rules;

namespace QbImportMgr.Tests;

public class DuplicateDetectorTests
{
    private static readonly DateOnly D = new(2026, 10, 14);

    private static DupCandidate C(long id, long file, string? fitId, int dayOffset = 0, long cents = -500,
        string payee = "PARKING METER 4471", long acct = 1, TxnStatus status = TxnStatus.New) =>
        new(id, acct, file, fitId, D.AddDays(dayOffset), cents, payee, status);

    [Fact]
    public void Same_fitid_same_account_is_exact_duplicate()
    {
        var r = DuplicateDetector.Check(C(0, 2, "F1"), [C(10, 1, "F1", dayOffset: 30, cents: 1)]);
        Assert.Equal(DupKind.ExactFitId, r.Kind);
        Assert.Equal(10, r.MatchTxnId);
    }

    [Fact]
    public void Same_fitid_other_account_is_not_a_duplicate()
    {
        var r = DuplicateDetector.Check(C(0, 2, "F1", payee: "X"), [C(10, 1, "F1", acct: 2)]);
        Assert.Equal(DupKind.None, r.Kind);
    }

    [Fact]
    public void Identical_charges_in_the_same_download_are_both_real()
    {
        // Two $5 parking charges on the same day in one file; neither FITID nor fuzzy matching applies.
        var r = DuplicateDetector.Check(C(0, 1, "F2"), [C(10, 1, "F1")]);
        Assert.Equal(DupKind.None, r.Kind);

        var noFitIds = DuplicateDetector.Check(C(0, 1, null), [C(10, 1, null)]);
        Assert.Equal(DupKind.None, noFitIds.Kind);
    }

    [Theory]
    [InlineData("F9", "F1")]  // FITIDs differ across downloads (bank regenerated them)
    [InlineData(null, "F1")]  // new row lacks a FITID
    [InlineData("F9", null)]  // existing row lacks a FITID
    [InlineData(null, null)]
    public void Fuzzy_match_from_another_download_is_flagged(string? incomingFit, string? existingFit)
    {
        var r = DuplicateDetector.Check(C(0, 2, incomingFit, dayOffset: 2), [C(10, 1, existingFit)]);
        Assert.Equal(DupKind.Possible, r.Kind);
        Assert.Equal(10, r.MatchTxnId);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(-3, true)]
    [InlineData(4, false)]
    [InlineData(-4, false)]
    public void Date_window_is_plus_minus_three_days(int offset, bool flagged)
    {
        var r = DuplicateDetector.Check(C(0, 2, null, dayOffset: offset), [C(10, 1, null)]);
        Assert.Equal(flagged ? DupKind.Possible : DupKind.None, r.Kind);
    }

    [Fact]
    public void Amount_payee_and_account_must_match()
    {
        Assert.Equal(DupKind.None, DuplicateDetector.Check(C(0, 2, null, cents: -501), [C(10, 1, null)]).Kind);
        Assert.Equal(DupKind.None, DuplicateDetector.Check(C(0, 2, null, cents: 500), [C(10, 1, null)]).Kind);
        Assert.Equal(DupKind.None, DuplicateDetector.Check(C(0, 2, null, payee: "CITY GARAGE"), [C(10, 1, null)]).Kind);
        Assert.Equal(DupKind.None, DuplicateDetector.Check(C(0, 2, null, acct: 2), [C(10, 1, null)]).Kind);
    }

    [Fact]
    public void Truncated_or_reformatted_payee_still_matches()
    {
        var r = DuplicateDetector.Check(C(0, 2, null, payee: "Parking-Meter #4471 DENVER CO"), [C(10, 1, null, payee: "PARKING METER 4471")]);
        Assert.Equal(DupKind.Possible, r.Kind);
    }

    [Fact]
    public void Rows_already_flagged_duplicate_are_not_used_as_originals_and_closest_date_wins()
    {
        var existing = new[]
        {
            C(10, 1, null, dayOffset: 0, status: TxnStatus.Duplicate),
            C(11, 1, null, dayOffset: -3),
            C(12, 3, null, dayOffset: 1),
        };
        var r = DuplicateDetector.Check(C(0, 2, null), existing);
        Assert.Equal(12, r.MatchTxnId);
    }

    [Theory]
    [InlineData("AT&T *PAYMENT", "AT&T PAYMENT", true)]
    [InlineData("SHELL", "SHELL OIL 123", false)]  // prefix too short to trust
    [InlineData("AMAZON MKTPL", "AMAZON MKTPL*AB12C3", true)]
    [InlineData("", "", true)]
    [InlineData("", "X", false)]
    public void SamePayee(string a, string b, bool expected) => Assert.Equal(expected, PayeeText.SamePayee(a, b));
}
