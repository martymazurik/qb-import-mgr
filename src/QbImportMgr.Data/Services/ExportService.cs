using QbImportMgr.Core;
using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Export;
using QbImportMgr.Core.Iif;
using QbImportMgr.Data.Db;

namespace QbImportMgr.Data.Services;

public sealed record AccountExportSummary(string AccountDisplay, string? QbAccount, int Count, long NetCents);

public sealed record ExportPreview(IReadOnlyList<ExportItem> Items, ValidationResult Validation, bool IsFirstExport)
{
    public long TotalCents => Items.Sum(i => i.SignedAmountCents);

    public IReadOnlyList<AccountExportSummary> ByAccount => Items
        .GroupBy(i => (i.AccountDisplay, i.BankQbAccount))
        .Select(g => new AccountExportSummary(g.Key.AccountDisplay, g.Key.BankQbAccount, g.Count(), g.Sum(i => i.SignedAmountCents)))
        .OrderBy(a => a.AccountDisplay)
        .ToList();
}

public sealed record ExportResult(ExportBatch Batch, string IifPath, string BackupPath);

public sealed class ExportValidationException(ValidationResult result)
    : InvalidOperationException("Export blocked: " + string.Join("; ", result.Errors.Select(e => e.Message).Take(5)))
{
    public ValidationResult Result { get; } = result;
}

public sealed class ExportService(Database db, AppPaths paths, BackupService backup, TimeProvider clock)
{
    /// <summary>Every Ready transaction, optionally for one account.</summary>
    public ExportPreview PreviewReady(long? accountId = null)
    {
        using var s = db.Open();
        return Preview(s, s.Txns.ListByStatus(TxnStatus.Ready, accountId));
    }

    public ExportPreview Preview(IEnumerable<long> txnIds)
    {
        using var s = db.Open();
        return Preview(s, s.Txns.GetMany(txnIds));
    }

    private static ExportPreview Preview(DbSession s, IReadOnlyList<Txn> txns)
    {
        var accounts = s.Accounts.GetAll().ToDictionary(a => a.Id);
        var categories = s.Categories.GetAll();
        var catById = categories.ToDictionary(c => c.Id);
        var names = s.Names.GetAll();
        var nameById = names.ToDictionary(n => n.Id);
        var splits = s.Splits.ForTxns(txns.Select(t => t.Id));

        var items = txns.Select(t =>
        {
            var a = accounts[t.BankAccountId];
            return new ExportItem(
                t.Id, t.Status, a.Kind, $"{a.DisplayName}", a.QbAccountName, t.EffectiveDate, t.SignedAmountCents,
                t.EffectivePayee, t.EffectiveMemo, t.EffectiveCheckNum,
                t.CategoryId, t.CategoryId is { } c && catById.TryGetValue(c, out var cat) ? cat.FullName : null,
                t.QbNameId, t.QbNameId is { } n && nameById.TryGetValue(n, out var nm) ? nm.Name : null,
                t.Class,
                splits[t.Id].Select(x => new ExportSplit(
                    x.CategoryId, catById.TryGetValue(x.CategoryId, out var sc) ? sc.FullName : null,
                    x.QbNameId, x.QbNameId is { } sn && nameById.TryGetValue(sn, out var snm) ? snm.Name : null,
                    x.Class, x.AmountCents, x.Memo,
                    x.QbNameId is { } st && nameById.TryGetValue(st, out var stn) ? stn.NameType : null)).ToList());
        }).ToList();

        var qbAccounts = categories
            .GroupBy(c => c.FullName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Any(c => c.IsActive), StringComparer.OrdinalIgnoreCase);
        var qbNames = names.Where(n => n.IsActive).Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool firstExport = s.Batches.GetAll().Count == 0;
        return new ExportPreview(items, ExportValidator.Validate(items, qbAccounts, qbNames), firstExport);
    }

    /// <summary>
    /// Validates, backs up the DB, then in one transaction creates the batch, writes the IIF file
    /// and marks the rows Exported. If anything fails the transaction rolls back and the file is removed.
    /// </summary>
    public ExportResult Export(IEnumerable<long> txnIds)
    {
        var ids = txnIds.Distinct().ToList();
        var preview = Preview(ids);
        if (!preview.Validation.CanExport) throw new ExportValidationException(preview.Validation);

        var backupPath = backup.Backup();
        Directory.CreateDirectory(paths.Iif);
        var now = clock.GetUtcNow().UtcDateTime;
        var stamp = clock.GetLocalNow().ToString("yyyyMMdd_HHmmss");

        using var s = db.Open();
        var options = new ExportOptions(Clear: s.Settings.GetIifClear());
        string? written = null, tmpFile = null;
        try
        {
            return s.InTransaction(() =>
            {
                var batch = new ExportBatch
                {
                    CreatedUtc = now, IifPath = "", BackupPath = backupPath,
                    TxnCount = preview.Items.Count, TotalCents = preview.TotalCents,
                };
                s.Batches.Insert(batch);

                var path = Path.Combine(paths.Iif, $"qb-import_{stamp}_batch{batch.Id}.iif");
                batch.IifPath = path;
                s.Batches.SetIifPath(batch.Id, path);

                int marked = s.Txns.MarkExported(ids, batch.Id, now);
                if (marked != ids.Count)
                    throw new InvalidOperationException($"Only {marked} of {ids.Count} transactions were still Ready; export cancelled.");

                var iif = preview.Items.OrderBy(i => i.Date).ThenBy(i => i.TxnId).Select(i => IifMapper.ToIif(i, options)).ToList();
                tmpFile = path + ".tmp";
                using (var fs = File.Create(tmpFile)) IifWriter.Write(fs, iif);
                File.Move(tmpFile, path, overwrite: false);
                written = path;

                s.Audit.Write("ExportBatch", batch.Id, "Exported", new { path, batch.TxnCount, batch.TotalCents, backupPath, TxnIds = ids }, now);
                return new ExportResult(batch, path, backupPath);
            });
        }
        catch
        {
            if (written is not null && File.Exists(written)) File.Delete(written);
            if (tmpFile is not null && File.Exists(tmpFile)) File.Delete(tmpFile);
            throw;
        }
    }

    /// <summary>
    /// Void: marks the batch voided and returns its rows to Ready. It does not touch QuickBooks; delete
    /// those transactions there first. The IIF file moves to iif\voided\YYYY-MM\ and is renamed *.voided
    /// so it can't be imported by mistake. Files are never deleted.
    /// </summary>
    public int VoidBatch(long batchId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        using var s = db.Open();
        return s.InTransaction(() =>
        {
            var batch = s.Batches.Get(batchId) ?? throw new KeyNotFoundException($"Batch {batchId} not found.");
            if (batch.Status == BatchStatus.Voided) throw new TxnStateException($"Batch {batchId} is already voided.");
            var move = PlanMove(batch, BatchStatus.Voided);
            if (!s.Batches.SetVoided(batchId, now, move?.To ?? batch.IifPath)) throw new TxnStateException($"Batch {batchId} is already voided.");
            var txnIds = s.Txns.ListByBatch(batchId).Select(t => t.Id).ToList();
            int n = s.Txns.ReturnBatchToReady(batchId, now);
            s.Audit.Write("ExportBatch", batchId, "Voided",
                new { Returned = n, From = move?.From ?? batch.IifPath, To = move?.To, FileMissing = move is null }, now);
            foreach (var id in txnIds)
                s.Audit.Write("Txn", id, "BatchVoided", new { batchId }, now);
            if (move is not null && move.From != move.To) File.Move(move.From, move.To); // last: a failed move rolls the void back
            return n;
        });
    }

    /// <summary>
    /// The batch's IIF went into QuickBooks: status ImportedToQb and the file moves to
    /// iif\imported\YYYY-MM\ (the month the batch was exported), leaving the iif folder to exports still waiting.
    /// </summary>
    public ExportBatch MarkImported(long batchId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        using var s = db.Open();
        return s.InTransaction(() =>
        {
            var batch = s.Batches.Get(batchId) ?? throw new KeyNotFoundException($"Batch {batchId} not found.");
            if (batch.Status != BatchStatus.Exported)
                throw new TxnStateException(batch.Status == BatchStatus.Voided
                    ? $"Batch {batchId} is voided."
                    : $"Batch {batchId} is already marked imported to QuickBooks.");
            var move = PlanMove(batch, BatchStatus.ImportedToQb);
            if (!s.Batches.SetImported(batchId, now, move?.To ?? batch.IifPath))
                throw new TxnStateException($"Batch {batchId} is not waiting for import.");
            s.Audit.Write("ExportBatch", batchId, "ImportedToQb",
                new { From = move?.From ?? batch.IifPath, To = move?.To, FileMissing = move is null }, now);
            if (move is not null && move.From != move.To) File.Move(move.From, move.To);
            return s.Batches.Get(batchId)!;
        });
    }

    /// <summary>
    /// Startup housekeeping for the iif folder. Files of imported and voided batches go to their month
    /// folders (including old *.iif.voided files left in the root), and QuickBooks error files
    /// (*_batch*_&lt;digits&gt;.iif) and *_fixed.iif files go to the month folder of their batch. Each move is
    /// audited. Nothing is ever deleted or overwritten; a file that is in use is skipped and tried next time.
    /// </summary>
    public IifFilingReport FileIifFolder()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        int moved = 0;
        var skipped = new List<string>();
        using var s = db.Open();
        var batches = s.Batches.GetAll();

        foreach (var b in batches.Where(b => b.Status != BatchStatus.Exported))
        {
            var move = PlanMove(b, b.Status);
            if (move is null || SameFolder(move.From, move.To)) continue;
            try
            {
                s.InTransaction(() =>
                {
                    s.Batches.SetIifPath(b.Id, move.To);
                    s.Audit.Write("ExportBatch", b.Id, "FileFiled", new { move.From, move.To, Status = b.Status.ToString() }, now);
                    File.Move(move.From, move.To);
                });
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{Path.GetFileName(move.From)}: {ex.Message}");
            }
        }

        if (!Directory.Exists(paths.Iif)) return new IifFilingReport(moved, skipped);
        var byId = batches.ToDictionary(b => b.Id);
        var batchFiles = batches.Select(b => b.IifPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(paths.Iif, "*.iif"))
        {
            var name = Path.GetFileName(file);
            if (batchFiles.Contains(file) || !IifFiling.TryClassifySideFile(name, out var kind, out var batchId)) continue;
            var batch = batchId is { } id && byId.TryGetValue(id, out var bb) ? bb : null;
            var month = IifFiling.MonthFolder(batch?.CreatedUtc ?? File.GetLastWriteTimeUtc(file), clock.LocalTimeZone);
            var folder = Path.Combine(batch?.Status == BatchStatus.Voided ? paths.IifVoided : paths.IifImported, month);
            try
            {
                Directory.CreateDirectory(folder);
                var dest = IifFiling.FreeName(folder, name, File.Exists);
                s.InTransaction(() =>
                {
                    s.Audit.Write("IifFile", batch?.Id ?? 0, "Filed", new { From = file, To = dest, Kind = kind.ToString(), BatchId = batch?.Id }, now);
                    File.Move(file, dest);
                });
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{name}: {ex.Message}");
            }
        }
        return new IifFilingReport(moved, skipped);
    }

    public IReadOnlyList<ExportBatch> Batches()
    {
        using var s = db.Open();
        return s.Batches.GetAll();
    }

    private sealed record FileMove(string From, string To);

    /// <summary>Where the batch's file should live for the given status; null when the file can't be found.</summary>
    private FileMove? PlanMove(ExportBatch batch, BatchStatus status)
    {
        // Before v4, voided files were renamed in place to *.iif.voided.
        var from = new[] { batch.IifPath, batch.IifPath + IifFiling.VoidedSuffix }.FirstOrDefault(File.Exists);
        if (from is null) return null;

        var name = Path.GetFileName(from);
        string root;
        if (status == BatchStatus.Voided)
        {
            root = paths.IifVoided;
            if (!name.EndsWith(IifFiling.VoidedSuffix, StringComparison.OrdinalIgnoreCase)) name += IifFiling.VoidedSuffix;
        }
        else if (status == BatchStatus.ImportedToQb) root = paths.IifImported;
        else return new FileMove(from, from);

        var folder = Path.Combine(root, IifFiling.MonthFolder(batch.CreatedUtc, clock.LocalTimeZone));
        var current = Path.Combine(Path.GetDirectoryName(from)!, name);
        if (SameFolder(from, Path.Combine(folder, name)) && string.Equals(from, current, StringComparison.OrdinalIgnoreCase))
            return new FileMove(from, from);
        Directory.CreateDirectory(folder);
        return new FileMove(from, IifFiling.FreeName(folder, name, File.Exists));
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(Path.GetDirectoryName(a)!), Path.GetFullPath(Path.GetDirectoryName(b)!), StringComparison.OrdinalIgnoreCase);
}

public sealed record IifFilingReport(int Moved, IReadOnlyList<string> Skipped);
