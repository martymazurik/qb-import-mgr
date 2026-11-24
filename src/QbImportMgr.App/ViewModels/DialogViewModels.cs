using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Export;
using QbImportMgr.Core.Rules;
using QbImportMgr.Data.Services;

namespace QbImportMgr.App.ViewModels;

// ====================================================================== rule editor

public sealed partial class RuleEditorViewModel : ObservableObject
{
    private readonly IReadOnlyList<TxnRowViewModel> _rows;

    private RuleEditorViewModel(IReadOnlyList<TxnRowViewModel> rows, Lookups lookups)
    {
        _rows = rows;
        Lookups = lookups;
    }

    public Lookups Lookups { get; }
    public IReadOnlyList<MatchKind> MatchKinds { get; } = Enum.GetValues<MatchKind>();
    public IReadOnlyList<RuleField> Fields { get; } = Enum.GetValues<RuleField>();
    public IReadOnlyList<SignFilter> Signs { get; } = Enum.GetValues<SignFilter>();
    public long? AccountId { get; private init; }
    public string AccountName { get; private init; } = "";
    public bool HasSingleAccount => AccountId is not null;
    public string Heading => _rows.Count == 1 ? $"From: {_rows[0].Txn.Name}" : $"From {_rows.Count} selected rows";

    [ObservableProperty] private string _pattern = "";
    [ObservableProperty] private MatchKind _matchKind;
    [ObservableProperty] private RuleField _field;
    [ObservableProperty] private SignFilter _sign;
    [ObservableProperty] private string _minText = "";
    [ObservableProperty] private string _maxText = "";
    [ObservableProperty] private bool _onlyThisAccount;
    [ObservableProperty] private CategoryOption? _category;
    [ObservableProperty] private NameOption? _name;
    [ObservableProperty] private string _classText = "";
    [ObservableProperty] private bool _autoExclude;
    [ObservableProperty] private SplitTemplate? _template;
    [ObservableProperty] private int _priority = 100;
    [ObservableProperty] private string _matchPreview = "";
    [ObservableProperty] private string _error = "";

    partial void OnPatternChanged(string value) => UpdatePreview();
    partial void OnMatchKindChanged(MatchKind value) => UpdatePreview();
    partial void OnFieldChanged(RuleField value) => UpdatePreview();
    partial void OnSignChanged(SignFilter value) => UpdatePreview();
    partial void OnMinTextChanged(string value) => UpdatePreview();
    partial void OnMaxTextChanged(string value) => UpdatePreview();

    public static RuleEditorViewModel FromRows(IReadOnlyList<TxnRowViewModel> rows, Lookups lookups, IReadOnlyDictionary<long, BankAccount> accounts)
    {
        var suggestions = rows.Select(r => RuleSuggester.Suggest(r.Txn)).ToList();
        var first = suggestions[0];
        var accountIds = rows.Select(r => r.Txn.BankAccountId).Distinct().ToList();
        var catIds = rows.Select(r => r.Txn.CategoryId).Where(c => c is not null).Distinct().ToList();
        var signs = suggestions.Select(s => s.Sign).Distinct().ToList();

        var vm = new RuleEditorViewModel(rows, lookups)
        {
            AccountId = accountIds.Count == 1 ? accountIds[0] : null,
            AccountName = accountIds.Count == 1 && accounts.TryGetValue(accountIds[0], out var a) ? a.DisplayName : "",
        };
        vm.Pattern = CommonPattern(suggestions.Select(s => s.Pattern).ToList());
        vm.MatchKind = MatchKind.Contains;
        vm.Field = RuleField.Payee;
        vm.Sign = signs.Count == 1 ? signs[0] : SignFilter.Any;
        vm.OnlyThisAccount = false;
        vm.Category = catIds.Count >= 1 ? lookups.Category(catIds[0]) : null;
        vm.Name = lookups.Name(first.QbNameId);
        vm.ClassText = first.Class ?? "";
        vm.AutoExclude = rows.All(r => r.Status == TxnStatus.Excluded);
        vm.UpdatePreview();
        return vm;
    }

    /// <summary>Longest common word prefix of the suggested patterns, or the first one if they share nothing.</summary>
    internal static string CommonPattern(IReadOnlyList<string> patterns)
    {
        var words = patterns.Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var common = new List<string>();
        for (int i = 0; words.All(w => w.Length > i) && words.All(w => w[i].Equals(words[0][i], StringComparison.OrdinalIgnoreCase)); i++)
            common.Add(words[0][i]);
        return common.Count > 0 ? string.Join(' ', common) : patterns[0];
    }

    private void UpdatePreview()
    {
        if (TextMatcher.Validate(MatchKind, Pattern) is { } err) { MatchPreview = err; return; }
        var rule = TryBuild(out _);
        if (rule is null) { MatchPreview = ""; return; }
        int n = _rows.Count(r => RuleEngine.Matches(rule, RuleInput.From(r.Txn)));
        MatchPreview = $"Matches {n} of the {_rows.Count} selected row(s). Applies to every uncategorized row that matches.";
    }

    private Rule? TryBuild(out string? error)
    {
        error = null;
        long? min = null, max = null;
        if (!string.IsNullOrWhiteSpace(MinText))
        {
            if (!Money.TryParseCents(MinText, out var m)) { error = "Minimum amount is not a number."; return null; }
            min = Math.Abs(m);
        }
        if (!string.IsNullOrWhiteSpace(MaxText))
        {
            if (!Money.TryParseCents(MaxText, out var m)) { error = "Maximum amount is not a number."; return null; }
            max = Math.Abs(m);
        }
        return new Rule
        {
            Priority = Priority,
            Field = Field,
            MatchKind = MatchKind,
            Pattern = Pattern.Trim(),
            Sign = Sign,
            MinCents = min,
            MaxCents = max,
            BankAccountId = OnlyThisAccount ? AccountId : null,
            CategoryId = AutoExclude || Template is not null ? null : Category?.Id,
            QbNameId = AutoExclude ? null : Name?.Id,
            Class = AutoExclude || string.IsNullOrWhiteSpace(ClassText) ? null : ClassText.Trim(),
            AutoExclude = AutoExclude,
            SplitTemplateId = AutoExclude ? null : Template?.Id,
            IsEnabled = true,
        };
    }

    /// <summary>Called by the dialog's Save button; false keeps the dialog open with Error set.</summary>
    public bool Validate()
    {
        Error = "";
        if (TextMatcher.Validate(MatchKind, Pattern) is { } err) { Error = err; return false; }
        if (TryBuild(out var e) is not { } r) { Error = e ?? "Invalid rule."; return false; }
        if (r.MinCents is { } mn && r.MaxCents is { } mx && mn > mx) { Error = "Minimum is greater than maximum."; return false; }
        if (!r.AutoExclude && r.CategoryId is null && r.QbNameId is null && r.Class is null && r.SplitTemplateId is null)
        {
            Error = "Pick a category or split template (or a name or class), or tick Auto-exclude.";
            return false;
        }
        return true;
    }

    public Rule BuildRule() => TryBuild(out var e) ?? throw new ArgumentException(e);
}

// ====================================================================== account mapping

public sealed partial class AccountMappingRow : ObservableObject
{
    public AccountMappingRow(BankAccount account, IReadOnlyList<string> options)
    {
        Account = account;
        Options = options;
        _displayName = account.DisplayName;
        _selectedQbAccount = account.QbAccountName;
        _invertSign = account.InvertSign;
    }

    public BankAccount Account { get; }
    public IReadOnlyList<string> Options { get; }
    public string Masked => Account.OfxAcctIdMasked;
    public string KindText => Account.Kind == AccountKind.Bank ? "Bank" : "Credit card";

    public string Institution
    {
        get
        {
            var parts = new List<string>();
            if (Account.BankId is { } b) parts.Add($"BANKID {b}");
            if (Account.FiId is { } f) parts.Add($"INTU.BID {f}");
            if (Account.FiOrg is { } o) parts.Add(o);
            return parts.Count > 0 ? string.Join("  ·  ", parts) : "(no institution id in download)";
        }
    }

    public string OptionsHint => Options.Count > 0 ? "" :
        $"No QuickBooks {(Account.Kind == AccountKind.Bank ? "BANK" : "CCARD")} accounts in the imported lists.";

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string? _selectedQbAccount;
    [ObservableProperty] private bool _invertSign;

    public bool IsChanged =>
        DisplayName.Trim() != Account.DisplayName || SelectedQbAccount != Account.QbAccountName || InvertSign != Account.InvertSign;
}

public sealed class AccountMappingViewModel(string title, IReadOnlyList<BankAccount> accounts, Lookups lookups)
{
    public string Title { get; } = title;
    public IReadOnlyList<AccountMappingRow> Rows { get; } =
        accounts.Select(a => new AccountMappingRow(a, lookups.QbAccountsFor(a.Kind))).ToList();
}

// ====================================================================== export

public sealed record ExportAccountRow(string AccountDisplay, string? QbAccount, int Count, string Net);

public sealed partial class ExportViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly IDialogService _dialogs;

    public ExportViewModel(ExportPreview preview, AppServices svc, IDialogService dialogs)
    {
        _svc = svc;
        _dialogs = dialogs;
        Preview = preview;
        Accounts = preview.ByAccount.Select(a => new ExportAccountRow(a.AccountDisplay, a.QbAccount, a.Count, Money.FormatDisplay(a.NetCents))).ToList();
        Issues = preview.Validation.Issues.OrderByDescending(i => i.Severity).ToList();
        TotalText = $"{preview.Items.Count} transactions  ·  net {Money.FormatDisplay(preview.TotalCents)}";
        using var s = svc.Db.Open();
        ClearText = s.Settings.GetIifClear() ? "Transactions will be marked cleared (CLEAR=Y)." : "Transactions will be left uncleared (CLEAR=N).";
    }

    public ExportPreview Preview { get; }
    public IReadOnlyList<ExportAccountRow> Accounts { get; }
    public IReadOnlyList<ValidationIssue> Issues { get; }
    public string TotalText { get; }
    public string ClearText { get; }
    public bool IsFirstExport => Preview.IsFirstExport;
    public bool HasIssues => Issues.Count > 0;
    public bool CanExportNow => Preview.Validation.CanExport && Result is null;
    public string ErrorSummary => Preview.Validation.CanExport ? "" :
        $"{Preview.Validation.Errors.Count()} problem(s) must be fixed before exporting.";

    [ObservableProperty] private ExportResult? _result;
    [ObservableProperty] private string _resultText = "";

    public event EventHandler? RequestClose;

    [RelayCommand(CanExecute = nameof(CanExportNow))]
    private void Export()
    {
        try
        {
            Result = _svc.Export.Export(Preview.Items.Select(i => i.TxnId));
            ResultText = $"Wrote {Result.IifPath}\nBackup: {Result.BackupPath}\n\n" +
                         "In QuickBooks: File > Utilities > Import > IIF Files, and pick this file.";
            OnPropertyChanged(nameof(CanExportNow));
            ExportCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) when (MainViewModel.IsUserError(ex))
        {
            _dialogs.Error(ex.Message, "Export failed");
        }
    }

    [RelayCommand]
    private void OpenIifFolder() => _dialogs.OpenFolder(_svc.Paths.Iif);

    [RelayCommand]
    private void Close() => RequestClose?.Invoke(this, EventArgs.Empty);
}

// ====================================================================== batches / void

public sealed record BatchRow(ExportBatch Batch)
{
    public long Id => Batch.Id;
    public string Created => Batch.CreatedUtc.ToLocalTime().ToString("MM/dd/yyyy HH:mm", CultureInfo.InvariantCulture);
    public int Count => Batch.TxnCount;
    public string Total => Money.FormatDisplay(Batch.TotalCents);
    public string File => Path.GetFileName(Batch.IifPath);
    public string State => Batch.VoidedUtc is { } v ? $"Voided {v.ToLocalTime():MM/dd/yyyy}" : "Exported";
    public bool IsVoided => Batch.VoidedUtc is not null;
}

public sealed partial class BatchesViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly IDialogService _dialogs;

    public BatchesViewModel(AppServices svc, IDialogService dialogs)
    {
        _svc = svc;
        _dialogs = dialogs;
        Reload();
    }

    public ObservableCollection<BatchRow> Batches { get; } = [];
    public bool Changed { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VoidCommand))]
    private BatchRow? _selected;

    private void Reload()
    {
        Batches.Clear();
        foreach (var b in _svc.Export.Batches()) Batches.Add(new BatchRow(b));
    }

    [RelayCommand(CanExecute = nameof(CanVoid))]
    private void Void()
    {
        if (Selected is not { } b) return;
        if (!_dialogs.Confirm(
                $"Void batch {b.Id} ({b.Count} transactions, {b.Total})?\n\n" +
                "This does NOT change QuickBooks. Delete those transactions in QuickBooks first, " +
                "otherwise re-exporting will post them twice.\n\nThe rows return to Ready and the IIF file is renamed *.voided.",
                "Void export batch"))
            return;
        try
        {
            int n = _svc.Export.VoidBatch(b.Id);
            Changed = true;
            Reload();
            _dialogs.Info($"Batch {b.Id} voided; {n} transactions are Ready again.");
        }
        catch (Exception ex) when (MainViewModel.IsUserError(ex))
        {
            _dialogs.Error(ex.Message);
        }
    }

    private bool CanVoid() => Selected is { IsVoided: false };

    [RelayCommand]
    private void OpenIifFolder() => _dialogs.OpenFolder(_svc.Paths.Iif);
}
