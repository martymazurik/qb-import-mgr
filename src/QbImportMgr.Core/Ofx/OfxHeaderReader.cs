using System.Text;
using System.Text.RegularExpressions;

namespace QbImportMgr.Core.Ofx;

public enum OfxFormat { Sgml, Xml }

public sealed record OfxDecoded(OfxFormat Format, string Version, Encoding Encoding, IReadOnlyDictionary<string, string> Header, string Body);

/// <summary>Detects OFX 1.x (SGML header) vs 2.x (XML), picks the text encoding, returns the body from &lt;OFX&gt; on.</summary>
public static partial class OfxHeaderReader
{
    static OfxHeaderReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Windows1252 => Encoding.GetEncoding(1252);

    public static OfxDecoded Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        int offset = HasUtf8Bom(bytes) ? 3 : 0;

        // Latin1 maps every byte 1:1, so it is safe for sniffing the ASCII header.
        var sniff = Encoding.Latin1.GetString(bytes, offset, Math.Min(bytes.Length - offset, 4096));
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        OfxFormat format;
        string version;
        string? declared;

        var trimmed = sniff.TrimStart();
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<?OFX", StringComparison.OrdinalIgnoreCase))
        {
            format = OfxFormat.Xml;
            declared = XmlEncodingRegex().Match(sniff) is { Success: true } m ? m.Groups[1].Value : "UTF-8";
            version = OfxPiVersionRegex().Match(sniff) is { Success: true } v ? v.Groups[1].Value : "200";
            header["ENCODING"] = declared;
            header["VERSION"] = version;
        }
        else if (sniff.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase) ||
                 sniff.Contains("<OFX>", StringComparison.OrdinalIgnoreCase))
        {
            format = OfxFormat.Sgml;
            int end = sniff.IndexOf('<');
            var headerText = end < 0 ? sniff : sniff[..end];
            foreach (var raw in headerText.Split('\n'))
            {
                var line = raw.Trim();
                int colon = line.IndexOf(':');
                if (colon > 0) header[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            version = header.GetValueOrDefault("VERSION", "102");
            var enc = header.GetValueOrDefault("ENCODING", "USASCII");
            declared = enc.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) ? "UTF-8"
                     : header.GetValueOrDefault("CHARSET", "1252");
        }
        else
        {
            throw new OfxParseException("Not an OFX/QFX/QBO file: no OFX header or <OFX> element found.");
        }

        var encoding = ChooseEncoding(bytes, offset, declared);
        var text = encoding.GetString(bytes, offset, bytes.Length - offset);
        int ofxStart = text.IndexOf("<OFX>", StringComparison.OrdinalIgnoreCase);
        if (ofxStart < 0)
            throw new OfxParseException("No <OFX> element found.");
        return new OfxDecoded(format, version, encoding, header, text[ofxStart..]);
    }

    /// <summary>
    /// Banks routinely mislabel encodings, so the bytes decide: strictly valid UTF-8 containing
    /// multi-byte sequences is UTF-8; anything else is Windows-1252 (a superset of USASCII and,
    /// for printable characters, of ISO-8859-1).
    /// </summary>
    internal static Encoding ChooseEncoding(byte[] bytes, int offset, string? declared)
    {
        bool hasHigh = false;
        for (int i = offset; i < bytes.Length; i++)
            if (bytes[i] >= 0x80) { hasHigh = true; break; }

        bool declaredUtf8 = declared is not null &&
            declared.Replace("-", "").Equals("UTF8", StringComparison.OrdinalIgnoreCase);

        if (!hasHigh)
            return declaredUtf8 ? new UTF8Encoding(false) : Windows1252;

        return IsValidUtf8(bytes.AsSpan(offset)) ? new UTF8Encoding(false) : Windows1252;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> span)
    {
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool HasUtf8Bom(byte[] b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;

    [GeneratedRegex("""<\?xml[^>]*encoding\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex XmlEncodingRegex();

    [GeneratedRegex("""<\?OFX[^>]*VERSION\s*=\s*["']?(\d+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OfxPiVersionRegex();
}

public sealed class OfxParseException(string message) : Exception(message);
