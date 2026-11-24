namespace QbImportMgr.Core;

public sealed class AppPaths
{
    public const string DefaultRoot = @"C:\accounting";
    public const string DevRoot = @"C:\accounting-dev";
    public const string DataDirArg = "--data-dir";
    public const string DataDirEnv = "QBIM_DATA_DIR";

    /// <summary>
    /// Data folder precedence: --data-dir argument, then QBIM_DATA_DIR, then C:\accounting
    /// (Release) or C:\accounting-dev (Debug, so testing never touches the real database).
    /// </summary>
    public static string ResolveRoot(IReadOnlyList<string> args, Func<string, string?> getEnv, bool isDebugBuild)
    {
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.Equals(DataDirArg, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
                    throw new ArgumentException($"{DataDirArg} needs a folder path.");
                return Path.GetFullPath(args[i + 1].Trim().Trim('"'));
            }
            if (a.StartsWith(DataDirArg + "=", StringComparison.OrdinalIgnoreCase))
            {
                var v = a[(DataDirArg.Length + 1)..].Trim().Trim('"');
                if (v.Length == 0) throw new ArgumentException($"{DataDirArg} needs a folder path.");
                return Path.GetFullPath(v);
            }
        }
        if (getEnv(DataDirEnv) is { } env && !string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env.Trim().Trim('"'));
        return isDebugBuild ? DevRoot : DefaultRoot;
    }

    public AppPaths(string root = DefaultRoot)
    {
        Root = root;
        Inbox = Path.Combine(root, "qbo");
        Archive = Path.Combine(Inbox, "archive");
        Iif = Path.Combine(root, "iif");
        IifImported = Path.Combine(Iif, "imported");
        IifVoided = Path.Combine(Iif, "voided");
        Backup = Path.Combine(root, "backup");
        Database = Path.Combine(root, "qb-import-mgr.db");
    }

    public string Root { get; }
    public string Inbox { get; }
    public string Archive { get; }
    /// <summary>Exports waiting for QuickBooks stay in the root of this folder.</summary>
    public string Iif { get; }

    /// <summary>Batches imported into QuickBooks, plus QuickBooks error and *_fixed files, by month.</summary>
    public string IifImported { get; }

    /// <summary>Voided batches, by month.</summary>
    public string IifVoided { get; }

    public string Backup { get; }
    public string Database { get; }

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, Inbox, Archive, Iif, IifImported, IifVoided, Backup })
            Directory.CreateDirectory(d);
    }
}
