using Dapper;
using Microsoft.Data.Sqlite;
using QbImportMgr.Core.Domain;
using QbImportMgr.Data.Db;

namespace QbImportMgr.Tests.Data;

public class SchemaTests : DataTestBase
{
    [Fact]
    public void Migrate_is_idempotent_and_seeds_settings()
    {
        Assert.Equal(4, Db.Migrate());
        Assert.Equal(4, Db.Migrate());
        using var s = Db.Open();
        Assert.True(s.Settings.GetIifClear());
        s.Settings.SetIifClear(false);
        Assert.False(s.Settings.GetIifClear());
        Assert.Equal(1, s.Connection.ExecuteScalar<int>("PRAGMA foreign_keys;"));
    }

    private long FirstTxnId()
    {
        ImportSample("bank_sgml_1x.qbo");
        return AllTxns().First().Id;
    }

    [Theory]
    [InlineData("Name = 'X'")]
    [InlineData("Memo = 'X'")]
    [InlineData("AmountCents = 1")]
    [InlineData("DtPosted = '2020-01-01'")]
    [InlineData("FitId = 'X'")]
    [InlineData("CheckNum = '9'")]
    [InlineData("TrnType = 'X'")]
    public void Raw_bank_fields_are_immutable(string assignment)
    {
        var id = FirstTxnId();
        using var s = Db.Open();
        var ex = Assert.Throws<SqliteException>(() => s.Connection.Execute($"UPDATE Txn SET {assignment} WHERE Id = @id", new { id }));
        Assert.Contains("immutable", ex.Message);
    }

    [Fact]
    public void Signed_amount_must_be_plus_or_minus_raw()
    {
        var id = FirstTxnId();
        using var s = Db.Open();
        Assert.Throws<SqliteException>(() => s.Connection.Execute("UPDATE Txn SET SignedAmountCents = 12345 WHERE Id = @id", new { id }));
    }

    [Fact]
    public void Fitid_unique_per_account_but_empty_and_null_allowed_repeatedly()
    {
        ImportSample("bank_sgml_1x.qbo");
        using var s = Db.Open();
        var t = s.Txns.ListByAccount(s.Accounts.GetAll()[0].Id).First(x => x.FitId is not null);

        t.Id = 0;
        Assert.Throws<SqliteException>(() => s.Txns.Insert(t));

        t.FitId = null;
        s.Txns.Insert(t);
        t.FitId = null;
        s.Txns.Insert(t);
        t.FitId = "";
        s.Txns.Insert(t);
        t.FitId = "";
        s.Txns.Insert(t);
    }

    [Fact]
    public void Exported_status_requires_batch()
    {
        var id = FirstTxnId();
        using var s = Db.Open();
        Assert.Throws<SqliteException>(() => s.Connection.Execute("UPDATE Txn SET Status = 'Exported' WHERE Id = @id", new { id }));
    }

    private (long TxnId, long BatchId) ExportOne()
    {
        ImportSample("bank_sgml_1x.qbo");
        SetUpQuickBooks();
        var t = AllTxns().First();
        Txns.SetCategory(t.Id, CategoryId("Utilities:Telephone"));
        Txns.MarkReady([t.Id]);
        var r = Export.Export([t.Id]);
        return (t.Id, r.Batch.Id);
    }

    [Theory]
    [InlineData("CategoryId = NULL")]
    [InlineData("OvrPayee = 'X'")]
    [InlineData("Status = 'Ready', BatchId = NULL")]           // not voided
    [InlineData("Status = 'New', BatchId = NULL")]
    public void Exported_rows_are_locked(string assignment)
    {
        var (id, _) = ExportOne();
        using var s = Db.Open();
        var ex = Assert.Throws<SqliteException>(() => s.Connection.Execute($"UPDATE Txn SET {assignment} WHERE Id = @id", new { id }));
        Assert.Contains("locked", ex.Message);
    }

    [Fact]
    public void Exported_rows_cannot_be_deleted_or_have_splits_changed()
    {
        var (id, _) = ExportOne();
        using var s = Db.Open();
        Assert.Throws<SqliteException>(() => s.Connection.Execute("DELETE FROM Txn WHERE Id = @id", new { id }));
        Assert.Throws<SqliteException>(() => s.Splits.Replace(id, [new TxnSplit { CategoryId = CategoryId("Travel"), AmountCents = 1 }]));
    }

    [Fact]
    public void Void_exception_allows_only_exported_to_ready_with_null_batch_after_batch_voided()
    {
        var (id, batchId) = ExportOne();
        using var s = Db.Open();
        s.Connection.Execute("UPDATE ExportBatch SET Status = 'Voided', VoidedUtc = '2026-11-20T00:00:00.000Z' WHERE Id = @batchId", new { batchId });

        // Even after the void, other changes in the same update are refused.
        Assert.Throws<SqliteException>(() => s.Connection.Execute("UPDATE Txn SET Status = 'Ready', BatchId = NULL, CategoryId = NULL WHERE Id = @id", new { id }));
        Assert.Throws<SqliteException>(() => s.Connection.Execute("UPDATE Txn SET Status = 'New', BatchId = NULL WHERE Id = @id", new { id }));

        s.Connection.Execute("UPDATE Txn SET Status = 'Ready', BatchId = NULL, UpdatedUtc = '2026-11-20T00:00:01.000Z' WHERE Id = @id", new { id });
        Assert.Equal(TxnStatus.Ready, s.Txns.Get(id)!.Status);
    }

    [Fact]
    public void Voided_batches_are_permanent_and_batches_and_audit_are_append_only()
    {
        var (_, batchId) = ExportOne();
        Export.VoidBatch(batchId);
        using var s = Db.Open();
        Assert.Throws<SqliteException>(() => s.Connection.Execute("UPDATE ExportBatch SET VoidedUtc = NULL WHERE Id = @batchId", new { batchId }));
        Assert.Throws<SqliteException>(() => s.Connection.Execute("DELETE FROM ExportBatch"));
        Assert.Throws<SqliteException>(() => s.Connection.Execute("UPDATE AuditLog SET Action = 'x'"));
        Assert.Throws<SqliteException>(() => s.Connection.Execute("DELETE FROM AuditLog"));
    }

    [Fact]
    public void Dates_and_enums_round_trip()
    {
        ImportSample("bank_sgml_1x.qbo");
        using var s = Db.Open();
        var acct = s.Accounts.GetAll().Single();
        Assert.Equal(AccountKind.Bank, acct.Kind);
        Assert.Equal("****4321", acct.OfxAcctIdMasked);
        Assert.Equal("Checking ****4321", acct.DisplayName);
        Assert.Equal(DateTimeKind.Utc, acct.CreatedUtc.Kind);
        Assert.Equal(Clock.Now.UtcDateTime, acct.CreatedUtc);

        var check = s.Txns.ListByAccount(acct.Id).Single(t => t.CheckNum == "1042");
        Assert.Equal(new DateOnly(2026, 10, 5), check.DtPosted);
        Assert.Equal(new DateOnly(2026, 10, 3), check.DtUser);
        Assert.Equal(TxnStatus.New, check.Status);

        var stmt = s.Files.StatementsForAccount(acct.Id).Single();
        Assert.Equal(new DateOnly(2026, 10, 31), stmt.LedgerBalDate);
        Assert.Equal(317827, stmt.LedgerBalCents);
    }

    [Fact]
    public void Status_counts_group_by_account_and_statement()
    {
        ImportSample("bank_sgml_1x.qbo");
        ImportSample("amex_ccstmt.qfx");
        using var s = Db.Open();
        var amex = s.Accounts.GetAll().Single(a => a.Kind == AccountKind.CreditCard);
        var stmt = s.Files.StatementsForAccount(amex.Id).Single();

        Assert.Equal(11, s.Txns.StatusCounts()[TxnStatus.New]);
        Assert.Equal(6, s.Txns.StatusCounts(amex.Id)[TxnStatus.New]);
        Assert.Equal(6, s.Txns.StatusCounts(statementId: stmt.Id)[TxnStatus.New]);
        Assert.Equal("amex_ccstmt.qfx", s.Files.StatementFileNames(amex.Id)[stmt.Id]);
    }

    [Fact]
    public void Full_account_number_is_never_stored()
    {
        ImportSample("bank_sgml_1x.qbo");
        using var s = Db.Open();
        var hits = s.Connection.ExecuteScalar<long>("""
            SELECT (SELECT COUNT(*) FROM BankAccount WHERE OfxAcctIdMasked LIKE '%987654321%' OR DisplayName LIKE '%987654321%' OR AcctIdHash LIKE '%987654321%')
                 + (SELECT COUNT(*) FROM AuditLog WHERE Detail LIKE '%987654321%')
            """);
        Assert.Equal(0, hits);
    }
}
