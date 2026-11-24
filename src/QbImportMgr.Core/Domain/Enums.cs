namespace QbImportMgr.Core.Domain;

public enum AccountKind { Bank, CreditCard }

public enum TxnStatus { New, Categorized, Ready, Exported, Excluded, Duplicate }

public enum RuleField { Payee, Memo, Any }

public enum MatchKind { Contains, StartsWith, Regex }

public enum SignFilter { Any, In, Out }

public enum QbNameType { Customer, Vendor, OtherName, Employee }

public static class EnumCodes
{
    public static string ToCode(this AccountKind k) => k == AccountKind.Bank ? "BANK" : "CC";

    public static AccountKind ParseAccountKind(string code) => code switch
    {
        "BANK" => AccountKind.Bank,
        "CC" => AccountKind.CreditCard,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown account kind"),
    };

    public static string ToCode(this QbNameType t) => t switch
    {
        QbNameType.Customer => "CUST",
        QbNameType.Vendor => "VEND",
        QbNameType.OtherName => "OTHER",
        QbNameType.Employee => "EMP",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public static QbNameType ParseQbNameType(string code) => code switch
    {
        "CUST" => QbNameType.Customer,
        "VEND" => QbNameType.Vendor,
        "OTHER" => QbNameType.OtherName,
        "EMP" => QbNameType.Employee,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown name type"),
    };
}
