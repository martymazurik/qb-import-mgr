using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Iif;

namespace QbImportMgr.Core.Export;

/// <summary>A split with its references resolved. A null name for a non-null id means the referenced row is missing.</summary>
public sealed record ExportSplit(long CategoryId, string? CategoryName, long? QbNameId, string? QbName, string? Class, long AmountCents, string? Memo);

/// <summary>A transaction with its account, category, name and splits resolved by the data layer.</summary>
public sealed record ExportItem(
    long TxnId,
    TxnStatus Status,
    AccountKind Kind,
    string AccountDisplay,
    string? BankQbAccount,
    DateOnly Date,
    long SignedAmountCents,
    string Payee,
    string Memo,
    string CheckNum,
    long? CategoryId,
    string? CategoryName,
    long? QbNameId,
    string? QbName,
    string? Class,
    IReadOnlyList<ExportSplit> Splits);

public sealed record ExportOptions(bool Clear = true);

public static class IifMapper
{
    public static IifTransaction ToIif(ExportItem item, ExportOptions options)
    {
        var memo = BuildMemo(item);
        IReadOnlyList<IifSplitLine> lines = item.Splits.Count > 0
            ? item.Splits
                .Select(s => new IifSplitLine(
                    s.CategoryName ?? throw Missing(item, "split category"),
                    s.QbName ?? item.QbName,
                    s.Class ?? item.Class,
                    -s.AmountCents,
                    string.IsNullOrWhiteSpace(s.Memo) ? memo : s.Memo))
                .ToList()
            : [new IifSplitLine(item.CategoryName ?? throw Missing(item, "category"), item.QbName, item.Class, -item.SignedAmountCents, memo)];

        return new IifTransaction(
            IifTypes.For(item.Kind, item.SignedAmountCents),
            item.Date,
            item.BankQbAccount ?? throw Missing(item, "QuickBooks account"),
            item.QbName,
            item.Class,
            item.SignedAmountCents,
            string.IsNullOrWhiteSpace(item.CheckNum) ? null : item.CheckNum,
            memo,
            options.Clear,
            lines);
    }

    /// <summary>Without a QB name the bank payee would be lost, so it goes in the memo.</summary>
    internal static string BuildMemo(ExportItem item)
    {
        if (item.QbName is not null) return item.Memo;
        if (string.IsNullOrWhiteSpace(item.Memo) || item.Memo.Equals(item.Payee, StringComparison.OrdinalIgnoreCase))
            return item.Payee;
        if (string.IsNullOrWhiteSpace(item.Payee)) return item.Memo;
        return $"{item.Payee} - {item.Memo}";
    }

    private static InvalidOperationException Missing(ExportItem item, string what) =>
        new($"Txn {item.TxnId}: {what} not resolved; run ExportValidator first.");
}
