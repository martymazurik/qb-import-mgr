using System.Text;
using QbImportMgr.Core.Domain;

namespace QbImportMgr.Core.Iif;

public sealed record IifAccount(string FullName, string? AccntType, bool Hidden);

public sealed record IifName(string Name, QbNameType Type, bool Hidden);

public sealed record IifLists(
    IReadOnlyList<IifAccount> Accounts,
    IReadOnlyList<IifName> Names,
    IReadOnlyList<string> Classes,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads a QuickBooks list export (File > Utilities > Export > Lists to IIF Files). Each "!XXX"
/// header line defines the columns for the "XXX" rows after it.
/// </summary>
public static class IifListReader
{
    private static readonly Dictionary<string, QbNameType> NameRecords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CUST"] = QbNameType.Customer,
        ["VEND"] = QbNameType.Vendor,
        ["OTHERNAME"] = QbNameType.OtherName,
        ["EMP"] = QbNameType.Employee,
    };

    public static IifLists Read(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = bom ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.GetEncoding(1252).GetString(bytes);
        return Read(text);
    }

    public static IifLists Read(string text)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var accounts = new List<IifAccount>();
        var names = new List<IifName>();
        var classes = new List<string>();
        var warnings = new List<string>();
        int lineNo = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            lineNo++;
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            var cells = line.Split('\t').Select(Unquote).ToArray();
            var rec = cells[0];

            if (rec.StartsWith('!'))
            {
                headers[rec[1..]] = cells;
                continue;
            }
            if (!headers.TryGetValue(rec, out var hdr)) continue; // e.g. ENDTRNS or unknown record

            string? Col(string col)
            {
                int idx = Array.FindIndex(hdr, h => h.Equals(col, StringComparison.OrdinalIgnoreCase));
                return idx >= 0 && idx < cells.Length && cells[idx].Length > 0 ? cells[idx] : null;
            }
            bool hidden = Col("HIDDEN") is "Y" or "y";
            var name = Col("NAME");
            if (name is null)
            {
                if (rec is "ACCNT" or "CLASS" || NameRecords.ContainsKey(rec))
                    warnings.Add($"Line {lineNo}: {rec} row without NAME skipped.");
                continue;
            }

            if (rec.Equals("ACCNT", StringComparison.OrdinalIgnoreCase))
                accounts.Add(new IifAccount(name, Col("ACCNTTYPE"), hidden));
            else if (rec.Equals("CLASS", StringComparison.OrdinalIgnoreCase))
                classes.Add(name);
            else if (NameRecords.TryGetValue(rec, out var type))
                names.Add(new IifName(name, type, hidden));
        }

        return new IifLists(accounts, names, classes, warnings);
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1].Replace("\"\"", "\"") : s;
    }
}
