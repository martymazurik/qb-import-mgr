-- qb-import-mgr schema v1
-- Money: INTEGER cents. Dates: TEXT yyyy-MM-dd. Timestamps: TEXT UTC ISO-8601.

CREATE TABLE BankAccount (
    Id              INTEGER PRIMARY KEY,
    OfxAcctIdMasked TEXT    NOT NULL,
    AcctIdHash      TEXT    NOT NULL UNIQUE,
    BankId          TEXT,
    Kind            TEXT    NOT NULL CHECK (Kind IN ('BANK','CC')),
    DisplayName     TEXT    NOT NULL,
    QbAccountName   TEXT,
    InvertSign      INTEGER NOT NULL DEFAULT 0 CHECK (InvertSign IN (0,1)),
    CreatedUtc      TEXT    NOT NULL
);

CREATE TABLE QboFile (
    Id          INTEGER PRIMARY KEY,
    FileName    TEXT NOT NULL,
    Sha256      TEXT NOT NULL UNIQUE,
    RawBytes    BLOB NOT NULL,
    ImportedUtc TEXT NOT NULL
);

CREATE TABLE QboStatement (
    Id             INTEGER PRIMARY KEY,
    QboFileId      INTEGER NOT NULL REFERENCES QboFile(Id),
    BankAccountId  INTEGER NOT NULL REFERENCES BankAccount(Id),
    DtStart        TEXT,
    DtEnd          TEXT,
    LedgerBalCents INTEGER,
    LedgerBalDate  TEXT,
    TxnCount       INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IX_QboStatement_Account ON QboStatement(BankAccountId, LedgerBalDate);
CREATE INDEX IX_QboStatement_File ON QboStatement(QboFileId);

CREATE TABLE Category (
    Id          INTEGER PRIMARY KEY,
    FullName    TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    AccntType   TEXT,
    IsActive    INTEGER NOT NULL DEFAULT 1 CHECK (IsActive IN (0,1)),
    ImportedUtc TEXT    NOT NULL
);

CREATE TABLE QbName (
    Id       INTEGER PRIMARY KEY,
    Name     TEXT    NOT NULL COLLATE NOCASE,
    NameType TEXT    NOT NULL CHECK (NameType IN ('CUST','VEND','OTHER','EMP')),
    IsActive INTEGER NOT NULL DEFAULT 1 CHECK (IsActive IN (0,1)),
    UNIQUE (Name, NameType)
);

CREATE TABLE Rule (
    Id            INTEGER PRIMARY KEY,
    Priority      INTEGER NOT NULL DEFAULT 100,
    Field         TEXT    NOT NULL DEFAULT 'Payee' CHECK (Field IN ('Payee','Memo','Any')),
    MatchKind     TEXT    NOT NULL DEFAULT 'Contains' CHECK (MatchKind IN ('Contains','StartsWith','Regex')),
    Pattern       TEXT    NOT NULL,
    Sign          TEXT    NOT NULL DEFAULT 'Any' CHECK (Sign IN ('Any','In','Out')),
    MinCents      INTEGER,
    MaxCents      INTEGER,
    BankAccountId INTEGER REFERENCES BankAccount(Id) ON DELETE CASCADE,
    CategoryId    INTEGER REFERENCES Category(Id),
    QbNameId      INTEGER REFERENCES QbName(Id),
    Class         TEXT,
    AutoExclude   INTEGER NOT NULL DEFAULT 0 CHECK (AutoExclude IN (0,1)),
    HitCount      INTEGER NOT NULL DEFAULT 0,
    LastHitUtc    TEXT,
    IsEnabled     INTEGER NOT NULL DEFAULT 1 CHECK (IsEnabled IN (0,1))
);

CREATE TABLE PayeeAlias (
    Id         INTEGER PRIMARY KEY,
    RawPattern TEXT NOT NULL,
    MatchKind  TEXT NOT NULL DEFAULT 'Contains' CHECK (MatchKind IN ('Contains','StartsWith','Regex')),
    CleanPayee TEXT NOT NULL,
    QbNameId   INTEGER REFERENCES QbName(Id)
);

CREATE TABLE ExportBatch (
    Id         INTEGER PRIMARY KEY,
    CreatedUtc TEXT    NOT NULL,
    IifPath    TEXT    NOT NULL,
    BackupPath TEXT,
    TxnCount   INTEGER NOT NULL,
    TotalCents INTEGER NOT NULL,
    VoidedUtc  TEXT
);

CREATE TABLE Txn (
    Id                INTEGER PRIMARY KEY,
    BankAccountId     INTEGER NOT NULL REFERENCES BankAccount(Id),
    QboStatementId    INTEGER NOT NULL REFERENCES QboStatement(Id),
    -- raw bank values (immutable, see TR_Txn_RawImmutable)
    FitId             TEXT,
    TrnType           TEXT    NOT NULL,
    DtPosted          TEXT    NOT NULL,
    DtUser            TEXT,
    AmountCents       INTEGER NOT NULL,
    Name              TEXT,
    Memo              TEXT,
    CheckNum          TEXT,
    -- AmountCents normalized with BankAccount.InvertSign
    SignedAmountCents INTEGER NOT NULL CHECK (SignedAmountCents = AmountCents OR SignedAmountCents = -AmountCents),
    -- overrides (NULL = use raw)
    OvrDate           TEXT,
    OvrPayee          TEXT,
    OvrMemo           TEXT,
    OvrCheckNum       TEXT,
    CategoryId        INTEGER REFERENCES Category(Id),
    QbNameId          INTEGER REFERENCES QbName(Id),
    Class             TEXT,
    Status            TEXT    NOT NULL DEFAULT 'New'
                      CHECK (Status IN ('New','Categorized','Ready','Exported','Excluded','Duplicate')),
    DuplicateOfTxnId  INTEGER REFERENCES Txn(Id),
    RuleId            INTEGER REFERENCES Rule(Id) ON DELETE SET NULL,
    BatchId           INTEGER REFERENCES ExportBatch(Id),
    UpdatedUtc        TEXT    NOT NULL,
    CHECK ((Status = 'Exported') = (BatchId IS NOT NULL))
);
CREATE UNIQUE INDEX UX_Txn_FitId ON Txn(BankAccountId, FitId) WHERE FitId IS NOT NULL AND FitId <> '';
CREATE INDEX IX_Txn_Account_Date ON Txn(BankAccountId, DtPosted);
CREATE INDEX IX_Txn_Statement ON Txn(QboStatementId);
CREATE INDEX IX_Txn_Batch ON Txn(BatchId);
CREATE INDEX IX_Txn_Status ON Txn(Status);

CREATE TABLE TxnSplit (
    Id          INTEGER PRIMARY KEY,
    TxnId       INTEGER NOT NULL REFERENCES Txn(Id) ON DELETE CASCADE,
    Seq         INTEGER NOT NULL,
    CategoryId  INTEGER NOT NULL REFERENCES Category(Id),
    QbNameId    INTEGER REFERENCES QbName(Id),
    Class       TEXT,
    AmountCents INTEGER NOT NULL,
    Memo        TEXT,
    UNIQUE (TxnId, Seq)
);

CREATE TABLE AuditLog (
    Id       INTEGER PRIMARY KEY,
    Utc      TEXT    NOT NULL,
    Entity   TEXT    NOT NULL,
    EntityId INTEGER NOT NULL,
    Action   TEXT    NOT NULL,
    Detail   TEXT
);
CREATE INDEX IX_AuditLog_Entity ON AuditLog(Entity, EntityId);

CREATE TABLE Setting (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);
INSERT INTO Setting (Key, Value) VALUES ('IifClear', 'Y');

-- ---------------------------------------------------------------- triggers

-- Raw bank values never change.
CREATE TRIGGER TR_Txn_RawImmutable BEFORE UPDATE ON Txn
WHEN OLD.FitId IS NOT NEW.FitId OR OLD.TrnType IS NOT NEW.TrnType OR OLD.DtPosted IS NOT NEW.DtPosted
  OR OLD.DtUser IS NOT NEW.DtUser OR OLD.AmountCents IS NOT NEW.AmountCents OR OLD.Name IS NOT NEW.Name
  OR OLD.Memo IS NOT NEW.Memo OR OLD.CheckNum IS NOT NEW.CheckNum
  OR OLD.BankAccountId IS NOT NEW.BankAccountId OR OLD.QboStatementId IS NOT NEW.QboStatementId
BEGIN
    SELECT RAISE(ABORT, 'Raw bank fields are immutable');
END;

-- Exported rows are locked. The only permitted change is the void: Exported -> Ready with
-- BatchId set to NULL, the batch already has VoidedUtc set, and nothing else changes but UpdatedUtc.
CREATE TRIGGER TR_Txn_ExportedLocked BEFORE UPDATE ON Txn
WHEN OLD.Status = 'Exported' AND NOT (
        NEW.Status = 'Ready'
    AND NEW.BatchId IS NULL
    AND EXISTS (SELECT 1 FROM ExportBatch b WHERE b.Id = OLD.BatchId AND b.VoidedUtc IS NOT NULL)
    AND OLD.SignedAmountCents IS NEW.SignedAmountCents
    AND OLD.OvrDate IS NEW.OvrDate AND OLD.OvrPayee IS NEW.OvrPayee
    AND OLD.OvrMemo IS NEW.OvrMemo AND OLD.OvrCheckNum IS NEW.OvrCheckNum
    AND OLD.CategoryId IS NEW.CategoryId AND OLD.QbNameId IS NEW.QbNameId AND OLD.Class IS NEW.Class
    AND OLD.DuplicateOfTxnId IS NEW.DuplicateOfTxnId AND OLD.RuleId IS NEW.RuleId)
BEGIN
    SELECT RAISE(ABORT, 'Exported transactions are locked; void the batch first');
END;

CREATE TRIGGER TR_Txn_ExportedNoDelete BEFORE DELETE ON Txn
WHEN OLD.Status = 'Exported'
BEGIN
    SELECT RAISE(ABORT, 'Exported transactions cannot be deleted');
END;

CREATE TRIGGER TR_TxnSplit_LockedInsert BEFORE INSERT ON TxnSplit
WHEN (SELECT Status FROM Txn WHERE Id = NEW.TxnId) = 'Exported'
BEGIN
    SELECT RAISE(ABORT, 'Exported transactions are locked; void the batch first');
END;

CREATE TRIGGER TR_TxnSplit_LockedUpdate BEFORE UPDATE ON TxnSplit
WHEN (SELECT Status FROM Txn WHERE Id = OLD.TxnId) = 'Exported'
BEGIN
    SELECT RAISE(ABORT, 'Exported transactions are locked; void the batch first');
END;

CREATE TRIGGER TR_TxnSplit_LockedDelete BEFORE DELETE ON TxnSplit
WHEN (SELECT Status FROM Txn WHERE Id = OLD.TxnId) = 'Exported'
BEGIN
    SELECT RAISE(ABORT, 'Exported transactions are locked; void the batch first');
END;

-- A void is permanent.
CREATE TRIGGER TR_ExportBatch_VoidPermanent BEFORE UPDATE ON ExportBatch
WHEN OLD.VoidedUtc IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'A voided batch cannot be changed');
END;

CREATE TRIGGER TR_ExportBatch_NoDelete BEFORE DELETE ON ExportBatch
BEGIN
    SELECT RAISE(ABORT, 'Export batches cannot be deleted');
END;

-- Audit log is append-only.
CREATE TRIGGER TR_AuditLog_NoUpdate BEFORE UPDATE ON AuditLog
BEGIN
    SELECT RAISE(ABORT, 'Audit log is append-only');
END;

CREATE TRIGGER TR_AuditLog_NoDelete BEFORE DELETE ON AuditLog
BEGIN
    SELECT RAISE(ABORT, 'Audit log is append-only');
END;
