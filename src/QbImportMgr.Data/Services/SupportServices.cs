using Microsoft.Data.Sqlite;
using QbImportMgr.Core;
using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Iif;
using QbImportMgr.Core.Import;
using QbImportMgr.Data.Db;

namespace QbImportMgr.Data.Services;

public sealed class BackupService(Database db, AppPaths paths, TimeProvider clock, int keep = 30)
{
    public const string Prefix = "qb-import-mgr_";

    /// <summary>Online SQLite backup to backup\qb-import-mgr_yyyyMMdd_HHmmss_fff.db, then prune to the newest <c>keep</c>.</summary>
    public string Backup()
    {
        Directory.CreateDirectory(paths.Backup);
        var stamp = clock.GetLocalNow().ToString("yyyyMMdd_HHmmss_fff");
        var dest = Path.Combine(paths.Backup, $"{Prefix}{stamp}.db");
        for (int i = 2; File.Exists(dest); i++)
            dest = Path.Combine(paths.Backup, $"{Prefix}{stamp}_{i}.db");

        using (var src = db.Open())
        using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dest, Pooling = false }.ToString()))
        {
            dst.Open();
            src.Connection.BackupDatabase(dst);
        }
        Prune();
        return dest;
    }

    public int Prune()
    {
        var old = Directory.EnumerateFiles(paths.Backup, Prefix + "*.db")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();
        foreach (var f in old) File.Delete(f);
        return old.Count;
    }
}

public sealed record ListImportResult(int AccountsAdded, int AccountsUpdated, int AccountsDeactivated,
    int NamesAdded, int NamesUpdated, int NamesDeactivated, int Classes, IReadOnlyList<string> Warnings);

public sealed class ListImportService(Database db, TimeProvider clock)
{
    /// <summary>
    /// Loads a QB "Lists to IIF" export. Accounts become Categories and names become QbNames, upserted
    /// by name. Hidden entries, and entries missing from the export, are marked inactive (never deleted,
    /// since transactions reference them).
    /// </summary>
    public ListImportResult Import(byte[] iifBytes)
    {
        var lists = IifListReader.Read(iifBytes);
        if (lists.Accounts.Count == 0 && lists.Names.Count == 0)
            throw new InvalidDataException("No !ACCNT or name lists found. Export from QuickBooks with File > Utilities > Export > Lists to IIF Files.");

        var now = clock.GetUtcNow().UtcDateTime;
        using var s = db.Open();
        return s.InTransaction(() =>
        {
            int aAdd = 0, aUpd = 0, nAdd = 0, nUpd = 0;
            foreach (var a in lists.Accounts)
                if (s.Categories.Upsert(a.FullName, a.AccntType, !a.Hidden, now)) aAdd++; else aUpd++;
            int aDeact = lists.Accounts.Count > 0 ? s.Categories.DeactivateAllExcept(lists.Accounts.Select(a => a.FullName).ToList()) : 0;

            foreach (var n in lists.Names)
                if (s.Names.Upsert(n.Name, n.Type, !n.Hidden)) nAdd++; else nUpd++;
            int nDeact = lists.Names.Count > 0 ? s.Names.DeactivateAllExcept(lists.Names.Select(n => (n.Name, n.Type)).ToList()) : 0;

            if (lists.Classes.Count > 0) s.Settings.SetClasses(lists.Classes);

            var result = new ListImportResult(aAdd, aUpd, aDeact, nAdd, nUpd, nDeact, lists.Classes.Count, lists.Warnings);
            s.Audit.Write("QbLists", 0, "Imported", result, now);
            return result;
        });
    }
}

public sealed class AccountService(Database db, TimeProvider clock)
{
    public IReadOnlyList<BankAccount> GetAll()
    {
        using var s = db.Open();
        return s.Accounts.GetAll();
    }

    /// <summary>
    /// Changing InvertSign re-normalizes SignedAmountCents on every row of the account. That is refused
    /// while the account has exported rows, since those amounts are already in QuickBooks.
    /// </summary>
    public BankAccount Update(long id, string displayName, string? qbAccountName, bool invertSign)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        using var s = db.Open();
        return s.InTransaction(() =>
        {
            var a = s.Accounts.Get(id) ?? throw new KeyNotFoundException($"Account {id} not found.");
            if (a.InvertSign != invertSign)
            {
                if (s.Txns.CountByStatus(id, TxnStatus.Exported) > 0)
                    throw new TxnStateException("This account has exported transactions; void those batches before changing the sign convention.");
                s.Txns.FlipSignForAccount(id, now);
            }
            var qb = string.IsNullOrWhiteSpace(qbAccountName) ? null : qbAccountName.Trim();
            s.Accounts.UpdateSettings(id, displayName.Trim(), qb, invertSign);
            s.Audit.Write("BankAccount", id, "Updated",
                new { From = new { a.DisplayName, a.QbAccountName, a.InvertSign }, To = new { displayName, QbAccountName = qb, invertSign } }, now);
            return s.Accounts.Get(id)!;
        });
    }
}

public sealed record LedgerStatus(QboStatement Statement, QboStatement? Previous, LedgerCheckResult? Result, string? Note);

public sealed class LedgerService(Database db)
{
    /// <summary>Checks a statement's ledger snapshot against the nearest earlier one for the same account.</summary>
    public LedgerStatus ForStatement(long statementId)
    {
        using var s = db.Open();
        var b = s.Files.GetStatement(statementId) ?? throw new KeyNotFoundException($"Statement {statementId} not found.");
        return Check(s, b);
    }

    /// <summary>Checks the account's latest ledger snapshot, or null if it has none.</summary>
    public LedgerStatus? ForAccount(long accountId)
    {
        using var s = db.Open();
        var latest = s.Files.StatementsForAccount(accountId)
            .Where(x => x.LedgerBalCents is not null && x.LedgerBalDate is not null)
            .OrderByDescending(x => x.LedgerBalDate).ThenByDescending(x => x.Id)
            .FirstOrDefault();
        return latest is null ? null : Check(s, latest);
    }

    private static LedgerStatus Check(DbSession s, QboStatement b)
    {
        if (b.LedgerBalCents is null || b.LedgerBalDate is null)
            return new(b, null, null, "No ledger balance in this download.");
        var prev = LedgerCheck.PreviousSnapshot(b, s.Files.StatementsForAccount(b.BankAccountId));
        if (prev is null)
            return new(b, null, null, "First ledger balance for this account; nothing to compare yet.");

        var account = s.Accounts.Get(b.BankAccountId)!;
        var txns = s.Txns.ListPostedBetween(b.BankAccountId, prev.LedgerBalDate!.Value, b.LedgerBalDate.Value);
        return new(b, prev, LedgerCheck.Compute(prev, b, account.InvertSign, txns), null);
    }
}
