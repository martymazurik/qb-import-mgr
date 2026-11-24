using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Iif;

namespace QbImportMgr.Tests;

public class IifListReaderTests
{
    [Fact]
    public void Reads_accounts_names_and_classes_from_qb_list_export()
    {
        var lists = IifListReader.Read(TestData.Sample("qb_lists.iif"));

        Assert.Empty(lists.Warnings);
        Assert.Equal(8, lists.Accounts.Count);
        Assert.Contains(lists.Accounts, a => a is { FullName: "Checking", AccntType: "BANK", Hidden: false });
        Assert.Contains(lists.Accounts, a => a is { FullName: "American Express", AccntType: "CCARD" });
        Assert.Contains(lists.Accounts, a => a.FullName == "Utilities:Telephone");
        Assert.True(lists.Accounts.Single(a => a.FullName == "Old Expense").Hidden);

        Assert.Equal(["Admin", "Field"], lists.Classes);

        Assert.Equal(6, lists.Names.Count);
        Assert.Contains(lists.Names, n => n is { Name: "AT&T", Type: QbNameType.Vendor }); // quotes removed
        Assert.Contains(lists.Names, n => n is { Name: "Acme Corp:Project X", Type: QbNameType.Customer });
        Assert.Contains(lists.Names, n => n is { Name: "Owner", Type: QbNameType.OtherName });
        Assert.Contains(lists.Names, n => n is { Name: "Pat Example", Type: QbNameType.Employee, Hidden: true });
    }

    [Fact]
    public void Rows_without_header_or_name_are_skipped()
    {
        var text = "ACCNT\tOrphan\r\n!ACCNT\tNAME\tACCNTTYPE\r\nACCNT\t\tEXP\r\nACCNT\tGood\tEXP\r\n";
        var lists = IifListReader.Read(text);

        Assert.Equal("Good", Assert.Single(lists.Accounts).FullName);
        Assert.Single(lists.Warnings);
    }
}
