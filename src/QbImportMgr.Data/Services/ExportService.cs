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
                    x.Class, x.AmountCents, x.Memo)).ToList());
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
    /// those transactions there first. The IIF file is renamed *.voided so it can't be imported by mistake.
    /// </summary>
    public int VoidBatch(long batchId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        using var s = db.Open();
        var (count, path) = s.InTransaction(() =>
        {
            var batch = s.Batches.Get(batchId) ?? throw new KeyNotFoundException($"Batch {batchId} not found.");
            if (!s.Batches.SetVoided(batchId, now)) throw new TxnStateException($"Batch {batchId} is already voided.");
            var txnIds = s.Txns.ListByBatch(batchId).Select(t => t.Id).ToList();
            int n = s.Txns.ReturnBatchToReady(batchId, now);
            s.Audit.Write("ExportBatch", batchId, "Voided", new { Returned = n, batch.IifPath }, now);
            foreach (var id in txnIds)
                s.Audit.Write("Txn", id, "BatchVoided", new { batchId }, now);
            return (n, batch.IifPath);
        });

        if (File.Exists(path))
        {
            var dest = path + ".voided";
            if (!File.Exists(dest)) File.Move(path, dest);
        }
        return count;
    }

    public IReadOnlyList<ExportBatch> Batches()
    {
        using var s = db.Open();
        return s.Batches.GetAll();
    }
}
