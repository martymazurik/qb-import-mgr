using System.Security.Cryptography;
using System.Text;

namespace QbImportMgr.Core.Domain;

/// <summary>Full account numbers are never stored: only a mask and a hash for matching.</summary>
public static class AccountIdentity
{
    public static string Mask(string acctId)
    {
        var clean = Normalize(acctId);
        return clean.Length <= 4 ? clean : "****" + clean[^4..];
    }

    public static string Hash(AccountKind kind, string? bankId, string acctId)
    {
        var key = $"{kind.ToCode()}|{Normalize(bankId ?? "")}|{Normalize(acctId)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private static string Normalize(string s) =>
        new(s.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
}
