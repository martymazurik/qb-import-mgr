using System.Globalization;
using QbImportMgr.Core.Domain;

namespace QbImportMgr.Core.Ofx;

public sealed record OfxTransaction(
    string TrnType,
    DateOnly DtPosted,
    DateOnly? DtUser,
    long AmountCents,
    string? FitId,
    string? CheckNum,
    string? Name,
    string? Memo);

public sealed record OfxStatement(
    AccountKind Kind,
    string? BankId,
    string AcctId,
    string? AcctType,
    string? CurDef,
    DateOnly? DtStart,
    DateOnly? DtEnd,
    long? LedgerBalCents,
    DateOnly? LedgerBalDate,
    IReadOnlyList<OfxTransaction> Transactions);

public sealed record OfxDocument(
    OfxFormat Format,
    string Version,
    string EncodingName,
    IReadOnlyList<OfxStatement> Statements,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Institution id from the sign-on: INTU.BID (Quicken/QuickBooks) or FI/FID.</summary>
    public string? FiId { get; init; }

    /// <summary>Institution name from SONRS/FI/ORG.</summary>
    public string? FiOrg { get; init; }
}

public static class OfxParser
{
    public static OfxDocument Parse(byte[] bytes)
    {
        var decoded = OfxHeaderReader.Decode(bytes);
        var root = OfxTreeBuilder.Build(OfxTokenizer.Tokenize(decoded.Body), decoded.Format);
        var warnings = new List<string>();

        foreach (var sonrs in root.Descendants("SONRS"))
            CheckStatus(sonrs, "Sign-on", warnings);

        var statements = new List<OfxStatement>();
        foreach (var rs in root.Descendants("STMTRS"))
            if (ParseStatement(rs, AccountKind.Bank, "BANKACCTFROM", warnings) is { } s) statements.Add(s);
        foreach (var rs in root.Descendants("CCSTMTRS"))
            if (ParseStatement(rs, AccountKind.CreditCard, "CCACCTFROM", warnings) is { } s) statements.Add(s);

        foreach (var trnrs in root.Descendants("STMTTRNRS").Concat(root.Descendants("CCSTMTTRNRS")))
            CheckStatus(trnrs, "Statement", warnings);

        if (root.Descendants("INVSTMTRS").Any())
            warnings.Add("Investment statements (INVSTMTRS) are not supported and were skipped.");
        if (statements.Count == 0)
            warnings.Add("File contains no bank or credit card statements.");

        var sonrsFirst = root.Descendants("SONRS").FirstOrDefault();
        var fi = sonrsFirst?.Child("FI");
        return new OfxDocument(decoded.Format, decoded.Version, decoded.Encoding.WebName, statements, warnings)
        {
            FiId = Clean(sonrsFirst?.Child("INTU.BID")?.Value) ?? Clean(fi?.Get("FID")),
            FiOrg = Clean(fi?.Get("ORG")),
        };
    }

    private static OfxStatement? ParseStatement(OfxElement rs, AccountKind kind, string acctAggregate, List<string> warnings)
    {
        var acct = rs.Child(acctAggregate) ?? rs.Descendants(acctAggregate).FirstOrDefault();
        var acctId = acct?.Get("ACCTID");
        if (string.IsNullOrWhiteSpace(acctId))
        {
            warnings.Add($"{rs.Name} without {acctAggregate}/ACCTID skipped.");
            return null;
        }

        var tranList = rs.Child("BANKTRANLIST") ?? rs.Descendants("BANKTRANLIST").FirstOrDefault();
        var ledger = rs.Child("LEDGERBAL") ?? rs.Descendants("LEDGERBAL").FirstOrDefault();

        var txns = new List<OfxTransaction>();
        if (tranList is not null)
        {
            foreach (var t in tranList.Descendants("STMTTRN"))
            {
                if (ParseTxn(t, acctId, warnings) is { } txn) txns.Add(txn);
            }
        }

        long? ledgerCents = Money.TryParseCents(ledger?.Get("BALAMT"), out var lc) ? lc : null;

        return new OfxStatement(
            kind,
            Clean(acct!.Get("BANKID")),
            acctId.Trim(),
            Clean(acct.Get("ACCTTYPE")),
            Clean(rs.Get("CURDEF")),
            ParseDate(tranList?.Child("DTSTART")?.Value),
            ParseDate(tranList?.Child("DTEND")?.Value),
            ledgerCents,
            ParseDate(ledger?.Get("DTASOF")),
            txns);
    }

    private static OfxTransaction? ParseTxn(OfxElement t, string acctId, List<string> warnings)
    {
        var fitId = Clean(t.Child("FITID")?.Value);
        var posted = ParseDate(t.Child("DTPOSTED")?.Value);
        if (posted is null)
        {
            warnings.Add($"Account {AccountIdentity.Mask(acctId)}: transaction {fitId ?? "(no FITID)"} has no valid DTPOSTED; skipped.");
            return null;
        }
        if (!Money.TryParseCents(t.Child("TRNAMT")?.Value, out var cents))
        {
            warnings.Add($"Account {AccountIdentity.Mask(acctId)}: transaction {fitId ?? "(no FITID)"} has no valid TRNAMT; skipped.");
            return null;
        }

        var name = Clean(t.Child("NAME")?.Value) ?? Clean(t.Child("PAYEE")?.Get("NAME"));
        return new OfxTransaction(
            (Clean(t.Child("TRNTYPE")?.Value) ?? "OTHER").ToUpperInvariant(),
            posted.Value,
            ParseDate(t.Child("DTUSER")?.Value),
            cents,
            fitId,
            Clean(t.Child("CHECKNUM")?.Value),
            name,
            Clean(t.Child("MEMO")?.Value));
    }

    private static void CheckStatus(OfxElement aggregate, string what, List<string> warnings)
    {
        var status = aggregate.Child("STATUS");
        var code = status?.Get("CODE");
        if (code is not null && code != "0")
            warnings.Add($"{what} status {code} {status!.Get("SEVERITY")}: {status.Get("MESSAGE")}".TrimEnd(' ', ':'));
    }

    /// <summary>OFX dates are YYYYMMDD[HHMMSS[.XXX]][[offset:TZ]]; the calendar date is the first 8 digits.</summary>
    public static DateOnly? ParseDate(string? s)
    {
        if (s is null) return null;
        s = s.Trim();
        if (s.Length < 8) return null;
        return DateOnly.TryParseExact(s[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
