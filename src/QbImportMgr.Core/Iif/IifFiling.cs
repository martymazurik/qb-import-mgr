using System.Globalization;
using System.Text.RegularExpressions;

namespace QbImportMgr.Core.Iif;

public enum IifSideFileKind { QbErrorFile, FixedFile }

/// <summary>Naming rules for filing IIF files by month. No file I/O here.</summary>
public static partial class IifFiling
{
    public const string VoidedSuffix = ".voided";

    /// <summary>"2026-11": the month folder for a batch, from its creation time in local time.</summary>
    public static string MonthFolder(DateTime createdUtc, TimeZoneInfo local) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc), local)
            .ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// QuickBooks error files (*_batch*_&lt;digits&gt;.iif, written next to an import that had errors) and
    /// hand-corrected *_fixed.iif files. The batch id comes from "_batch&lt;id&gt;" in the name when present.
    /// Our own export name (…_batch12.iif) is not a side file.
    /// </summary>
    public static bool TryClassifySideFile(string fileName, out IifSideFileKind kind, out long? batchId)
    {
        kind = default;
        batchId = null;
        if (!fileName.EndsWith(".iif", StringComparison.OrdinalIgnoreCase)) return false;

        if (FixedPattern().IsMatch(fileName)) kind = IifSideFileKind.FixedFile;
        else if (ErrorPattern().IsMatch(fileName)) kind = IifSideFileKind.QbErrorFile;
        else return false;

        var m = BatchIdPattern().Match(fileName);
        if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) batchId = id;
        return true;
    }

    /// <summary>The file name with " (2)", " (3)"… before the extension until <paramref name="exists"/> says it is free.</summary>
    public static string FreeName(string folder, string fileName, Func<string, bool> exists)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!exists(candidate)) return candidate;

        // Keep a double extension like ".iif.voided" together.
        string stem = fileName, ext = "";
        if (fileName.EndsWith(".iif" + VoidedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            ext = fileName[^(".iif" + VoidedSuffix).Length..];
            stem = fileName[..^ext.Length];
        }
        else
        {
            ext = Path.GetExtension(fileName);
            stem = Path.GetFileNameWithoutExtension(fileName);
        }
        for (int i = 2; ; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
    }

    [GeneratedRegex(@"_batch[^\\/]*_\d+\.iif$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorPattern();

    [GeneratedRegex(@"_fixed\.iif$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FixedPattern();

    [GeneratedRegex(@"_batch(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BatchIdPattern();
}
