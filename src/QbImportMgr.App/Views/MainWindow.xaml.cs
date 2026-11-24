using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QbImportMgr.App.ViewModels;

namespace QbImportMgr.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private bool _editing;

    public MainWindow() => InitializeComponent();

    private MainViewModel Vm => _vm ?? throw new InvalidOperationException("View model not attached.");

    public void Attach(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        ApplyLayout(vm.LoadLayout());
    }

    // ------------------------------------------------------------------ layout persistence (Setting table)

    private void ApplyLayout(UiLayout l)
    {
        if (l.Width is > 200 and var w) Width = w;
        if (l.Height is > 200 and var h) Height = h;
        if (l.Left is { } left && l.Top is { } top &&
            left >= SystemParameters.VirtualScreenLeft - 50 && top >= SystemParameters.VirtualScreenTop - 50 &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (l.Maximized) WindowState = WindowState.Maximized;
        if (l.LeftPane is > 100 and var lp) LeftCol.Width = new GridLength(lp);
        if (l.RightPane is > 150 and var rp) RightCol.Width = new GridLength(rp);
        foreach (var col in TxnGrid.Columns)
            if (col.Header is string header && l.Columns.TryGetValue(header, out var cw) && cw > 20)
                col.Width = new DataGridLength(cw);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_vm is null) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var layout = new UiLayout
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
            LeftPane = LeftCol.ActualWidth,
            RightPane = RightCol.ActualWidth,
            Columns = TxnGrid.Columns.Where(c => c.Header is string).ToDictionary(c => (string)c.Header, c => c.ActualWidth),
        };
        try { _vm.SaveLayout(layout); }
        catch (Exception ex) when (MainViewModel.IsUserError(ex)) { /* never block closing on a layout save */ }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ tree and selection

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeNodeViewModel node) Vm.SelectedNode = node;
    }

    private void TxnGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Vm.SetSelection(TxnGrid.SelectedItems.OfType<TxnRowViewModel>().ToList());

    // ------------------------------------------------------------------ keyboard

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // Text boxes keep their own Ctrl+Z while they have something to undo.
            if (Keyboard.FocusedElement is TextBox { CanUndo: true }) return;
            if (_editing) return;
            if (Vm.UndoCommand.CanExecute(null)) Vm.UndoCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Enter approves (Categorized → Ready) and moves down; Del excludes. Both act on the whole selection.</summary>
    private void TxnGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_editing || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Enter)
        {
            int next = LastSelectedIndex() + 1;
            Vm.ApproveSelected();
            MoveTo(next);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            int next = LastSelectedIndex() + 1;
            Vm.ExcludeSelected();
            MoveTo(next);
            e.Handled = true;
        }
    }

    private int LastSelectedIndex() =>
        TxnGrid.SelectedItems.Count == 0 ? TxnGrid.SelectedIndex : TxnGrid.SelectedItems.Cast<object>().Max(i => TxnGrid.Items.IndexOf(i));

    private void MoveTo(int index)
    {
        if (index < 0 || index >= TxnGrid.Items.Count) return;
        var item = TxnGrid.Items[index];
        TxnGrid.SelectedItem = item;
        TxnGrid.CurrentCell = new DataGridCellInfo(item, TxnGrid.CurrentColumn ?? CategoryColumn);
        TxnGrid.ScrollIntoView(item);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (TxnGrid.ItemContainerGenerator.ContainerFromIndex(index) is DataGridRow row)
                row.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        });
    }

    /// <summary>Typing on a row (outside the payee cell) starts editing its category with that text.</summary>
    private void TxnGrid_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_editing || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (TxnGrid.CurrentColumn == PayeeColumn) return;
        if (TxnGrid.SelectedItem is not TxnRowViewModel row || row.IsLocked) return;
        TxnGrid.CurrentCell = new DataGridCellInfo(row, CategoryColumn);
        TxnGrid.BeginEdit(e);
        e.Handled = true;
    }

    private void TxnGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is TxnRowViewModel { IsLocked: true })
        {
            e.Cancel = true;
            Vm.StatusMessage = "Exported rows are locked. Void the batch to edit.";
        }
    }

    private void TxnGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        _editing = true;
        if (e.Column != CategoryColumn || e.Row.Item is not TxnRowViewModel row) return;
        var typed = (e.EditingEventArgs as TextCompositionEventArgs)?.Text;
        var editor = e.EditingElement;

        // The editing template's ComboBox exists only after layout.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (FindChild<ComboBox>(editor) is not { } combo) return;
            combo.ApplyTemplate();
            var box = combo.Template.FindName("PART_EditableTextBox", combo) as TextBox;
            combo.Text = typed ?? (row.HasSplits ? "" : row.CategoryText);
            if (box is null) return;
            box.Focus();
            if (typed is null) box.SelectAll();
            else box.CaretIndex = box.Text.Length;
            combo.IsDropDownOpen = true;
        });
    }

    private void TxnGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        _editing = false;
        if (e.EditAction != DataGridEditAction.Commit || e.Column != CategoryColumn || e.Row.Item is not TxnRowViewModel row) return;
        if (FindChild<ComboBox>(e.EditingElement) is not { } combo) return;

        var text = combo.Text?.Trim() ?? "";
        CategoryOption? option = combo.SelectedItem as CategoryOption;
        if (option is null || !option.FullName.Equals(text, StringComparison.OrdinalIgnoreCase))
            option = Vm.Lookups.FindCategory(text);

        if (option is null && text.Length > 0)
        {
            Vm.StatusMessage = $"No QuickBooks account matches \"{text}\".";
            return;
        }
        if (option is null && row.HasSplits) return; // blank edit on a split row: leave the splits alone
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Vm.SetCategoryFromGrid(row, option));
    }

    private static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        if (parent is T self) return self;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindChild<T>(VisualTreeHelper.GetChild(parent, i)) is { } found)
                return found;
        return null;
    }
}
