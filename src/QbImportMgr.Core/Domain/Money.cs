using System.Globalization;

namespace QbImportMgr.Core.Domain;

public static class Money
{
    /// <summary>Parses an OFX amount ("-12.34", "+5", "1,234.56", "12,34") into cents.</summary>
    public static bool TryParseCents(string? text, out long cents)
    {
        cents = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().Replace(" ", "");
        int comma = s.LastIndexOf(','), dot = s.IndexOf('.');
        if (comma >= 0 && dot < 0 && s.Length - comma - 1 is 1 or 2)
            s = s.Remove(comma, 1).Insert(comma, ".");   // European decimal comma
        else
            s = s.Replace(",", "");                      // thousands separator
        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var d))
            return false;
        cents = (long)Math.Round(d * 100m, MidpointRounding.AwayFromZero);
        return true;
    }

    public static string FormatIif(long cents) =>
        (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    public static string FormatDisplay(long cents) =>
        (cents / 100m).ToString("#,##0.00;-#,##0.00", CultureInfo.InvariantCulture);
}
