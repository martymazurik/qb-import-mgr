using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QbImportMgr.Core.Domain;

namespace QbImportMgr.App.ViewModels;

public sealed partial class SplitLineViewModel : ObservableObject
{
    [ObservableProperty] private CategoryOption? _category;
    [ObservableProperty] private string _amountText = "";
    [ObservableProperty] private string? _memo;

    /// <summary>Entered amount in cents, in the direction of the transaction; null if unparseable.</summary>
    public long? EnteredCents => Money.TryParseCents(AmountText, out var c) ? c : null;
}

/// <summary>Right-hand panel: raw bank values, overrides, duplicate resolution, split editor.</summary>
public sealed partial class DetailViewModel(MainViewModel owner, AppServices svc) : ObservableObject
{
    private bool _loading;

    [ObservableProperty] private TxnRowViewModel? _row;
    public bool HasRow => Row is not null;
    public Lookups Lookups => owner.Lookups;

    // raw bank values (read-only)
    [ObservableProperty] private string _accountText = "";
    [ObservableProperty] private string _sourceFile = "";
    [ObservableProperty] private string _rawFitId = "";
    [ObservableProperty] private string _rawType = "";
    [ObservableProperty] private string _rawPosted = "";
    [ObservableProperty] private string _rawUser = "";
    [ObservableProperty] private string _rawAmount = "";
    [ObservableProperty] private string _signedAmount = "";
    [ObservableProperty] private string _rawName = "";
    [ObservableProperty] private string _rawMemo = "";
    [ObservableProperty] private string _rawCheckNum = "";

    // editable
    [ObservableProperty] private string _dateText = "";
    [ObservableProperty] private string _payeeText = "";
    [ObservableProperty] private string _memoText = "";
    [ObservableProperty] private string _checkNumText = "";
    [ObservableProperty] private NameOption? _selectedName;
    [ObservableProperty] private string _classText = "";
    [ObservableProperty] private bool _isDirty;

    // state
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private bool _isDuplicate;
    [ObservableProperty] private string _duplicateInfo = "";
    [ObservableProperty] private bool _isExcluded;
    [ObservableProperty] private bool _isReady;

    // splits
    public ObservableCollection<SplitLineViewModel> Splits { get; } = [];
    [ObservableProperty] private string _remainderText = "";
    [ObservableProperty] private bool _isBalanced = true;
    [ObservableProperty] private bool _splitsDirty;

    partial void OnRowChanged(TxnRowViewModel? value) => OnPropertyChanged(nameof(HasRow));
    partial void OnDateTextChanged(string value) => MarkDirty();
    partial void OnPayeeTextChanged(string value) => MarkDirty();
    partial void OnMemoTextChanged(string value) => MarkDirty();
    partial void OnCheckNumTextChanged(string value) => MarkDirty();
    partial void OnSelectedNameChanged(NameOption? value) => MarkDirty();
    partial void OnClassTextChanged(string value) => MarkDirty();

    private void MarkDirty()
    {
        if (_loading) return;
        IsDirty = true;
        SaveEditsCommand.NotifyCanExecuteChanged();
    }

    public void Load(TxnRowViewModel? row)
    {
        _loading = true;
        try
        {
            Row = row;
            foreach (var l in Splits) l.PropertyChanged -= OnSplitChanged;
            Splits.Clear();
            if (row is null) return;
            var t = row.Txn;

            using var s = svc.Db.Open();
            var stmt = s.Files.GetStatement(t.QboStatementId);
            SourceFile = stmt is null ? "" : s.Files.StatementFileNames(t.BankAccountId).GetValueOrDefault(stmt.Id) ?? "";
            AccountText = row.Account;
            RawFitId = t.FitId ?? "(none)";
            RawType = t.TrnType;
            RawPosted = D(t.DtPosted);
            RawUser = t.DtUser is { } du ? D(du) : "";
            RawAmount = Money.FormatDisplay(t.AmountCents);
            SignedAmount = Money.FormatDisplay(t.SignedAmountCents) + (t.AmountCents != t.SignedAmountCents ? "  (sign inverted)" : "");
            RawName = t.Name ?? "";
            RawMemo = t.Memo ?? "";
            RawCheckNum = t.CheckNum ?? "";

            DateText = D(t.EffectiveDate);
            PayeeText = t.EffectivePayee;
            MemoText = t.EffectiveMemo;
            CheckNumText = t.EffectiveCheckNum;
            SelectedName = owner.Lookups.Name(t.QbNameId);
            ClassText = t.Class ?? "";
            IsDirty = false;

            StatusText = row.StatusText + (t.RuleId is { } rid ? $"  ·  rule #{rid}" : "");
            IsLocked = t.IsLocked;
            IsDuplicate = t.Status == TxnStatus.Duplicate;
            IsExcluded = t.Status == TxnStatus.Excluded;
            IsReady = t.Status == TxnStatus.Ready;
            DuplicateInfo = "";
            if (t.DuplicateOfTxnId is { } dupOf && s.Txns.Get(dupOf) is { } orig)
            {
                var origFile = s.Files.StatementFileNames(orig.BankAccountId).GetValueOrDefault(orig.QboStatementId) ?? "?";
                DuplicateInfo = $"Matches {D(orig.DtPosted)}  {orig.Name}  {Money.FormatDisplay(orig.SignedAmountCents)}  " +
                                $"FITID {orig.FitId ?? "(none)"}  [{orig.Status}]  from {origFile}";
            }

            foreach (var x in s.Splits.ForTxn(t.Id))
            {
                var line = new SplitLineViewModel
                {
                    Category = owner.Lookups.Category(x.CategoryId),
                    AmountText = (SplitMath.ToEnteredCents(x.AmountCents, t.SignedAmountCents) / 100m).ToString("0.00", CultureInfo.InvariantCulture),
                    Memo = x.Memo,
                };
                line.PropertyChanged += OnSplitChanged;
                Splits.Add(line);
            }
            SplitsDirty = false;
            Recalc();
        }
        finally
        {
            _loading = false;
            NotifyAll();
        }
    }

    private static string D(DateOnly d) => d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);

    private void NotifyAll()
    {
        SaveEditsCommand.NotifyCanExecuteChanged();
        ApproveCommand.NotifyCanExecuteChanged();
        UnapproveCommand.NotifyCanExecuteChanged();
        ExcludeCommand.NotifyCanExecuteChanged();
        IncludeCommand.NotifyCanExecuteChanged();
        NotADuplicateCommand.NotifyCanExecuteChanged();
        ConfirmDuplicateCommand.NotifyCanExecuteChanged();
        AddSplitCommand.NotifyCanExecuteChanged();
        SaveSplitsCommand.NotifyCanExecuteChanged();
        ClearSplitsCommand.NotifyCanExecuteChanged();
        RememberRuleCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------ splits

    private void OnSplitChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading) return;
        SplitsDirty = true;
        Recalc();
    }

    private void Recalc()
    {
        if (Row is null || Splits.Count == 0)
        {
            RemainderText = "";
            IsBalanced = true;
        }
        else
        {
            var entered = Splits.Select(l => l.EnteredCents).ToList();
            if (entered.Any(c => c is null))
            {
                RemainderText = "Remainder: enter a valid amount on every line";
                IsBalanced = false;
            }
            else
            {
                long rem = SplitMath.Remainder(Row.Txn.SignedAmountCents, entered.Select(c => c!.Value));
                RemainderText = $"Total {Money.FormatDisplay(Math.Abs(Row.Txn.SignedAmountCents))}  ·  Remainder {Money.FormatDisplay(rem)}";
                IsBalanced = rem == 0;
            }
        }
        ApproveCommand.NotifyCanExecuteChanged();
        SaveSplitsCommand.NotifyCanExecuteChanged();
    }

    private bool Editable => Row is not null && !Row.IsLocked;

    [RelayCommand(CanExecute = nameof(Editable))]
    private void AddSplit()
    {
        if (Row is null) return;
        if (Splits.Count == 0)
        {
            // Start from the current category with the full amount, then add a second line for the rest.
            AddLine(Lookups.Category(Row.Txn.CategoryId), Math.Abs(Row.Txn.SignedAmountCents));
            AddLine(null, 0);
        }
        else
        {
            var entered = Splits.Select(l => l.EnteredCents ?? 0);
            AddLine(null, Math.Max(0, SplitMath.Remainder(Row.Txn.SignedAmountCents, entered)));
        }
        SplitsDirty = true;
        Recalc();
    }

    private void AddLine(CategoryOption? category, long enteredCents)
    {
        var line = new SplitLineViewModel
        {
            Category = category,
            AmountText = (enteredCents / 100m).ToString("0.00", CultureInfo.InvariantCulture),
        };
        line.PropertyChanged += OnSplitChanged;
        Splits.Add(line);
    }

    [RelayCommand]
    private void RemoveSplit(SplitLineViewModel? line)
    {
        if (line is null || IsLocked) return;
        line.PropertyChanged -= OnSplitChanged;
        Splits.Remove(line);
        SplitsDirty = true;
        Recalc();
    }

    [RelayCommand(CanExecute = nameof(CanSaveSplits))]
    private void SaveSplits()
    {
        if (Row is null) return;
        if (Splits.Any(l => l.Category is null))
        {
            owner.StatusMessage = "Every split line needs a category.";
            return;
        }
        var list = Splits.Select(l => new TxnSplit
        {
            CategoryId = l.Category!.Id,
            AmountCents = SplitMath.ToStoredCents(l.EnteredCents!.Value, Row.Txn.SignedAmountCents),
            Memo = string.IsNullOrWhiteSpace(l.Memo) ? null : l.Memo.Trim(),
        }).ToList();
        owner.SaveSplits(Row, list);
    }

    private bool CanSaveSplits() => Editable && Splits.Count > 0 && SplitsDirty && Splits.All(l => l.EnteredCents is not null);

    [RelayCommand(CanExecute = nameof(CanClearSplits))]
    private void ClearSplits()
    {
        if (Row is null) return;
        if (Row.HasSplits) owner.SaveSplits(Row, []);
        else Load(Row);
    }

    private bool CanClearSplits() => Editable && Splits.Count > 0;

    // ------------------------------------------------------------------ edits and status

    [RelayCommand(CanExecute = nameof(CanSaveEdits))]
    private void SaveEdits()
    {
        if (Row is null) return;
        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(DateText))
        {
            if (!DateOnly.TryParseExact(DateText.Trim(), ["MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                owner.StatusMessage = "Date must be MM/DD/YYYY.";
                return;
            }
            date = d;
        }
        owner.SaveDetails(Row, date, PayeeText, MemoText, CheckNumText, SelectedName?.Id, ClassText);
    }

    private bool CanSaveEdits() => Editable && IsDirty;

    [RelayCommand]
    private void RevertEdits() => Load(Row);

    /// <summary>Ready is blocked while split edits are unsaved or don't balance.</summary>
    [RelayCommand(CanExecute = nameof(CanApprove))]
    private void Approve()
    {
        if (Row is not null) owner.Approve([Row]);
    }

    private bool CanApprove() => Row is { Status: TxnStatus.Categorized } && !SplitsDirty && IsBalanced;

    [RelayCommand(CanExecute = nameof(CanUnapprove))]
    private void Unapprove() { if (Row is not null) owner.Unapprove(Row); }
    private bool CanUnapprove() => Row is { Status: TxnStatus.Ready };

    [RelayCommand(CanExecute = nameof(CanExclude))]
    private void Exclude() { if (Row is not null) owner.ExcludeSelected(); }
    private bool CanExclude() => Editable && Row!.Status is not (TxnStatus.Excluded or TxnStatus.Duplicate);

    [RelayCommand(CanExecute = nameof(CanInclude))]
    private void Include() { if (Row is not null) owner.Include(Row); }
    private bool CanInclude() => Row is { Status: TxnStatus.Excluded };

    [RelayCommand(CanExecute = nameof(CanResolveDuplicate))]
    private void NotADuplicate() { if (Row is not null) owner.NotADuplicate(Row); }

    [RelayCommand(CanExecute = nameof(CanResolveDuplicate))]
    private void ConfirmDuplicate() { if (Row is not null) owner.ConfirmDuplicate(Row); }
    private bool CanResolveDuplicate() => Row is { Status: TxnStatus.Duplicate };

    [RelayCommand(CanExecute = nameof(HasRowCanRule))]
    private void RememberRule() => owner.RememberRule();
    private bool HasRowCanRule() => Row is not null;
}
