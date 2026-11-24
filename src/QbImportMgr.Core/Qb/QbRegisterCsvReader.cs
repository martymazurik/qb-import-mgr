using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using QbImportMgr.Core.Domain;

namespace QbImportMgr.Core.Qb;

/// <summary>One detail row of a QuickBooks Transaction Detail report. AmountCents is signed for the register account: money in positive.</summary>
public sealed record QbRegisterRow(
    int LineNumber,
    string? Account,
    DateOnly Date,
    string Type,
    string? Num,
    string? Name,
    string? Memo,
    string? Clr,
    string? Split,
    long AmountCents);

public sealed record QbRegisterFile(
    IReadOnlyList<QbRegisterRow> Rows,
    bool HasAccountColumn,
    bool UsesDebitCredit,
    int SkippedRows,
    IReadOnlyList<string> Warnings);

public sealed class QbRegisterFormatException(string message) : Exception(message);

/// <summary>
/// Reads a QB Desktop "Transaction Detail" report saved as CSV. Supports a Debit/Credit pair or a
/// single Amount column, skips title, group-header, subtotal and total rows, and accepts
/// (123.45), -123.45, $1,234.56 and trailing-minus amounts.
/// </summary>
public static partial class QbRegisterCsvReader
{
    public static QbRegisterFile Read(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        else
        {
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { text = Encoding.GetEncoding(1252).GetString(bytes); }
        }
        return Read(text);
    }

    public static QbRegisterFile Read(string text)
    {
        var rows = ParseCsv(text);
        int headerIndex = rows.FindIndex(IsHeader);
        if (headerIndex < 0)
            throw new QbRegisterFormatException(
                "No header row with Type, Date and Amount (or Debit/Credit) columns. In QuickBooks use Reports > Custom Reports > Transaction Detail and save as CSV.");

        var header = rows[headerIndex].Select(h => h.Trim()).ToList();
        int Col(params string[] names) =>
            header.FindIndex(h => names.Any(n => h.Equals(n, StringComparison.OrdinalIgnoreCase)));

        int cType = Col("Type", "Transaction Type"), cDate = Col("Date"), cNum = Col("Num", "No.", "Number");
        int cName = Col("Name", "Source Name"), cMemo = Col("Memo", "Memo/Description", "Description");
        int cAccount = Col("Account", "Account Name"), cClr = Col("Clr", "Cleared"), cSplit = Col("Split", "Split Account");
        int cDebit = Col("Debit"), cCredit = Col("Credit"), cAmount = Col("Amount");
        bool debitCredit = cDebit >= 0 || cCredit >= 0;
        if (!debitCredit && cAmount < 0)
            throw new QbRegisterFormatException("The report needs either Debit and Credit columns or an Amount column.");

        var result = new List<QbRegisterRow>();
        var warnings = new List<string>();
        int skipped = 0;
        string? group = null;
        int firstDataCol = new[] { cType, cDate }.Where(c => c >= 0).Min();

        for (int i = headerIndex + 1; i < rows.Count; i++)
        {
            var r = rows[i];
            string? Cell(int c) => c >= 0 && c < r.Count && r[c].Trim().Length > 0 ? r[c].Trim() : null;

            var dateText = Cell(cDate);
            if (dateText is null || !TryParseDate(dateText, out var date))
            {
                // Group headers ("Checking") sit left of the data columns; subtotal rows start with "Total".
                var label = r.Take(firstDataCol).Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0);
                if (label is not null && !label.StartsWith("Total", StringComparison.OrdinalIgnoreCase) && r.Skip(firstDataCol).All(x => x.Trim().Length == 0))
                    group = label;
                if (r.Any(x => x.Trim().Length > 0)) skipped++;
                continue;
            }

            long cents;
            if (debitCredit)
            {
                bool hasD = TryParseAmount(Cell(cDebit), out var d);
                bool hasC = TryParseAmount(Cell(cCredit), out var c);
                if (!hasD && !hasC)
                {
                    warnings.Add($"Line {i + 1}: no Debit or Credit amount; skipped.");
                    skipped++;
                    continue;
                }
                cents = (hasD ? d : 0) - (hasC ? c : 0);
            }
            else if (!TryParseAmount(Cell(cAmount), out cents))
            {
                warnings.Add($"Line {i + 1}: no Amount; skipped.");
                skipped++;
                continue;
            }

            var type = Cell(cType) ?? "";
            result.Add(new QbRegisterRow(
                i + 1,
                cAccount >= 0 ? CleanAccount(Cell(cAccount) ?? group) : null,
                date, type, Cell(cNum), Cell(cName), Cell(cMemo), Cell(cClr),
                CleanAccount(Cell(cSplit)), cents));
        }

        if (result.Count == 0) warnings.Add("No transaction rows found under the header.");
        return new QbRegisterFile(result, cAccount >= 0, debitCredit, skipped, warnings);
    }

    private static bool IsHeader(List<string> row)
    {
        var cells = row.Select(c => c.Trim()).ToList();
        bool Has(string n) => cells.Any(c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
        return Has("Date") && (Has("Type") || Has("Transaction Type")) && (Has("Amount") || Has("Debit") || Has("Credit"));
    }

    /// <summary>Removes the account-number prefix QB adds when account numbers are on ("1000 · Checking").</summary>
    internal static string? CleanAccount(string? s)
    {
        if (s is null) return null;
        s = AccountNumberPrefix().Replace(s.Trim(), "");
        return s.Length == 0 ? null : s;
    }

    private static readonly string[] DateFormats = ["MM/dd/yyyy", "M/d/yyyy", "MM/dd/yy", "M/d/yy", "yyyy-MM-dd"];

    public static bool TryParseDate(string s, out DateOnly date) =>
        DateOnly.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>Parses 123.45, -123.45, (123.45), $1,234.56, 1,234.56- into cents.</summary>
    public static bool TryParseAmount(string? s, out long cents)
    {
        cents = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim().Replace("$", "").Replace(" ", "");
        bool negative = false;
        if (t.StartsWith('(') && t.EndsWith(')')) { negative = true; t = t[1..^1]; }
        if (t.EndsWith('-')) { negative = !negative; t = t[..^1]; }
        if (t.StartsWith('-')) { negative = !negative; t = t[1..]; }
        t = t.Replace(",", "");
        if (t.Length == 0 || !decimal.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)) return false;
        cents = (long)Math.Round(d * 100m, MidpointRounding.AwayFromZero) * (negative ? -1 : 1);
        return true;
    }

    /// <summary>RFC 4180 CSV: quoted fields, doubled quotes, commas and line breaks inside quotes.</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = [];
                    break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    [GeneratedRegex(@"^\d+\s*·\s*")]
    private static partial Regex AccountNumberPrefix();
}
