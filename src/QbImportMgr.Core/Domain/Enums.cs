namespace QbImportMgr.Core.Domain;

public enum AccountKind { Bank, CreditCard }

public enum TxnStatus
{
    New, Categorized, Ready, Exported, Excluded, Duplicate,
    /// <summary>Matched to a row in the QuickBooks register: already entered, never exported.</summary>
    InQb,
    /// <summary>More than one QuickBooks register row could be this transaction; pick one.</summary>
    Ambiguous,
    /// <summary>Deposit from an invoiced customer: record with Receive Payments in QB, then re-import the register.</summary>
    HoldReceivePayment,
    /// <summary>The "in" side of a transfer between mapped accounts; the "out" side carries the export.</summary>
    Paired,
}

public static class TxnStatusExtensions
{
    /// <summary>Statuses that never reach an IIF export.</summary>
    public static bool IsNonExporting(this TxnStatus s) =>
        s is TxnStatus.Excluded or TxnStatus.Duplicate or TxnStatus.InQb or TxnStatus.Ambiguous or TxnStatus.HoldReceivePayment or TxnStatus.Paired;
}

/// <summary>Export batch lifecycle. Exported = IIF waiting in the iif folder for QuickBooks.</summary>
public enum BatchStatus { Exported, ImportedToQb, Voided }

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
