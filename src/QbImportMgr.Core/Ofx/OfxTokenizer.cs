using System.Globalization;
using System.Text;

namespace QbImportMgr.Core.Ofx;

public enum OfxTokenKind { StartTag, EndTag, Text }

public readonly record struct OfxToken(OfxTokenKind Kind, string Value);

/// <summary>
/// Tolerant tokenizer for both SGML and XML OFX bodies. Skips processing instructions, comments
/// and declarations; ignores attributes; decodes known entities and leaves a raw '&amp;' as-is.
/// </summary>
public static class OfxTokenizer
{
    public static IEnumerable<OfxToken> Tokenize(string body)
    {
        int i = 0, n = body.Length;
        while (i < n)
        {
            char c = body[i];
            if (c == '<')
            {
                if (Peek(body, i, "<?")) { i = SkipPast(body, i, "?>"); continue; }
                if (Peek(body, i, "<!--")) { i = SkipPast(body, i, "-->"); continue; }
                if (Peek(body, i, "<!")) { i = SkipPast(body, i, ">"); continue; }

                int close = body.IndexOf('>', i + 1);
                if (close < 0) yield break; // truncated file: stop at last complete tag
                var inner = body.AsSpan(i + 1, close - i - 1).Trim();
                i = close + 1;
                if (inner.IsEmpty) continue;

                if (inner[0] == '/')
                {
                    yield return new OfxToken(OfxTokenKind.EndTag, TagName(inner[1..]));
                }
                else
                {
                    bool selfClosing = inner[^1] == '/';
                    var name = TagName(selfClosing ? inner[..^1] : inner);
                    yield return new OfxToken(OfxTokenKind.StartTag, name);
                    if (selfClosing) yield return new OfxToken(OfxTokenKind.EndTag, name);
                }
            }
            else
            {
                int next = body.IndexOf('<', i);
                if (next < 0) next = n;
                var raw = body.AsSpan(i, next - i).Trim();
                i = next;
                if (!raw.IsEmpty)
                    yield return new OfxToken(OfxTokenKind.Text, DecodeEntities(raw.ToString()));
            }
        }
    }

    private static string TagName(ReadOnlySpan<char> inner)
    {
        inner = inner.Trim();
        int ws = inner.IndexOfAny(" \t\r\n");
        if (ws >= 0) inner = inner[..ws];
        return inner.ToString().ToUpperInvariant();
    }

    private static bool Peek(string s, int i, string what) =>
        string.CompareOrdinal(s, i, what, 0, what.Length) == 0;

    private static int SkipPast(string s, int i, string terminator)
    {
        int idx = s.IndexOf(terminator, i, StringComparison.Ordinal);
        return idx < 0 ? s.Length : idx + terminator.Length;
    }

    public static string DecodeEntities(string s)
    {
        if (!s.Contains('&')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '&')
            {
                int semi = s.IndexOf(';', i + 1);
                if (semi > i + 1 && semi - i <= 10 && TryEntity(s.AsSpan(i + 1, semi - i - 1), out var decoded))
                {
                    sb.Append(decoded);
                    i = semi;
                    continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static bool TryEntity(ReadOnlySpan<char> e, out string decoded)
    {
        decoded = e switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            "apos" => "'",
            "nbsp" => " ",
            _ => "",
        };
        if (decoded.Length > 0) return true;
        if (e.Length > 1 && e[0] == '#')
        {
            bool ok = e[1] is 'x' or 'X'
                ? int.TryParse(e[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp)
                : int.TryParse(e[1..], NumberStyles.None, CultureInfo.InvariantCulture, out cp);
            if (ok && cp is > 0 and <= 0x10FFFF)
            {
                decoded = char.ConvertFromUtf32(cp);
                return true;
            }
        }
        return false;
    }
}
