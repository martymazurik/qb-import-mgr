using System.Windows;
using System.Windows.Threading;
using QbImportMgr.Core;
using QbImportMgr.Data.Db;
using QbImportMgr.Data.Services;
using QbImportMgr.App.ViewModels;
using QbImportMgr.App.Views;

namespace QbImportMgr.App;

public partial class App : Application
{
#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        AppPaths paths;
        try
        {
            paths = new AppPaths(AppPaths.ResolveRoot(e.Args, Environment.GetEnvironmentVariable, IsDebugBuild));
            paths.EnsureCreated();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Cannot use the data folder: {ex.Message}", "qb-import-mgr", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        var db = new Database(paths.Database);
        db.Migrate();

        var services = AppServices.Create(paths, db, TimeProvider.System);
        var window = new MainWindow();
        var vm = new MainViewModel(services, new DialogService(window));
        window.Attach(vm);
        MainWindow = window;
        window.Show();
        vm.Start();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "qb-import-mgr: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

public sealed record AppServices(
    AppPaths Paths,
    Database Db,
    TimeProvider Clock,
    ImportService Import,
    TxnService Txns,
    ExportService Export,
    ListImportService Lists,
    AccountService Accounts,
    LedgerService Ledger)
{
    public static AppServices Create(AppPaths paths, Database db, TimeProvider clock)
    {
        var backup = new BackupService(db, paths, clock);
        return new AppServices(paths, db, clock,
            new ImportService(db, paths, clock),
            new TxnService(db, clock),
            new ExportService(db, paths, backup, clock),
            new ListImportService(db, clock),
            new AccountService(db, clock),
            new LedgerService(db));
    }
}
