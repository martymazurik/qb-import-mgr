using QbImportMgr.Core.Domain;
using QbImportMgr.Data.Services;

namespace QbImportMgr.Tests.Data;

public class TxnServiceTests : DataTestBase
{
    private Txn FlaggedDuplicate()
    {
        ImportText("a.qbo", BankOfx("777", "1.00", "20261031", Trn("20261010", "-60.00", "F2", "GAS STATION 123")));
        ImportText("b.qbo", BankOfx("777", "1.00", "20261101", Trn("20261011", "-60.00", "F2-NEW", "GAS STATION 123")));
        return AllTxns().Single(t => t.Status == TxnStatus.Duplicate);
    }

    [Fact]
    public void Not_a_duplicate_returns_to_new_clears_link_reruns_rules_and_audits()
    {
        var dup = FlaggedDuplicate();
        Lists.Import(TestData.Sample("qb_lists.iif"));
        using (var s = Db.Open())
            s.Rules.Insert(new Rule { Pattern = "GAS STATION", CategoryId = CategoryId("Travel") });

        var t = Txns.NotADuplicate(dup.Id);

        Assert.Equal(TxnStatus.Categorized, t.Status); // rules re-ran
        Assert.Null(t.DuplicateOfTxnId);
        Assert.Equal(CategoryId("Travel"), t.CategoryId);
        var saved = Get(dup.Id);
        Assert.Equal((TxnStatus.Categorized, (long?)null), (saved.Status, saved.DuplicateOfTxnId));

        using var s2 = Db.Open();
        var audit = s2.Audit.For("Txn", dup.Id).Single(a => a.Action == "NotADuplicate");
        Assert.Contains("\"WasDuplicateOf\":" + dup.DuplicateOfTxnId, audit.Detail);
        Assert.Contains("\"From\":\"Duplicate\"", audit.Detail);
    }

    [Fact]
    public void Not_a_duplicate_without_matching_rule_is_new()
    {
        var t = Txns.NotADuplicate(FlaggedDuplicate().Id);
        Assert.Equal(TxnStatus.New, t.Status);
    }

    [Fact]
    public void Confirm_duplicate_moves_to_excluded_and_keeps_link()
    {
        var dup = FlaggedDuplicate();
        var t = Txns.ConfirmDuplicate(dup.Id);
        Assert.Equal(TxnStatus.Excluded, t.Status);
        Assert.Equal(dup.DuplicateOfTxnId, Get(dup.Id).DuplicateOfTxnId);
    }

    [Fact]
    public void Duplicate_actions_require_duplicate_status()
    {
        ImportSample("bank_sgml_1x.qbo");
        var id = AllTxns()[0].Id;
        Assert.Throws<TxnStateException>(() => Txns.NotADuplicate(id));
        Assert.Throws<TxnStateException>(() => Txns.ConfirmDuplicate(id));
    }

    [Fact]
    public void Category_drives_status_and_ready_requires_categorization()
    {
        ImportSample("bank_sgml_1x.qbo");
        Lists.Import(TestData.Sample("qb_lists.iif"));
        var id = AllTxns()[0].Id;

        Assert.Throws<TxnStateException>(() => Txns.MarkReady([id]));
        Assert.Equal(TxnStatus.Categorized, Txns.SetCategory(id, CategoryId("Travel")).Status);
        Assert.Equal(TxnStatus.Ready, Txns.MarkReady([id])[0].Status);
        Assert.Equal(TxnStatus.Ready, Txns.SetCategory(id, CategoryId("Meals and Entertainment")).Status);
        Assert.Equal(TxnStatus.New, Txns.SetCategory(id, null).Status);
    }

    [Fact]
    public void Category_and_splits_are_mutually_exclusive()
    {
        ImportSample("bank_sgml_1x.qbo");
        Lists.Import(TestData.Sample("qb_lists.iif"));
        var t = AllTxns().Single(x => x.CheckNum == "1042"); // -1250.00
        Txns.SetCategory(t.Id, CategoryId("Travel"));

        var after = Txns.SetSplits(t.Id,
        [
            new TxnSplit { CategoryId = CategoryId("Travel"), AmountCents = -100000 },
            new TxnSplit { CategoryId = CategoryId("Meals and Entertainment"), AmountCents = -25000 },
        ]);
        Assert.Null(after.CategoryId);
        Assert.Equal(TxnStatus.Categorized, after.Status);

        Txns.SetCategory(t.Id, CategoryId("Travel"));
        using var s = Db.Open();
        Assert.Empty(s.Splits.ForTxn(t.Id));
    }

    [Fact]
    public void Overrides_store_only_differences()
    {
        ImportSample("bank_sgml_1x.qbo");
        var t = AllTxns()[0];
        var after = Txns.SetOverrides(t.Id, t.DtPosted, "AT&T", t.Memo, null);
        Assert.Null(after.OvrDate);
        Assert.Equal("AT&T", after.OvrPayee);
        Assert.Null(after.OvrMemo);
        Assert.Equal("AT&T *PAYMENT", Get(t.Id).Name);
    }

    [Fact]
    public void Exclude_and_include_reruns_rules()
    {
        ImportSample("bank_sgml_1x.qbo");
        var id = AllTxns()[0].Id;
        Assert.Equal(TxnStatus.Excluded, Txns.Exclude(id).Status);
        Assert.Throws<TxnStateException>(() => Txns.MarkReady([id]));
        Assert.Equal(TxnStatus.New, Txns.Include(id).Status);
    }

    [Fact]
    public void Save_rule_applies_to_new_rows_only()
    {
        ImportSample("amex_ccstmt.qfx");
        Lists.Import(TestData.Sample("qb_lists.iif"));
        var excluded = AllTxns().First(t => t.Name == "PARKING METER 4471");
        Txns.Exclude(excluded.Id);

        var (rule, applied) = Txns.SaveRuleAndApply(new Rule { Pattern = "PARKING", CategoryId = CategoryId("Travel") });
        Assert.Equal(1, applied);
        Assert.True(rule.Id > 0);
        Assert.Equal(TxnStatus.Excluded, Get(excluded.Id).Status);

        Assert.Throws<ArgumentException>(() => Txns.SaveRuleAndApply(new Rule { Pattern = "(bad", MatchKind = MatchKind.Regex }));
    }

    [Fact]
    public void Exported_rows_refuse_service_edits()
    {
        ImportSample("bank_sgml_1x.qbo");
        SetUpQuickBooks();
        var id = AllTxns()[0].Id;
        Txns.SetCategory(id, CategoryId("Travel"));
        Txns.MarkReady([id]);
        Export.Export([id]);

        Assert.Throws<TxnLockedException>(() => Txns.SetCategory(id, null));
        Assert.Throws<TxnLockedException>(() => Txns.Exclude(id));
        Assert.Throws<TxnLockedException>(() => Txns.SetOverrides(id, null, "x", null, null));
    }
}
