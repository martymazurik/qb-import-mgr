namespace QbImportMgr.Core.Domain;

public sealed class BankAccount
{
    public long Id { get; set; }
    public string OfxAcctIdMasked { get; set; } = "";
    public string AcctIdHash { get; set; } = "";
    public string? BankId { get; set; }
    public AccountKind Kind { get; set; }
    public string DisplayName { get; set; } = "";
    public string? QbAccountName { get; set; }
    public bool InvertSign { get; set; }
    public DateTime CreatedUtc { get; set; }

    /// <summary>Institution id from the download (INTU.BID or FI/FID) and name (FI/ORG).</summary>
    public string? FiId { get; set; }
    public string? FiOrg { get; set; }
}

public sealed class QboFile
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public byte[] RawBytes { get; set; } = [];
    public DateTime ImportedUtc { get; set; }
}

public sealed class QboStatement
{
    public long Id { get; set; }
    public long QboFileId { get; set; }
    public long BankAccountId { get; set; }
    public DateOnly? DtStart { get; set; }
    public DateOnly? DtEnd { get; set; }
    public long? LedgerBalCents { get; set; }
    public DateOnly? LedgerBalDate { get; set; }
    public int TxnCount { get; set; }
}

public sealed class Txn
{
    public long Id { get; set; }
    public long BankAccountId { get; set; }
    public long QboStatementId { get; set; }

    // Raw bank values: immutable once inserted.
    public string? FitId { get; set; }
    public string TrnType { get; set; } = "";
    public DateOnly DtPosted { get; set; }
    public DateOnly? DtUser { get; set; }
    public long AmountCents { get; set; }
    public string? Name { get; set; }
    public string? Memo { get; set; }
    public string? CheckNum { get; set; }

    /// <summary>AmountCents normalized with BankAccount.InvertSign; money out is negative. Use this downstream.</summary>
    public long SignedAmountCents { get; set; }

    // Overrides (null = use raw).
    public DateOnly? OvrDate { get; set; }
    public string? OvrPayee { get; set; }
    public string? OvrMemo { get; set; }
    public string? OvrCheckNum { get; set; }

    public long? CategoryId { get; set; }
    public long? QbNameId { get; set; }
    public string? Class { get; set; }
    public TxnStatus Status { get; set; } = TxnStatus.New;
    public long? DuplicateOfTxnId { get; set; }
    public long? RuleId { get; set; }
    public long? BatchId { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>The QuickBooks register row this transaction was matched to.</summary>
    public long? QbRegisterTxnId { get; set; }

    /// <summary>The other side of a transfer between mapped accounts.</summary>
    public long? PairedTxnId { get; set; }

    public DateOnly EffectiveDate => OvrDate ?? DtPosted;
    public string EffectivePayee => OvrPayee ?? Name ?? "";
    public string EffectiveMemo => OvrMemo ?? Memo ?? "";
    public string EffectiveCheckNum => OvrCheckNum ?? CheckNum ?? "";
    public bool IsLocked => Status == TxnStatus.Exported;
}

public sealed class TxnSplit
{
    public long Id { get; set; }
    public long TxnId { get; set; }
    public int Seq { get; set; }
    public long CategoryId { get; set; }
    public long? QbNameId { get; set; }
    public string? Class { get; set; }

    /// <summary>Same sign convention as Txn.SignedAmountCents; a txn's splits sum to it.</summary>
    public long AmountCents { get; set; }
    public string? Memo { get; set; }
}

public sealed class Category
{
    public long Id { get; set; }
    public string FullName { get; set; } = "";
    public string? AccntType { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime ImportedUtc { get; set; }
}

public sealed class QbName
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public QbNameType NameType { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Customer paid by invoice: deposits wait for Receive Payments in QB instead of exporting.</summary>
    public bool IsInvoiced { get; set; }
}

public sealed class Rule
{
    public long Id { get; set; }
    public int Priority { get; set; } = 100;
    public RuleField Field { get; set; } = RuleField.Payee;
    public MatchKind MatchKind { get; set; } = MatchKind.Contains;
    public string Pattern { get; set; } = "";
    public SignFilter Sign { get; set; } = SignFilter.Any;

    /// <summary>Compared against the absolute value of SignedAmountCents.</summary>
    public long? MinCents { get; set; }
    public long? MaxCents { get; set; }
    public long? BankAccountId { get; set; }
    public long? CategoryId { get; set; }
    public long? QbNameId { get; set; }
    public string? Class { get; set; }
    public bool AutoExclude { get; set; }
    public int HitCount { get; set; }
    public DateTime? LastHitUtc { get; set; }
    public bool IsEnabled { get; set; } = true;
    public long? SplitTemplateId { get; set; }
    public string Source { get; set; } = RuleSources.Manual;
    public bool IsLearned => Source == RuleSources.LearnedFromQb;
}

public static class RuleSources
{
    public const string Manual = "Manual";
    public const string LearnedFromQb = "Learned from QB";
}

/// <summary>A row from a QuickBooks Transaction Detail export. AmountCents is signed for the register account.</summary>
public sealed class QbRegisterTxn
{
    public long Id { get; set; }
    public long? BankAccountId { get; set; }
    public string? QbAccountName { get; set; }
    public DateOnly TxnDate { get; set; }
    public string TxnType { get; set; } = "";
    public string? Num { get; set; }
    public string? Name { get; set; }
    public string? Memo { get; set; }
    public string? Clr { get; set; }
    public string? SplitAccount { get; set; }
    public long AmountCents { get; set; }
    public string SourceFile { get; set; } = "";
    public DateTime ImportedUtc { get; set; }
    public string RowKey { get; set; } = "";
}

public sealed class PayeeAlias
{
    public long Id { get; set; }
    public string RawPattern { get; set; } = "";
    public MatchKind MatchKind { get; set; } = MatchKind.Contains;
    public string CleanPayee { get; set; } = "";
    public long? QbNameId { get; set; }
    public string Source { get; set; } = RuleSources.Manual;
}

public sealed class ExportBatch
{
    public long Id { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string IifPath { get; set; } = "";
    public string? BackupPath { get; set; }
    public int TxnCount { get; set; }
    public long TotalCents { get; set; }
    public DateTime? VoidedUtc { get; set; }
}

public sealed class AuditLogEntry
{
    public long Id { get; set; }
    public DateTime Utc { get; set; }
    public string Entity { get; set; } = "";
    public long EntityId { get; set; }
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
}
