-- v4: export batch lifecycle (Exported -> ImportedToQb -> Voided, or Exported -> Voided)
-- so IIF files can be filed by state under iif\imported\YYYY-MM and iif\voided\YYYY-MM.

DROP TRIGGER TR_ExportBatch_VoidPermanent;

ALTER TABLE ExportBatch ADD COLUMN Status TEXT NOT NULL DEFAULT 'Exported'
    CHECK (Status IN ('Exported', 'ImportedToQb', 'Voided'));
ALTER TABLE ExportBatch ADD COLUMN ImportedUtc TEXT;

UPDATE ExportBatch SET Status = 'Voided' WHERE VoidedUtc IS NOT NULL;

-- New batches start Exported.
CREATE TRIGGER TR_ExportBatch_InsertState BEFORE INSERT ON ExportBatch
WHEN NEW.Status IS NOT 'Exported' OR NEW.VoidedUtc IS NOT NULL OR NEW.ImportedUtc IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'A new export batch must start as Exported');
END;

-- Only forward moves: Exported -> ImportedToQb, Exported -> Voided, ImportedToQb -> Voided.
CREATE TRIGGER TR_ExportBatch_StatusFlow BEFORE UPDATE OF Status ON ExportBatch
WHEN NEW.Status IS NOT OLD.Status
 AND NOT ((OLD.Status = 'Exported' AND NEW.Status IN ('ImportedToQb', 'Voided'))
       OR (OLD.Status = 'ImportedToQb' AND NEW.Status = 'Voided'))
BEGIN
    SELECT RAISE(ABORT, 'Export batch status can only move forward');
END;

-- Status and timestamps agree; an import date, once set, stays.
CREATE TRIGGER TR_ExportBatch_StateConsistent BEFORE UPDATE ON ExportBatch
WHEN (NEW.Status = 'Voided') <> (NEW.VoidedUtc IS NOT NULL)
  OR (NEW.Status = 'ImportedToQb' AND NEW.ImportedUtc IS NULL)
  OR (OLD.ImportedUtc IS NOT NULL AND NEW.ImportedUtc IS NOT OLD.ImportedUtc)
  OR (OLD.VoidedUtc IS NOT NULL AND NEW.VoidedUtc IS NOT OLD.VoidedUtc)
BEGIN
    SELECT RAISE(ABORT, 'Export batch status and dates disagree');
END;

-- A void is permanent; only the file location of a voided batch may change.
CREATE TRIGGER TR_ExportBatch_VoidPermanent BEFORE UPDATE ON ExportBatch
WHEN OLD.Status = 'Voided'
 AND NOT (NEW.Status = 'Voided' AND NEW.VoidedUtc IS OLD.VoidedUtc AND NEW.ImportedUtc IS OLD.ImportedUtc
      AND NEW.CreatedUtc IS OLD.CreatedUtc AND NEW.BackupPath IS OLD.BackupPath
      AND NEW.TxnCount IS OLD.TxnCount AND NEW.TotalCents IS OLD.TotalCents)
BEGIN
    SELECT RAISE(ABORT, 'A voided batch cannot be changed');
END;

-- Content of any batch is fixed once written; the file path may change when the file is filed.
CREATE TRIGGER TR_ExportBatch_ContentFixed BEFORE UPDATE ON ExportBatch
WHEN NEW.CreatedUtc IS NOT OLD.CreatedUtc OR NEW.TxnCount IS NOT OLD.TxnCount OR NEW.TotalCents IS NOT OLD.TotalCents
BEGIN
    SELECT RAISE(ABORT, 'Export batch totals cannot be changed');
END;
