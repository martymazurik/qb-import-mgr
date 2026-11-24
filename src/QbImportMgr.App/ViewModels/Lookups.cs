using QbImportMgr.Core.Domain;
using QbImportMgr.Data.Db;

namespace QbImportMgr.App.ViewModels;

public sealed record CategoryOption(long Id, string FullName, string? AccntType, bool IsActive)
{
    public string Display => IsActive ? FullName : FullName + " (inactive)";
    public override string ToString() => FullName;
}

public sealed record NameOption(long? Id, string Name, string TypeLabel)
{
    public static readonly NameOption None = new(null, "(none)", "");
    public string Display => Id is null ? Name : $"{Name}  ·  {TypeLabel}";
    public override string ToString() => Name;
}

/// <summary>QuickBooks lists loaded from the DB, shared by every view.</summary>
public sealed class Lookups : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private Dictionary<long, CategoryOption> _categoryById = [];
    private Dictionary<long, NameOption> _nameById = [];

    /// <summary>Active categories, for pickers.</summary>
    public IReadOnlyList<CategoryOption> Categories { get; private set; } = [];
    public IReadOnlyList<NameOption> Names { get; private set; } = [NameOption.None];
    public IReadOnlyList<string> Classes { get; private set; } = [];
    public bool HasQbLists => _categoryById.Count > 0;

    public void Reload(DbSession s)
    {
        var all = s.Categories.GetAll().Select(c => new CategoryOption(c.Id, c.FullName, c.AccntType, c.IsActive)).ToList();
        _categoryById = all.ToDictionary(c => c.Id);
        Categories = all.Where(c => c.IsActive).ToList();

        var rawNames = s.Names.GetAll();
        _nameById = rawNames.ToDictionary(n => n.Id, n => new NameOption(n.Id, n.Name, TypeLabel(n.NameType)));
        Names = [NameOption.None, .. rawNames.Where(n => n.IsActive).Select(n => _nameById[n.Id])];
        Classes = s.Settings.GetClasses();
        OnPropertyChanged(string.Empty);
    }

    public CategoryOption? Category(long? id) => id is { } v && _categoryById.TryGetValue(v, out var c) ? c : null;
    public NameOption Name(long? id) => id is { } v && _nameById.TryGetValue(v, out var n) ? n : NameOption.None;

    /// <summary>Exact (case-insensitive) match on full name, then a unique prefix match.</summary>
    public CategoryOption? FindCategory(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        var exact = Categories.FirstOrDefault(c => c.FullName.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var prefix = Categories.Where(c => c.FullName.StartsWith(text, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return prefix.Count == 1 ? prefix[0] : null;
    }

    /// <summary>QB accounts a bank account can map to: BANK for bank accounts, CCARD for credit cards.</summary>
    public IReadOnlyList<string> QbAccountsFor(AccountKind kind)
    {
        var type = kind == AccountKind.Bank ? "BANK" : "CCARD";
        return Categories.Where(c => string.Equals(c.AccntType, type, StringComparison.OrdinalIgnoreCase)).Select(c => c.FullName).ToList();
    }

    private static string TypeLabel(QbNameType t) => t switch
    {
        QbNameType.Customer => "Customer",
        QbNameType.Vendor => "Vendor",
        QbNameType.Employee => "Employee",
        _ => "Other",
    };
}
