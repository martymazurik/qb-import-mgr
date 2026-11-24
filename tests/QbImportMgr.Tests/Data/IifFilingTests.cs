using Dapper;
using Microsoft.Data.Sqlite;
using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Iif;
using QbImportMgr.Data.Db;
using QbImportMgr.Data.Services;

namespace QbImportMgr.Tests.Data;

public class IifFilingRulesTests
{
    [Theory]
    [InlineData("qb-import_20261124_113839_batch12_1764001234.iif", IifSideFileKind.QbErrorFile, 12L)]
    [InlineData("QB-IMPORT_20261124_113839_BATCH7_99.IIF", IifSideFileKind.QbErrorFile, 7L)]
    [InlineData("qb-import_20261124_113839_batch12_fixed.iif", IifSideFileKind.FixedFile, 12L)]
    [InlineData("my_own_fixed.iif", IifSideFileKind.FixedFile, null)]
    [InlineData("old_batch_2.iif", IifSideFileKind.QbErrorFile, null)]
    public void Side_files_are_recognised(string name, IifSideFileKind kind, long? batch)
    {
        Assert.True(IifFiling.TryClassifySideFile(name, out var k, out var b));
        Assert.Equal((kind, batch), (k, b));
    }

    [Theory]
    [InlineData("qb-import_20261124_113839_batch12.iif")] // our own export: waits in the root
    [InlineData("lists.iif")]
    [InlineData("qb-import_20261124_113839_batch12_1764001234.txt")]
    [InlineData("qb-import_20261124_113839_batch12.iif.voided")]
    public void Other_files_are_not_side_files(string name) =>
        Assert.False(IifFiling.TryClassifySideFile(name, out _, out _));

    [Fact]
    public void Month_folder_uses_local_time()
    {
        var eastern = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(-5), "t", "t");
        var utc = new DateTime(2026, 12, 1, 3, 0, 0, DateTimeKind.Utc); // Nov 30, 22:00 local
        Assert.Equal("2026-11", IifFiling.MonthFolder(utc, eastern));
        Assert.Equal("2026-12", IifFiling.MonthFolder(utc, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Free_name_never_reuses_an_existing_file()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\f\a.iif", @"C:\f\a (2).iif", @"C:\f\b.iif.voided" };
        Assert.Equal(@"C:\f\c.iif", IifFiling.FreeName(@"C:\f", "c.iif", taken.Contains));
        Assert.Equal(@"C:\f\a (3).iif", IifFiling.FreeName(@"C:\f", "a.iif", taken.Contains));
        Assert.Equal(@"C:\f\b (2).iif.voided", IifFiling.FreeName(@"C:\f", "b.iif.voided", taken.Contains));
    }
}

public class IifFilingTests : DataTestBase
{
    private string Month => Clock.Now.UtcDateTime.ToString("yyyy-MM"); // test clock's local zone is UTC

    private ExportResult ExportOne(string payee = "AT&T *PAYMENT")
    {
        if (!AllTxns().Any()) { ImportSample("bank_sgml_1x.qbo"); SetUpQuickBooks(); }
        var t = AllTxns().Single(x => x.Name == payee);
        Txns.SetCategory(t.Id, CategoryId("Utilities:Telephone"));
        Txns.MarkReady([t.Id]);
        Clock.Advance(TimeSpan.FromMinutes(1));
        return Export.Export([t.Id]);
    }

    private static int CountFiles(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;

    private IReadOnlyList<AuditLogEntry> Audit(string entity, long id)
    {
        using var s = Db.Open();
        return s.Audit.For(entity, id);
    }

    [Fact]
    public void Mark_imported_sets_status_and_files_the_iif_by_month()
    {
        var r = ExportOne();
        Assert.Equal(BatchStatus.Exported, Export.Batches().Single().Status);
        Assert.Equal(Paths.Iif, Path.GetDirectoryName(r.IifPath));

        var b = Export.MarkImported(r.Batch.Id);

        var dest = Path.Combine(Paths.IifImported, Month, Path.GetFileName(r.IifPath));
        Assert.Equal((BatchStatus.ImportedToQb, dest), (b.Status, b.IifPath));
        Assert.Equal(Clock.Now.UtcDateTime, b.ImportedUtc);
        Assert.True(File.Exists(dest));
        Assert.False(File.Exists(r.IifPath));
        Assert.Empty(Directory.GetFiles(Paths.Iif)); // the root only holds exports still waiting
        Assert.Contains(Audit("ExportBatch", b.Id), a => a.Action == "ImportedToQb" && a.Detail!.Contains("imported"));

        Assert.Throws<TxnStateException>(() => Export.MarkImported(b.Id));
        Assert.Equal(TxnStatus.Exported, AllTxns().Single(t => t.BatchId == b.Id).Status); // rows untouched
    }

    [Fact]
    public void Void_after_import_moves_file_to_voided_and_voided_cannot_be_marked_imported()
    {
        var r = ExportOne();
        Export.MarkImported(r.Batch.Id);

        Assert.Equal(1, Export.VoidBatch(r.Batch.Id));

        var dest = Path.Combine(Paths.IifVoided, Month, Path.GetFileName(r.IifPath) + ".voided");
        var b = Export.Batches().Single();
        Assert.Equal((BatchStatus.Voided, dest), (b.Status, b.IifPath));
        Assert.NotNull(b.ImportedUtc); // history kept
        Assert.True(File.Exists(dest));
        Assert.Equal(1, CountFiles(Paths.Iif));
        Assert.Throws<TxnStateException>(() => Export.MarkImported(b.Id));
    }

    [Fact]
    public void Mark_imported_with_missing_file_still_records_it()
    {
        var r = ExportOne();
        File.Move(r.IifPath, Path.Combine(Paths.Root, "elsewhere.iif")); // user moved it themselves

        var b = Export.MarkImported(r.Batch.Id);

        Assert.Equal((BatchStatus.ImportedToQb, r.IifPath), (b.Status, b.IifPath));
        Assert.Contains(Audit("ExportBatch", b.Id), a => a.Action == "ImportedToQb" && a.Detail!.Contains("\"FileMissing\":true"));
    }

    [Fact]
    public void A_failed_move_leaves_the_batch_unchanged()
    {
        var r = ExportOne();
        using (new FileStream(r.IifPath, FileMode.Open, FileAccess.Read, FileShare.None)) // QuickBooks has it open
            Assert.ThrowsAny<IOException>(() => Export.MarkImported(r.Batch.Id));

        var b = Export.Batches().Single();
        Assert.Equal((BatchStatus.Exported, r.IifPath), (b.Status, b.IifPath));
        Assert.True(File.Exists(r.IifPath));
        Assert.DoesNotContain(Audit("ExportBatch", b.Id), a => a.Action == "ImportedToQb");
    }

    [Fact]
    public void Startup_files_qb_error_and_fixed_files_with_their_batch_and_never_deletes()
    {
        var waiting = ExportOne("AT&T *PAYMENT");
        var imported = ExportOne("PAYROLL ACME CORP");
        var stem = Path.GetFileNameWithoutExtension(imported.IifPath);
        var errorFile = Path.Combine(Paths.Iif, $"{stem}_1764001234.iif");
        var fixedFile = Path.Combine(Paths.Iif, $"{stem}_fixed.iif");
        var orphan = Path.Combine(Paths.Iif, "old_batch_5.iif");
        var unrelated = Path.Combine(Paths.Iif, "lists.iif");
        foreach (var f in new[] { errorFile, fixedFile, orphan, unrelated }) File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(orphan, new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc));
        Export.MarkImported(imported.Batch.Id);
        // A file of the same name already filed: kept, the new one gets " (2)".
        var clash = Path.Combine(Paths.IifImported, Month, Path.GetFileName(fixedFile));
        File.WriteAllText(clash, "earlier");
        int before = CountFiles(Paths.Root);

        var report = Export.FileIifFolder();

        Assert.Equal(3, report.Moved);
        Assert.Empty(report.Skipped);
        Assert.Equal(before, CountFiles(Paths.Root)); // nothing deleted
        var month = Path.Combine(Paths.IifImported, Month);
        Assert.True(File.Exists(Path.Combine(month, Path.GetFileName(errorFile))));
        Assert.Equal("earlier", File.ReadAllText(clash));
        Assert.True(File.Exists(Path.Combine(month, $"{stem}_fixed (2).iif")));
        Assert.True(File.Exists(Path.Combine(Paths.IifImported, "2026-03", "old_batch_5.iif"))); // no batch: file's own month
        Assert.Equal(new[] { unrelated, waiting.IifPath }.Order(), Directory.GetFiles(Paths.Iif).Order());

        var filed = Audit("IifFile", imported.Batch.Id).Where(a => a.Action == "Filed").ToList();
        Assert.Equal(2, filed.Count);
        Assert.Contains(filed, a => a.Detail!.Contains("QbErrorFile"));
        Assert.Contains(filed, a => a.Detail!.Contains("FixedFile"));
        Assert.Single(Audit("IifFile", 0), a => a.Action == "Filed");

        Assert.Equal(0, Export.FileIifFolder().Moved); // idempotent
    }

    [Fact]
    public void Startup_sends_side_files_of_a_voided_batch_to_voided()
    {
        var r = ExportOne();
        Export.VoidBatch(r.Batch.Id);
        var errorFile = Path.Combine(Paths.Iif, Path.GetFileNameWithoutExtension(r.IifPath) + "_42.iif");
        File.WriteAllText(errorFile, "x");

        Assert.Equal(1, Export.FileIifFolder().Moved);
        Assert.True(File.Exists(Path.Combine(Paths.IifVoided, Month, Path.GetFileName(errorFile))));
    }

    [Fact]
    public void Startup_files_legacy_voided_files_left_in_the_root()
    {
        var r = ExportOne();
        using (var s = Db.Open()) // a batch voided before v4: file renamed in place, path unchanged
        {
            s.Connection.Execute("UPDATE ExportBatch SET Status = 'Voided', VoidedUtc = '2026-11-20T15:00:00.000Z' WHERE Id = @id", new { id = r.Batch.Id });
        }
        File.Move(r.IifPath, r.IifPath + ".voided");

        Assert.Equal(1, Export.FileIifFolder().Moved);

        var dest = Path.Combine(Paths.IifVoided, Month, Path.GetFileName(r.IifPath) + ".voided");
        Assert.True(File.Exists(dest));
        Assert.Equal(dest, Export.Batches().Single().IifPath);
        Assert.Contains(Audit("ExportBatch", r.Batch.Id), a => a.Action == "FileFiled");
        Assert.Empty(Directory.GetFiles(Paths.Iif));
    }

    [Fact]
    public void Startup_skips_files_in_use_and_files_them_next_time()
    {
        var r = ExportOne();
        var errorFile = Path.Combine(Paths.Iif, Path.GetFileNameWithoutExtension(r.IifPath) + "_42.iif");
        File.WriteAllText(errorFile, "x");

        using (new FileStream(errorFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var first = Export.FileIifFolder();
            Assert.Equal(0, first.Moved);
            Assert.Single(first.Skipped);
        }
        Assert.True(File.Exists(errorFile));
        Assert.DoesNotContain(Audit("IifFile", r.Batch.Id), a => a.Action == "Filed"); // rolled back with the failed move

        Assert.Equal(1, Export.FileIifFolder().Moved);
    }

    [Fact]
    public void Batch_status_triggers_allow_only_forward_moves()
    {
        var r = ExportOne();
        var id = r.Batch.Id;
        using var s = Db.Open();
        var c = s.Connection;

        Assert.Throws<SqliteException>(() => c.Execute("UPDATE ExportBatch SET Status = 'ImportedToQb' WHERE Id = @id", new { id })); // needs ImportedUtc
        Assert.Throws<SqliteException>(() => c.Execute("UPDATE ExportBatch SET Status = 'Voided' WHERE Id = @id", new { id }));    // needs VoidedUtc
        Assert.Throws<SqliteException>(() => c.Execute("UPDATE ExportBatch SET Status = 'Bogus' WHERE Id = @id", new { id }));
        Assert.Throws<SqliteException>(() => c.Execute("UPDATE ExportBatch SET TotalCents = 1 WHERE Id = @id", new { id }));
        Assert.Throws<SqliteException>(() => c.Execute(
            "INSERT INTO ExportBatch (CreatedUtc, IifPath, TxnCount, TotalCents, Status, ImportedUtc) VALUES ('2026-01-01', 'x', 0, 0, 'ImportedToQb', '2026-01-01')"));

        c.Execute("UPDATE ExportBatch SET Status = 'ImportedToQb', ImportedUtc = '2026-11-20T00:00:00.000Z' WHERE Id = @id", new { id });
        Assert.Throws<SqliteException>(() => c.Execute("UPDATE ExportBatch SET Status = 'Exported' WHERE Id = @id", new { id }));
        Assert.Throws<SqliteException>(() => c.Execute("UPDATE ExportBatch SET ImportedUtc = '2027-01-01T00:00:00.000Z' WHERE Id = @id", new { id }));

        c.Execute("UPDATE ExportBatch SET Status = 'Voided', VoidedUtc = '2026-11-21T00:00:00.000Z' WHERE Id = @id", new { id });
        Assert.Throws<SqliteException>(() => c.Execute("UPDATE ExportBatch SET Status = 'ImportedToQb' WHERE Id = @id", new { id }));
        Assert.Throws<SqliteException>(() => c.Execute("UPDATE ExportBatch SET VoidedUtc = NULL WHERE Id = @id", new { id }));
        Assert.Throws<SqliteException>(() => c.Execute("UPDATE ExportBatch SET BackupPath = 'x' WHERE Id = @id", new { id }));
        c.Execute("UPDATE ExportBatch SET IifPath = 'moved.iif.voided' WHERE Id = @id", new { id }); // filing a voided batch is allowed
        Assert.Throws<SqliteException>(() => c.Execute("DELETE FROM ExportBatch"));
    }
}

public class Schema4UpgradeTests : DataTestBase
{
    [Fact]
    public void V3_batches_upgrade_with_status_from_their_void_date()
    {
        var path = Path.Combine(Paths.Root, "v3.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            foreach (var script in new[] { "Schema_001", "Schema_002", "Schema_003" })
            {
                using var stream = typeof(Database).Assembly.GetManifestResourceStream($"QbImportMgr.Data.Db.{script}.sql")!;
                c.Execute(new StreamReader(stream).ReadToEnd());
            }
            c.Execute("PRAGMA user_version = 3;");
            c.Execute("""
                INSERT INTO ExportBatch (Id, CreatedUtc, IifPath, TxnCount, TotalCents) VALUES (1, '2026-10-01T00:00:00.000Z', 'a.iif', 1, -100);
                INSERT INTO ExportBatch (Id, CreatedUtc, IifPath, TxnCount, TotalCents, VoidedUtc) VALUES (2, '2026-10-02T00:00:00.000Z', 'b.iif', 2, -200, '2026-10-03T00:00:00.000Z');
                """);
        }

        var db = new Database(path);
        Assert.True(db.NeedsUpgrade());
        Assert.Equal(4, db.Migrate());

        using var s = db.Open();
        var batches = s.Batches.GetAll().OrderBy(b => b.Id).ToList();
        Assert.Equal((BatchStatus.Exported, (DateTime?)null), (batches[0].Status, batches[0].ImportedUtc));
        Assert.Equal(BatchStatus.Voided, batches[1].Status);
        Assert.Equal((2, -200L), (batches[1].TxnCount, batches[1].TotalCents));
        Assert.Throws<SqliteException>(() => s.Connection.Execute("UPDATE ExportBatch SET VoidedUtc = NULL WHERE Id = 2"));
        Assert.Equal("ok", s.Connection.ExecuteScalar<string>("PRAGMA integrity_check;"));
    }
}
