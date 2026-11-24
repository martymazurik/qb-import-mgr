using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Iif;
using QbImportMgr.Core.Qb;
using QbImportMgr.Data.Db;
using QbImportMgr.Data.Services;

namespace QbImportMgr.Tests.Data;

public abstract class Phase2TestBase : DataTestBase
{
    protected Phase2TestBase()
    {
        Qb = new QbService(Db, Clock);
        Templates = new TemplateService(Db, Clock);
        Audit = new AuditService(Db);
    }

    protected QbService Qb { get; }
    protected TemplateService Templates { get; }
    protected AuditService Audit { get; }

    protected const string ListsIif =
        "!ACCNT\tNAME\tACCNTTYPE\r\n" +
        "ACCNT\tChecking\tBANK\r\nACCNT\tSavings\tBANK\r\nACCNT\tAmerican Express\tCCARD\r\n" +
        "ACCNT\tRent\tEXP\r\nACCNT\tSales Income\tINC\r\nACCNT\tUtilities:Telephone\tEXP\r\nACCNT\tAuto:Fuel\tEXP\r\n" +
        "ACCNT\tTravel\tEXP\r\nACCNT\tMeals\tEXP\r\nACCNT\tBank Service Charges\tEXP\r\nACCNT\tOffice Supplies\tEXP\r\n" +
        "!CUST\tNAME\r\nCUST\tAcme Corp\r\n" +
        "!VEND\tNAME\r\nVEND\tLandlord LLC\r\nVEND\tAT&T\r\nVEND\tShell\r\nVEND\tAmazon\r\n";

    protected void ImportLists() => Lists.Import(Encoding.ASCII.GetBytes(ListsIif));

    /// <summary>Maps every bank account by its masked number to a QB account.</summary>
    protected void Map(params (string Masked, string QbAccount)[] map)
    {
        foreach (var a in Accounts.GetAll())
            if (map.FirstOrDefault(m => m.Masked == a.OfxAcctIdMasked) is { QbAccount: { } qb })
                Accounts.Update(a.Id, a.DisplayName, qb, a.InvertSign);
    }

    /// <summary>A Transaction Detail CSV in the Debit/Credit layout. Amount: signed for the account (in positive).</summary>
    protected static byte[] RegisterCsv(params (string Account, string Date, string Type, string? Num, string? Name, string? Memo, string? Split, decimal Amount)[] rows)
    {
        var sb = new StringBuilder("Transaction Detail\r\n,Type,Date,Num,Name,Memo,Account,Clr,Split,Debit,Credit\r\n");
        foreach (var r in rows)
        {
            var amt = Math.Abs(r.Amount).ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
            sb.Append($",{r.Type},{r.Date},{r.Num},\"{r.Name}\",\"{r.Memo}\",{r.Account},,\"{r.Split}\",{(r.Amount > 0 ? "\"" + amt + "\"" : "")},{(r.Amount < 0 ? "\"" + amt + "\"" : "")}\r\n");
        }
        sb.Append("TOTAL,,,,,,,,,,\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    protected Txn ByName(string name) => AllTxns().Single(t => t.Name == name);

    /// <summary>Bank sample (Checking ****4321) imported and mapped, lists loaded.</summary>
    protected void BankSetup()
    {
        ImportLists();
        ImportSample("bank_sgml_1x.qbo");
        Map(("****4321", "Checking"));
    }

    protected static readonly (string, string, string, string?, string?, string?, string?, decimal)[] CheckingRegister =
    [
        ("Checking", "10/05/2026", "Check", "1042", "Landlord LLC", "October rent", "Rent", -1250m),
        ("Checking", "10/15/2026", "Deposit", null, "Acme Corp", "DIRECT DEP", "Sales Income", 2500m),
        ("Checking", "10/20/2026", "General Journal", null, null, "Fee", "-SPLIT-", -5m),
        ("Checking", "10/02/2026", "Check", "DEBIT", "AT&T", null, "Utilities:Telephone", -54.23m),
    ];
}

public class Schema3UpgradeTests : DataTestBase
{
    [Fact]
    public void V2_database_with_exported_rows_and_splits_upgrades_intact()
    {
        var path = Path.Combine(Paths.Root, "v2.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False;Foreign Keys=True"))
        {
            c.Open();
            foreach (var script in new[] { "Schema_001", "Schema_002" })
            {
                using var stream = typeof(Database).Assembly.GetManifestResourceStream($"QbImportMgr.Data.Db.{script}.sql")!;
                c.Execute(new StreamReader(stream).ReadToEnd());
            }
            c.Execute("PRAGMA user_version = 2;");
            c.Execute("""
                INSERT INTO BankAccount (Id, OfxAcctIdMasked, AcctIdHash, Kind, DisplayName, QbAccountName, CreatedUtc) VALUES (1, '****1', 'h', 'BANK', 'Chk', 'Checking', '2026-01-01T00:00:00.000Z');
                INSERT INTO QboFile (Id, FileName, Sha256, RawBytes, ImportedUtc) VALUES (1, 'a.qbo', 's', x'00', '2026-01-01T00:00:00.000Z');
                INSERT INTO QboStatement (Id, QboFileId, BankAccountId) VALUES (1, 1, 1);
                INSERT INTO Category (Id, FullName, ImportedUtc) VALUES (1, 'Rent', '2026-01-01T00:00:00.000Z'), (2, 'Travel', '2026-01-01T00:00:00.000Z');
                INSERT INTO ExportBatch (Id, CreatedUtc, IifPath, TxnCount, TotalCents) VALUES (1, '2026-01-01T00:00:00.000Z', 'x.iif', 1, -100);
                INSERT INTO Txn (Id, BankAccountId, QboStatementId, FitId, TrnType, DtPosted, AmountCents, Name, SignedAmountCents, CategoryId, Status, UpdatedUtc)
                    VALUES (1, 1, 1, 'F1', 'DEBIT', '2026-01-02', -1000, 'SPLIT ME', -1000, NULL, 'Categorized', '2026-01-01T00:00:00.000Z');
                INSERT INTO TxnSplit (TxnId, Seq, CategoryId, AmountCents) VALUES (1, 1, 1, -600), (1, 2, 2, -400);
                INSERT INTO Txn (Id, BankAccountId, QboStatementId, FitId, TrnType, DtPosted, AmountCents, Name, SignedAmountCents, CategoryId, Status, UpdatedUtc)
                    VALUES (2, 1, 1, 'F2', 'DEBIT', '2026-01-03', -100, 'EXPORTED', -100, 1, 'Ready', '2026-01-01T00:00:00.000Z');
                UPDATE Txn SET Status = 'Exported', BatchId = 1 WHERE Id = 2;
                INSERT INTO Txn (Id, BankAccountId, QboStatementId, FitId, TrnType, DtPosted, AmountCents, Name, SignedAmountCents, Status, DuplicateOfTxnId, UpdatedUtc)
                    VALUES (3, 1, 1, 'F3', 'DEBIT', '2026-01-03', -100, 'EXPORTED', -100, 'Duplicate', 2, '2026-01-01T00:00:00.000Z');
                INSERT INTO AuditLog (Utc, Entity, EntityId, Action) VALUES ('2026-01-01T00:00:00.000Z', 'Txn', 1, 'x');
                """);
        }

        var db = new Database(path);
        Assert.True(db.NeedsUpgrade());
        Assert.Equal(Migrator.LatestVersion, db.Migrate());
        Assert.False(db.NeedsUpgrade());
        Assert.False(new Database(Path.Combine(Paths.Root, "missing.db")).NeedsUpgrade());

        using var s = db.Open();
        var con = s.Connection;
        Assert.Equal(3, con.ExecuteScalar<int>("SELECT COUNT(*) FROM Txn"));
        Assert.Equal(2, con.ExecuteScalar<int>("SELECT COUNT(*) FROM TxnSplit WHERE TxnId = 1")); // not cascade-deleted by the rebuild
        Assert.Equal(1, con.ExecuteScalar<int>("PRAGMA foreign_keys;"));
        Assert.Empty(con.Query("PRAGMA foreign_key_check;"));
        Assert.Equal("ok", con.ExecuteScalar<string>("PRAGMA integrity_check;"));

        var exported = s.Txns.Get(2)!;
        Assert.Equal((TxnStatus.Exported, (long?)1, (long?)null, (long?)null), (exported.Status, exported.BatchId, exported.QbRegisterTxnId, exported.PairedTxnId));
        Assert.Equal(2, s.Txns.Get(3)!.DuplicateOfTxnId);
        Assert.Equal(RuleSources.Manual, con.ExecuteScalar<string>("SELECT dflt_value FROM pragma_table_info('Rule') WHERE name = 'Source'")?.Trim('\''));

        // Triggers came back with the rebuilt table.
        Assert.Throws<SqliteException>(() => con.Execute("UPDATE Txn SET Name = 'x' WHERE Id = 1"));
        Assert.Throws<SqliteException>(() => con.Execute("UPDATE Txn SET CategoryId = 2 WHERE Id = 2"));
        Assert.Throws<SqliteException>(() => con.Execute("DELETE FROM Txn WHERE Id = 2"));
        Assert.Throws<SqliteException>(() => con.Execute("INSERT INTO TxnSplit (TxnId, Seq, CategoryId, AmountCents) VALUES (2, 1, 1, -100)"));
        Assert.Throws<SqliteException>(() => con.Execute("UPDATE Txn SET Status = 'InQb' WHERE Id = 1")); // InQb needs a register link
        Assert.Throws<SqliteException>(() => con.Execute("UPDATE Txn SET Status = 'Bogus' WHERE Id = 1"));

        // New statuses are accepted, and an exported row may take a link without unlocking anything else.
        con.Execute("INSERT INTO QbRegisterTxn (Id, BankAccountId, TxnDate, TxnType, AmountCents, SourceFile, ImportedUtc, RowKey) VALUES (9, 1, '2026-01-03', 'Check', -100, 'r.csv', 'x', 'k')");
        con.Execute("UPDATE Txn SET QbRegisterTxnId = 9 WHERE Id = 2");
        Assert.Equal(9, s.Txns.Get(2)!.QbRegisterTxnId);
        Assert.Throws<SqliteException>(() => con.Execute("DELETE FROM QbRegisterTxn WHERE Id = 9")); // linked
    }
}

public class QbRegisterImportTests : Phase2TestBase
{
    [Fact]
    public void Register_import_matches_by_check_number_and_date_and_sets_InQb()
    {
        BankSetup();
        var r = Qb.ImportRegister(RegisterCsv(CheckingRegister), "reg.csv");

        Assert.Equal((4, 4, 0, 0), (r.Rows, r.Added, r.Unchanged, r.Removed));
        Assert.Equal(3, r.Reconcile.Matched);
        Assert.Empty(r.UnmappedAccounts);

        Assert.Equal(TxnStatus.InQb, ByName("CHECK 1042").Status);
        Assert.Equal(TxnStatus.InQb, ByName("PAYROLL ACME CORP").Status);
        Assert.Equal(TxnStatus.InQb, ByName("AT&T *PAYMENT").Status);
        Assert.Equal(TxnStatus.New, ByName("SERVICE FEE").Status); // QB fee is 8 days away
        var q = Qb.RegisterRow(ByName("CHECK 1042").QbRegisterTxnId!.Value)!;
        Assert.Equal(("Check", "1042", "Landlord LLC", "Rent"), (q.TxnType, q.Num, q.Name, q.SplitAccount));
    }

    [Fact]
    public void Re_importing_an_overlapping_range_is_idempotent()
    {
        BankSetup();
        Qb.ImportRegister(RegisterCsv(CheckingRegister), "reg.csv");
        var before = AllTxns().Select(t => (t.Id, t.Status, t.QbRegisterTxnId)).ToList();

        var again = Qb.ImportRegister(RegisterCsv(CheckingRegister), "reg-again.csv");
        Assert.Equal((0, 4, 0, 0), (again.Added, again.Unchanged, again.Removed, again.Reconcile.Matched));
        Assert.Equal(before, AllTxns().Select(t => (t.Id, t.Status, t.QbRegisterTxnId)));
        Assert.Equal(4, Qb.RegisterRowCount());
    }

    [Fact]
    public void Identical_qb_rows_are_kept_apart_and_edited_rows_replace_old_ones()
    {
        BankSetup();
        var parking = ("Checking", "10/20/2026", "Check", (string?)null, (string?)"City", (string?)"Parking", (string?)"Travel", -5m);
        Qb.ImportRegister(RegisterCsv([.. CheckingRegister, parking, parking]), "a.csv");
        Assert.Equal(6, Qb.RegisterRowCount());

        // QB memo on the unmatched journal entry was edited: the old row goes, the new one comes in.
        var edited = CheckingRegister.Select(x => x.Item3 == "General Journal" ? x with { Item6 = "Fee (edited)" } : x).ToArray();
        var r = Qb.ImportRegister(RegisterCsv([.. edited, parking, parking]), "b.csv");
        Assert.Equal((1, 5, 1), (r.Added, r.Unchanged, r.Removed));
        Assert.Equal(6, Qb.RegisterRowCount());
    }

    private static readonly (string, string, string, string?, string?, string?, string?, decimal)[] AmexRegister =
    [
        ("American Express", "10/12/2026", "Credit Card Charge", null, "Amazon", "Office supplies", "Office Supplies", -84.17m),
        ("American Express", "10/16/2026", "Credit Card Charge", null, "Shell", null, "Auto:Fuel", -45.00m),
        ("American Express", "10/22/2026", "Credit Card Charge", null, "Delta Air Lines", null, "Travel", -300.00m),
    ];

    private static readonly (string, string, string, string?, string?, string?, string?, decimal) Late =
        ("Checking", "10/25/2026", "Check", null, "Someone", null, "Rent", -1m);

    private int RegisterRows(string qbAccount)
    {
        using var s = Db.Open();
        return s.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM QbRegisterTxn WHERE QbAccountName = @qbAccount", new { qbAccount });
    }

    [Fact]
    public void Re_import_removal_is_scoped_to_accounts_in_the_new_file()
    {
        BankSetup();
        // All-accounts history: Checking and American Express over the same October range.
        Qb.ImportRegister(RegisterCsv([.. CheckingRegister, .. AmexRegister]), "history.csv");
        Assert.Equal((4, 3), (RegisterRows("Checking"), RegisterRows("American Express")));

        // Checking-only file for an overlapping range, without the (unmatched) journal entry.
        // Ends 10/25, so the 10/20 journal entry is inside this file's Checking range.
        var checkingOnly = CheckingRegister.Where(x => x.Item3 != "General Journal").Append(Late).ToArray();
        var r = Qb.ImportRegister(RegisterCsv(checkingOnly), "checking-only.csv");

        Assert.Equal(1, r.Removed);
        Assert.Equal(["Checking"], r.Accounts.Select(a => a.QbAccount));
        Assert.Equal((4, 3), (RegisterRows("Checking"), RegisterRows("American Express")));
    }

    [Fact]
    public void Re_import_without_account_column_only_touches_the_assigned_account()
    {
        BankSetup();
        Qb.ImportRegister(RegisterCsv([.. CheckingRegister, .. AmexRegister]), "history.csv");
        var checking = Accounts.GetAll().Single(a => a.QbAccountName == "Checking");

        // Amount layout, no Account column, assigned to Checking; same dates as the Amex rows.
        var csv = Encoding.UTF8.GetBytes(
            ",Type,Date,Num,Name,Memo,Split,Amount\r\n" +
            ",Check,10/05/2026,1042,Landlord LLC,October rent,Rent,\"-1,250.00\"\r\n" +
            ",Check,10/22/2026,,Someone,,Rent,-1.00\r\n");
        var r = Qb.ImportRegister(csv, "checking-noacct.csv", checking.Id);

        Assert.Equal(3, RegisterRows("American Express"));
        Assert.Equal(["Checking"], r.Accounts.Select(a => a.QbAccount));
        // Checking rows in 10/05–10/22 missing from this file and unmatched: only the journal entry (10/20).
        Assert.Equal(1, r.Removed);
    }

    [Fact]
    public void Account_name_case_differences_still_scope_removal_to_that_account()
    {
        BankSetup();
        Qb.ImportRegister(RegisterCsv([.. CheckingRegister, .. AmexRegister]), "history.csv");
        var upper = CheckingRegister.Where(x => x.Item3 != "General Journal").Append(Late).Select(x => x with { Item1 = "CHECKING" }).ToArray();

        var r = Qb.ImportRegister(RegisterCsv(upper), "upper.csv");
        Assert.Equal((1, 3, 1), (r.Added, r.Unchanged, r.Removed));
        Assert.Equal(3, RegisterRows("American Express"));
    }

    [Fact]
    public void Report_without_account_column_needs_an_account()
    {
        ImportLists();
        ImportSample("amex_ccstmt.qfx");
        Map(("****1005", "American Express"));
        var csv = TestData.Sample("qb_register_amount.csv");

        Assert.False(Qb.Inspect(csv).HasAccountColumn);
        Assert.Throws<InvalidOperationException>(() => Qb.ImportRegister(csv, "amex.csv"));

        var amex = Accounts.GetAll().Single();
        var r = Qb.ImportRegister(csv, "amex.csv", amex.Id);
        Assert.Equal(5, r.Rows);
        Assert.Equal(TxnStatus.InQb, ByName("AMAZON MKTPL*AB12C3").Status);   // -84.17, 10/12
        Assert.Equal(TxnStatus.InQb, ByName("DELTA AIR LINES").Status);
        Assert.Equal(TxnStatus.InQb, ByName("AMAZON MKTPL*RETURN").Status);  // +19.99 credit
        // Two identical bank parking charges, one QB entry: exactly one is InQb.
        Assert.Equal(1, AllTxns().Count(t => t.Name == "PARKING METER 4471" && t.Status == TxnStatus.InQb));
    }

    [Fact]
    public void Unmapped_qb_accounts_are_kept_for_learning_and_bound_once_mapped()
    {
        ImportLists();
        ImportSample("bank_sgml_1x.qbo"); // not mapped yet
        var r = Qb.ImportRegister(RegisterCsv(CheckingRegister), "reg.csv");
        Assert.Equal(["Checking"], r.UnmappedAccounts);
        Assert.Equal(0, r.Reconcile.Matched);

        Map(("****4321", "Checking"));
        var again = Qb.ImportRegister(RegisterCsv(CheckingRegister), "reg.csv");
        Assert.Equal(3, again.Reconcile.Matched);
    }

    [Fact]
    public void InQb_rows_are_never_exported_or_made_ready_but_count_in_the_ledger()
    {
        BankSetup();
        Qb.ImportRegister(RegisterCsv(CheckingRegister), "reg.csv");
        var inQb = ByName("CHECK 1042");

        Assert.Throws<TxnStateException>(() => Txns.MarkReady([inQb.Id]));
        Assert.Contains(Export.Preview([inQb.Id]).Validation.Errors, e => e.Message.Contains("InQb"));
        Assert.DoesNotContain(inQb.Id, Export.PreviewReady().Items.Select(i => i.TxnId));
        using var s = Db.Open();
        Assert.DoesNotContain(inQb.Id, s.Txns.ListByStatus(TxnStatus.New).Select(t => t.Id));
        Assert.Contains(inQb.Id, s.Txns.ListPostedBetween(inQb.BankAccountId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)).Select(t => t.Id));
    }

    [Fact]
    public void Not_in_qb_unlinks_and_is_remembered()
    {
        BankSetup();
        Qb.ImportRegister(RegisterCsv(CheckingRegister), "reg.csv");
        var t = Txns.NotInQb(ByName("CHECK 1042").Id);
        Assert.Equal((TxnStatus.New, (long?)null), (t.Status, t.QbRegisterTxnId));

        Qb.ImportRegister(RegisterCsv(CheckingRegister), "reg.csv");
        Assert.Equal(TxnStatus.New, ByName("CHECK 1042").Status);
        Assert.Throws<TxnStateException>(() => Txns.NotInQb(ByName("SERVICE FEE").Id));
    }

    [Fact]
    public void Ambiguous_match_lists_candidates_and_pick_match_links()
    {
        BankSetup();
        var r = Qb.ImportRegister(RegisterCsv(
            ("Checking", "10/26/2026", "Check", null, "Bank", "a", "Bank Service Charges", -5m),
            ("Checking", "10/30/2026", "Check", null, "Bank", "b", "Bank Service Charges", -5m)), "reg.csv");
        Assert.Equal(1, r.Reconcile.Ambiguous);

        var fee = ByName("SERVICE FEE");
        Assert.Equal(TxnStatus.Ambiguous, fee.Status);
        var options = Txns.MatchOptions(fee.Id);
        Assert.Equal(2, options.Count);

        var picked = Txns.PickMatch(fee.Id, options[1].Row.Id);
        Assert.Equal((TxnStatus.InQb, (long?)options[1].Row.Id), (picked.Status, picked.QbRegisterTxnId));
        Assert.Throws<TxnStateException>(() => Txns.PickMatch(ByName("CAFÉ ROMA").Id, options[0].Row.Id)); // wrong amount
    }

    [Fact]
    public void Not_in_qb_on_an_ambiguous_row_rejects_all_candidates()
    {
        BankSetup();
        var csv = RegisterCsv(
            ("Checking", "10/26/2026", "Check", null, "Bank", "a", "Bank Service Charges", -5m),
            ("Checking", "10/30/2026", "Check", null, "Bank", "b", "Bank Service Charges", -5m));
        Qb.ImportRegister(csv, "reg.csv");
        Txns.NotInQb(ByName("SERVICE FEE").Id);
        Qb.ImportRegister(csv, "reg.csv");
        Assert.Equal(TxnStatus.New, ByName("SERVICE FEE").Status);
    }

    [Fact]
    public void Exported_rows_claim_their_qb_row_so_a_later_lookalike_does_not()
    {
        BankSetup();
        var fee = ByName("SERVICE FEE");
        Txns.SetCategory(fee.Id, CategoryId("Bank Service Charges"));
        Txns.MarkReady([fee.Id]);
        Export.Export([fee.Id]);

        // QB now has that fee; a second identical fee arrives in a later download.
        var later = ImportText("nov.qbo", BankOfx("000987654321", "1.00", "20261102", Trn("20261101", "-5.00", "NOVFEE", "SERVICE FEE")));
        Qb.ImportRegister(RegisterCsv(("Checking", "10/28/2026", "Check", null, "Bank", null, "Bank Service Charges", -5m)), "reg.csv");

        Assert.Equal(TxnStatus.Exported, Get(fee.Id).Status);
        Assert.NotNull(Get(fee.Id).QbRegisterTxnId);
        Assert.Equal(TxnStatus.New, AllTxns().Single(t => t.FitId == "NOVFEE").Status); // still needs export
        Assert.Equal(1, later.Statements[0].Inserted);
    }

    [Fact]
    public void Bank_import_after_register_import_matches_too()
    {
        ImportLists();
        ImportText("first.qbo", BankOfx("000987654321", "1.00", "20261001", Trn("20260930", "-1.00", "OLD", "OLD")));
        Map(("****4321", "Checking"));
        Qb.ImportRegister(RegisterCsv(CheckingRegister), "reg.csv");

        var r = ImportSample("bank_sgml_1x.qbo");
        Assert.Equal(3, r.Reconcile!.Matched);
    }
}

public class LearningTests : Phase2TestBase
{
    private void HistorySetup()
    {
        BankSetup();
        // A long history for learning: Shell 9x to fuel, 1x meals, plus a -SPLIT- row; plus checking rows that match bank rows.
        var history = new List<(string, string, string, string?, string?, string?, string?, decimal)>(CheckingRegister);
        for (int i = 1; i <= 9; i++) history.Add(("Checking", $"0{i}/10/2025", "Check", null, "Shell", null, "Auto:Fuel", -40m - i));
        history.Add(("Checking", "10/10/2025", "Check", null, "Shell", null, "Meals", -12m));
        history.Add(("Checking", "11/10/2025", "Check", null, "Shell", null, "-SPLIT-", -60m));
        history.Add(("Old Account", "11/11/2025", "Check", null, "Amazon", null, "Office Supplies", -10m));
        history.Add(("Old Account", "11/12/2025", "Check", null, "Amazon", null, "Office Supplies", -11m));
        history.Add(("Old Account", "11/13/2025", "Check", null, "Amazon", null, "Office Supplies", -12m));
        Qb.ImportRegister(RegisterCsv([.. history]), "history.csv");
    }

    [Fact]
    public void Preview_builds_profiles_and_proposals_from_pairs_and_names()
    {
        HistorySetup();
        var p = Qb.Preview();

        var shell = p.Profiles.Single(x => x.Name == "Shell");
        Assert.Equal(("Auto:Fuel", 9, 10, 1), (shell.TopAccount, shell.TopCount, shell.SingleAccountRows, shell.SplitRows));
        Assert.Equal(3, p.MatchedPairs);

        // Matched pair: bank "CHECK 1042" ↔ Landlord LLC / Rent.
        Assert.Contains(p.Proposals, v => v.Proposal.Basis == ProposalBasis.MatchedBankText && v.Proposal.QbName == "Landlord LLC" && v.Proposal.Account == "Rent");
        // Name-based: Shell (90%, 10 rows) and Amazon (3 rows from an unmapped account, still used for learning).
        var shellProp = p.Proposals.Single(v => v.Proposal.QbName == "Shell");
        Assert.True(shellProp.Proposal.MeetsThreshold());
        Assert.Equal(ProposalState.New, shellProp.State);
        Assert.True(p.Proposals.Single(v => v.Proposal.QbName == "Amazon").Proposal.MeetsThreshold());
    }

    [Fact]
    public void Accepting_creates_learned_rule_and_alias_and_recategorizes_new_rows()
    {
        HistorySetup();
        ImportText("fuel.qbo", BankOfx("000987654321", "1.00", "20261110", Trn("20261105", "-33.00", "FUEL1", "POS PURCHASE SHELL OIL 57444")));
        var shell = Qb.Preview().Proposals.Single(v => v.Proposal.QbName == "Shell");

        var r = Qb.Accept([(shell.Proposal, "SHELL")]);
        Assert.Equal((1, 0, 1, 0), (r.RulesCreated, r.RulesUpdated, r.AliasesCreated, r.AliasesUpdated));
        Assert.Equal(1, r.RowsRecategorized);

        var t = AllTxns().Single(x => x.FitId == "FUEL1");
        Assert.Equal((TxnStatus.Categorized, CategoryId("Auto:Fuel"), "Shell"), (t.Status, t.CategoryId!.Value, t.EffectivePayee));
        using var s = Db.Open();
        var rule = s.Rules.GetAll().Single();
        Assert.Equal((RuleSources.LearnedFromQb, 200, "SHELL"), (rule.Source, rule.Priority, rule.Pattern));
        Assert.Equal(RuleSources.LearnedFromQb, s.Aliases.GetAll().Single().Source);

        // Accepting again is "already learned".
        Assert.Equal(ProposalState.Covered, Qb.Preview().Proposals.Single(v => v.Proposal.QbName == "Shell" && v.Pattern == "SHELL").State);
    }

    [Fact]
    public void Hand_made_rules_are_never_overwritten_and_show_as_conflicts()
    {
        HistorySetup();
        Txns.SaveRuleAndApply(new Rule { Pattern = "SHELL", CategoryId = CategoryId("Travel") });

        var shell = Qb.Preview().Proposals.Single(v => v.Proposal.QbName == "Shell");
        Assert.Equal(ProposalState.Conflict, shell.State);
        Assert.Contains("hand-made rule", shell.Note);

        var r = Qb.Accept([(shell.Proposal, shell.Pattern)]);
        Assert.Equal(0, r.RulesCreated);
        Assert.Single(r.Skipped);
        using var s = Db.Open();
        Assert.Equal(CategoryId("Travel"), s.Rules.GetAll().Single().CategoryId);
    }

    [Fact]
    public void Agreeing_hand_made_rule_shows_as_covered_and_missing_account_is_flagged()
    {
        HistorySetup();
        Txns.SaveRuleAndApply(new Rule { Pattern = "SHELL", CategoryId = CategoryId("Auto:Fuel") });
        Assert.Equal(ProposalState.Covered, Qb.Preview().Proposals.Single(v => v.Proposal.QbName == "Shell").State);

        var missing = new LearnProposal("ACME", "Acme Corp", "No Such Account", 5, 1, [], ProposalBasis.QbName);
        Assert.Contains("not in the imported lists", Qb.Accept([(missing, "ACME")]).Skipped.Single());
    }
}

public class HoldAndTransferTests : Phase2TestBase
{
    [Fact]
    public void Invoiced_customer_deposit_is_held_until_qb_has_it()
    {
        BankSetup();
        long acme;
        using (var s = Db.Open()) acme = s.Names.FindByName("Acme Corp")!.Id;
        var payroll = ByName("PAYROLL ACME CORP");
        Txns.SetCategory(payroll.Id, CategoryId("Sales Income"));
        Txns.SetName(payroll.Id, acme);
        Assert.Equal(TxnStatus.Categorized, Get(payroll.Id).Status);

        var rec = Txns.SetInvoiced(acme, true);
        Assert.Equal(1, rec.Held);
        Assert.Equal(TxnStatus.HoldReceivePayment, Get(payroll.Id).Status);
        Assert.Throws<TxnStateException>(() => Txns.MarkReady([payroll.Id]));
        Assert.Contains(Export.Preview([payroll.Id]).Validation.Errors, e => e.Message.Contains("HoldReceivePayment"));

        // Editing doesn't release it while the customer is invoiced; changing the name does.
        Assert.Equal(TxnStatus.HoldReceivePayment, Txns.SetClass(payroll.Id, "Admin").Status);
        Assert.Equal(TxnStatus.Categorized, Txns.SetName(payroll.Id, null).Status);
        Assert.Equal(TxnStatus.HoldReceivePayment, Txns.SetName(payroll.Id, acme).Status);

        // Received in QB, register re-imported: the deposit is InQb.
        Qb.ImportRegister(RegisterCsv(CheckingRegister), "reg.csv");
        Assert.Equal(TxnStatus.InQb, Get(payroll.Id).Status);
    }

    [Fact]
    public void Clearing_invoiced_releases_holds_and_money_out_is_never_held()
    {
        BankSetup();
        long acme;
        using (var s = Db.Open()) acme = s.Names.FindByName("Acme Corp")!.Id;
        Txns.SetInvoiced(acme, true);
        var att = ByName("AT&T *PAYMENT");
        Assert.Equal(TxnStatus.New, Txns.SetName(att.Id, acme).Status); // payment out
        var payroll = ByName("PAYROLL ACME CORP");
        Assert.Equal(TxnStatus.HoldReceivePayment, Txns.SetName(payroll.Id, acme).Status);
        Assert.Equal(1, Txns.SetInvoiced(acme, false).Released);
        Assert.Equal(TxnStatus.New, Get(payroll.Id).Status);
    }

    private (Txn Out, Txn In) TransferSetup()
    {
        ImportLists();
        ImportText("chk.qbo", BankOfx("111", "500.00", "20261031", Trn("20261010", "-500.00", "C1", "TRANSFER TO SAV")));
        ImportText("sav.qbo", BankOfx("222", "500.00", "20261031", Trn("20261011", "500.00", "S1", "TRANSFER FROM CHK", "CREDIT")));
        Map(("111", "Checking"), ("222", "Savings"));
        var r = Qb.ImportRegister(RegisterCsv(("Checking", "01/01/2020", "Check", null, "x", null, "Rent", -1m)), "nothing.csv");
        return (AllTxns().Single(t => t.FitId == "C1"), AllTxns().Single(t => t.FitId == "S1"));
    }

    [Fact]
    public void Transfer_between_mapped_accounts_pairs_and_exports_only_the_out_side()
    {
        var (o, i) = TransferSetup();
        Assert.Equal((TxnStatus.Categorized, CategoryId("Savings"), i.Id), (o.Status, o.CategoryId!.Value, o.PairedTxnId!.Value));
        Assert.Equal((TxnStatus.Paired, o.Id), (i.Status, i.PairedTxnId!.Value));

        Assert.Throws<TxnStateException>(() => Txns.MarkReady([i.Id]));
        Txns.MarkReady([o.Id]);
        var result = Export.Export([o.Id]);
        var text = IifWriter.Encoding.GetString(File.ReadAllBytes(result.IifPath));
        Assert.Contains("TRNS\t\tCHECK\t10/10/2026\tChecking\t", text);
        Assert.Contains("SPL\t\tCHECK\t10/10/2026\tSavings\t\t\t500.00", text);
        Assert.DoesNotContain("DEPOSIT", text);
        Assert.Equal(TxnStatus.Exported, Get(o.Id).Status);
        Assert.Equal(i.Id, Get(o.Id).PairedTxnId); // link survived the export lock
    }

    [Fact]
    public void Unpair_restores_both_sides_and_is_remembered()
    {
        var (o, i) = TransferSetup();
        var sides = Txns.Unpair(i.Id);
        Assert.All(sides, t => Assert.Null(t.PairedTxnId));
        Assert.Equal(TxnStatus.New, Get(i.Id).Status);

        Qb.ImportRegister(RegisterCsv(("Checking", "01/01/2020", "Check", null, "x", null, "Rent", -1m)), "again.csv");
        Assert.Null(Get(i.Id).PairedTxnId);
        Assert.Throws<TxnStateException>(() => Txns.Unpair(i.Id));
    }

    [Fact]
    public void Transfer_already_in_qb_on_one_side_needs_no_export_on_either()
    {
        ImportLists();
        ImportText("chk.qbo", BankOfx("111", "500.00", "20261031", Trn("20261010", "-500.00", "C1", "TRANSFER TO SAV")));
        ImportText("sav.qbo", BankOfx("222", "500.00", "20261031", Trn("20261011", "500.00", "S1", "TRANSFER FROM CHK", "CREDIT")));
        Map(("111", "Checking"), ("222", "Savings"));
        Qb.ImportRegister(RegisterCsv(("Checking", "10/10/2026", "Transfer", null, null, "To savings", "Savings", -500m)), "chk-reg.csv");

        var o = AllTxns().Single(t => t.FitId == "C1");
        var i = AllTxns().Single(t => t.FitId == "S1");
        Assert.Equal(TxnStatus.InQb, o.Status);
        Assert.Equal((TxnStatus.Paired, o.Id), (i.Status, i.PairedTxnId!.Value));
        Assert.Empty(Export.PreviewReady().Items);
    }

    [Fact]
    public void Paired_in_side_later_matched_in_its_own_register_becomes_InQb()
    {
        var (o, i) = TransferSetup();
        Qb.ImportRegister(RegisterCsv(("Savings", "10/11/2026", "Deposit", null, null, null, "Checking", 500m)), "sav-reg.csv");
        Assert.Equal(TxnStatus.InQb, Get(i.Id).Status);
        Assert.Equal(o.Id, Get(i.Id).PairedTxnId);
    }
}

public class TemplateAndAuditTests : Phase2TestBase
{
    [Fact]
    public void Template_from_splits_applies_through_a_rule_on_import()
    {
        BankSetup();
        var check = ByName("CHECK 1042"); // -1250.00
        Txns.SetSplits(check.Id,
        [
            new TxnSplit { CategoryId = CategoryId("Rent"), AmountCents = -100000 },
            new TxnSplit { CategoryId = CategoryId("Meals"), AmountCents = -25000 },
        ]);
        var template = Templates.SaveFromTxn(check.Id, "Rent 80/20", TemplateLineKind.Percent);
        Assert.Equal([8000L, 2000L], template.Lines.Select(l => l.Value));
        Assert.Throws<ArgumentException>(() => Templates.SaveFromTxn(check.Id, "rent 80/20", TemplateLineKind.Percent));

        Txns.SaveRuleAndApply(new Rule { Pattern = "LANDLORD", SplitTemplateId = template.Id });
        var r = ImportText("nov.qbo", BankOfx("000987654321", "1.00", "20261110", Trn("20261105", "-1000.00", "RENT11", "LANDLORD PROPERTY")));

        var t = AllTxns().Single(x => x.FitId == "RENT11");
        Assert.Equal((TxnStatus.Categorized, (long?)null), (t.Status, t.CategoryId));
        using var s = Db.Open();
        Assert.Equal([-80000L, -20000L], s.Splits.ForTxn(t.Id).Select(x => x.AmountCents));
        Assert.Equal(TxnStatus.Ready, Txns.MarkReady([t.Id]).Single().Status);
        Assert.Equal(1, r.Statements[0].Categorized);
    }

    [Fact]
    public void Template_needs_saved_splits_and_can_be_deleted()
    {
        BankSetup();
        Assert.Throws<TxnStateException>(() => Templates.SaveFromTxn(ByName("CHECK 1042").Id, "x", TemplateLineKind.Fixed));
        var check = ByName("CHECK 1042");
        Txns.SetSplits(check.Id,
        [
            new TxnSplit { CategoryId = CategoryId("Rent"), AmountCents = -100000 },
            new TxnSplit { CategoryId = CategoryId("Meals"), AmountCents = -25000 },
        ]);
        var t = Templates.SaveFromTxn(check.Id, "Fixed", TemplateLineKind.Fixed);
        Templates.Delete(t.Id);
        Assert.Empty(Templates.GetAll());
    }

    [Fact]
    public void Audit_search_filters_by_date_entity_and_action()
    {
        BankSetup();
        var id = ByName("CHECK 1042").Id;
        Clock.Advance(TimeSpan.FromDays(3));
        Txns.Exclude(id);

        var today = DateOnly.FromDateTime(Clock.Now.UtcDateTime);
        var excl = Audit.Search(today, today, "Txn", "Exclude");
        Assert.Equal(id, Assert.Single(excl).EntityId);
        Assert.Empty(Audit.Search(today.AddDays(-1), today.AddDays(-1), "Txn", "Exclude"));
        Assert.NotEmpty(Audit.Search(null, null, "QboFile", null));
        var (entities, actions) = Audit.Filters();
        Assert.Contains("Txn", entities);
        Assert.Contains("Exclude", actions);
    }
}
