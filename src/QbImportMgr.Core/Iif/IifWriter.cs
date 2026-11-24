using System.Globalization;
using System.Text;
using QbImportMgr.Core.Domain;

namespace QbImportMgr.Core.Iif;

public sealed record IifSplitLine(string Account, string? Name, string? Class, long AmountCents, string? Memo);

/// <summary>One TRNS block. Amounts are in IIF sign: SPL amounts must sum to -TRNS amount.</summary>
public sealed record IifTransaction(
    string TrnsType,
    DateOnly Date,
    string Account,
    string? Name,
    string? Class,
    long AmountCents,
    string? DocNum,
    string? Memo,
    bool Clear,
    IReadOnlyList<IifSplitLine> Splits);

public static class IifTypes
{
    public const string Check = "CHECK";
    public const string Deposit = "DEPOSIT";
    public const string CreditCard = "CREDIT CARD";
    public const string CreditCardRefund = "CCARD REFUND";

    /// <summary>Bank: out is CHECK, in is DEPOSIT. Credit card: charge (out) is CREDIT CARD, credit (in) is CCARD REFUND. Zero counts as in.</summary>
    public static string For(AccountKind kind, long signedAmountCents) => kind switch
    {
        AccountKind.Bank => signedAmountCents < 0 ? Check : Deposit,
        AccountKind.CreditCard => signedAmountCents < 0 ? CreditCard : CreditCardRefund,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public static class IifSanitizer
{
    /// <summary>Removes tabs, quotes and line breaks (replaced by a space, collapsed) and trims.</summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool lastSpace = false;
        foreach (var c in s)
        {
            if (c == '"') continue;
            bool space = c is '\t' or '\r' or '\n' or ' ' || char.IsControl(c);
            if (space)
            {
                if (!lastSpace && sb.Length > 0) sb.Append(' ');
                lastSpace = true;
            }
            else
            {
                sb.Append(c);
                lastSpace = false;
            }
        }
        return sb.ToString().TrimEnd();
    }
}

public sealed class IifWriter
{
    public const string TrnsHeader = "!TRNS\tTRNSID\tTRNSTYPE\tDATE\tACCNT\tNAME\tCLASS\tAMOUNT\tDOCNUM\tMEMO\tCLEAR";
    public const string SplHeader = "!SPL\tSPLID\tTRNSTYPE\tDATE\tACCNT\tNAME\tCLASS\tAMOUNT\tDOCNUM\tMEMO\tCLEAR";
    public const string EndHeader = "!ENDTRNS";

    public static Encoding Encoding { get; } = CreateEncoding();

    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
    }

    /// <summary>Writes cp1252 bytes with CRLF line endings.</summary>
    public static void Write(Stream output, IEnumerable<IifTransaction> txns)
    {
        using var w = new StreamWriter(output, Encoding, leaveOpen: true) { NewLine = "\r\n" };
        Write(w, txns);
    }

    public static byte[] WriteToBytes(IEnumerable<IifTransaction> txns)
    {
        using var ms = new MemoryStream();
        Write(ms, txns);
        return ms.ToArray();
    }

    public static void Write(TextWriter w, IEnumerable<IifTransaction> txns)
    {
        w.WriteLine(TrnsHeader);
        w.WriteLine(SplHeader);
        w.WriteLine(EndHeader);

        foreach (var t in txns)
        {
            if (t.Splits.Count == 0)
                throw new InvalidOperationException($"IIF transaction on {t.Date} for {t.Account} has no split lines.");
            long splSum = t.Splits.Sum(s => s.AmountCents);
            if (splSum != -t.AmountCents)
                throw new InvalidOperationException(
                    $"IIF transaction on {t.Date} for {t.Account} is unbalanced: TRNS {Money.FormatIif(t.AmountCents)}, SPL total {Money.FormatIif(splSum)}.");

            var date = t.Date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
            var type = IifSanitizer.Clean(t.TrnsType);
            var doc = IifSanitizer.Clean(t.DocNum);

            WriteRow(w, "TRNS", type, date, t.Account, t.Name, t.Class, t.AmountCents, doc, t.Memo, t.Clear ? "Y" : "N");
            foreach (var s in t.Splits)
                WriteRow(w, "SPL", type, date, s.Account, s.Name, s.Class, s.AmountCents, doc, s.Memo, "N");
            w.WriteLine("ENDTRNS");
        }
        w.Flush();
    }

    private static void WriteRow(TextWriter w, string kind, string type, string date, string account,
        string? name, string? cls, long cents, string doc, string? memo, string clear)
    {
        w.Write(kind);
        w.Write("\t\t"); // empty TRNSID / SPLID
        w.Write(type); w.Write('\t');
        w.Write(date); w.Write('\t');
        w.Write(IifSanitizer.Clean(account)); w.Write('\t');
        w.Write(IifSanitizer.Clean(name)); w.Write('\t');
        w.Write(IifSanitizer.Clean(cls)); w.Write('\t');
        w.Write(Money.FormatIif(cents)); w.Write('\t');
        w.Write(doc); w.Write('\t');
        w.Write(IifSanitizer.Clean(memo)); w.Write('\t');
        w.WriteLine(clear);
    }
}
