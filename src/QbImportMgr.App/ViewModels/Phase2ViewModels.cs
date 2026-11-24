using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Qb;
using QbImportMgr.Data.Services;

namespace QbImportMgr.App.ViewModels;

// ====================================================================== pick match

public sealed record MatchOptionRow(QbMatchOption Option)
{
    public QbRegisterTxn Row => Option.Row;
    public string Date => Row.TxnDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
    public string Type => Row.TxnType;
    public string? Num => Row.Num;
    public string? Name => Row.Name;
    public string? Memo => Row.Memo;
    public string? Split => Row.SplitAccount;
    public string Why => Option.CheckNumberMatch ? "check number" : Option.DayDiff == 0 ? "same day" : $"{Option.DayDiff} day(s) apart";
    public string Similarity => $"{Option.NameSimilarity:P0}";
}

public sealed partial class PickMatchViewModel(TxnRowViewModel row, IReadOnlyList<QbMatchOption> options) : ObservableObject
{
    public string Heading { get; } = $"{row.Date:MM/dd/yyyy}  {row.Txn.Name}  {Money.FormatDisplay(row.Txn.SignedAmountCents)}  ({row.Account})";
    public IReadOnlyList<MatchOptionRow> Options { get; } = options.Select(o => new MatchOptionRow(o)).ToList();
    [ObservableProperty] private MatchOptionRow? _selectedRow;
    public QbMatchOption? Selected => SelectedRow?.Option;
}

// ====================================================================== learning review

public sealed partial class ProposalRow : ObservableObject
{
    public ProposalRow(ProposalView view)
    {
        View = view;
        _pattern = view.Pattern;
        _state = view.State;
        _note = view.Note;
    }

    public ProposalView View { get; }
    public LearnProposal Proposal => View.Proposal;
    public string QbName => Proposal.QbName;
    public string Account => Proposal.Account;
    public int Count => Proposal.Count;
    public string Agreement => $"{Proposal.Agreement:P0}";
    public double AgreementValue => Proposal.Agreement;
    public string Basis => Proposal.Basis == ProposalBasis.MatchedBankText ? "Bank text" : "QB name";
    public int BankHits => View.BankHits;
    public string Samples => string.Join("  |  ", Proposal.Samples);
    public bool CanAccept => State is ProposalState.New or ProposalState.UpdatesLearned;

    [ObservableProperty] private string _pattern;
    [ObservableProperty] private bool _accept;
    [ObservableProperty] private ProposalState _state;
    [ObservableProperty] private string _note;

    public string StateText => State switch
    {
        ProposalState.New => "New",
        ProposalState.UpdatesLearned => "Updates learned rule",
        ProposalState.Covered => "Already covered",
        ProposalState.Conflict => "Conflict",
        _ => "Account missing",
    };

    partial void OnStateChanged(ProposalState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CanAccept));
        if (!CanAccept) Accept = false;
    }
}

public sealed partial class LearningViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly IDialogService _dialogs;

    public LearningViewModel(AppServices svc, IDialogService dialogs)
    {
        _svc = svc;
        _dialogs = dialogs;
        var preview = svc.Qb.Preview();
        Summary = $"{preview.RegisterRows} QB register rows · {preview.Profiles.Count} payee profiles · {preview.MatchedPairs} matched bank↔QB pairs";
        AllRows = preview.Proposals.Select(p => new ProposalRow(p)).ToList();
        foreach (var r in AllRows)
            r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProposalRow.Accept)) UpdateStatus(); };
        Profiles = preview.Profiles;
        ApplyFilter();
    }

    /// <summary>Accepts every acceptable suggestion meeting the count and agreement thresholds.</summary>
    [RelayCommand]
    private void AcceptAboveThreshold()
    {
        SelectAboveThreshold();
        if (AllRows.Any(r => r.Accept)) AcceptSelected();
        else _dialogs.Info($"No suggestions meet {MinCount}+ occurrences and {MinAgreementPercent}%+ agreement.");
    }

    public string Summary { get; }
    public IReadOnlyList<ProposalRow> AllRows { get; }
    public IReadOnlyList<PayeeProfile> Profiles { get; }
    public ObservableCollection<ProposalRow> Rows { get; } = [];
    public LearnAcceptResult? Result { get; private set; }
    public event EventHandler? RequestClose;

    [ObservableProperty] private int _minCount = PayeeLearner.DefaultMinCount;
    [ObservableProperty] private int _minAgreementPercent = (int)(PayeeLearner.DefaultMinAgreement * 100);
    [ObservableProperty] private bool _hideCovered = true;
    [ObservableProperty] private string _status = "";

    partial void OnHideCoveredChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (var r in AllRows.Where(r => !HideCovered || r.State != ProposalState.Covered)) Rows.Add(r);
        UpdateStatus();
    }

    private void UpdateStatus() =>
        Status = $"{Rows.Count} shown · {AllRows.Count(r => r.Accept)} selected · {AllRows.Count(r => r.State == ProposalState.Conflict)} conflicts with hand-made rules";

    [RelayCommand]
    private void SelectAboveThreshold()
    {
        foreach (var r in AllRows)
            r.Accept = r.CanAccept && r.Proposal.MeetsThreshold(MinCount, MinAgreementPercent / 100.0);
        UpdateStatus();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var r in AllRows) r.Accept = false;
        UpdateStatus();
    }

    [RelayCommand]
    private void AcceptSelected()
    {
        var chosen = AllRows.Where(r => r.Accept).ToList();
        if (chosen.Count == 0) { _dialogs.Info("Tick the suggestions to accept, or use \"Select above threshold\"."); return; }
        try
        {
            Result = _svc.Qb.Accept(chosen.Select(r => (r.Proposal, r.Pattern)));
            var msg = $"Rules: {Result.RulesCreated} created, {Result.RulesUpdated} updated.\nAliases: {Result.AliasesCreated} created, {Result.AliasesUpdated} updated.\n" +
                      $"{Result.RowsRecategorized} uncategorized row(s) picked up a category or payee.";
            if (Result.Skipped.Count > 0) msg += "\n\nSkipped:\n" + string.Join("\n", Result.Skipped.Take(15));
            _dialogs.Info(msg, "Learned from QuickBooks");
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (MainViewModel.IsUserError(ex)) { _dialogs.Error(ex.Message); }
    }
}

// ====================================================================== names (invoiced customers)

public sealed partial class NameRow(QbName name, Action<NameRow, bool> onInvoicedChanged) : ObservableObject
{
    public QbName Name { get; } = name;
    public string Display => Name.Name;
    public string Type => Name.NameType.ToString();
    public bool IsActive => Name.IsActive;

    [ObservableProperty] private bool _isInvoiced = name.IsInvoiced;
    partial void OnIsInvoicedChanged(bool value) => onInvoicedChanged(this, value);
}

public sealed partial class NamesViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly IDialogService _dialogs;
    private readonly List<NameRow> _all;

    public NamesViewModel(AppServices svc, IDialogService dialogs)
    {
        _svc = svc;
        _dialogs = dialogs;
        using var s = svc.Db.Open();
        _all = s.Names.GetAll().Select(n => new NameRow(n, OnInvoicedChanged)).ToList();
        ApplyFilter();
    }

    public ObservableCollection<NameRow> Rows { get; } = [];
    public bool Changed { get; private set; }

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _customersOnly = true;
    [ObservableProperty] private string _status = "Tick customers you invoice. Their deposits wait for Receive Payments in QB instead of exporting.";

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnCustomersOnlyChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (var r in _all.Where(r => (!CustomersOnly || r.Name.NameType == QbNameType.Customer) &&
                                          (Filter.Length == 0 || r.Display.Contains(Filter, StringComparison.OrdinalIgnoreCase))))
            Rows.Add(r);
    }

    private void OnInvoicedChanged(NameRow row, bool invoiced)
    {
        try
        {
            var rec = _svc.Txns.SetInvoiced(row.Name.Id, invoiced);
            Changed = true;
            Status = $"{row.Display}: {(invoiced ? "invoiced" : "not invoiced")}. Deposits held: {rec.Held}, released: {rec.Released}.";
        }
        catch (Exception ex) when (MainViewModel.IsUserError(ex)) { _dialogs.Error(ex.Message); }
    }
}

// ====================================================================== audit log

public sealed record AuditRow(AuditLogEntry Entry)
{
    public string When => Entry.Utc.ToLocalTime().ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    public string Entity => Entry.Entity;
    public long EntityId => Entry.EntityId;
    public string Action => Entry.Action;
    public string? Detail => Entry.Detail;
}

public sealed partial class AuditViewModel : ObservableObject
{
    private const string Any = "(any)";
    private readonly AppServices _svc;

    public AuditViewModel(AppServices svc)
    {
        _svc = svc;
        var (entities, actions) = svc.Audit.Filters();
        Entities = [Any, .. entities];
        Actions = [Any, .. actions];
        _from = DateTime.Today.AddDays(-30);
        _to = DateTime.Today;
        Search();
    }

    public IReadOnlyList<string> Entities { get; }
    public IReadOnlyList<string> Actions { get; }
    public ObservableCollection<AuditRow> Rows { get; } = [];

    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private string _entity = Any;
    [ObservableProperty] private string _action = Any;
    [ObservableProperty] private string _status = "";

    [RelayCommand]
    private void Search()
    {
        const int limit = 5000;
        // Stored timestamps are UTC; widen by a day each side, then filter on local dates.
        DateOnly? from = From is { } f ? DateOnly.FromDateTime(f.Date).AddDays(-1) : null;
        DateOnly? to = To is { } t ? DateOnly.FromDateTime(t.Date).AddDays(1) : null;
        var rows = _svc.Audit.Search(from, to, Entity == Any ? null : Entity, Action == Any ? null : Action, limit)
            .Where(e => (From is null || e.Utc.ToLocalTime().Date >= From.Value.Date) && (To is null || e.Utc.ToLocalTime().Date <= To.Value.Date))
            .ToList();
        Rows.Clear();
        foreach (var r in rows) Rows.Add(new AuditRow(r));
        Status = rows.Count >= limit ? $"Showing the newest {limit}; narrow the filter to see older entries." : $"{rows.Count} entries.";
    }
}
