using Dapper;
using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Qb;
using QbImportMgr.Data.Db;
using DbUtil = QbImportMgr.Data.Db.Db;

namespace QbImportMgr.Data.Repositories;

public sealed class QbRegisterRepository(DbSession s) : RepositoryBase(s)
{
    private const string Select = "SELECT * FROM QbRegisterTxn";
    private const string LinkedIds = "SELECT QbRegisterTxnId FROM Txn WHERE QbRegisterTxnId IS NOT NULL";

    public QbRegisterTxn? Get(long id) => Single<QbRegisterTxn>($"{Select} WHERE Id = @id", new { id });

    public IReadOnlyList<QbRegisterTxn> GetMany(IEnumerable<long> ids) =>
        Query<QbRegisterTxn>($"{Select} WHERE Id IN @ids", new { ids = ids.Distinct().ToArray() }).ToList();

    public int Count() => Scalar<int>("SELECT COUNT(*) FROM QbRegisterTxn");

    /// <summary>Insert, or refresh Clr/account/source on an existing row with the same key. Returns true when inserted.</summary>
    public bool Upsert(QbRegisterTxn r)
    {
        bool existed = Scalar<long>("SELECT COUNT(*) FROM QbRegisterTxn WHERE RowKey = @RowKey", new { r.RowKey }) > 0;
        Exec("""
            INSERT INTO QbRegisterTxn (BankAccountId, QbAccountName, TxnDate, TxnType, Num, Name, Memo, Clr, SplitAccount, AmountCents, SourceFile, ImportedUtc, RowKey)
            VALUES (@BankAccountId, @QbAccountName, @TxnDate, @TxnType, @Num, @Name, @Memo, @Clr, @SplitAccount, @AmountCents, @SourceFile, @ImportedUtc, @RowKey)
            ON CONFLICT (RowKey) DO UPDATE SET Clr = excluded.Clr, BankAccountId = COALESCE(excluded.BankAccountId, BankAccountId),
                SourceFile = excluded.SourceFile, ImportedUtc = excluded.ImportedUtc
            """, new
        {
            r.BankAccountId, r.QbAccountName, TxnDate = DbUtil.Date(r.TxnDate), r.TxnType, r.Num, r.Name, r.Memo, r.Clr,
            r.SplitAccount, r.AmountCents, r.SourceFile, ImportedUtc = DbUtil.Utc(r.ImportedUtc), r.RowKey,
        });
        return !existed;
    }

    /// <summary>Unlinked rows for an account, the ones the matcher may still use.</summary>
    public IReadOnlyList<QbRegisterTxn> Unlinked(long bankAccountId) => Query<QbRegisterTxn>(
        $"{Select} WHERE BankAccountId = @bankAccountId AND Id NOT IN ({LinkedIds}) ORDER BY TxnDate, Id", new { bankAccountId }).ToList();

    /// <summary>
    /// Re-import of an overlapping range: unlinked rows of this QB account dated inside the file's
    /// range but absent from the file were changed or deleted in QuickBooks, so they go.
    /// </summary>
    public int DeleteMissing(string qbAccountName, DateOnly from, DateOnly to, IReadOnlySet<string> keepKeys)
    {
        var stale = Query<(long Id, string RowKey)>($"""
            SELECT Id, RowKey FROM QbRegisterTxn
            WHERE QbAccountName = @qbAccountName COLLATE NOCASE AND TxnDate BETWEEN @from AND @to AND Id NOT IN ({LinkedIds})
            """, new { qbAccountName, from = DbUtil.Date(from), to = DbUtil.Date(to) })
            .Where(r => !keepKeys.Contains(r.RowKey)).Select(r => r.Id).ToList();
        foreach (var chunk in stale.Chunk(500))
            Exec("DELETE FROM QbRegisterTxn WHERE Id IN @ids", new { ids = chunk });
        return stale.Count;
    }

    /// <summary>Distinct QB account names on rows not yet tied to a bank account.</summary>
    public IReadOnlyList<string> UnboundAccountNames() =>
        Query<string>("SELECT DISTINCT QbAccountName FROM QbRegisterTxn WHERE BankAccountId IS NULL AND QbAccountName IS NOT NULL").ToList();

    public int Bind(string qbAccountName, long bankAccountId) => Exec(
        "UPDATE QbRegisterTxn SET BankAccountId = @bankAccountId WHERE BankAccountId IS NULL AND QbAccountName = @qbAccountName",
        new { qbAccountName, bankAccountId });

    public IReadOnlyList<LearnRow> LearningRows() =>
        Query<LearnRowDto>("SELECT Name, SplitAccount, TxnDate FROM QbRegisterTxn WHERE Name IS NOT NULL AND SplitAccount IS NOT NULL")
            .Select(r => new LearnRow(r.Name, r.SplitAccount, r.TxnDate)).ToList();

    /// <summary>Bank rows matched to QB rows: raw bank NAME → QB name and split account.</summary>
    public IReadOnlyList<MatchedPair> MatchedPairs() =>
        Query<(string BankText, string? QbName, string? Split)>("""
            SELECT t.Name, q.Name, q.SplitAccount FROM Txn t JOIN QbRegisterTxn q ON q.Id = t.QbRegisterTxnId
            WHERE t.Name IS NOT NULL AND t.Name <> ''
            """).Select(r => new MatchedPair(r.BankText, r.QbName, r.Split)).ToList();

    private sealed class LearnRowDto
    {
        public string? Name { get; set; }
        public string? SplitAccount { get; set; }
        public DateOnly TxnDate { get; set; }
    }
}

public sealed class RejectionRepository(DbSession s) : RepositoryBase(s)
{
    public IReadOnlySet<(long TxnId, long QbId)> QbRejections(long bankAccountId) =>
        Query<(long, long)>("""
            SELECT r.TxnId, r.QbRegisterTxnId FROM QbMatchRejection r JOIN Txn t ON t.Id = r.TxnId WHERE t.BankAccountId = @bankAccountId
            """, new { bankAccountId }).ToHashSet();

    public void RejectQb(long txnId, long qbRegisterTxnId, DateTime now) => Exec(
        "INSERT OR IGNORE INTO QbMatchRejection (TxnId, QbRegisterTxnId, CreatedUtc) VALUES (@txnId, @qbRegisterTxnId, @now)",
        new { txnId, qbRegisterTxnId, now = DbUtil.Utc(now) });

    public IReadOnlySet<(long, long)> TransferRejections() =>
        Query<(long, long)>("SELECT OutTxnId, InTxnId FROM TransferRejection").ToHashSet();

    public void RejectTransfer(long outTxnId, long inTxnId, DateTime now) => Exec(
        "INSERT OR IGNORE INTO TransferRejection (OutTxnId, InTxnId, CreatedUtc) VALUES (@outTxnId, @inTxnId, @now)",
        new { outTxnId, inTxnId, now = DbUtil.Utc(now) });
}

public sealed class SplitTemplateRepository(DbSession s) : RepositoryBase(s)
{
    public IReadOnlyList<SplitTemplate> GetAll()
    {
        var templates = Query<SplitTemplate>("SELECT Id, Name FROM SplitTemplate ORDER BY Name").ToList();
        var lines = Query<LineDto>("SELECT * FROM SplitTemplateLine ORDER BY TemplateId, Seq").ToLookup(l => l.TemplateId);
        foreach (var t in templates) t.Lines = lines[t.Id].Select(l => l.ToLine()).ToList();
        return templates;
    }

    public SplitTemplate? Get(long id) => GetAll().FirstOrDefault(t => t.Id == id);

    public long Insert(SplitTemplate t, DateTime now)
    {
        t.Id = Insert("INSERT INTO SplitTemplate (Name, CreatedUtc) VALUES (@Name, @now)", new { t.Name, now = DbUtil.Utc(now) });
        for (int i = 0; i < t.Lines.Count; i++)
        {
            var l = t.Lines[i];
            Exec("""
                INSERT INTO SplitTemplateLine (TemplateId, Seq, CategoryId, QbNameId, Class, Kind, Value, Memo)
                VALUES (@TemplateId, @Seq, @CategoryId, @QbNameId, @Class, @Kind, @Value, @Memo)
                """, new { TemplateId = t.Id, Seq = i + 1, l.CategoryId, l.QbNameId, l.Class, Kind = l.Kind.ToString(), l.Value, l.Memo });
        }
        return t.Id;
    }

    public void Delete(long id) => Exec("DELETE FROM SplitTemplate WHERE Id = @id", new { id });

    private sealed class LineDto
    {
        public long TemplateId { get; set; }
        public long CategoryId { get; set; }
        public long? QbNameId { get; set; }
        public string? Class { get; set; }
        public string Kind { get; set; } = "";
        public long Value { get; set; }
        public string? Memo { get; set; }
        public TemplateLine ToLine() => new(CategoryId, QbNameId, Class, Enum.Parse<TemplateLineKind>(Kind), Value, Memo);
    }
}
