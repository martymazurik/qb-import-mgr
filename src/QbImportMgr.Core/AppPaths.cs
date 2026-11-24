namespace QbImportMgr.Core;

public sealed class AppPaths
{
    public const string DefaultRoot = @"C:\accounting";

    public AppPaths(string root = DefaultRoot)
    {
        Root = root;
        Inbox = Path.Combine(root, "qbo");
        Archive = Path.Combine(Inbox, "archive");
        Iif = Path.Combine(root, "iif");
        Backup = Path.Combine(root, "backup");
        Database = Path.Combine(root, "qb-import-mgr.db");
    }

    public string Root { get; }
    public string Inbox { get; }
    public string Archive { get; }
    public string Iif { get; }
    public string Backup { get; }
    public string Database { get; }

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, Inbox, Archive, Iif, Backup })
            Directory.CreateDirectory(d);
    }
}
