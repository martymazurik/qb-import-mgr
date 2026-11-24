using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using QbImportMgr.App.ViewModels;

namespace QbImportMgr.App.Views;

public partial class AccountMappingWindow : Window
{
    public AccountMappingWindow() => InitializeComponent();
    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

public partial class RuleEditorWindow : Window
{
    public RuleEditorWindow() => InitializeComponent();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is RuleEditorViewModel vm && vm.Validate()) DialogResult = true;
    }
}

public partial class ExportWindow : Window
{
    public ExportWindow() => InitializeComponent();
}

public partial class BatchesWindow : Window
{
    public BatchesWindow() => InitializeComponent();
}

public partial class TextWindow : Window
{
    public TextWindow() => InitializeComponent();
    public string Body { set => BodyBox.Text = value; }
}

public sealed class DialogService(Window owner) : IDialogService
{
    public void Info(string message, string title = "qb-import-mgr") =>
        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string message, string title = "qb-import-mgr") =>
        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public bool Confirm(string message, string title = "qb-import-mgr") =>
        MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public string? OpenFile(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    public void ShowText(string title, string text) =>
        new TextWindow { Owner = owner, Title = title, Body = text }.ShowDialog();

    public bool ShowAccountMapping(AccountMappingViewModel vm) =>
        new AccountMappingWindow { Owner = owner, DataContext = vm }.ShowDialog() == true;

    public bool ShowRuleEditor(RuleEditorViewModel vm) =>
        new RuleEditorWindow { Owner = owner, DataContext = vm }.ShowDialog() == true;

    public void ShowExport(ExportViewModel vm)
    {
        var w = new ExportWindow { Owner = owner, DataContext = vm };
        vm.RequestClose += (_, _) => w.Close();
        w.ShowDialog();
    }

    public void ShowBatches(BatchesViewModel vm) =>
        new BatchesWindow { Owner = owner, DataContext = vm }.ShowDialog();

    public long? ChooseAccount(IReadOnlyList<Core.Domain.BankAccount> accounts, string prompt)
    {
        var w = new ChooseAccountWindow(accounts, prompt) { Owner = owner };
        return w.ShowDialog() == true ? w.SelectedId : null;
    }

    public bool ShowPickMatch(PickMatchViewModel vm) =>
        new PickMatchWindow { Owner = owner, DataContext = vm }.ShowDialog() == true;

    public void ShowLearning(LearningViewModel vm)
    {
        var w = new LearningWindow { Owner = owner, DataContext = vm };
        vm.RequestClose += (_, _) => w.Close();
        w.ShowDialog();
    }

    public void ShowNames(NamesViewModel vm) => new NamesWindow { Owner = owner, DataContext = vm }.ShowDialog();

    public void ShowAudit(AuditViewModel vm) => new AuditWindow { Owner = owner, DataContext = vm }.ShowDialog();

    public (string Name, Core.Domain.TemplateLineKind Kind)? AskTemplate()
    {
        var w = new TemplateNameWindow { Owner = owner };
        return w.ShowDialog() == true ? (w.TemplateName, w.Kind) : null;
    }

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
