using System.Text;
using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Export;
using QbImportMgr.Core.Iif;

namespace QbImportMgr.Tests;

public class IifWriterTests
{
    private static ExportItem Item(
        AccountKind kind = AccountKind.Bank, long amount = -5423, string payee = "AT&T *PAYMENT", string memo = "ACH DEBIT",
        string check = "", string? category = "Utilities:Telephone", string? name = null, string? cls = null,
        IReadOnlyList<ExportSplit>? splits = null) =>
        new(1, TxnStatus.Ready, kind, "Checking ****4321", kind == AccountKind.Bank ? "Checking" : "American Express",
            new DateOnly(2026, 10, 2), amount, payee, memo, check,
            category is null ? null : 10, category, name is null ? null : 20, name, cls, splits ?? []);

    private static string WriteText(params ExportItem[] items) =>
        IifWriter.Encoding.GetString(IifWriter.WriteToBytes(items.Select(i => IifMapper.ToIif(i, new ExportOptions()))));

    [Fact]
    public void Bank_check_writes_exact_iif_block()
    {
        var text = WriteText(Item(amount: -125000, payee: "CHECK 1042", memo: "", check: "1042", category: "Travel", name: "Delta Air Lines", cls: "Admin"));

        var expected =
            "!TRNS\tTRNSID\tTRNSTYPE\tDATE\tACCNT\tNAME\tCLASS\tAMOUNT\tDOCNUM\tMEMO\tCLEAR\r\n" +
            "!SPL\tSPLID\tTRNSTYPE\tDATE\tACCNT\tNAME\tCLASS\tAMOUNT\tDOCNUM\tMEMO\tCLEAR\r\n" +
            "!ENDTRNS\r\n" +
            "TRNS\t\tCHECK\t10/02/2026\tChecking\tDelta Air Lines\tAdmin\t-1250.00\t1042\t\tY\r\n" +
            "SPL\t\tCHECK\t10/02/2026\tTravel\tDelta Air Lines\tAdmin\t1250.00\t1042\t\tN\r\n" +
            "ENDTRNS\r\n";
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData(AccountKind.Bank, -100, "CHECK")]
    [InlineData(AccountKind.Bank, 100, "DEPOSIT")]
    [InlineData(AccountKind.Bank, 0, "DEPOSIT")]
    [InlineData(AccountKind.CreditCard, -100, "CREDIT CARD")]
    [InlineData(AccountKind.CreditCard, 100, "CCARD REFUND")]
    public void Transaction_type_follows_account_kind_and_sign(AccountKind kind, long cents, string expected) =>
        Assert.Equal(expected, IifTypes.For(kind, cents));

    [Fact]
    public void Trns_amount_is_ofx_amount_and_spl_is_negated()
    {
        var deposit = IifMapper.ToIif(Item(amount: 250000, category: "Sales Income"), new ExportOptions());
        Assert.Equal("DEPOSIT", deposit.TrnsType);
        Assert.Equal(250000, deposit.AmountCents);
        Assert.Equal(-250000, Assert.Single(deposit.Splits).AmountCents);

        var charge = IifMapper.ToIif(Item(AccountKind.CreditCard, -8417, category: "Meals and Entertainment"), new ExportOptions());
        Assert.Equal("CREDIT CARD", charge.TrnsType);
        Assert.Equal("American Express", charge.Account);
        Assert.Equal(-8417, charge.AmountCents);
        Assert.Equal(8417, charge.Splits[0].AmountCents);

        var text = WriteText(Item(AccountKind.CreditCard, 1999, category: "Meals and Entertainment"));
        Assert.Contains("TRNS\t\tCCARD REFUND\t10/02/2026\tAmerican Express\t\t\t19.99\t", text);
        Assert.Contains("SPL\t\tCCARD REFUND\t10/02/2026\tMeals and Entertainment\t\t\t-19.99\t", text);
    }

    [Fact]
    public void Splits_produce_one_spl_line_each_with_negated_amounts()
    {
        var splits = new List<ExportSplit>
        {
            new(10, "Travel", null, null, "Field", -100000, "Airfare"),
            new(11, "Meals and Entertainment", null, null, null, -23456, null),
        };
        var t = IifMapper.ToIif(Item(amount: -123456, category: null, cls: "Admin", splits: splits), new ExportOptions());

        Assert.Equal(2, t.Splits.Count);
        Assert.Equal([100000L, 23456L], t.Splits.Select(s => s.AmountCents));
        Assert.Equal(["Field", "Admin"], t.Splits.Select(s => s.Class));
        Assert.Equal("Airfare", t.Splits[0].Memo);
        Assert.Equal("AT&T *PAYMENT - ACH DEBIT", t.Splits[1].Memo); // falls back to txn memo
    }

    [Fact]
    public void Memo_keeps_bank_payee_when_no_qb_name_assigned()
    {
        Assert.Equal("AT&T *PAYMENT - ACH DEBIT", IifMapper.BuildMemo(Item()));
        Assert.Equal("ACH DEBIT", IifMapper.BuildMemo(Item(name: "AT&T")));
        Assert.Equal("SHOP", IifMapper.BuildMemo(Item(payee: "SHOP", memo: "")));
        Assert.Equal("SHOP", IifMapper.BuildMemo(Item(payee: "SHOP", memo: "shop")));
    }

    [Fact]
    public void Output_uses_crlf_only()
    {
        var bytes = IifWriter.WriteToBytes([IifMapper.ToIif(Item(memo: "line1\nline2\r\nline3"), new ExportOptions())]);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n') Assert.Equal((byte)'\r', bytes[i - 1]);
            if (bytes[i] == (byte)'\r') Assert.Equal((byte)'\n', bytes[i + 1]);
        }
        Assert.Equal(6, Encoding.ASCII.GetString(bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Output_is_cp1252_with_replacement_for_unmappable()
    {
        var bytes = IifWriter.WriteToBytes([IifMapper.ToIif(Item(payee: "CAFÉ ✓ – ROMA", memo: ""), new ExportOptions())]);

        Assert.Contains((byte)0xC9, bytes);  // É in cp1252
        Assert.Contains((byte)0x96, bytes);  // en dash in cp1252
        Assert.DoesNotContain((byte)0xC3, bytes); // no UTF-8 lead bytes
        Assert.Contains("CAFÉ ? – ROMA", IifWriter.Encoding.GetString(bytes));
    }

    [Fact]
    public void Text_fields_are_stripped_of_tabs_quotes_and_newlines()
    {
        Assert.Equal("He said hi there ok", IifSanitizer.Clean("He said \"hi\"\tthere\r\nok"));
        Assert.Equal("a b", IifSanitizer.Clean("  a \t\t b  "));
        Assert.Equal("", IifSanitizer.Clean(null));

        var text = WriteText(Item(payee: "BOB'S \"BEST\"\tBURGERS", memo: "", name: null, category: "Meals and Entertainment"));
        var trns = text.Split("\r\n")[3].Split('\t');
        Assert.Equal(11, trns.Length);
        Assert.Equal("BOB'S BEST BURGERS", trns[9]);
    }

    [Fact]
    public void Clear_flag_follows_option()
    {
        var t = IifMapper.ToIif(Item(), new ExportOptions(Clear: false));
        var text = IifWriter.Encoding.GetString(IifWriter.WriteToBytes([t]));
        Assert.EndsWith("\tN", text.Split("\r\n")[3]);
    }

    [Fact]
    public void Unbalanced_transaction_throws()
    {
        var t = new IifTransaction("CHECK", new DateOnly(2026, 1, 1), "Checking", null, null, -1000, null, null, true,
            [new IifSplitLine("Travel", null, null, 999, null)]);
        var ex = Assert.Throws<InvalidOperationException>(() => IifWriter.WriteToBytes([t]));
        Assert.Contains("unbalanced", ex.Message);
    }

    [Fact]
    public void Mapper_refuses_unresolved_category()
    {
        Assert.Throws<InvalidOperationException>(() => IifMapper.ToIif(Item(category: null), new ExportOptions()));
    }

    [Theory]
    [InlineData(0, "0.00")]
    [InlineData(-5, "-0.05")]
    [InlineData(123456, "1234.56")]
    [InlineData(-100000000, "-1000000.00")]
    public void Amounts_format_invariant_without_thousands_separator(long cents, string expected) =>
        Assert.Equal(expected, Money.FormatIif(cents));
}
