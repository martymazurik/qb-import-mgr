using Microsoft.Data.Sqlite;
using QbImportMgr.Core.Domain;
using QbImportMgr.Data.Services;

namespace QbImportMgr.Tests.Data;

public class SupportServiceTests : DataTestBase
{
    [Fact]
    public void Backup_is_a_readable_copy_and_keeps_newest_30()
    {
        ImportSample("bank_sgml_1x.qbo");
        for (int i = 0; i < 35; i++)
            File.WriteAllBytes(Path.Combine(Paths.Backup, $"{BackupService.Prefix}20250101_0000{i:00}_000.db"), [0]);
        File.WriteAllBytes(Path.Combine(Paths.Backup, "keep-me.txt"), [0]);

        var path = Backup.Backup();

        var backups = Directory.GetFiles(Paths.Backup, BackupService.Prefix + "*.db");
        Assert.Equal(30, backups.Length);
        Assert.Contains(path, backups);
        Assert.True(File.Exists(Path.Combine(Paths.Backup, "keep-me.txt")));

        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Txn";
        Assert.Equal(5L, cmd.ExecuteScalar());
    }

    [Fact]
    public void List_import_upserts_and_deactivates_missing_or_hidden()
    {
        var first = Lists.Import(TestData.Sample("qb_lists.iif"));
        Assert.Equal((8, 0, 0), (first.AccountsAdded, first.AccountsUpdated, first.AccountsDeactivated));
        Assert.Equal(6, first.NamesAdded);
        Assert.Equal(2, first.Classes);

        using (var s = Db.Open())
        {
            Assert.False(s.Categories.GetByName("Old Expense")!.IsActive);
            Assert.Equal(7, s.Categories.GetAll(activeOnly: true).Count);
            Assert.Equal(["Admin", "Field"], s.Settings.GetClasses());
        }

        var second = Lists.Import(TestData.Utf8("!ACCNT\tNAME\tACCNTTYPE\r\nACCNT\tChecking\tBANK\r\nACCNT\tTravel\tEXP\r\nACCNT\tNew One\tEXP\r\n"));
        Assert.Equal((1, 2, 5), (second.AccountsAdded, second.AccountsUpdated, second.AccountsDeactivated));
        Assert.Equal(0, second.NamesDeactivated); // no name lists in file: names untouched

        using var s2 = Db.Open();
        Assert.Equal(["Checking", "New One", "Travel"], s2.Categories.GetAll(activeOnly: true).Select(c => c.FullName));
        Assert.Equal(9, s2.Categories.GetAll().Count);
    }

    [Fact]
    public void List_import_rejects_files_without_lists()
    {
        Assert.Throws<InvalidDataException>(() => Lists.Import(TestData.Utf8("!TRNS\tDATE\r\n")));
    }

    [Fact]
    public void Ledger_check_ties_across_overlapping_downloads_ignoring_duplicates_and_excluded()
    {
        ImportText("a.qbo", BankOfx("777", "1000.00", "20261010",
            Trn("20261005", "-40.00", "F1", "GROCERY MART")));
        ImportText("b.qbo", BankOfx("777", "880.00", "20261020",
            Trn("20261005", "-40.00", "F1", "GROCERY MART"),           // same FITID: skipped; before A's date anyway
            Trn("20261012", "-60.00", "F2", "GAS STATION 123"),
            Trn("20261013", "-60.00", "F2B", "GAS STATION 123"),       // same download as F2: a real second charge
            Trn("20261015", "-25.00", "F3", "TRANSFER")));

        // Balance moved -120.00 but the rows sum to -145.00: 25.00 unexplained.
        var acct = Accounts.GetAll().Single();
        var status = Ledger.ForAccount(acct.Id)!;
        Assert.NotNull(status.Result);
        Assert.Equal(-12000, status.Result!.ExpectedChangeCents);
        Assert.Equal(-14500, status.Result.TxnSumCents);
        Assert.Equal(2500, status.Result.DifferenceCents);

        // Excluding the transfer makes it tie: excluded rows are left out.
        Txns.Exclude(AllTxns().Single(t => t.FitId == "F3").Id);
        Assert.True(Ledger.ForAccount(acct.Id)!.Result!.Ties);
    }

    [Fact]
    public void Ledger_check_leaves_out_flagged_duplicates()
    {
        ImportText("a.qbo", BankOfx("777", "1000.00", "20261010", Trn("20261012", "-60.00", "F2", "GAS STATION 123")));
        ImportText("b.qbo", BankOfx("777", "940.00", "20261020", Trn("20261013", "-60.00", "F2-NEW", "GAS STATION 123")));

        Assert.Single(AllTxns(), t => t.Status == TxnStatus.Duplicate);
        var r = Ledger.ForAccount(Accounts.GetAll().Single().Id)!.Result!;
        Assert.True(r.Ties);
        Assert.Equal(1, r.TxnCount);
    }

    [Fact]
    public void Ledger_check_explains_first_snapshot()
    {
        ImportSample("bank_sgml_1x.qbo");
        var status = Ledger.ForAccount(Accounts.GetAll().Single().Id)!;
        Assert.Null(status.Result);
        Assert.Contains("First ledger balance", status.Note);
    }
}
