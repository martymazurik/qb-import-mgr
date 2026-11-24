using QbImportMgr.Core.Domain;
using QbImportMgr.Data.Services;

namespace QbImportMgr.Tests.Data;

public class ImportServiceTests : DataTestBase
{
    [Fact]
    public void Scan_inbox_finds_ofx_family_only()
    {
        Drop("a.qbo", [1]); Drop("b.QFX", [1]); Drop("c.ofx", [1]); Drop("d.csv", [1]); Drop("e.iif", [1]);
        Directory.CreateDirectory(Path.Combine(Paths.Archive, "nested"));
        Assert.Equal(["a.qbo", "b.QFX", "c.ofx"], Import.ScanInbox().Select(Path.GetFileName));
    }

    [Fact]
    public void Imports_bank_file_stores_raw_bytes_and_archives()
    {
        var r = ImportSample("bank_sgml_1x.qbo");

        Assert.Equal(FileImportStatus.Imported, r.Status);
        var st = Assert.Single(r.Statements);
        Assert.True(st.NewAccount);
        Assert.Equal((5, 5, 0, 0), (st.Parsed, st.Inserted, st.SkippedExisting, st.FlaggedDuplicates));
        Assert.Empty(Directory.GetFiles(Paths.Inbox));
        Assert.True(File.Exists(r.ArchivedTo));
        Assert.StartsWith(Paths.Archive, r.ArchivedTo);

        using var s = Db.Open();
        var stmt = s.Files.StatementsForAccount(st.BankAccountId).Single();
        Assert.Equal(TestData.Sample("bank_sgml_1x.qbo"), s.Files.Get(stmt.QboFileId)!.RawBytes);
        Assert.All(s.Txns.ListByAccount(st.BankAccountId), t => Assert.Equal(t.AmountCents, t.SignedAmountCents));
    }

    [Fact]
    public void Same_file_again_is_skipped_by_hash_and_archived()
    {
        ImportSample("bank_sgml_1x.qbo");
        var again = ImportSample("bank_sgml_1x.qbo", "renamed.qbo");

        Assert.Equal(FileImportStatus.AlreadyImported, again.Status);
        Assert.NotNull(again.ArchivedTo);
        Assert.Equal(5, AllTxns().Count);
        Assert.Equal(2, Directory.GetFiles(Paths.Archive).Length);
    }

    [Fact]
    public void Unparseable_file_fails_and_stays_in_inbox()
    {
        var path = Drop("junk.qbo", "not ofx at all"u8.ToArray());
        var r = Import.ImportFile(path);

        Assert.Equal(FileImportStatus.Failed, r.Status);
        Assert.NotNull(r.Error);
        Assert.True(File.Exists(path));
        Assert.Empty(AllTxns());
    }

    [Fact]
    public void Overlapping_download_skips_same_fitids_and_flags_reissued_fitid()
    {
        ImportText("oct.qbo", BankOfx("777", "900.00", "20261015",
            Trn("20261005", "-40.00", "F1", "GROCERY MART"),
            Trn("20261010", "-60.00", "F2", "GAS STATION 123")));

        var r = ImportText("oct-nov.qbo", BankOfx("777", "850.00", "20261020",
            Trn("20261005", "-40.00", "F1", "GROCERY MART"),            // same FITID: skipped
            Trn("20261011", "-60.00", "F2-NEW", "GAS STATION 123"),     // reissued FITID: flagged
            Trn("20261018", "50.00", "F3", "REFUND", "CREDIT")));       // new

        var st = Assert.Single(r.Statements);
        Assert.False(st.NewAccount);
        Assert.Equal((3, 2, 1, 1), (st.Parsed, st.Inserted, st.SkippedExisting, st.FlaggedDuplicates));

        var txns = AllTxns();
        var dup = txns.Single(t => t.FitId == "F2-NEW");
        Assert.Equal(TxnStatus.Duplicate, dup.Status);
        Assert.Equal(txns.Single(t => t.FitId == "F2").Id, dup.DuplicateOfTxnId);
        Assert.Equal(TxnStatus.New, txns.Single(t => t.FitId == "F3").Status);
    }

    [Fact]
    public void Identical_charges_in_one_download_are_both_kept()
    {
        var r = ImportSample("amex_ccstmt.qfx");
        Assert.Equal(0, r.Statements[0].FlaggedDuplicates);
        Assert.Equal(2, AllTxns().Count(t => t.Name == "PARKING METER 4471" && t.Status == TxnStatus.New));
    }

    [Fact]
    public void Missing_fitids_across_downloads_are_flagged_not_dropped()
    {
        ImportText("a.qbo", BankOfx("777", "1.00", "20261031", Trn("20261028", "-5.00", null, "SERVICE FEE")));
        var r = ImportText("b.qbo", BankOfx("777", "1.00", "20261101", Trn("20261028", "-5.00", null, "SERVICE FEE")));

        Assert.Equal(1, r.Statements[0].FlaggedDuplicates);
        Assert.Equal(2, AllTxns().Count);
    }

    [Fact]
    public void Multi_statement_file_creates_three_accounts()
    {
        var r = ImportSample("multi_stmt.ofx");
        Assert.Equal(3, r.Statements.Count);
        Assert.All(r.Statements, st => Assert.True(st.NewAccount));
        Assert.Equal(["Checking ****2233", "Savings ****2244", "Credit Card ****1111"], r.Statements.Select(x => x.AccountDisplay));
        Assert.Equal(4, AllTxns().Count);
    }

    [Fact]
    public void Rules_and_aliases_apply_on_import_and_count_hits()
    {
        Lists.Import(TestData.Sample("qb_lists.iif"));
        using (var s = Db.Open())
        {
            s.Rules.Insert(new Rule { Pattern = "AT&T", CategoryId = s.Categories.GetByName("Utilities:Telephone")!.Id, Priority = 10 });
            s.Rules.Insert(new Rule { Pattern = "SERVICE FEE", AutoExclude = true });
            s.Aliases.Insert(new PayeeAlias { RawPattern = "PAYROLL ACME", CleanPayee = "Acme Corp" });
        }

        var r = ImportSample("bank_sgml_1x.qbo");
        Assert.Equal((1, 1), (r.Statements[0].Categorized, r.Statements[0].Excluded));

        var txns = AllTxns();
        Assert.Equal(TxnStatus.Categorized, txns.Single(t => t.Name == "AT&T *PAYMENT").Status);
        Assert.Equal(TxnStatus.Excluded, txns.Single(t => t.Name == "SERVICE FEE").Status);
        Assert.Equal("Acme Corp", txns.Single(t => t.Name == "PAYROLL ACME CORP").EffectivePayee);

        using var s2 = Db.Open();
        Assert.All(s2.Rules.GetAll(), rule => Assert.Equal(1, rule.HitCount));
    }

    [Fact]
    public void Invert_sign_account_normalizes_new_imports()
    {
        ImportText("a.qbo", BankOfx("888", "0", "20261031", Trn("20261005", "10.00", "A", "CHARGE")));
        var acct = Accounts.GetAll().Single();
        Accounts.Update(acct.Id, acct.DisplayName, null, invertSign: true);
        ImportText("b.qbo", BankOfx("888", "0", "20261101", Trn("20261020", "7.00", "B", "CHARGE TWO")));

        var txns = AllTxns();
        Assert.All(txns, t => Assert.Equal(-t.AmountCents, t.SignedAmountCents));
        Assert.Equal([1000L, 700L], txns.Select(t => t.AmountCents)); // raw untouched
    }
}
