using CommunityToolkit.Mvvm.ComponentModel;
using QbImportMgr.Core.Domain;

namespace QbImportMgr.App.ViewModels;

public sealed class TxnRowViewModel(MainViewModel owner, Txn txn, string accountName, bool hasSplits) : ObservableObject
{
    public Txn Txn { get; private set; } = txn;
    public bool HasSplits { get; private set; } = hasSplits;

    public long Id => Txn.Id;
    public DateTime Date => Txn.EffectiveDate.ToDateTime(TimeOnly.MinValue);
    public string Account { get; } = accountName;

    /// <summary>Editable in the grid; writes OvrPayee (blank reverts to the bank NAME).</summary>
    public string Payee
    {
        get => Txn.EffectivePayee;
        set
        {
            if (value != Txn.EffectivePayee) owner.SetPayee(this, value);
        }
    }

    public string Memo => Txn.EffectiveMemo;
    public CategoryOption? Category => owner.Lookups.Category(Txn.CategoryId);
    public string CategoryText => HasSplits ? "(split)" : Category?.FullName ?? "";
    public decimal Amount => Txn.SignedAmountCents / 100m;
    public TxnStatus Status => Txn.Status;
    public string StatusText => Txn.Status == TxnStatus.Duplicate ? "Duplicate?" : Txn.Status.ToString();
    public bool IsLocked => Txn.IsLocked;

    public void Update(Txn t, bool hasSplits)
    {
        Txn = t;
        HasSplits = hasSplits;
        OnPropertyChanged(string.Empty);
    }
}
