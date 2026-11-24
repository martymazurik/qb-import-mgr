using QbImportMgr.Core;
using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Ofx;
using QbImportMgr.Data.Services;

namespace QbImportMgr.Tests.Data;

/// <summary>Service behaviour added for the UI: institution info, bulk actions, undo, split balance, export summary.</summary>
public class UiSupportTests : DataTestBase
{
    [Fact]
    public void Institution_id_is_read_from_intu_bid_or_fid_and_stored()
    {
        Assert.Equal(("1001", "Sample Bank"), (OfxParser.Parse(TestData.Sample("bank_sgml_1x.qbo")) is var d ? (d.FiId, d.FiOrg) : default));
        Assert.Equal("3101", OfxParser.Parse(TestData.Sample("amex_ccstmt.qfx")).FiId);
        Assert.Null(OfxParser.Parse(TestData.Sample("ofx2_xml.ofx")).FiId);

        ImportSample("amex_ccstmt.qfx");
        var a = Accounts.GetAll().Single();
        Assert.Equal(("3101", "AMEX", "****1005", (string?)null), (a.FiId, a.FiOrg, a.OfxAcctIdMasked, a.BankId));
    }

    [Fact]
    public void Approve_moves_categorized_to_ready_and_skips_the_rest()
    {
        ImportSample("bank_sgml_1x.qbo");
        Lists.Import(TestData.Sample("qb_lists.iif"));
        var ids = AllTxns().Select(t => t.Id).ToList();
        Txns.SetCategory(ids.Take(2).ToList(), CategoryId("Travel"));
        Txns.Exclude(ids[2]);

        var r = Txns.Approve(ids);

        Assert.Equal(2, r.Changed.Count);
        Assert.All(r.Changed, t => Assert.Equal(TxnStatus.Ready, t.Status));
        Assert.Equal(3, r.Skipped.Count); // excluded + two New
        Assert.Equal(TxnStatus.Excluded, Get(ids[2]).Status);
    }

    [Fact]
    public void Bulk_set_category_and_exclude_skip_locked_rows_for_exclude()
    {
        ImportSample("bank_sgml_1x.qbo");
        SetUpQuickBooks();
        var ids = AllTxns().Select(t => t.Id).ToList();
        var r = Txns.SetCategory(ids, CategoryId("Travel"));
        Assert.Equal(5, r.Changed.Count);
        Assert.All(AllTxns(), t => Assert.Equal(TxnStatus.Categorized, t.Status));

        Txns.MarkReady([ids[0]]);
        Export.Export([ids[0]]);
        var ex = Txns.Exclude(ids);
        Assert.Equal(4, ex.Changed.Count);
        Assert.Single(ex.Skipped);
        Assert.Equal(TxnStatus.Exported, Get(ids[0]).Status);
    }

    [Fact]
    public void Ready_is_blocked_until_splits_balance()
    {
        ImportSample("bank_sgml_1x.qbo");
        Lists.Import(TestData.Sample("qb_lists.iif"));
        var check = AllTxns().Single(t => t.CheckNum == "1042"); // -1250.00
        Txns.SetSplits(check.Id,
        [
            new TxnSplit { CategoryId = CategoryId("Travel"), AmountCents = SplitMath.ToStoredCents(100000, check.SignedAmountCents) },
            new TxnSplit { CategoryId = CategoryId("Meals and Entertainment"), AmountCents = SplitMath.ToStoredCents(24999, check.SignedAmountCents) },
        ]);

        var ex = Assert.Throws<TxnStateException>(() => Txns.MarkReady([check.Id]));
        Assert.Contains("splits total", ex.Message);
        Assert.Single(Txns.Approve([check.Id]).Skipped);

        using (var s = Db.Open())
        {
            var splits = s.Splits.ForTxn(check.Id);
            splits[1].AmountCents = -25000;
            Txns.SetSplits(check.Id, splits);
        }
        Assert.Equal(TxnStatus.Ready, Txns.MarkReady([check.Id]).Single().Status);
    }

    [Fact]
    public void Undo_restores_status_category_overrides_and_splits()
    {
        ImportSample("bank_sgml_1x.qbo");
        Lists.Import(TestData.Sample("qb_lists.iif"));
        var check = AllTxns().Single(t => t.CheckNum == "1042");
        Txns.SetSplits(check.Id,
        [
            new TxnSplit { CategoryId = CategoryId("Travel"), AmountCents = -100000 },
            new TxnSplit { CategoryId = CategoryId("Meals and Entertainment"), AmountCents = -25000 },
        ]);
        Txns.SetOverrides(check.Id, null, "Landlord", null, null);
        var before = Txns.Snapshot([check.Id]);

        Txns.SetCategory(check.Id, CategoryId("Travel")); // wipes splits
        Txns.MarkReady([check.Id]);
        Txns.SetOverrides(check.Id, null, null, null, null);

        var restored = Txns.Restore(before).Single();
        Assert.Equal(TxnStatus.Categorized, restored.Status);
        Assert.Null(restored.CategoryId);
        Assert.Equal("Landlord", restored.OvrPayee);
        using var s = Db.Open();
        Assert.Equal([-100000L, -25000L], s.Splits.ForTxn(check.Id).Select(x => x.AmountCents));
        Assert.Contains(s.Audit.For("Txn", check.Id), a => a.Action == "Undo");
    }

    [Fact]
    public void Undo_of_remember_as_rule_restores_rows_and_deletes_rule()
    {
        ImportSample("amex_ccstmt.qfx");
        Lists.Import(TestData.Sample("qb_lists.iif"));

        var result = Txns.SaveRuleAndApply(new Rule { Pattern = "PARKING", CategoryId = CategoryId("Travel") });
        Assert.Equal(2, result.ChangedBefore.Count);
        Assert.Equal(2, AllTxns().Count(t => t.Status == TxnStatus.Categorized));

        Txns.Restore(result.ChangedBefore);
        Txns.DeleteRule(result.Rule.Id);

        Assert.All(AllTxns(), t => Assert.Equal((TxnStatus.New, (long?)null, (long?)null), (t.Status, t.CategoryId, t.RuleId)));
        using var s = Db.Open();
        Assert.Empty(s.Rules.GetAll());
    }

    [Fact]
    public void Undo_cannot_touch_exported_rows()
    {
        ImportSample("bank_sgml_1x.qbo");
        SetUpQuickBooks();
        var id = AllTxns()[0].Id;
        var before = Txns.Snapshot([id]);
        Txns.SetCategory(id, CategoryId("Travel"));
        Txns.MarkReady([id]);
        Export.Export([id]);
        Assert.Throws<TxnLockedException>(() => Txns.Restore(before));
    }

    [Fact]
    public void Export_preview_summarizes_per_account_and_flags_first_export()
    {
        ImportSample("multi_stmt.ofx");
        SetUpQuickBooks();
        var ids = AllTxns().Select(t => t.Id).ToList();
        Txns.SetCategory(ids, CategoryId("Travel"));
        Txns.MarkReady(ids);

        var preview = Export.PreviewReady();
        Assert.True(preview.IsFirstExport);
        Assert.Equal(
            [("Checking ****2233", 2, 6000L), ("Credit Card ****1111", 1, -6310L), ("Savings ****2244", 1, 123L)],
            preview.ByAccount.Select(a => (a.AccountDisplay, a.Count, a.NetCents)));

        Export.Export(ids.Take(1));
        Assert.False(Export.PreviewReady().IsFirstExport);
    }

    [Fact]
    public void Schema_v2_upgrades_a_v1_database()
    {
        var path = Path.Combine(Paths.Root, "v1.db");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            using var stream = typeof(QbImportMgr.Data.Db.Database).Assembly.GetManifestResourceStream("QbImportMgr.Data.Db.Schema_001.sql")!;
            cmd.CommandText = new StreamReader(stream).ReadToEnd() + ";PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(2, new QbImportMgr.Data.Db.Database(path).Migrate());
    }

    [Theory]
    [InlineData(new[] { "--data-dir", @"D:\books" }, null, false, @"D:\books")]
    [InlineData(new[] { @"--data-dir=""D:\my books""" }, null, false, @"D:\my books")]
    [InlineData(new[] { "--DATA-DIR", @"D:\a" }, @"E:\env", true, @"D:\a")]
    [InlineData(new string[] { }, @"E:\env", false, @"E:\env")]
    [InlineData(new string[] { }, "  ", false, AppPaths.DefaultRoot)]
    [InlineData(new string[] { }, null, false, AppPaths.DefaultRoot)]
    [InlineData(new string[] { }, null, true, AppPaths.DevRoot)]
    public void Data_dir_precedence(string[] args, string? env, bool debug, string expected) =>
        Assert.Equal(expected, AppPaths.ResolveRoot(args, _ => env, debug));

    [Theory]
    [InlineData("--data-dir")]
    [InlineData("--data-dir=")]
    public void Data_dir_without_value_is_an_error(string arg) =>
        Assert.Throws<ArgumentException>(() => AppPaths.ResolveRoot([arg], _ => null, false));

    [Fact]
    public void Split_math_works_in_the_direction_of_the_transaction()
    {
        Assert.Equal(-100000, SplitMath.ToStoredCents(100000, -125000));
        Assert.Equal(100000, SplitMath.ToEnteredCents(-100000, -125000));
        Assert.Equal(5000, SplitMath.ToStoredCents(5000, 20000));
        Assert.Equal(-500, SplitMath.ToStoredCents(-500, 20000)); // a line against the transaction
        Assert.Equal(25000, SplitMath.Remainder(-125000, [100000]));
        Assert.Equal(0, SplitMath.Remainder(-125000, [100000, 25000]));
        Assert.True(SplitMath.Balances(-125000, [-100000, -25000]));
        Assert.False(SplitMath.Balances(-125000, [-100000, -24999]));
    }
}
