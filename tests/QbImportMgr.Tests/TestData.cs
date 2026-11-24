using System.Text;

namespace QbImportMgr.Tests;

internal static class TestData
{
    static TestData() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static byte[] Sample(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", name));

    public static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    public static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);
    public static byte[] Cp1252(string s) => Encoding.GetEncoding(1252).GetBytes(s);

    public const string SgmlHeader =
        "OFXHEADER:100\r\nDATA:OFXSGML\r\nVERSION:102\r\nSECURITY:NONE\r\nENCODING:USASCII\r\nCHARSET:1252\r\n" +
        "COMPRESSION:NONE\r\nOLDFILEUID:NONE\r\nNEWFILEUID:NONE\r\n\r\n";

    /// <summary>A minimal one-transaction bank file body (SGML style) for inline tests.</summary>
    public static string BankBody(string stmttrn, string ledger = "<LEDGERBAL><BALAMT>10.00<DTASOF>20261031</LEDGERBAL>") =>
        "<OFX><BANKMSGSRSV1><STMTTRNRS><TRNUID>1<STATUS><CODE>0<SEVERITY>INFO</STATUS><STMTRS><CURDEF>USD" +
        "<BANKACCTFROM><BANKID>1<ACCTID>99998888<ACCTTYPE>CHECKING</BANKACCTFROM>" +
        "<BANKTRANLIST><DTSTART>20261001<DTEND>20261031" + stmttrn + "</BANKTRANLIST>" + ledger +
        "</STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
}
