using System.Windows;
using System.Windows.Input;
using QbImportMgr.App.ViewModels;
using QbImportMgr.Core.Domain;

namespace QbImportMgr.App.Views;

public partial class LearningWindow : Window
{
    public LearningWindow() => InitializeComponent();
}

public partial class NamesWindow : Window
{
    public NamesWindow() => InitializeComponent();
}

public partial class AuditWindow : Window
{
    public AuditWindow() => InitializeComponent();
}

public partial class PickMatchWindow : Window
{
    public PickMatchWindow() => InitializeComponent();

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PickMatchViewModel { Selected: not null }) DialogResult = true;
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Pick_Click(sender, e);
}

public partial class ChooseAccountWindow : Window
{
    private sealed record Item(long Id, string Label);

    public ChooseAccountWindow(IReadOnlyList<BankAccount> accounts, string prompt)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        AccountList.ItemsSource = accounts.Select(a => new Item(a.Id, $"{a.DisplayName}  →  {a.QbAccountName}")).ToList();
        AccountList.SelectedIndex = accounts.Count == 1 ? 0 : -1;
    }

    public long? SelectedId => (AccountList.SelectedItem as Item)?.Id;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId is not null) DialogResult = true;
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Ok_Click(sender, e);
}

public partial class TemplateNameWindow : Window
{
    public TemplateNameWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    public string TemplateName => NameBox.Text.Trim();
    public TemplateLineKind Kind => FixedOption.IsChecked == true ? TemplateLineKind.Fixed : TemplateLineKind.Percent;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TemplateName.Length > 0) DialogResult = true;
    }
}
