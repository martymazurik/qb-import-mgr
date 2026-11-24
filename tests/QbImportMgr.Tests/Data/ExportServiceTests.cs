using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Iif;
using QbImportMgr.Data.Services;

namespace QbImportMgr.Tests.Data;

public class ExportServiceTests : DataTestBase
{
    private IReadOnlyList<long> ReadyBankRows()
    {
        ImportSample("bank_sgml_1x.qbo");
        SetUpQuickBooks();
        var txns = AllTxns();
        Txns.SetCategory(txns.Single(t => t.Name == "AT&T *PAYMENT").Id, CategoryId("Utilities:Telephone"));
        Txns.SetCategory(txns.Single(t => t.Name == "PAYROLL ACME CORP").Id, CategoryId("Sales Income"));
        var check = txns.Single(t => t.CheckNum == "1042");
        Txns.SetSplits(check.Id,
        [
            new TxnSplit { CategoryId = CategoryId("Travel"), AmountCents = -100000, Memo = "Airfare" },
            new TxnSplit { CategoryId = CategoryId("Meals and Entertainment"), AmountCents = -25000 },
        ]);
        var ids = new[] { "AT&T *PAYMENT", "PAYROLL ACME CORP", "CHECK 1042" }.Select(n => txns.Single(t => t.Name == n).Id).ToList();
        Txns.MarkReady(ids);
        return ids;
    }

    [Fact]
    public void Export_writes_iif_backs_up_marks_exported_and_audits()
    {
        var ids = ReadyBankRows();

        var preview = Export.PreviewReady();
        Assert.True(preview.Validation.CanExport, string.Join("; ", preview.Validation.Issues.Select(i => i.Message)));
        Assert.Equal(3, preview.Items.Count);

        var r = Export.Export(ids);

        Assert.True(File.Exists(r.IifPath));
        Assert.StartsWith(Paths.Iif, r.IifPath);
        Assert.True(File.Exists(r.BackupPath));
        Assert.Empty(Directory.GetFiles(Paths.Iif, "*.tmp"));

        var text = IifWriter.Encoding.GetString(File.ReadAllBytes(r.IifPath));
        Assert.Equal(3, text.Split("\r\n").Count(l => l.StartsWith("TRNS\t")));
        Assert.Contains("TRNS\t\tCHECK\t10/02/2026\tChecking\t\t\t-54.23\t\tAT&T *PAYMENT - ACH DEBIT\tY", text);
        Assert.Contains("SPL\t\tCHECK\t10/02/2026\tUtilities:Telephone\t\t\t54.23\t", text);
        Assert.Contains("TRNS\t\tDEPOSIT\t10/15/2026\tChecking\t\t\t2500.00\t", text);
        Assert.Contains("SPL\t\tCHECK\t10/05/2026\tTravel\t\t\t1000.00\t1042\tAirfare\tN", text);
        Assert.Contains("SPL\t\tCHECK\t10/05/2026\tMeals and Entertainment\t\t\t250.00\t1042\t", text);

        Assert.All(ids, id => Assert.Equal((TxnStatus.Exported, (long?)r.Batch.Id), (Get(id).Status, Get(id).BatchId)));
        Assert.Equal(-54_23 + 2500_00 - 1250_00, r.Batch.TotalCents);

        using var s = Db.Open();
        Assert.Contains(s.Audit.For("ExportBatch", r.Batch.Id), a => a.Action == "Exported");
        Assert.Empty(s.Txns.ListByStatus(TxnStatus.Ready));
    }

    [Fact]
    public void Export_writes_vendor_on_trns_only_and_keeps_customer_split_names()
    {
        ImportSample("bank_sgml_1x.qbo");
        SetUpQuickBooks();
        long NameId(string n) { using var s = Db.Open(); return s.Names.FindByName(n)!.Id; }

        var txns = AllTxns();
        var att = txns.Single(t => t.Name == "AT&T *PAYMENT");
        Txns.SetCategory(att.Id, CategoryId("Utilities:Telephone"));
        Txns.SetName(att.Id, NameId("AT&T"));
        var check = txns.Single(t => t.CheckNum == "1042");
        Txns.SetName(check.Id, NameId("Delta Air Lines"));
        Txns.SetSplits(check.Id,
        [
            new TxnSplit { CategoryId = CategoryId("Travel"), QbNameId = NameId("Acme Corp:Project X"), AmountCents = -100000 },
            new TxnSplit { CategoryId = CategoryId("Meals and Entertainment"), QbNameId = NameId("Delta Air Lines"), AmountCents = -25000 },
        ]);
        Txns.MarkReady([att.Id, check.Id]);

        var text = IifWriter.Encoding.GetString(File.ReadAllBytes(Export.Export([att.Id, check.Id]).IifPath));

        Assert.Contains("TRNS\t\tCHECK\t10/02/2026\tChecking\tAT&T\t\t-54.23\t", text);
        Assert.Contains("SPL\t\tCHECK\t10/02/2026\tUtilities:Telephone\t\t\t54.23\t", text);
        Assert.Contains("TRNS\t\tCHECK\t10/05/2026\tChecking\tDelta Air Lines\t\t-1250.00\t1042\t", text);
        Assert.Contains("SPL\t\tCHECK\t10/05/2026\tTravel\tAcme Corp:Project X\t\t1000.00\t1042\t", text);
        Assert.Contains("SPL\t\tCHECK\t10/05/2026\tMeals and Entertainment\t\t\t250.00\t1042\t", text);
        Assert.Equal(2, text.Split("\r\n").Count(l => l.StartsWith("TRNS\t") && l.Split('\t')[5] != ""));
        Assert.Equal(1, text.Split("\r\n").Count(l => l.StartsWith("SPL\t") && l.Split('\t')[5] != ""));
    }

    [Fact]
    public void Clear_setting_controls_iif_clear_column()
    {
        var ids = ReadyBankRows();
        using (var s = Db.Open()) s.Settings.SetIifClear(false);
        var r = Export.Export(ids);
        var trns = File.ReadAllLines(r.IifPath, IifWriter.Encoding).Where(l => l.StartsWith("TRNS\t"));
        Assert.All(trns, l => Assert.EndsWith("\tN", l));
    }

    [Fact]
    public void Validation_failure_blocks_without_backup_batch_or_file()
    {
        ImportSample("bank_sgml_1x.qbo");
        Lists.Import(TestData.Sample("qb_lists.iif")); // account deliberately not mapped
        var id = AllTxns()[0].Id;
        Txns.SetCategory(id, CategoryId("Travel"));
        Txns.MarkReady([id]);

        var ex = Assert.Throws<ExportValidationException>(() => Export.Export([id]));
        Assert.Contains(ex.Result.Errors, e => e.Message.Contains("not mapped"));
        Assert.Empty(Directory.GetFiles(Paths.Backup));
        Assert.Empty(Directory.GetFiles(Paths.Iif));
        Assert.Empty(Export.Batches());
        Assert.Equal(TxnStatus.Ready, Get(id).Status);
    }

    [Fact]
    public void Duplicate_excluded_and_unready_rows_cannot_be_exported()
    {
        ImportText("a.qbo", BankOfx("777", "1.00", "20261031", Trn("20261010", "-60.00", "F2", "GAS STATION 123")));
        ImportText("b.qbo", BankOfx("777", "1.00", "20261101", Trn("20261011", "-60.00", "F2-NEW", "GAS STATION 123")));
        SetUpQuickBooks();
        var txns = AllTxns();
        var dup = txns.Single(t => t.Status == TxnStatus.Duplicate);
        var original = txns.Single(t => t.Status == TxnStatus.New);

        Assert.Empty(Export.PreviewReady().Items); // nothing Ready
        Assert.Contains(Export.Preview([dup.Id]).Validation.Errors, e => e.Message.Contains("Duplicate"));

        Txns.ConfirmDuplicate(dup.Id);
        Assert.Contains(Export.Preview([dup.Id]).Validation.Errors, e => e.Message.Contains("Excluded"));
        Assert.Throws<TxnStateException>(() => Txns.MarkReady([dup.Id]));

        Txns.SetCategory(original.Id, CategoryId("Travel"));
        Txns.MarkReady([original.Id]);
        Assert.Equal([original.Id], Export.PreviewReady().Items.Select(i => i.TxnId));
    }

    [Fact]
    public void Void_returns_rows_to_ready_renames_file_and_allows_reexport()
    {
        var ids = ReadyBankRows();
        var first = Export.Export(ids);

        Assert.Equal(3, Export.VoidBatch(first.Batch.Id));
        Assert.All(ids, id => Assert.Equal((TxnStatus.Ready, (long?)null), (Get(id).Status, Get(id).BatchId)));
        Assert.False(File.Exists(first.IifPath));
        var voided = Path.Combine(Paths.IifVoided, "2026-11", Path.GetFileName(first.IifPath) + ".voided");
        Assert.True(File.Exists(voided));
        var batch = Export.Batches().Single();
        Assert.Equal((BatchStatus.Voided, voided), (batch.Status, batch.IifPath));
        Assert.NotNull(batch.VoidedUtc);
        Assert.Throws<TxnStateException>(() => Export.VoidBatch(first.Batch.Id));

        using (var s = Db.Open())
            Assert.All(ids, id => Assert.Contains(s.Audit.For("Txn", id), a => a.Action == "BatchVoided"));

        // Editable again, and exportable again.
        Txns.Unready(ids[0]);
        Txns.MarkReady([ids[0]]);
        Clock.Advance(TimeSpan.FromMinutes(1));
        var second = Export.Export(ids);
        Assert.NotEqual(first.Batch.Id, second.Batch.Id);
        Assert.Equal(2, Directory.GetFiles(Paths.Backup).Length);
    }

    [Fact]
    public void Export_refuses_rows_that_changed_since_preview()
    {
        var ids = ReadyBankRows();
        // A row that is not Ready in the list: validation catches it.
        Txns.Unready(ids[0]);
        Assert.Throws<ExportValidationException>(() => Export.Export(ids));
        Assert.All(ids.Skip(1), id => Assert.Equal(TxnStatus.Ready, Get(id).Status));
    }

    [Fact]
    public void Invert_sign_change_is_blocked_once_exported()
    {
        var ids = ReadyBankRows();
        Export.Export(ids);
        var a = Accounts.GetAll().Single();
        Assert.Throws<TxnStateException>(() => Accounts.Update(a.Id, a.DisplayName, a.QbAccountName, invertSign: true));
        Accounts.Update(a.Id, "Operating", a.QbAccountName, a.InvertSign); // other settings still editable
        Assert.Equal("Operating", Accounts.GetAll().Single().DisplayName);
    }
}
