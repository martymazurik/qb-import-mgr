using System.Text;
using QbImportMgr.Core;
using QbImportMgr.Core.Domain;
using QbImportMgr.Data.Db;
using QbImportMgr.Data.Services;

namespace QbImportMgr.Tests.Data;

public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Fresh temp root (inbox/archive/iif/backup + DB) per test.</summary>
public abstract class DataTestBase : IDisposable
{
    protected DataTestBase()
    {
        Paths = new AppPaths(Path.Combine(Path.GetTempPath(), "qbim-tests", Guid.NewGuid().ToString("N")));
        Paths.EnsureCreated();
        Db = new Database(Paths.Database);
        Db.Migrate();
        Clock = new TestClock(new DateTimeOffset(2026, 11, 20, 15, 0, 0, TimeSpan.Zero));
        Import = new ImportService(Db, Paths, Clock);
        Txns = new TxnService(Db, Clock);
        Backup = new BackupService(Db, Paths, Clock);
        Export = new ExportService(Db, Paths, Backup, Clock);
        Lists = new ListImportService(Db, Clock);
        Accounts = new AccountService(Db, Clock);
        Ledger = new LedgerService(Db);
    }

    protected AppPaths Paths { get; }
    protected Database Db { get; }
    protected TestClock Clock { get; }
    protected ImportService Import { get; }
    protected TxnService Txns { get; }
    protected BackupService Backup { get; }
    protected ExportService Export { get; }
    protected ListImportService Lists { get; }
    protected AccountService Accounts { get; }
    protected LedgerService Ledger { get; }

    protected string Drop(string fileName, byte[] bytes)
    {
        var path = Path.Combine(Paths.Inbox, fileName);
        File.WriteAllBytes(path, bytes);
        Clock.Advance(TimeSpan.FromSeconds(1)); // distinct archive stamps
        return path;
    }

    protected FileImportResult ImportSample(string sample, string? asName = null) =>
        Import.ImportFile(Drop(asName ?? sample, TestData.Sample(sample)));

    protected FileImportResult ImportText(string fileName, string ofx) => Import.ImportFile(Drop(fileName, Encoding.ASCII.GetBytes(ofx)));

    protected IReadOnlyList<Txn> AllTxns()
    {
        using var s = Db.Open();
        return s.Accounts.GetAll().SelectMany(a => s.Txns.ListByAccount(a.Id)).ToList();
    }

    protected Txn Get(long id)
    {
        using var s = Db.Open();
        return s.Txns.Get(id)!;
    }

    protected long CategoryId(string name)
    {
        using var s = Db.Open();
        return s.Categories.GetByName(name)!.Id;
    }

    /// <summary>Imports QB lists and maps every account to a QB account by kind.</summary>
    protected void SetUpQuickBooks()
    {
        Lists.Import(TestData.Sample("qb_lists.iif"));
        foreach (var a in Accounts.GetAll())
            Accounts.Update(a.Id, a.DisplayName, a.Kind == AccountKind.Bank ? "Checking" : "American Express", a.InvertSign);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(Paths.Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- synthetic OFX builders

    public static string Trn(string date, string amount, string? fitId, string name, string type = "DEBIT", string? memo = null) =>
        $"<STMTTRN><TRNTYPE>{type}<DTPOSTED>{date}<TRNAMT>{amount}<FITID>{fitId}<NAME>{name}" +
        (memo is null ? "" : $"<MEMO>{memo}") + "</STMTTRN>";

    public static string BankOfx(string acctId, string ledgerBal, string ledgerDate, params string[] trns) =>
        TestData.SgmlHeader +
        "<OFX><BANKMSGSRSV1><STMTTRNRS><TRNUID>1<STATUS><CODE>0<SEVERITY>INFO</STATUS><STMTRS><CURDEF>USD" +
        $"<BANKACCTFROM><BANKID>123456789<ACCTID>{acctId}<ACCTTYPE>CHECKING</BANKACCTFROM>" +
        "<BANKTRANLIST><DTSTART>20261001<DTEND>20261130" + string.Concat(trns) + "</BANKTRANLIST>" +
        $"<LEDGERBAL><BALAMT>{ledgerBal}<DTASOF>{ledgerDate}</LEDGERBAL></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
}
