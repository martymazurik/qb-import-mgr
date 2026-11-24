using System.Text.Json;
using Dapper;
using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Import;
using QbImportMgr.Data.Db;
using DbUtil = QbImportMgr.Data.Db.Db;

namespace QbImportMgr.Data.Repositories;

public abstract class RepositoryBase(DbSession s)
{
    protected DbSession S { get; } = s;

    protected IEnumerable<T> Query<T>(string sql, object? p = null) => S.Connection.Query<T>(sql, p, S.Transaction);
    protected T? Single<T>(string sql, object? p = null) => S.Connection.QuerySingleOrDefault<T>(sql, p, S.Transaction);
    protected T Scalar<T>(string sql, object? p = null) => S.Connection.ExecuteScalar<T>(sql, p, S.Transaction)!;
    protected int Exec(string sql, object? p = null) => S.Connection.Execute(sql, p, S.Transaction);
    protected long Insert(string sql, object p) => S.Connection.ExecuteScalar<long>(sql + " RETURNING Id;", p, S.Transaction);
}

public sealed class BankAccountRepository(DbSession s) : RepositoryBase(s)
{
    private const string Select = """
        SELECT Id, OfxAcctIdMasked, AcctIdHash, BankId,
               CASE Kind WHEN 'BANK' THEN 'Bank' ELSE 'CreditCard' END AS Kind,
               DisplayName, QbAccountName, InvertSign, CreatedUtc, FiId, FiOrg
        FROM BankAccount
        """;

    public BankAccount? Get(long id) => Single<BankAccount>($"{Select} WHERE Id = @id", new { id });
    public BankAccount? GetByHash(string hash) => Single<BankAccount>($"{Select} WHERE AcctIdHash = @hash", new { hash });
    public IReadOnlyList<BankAccount> GetAll() => Query<BankAccount>($"{Select} ORDER BY Kind, DisplayName").ToList();

    public long Insert(BankAccount a) => a.Id = Insert("""
        INSERT INTO BankAccount (OfxAcctIdMasked, AcctIdHash, BankId, Kind, DisplayName, QbAccountName, InvertSign, CreatedUtc, FiId, FiOrg)
        VALUES (@OfxAcctIdMasked, @AcctIdHash, @BankId, @Kind, @DisplayName, @QbAccountName, @InvertSign, @CreatedUtc, @FiId, @FiOrg)
        """, new { a.OfxAcctIdMasked, a.AcctIdHash, a.BankId, Kind = a.Kind.ToCode(), a.DisplayName, a.QbAccountName,
                   InvertSign = a.InvertSign ? 1 : 0, CreatedUtc = DbUtil.Utc(a.CreatedUtc), a.FiId, a.FiOrg });

    /// <summary>Fills in institution info the first time a download carries it.</summary>
    public void FillInstitution(long id, string? fiId, string? fiOrg) => Exec(
        "UPDATE BankAccount SET FiId = COALESCE(FiId, @fiId), FiOrg = COALESCE(FiOrg, @fiOrg) WHERE Id = @id AND (FiId IS NULL OR FiOrg IS NULL)",
        new { id, fiId, fiOrg });

    public void UpdateSettings(long id, string displayName, string? qbAccountName, bool invertSign) => Exec(
        "UPDATE BankAccount SET DisplayName = @displayName, QbAccountName = @qbAccountName, InvertSign = @inv WHERE Id = @id",
        new { id, displayName, qbAccountName, inv = invertSign ? 1 : 0 });
}

public sealed class QboFileRepository(DbSession s) : RepositoryBase(s)
{
    public bool ExistsBySha(string sha256) => Scalar<long>("SELECT COUNT(*) FROM QboFile WHERE Sha256 = @sha256", new { sha256 }) > 0;

    public long Insert(QboFile f) => f.Id = Insert(
        "INSERT INTO QboFile (FileName, Sha256, RawBytes, ImportedUtc) VALUES (@FileName, @Sha256, @RawBytes, @ImportedUtc)",
        new { f.FileName, f.Sha256, f.RawBytes, ImportedUtc = DbUtil.Utc(f.ImportedUtc) });

    public QboFile? Get(long id) => Single<QboFile>("SELECT * FROM QboFile WHERE Id = @id", new { id });

    public long InsertStatement(QboStatement st) => st.Id = Insert("""
        INSERT INTO QboStatement (QboFileId, BankAccountId, DtStart, DtEnd, LedgerBalCents, LedgerBalDate, TxnCount)
        VALUES (@QboFileId, @BankAccountId, @DtStart, @DtEnd, @LedgerBalCents, @LedgerBalDate, @TxnCount)
        """, new { st.QboFileId, st.BankAccountId, DtStart = DbUtil.Date(st.DtStart), DtEnd = DbUtil.Date(st.DtEnd),
                   st.LedgerBalCents, LedgerBalDate = DbUtil.Date(st.LedgerBalDate), st.TxnCount });

    public QboStatement? GetStatement(long id) => Single<QboStatement>("SELECT * FROM QboStatement WHERE Id = @id", new { id });

    public IReadOnlyList<QboStatement> StatementsForAccount(long accountId) => Query<QboStatement>(
        "SELECT * FROM QboStatement WHERE BankAccountId = @accountId ORDER BY COALESCE(DtEnd, LedgerBalDate), Id",
        new { accountId }).ToList();

    /// <summary>File names for statements, for the account tree.</summary>
    public IReadOnlyDictionary<long, string> StatementFileNames(long accountId) => Query<(long Id, string FileName)>(
        "SELECT s.Id, f.FileName FROM QboStatement s JOIN QboFile f ON f.Id = s.QboFileId WHERE s.BankAccountId = @accountId",
        new { accountId }).ToDictionary(r => r.Id, r => r.FileName);
}

public sealed class TxnRepository(DbSession s) : RepositoryBase(s)
{
    private const string Select = "SELECT * FROM Txn";

    public Txn? Get(long id) => Single<Txn>($"{Select} WHERE Id = @id", new { id });

    public IReadOnlyList<Txn> GetMany(IEnumerable<long> ids) =>
        Query<Txn>($"{Select} WHERE Id IN @ids ORDER BY DtPosted, Id", new { ids = ids.Distinct().ToArray() }).ToList();

    public IReadOnlyList<Txn> ListByAccount(long accountId) =>
        Query<Txn>($"{Select} WHERE BankAccountId = @accountId ORDER BY DtPosted, Id", new { accountId }).ToList();

    public IReadOnlyList<Txn> ListByStatement(long statementId) =>
        Query<Txn>($"{Select} WHERE QboStatementId = @statementId ORDER BY DtPosted, Id", new { statementId }).ToList();

    public IReadOnlyList<Txn> ListByStatus(TxnStatus status, long? accountId = null) => Query<Txn>(
        $"{Select} WHERE Status = @st AND (@accountId IS NULL OR BankAccountId = @accountId) ORDER BY BankAccountId, DtPosted, Id",
        new { st = status.ToString(), accountId }).ToList();

    public IReadOnlyList<Txn> ListByBatch(long batchId) =>
        Query<Txn>($"{Select} WHERE BatchId = @batchId ORDER BY Id", new { batchId }).ToList();

    /// <summary>Posted in (after, through], for the ledger check.</summary>
    public IReadOnlyList<Txn> ListPostedBetween(long accountId, DateOnly afterExclusive, DateOnly throughInclusive) => Query<Txn>(
        $"{Select} WHERE BankAccountId = @accountId AND DtPosted > @a AND DtPosted <= @b ORDER BY DtPosted, Id",
        new { accountId, a = DbUtil.Date(afterExclusive), b = DbUtil.Date(throughInclusive) }).ToList();

    /// <summary>Rows that could duplicate an incoming batch: posted in the date window, or sharing a FITID.</summary>
    public List<DupCandidate> DupCandidates(long accountId, DateOnly from, DateOnly to, IReadOnlyCollection<string> fitIds) =>
        Query<DupRow>("""
            SELECT t.Id, t.BankAccountId, s.QboFileId, t.FitId, t.DtPosted, t.SignedAmountCents, t.Name, t.Status
            FROM Txn t JOIN QboStatement s ON s.Id = t.QboStatementId
            WHERE t.BankAccountId = @accountId AND ((t.DtPosted BETWEEN @from AND @to) OR t.FitId IN @fitIds)
            """, new { accountId, from = DbUtil.Date(from), to = DbUtil.Date(to), fitIds = fitIds.ToArray() })
        .Select(r => new DupCandidate(r.Id, r.BankAccountId, r.QboFileId, r.FitId, r.DtPosted, r.SignedAmountCents, r.Name, r.Status))
        .ToList();

    private sealed class DupRow
    {
        public long Id { get; set; }
        public long BankAccountId { get; set; }
        public long QboFileId { get; set; }
        public string? FitId { get; set; }
        public DateOnly DtPosted { get; set; }
        public long SignedAmountCents { get; set; }
        public string? Name { get; set; }
        public TxnStatus Status { get; set; }
    }

    public long Insert(Txn t) => t.Id = Insert("""
        INSERT INTO Txn (BankAccountId, QboStatementId, FitId, TrnType, DtPosted, DtUser, AmountCents, Name, Memo, CheckNum,
                         SignedAmountCents, OvrDate, OvrPayee, OvrMemo, OvrCheckNum, CategoryId, QbNameId, Class, Status,
                         DuplicateOfTxnId, RuleId, BatchId, UpdatedUtc, QbRegisterTxnId, PairedTxnId)
        VALUES (@BankAccountId, @QboStatementId, @FitId, @TrnType, @DtPosted, @DtUser, @AmountCents, @Name, @Memo, @CheckNum,
                @SignedAmountCents, @OvrDate, @OvrPayee, @OvrMemo, @OvrCheckNum, @CategoryId, @QbNameId, @Class, @Status,
                @DuplicateOfTxnId, @RuleId, NULL, @UpdatedUtc, @QbRegisterTxnId, @PairedTxnId)
        """, new
    {
        t.BankAccountId, t.QboStatementId, t.FitId, t.TrnType, DtPosted = DbUtil.Date(t.DtPosted), DtUser = DbUtil.Date(t.DtUser),
        t.AmountCents, t.Name, t.Memo, t.CheckNum, t.SignedAmountCents, OvrDate = DbUtil.Date(t.OvrDate), t.OvrPayee, t.OvrMemo,
        t.OvrCheckNum, t.CategoryId, t.QbNameId, t.Class, Status = t.Status.ToString(), t.DuplicateOfTxnId, t.RuleId,
        UpdatedUtc = DbUtil.Utc(t.UpdatedUtc), t.QbRegisterTxnId, t.PairedTxnId,
    });

    /// <summary>Writes every user-editable column. Raw columns and BatchId are never written here.</summary>
    public void UpdateEditable(Txn t) => Exec("""
        UPDATE Txn SET SignedAmountCents = @SignedAmountCents, OvrDate = @OvrDate, OvrPayee = @OvrPayee, OvrMemo = @OvrMemo,
               OvrCheckNum = @OvrCheckNum, CategoryId = @CategoryId, QbNameId = @QbNameId, Class = @Class, Status = @Status,
               DuplicateOfTxnId = @DuplicateOfTxnId, RuleId = @RuleId, UpdatedUtc = @UpdatedUtc,
               QbRegisterTxnId = @QbRegisterTxnId, PairedTxnId = @PairedTxnId
        WHERE Id = @Id
        """, new
    {
        t.Id, t.SignedAmountCents, OvrDate = DbUtil.Date(t.OvrDate), t.OvrPayee, t.OvrMemo, t.OvrCheckNum, t.CategoryId,
        t.QbNameId, t.Class, Status = t.Status.ToString(), t.DuplicateOfTxnId, t.RuleId, UpdatedUtc = DbUtil.Utc(t.UpdatedUtc),
        t.QbRegisterTxnId, t.PairedTxnId,
    });

    public Txn? GetByQbRegisterTxn(long qbRegisterTxnId) =>
        Single<Txn>($"{Select} WHERE QbRegisterTxnId = @qbRegisterTxnId", new { qbRegisterTxnId });

    /// <summary>Distinct raw bank payee names, for previewing what a learned pattern would hit.</summary>
    public IReadOnlyList<string> AllNames() =>
        Query<string>("SELECT DISTINCT Name FROM Txn WHERE Name IS NOT NULL AND Name <> ''").ToList();

    /// <summary>Rows that are one side of a transfer pair.</summary>
    public IReadOnlyList<Txn> ListPaired() =>
        Query<Txn>($"{Select} WHERE PairedTxnId IS NOT NULL ORDER BY DtPosted, Id").ToList();

    public IReadOnlyList<Txn> ListByAccounts(IEnumerable<long> accountIds) =>
        Query<Txn>($"{Select} WHERE BankAccountId IN @ids ORDER BY DtPosted, Id", new { ids = accountIds.Distinct().ToArray() }).ToList();

    public int MarkExported(IReadOnlyCollection<long> ids, long batchId, DateTime now) => Exec(
        "UPDATE Txn SET Status = 'Exported', BatchId = @batchId, UpdatedUtc = @now WHERE Id IN @ids AND Status = 'Ready'",
        new { ids = ids.ToArray(), batchId, now = DbUtil.Utc(now) });

    public int ReturnBatchToReady(long batchId, DateTime now) => Exec(
        "UPDATE Txn SET Status = 'Ready', BatchId = NULL, UpdatedUtc = @now WHERE BatchId = @batchId AND Status = 'Exported'",
        new { batchId, now = DbUtil.Utc(now) });

    public int FlipSignForAccount(long accountId, DateTime now) => Exec(
        "UPDATE Txn SET SignedAmountCents = -SignedAmountCents, UpdatedUtc = @now WHERE BankAccountId = @accountId",
        new { accountId, now = DbUtil.Utc(now) });

    public int CountByStatus(long accountId, TxnStatus status) => Scalar<int>(
        "SELECT COUNT(*) FROM Txn WHERE BankAccountId = @accountId AND Status = @st", new { accountId, st = status.ToString() });

    public IReadOnlyDictionary<TxnStatus, int> StatusCounts(long? accountId = null, long? statementId = null) => Query<(string Status, int N)>(
        """
        SELECT Status, COUNT(*) FROM Txn
        WHERE (@accountId IS NULL OR BankAccountId = @accountId) AND (@statementId IS NULL OR QboStatementId = @statementId)
        GROUP BY Status
        """, new { accountId, statementId })
        .ToDictionary(r => Enum.Parse<TxnStatus>(r.Status), r => r.N);
}

public sealed class TxnSplitRepository(DbSession s) : RepositoryBase(s)
{
    public IReadOnlyList<TxnSplit> ForTxn(long txnId) =>
        Query<TxnSplit>("SELECT * FROM TxnSplit WHERE TxnId = @txnId ORDER BY Seq", new { txnId }).ToList();

    public ILookup<long, TxnSplit> ForTxns(IEnumerable<long> txnIds) =>
        Query<TxnSplit>("SELECT * FROM TxnSplit WHERE TxnId IN @ids ORDER BY TxnId, Seq", new { ids = txnIds.Distinct().ToArray() })
        .ToLookup(x => x.TxnId);

    public IReadOnlySet<long> TxnIdsWithSplits(IEnumerable<long> txnIds) =>
        Query<long>("SELECT DISTINCT TxnId FROM TxnSplit WHERE TxnId IN @ids", new { ids = txnIds.Distinct().ToArray() }).ToHashSet();

    public void Replace(long txnId, IReadOnlyList<TxnSplit> splits)
    {
        Exec("DELETE FROM TxnSplit WHERE TxnId = @txnId", new { txnId });
        for (int i = 0; i < splits.Count; i++)
        {
            var x = splits[i];
            x.TxnId = txnId;
            x.Seq = i + 1;
            x.Id = Insert("""
                INSERT INTO TxnSplit (TxnId, Seq, CategoryId, QbNameId, Class, AmountCents, Memo)
                VALUES (@TxnId, @Seq, @CategoryId, @QbNameId, @Class, @AmountCents, @Memo)
                """, x);
        }
    }
}

public sealed class CategoryRepository(DbSession s) : RepositoryBase(s)
{
    public IReadOnlyList<Category> GetAll(bool activeOnly = false) => Query<Category>(
        "SELECT * FROM Category WHERE (@activeOnly = 0 OR IsActive = 1) ORDER BY FullName", new { activeOnly = activeOnly ? 1 : 0 }).ToList();

    public Category? Get(long id) => Single<Category>("SELECT * FROM Category WHERE Id = @id", new { id });
    public Category? GetByName(string fullName) => Single<Category>("SELECT * FROM Category WHERE FullName = @fullName", new { fullName });

    /// <summary>Insert or update by FullName. Returns true if inserted.</summary>
    public bool Upsert(string fullName, string? accntType, bool isActive, DateTime now)
    {
        bool existed = Scalar<long>("SELECT COUNT(*) FROM Category WHERE FullName = @fullName", new { fullName }) > 0;
        Exec("""
            INSERT INTO Category (FullName, AccntType, IsActive, ImportedUtc) VALUES (@fullName, @accntType, @active, @now)
            ON CONFLICT (FullName) DO UPDATE SET AccntType = excluded.AccntType, IsActive = excluded.IsActive, ImportedUtc = excluded.ImportedUtc
            """, new { fullName, accntType, active = isActive ? 1 : 0, now = DbUtil.Utc(now) });
        return !existed;
    }

    public int DeactivateAllExcept(IReadOnlyCollection<string> fullNames) => Exec(
        "UPDATE Category SET IsActive = 0 WHERE IsActive = 1 AND FullName NOT IN @fullNames", new { fullNames = fullNames.ToArray() });
}

public sealed class QbNameRepository(DbSession s) : RepositoryBase(s)
{
    private const string Select = """
        SELECT Id, Name,
               CASE NameType WHEN 'CUST' THEN 'Customer' WHEN 'VEND' THEN 'Vendor' WHEN 'OTHER' THEN 'OtherName' ELSE 'Employee' END AS NameType,
               IsActive, IsInvoiced
        FROM QbName
        """;

    public IReadOnlyList<QbName> GetAll(bool activeOnly = false) => Query<QbName>(
        $"{Select} WHERE (@activeOnly = 0 OR IsActive = 1) ORDER BY Name", new { activeOnly = activeOnly ? 1 : 0 }).ToList();

    public QbName? Get(long id) => Single<QbName>($"{Select} WHERE Id = @id", new { id });

    public QbName? FindByName(string name) =>
        Query<QbName>($"{Select} WHERE Name = @name ORDER BY IsActive DESC, Id", new { name }).FirstOrDefault();

    public void SetInvoiced(long id, bool invoiced) =>
        Exec("UPDATE QbName SET IsInvoiced = @v WHERE Id = @id", new { id, v = invoiced ? 1 : 0 });

    public IReadOnlySet<long> InvoicedIds() => Query<long>("SELECT Id FROM QbName WHERE IsInvoiced = 1").ToHashSet();

    public bool Upsert(string name, QbNameType type, bool isActive)
    {
        var code = type.ToCode();
        bool existed = Scalar<long>("SELECT COUNT(*) FROM QbName WHERE Name = @name AND NameType = @code", new { name, code }) > 0;
        Exec("""
            INSERT INTO QbName (Name, NameType, IsActive) VALUES (@name, @code, @active)
            ON CONFLICT (Name, NameType) DO UPDATE SET IsActive = excluded.IsActive
            """, new { name, code, active = isActive ? 1 : 0 });
        return !existed;
    }

    public int DeactivateAllExcept(IReadOnlyCollection<(string Name, QbNameType Type)> keep)
    {
        var keys = keep.Select(k => k.Type.ToCode() + "|" + k.Name.ToUpperInvariant()).ToArray();
        return Exec("UPDATE QbName SET IsActive = 0 WHERE IsActive = 1 AND (NameType || '|' || UPPER(Name)) NOT IN @keys", new { keys });
    }
}

public sealed class RuleRepository(DbSession s) : RepositoryBase(s)
{
    public IReadOnlyList<Rule> GetAll() => Query<Rule>("SELECT * FROM Rule ORDER BY Priority, Id").ToList();
    public Rule? Get(long id) => Single<Rule>("SELECT * FROM Rule WHERE Id = @id", new { id });

    public long Insert(Rule r) => r.Id = Insert("""
        INSERT INTO Rule (Priority, Field, MatchKind, Pattern, Sign, MinCents, MaxCents, BankAccountId, CategoryId, QbNameId, Class,
                          AutoExclude, HitCount, LastHitUtc, IsEnabled, SplitTemplateId, Source)
        VALUES (@Priority, @Field, @MatchKind, @Pattern, @Sign, @MinCents, @MaxCents, @BankAccountId, @CategoryId, @QbNameId, @Class,
                @AutoExclude, 0, NULL, @IsEnabled, @SplitTemplateId, @Source)
        """, Params(r));

    public void Update(Rule r) => Exec("""
        UPDATE Rule SET Priority = @Priority, Field = @Field, MatchKind = @MatchKind, Pattern = @Pattern, Sign = @Sign,
               MinCents = @MinCents, MaxCents = @MaxCents, BankAccountId = @BankAccountId, CategoryId = @CategoryId,
               QbNameId = @QbNameId, Class = @Class, AutoExclude = @AutoExclude, IsEnabled = @IsEnabled,
               SplitTemplateId = @SplitTemplateId, Source = @Source
        WHERE Id = @Id
        """, Params(r));

    public void Delete(long id) => Exec("DELETE FROM Rule WHERE Id = @id", new { id });

    public void AddHits(IReadOnlyDictionary<long, int> hits, DateTime now)
    {
        foreach (var (id, n) in hits)
            Exec("UPDATE Rule SET HitCount = HitCount + @n, LastHitUtc = @now WHERE Id = @id", new { id, n, now = DbUtil.Utc(now) });
    }

    private static object Params(Rule r) => new
    {
        r.Id, r.Priority, Field = r.Field.ToString(), MatchKind = r.MatchKind.ToString(), r.Pattern, Sign = r.Sign.ToString(),
        r.MinCents, r.MaxCents, r.BankAccountId, r.CategoryId, r.QbNameId, r.Class,
        AutoExclude = r.AutoExclude ? 1 : 0, IsEnabled = r.IsEnabled ? 1 : 0, r.SplitTemplateId, r.Source,
    };
}

public sealed class PayeeAliasRepository(DbSession s) : RepositoryBase(s)
{
    public IReadOnlyList<PayeeAlias> GetAll() => Query<PayeeAlias>("SELECT * FROM PayeeAlias ORDER BY Id").ToList();

    public long Insert(PayeeAlias a) => a.Id = Insert(
        "INSERT INTO PayeeAlias (RawPattern, MatchKind, CleanPayee, QbNameId, Source) VALUES (@RawPattern, @MatchKind, @CleanPayee, @QbNameId, @Source)",
        new { a.RawPattern, MatchKind = a.MatchKind.ToString(), a.CleanPayee, a.QbNameId, a.Source });

    public void Update(PayeeAlias a) => Exec(
        "UPDATE PayeeAlias SET RawPattern = @RawPattern, MatchKind = @MatchKind, CleanPayee = @CleanPayee, QbNameId = @QbNameId, Source = @Source WHERE Id = @Id",
        new { a.Id, a.RawPattern, MatchKind = a.MatchKind.ToString(), a.CleanPayee, a.QbNameId, a.Source });

    public void Delete(long id) => Exec("DELETE FROM PayeeAlias WHERE Id = @id", new { id });
}

public sealed class ExportBatchRepository(DbSession s) : RepositoryBase(s)
{
    public long Insert(ExportBatch b) => b.Id = Insert("""
        INSERT INTO ExportBatch (CreatedUtc, IifPath, BackupPath, TxnCount, TotalCents, VoidedUtc)
        VALUES (@CreatedUtc, @IifPath, @BackupPath, @TxnCount, @TotalCents, NULL)
        """, new { CreatedUtc = DbUtil.Utc(b.CreatedUtc), b.IifPath, b.BackupPath, b.TxnCount, b.TotalCents });

    public ExportBatch? Get(long id) => Single<ExportBatch>("SELECT * FROM ExportBatch WHERE Id = @id", new { id });
    public IReadOnlyList<ExportBatch> GetAll() => Query<ExportBatch>("SELECT * FROM ExportBatch ORDER BY Id DESC").ToList();

    public void SetIifPath(long id, string path) => Exec("UPDATE ExportBatch SET IifPath = @path WHERE Id = @id", new { id, path });

    public bool SetVoided(long id, DateTime now) =>
        Exec("UPDATE ExportBatch SET VoidedUtc = @now WHERE Id = @id AND VoidedUtc IS NULL", new { id, now = DbUtil.Utc(now) }) == 1;
}

public sealed class AuditRepository(DbSession s) : RepositoryBase(s)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public void Write(string entity, long entityId, string action, object? detail, DateTime now) => Exec(
        "INSERT INTO AuditLog (Utc, Entity, EntityId, Action, Detail) VALUES (@now, @entity, @entityId, @action, @detail)",
        new { now = DbUtil.Utc(now), entity, entityId, action, detail = detail is null ? null : JsonSerializer.Serialize(detail, Json) });

    public IReadOnlyList<AuditLogEntry> For(string entity, long entityId) => Query<AuditLogEntry>(
        "SELECT * FROM AuditLog WHERE Entity = @entity AND EntityId = @entityId ORDER BY Id", new { entity, entityId }).ToList();

    /// <summary>Newest first. Dates are inclusive calendar days (UTC); null filters are ignored.</summary>
    public IReadOnlyList<AuditLogEntry> Search(DateOnly? from, DateOnly? to, string? entity, string? action, int limit = 5000) => Query<AuditLogEntry>("""
        SELECT * FROM AuditLog
        WHERE (@from IS NULL OR Utc >= @from) AND (@to IS NULL OR Utc < @to)
          AND (@entity IS NULL OR Entity = @entity) AND (@action IS NULL OR Action = @action)
        ORDER BY Id DESC LIMIT @limit
        """, new { from = DbUtil.Date(from), to = DbUtil.Date(to?.AddDays(1)), entity, action, limit }).ToList();

    public IReadOnlyList<string> Entities() => Query<string>("SELECT DISTINCT Entity FROM AuditLog ORDER BY Entity").ToList();
    public IReadOnlyList<string> Actions() => Query<string>("SELECT DISTINCT Action FROM AuditLog ORDER BY Action").ToList();
}

public sealed class SettingRepository(DbSession s) : RepositoryBase(s)
{
    public const string IifClear = "IifClear";
    public const string QbClasses = "QbClasses";

    public string? Get(string key) => Single<string>("SELECT Value FROM Setting WHERE Key = @key", new { key });

    public void Set(string key, string value) => Exec(
        "INSERT INTO Setting (Key, Value) VALUES (@key, @value) ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value",
        new { key, value });

    public bool GetIifClear() => Get(IifClear) is not "N";
    public void SetIifClear(bool clear) => Set(IifClear, clear ? "Y" : "N");

    public IReadOnlyList<string> GetClasses() =>
        Get(QbClasses) is { } json ? JsonSerializer.Deserialize<List<string>>(json) ?? [] : [];

    public void SetClasses(IEnumerable<string> classes) => Set(QbClasses, JsonSerializer.Serialize(classes.ToList()));
}
