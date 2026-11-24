using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using QbImportMgr.Core.Domain;
using QbImportMgr.Data.Services;

namespace QbImportMgr.App.ViewModels;

public sealed record UndoEntry(string Description, IReadOnlyList<TxnSnapshot> Before, long? CreatedRuleId);

public sealed class UiLayout
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }
    public double? LeftPane { get; set; }
    public double? RightPane { get; set; }
    public Dictionary<string, double> Columns { get; set; } = [];
}

public sealed partial class MainViewModel : ObservableObject
{
    private const string LayoutKey = "Ui.Layout";
    private const int UndoLimit = 50;

    private readonly AppServices _svc;
    private readonly IDialogService _dialogs;
    private readonly List<UndoEntry> _undo = [];
    private readonly Dictionary<long, TxnRowViewModel> _rowsById = [];
    private Dictionary<long, BankAccount> _accounts = [];
    private IReadOnlyList<TxnRowViewModel> _selection = [];
    private bool _loading;

    public MainViewModel(AppServices svc, IDialogService dialogs)
    {
        _svc = svc;
        _dialogs = dialogs;
        Detail = new DetailViewModel(this, svc);
    }

    public Lookups Lookups { get; } = new();
    public DetailViewModel Detail { get; }
    public ObservableCollection<TreeNodeViewModel> Tree { get; } = [];
    public string Title => $"qb-import-mgr  ·  {_svc.Paths.Root}";
    public bool IsDevDataFolder => _svc.Paths.Root.Equals(Core.AppPaths.DevRoot, StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private ObservableCollection<TxnRowViewModel> _rows = [];
    [ObservableProperty] private TreeNodeViewModel? _selectedNode;
    [ObservableProperty] private TxnRowViewModel? _selectedRow;
    [ObservableProperty] private string _viewTitle = "";
    [ObservableProperty] private string _footerCounts = "";
    [ObservableProperty] private string _footerTotals = "";
    [ObservableProperty] private string _ledgerText = "";
    [ObservableProperty] private bool? _ledgerTies;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private int _inboxCount;
    [ObservableProperty] private CategoryOption? _bulkCategory;
    [ObservableProperty] private string? _undoDescription;
    [ObservableProperty] private bool _iifClear = true;
    [ObservableProperty] private int _selectionCount;

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value) => LoadRows();
    partial void OnSelectedRowChanged(TxnRowViewModel? value) => Detail.Load(value);

    partial void OnIifClearChanged(bool value)
    {
        if (_loading) return;
        using var s = _svc.Db.Open();
        s.Settings.SetIifClear(value);
        StatusMessage = value ? "IIF exports will mark transactions cleared (CLEAR=Y)." : "IIF exports will leave transactions uncleared (CLEAR=N).";
    }

    // ================================================================== startup and loading

    public void Start()
    {
        _loading = true;
        using (var s = _svc.Db.Open()) IifClear = s.Settings.GetIifClear();
        _loading = false;

        ReloadLookups();
        RebuildTree("view:New");
        RefreshInboxCount();
        StatusMessage = InboxCount > 0 ? $"{InboxCount} file(s) waiting in {_svc.Paths.Inbox}. Use Import inbox." : "Ready.";
        if (!Lookups.HasQbLists)
            StatusMessage += "  Import your QuickBooks lists first (File > Import QuickBooks lists).";
    }

    private void ReloadLookups()
    {
        using var s = _svc.Db.Open();
        Lookups.Reload(s);
        _accounts = s.Accounts.GetAll().ToDictionary(a => a.Id);
        OnPropertyChanged(nameof(Lookups));
    }

    public void RefreshInboxCount() => InboxCount = _svc.Import.ScanInbox().Count;

    private static readonly (TxnStatus? Status, string Label)[] Views =
    [
        (TxnStatus.New, "Needs category"),
        (TxnStatus.Duplicate, "Possible duplicates"),
        (TxnStatus.Ambiguous, "Ambiguous match"),
        (TxnStatus.HoldReceivePayment, "Waiting for Receive Payments"),
        (TxnStatus.Ready, "Ready"),
        (TxnStatus.InQb, "In QuickBooks"),
        (null, "Transfers"),
        (TxnStatus.Excluded, "Excluded"),
    ];

    private static readonly TxnStatus[] Attention = [TxnStatus.New, TxnStatus.Duplicate, TxnStatus.Ambiguous, TxnStatus.HoldReceivePayment];

    private void RebuildTree(string? selectKey = null)
    {
        var key = selectKey ?? SelectedNode?.Key;
        using var s = _svc.Db.Open();
        Tree.Clear();

        var accountsHeader = new TreeNodeViewModel(NodeKind.Header, "Accounts", "hdr:accounts") { IsExpanded = true };
        foreach (var a in s.Accounts.GetAll())
        {
            var node = new TreeNodeViewModel(NodeKind.Account, a.DisplayName, $"acct:{a.Id}") { AccountId = a.Id };
            var files = s.Files.StatementFileNames(a.Id);
            foreach (var st in s.Files.StatementsForAccount(a.Id).Reverse())
            {
                node.Children.Add(new TreeNodeViewModel(NodeKind.Statement, PeriodLabel(st), $"stmt:{st.Id}")
                {
                    AccountId = a.Id,
                    StatementId = st.Id,
                    Subtitle = files.GetValueOrDefault(st.Id),
                    Badge = st.TxnCount.ToString(CultureInfo.InvariantCulture),
                });
            }
            accountsHeader.Children.Add(node);
        }
        if (accountsHeader.Children.Count == 0)
            accountsHeader.Children.Add(new TreeNodeViewModel(NodeKind.Header, "No downloads imported yet", "hdr:none"));

        var views = new TreeNodeViewModel(NodeKind.Header, "Views", "hdr:views") { IsExpanded = true };
        foreach (var (status, label) in Views)
            views.Children.Add(new TreeNodeViewModel(NodeKind.View, label, $"view:{status?.ToString() ?? "Transfers"}")
            {
                ViewStatus = status,
                IsTransfers = status is null,
            });

        Tree.Add(accountsHeader);
        Tree.Add(views);
        RefreshTreeCounts(s);

        var target = Tree.SelectMany(t => t.SelfAndDescendants()).FirstOrDefault(n => n.Key == key && n.Kind != NodeKind.Header)
                     ?? views.Children[0];
        foreach (var acct in accountsHeader.Children.Where(a => a.Children.Contains(target))) acct.IsExpanded = true;
        target.IsSelected = true;
        if (ReferenceEquals(SelectedNode, target)) LoadRows(); else SelectedNode = target;
    }

    private void RefreshTreeCounts(Data.Db.DbSession? session = null)
    {
        var s = session ?? _svc.Db.Open();
        try
        {
            var global = s.Txns.StatusCounts();
            foreach (var n in Tree.SelectMany(t => t.SelfAndDescendants()))
            {
                if (n.Kind == NodeKind.View)
                {
                    int c = global.GetValueOrDefault(n.ViewStatus ?? TxnStatus.Paired);
                    n.Badge = c.ToString(CultureInfo.InvariantCulture);
                    n.NeedsAttention = c > 0 && n.ViewStatus is { } vs && Attention.Contains(vs);
                }
                else if (n.Kind == NodeKind.Account)
                {
                    var a = _accounts.GetValueOrDefault(n.AccountId!.Value) ?? s.Accounts.Get(n.AccountId!.Value)!;
                    var c = s.Txns.StatusCounts(a.Id);
                    int open = c.GetValueOrDefault(TxnStatus.New) + c.GetValueOrDefault(TxnStatus.Categorized) + c.GetValueOrDefault(TxnStatus.Duplicate)
                               + c.GetValueOrDefault(TxnStatus.Ambiguous) + c.GetValueOrDefault(TxnStatus.HoldReceivePayment);
                    n.Label = a.DisplayName;
                    n.Badge = open > 0 ? $"{open} open" : null;
                    n.Subtitle = a.QbAccountName is null ? "not mapped to QuickBooks" : $"→ {a.QbAccountName}";
                    n.NeedsAttention = a.QbAccountName is null;
                }
            }
        }
        finally
        {
            if (session is null) s.Dispose();
        }
    }

    private static string PeriodLabel(QboStatement st)
    {
        static string D(DateOnly? d) => d?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? "?";
        return $"{D(st.DtStart)} – {D(st.DtEnd)}";
    }

    public void LoadRows()
    {
        _rowsById.Clear();
        var node = SelectedNode;
        if (node is null || node.Kind == NodeKind.Header)
        {
            Rows = [];
            ViewTitle = node?.Label ?? "";
            UpdateFooter();
            return;
        }

        using var s = _svc.Db.Open();
        IReadOnlyList<Txn> txns = node.Kind switch
        {
            NodeKind.Account => s.Txns.ListByAccount(node.AccountId!.Value),
            NodeKind.Statement => s.Txns.ListByStatement(node.StatementId!.Value),
            _ when node.IsTransfers => s.Txns.ListPaired(),
            _ => s.Txns.ListByStatus(node.ViewStatus!.Value),
        };
        var withSplits = s.Splits.TxnIdsWithSplits(txns.Select(t => t.Id));
        var rows = new ObservableCollection<TxnRowViewModel>();
        foreach (var t in txns)
        {
            var row = new TxnRowViewModel(this, t, AccountName(t.BankAccountId), withSplits.Contains(t.Id));
            rows.Add(row);
            _rowsById[t.Id] = row;
        }
        Rows = rows;
        ViewTitle = node.Kind switch
        {
            NodeKind.Statement => $"{AccountName(node.AccountId!.Value)}  ·  {node.Label}  ·  {node.Subtitle}",
            _ => node.Label,
        };
        SelectedRow = null;
        UpdateFooter();
    }

    private string AccountName(long accountId) => _accounts.TryGetValue(accountId, out var a) ? a.DisplayName : $"#{accountId}";

    private void UpdateFooter()
    {
        var byStatus = Rows.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
        var parts = new List<string> { $"{Rows.Count} rows" };
        foreach (var st in Enum.GetValues<TxnStatus>())
            if (byStatus.TryGetValue(st, out var n)) parts.Add($"{StatusLabels.Short(st)} {n}");
        FooterCounts = string.Join("  ·  ", parts);

        var counted = Rows.Where(r => r.Status is not (TxnStatus.Excluded or TxnStatus.Duplicate)).ToList();
        long inC = counted.Where(r => r.Txn.SignedAmountCents > 0).Sum(r => r.Txn.SignedAmountCents);
        long outC = counted.Where(r => r.Txn.SignedAmountCents < 0).Sum(r => r.Txn.SignedAmountCents);
        long ready = Rows.Where(r => r.Status == TxnStatus.Ready).Sum(r => r.Txn.SignedAmountCents);
        FooterTotals = $"In {Money.FormatDisplay(inC)}  ·  Out {Money.FormatDisplay(outC)}  ·  Net {Money.FormatDisplay(inC + outC)}  ·  Ready net {Money.FormatDisplay(ready)}";

        UpdateLedger();
    }

    private void UpdateLedger()
    {
        var node = SelectedNode;
        LedgerStatus? status = node?.Kind switch
        {
            NodeKind.Statement => _svc.Ledger.ForStatement(node.StatementId!.Value),
            NodeKind.Account => _svc.Ledger.ForAccount(node.AccountId!.Value),
            _ => null,
        };
        if (status is null)
        {
            LedgerText = node?.Kind is NodeKind.Account or NodeKind.Statement ? "Ledger: no balance in downloads" : "";
            LedgerTies = null;
            return;
        }
        if (status.Result is not { } r)
        {
            LedgerText = "Ledger: " + status.Note;
            LedgerTies = null;
            return;
        }
        static string D(DateOnly d) => d.ToString("MM/dd", CultureInfo.InvariantCulture);
        LedgerTies = r.Ties;
        LedgerText = r.Ties
            ? $"Ledger ties ✓  {D(r.FromDate)}→{D(r.ToDate)}: bank change {Money.FormatDisplay(r.ExpectedChangeCents)} = {r.TxnCount} rows"
            : $"Ledger off by {Money.FormatDisplay(r.DifferenceCents)} ✗  {D(r.FromDate)}→{D(r.ToDate)}: bank change {Money.FormatDisplay(r.ExpectedChangeCents)}, rows {Money.FormatDisplay(r.TxnSumCents)} ({r.TxnCount})";
    }

    // ================================================================== selection

    public void SetSelection(IReadOnlyList<TxnRowViewModel> rows)
    {
        _selection = rows;
        SelectionCount = rows.Count;
        ApplyBulkCategoryCommand.NotifyCanExecuteChanged();
        RememberRuleCommand.NotifyCanExecuteChanged();
        ApproveSelectedCommand.NotifyCanExecuteChanged();
        ExcludeSelectedCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<TxnRowViewModel> Targets =>
        _selection.Count > 0 ? _selection : SelectedRow is { } r ? [r] : [];

    private bool HasTargets() => Targets.Count > 0;

    // ================================================================== edits with undo

    /// <summary>Snapshots the rows, runs the action, records an undo entry, and refreshes the rows it changed.</summary>
    internal bool Run(string description, IReadOnlyCollection<long> ids, Func<IReadOnlyList<Txn>> action, IReadOnlyList<string>? skipped = null)
    {
        if (ids.Count == 0) return false;
        try
        {
            var before = _svc.Txns.Snapshot(ids);
            var changed = action();
            var changedIds = changed.Select(t => t.Id).ToHashSet();
            if (changed.Count > 0)
                PushUndo(new UndoEntry(description, before.Where(b => changedIds.Contains(b.TxnId)).ToList(), null));
            ApplyChanged(changed);
            StatusMessage = changed.Count == 1 && ids.Count == 1 ? $"{description}." : $"{description}: {changed.Count} of {ids.Count} rows.";
            return true;
        }
        catch (Exception ex) when (IsUserError(ex))
        {
            _dialogs.Error(ex.Message);
            return false;
        }
    }

    internal static bool IsUserError(Exception ex) =>
        ex is InvalidOperationException or ArgumentException or KeyNotFoundException or SqliteException or InvalidDataException or IOException;

    private void PushUndo(UndoEntry e)
    {
        _undo.Add(e);
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        UndoDescription = e.Description;
        UndoCommand.NotifyCanExecuteChanged();
    }

    private void ClearUndo()
    {
        _undo.Clear();
        UndoDescription = null;
        UndoCommand.NotifyCanExecuteChanged();
    }

    private void ApplyChanged(IReadOnlyCollection<Txn> changed)
    {
        if (changed.Count > 0)
        {
            using var s = _svc.Db.Open();
            var withSplits = s.Splits.TxnIdsWithSplits(changed.Select(t => t.Id));
            foreach (var t in changed)
                if (_rowsById.TryGetValue(t.Id, out var row)) row.Update(t, withSplits.Contains(t.Id));
        }
        if (SelectedRow is not null && changed.Any(t => t.Id == SelectedRow.Id)) Detail.Load(SelectedRow);
        RefreshTreeCounts();
        UpdateFooter();
    }

    private void ReloadChanged(IEnumerable<long> ids)
    {
        using var s = _svc.Db.Open();
        ApplyChanged(s.Txns.GetMany(ids));
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0) return;
        var e = _undo[^1];
        try
        {
            var restored = _svc.Txns.Restore(e.Before);
            if (e.CreatedRuleId is { } ruleId) _svc.Txns.DeleteRule(ruleId);
            _undo.RemoveAt(_undo.Count - 1);
            UndoDescription = _undo.Count > 0 ? _undo[^1].Description : null;
            ApplyChanged(restored);
            StatusMessage = $"Undid: {e.Description}.";
        }
        catch (Exception ex) when (IsUserError(ex))
        {
            _undo.RemoveAt(_undo.Count - 1);
            UndoDescription = _undo.Count > 0 ? _undo[^1].Description : null;
            _dialogs.Error($"Cannot undo \"{e.Description}\": {ex.Message}");
        }
        UndoCommand.NotifyCanExecuteChanged();
    }

    private bool CanUndo() => _undo.Count > 0;

    /// <summary>Enter: Categorized → Ready for the selection.</summary>
    [RelayCommand(CanExecute = nameof(HasTargets))]
    public void ApproveSelected() => Approve(Targets);

    public void Approve(IReadOnlyList<TxnRowViewModel> rows)
    {
        var ids = rows.Select(r => r.Id).ToList();
        BulkResult? result = null;
        if (!Run("Approve", ids, () => (result = _svc.Txns.Approve(ids)).Changed)) return;
        if (result!.Skipped.Count > 0)
            StatusMessage = $"Approved {result.Changed.Count}; skipped {result.Skipped.Count} (only Categorized rows with balanced splits can be approved).";
    }

    /// <summary>Del: exclude the selection (exported rows are skipped).</summary>
    [RelayCommand(CanExecute = nameof(HasTargets))]
    public void ExcludeSelected()
    {
        var ids = Targets.Where(r => r.Status != TxnStatus.Excluded).Select(r => r.Id).ToList();
        Run("Exclude", ids, () => _svc.Txns.Exclude(ids).Changed);
    }

    public void SetCategory(IReadOnlyList<TxnRowViewModel> rows, CategoryOption? category)
    {
        var ids = rows.Where(r => !r.IsLocked).Select(r => r.Id).ToList();
        var label = category is null ? "Clear category" : $"Set category {category.FullName}";
        Run(label, ids, () => _svc.Txns.SetCategory(ids, category?.Id).Changed);
    }

    /// <summary>Grid category edit: applies to the whole selection when the edited row is part of a multi-row selection.</summary>
    public void SetCategoryFromGrid(TxnRowViewModel row, CategoryOption? category)
    {
        var rows = _selection.Count > 1 && _selection.Contains(row) ? _selection : [row];
        if (rows.Count == 1 && row.Txn.CategoryId == category?.Id && !row.HasSplits) return;
        SetCategory(rows, category);
    }

    [RelayCommand(CanExecute = nameof(CanApplyBulkCategory))]
    private void ApplyBulkCategory() => SetCategory(Targets, BulkCategory);

    private bool CanApplyBulkCategory() => Targets.Count > 0 && BulkCategory is not null;
    partial void OnBulkCategoryChanged(CategoryOption? value) => ApplyBulkCategoryCommand.NotifyCanExecuteChanged();

    /// <summary>Called from the grid's payee cell while the edit is committing, so the save is posted to run after it.</summary>
    public void SetPayee(TxnRowViewModel row, string payee)
    {
        var t = row.Txn;
        void Save() => Run("Edit payee", [row.Id], () => [_svc.Txns.SetOverrides(t.Id, t.OvrDate, payee, t.OvrMemo, t.OvrCheckNum)]);
        if (_ui is null) Save(); else _ui.Post(_ => Save(), null);
    }

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public void SaveDetails(TxnRowViewModel row, DateOnly? date, string? payee, string? memo, string? checkNum, long? nameId, string? cls)
    {
        Run("Edit details", [row.Id], () =>
        {
            _svc.Txns.SetOverrides(row.Id, date, payee, memo, checkNum);
            if (nameId != row.Txn.QbNameId) _svc.Txns.SetName(row.Id, nameId);
            return [_svc.Txns.SetClass(row.Id, cls)];
        });
    }

    public void SaveSplits(TxnRowViewModel row, IReadOnlyList<TxnSplit> splits) =>
        Run(splits.Count == 0 ? "Remove splits" : "Edit splits", [row.Id], () => [_svc.Txns.SetSplits(row.Id, splits)]);

    public void Unapprove(TxnRowViewModel row) => Run("Unapprove", [row.Id], () => [_svc.Txns.Unready(row.Id)]);
    public void Include(TxnRowViewModel row) => Run("Include", [row.Id], () => [_svc.Txns.Include(row.Id)]);
    public void NotADuplicate(TxnRowViewModel row) => Run("Not a duplicate", [row.Id], () => [_svc.Txns.NotADuplicate(row.Id)]);
    public void ConfirmDuplicate(TxnRowViewModel row) => Run("Confirm duplicate", [row.Id], () => [_svc.Txns.ConfirmDuplicate(row.Id)]);

    [RelayCommand(CanExecute = nameof(HasTargets))]
    public void RememberRule()
    {
        var rows = Targets;
        if (rows.Count == 0) return;
        var vm = RuleEditorViewModel.FromRows(rows, Lookups, _accounts);
        if (!_dialogs.ShowRuleEditor(vm)) return;
        try
        {
            var rule = vm.BuildRule();
            var result = _svc.Txns.SaveRuleAndApply(rule);
            PushUndo(new UndoEntry($"Remember rule \"{rule.Pattern}\"", result.ChangedBefore, rule.Id));
            ReloadChanged(result.ChangedBefore.Select(b => b.TxnId));
            StatusMessage = $"Rule \"{rule.Pattern}\" saved; applied to {result.ChangedBefore.Count} uncategorized row(s). Ctrl+Z removes it.";
        }
        catch (Exception ex) when (IsUserError(ex))
        {
            _dialogs.Error(ex.Message);
        }
    }

    // ================================================================== import / lists / accounts

    [RelayCommand]
    private void ImportInbox()
    {
        var files = _svc.Import.ScanInbox();
        if (files.Count == 0)
        {
            _dialogs.Info($"No .qbo/.qfx/.ofx files in {_svc.Paths.Inbox}.");
            return;
        }
        var results = files.Select(f =>
        {
            try { return _svc.Import.ImportFile(f); }
            catch (Exception ex) when (IsUserError(ex))
            {
                return new FileImportResult(Path.GetFileName(f), FileImportStatus.Failed, [], [], ex.Message, null);
            }
        }).ToList();
        AfterImport(results);
    }

    [RelayCommand]
    private void ImportFile()
    {
        var path = _dialogs.OpenFile("Import bank download", "Bank downloads (*.qbo;*.qfx;*.ofx)|*.qbo;*.qfx;*.ofx|All files (*.*)|*.*");
        if (path is null) return;
        try
        {
            // Copy into the inbox so the original stays where it was and the archive gets a copy.
            var dest = Path.Combine(_svc.Paths.Inbox, Path.GetFileName(path));
            if (!Path.GetFullPath(path).Equals(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 2; File.Exists(dest); i++)
                    dest = Path.Combine(_svc.Paths.Inbox, $"{Path.GetFileNameWithoutExtension(path)}_{i}{Path.GetExtension(path)}");
                File.Copy(path, dest);
            }
            AfterImport([_svc.Import.ImportFile(dest)]);
        }
        catch (Exception ex) when (IsUserError(ex))
        {
            _dialogs.Error(ex.Message);
        }
    }

    private void AfterImport(IReadOnlyList<FileImportResult> results)
    {
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            sb.AppendLine($"{r.FileName}: {r.Status switch
            {
                FileImportStatus.Imported => "imported",
                FileImportStatus.AlreadyImported => "already imported (identical file) — archived, nothing added",
                _ => "FAILED — left in inbox",
            }}");
            if (r.Error is not null) sb.AppendLine($"    {r.Error}");
            foreach (var st in r.Statements)
            {
                sb.AppendLine($"    {st.AccountDisplay}{(st.NewAccount ? " (new account)" : "")}: {st.Inserted} added, " +
                              $"{st.SkippedExisting} already had, {st.FlaggedDuplicates} possible duplicates, " +
                              $"{st.Categorized} auto-categorized, {st.Excluded} auto-excluded");
            }
            foreach (var w in r.Warnings) sb.AppendLine($"    warning: {w}");
        }

        foreach (var rec in results.Select(r => r.Reconcile).OfType<ReconcileSummary>().Where(r => r != ReconcileSummary.Empty))
            sb.AppendLine($"QuickBooks register: {rec.Matched} already in QB, {rec.Ambiguous} ambiguous; transfers paired: {rec.Paired}; held for Receive Payments: {rec.Held}");

        ClearUndo(); // matching may have changed rows that earlier undo entries describe
        ReloadLookups();
        RebuildTree();
        RefreshInboxCount();
        int added = results.SelectMany(r => r.Statements).Sum(s => s.Inserted);
        int dups = results.SelectMany(r => r.Statements).Sum(s => s.FlaggedDuplicates);
        StatusMessage = $"Imported {results.Count(r => r.Status == FileImportStatus.Imported)} file(s): {added} rows added" +
                        (dups > 0 ? $", {dups} possible duplicates to review." : ".");
        _dialogs.ShowText("Import results", sb.ToString());
        PromptUnmappedAccounts();
    }

    [RelayCommand]
    private void ImportLists()
    {
        var path = _dialogs.OpenFile("Import QuickBooks lists (File > Utilities > Export > Lists to IIF Files)", "IIF files (*.iif)|*.iif|All files (*.*)|*.*");
        if (path is null) return;
        try
        {
            var r = _svc.Lists.Import(File.ReadAllBytes(path));
            ReloadLookups();
            LoadRows();
            _dialogs.Info($"Accounts: {r.AccountsAdded} added, {r.AccountsUpdated} updated, {r.AccountsDeactivated} marked inactive.\n" +
                          $"Names: {r.NamesAdded} added, {r.NamesUpdated} updated, {r.NamesDeactivated} marked inactive.\n" +
                          $"Classes: {r.Classes}." +
                          (r.Warnings.Count > 0 ? "\n\n" + string.Join("\n", r.Warnings.Take(10)) : ""), "QuickBooks lists imported");
            PromptUnmappedAccounts();
        }
        catch (Exception ex) when (IsUserError(ex))
        {
            _dialogs.Error(ex.Message);
        }
    }

    private void PromptUnmappedAccounts()
    {
        var unmapped = _accounts.Values.Where(a => a.QbAccountName is null).ToList();
        if (unmapped.Count == 0) return;
        if (!Lookups.HasQbLists)
        {
            _dialogs.Info($"{unmapped.Count} account(s) are not mapped to QuickBooks yet.\n\nImport your QuickBooks lists (File > Import QuickBooks lists), then pick the QuickBooks bank or credit card account for each.");
            return;
        }
        ShowAccountMapping(unmapped, "New accounts: pick the QuickBooks account for each");
    }

    [RelayCommand]
    private void EditAccounts()
    {
        if (_accounts.Count == 0) { _dialogs.Info("No accounts yet. Import a bank download first."); return; }
        ShowAccountMapping(_accounts.Values.OrderBy(a => a.DisplayName).ToList(), "Accounts");
    }

    private void ShowAccountMapping(IReadOnlyList<BankAccount> accounts, string title)
    {
        var vm = new AccountMappingViewModel(title, accounts, Lookups);
        if (!_dialogs.ShowAccountMapping(vm)) return;

        var errors = new List<string>();
        foreach (var row in vm.Rows.Where(r => r.IsChanged))
        {
            try { _svc.Accounts.Update(row.Account.Id, row.DisplayName, row.SelectedQbAccount, row.InvertSign); }
            catch (Exception ex) when (IsUserError(ex)) { errors.Add($"{row.Account.DisplayName}: {ex.Message}"); }
        }
        if (errors.Count > 0) _dialogs.Error(string.Join("\n", errors));
        ReloadLookups();
        RebuildTree();
    }

    // ================================================================== QuickBooks register, learning, names, audit

    [RelayCommand]
    private void ImportRegister()
    {
        var path = _dialogs.OpenFile("Import QB register (Reports > Custom Reports > Transaction Detail, saved as CSV)",
            "CSV files (*.csv)|*.csv|All files (*.*)|*.*");
        if (path is null) return;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var info = _svc.Qb.Inspect(bytes);
            long? accountId = null;
            if (!info.HasAccountColumn)
            {
                var mapped = _accounts.Values.Where(a => a.QbAccountName is not null).OrderBy(a => a.DisplayName).ToList();
                if (mapped.Count == 0) { _dialogs.Info("Map your bank accounts to QuickBooks accounts first (Settings > Accounts)."); return; }
                accountId = _dialogs.ChooseAccount(mapped,
                    $"This report has no Account column ({info.Rows} rows, {info.From:MM/dd/yyyy}–{info.To:MM/dd/yyyy}). Which account's register is it?");
                if (accountId is null) return;
            }

            var r = _svc.Qb.ImportRegister(bytes, Path.GetFileName(path), accountId);
            var sb = new StringBuilder();
            sb.AppendLine($"{r.FileName}: {r.Rows} rows · {r.Added} new · {r.Unchanged} already known · {r.Removed} removed (changed or deleted in QB)");
            foreach (var a in r.Accounts)
                sb.AppendLine($"    {a.QbAccount}: {a.Rows} rows → {a.BankAccount ?? "no mapped bank account (kept for learning only)"}");
            sb.AppendLine();
            sb.AppendLine($"Already in QuickBooks: {r.Reconcile.Matched} matched now");
            sb.AppendLine($"Ambiguous (pick a match): {r.Reconcile.Ambiguous}");
            if (r.Reconcile.AmbiguityCleared > 0) sb.AppendLine($"Ambiguity resolved: {r.Reconcile.AmbiguityCleared}");
            sb.AppendLine($"Transfers paired: {r.Reconcile.Paired}  ·  Receive-payment holds: {r.Reconcile.Held} new, {r.Reconcile.Released} released");
            foreach (var w in r.Warnings.Take(10)) sb.AppendLine($"warning: {w}");

            ClearUndo();
            ReloadLookups();
            RebuildTree();
            StatusMessage = $"QB register imported: {r.Reconcile.Matched} matched, {r.Reconcile.Ambiguous} ambiguous.";
            _dialogs.ShowText("QB register import", sb.ToString());
            if (_dialogs.Confirm("Review payee and account suggestions learned from QuickBooks now?", "Learn from QuickBooks"))
                LearnFromQb();
        }
        catch (Exception ex) when (IsUserError(ex) || ex is Core.Qb.QbRegisterFormatException)
        {
            _dialogs.Error(ex.Message);
        }
    }

    [RelayCommand]
    private void LearnFromQb()
    {
        if (_svc.Qb.RegisterRowCount() == 0)
        {
            _dialogs.Info("Import a QuickBooks register (File > Import QB register) first. A long history file (e.g. two years, all bank and card accounts) teaches the most.");
            return;
        }
        var vm = new LearningViewModel(_svc, _dialogs);
        _dialogs.ShowLearning(vm);
        if (vm.Result is null) return;
        ClearUndo();
        ReloadLookups();
        RebuildTree();
        StatusMessage = $"Learned {vm.Result.RulesCreated} rules ({vm.Result.RulesUpdated} updated), {vm.Result.AliasesCreated} aliases; {vm.Result.RowsRecategorized} rows categorized.";
    }

    [RelayCommand]
    private void ShowNames()
    {
        var vm = new NamesViewModel(_svc, _dialogs);
        _dialogs.ShowNames(vm);
        if (!vm.Changed) return;
        ClearUndo();
        ReloadLookups();
        RebuildTree();
    }

    [RelayCommand]
    private void ShowAuditLog() => _dialogs.ShowAudit(new AuditViewModel(_svc));

    public void NotInQb(TxnRowViewModel row) => Run("Not in QB", [row.Id], () => [_svc.Txns.NotInQb(row.Id)]);

    public void PickMatch(TxnRowViewModel row)
    {
        try
        {
            var options = _svc.Txns.MatchOptions(row.Id);
            if (options.Count == 0) { _dialogs.Info("No unmatched QuickBooks rows with this amount are left for this account."); return; }
            var vm = new PickMatchViewModel(row, options);
            if (!_dialogs.ShowPickMatch(vm) || vm.Selected is null) return;
            Run("Pick match", [row.Id], () => [_svc.Txns.PickMatch(row.Id, vm.Selected.Row.Id)]);
        }
        catch (Exception ex) when (IsUserError(ex)) { _dialogs.Error(ex.Message); }
    }

    public void Unpair(TxnRowViewModel row)
    {
        var ids = new[] { row.Id, row.Txn.PairedTxnId ?? row.Id }.Distinct().ToList();
        if (Run("Unpair transfer", ids, () => _svc.Txns.Unpair(row.Id)))
            ReloadChanged(ids);
    }

    public void SaveTemplate(TxnRowViewModel row)
    {
        if (_dialogs.AskTemplate() is not { } answer) return;
        try
        {
            var t = _svc.Templates.SaveFromTxn(row.Id, answer.Name, answer.Kind);
            ReloadLookups();
            StatusMessage = $"Template \"{t.Name}\" saved ({t.Lines.Count} lines, {answer.Kind.ToString().ToLowerInvariant()}).";
        }
        catch (Exception ex) when (IsUserError(ex)) { _dialogs.Error(ex.Message); }
    }

    public void DeleteTemplate(SplitTemplate template)
    {
        if (!_dialogs.Confirm($"Delete split template \"{template.Name}\"? Rules that use it will stop applying it.")) return;
        _svc.Templates.Delete(template.Id);
        ReloadLookups();
    }

    // ================================================================== export

    [RelayCommand]
    private void Export()
    {
        var preview = _svc.Export.PreviewReady();
        var vm = new ExportViewModel(preview, _svc, _dialogs);
        _dialogs.ShowExport(vm);
        if (vm.Result is null) return;
        ClearUndo(); // snapshots of exported rows can no longer be restored
        RebuildTree();
        StatusMessage = $"Exported {vm.Result.Batch.TxnCount} transactions to {vm.Result.IifPath}.";
    }

    [RelayCommand]
    private void ShowBatches()
    {
        var vm = new BatchesViewModel(_svc, _dialogs);
        _dialogs.ShowBatches(vm);
        if (!vm.Changed) return;
        ClearUndo();
        RebuildTree();
    }

    [RelayCommand]
    private void Refresh()
    {
        ReloadLookups();
        RebuildTree();
        RefreshInboxCount();
        StatusMessage = "Refreshed.";
    }

    [RelayCommand]
    private void OpenDataFolder() => _dialogs.OpenFolder(_svc.Paths.Root);

    // ================================================================== layout persistence

    public UiLayout LoadLayout()
    {
        try
        {
            using var s = _svc.Db.Open();
            return s.Settings.Get(LayoutKey) is { } json ? JsonSerializer.Deserialize<UiLayout>(json) ?? new() : new();
        }
        catch (JsonException) { return new(); }
    }

    public void SaveLayout(UiLayout layout)
    {
        using var s = _svc.Db.Open();
        s.Settings.Set(LayoutKey, JsonSerializer.Serialize(layout));
    }
}
