-- v2: institution id/name on accounts (shown when mapping a new account to QuickBooks)
ALTER TABLE BankAccount ADD COLUMN FiId TEXT;
ALTER TABLE BankAccount ADD COLUMN FiOrg TEXT;
