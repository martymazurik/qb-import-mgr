using QbImportMgr.Core.Domain;

namespace QbImportMgr.Core.Export;

public enum IssueSeverity { Warning, Error }

public sealed record ValidationIssue(long? TxnId, IssueSeverity Severity, string Message);

public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public bool CanExport => Issues.All(i => i.Severity != IssueSeverity.Error);
    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == IssueSeverity.Error);
    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == IssueSeverity.Warning);
}

public static class ExportValidator
{
    /// <param name="qbAccounts">Chart-of-accounts full names from the QB list import, mapped to IsActive (case-insensitive).</param>
    /// <param name="qbNames">Names from the QB list import.</param>
    public static ValidationResult Validate(
        IReadOnlyCollection<ExportItem> items,
        IReadOnlyDictionary<string, bool> qbAccounts,
        IReadOnlySet<string> qbNames)
    {
        var issues = new List<ValidationIssue>();
        if (items.Count == 0)
        {
            issues.Add(new(null, IssueSeverity.Error, "Nothing to export: no Ready transactions selected."));
            return new ValidationResult(issues);
        }

        foreach (var acct in items.Select(i => (i.AccountDisplay, i.BankQbAccount)).Distinct())
        {
            if (string.IsNullOrWhiteSpace(acct.BankQbAccount))
                issues.Add(new(null, IssueSeverity.Error, $"Bank account '{acct.AccountDisplay}' is not mapped to a QuickBooks account."));
            else
                CheckAccount(null, acct.BankQbAccount, $"QuickBooks account for '{acct.AccountDisplay}'", qbAccounts, issues);
        }

        foreach (var i in items)
        {
            void Err(string m) => issues.Add(new(i.TxnId, IssueSeverity.Error, m));
            void Warn(string m) => issues.Add(new(i.TxnId, IssueSeverity.Warning, m));
            var label = $"{i.Date:MM/dd/yyyy} {i.Payee} {Money.FormatDisplay(i.SignedAmountCents)}";

            if (i.Status != TxnStatus.Ready)
                Err($"{label}: status is {i.Status}, only Ready rows can be exported.");

            bool hasCategory = i.CategoryId is not null;
            bool hasSplits = i.Splits.Count > 0;
            if (hasCategory && hasSplits)
                Err($"{label}: has both a category and splits; use one or the other.");
            else if (!hasCategory && !hasSplits)
                Err($"{label}: uncategorized.");
            else if (hasCategory)
            {
                if (i.CategoryName is null)
                    Err($"{label}: category no longer exists.");
                else
                {
                    CheckAccount(i.TxnId, i.CategoryName, $"{label}: category", qbAccounts, issues);
                    if (i.BankQbAccount is not null && i.CategoryName.Equals(i.BankQbAccount, StringComparison.OrdinalIgnoreCase))
                        Err($"{label}: category is the bank account itself.");
                }
            }
            else
            {
                long sum = i.Splits.Sum(s => s.AmountCents);
                if (sum != i.SignedAmountCents)
                    Err($"{label}: splits total {Money.FormatDisplay(sum)}, transaction is {Money.FormatDisplay(i.SignedAmountCents)}.");
                foreach (var s in i.Splits)
                {
                    if (s.CategoryName is null) Err($"{label}: a split's category no longer exists.");
                    else CheckAccount(i.TxnId, s.CategoryName, $"{label}: split category", qbAccounts, issues);
                    if (s.QbNameId is not null) CheckName(i.TxnId, s.QbName, label, qbNames, issues);
                    if (s.AmountCents == 0) Warn($"{label}: a split has a zero amount.");
                }
            }

            if (i.QbNameId is not null) CheckName(i.TxnId, i.QbName, label, qbNames, issues);
            if (i.SignedAmountCents == 0) Warn($"{label}: zero amount.");
        }

        return new ValidationResult(issues);
    }

    private static void CheckAccount(long? txnId, string name, string what, IReadOnlyDictionary<string, bool> qbAccounts, List<ValidationIssue> issues)
    {
        if (!qbAccounts.TryGetValue(name, out var active))
            issues.Add(new(txnId, IssueSeverity.Error, $"{what} '{name}' is not in the QuickBooks chart of accounts; re-import lists or fix the mapping."));
        else if (!active)
            issues.Add(new(txnId, IssueSeverity.Error, $"{what} '{name}' is inactive in QuickBooks."));
    }

    private static void CheckName(long txnId, string? name, string label, IReadOnlySet<string> qbNames, List<ValidationIssue> issues)
    {
        if (name is null || !qbNames.Contains(name))
            issues.Add(new(txnId, IssueSeverity.Error, $"{label}: QuickBooks name '{name ?? "(deleted)"}' does not exist."));
    }
}
