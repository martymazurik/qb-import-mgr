using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Export;

namespace QbImportMgr.Tests;

public class ExportValidatorTests
{
    private static readonly Dictionary<string, bool> Accounts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Checking"] = true,
        ["Travel"] = true,
        ["Meals and Entertainment"] = true,
        ["Old Expense"] = false,
    };

    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase) { "Delta Air Lines" };

    private static ExportItem Item(long id = 1, TxnStatus status = TxnStatus.Ready, string? bankQb = "Checking",
        long? catId = 10, string? cat = "Travel", long? nameId = null, string? name = null, long amount = -10000,
        IReadOnlyList<ExportSplit>? splits = null) =>
        new(id, status, AccountKind.Bank, "Checking ****4321", bankQb, new DateOnly(2026, 10, 5), amount, "DELTA", "", "",
            catId, cat, nameId, name, null, splits ?? []);

    private static ValidationResult V(params ExportItem[] items) => ExportValidator.Validate(items, Accounts, Names);

    [Fact]
    public void Valid_items_pass()
    {
        var r = V(Item(), Item(2, nameId: 5, name: "Delta Air Lines"),
            Item(3, catId: null, cat: null, splits: [new(10, "Travel", null, null, null, -6000, null), new(11, "meals and entertainment", null, null, null, -4000, null)]));
        Assert.True(r.CanExport);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Empty_selection_is_an_error() => Assert.False(V().CanExport);

    [Fact]
    public void Category_and_splits_together_is_an_error()
    {
        var r = V(Item(splits: [new(10, "Travel", null, null, null, -10000, null)]));
        Assert.Contains(r.Errors, e => e.Message.Contains("both a category and splits"));
    }

    [Fact]
    public void Uncategorized_is_an_error()
    {
        var r = V(Item(catId: null, cat: null));
        Assert.Contains(r.Errors, e => e.TxnId == 1 && e.Message.Contains("uncategorized"));
    }

    [Fact]
    public void Splits_must_balance()
    {
        var r = V(Item(catId: null, cat: null, splits: [new(10, "Travel", null, null, null, -6000, null), new(11, "Travel", null, null, null, -3999, null)]));
        Assert.Contains(r.Errors, e => e.Message.Contains("splits total -99.99"));
    }

    [Fact]
    public void Bank_account_must_be_mapped_and_exist()
    {
        Assert.Contains(V(Item(bankQb: null)).Errors, e => e.Message.Contains("not mapped"));
        Assert.Contains(V(Item(bankQb: "Savings")).Errors, e => e.Message.Contains("'Savings' is not in the QuickBooks chart"));
    }

    [Fact]
    public void Unmapped_account_is_reported_once_per_account()
    {
        var r = V(Item(1, bankQb: null), Item(2, bankQb: null));
        Assert.Single(r.Errors, e => e.Message.Contains("not mapped"));
    }

    [Fact]
    public void Category_must_exist_be_active_and_not_be_the_bank_account()
    {
        Assert.Contains(V(Item(cat: "Nope")).Errors, e => e.Message.Contains("'Nope'"));
        Assert.Contains(V(Item(cat: "Old Expense")).Errors, e => e.Message.Contains("inactive"));
        Assert.Contains(V(Item(cat: null)).Errors, e => e.Message.Contains("no longer exists"));
        Assert.Contains(V(Item(cat: "Checking")).Errors, e => e.Message.Contains("bank account itself"));
    }

    [Fact]
    public void Qb_name_must_exist()
    {
        Assert.Contains(V(Item(nameId: 5, name: "Nobody")).Errors, e => e.Message.Contains("'Nobody'"));
        Assert.Contains(V(Item(nameId: 5, name: null)).Errors, e => e.Message.Contains("(deleted)"));
    }

    [Theory]
    [InlineData(TxnStatus.New)]
    [InlineData(TxnStatus.Categorized)]
    [InlineData(TxnStatus.Exported)]
    [InlineData(TxnStatus.Duplicate)]
    public void Only_ready_rows_export(TxnStatus status) =>
        Assert.Contains(V(Item(status: status)).Errors, e => e.Message.Contains("only Ready"));

    [Fact]
    public void Zero_amount_is_a_warning_not_an_error()
    {
        var r = V(Item(amount: 0));
        Assert.True(r.CanExport);
        Assert.Single(r.Warnings);
    }
}
