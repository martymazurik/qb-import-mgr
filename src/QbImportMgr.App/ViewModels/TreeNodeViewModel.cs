using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using QbImportMgr.Core.Domain;

namespace QbImportMgr.App.ViewModels;

public enum NodeKind { Header, Account, Statement, View }

public sealed partial class TreeNodeViewModel : ObservableObject
{
    public TreeNodeViewModel(NodeKind kind, string label, string key)
    {
        Kind = kind;
        Label = label;
        Key = key;
    }

    public NodeKind Kind { get; }
    public string Key { get; }
    public long? AccountId { get; init; }
    public long? StatementId { get; init; }
    public TxnStatus? ViewStatus { get; init; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    [ObservableProperty] private string _label;
    [ObservableProperty] private string? _badge;
    [ObservableProperty] private string? _subtitle;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _needsAttention;

    public IEnumerable<TreeNodeViewModel> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants())
                yield return d;
    }
}
