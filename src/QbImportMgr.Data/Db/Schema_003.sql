-- v3: QuickBooks register matching, payee learning, invoiced holds, transfer pairing, split templates.
-- Txn is rebuilt (SQLite cannot alter a CHECK constraint). The migrator runs this with foreign keys
-- off and verifies PRAGMA foreign_key_check before committing.

-- Triggers on other tables that reference Txn must go while Txn is swapped out.
DROP TRIGGER TR_TxnSplit_LockedInsert;
DROP TRIGGER TR_TxnSplit_LockedUpdate;
DROP TRIGGER TR_TxnSplit_LockedDelete;

CREATE TABLE QbRegisterTxn (
    Id            INTEGER PRIMARY KEY,
    BankAccountId INTEGER REFERENCES BankAccount(Id),
    QbAccountName TEXT,
    TxnDate       TEXT    NOT NULL,
    TxnType       TEXT    NOT NULL,
    Num           TEXT,
    Name          TEXT,
    Memo          TEXT,
    Clr           TEXT,
    SplitAccount  TEXT,
    AmountCents   INTEGER NOT NULL,
    SourceFile    TEXT    NOT NULL,
    ImportedUtc   TEXT    NOT NULL,
    RowKey        TEXT    NOT NULL UNIQUE
);
CREATE INDEX IX_QbRegisterTxn_Account ON QbRegisterTxn(BankAccountId, AmountCents, TxnDate);
CREATE INDEX IX_QbRegisterTxn_AccountName ON QbRegisterTxn(QbAccountName, TxnDate);
CREATE INDEX IX_QbRegisterTxn_Name ON QbRegisterTxn(Name);

CREATE TABLE SplitTemplate (
    Id         INTEGER PRIMARY KEY,
    Name       TEXT NOT NULL UNIQUE COLLATE NOCASE,
    CreatedUtc TEXT NOT NULL
);

CREATE TABLE SplitTemplateLine (
    Id         INTEGER PRIMARY KEY,
    TemplateId INTEGER NOT NULL REFERENCES SplitTemplate(Id) ON DELETE CASCADE,
    Seq        INTEGER NOT NULL,
    CategoryId INTEGER NOT NULL REFERENCES Category(Id),
    QbNameId   INTEGER REFERENCES QbName(Id),
    Class      TEXT,
    Kind       TEXT    NOT NULL CHECK (Kind IN ('Percent','Fixed')),
    Value      INTEGER NOT NULL,
    Memo       TEXT,
    UNIQUE (TemplateId, Seq)
);

ALTER TABLE QbName ADD COLUMN IsInvoiced INTEGER NOT NULL DEFAULT 0 CHECK (IsInvoiced IN (0,1));
ALTER TABLE Rule ADD COLUMN Source TEXT NOT NULL DEFAULT 'Manual';
ALTER TABLE Rule ADD COLUMN SplitTemplateId INTEGER REFERENCES SplitTemplate(Id) ON DELETE SET NULL;
ALTER TABLE PayeeAlias ADD COLUMN Source TEXT NOT NULL DEFAULT 'Manual';

-- ---------------------------------------------------------------- Txn rebuild

CREATE TABLE Txn_new (
    Id                INTEGER PRIMARY KEY,
    BankAccountId     INTEGER NOT NULL REFERENCES BankAccount(Id),
    QboStatementId    INTEGER NOT NULL REFERENCES QboStatement(Id),
    FitId             TEXT,
    TrnType           TEXT    NOT NULL,
    DtPosted          TEXT    NOT NULL,
    DtUser            TEXT,
    AmountCents       INTEGER NOT NULL,
    Name              TEXT,
    Memo              TEXT,
    CheckNum          TEXT,
    SignedAmountCents INTEGER NOT NULL CHECK (SignedAmountCents = AmountCents OR SignedAmountCents = -AmountCents),
    OvrDate           TEXT,
    OvrPayee          TEXT,
    OvrMemo           TEXT,
    OvrCheckNum       TEXT,
    CategoryId        INTEGER REFERENCES Category(Id),
    QbNameId          INTEGER REFERENCES QbName(Id),
    Class             TEXT,
    Status            TEXT    NOT NULL DEFAULT 'New'
                      CHECK (Status IN ('New','Categorized','Ready','Exported','Excluded','Duplicate',
                                        'InQb','Ambiguous','HoldReceivePayment','Paired')),
    DuplicateOfTxnId  INTEGER REFERENCES Txn(Id),
    RuleId            INTEGER REFERENCES Rule(Id) ON DELETE SET NULL,
    BatchId           INTEGER REFERENCES ExportBatch(Id),
    UpdatedUtc        TEXT    NOT NULL,
    QbRegisterTxnId   INTEGER REFERENCES QbRegisterTxn(Id),
    PairedTxnId       INTEGER REFERENCES Txn(Id),
    CHECK ((Status = 'Exported') = (BatchId IS NOT NULL)),
    CHECK (Status <> 'InQb' OR QbRegisterTxnId IS NOT NULL),
    CHECK (Status <> 'Paired' OR PairedTxnId IS NOT NULL)
);

INSERT INTO Txn_new (Id, BankAccountId, QboStatementId, FitId, TrnType, DtPosted, DtUser, AmountCents, Name, Memo, CheckNum,
                     SignedAmountCents, OvrDate, OvrPayee, OvrMemo, OvrCheckNum, CategoryId, QbNameId, Class, Status,
                     DuplicateOfTxnId, RuleId, BatchId, UpdatedUtc)
SELECT Id, BankAccountId, QboStatementId, FitId, TrnType, DtPosted, DtUser, AmountCents, Name, Memo, CheckNum,
       SignedAmountCents, OvrDate, OvrPayee, OvrMemo, OvrCheckNum, CategoryId, QbNameId, Class, Status,
       DuplicateOfTxnId, RuleId, BatchId, UpdatedUtc
FROM Txn;

DROP TABLE Txn;
ALTER TABLE Txn_new RENAME TO Txn;

CREATE UNIQUE INDEX UX_Txn_FitId ON Txn(BankAccountId, FitId) WHERE FitId IS NOT NULL AND FitId <> '';
CREATE UNIQUE INDEX UX_Txn_QbRegister ON Txn(QbRegisterTxnId) WHERE QbRegisterTxnId IS NOT NULL;
CREATE INDEX IX_Txn_Account_Date ON Txn(BankAccountId, DtPosted);
CREATE INDEX IX_Txn_Statement ON Txn(QboStatementId);
CREATE INDEX IX_Txn_Batch ON Txn(BatchId);
CREATE INDEX IX_Txn_Status ON Txn(Status);
CREATE INDEX IX_Txn_Paired ON Txn(PairedTxnId);

CREATE TABLE QbMatchRejection (
    TxnId           INTEGER NOT NULL REFERENCES Txn(Id) ON DELETE CASCADE,
    QbRegisterTxnId INTEGER NOT NULL REFERENCES QbRegisterTxn(Id) ON DELETE CASCADE,
    CreatedUtc      TEXT    NOT NULL,
    PRIMARY KEY (TxnId, QbRegisterTxnId)
);

CREATE TABLE TransferRejection (
    OutTxnId   INTEGER NOT NULL REFERENCES Txn(Id) ON DELETE CASCADE,
    InTxnId    INTEGER NOT NULL REFERENCES Txn(Id) ON DELETE CASCADE,
    CreatedUtc TEXT    NOT NULL,
    PRIMARY KEY (OutTxnId, InTxnId)
);

CREATE INDEX IX_AuditLog_Utc ON AuditLog(Utc);

-- ---------------------------------------------------------------- triggers (recreated)

CREATE TRIGGER TR_Txn_RawImmutable BEFORE UPDATE ON Txn
WHEN OLD.FitId IS NOT NEW.FitId OR OLD.TrnType IS NOT NEW.TrnType OR OLD.DtPosted IS NOT NEW.DtPosted
  OR OLD.DtUser IS NOT NEW.DtUser OR OLD.AmountCents IS NOT NEW.AmountCents OR OLD.Name IS NOT NEW.Name
  OR OLD.Memo IS NOT NEW.Memo OR OLD.CheckNum IS NOT NEW.CheckNum
  OR OLD.BankAccountId IS NOT NEW.BankAccountId OR OLD.QboStatementId IS NOT NEW.QboStatementId
BEGIN
    SELECT RAISE(ABORT, 'Raw bank fields are immutable');
END;

-- Exported rows are locked. Two changes are permitted, each with every business column unchanged:
--  * the void: Exported -> Ready, BatchId -> NULL, batch already voided, links unchanged;
--  * linking: still Exported in the same batch, only QbRegisterTxnId / PairedTxnId change
--    (so an exported row can claim its QB register row and its transfer partner).
CREATE TRIGGER TR_Txn_ExportedLocked BEFORE UPDATE ON Txn
WHEN OLD.Status = 'Exported' AND NOT (
        OLD.SignedAmountCents IS NEW.SignedAmountCents
    AND OLD.OvrDate IS NEW.OvrDate AND OLD.OvrPayee IS NEW.OvrPayee
    AND OLD.OvrMemo IS NEW.OvrMemo AND OLD.OvrCheckNum IS NEW.OvrCheckNum
    AND OLD.CategoryId IS NEW.CategoryId AND OLD.QbNameId IS NEW.QbNameId AND OLD.Class IS NEW.Class
    AND OLD.DuplicateOfTxnId IS NEW.DuplicateOfTxnId AND OLD.RuleId IS NEW.RuleId
    AND (
        (    NEW.Status = 'Ready' AND NEW.BatchId IS NULL
         AND EXISTS (SELECT 1 FROM ExportBatch b WHERE b.Id = OLD.BatchId AND b.VoidedUtc IS NOT NULL)
         AND OLD.QbRegisterTxnId IS NEW.QbRegisterTxnId AND OLD.PairedTxnId IS NEW.PairedTxnId)
     OR (NEW.Status = 'Exported' AND NEW.BatchId IS OLD.BatchId)
    ))
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

-- A linked QB register row cannot be deleted out from under its bank transaction.
CREATE TRIGGER TR_QbRegisterTxn_LinkedNoDelete BEFORE DELETE ON QbRegisterTxn
WHEN EXISTS (SELECT 1 FROM Txn WHERE QbRegisterTxnId = OLD.Id)
BEGIN
    SELECT RAISE(ABORT, 'QB register row is matched to a bank transaction');
END;
