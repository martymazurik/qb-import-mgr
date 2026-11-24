using QbImportMgr.Core;
using QbImportMgr.Core.Domain;

namespace QbImportMgr.Tests;

public class DomainTests
{
    [Theory]
    [InlineData("-12.34", -1234)]
    [InlineData("+5", 500)]
    [InlineData("1,234.56", 123456)]
    [InlineData("-1,234", -123400)]
    [InlineData("12,34", 1234)]
    [InlineData(" 0.005 ", 1)]
    [InlineData("-0.005", -1)]
    [InlineData(".5", 50)]
    public void Money_parses_ofx_amounts(string text, long cents)
    {
        Assert.True(Money.TryParseCents(text, out var c));
        Assert.Equal(cents, c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    public void Money_rejects_garbage(string? text) => Assert.False(Money.TryParseCents(text, out _));

    [Fact]
    public void Account_identity_masks_and_hashes()
    {
        Assert.Equal("****4321", AccountIdentity.Mask("000987654321"));
        Assert.Equal("****1005", AccountIdentity.Mask("3712-345678-91005"));
        Assert.Equal("123", AccountIdentity.Mask("123"));

        var h = AccountIdentity.Hash(AccountKind.Bank, "123456789", "000987654321");
        Assert.Equal(64, h.Length);
        Assert.Equal(h, AccountIdentity.Hash(AccountKind.Bank, "123456789", "0009-8765 4321"));
        Assert.NotEqual(h, AccountIdentity.Hash(AccountKind.CreditCard, "123456789", "000987654321"));
        Assert.NotEqual(h, AccountIdentity.Hash(AccountKind.Bank, "987654321", "000987654321"));
        Assert.DoesNotContain("987654321", h);
    }

    [Fact]
    public void Txn_effective_values_prefer_overrides()
    {
        var t = new Txn { DtPosted = new DateOnly(2026, 1, 2), Name = "RAW", Memo = "M", CheckNum = "1" };
        Assert.Equal(("RAW", "M", "1", new DateOnly(2026, 1, 2)), (t.EffectivePayee, t.EffectiveMemo, t.EffectiveCheckNum, t.EffectiveDate));

        t.OvrPayee = "Clean"; t.OvrMemo = ""; t.OvrCheckNum = "2"; t.OvrDate = new DateOnly(2026, 1, 3);
        Assert.Equal(("Clean", "", "2", new DateOnly(2026, 1, 3)), (t.EffectivePayee, t.EffectiveMemo, t.EffectiveCheckNum, t.EffectiveDate));
    }

    [Fact]
    public void App_paths_create_all_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "qbim-" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = new AppPaths(root);
            p.EnsureCreated();
            foreach (var d in new[] { p.Inbox, p.Archive, p.Iif, p.Backup })
                Assert.True(Directory.Exists(d), d);
            Assert.Equal(Path.Combine(root, "qbo", "archive"), p.Archive);
            Assert.Equal(Path.Combine(root, "qb-import-mgr.db"), p.Database);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
