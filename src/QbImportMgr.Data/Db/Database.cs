using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using QbImportMgr.Data.Repositories;

namespace QbImportMgr.Data.Db;

public sealed class Database
{
    public Database(string path)
    {
        Path = path;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Pooling = false, // release the file promptly so backups and moves see a closed DB
        }.ToString();
    }

    public string Path { get; }
    public string ConnectionString { get; }

    public DbSession Open()
    {
        DapperSetup.Init();
        var c = new SqliteConnection(ConnectionString);
        c.Open();
        c.Execute("PRAGMA busy_timeout = 5000;");
        return new DbSession(c);
    }

    /// <summary>Creates or upgrades the schema. Returns the resulting schema version.</summary>
    public int Migrate()
    {
        using var s = Open();
        return Migrator.Migrate(s.Connection);
    }
}

/// <summary>One open connection plus an optional ambient transaction that all repositories use.</summary>
public sealed class DbSession(SqliteConnection connection) : IDisposable
{
    public SqliteConnection Connection { get; } = connection;
    public SqliteTransaction? Transaction { get; private set; }

    public BankAccountRepository Accounts => new(this);
    public QboFileRepository Files => new(this);
    public TxnRepository Txns => new(this);
    public TxnSplitRepository Splits => new(this);
    public CategoryRepository Categories => new(this);
    public QbNameRepository Names => new(this);
    public RuleRepository Rules => new(this);
    public PayeeAliasRepository Aliases => new(this);
    public ExportBatchRepository Batches => new(this);
    public AuditRepository Audit => new(this);
    public SettingRepository Settings => new(this);
    public QbRegisterRepository QbRegister => new(this);
    public RejectionRepository Rejections => new(this);
    public SplitTemplateRepository Templates => new(this);

    /// <summary>Runs work in a transaction; nested calls join the outer one.</summary>
    public T InTransaction<T>(Func<T> work)
    {
        if (Transaction is not null) return work();
        Transaction = Connection.BeginTransaction();
        try
        {
            var result = work();
            Transaction.Commit();
            return result;
        }
        catch
        {
            Transaction.Rollback();
            throw;
        }
        finally
        {
            Transaction.Dispose();
            Transaction = null;
        }
    }

    public void InTransaction(Action work) => InTransaction(() => { work(); return 0; });

    public void Dispose()
    {
        Transaction?.Dispose();
        Connection.Dispose();
    }
}

internal static class Migrator
{
    private static readonly string[] Scripts = ["Schema_001.sql", "Schema_002.sql", "Schema_003.sql"];

    /// <summary>
    /// Runs pending scripts, each in its own transaction. Foreign keys are switched off for the
    /// duration (a table rebuild would otherwise cascade-delete child rows) and every script must
    /// leave PRAGMA foreign_key_check clean before it commits.
    /// </summary>
    public static int Migrate(SqliteConnection c)
    {
        int version = c.ExecuteScalar<int>("PRAGMA user_version;");
        if (version >= Scripts.Length) return version;

        c.Execute("PRAGMA foreign_keys = OFF;");
        try
        {
            for (int v = version; v < Scripts.Length; v++)
            {
                using var tx = c.BeginTransaction();
                c.Execute(LoadScript(Scripts[v]), transaction: tx);
                var problems = c.Query("PRAGMA foreign_key_check;", transaction: tx).ToList();
                if (problems.Count > 0)
                    throw new InvalidOperationException($"Schema upgrade to v{v + 1} left {problems.Count} foreign key problem(s); nothing was changed.");
                c.Execute($"PRAGMA user_version = {v + 1};", transaction: tx);
                tx.Commit();
            }
        }
        finally
        {
            c.Execute("PRAGMA foreign_keys = ON;");
        }
        return c.ExecuteScalar<int>("PRAGMA user_version;");
    }

    private static string LoadScript(string name)
    {
        var asm = typeof(Migrator).Assembly;
        using var stream = asm.GetManifestResourceStream($"QbImportMgr.Data.Db.{System.IO.Path.GetFileNameWithoutExtension(name)}.sql")
            ?? throw new InvalidOperationException($"Embedded schema script {name} not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal static class DapperSetup
{
    private static int _done;

    public static void Init()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new UtcDateTimeHandler());
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public override DateOnly Parse(object value) => value switch
        {
            string s => DateOnly.ParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dt => DateOnly.FromDateTime(dt),
            DateOnly d => d,
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateOnly"),
        };
    }

    private sealed class UtcDateTimeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override void SetValue(IDbDataParameter parameter, DateTime value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = Db.Utc(value);
        }

        public override DateTime Parse(object value) => value switch
        {
            string s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateTime"),
        };
    }
}

internal static class Db
{
    public static string Utc(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    public static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
