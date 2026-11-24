using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Ofx;

namespace QbImportMgr.Tests;

public class OfxParserTests
{
    [Fact]
    public void Bank_sgml_1x_with_cp1252_raw_ampersand_and_empty_leaves()
    {
        var doc = OfxParser.Parse(TestData.Sample("bank_sgml_1x.qbo"));

        Assert.Equal(OfxFormat.Sgml, doc.Format);
        Assert.Equal("102", doc.Version);
        Assert.Equal("windows-1252", doc.EncodingName);
        Assert.Empty(doc.Warnings);

        var s = Assert.Single(doc.Statements);
        Assert.Equal(AccountKind.Bank, s.Kind);
        Assert.Equal("123456789", s.BankId);
        Assert.Equal("000987654321", s.AcctId);
        Assert.Equal("CHECKING", s.AcctType);
        Assert.Equal("USD", s.CurDef);
        Assert.Equal(new DateOnly(2026, 10, 1), s.DtStart);
        Assert.Equal(new DateOnly(2026, 10, 31), s.DtEnd);
        Assert.Equal(317827, s.LedgerBalCents);
        Assert.Equal(new DateOnly(2026, 10, 31), s.LedgerBalDate);
        Assert.Equal(5, s.Transactions.Count);

        var att = s.Transactions[0];
        Assert.Equal("DEBIT", att.TrnType);
        Assert.Equal(new DateOnly(2026, 10, 2), att.DtPosted);
        Assert.Equal(-5423, att.AmountCents);
        Assert.Equal("2026100201", att.FitId);
        Assert.Equal("AT&T *PAYMENT", att.Name);
        Assert.Equal("ACH DEBIT", att.Memo);

        var check = s.Transactions[1];
        Assert.Equal("1042", check.CheckNum);
        Assert.Equal(new DateOnly(2026, 10, 3), check.DtUser);
        Assert.Equal(-125000, check.AmountCents);
        Assert.Null(check.Memo); // empty <MEMO> leaf must not swallow the following element

        var cafe = s.Transactions[3];
        Assert.Equal("CAFÉ ROMA", cafe.Name);
        Assert.Equal("COFFEE & PASTRY", cafe.Memo);

        var fee = s.Transactions[4];
        Assert.Null(fee.FitId);
        Assert.Equal("SERVICE FEE", fee.Name);
        Assert.Equal(-500, fee.AmountCents);
    }

    [Fact]
    public void Amex_ccstmt_single_line_with_mixed_closed_leaves()
    {
        var doc = OfxParser.Parse(TestData.Sample("amex_ccstmt.qfx"));

        var s = Assert.Single(doc.Statements);
        Assert.Equal(AccountKind.CreditCard, s.Kind);
        Assert.Null(s.BankId);
        Assert.Equal("371234567891005", s.AcctId);
        Assert.Equal(-80874, s.LedgerBalCents);
        Assert.Equal(new DateOnly(2026, 11, 8), s.LedgerBalDate);
        Assert.Equal(6, s.Transactions.Count);

        Assert.Equal("AMAZON MKTPL*AB12C3", s.Transactions[0].Name);
        Assert.Equal("AMZN.COM/BILL WA", s.Transactions[0].Memo);
        Assert.Equal("320262850012345678", s.Transactions[0].FitId);

        // Two identical parking charges on the same day are both kept.
        Assert.Equal(2, s.Transactions.Count(t => t.Name == "PARKING METER 4471" && t.AmountCents == -500));

        Assert.Equal(-123456, s.Transactions[3].AmountCents); // "-1,234.56"
        Assert.Equal(50000, s.Transactions[4].AmountCents);
        Assert.Null(s.Transactions[4].Memo);
        Assert.Equal(-80874, s.Transactions.Sum(t => t.AmountCents));
    }

    [Fact]
    public void Ofx2_xml_utf8_payee_aggregate_and_self_closing()
    {
        var doc = OfxParser.Parse(TestData.Sample("ofx2_xml.ofx"));

        Assert.Equal(OfxFormat.Xml, doc.Format);
        Assert.Equal("211", doc.Version);
        Assert.Equal("utf-8", doc.EncodingName);

        var s = Assert.Single(doc.Statements);
        Assert.Equal("5555000011112222", s.AcctId);
        Assert.Equal(429125, s.LedgerBalCents);
        Assert.Equal(3, s.Transactions.Count);
        Assert.Equal("CRÈME CAFÉ", s.Transactions[0].Name);
        Assert.Equal("LATTE & MUFFIN", s.Transactions[0].Memo);
        Assert.Equal("CLIENT WIRE", s.Transactions[1].Name);
        Assert.Null(s.Transactions[1].Memo);
        Assert.Equal("X-1003", s.Transactions[2].FitId);
    }

    [Fact]
    public void Multiple_statements_bank_and_credit_card_in_one_file()
    {
        var doc = OfxParser.Parse(TestData.Sample("multi_stmt.ofx"));

        Assert.Equal(3, doc.Statements.Count);
        Assert.Equal(["1111222233", "1111222244", "4111111111111111"], doc.Statements.Select(s => s.AcctId));
        Assert.Equal([AccountKind.Bank, AccountKind.Bank, AccountKind.CreditCard], doc.Statements.Select(s => s.Kind));
        Assert.Equal("SAVINGS", doc.Statements[1].AcctType);
        Assert.Equal([2, 1, 1], doc.Statements.Select(s => s.Transactions.Count));
        Assert.Equal(-6310, doc.Statements[2].LedgerBalCents);
    }

    [Fact]
    public void Xml_header_with_raw_ampersand_and_unclosed_leaves_is_tolerated()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <?OFX OFXHEADER="200" VERSION="220"?>
            """ + TestData.BankBody("<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20261005<TRNAMT>-3.00<FITID>A1<NAME>B&B SUPPLY & CO<MEMO>X</STMTTRN>");

        var s = Assert.Single(OfxParser.Parse(TestData.Utf8(xml)).Statements);
        var t = Assert.Single(s.Transactions);
        Assert.Equal("B&B SUPPLY & CO", t.Name);
        Assert.Equal("X", t.Memo);
        Assert.Equal(1000, s.LedgerBalCents);
    }

    [Fact]
    public void Utf8_declared_but_cp1252_bytes_falls_back_to_1252()
    {
        var text = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><?OFX OFXHEADER=\"200\" VERSION=\"211\"?>" +
                   TestData.BankBody("<STMTTRN><TRNTYPE>POS</TRNTYPE><DTPOSTED>20261005</DTPOSTED><TRNAMT>-3.00</TRNAMT><FITID>A1</FITID><NAME>NAÏVE TEA – SHOP</NAME></STMTTRN>");
        var doc = OfxParser.Parse(TestData.Cp1252(text));

        Assert.Equal("windows-1252", doc.EncodingName);
        Assert.Equal("NAÏVE TEA – SHOP", doc.Statements[0].Transactions[0].Name);
    }

    [Fact]
    public void Usascii_declared_but_utf8_bytes_decodes_as_utf8()
    {
        var text = TestData.SgmlHeader + TestData.BankBody("<STMTTRN><TRNTYPE>POS<DTPOSTED>20261005<TRNAMT>-3.00<FITID>A1<NAME>JOSÉ'S TACOS</STMTTRN>");
        var doc = OfxParser.Parse(TestData.Utf8(text));

        Assert.Equal("utf-8", doc.EncodingName);
        Assert.Equal("JOSÉ'S TACOS", doc.Statements[0].Transactions[0].Name);
    }

    [Fact]
    public void Utf8_bom_and_lf_only_header_are_accepted()
    {
        var text = TestData.SgmlHeader.Replace("\r\n", "\n") +
                   TestData.BankBody("<STMTTRN><TRNTYPE>POS<DTPOSTED>20261005<TRNAMT>-3.00<FITID>A1<NAME>SHOP</STMTTRN>");
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(TestData.Ascii(text)).ToArray();

        Assert.Single(OfxParser.Parse(bytes).Statements[0].Transactions);
    }

    [Fact]
    public void Headerless_sgml_body_is_accepted()
    {
        var doc = OfxParser.Parse(TestData.Ascii(TestData.BankBody("<STMTTRN><TRNTYPE>POS<DTPOSTED>20261005<TRNAMT>-3.00<FITID>A1<NAME>SHOP</STMTTRN>")));
        Assert.Equal(OfxFormat.Sgml, doc.Format);
        Assert.Single(doc.Statements[0].Transactions);
    }

    [Fact]
    public void Lowercase_tags_and_comments_are_tolerated()
    {
        var body = TestData.BankBody("<!-- note --><stmttrn><trntype>POS<dtposted>20261005<trnamt>-3.00<fitid>A1<name>SHOP</stmttrn>");
        var t = Assert.Single(OfxParser.Parse(TestData.Ascii(body)).Statements[0].Transactions);
        Assert.Equal("SHOP", t.Name);
    }

    [Fact]
    public void Invalid_transactions_are_skipped_with_warnings()
    {
        var body = TestData.BankBody(
            "<STMTTRN><TRNTYPE>POS<DTPOSTED>BAD<TRNAMT>-3.00<FITID>A1<NAME>NO DATE</STMTTRN>" +
            "<STMTTRN><TRNTYPE>POS<DTPOSTED>20261005<TRNAMT>abc<FITID>A2<NAME>NO AMOUNT</STMTTRN>" +
            "<STMTTRN><TRNTYPE>POS<DTPOSTED>20261005<TRNAMT>-1.00<FITID>A3<NAME>GOOD</STMTTRN>");
        var doc = OfxParser.Parse(TestData.Ascii(TestData.SgmlHeader + body));

        Assert.Equal("GOOD", Assert.Single(doc.Statements[0].Transactions).Name);
        Assert.Equal(2, doc.Warnings.Count);
        Assert.Contains(doc.Warnings, w => w.Contains("DTPOSTED") && w.Contains("****8888"));
        Assert.DoesNotContain(doc.Warnings, w => w.Contains("99998888")); // full account number never surfaces
    }

    [Fact]
    public void Error_status_is_reported_as_warning()
    {
        var body = "<OFX><SIGNONMSGSRSV1><SONRS><STATUS><CODE>15500<SEVERITY>ERROR<MESSAGE>Bad password</STATUS></SONRS></SIGNONMSGSRSV1></OFX>";
        var doc = OfxParser.Parse(TestData.Ascii(TestData.SgmlHeader + body));

        Assert.Empty(doc.Statements);
        Assert.Contains(doc.Warnings, w => w.Contains("15500") && w.Contains("Bad password"));
        Assert.Contains(doc.Warnings, w => w.Contains("no bank or credit card statements"));
    }

    [Fact]
    public void Truncated_file_does_not_throw()
    {
        var full = TestData.SgmlHeader + TestData.BankBody("<STMTTRN><TRNTYPE>POS<DTPOSTED>20261005<TRNAMT>-3.00<FITID>A1<NAME>SHOP</STMTTRN>");
        var cut = full[..(full.IndexOf("</BANKTRANLIST>", StringComparison.Ordinal) + 5)];

        var doc = OfxParser.Parse(TestData.Ascii(cut));
        Assert.Single(doc.Statements[0].Transactions);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("")]
    [InlineData("OFXHEADER:100\r\n\r\nno body here")]
    public void Non_ofx_input_throws(string text) =>
        Assert.Throws<OfxParseException>(() => OfxParser.Parse(TestData.Ascii(text)));

    [Theory]
    [InlineData("20261031", 2026, 10, 31)]
    [InlineData("20261031235959", 2026, 10, 31)]
    [InlineData("20261031235959.123[-5:EST]", 2026, 10, 31)]
    [InlineData(" 20260101120000[0:GMT] ", 2026, 1, 1)]
    public void ParseDate_reads_first_eight_digits(string s, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), OfxParser.ParseDate(s));

    [Theory]
    [InlineData(null)]
    [InlineData("2026")]
    [InlineData("20261340")]
    public void ParseDate_rejects_bad_input(string? s) => Assert.Null(OfxParser.ParseDate(s));

    [Fact]
    public void Entities_decode_and_raw_ampersands_survive()
    {
        Assert.Equal("A & B < C > D \" ' é", OfxTokenizer.DecodeEntities("A &amp; B &lt; C &gt; D &quot; &apos; &#233;"));
        Assert.Equal("AT&T & SONS &x; &", OfxTokenizer.DecodeEntities("AT&T & SONS &x; &"));
        Assert.Equal("é", OfxTokenizer.DecodeEntities("&#xE9;"));
    }
}
