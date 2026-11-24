using System.Security.Cryptography;
using System.Text;
using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Qb;
using QbImportMgr.Core.Rules;
using QbImportMgr.Data.Db;

namespace QbImportMgr.Data.Services;

public sealed record RegisterInspection(bool HasAccountColumn, int Rows, DateOnly? From, DateOnly? To,
    IReadOnlyList<string> QbAccounts, IReadOnlyList<string> Warnings);

public sealed record RegisterAccountResult(string QbAccount, string? BankAccount, int Rows, int Added, int Removed);

public sealed record RegisterImportResult(
    string FileName, int Rows, int Added, int Unchanged, int Removed,
    IReadOnlyList<RegisterAccountResult> Accounts, ReconcileSummary Reconcile,
    IReadOnlyList<string> Warnings)
{
    /// <summary>QB accounts in the file that no bank account is mapped to: kept for learning only.</summary>
    public IReadOnlyList<string> UnmappedAccounts => Accounts.Where(a => a.BankAccount is null).Select(a => a.QbAccount).ToList();
}

public enum ProposalState { New, UpdatesLearned, Covered, Conflict, MissingAccount }

public sealed record ProposalView(LearnProposal Proposal, string Pattern, long? CategoryId, long? QbNameId, ProposalState State, string Note, int BankHits)
{
    public bool CanAccept => State is ProposalState.New or ProposalState.UpdatesLearned;
}

public sealed record LearningPreview(IReadOnlyList<ProposalView> Proposals, IReadOnlyList<PayeeProfile> Profiles, int RegisterRows, int MatchedPairs);

public sealed record LearnAcceptResult(int RulesCreated, int RulesUpdated, int AliasesCreated, int AliasesUpdated, IReadOnlyList<string> Skipped, int RowsRecategorized);

public sealed class QbService(Database db, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ================================================================== register import

    public RegisterInspection Inspect(byte[] csv)
    {
        var f = QbRegisterCsvReader.Read(csv);
        return new RegisterInspection(f.HasAccountColumn, f.Rows.Count,
            f.Rows.Count > 0 ? f.Rows.Min(r => r.Date) : null, f.Rows.Count > 0 ? f.Rows.Max(r => r.Date) : null,
            f.Rows.Select(r => r.Account).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(), f.Warnings);
    }

    /// <summary>
    /// Stores a Transaction Detail CSV in QbRegisterTxn and reconciles. Rows map to bank accounts by
    /// the Account column (QB account name), or all to <paramref name="accountIdForFile"/> when the
    /// file has no Account column. Re-importing an overlapping range is idempotent: identical rows
    /// keep their identity, and unmatched rows missing from the new file inside its date range are
    /// removed (changed or deleted in QuickBooks).
    /// </summary>
    public RegisterImportResult ImportRegister(byte[] csv, string fileName, long? accountIdForFile = null)
    {
        var file = QbRegisterCsvReader.Read(csv);
        var now = Now;
        using var s = db.Open();
        var accounts = s.Accounts.GetAll();

        string? fileAccountName = null;
        if (!file.HasAccountColumn)
        {
            var a = accountIdForFile is { } id ? accounts.FirstOrDefault(x => x.Id == id) : null;
            if (a?.QbAccountName is null)
                throw new InvalidOperationException("This report has no Account column. Choose which mapped bank or credit card account it belongs to.");
            fileAccountName = a.QbAccountName;
        }

        var rows = file.Rows.Select(r => (Row: r, Account: r.Account ?? fileAccountName)).Where(x => x.Account is not null).ToList();
        int noAccount = file.Rows.Count - rows.Count;
        var warnings = file.Warnings.ToList();
        if (noAccount > 0) warnings.Add($"{noAccount} row(s) had no account and were skipped.");

        return s.InTransaction(() =>
        {
            int added = 0, unchanged = 0, removed = 0;
            var perAccount = new List<RegisterAccountResult>();
            var touched = new HashSet<long>();

            foreach (var g in rows.GroupBy(x => x.Account!, StringComparer.OrdinalIgnoreCase))
            {
                var bank = AccountResolver.Resolve(g.Key, accounts);
                var keys = new HashSet<string>();
                var occurrences = new Dictionary<string, int>();
                int gAdded = 0;
                foreach (var (r, _) in g)
                {
                    var tuple = $"{g.Key.ToUpperInvariant()}|{r.Date:yyyy-MM-dd}|{r.Type}|{r.Num}|{r.Name}|{r.Memo}|{r.Split}|{r.AmountCents}";
                    int n = occurrences[tuple] = occurrences.GetValueOrDefault(tuple) + 1;
                    var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tuple}|#{n}")));
                    keys.Add(key);
                    bool inserted = s.QbRegister.Upsert(new QbRegisterTxn
                    {
                        BankAccountId = bank?.Id, QbAccountName = g.Key, TxnDate = r.Date, TxnType = r.Type, Num = r.Num,
                        Name = r.Name, Memo = r.Memo, Clr = r.Clr, SplitAccount = r.Split, AmountCents = r.AmountCents,
                        SourceFile = fileName, ImportedUtc = now, RowKey = key,
                    });
                    if (inserted) { added++; gAdded++; } else unchanged++;
                }
                int gRemoved = s.QbRegister.DeleteMissing(g.Key, g.Min(x => x.Row.Date), g.Max(x => x.Row.Date), keys);
                removed += gRemoved;
                if (bank is not null) touched.Add(bank.Id);
                perAccount.Add(new RegisterAccountResult(g.Key, bank?.DisplayName, g.Count(), gAdded, gRemoved));
            }

            var rec = Reconciler.Run(s, touched, now);
            var result = new RegisterImportResult(fileName, rows.Count, added, unchanged, removed, perAccount, rec, warnings);
            s.Audit.Write("QbRegister", 0, "Imported", new { fileName, result.Rows, added, unchanged, removed, Reconcile = rec }, now);
            return result;
        });
    }

    public int RegisterRowCount()
    {
        using var s = db.Open();
        return s.QbRegister.Count();
    }

    public QbRegisterTxn? RegisterRow(long id)
    {
        using var s = db.Open();
        return s.QbRegister.Get(id);
    }

    // ================================================================== learning

    public LearningPreview Preview()
    {
        using var s = db.Open();
        var profiles = PayeeLearner.BuildProfiles(s.QbRegister.LearningRows());
        var pairs = s.QbRegister.MatchedPairs();
        var proposals = PayeeLearner.Propose(pairs, profiles);
        var ctx = new Context(s);
        return new LearningPreview(proposals.Select(p => ctx.View(p, p.Pattern)).ToList(), profiles, s.QbRegister.Count(), pairs.Count);
    }

    /// <summary>
    /// Accepts proposals (with optionally edited patterns). Each becomes a learned Rule (payee pattern →
    /// category + QB name, priority 200 so hand-made rules still win) and a learned PayeeAlias (clean
    /// payee = QB name). Hand-made rules and aliases are never overwritten; conflicts are skipped and
    /// reported. Then rules re-run on every New row.
    /// </summary>
    public LearnAcceptResult Accept(IEnumerable<(LearnProposal Proposal, string Pattern)> accepted)
    {
        var now = Now;
        using var s = db.Open();
        return s.InTransaction(() =>
        {
            int rc = 0, ru = 0, ac = 0, au = 0;
            var skipped = new List<string>();
            foreach (var (p, pattern) in accepted)
            {
                var ctx = new Context(s);
                var v = ctx.View(p, pattern);
                if (!v.CanAccept)
                {
                    skipped.Add($"{v.Pattern}: {v.Note}");
                    continue;
                }
                if (v.State == ProposalState.UpdatesLearned && ctx.LearnedRuleWithPattern(v.Pattern) is { } existing)
                {
                    existing.CategoryId = v.CategoryId;
                    existing.QbNameId = v.QbNameId;
                    s.Rules.Update(existing);
                    s.Audit.Write("Rule", existing.Id, "LearnedUpdated", new { v.Pattern, p.QbName, p.Account }, now);
                    ru++;
                }
                else
                {
                    var rule = new Rule
                    {
                        Priority = 200, Field = RuleField.Payee, MatchKind = MatchKind.Contains, Pattern = v.Pattern, Sign = SignFilter.Any,
                        CategoryId = v.CategoryId, QbNameId = v.QbNameId, IsEnabled = true, Source = RuleSources.LearnedFromQb,
                    };
                    s.Rules.Insert(rule);
                    s.Audit.Write("Rule", rule.Id, "Learned", new { v.Pattern, p.QbName, p.Account, p.Count, p.Agreement }, now);
                    rc++;
                }

                var alias = s.Aliases.GetAll().FirstOrDefault(a => a.RawPattern.Equals(v.Pattern, StringComparison.OrdinalIgnoreCase));
                if (alias is null)
                {
                    var a = new PayeeAlias { RawPattern = v.Pattern, MatchKind = MatchKind.Contains, CleanPayee = p.QbName, QbNameId = v.QbNameId, Source = RuleSources.LearnedFromQb };
                    s.Aliases.Insert(a);
                    s.Audit.Write("PayeeAlias", a.Id, "Learned", new { v.Pattern, p.QbName }, now);
                    ac++;
                }
                else if (alias.Source == RuleSources.LearnedFromQb && (alias.CleanPayee != p.QbName || alias.QbNameId != v.QbNameId))
                {
                    alias.CleanPayee = p.QbName;
                    alias.QbNameId = v.QbNameId;
                    s.Aliases.Update(alias);
                    s.Audit.Write("PayeeAlias", alias.Id, "LearnedUpdated", new { v.Pattern, p.QbName }, now);
                    au++;
                }
            }

            var changed = RuleRunner.ApplyToAllNew(s, now);
            Reconciler.Run(s, [], now);
            return new LearnAcceptResult(rc, ru, ac, au, skipped, changed.Count);
        });
    }

    /// <summary>Lookups for evaluating proposals against existing rules and data.</summary>
    private sealed class Context(DbSession s)
    {
        private readonly IReadOnlyList<Rule> _rules = s.Rules.GetAll();
        private readonly IReadOnlyList<string> _bankNames = s.Txns.AllNames();
        private readonly Dictionary<long, string> _categoryNames = s.Categories.GetAll().ToDictionary(c => c.Id, c => c.FullName);

        public Rule? LearnedRuleWithPattern(string pattern) =>
            _rules.FirstOrDefault(r => r.IsLearned && r.Pattern.Equals(pattern, StringComparison.OrdinalIgnoreCase));

        public ProposalView View(LearnProposal p, string pattern)
        {
            pattern = pattern.Trim();
            var cat = s.Categories.GetByName(p.Account);
            var name = s.Names.FindByName(p.QbName);
            var hits = _bankNames.Where(n => TextMatcher.IsMatch(MatchKind.Contains, pattern, n)).ToList();
            if (pattern.Length < 3)
                return new(p, pattern, cat?.Id, name?.Id, ProposalState.Conflict, "Pattern is too short.", hits.Count);
            if (cat is null || !cat.IsActive)
                return new(p, pattern, null, name?.Id, ProposalState.MissingAccount, $"QB account \"{p.Account}\" is not in the imported lists (or inactive).", hits.Count);

            // Text the pattern is meant for: samples, a few existing bank rows it hits, and the pattern itself.
            var probes = p.Samples.Concat(hits.Take(20)).Append(pattern).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var overlapping = _rules.Where(r => r.Field is RuleField.Payee or RuleField.Any &&
                    (r.Pattern.Equals(pattern, StringComparison.OrdinalIgnoreCase) || probes.Any(t => TextMatcher.IsMatch(r.MatchKind, r.Pattern, t))))
                .ToList();

            foreach (var r in overlapping.Where(r => !r.IsLearned).OrderBy(r => r.Priority))
            {
                bool agrees = !r.AutoExclude && r.SplitTemplateId is null && r.CategoryId == cat.Id && (r.QbNameId is null || r.QbNameId == name?.Id);
                return agrees
                    ? new(p, pattern, cat.Id, name?.Id, ProposalState.Covered, $"Already covered by hand-made rule #{r.Id} \"{r.Pattern}\".", hits.Count)
                    : new(p, pattern, cat.Id, name?.Id, ProposalState.Conflict,
                        $"Conflicts with hand-made rule #{r.Id} \"{r.Pattern}\" → {Describe(r)}. Hand-made rules are never overwritten.", hits.Count);
            }

            if (LearnedRuleWithPattern(pattern) is { } learned)
            {
                bool same = learned.CategoryId == cat.Id && learned.QbNameId == name?.Id;
                return same
                    ? new(p, pattern, cat.Id, name?.Id, ProposalState.Covered, $"Already learned (rule #{learned.Id}).", hits.Count)
                    : new(p, pattern, cat.Id, name?.Id, ProposalState.UpdatesLearned, $"Updates learned rule #{learned.Id}.", hits.Count);
            }

            var note = name is null ? $"QB name \"{p.QbName}\" not in imported lists; rule sets the account only." : "";
            return new(p, pattern, cat.Id, name?.Id, ProposalState.New, note, hits.Count);
        }

        private string Describe(Rule r) =>
            r.AutoExclude ? "auto-exclude"
            : r.SplitTemplateId is not null ? "split template"
            : r.CategoryId is { } c && _categoryNames.TryGetValue(c, out var n) ? n : "(no category)";
    }
}

public sealed class TemplateService(Database db, TimeProvider clock)
{
    public IReadOnlyList<SplitTemplate> GetAll()
    {
        using var s = db.Open();
        return s.Templates.GetAll();
    }

    /// <summary>Saves a transaction's current splits as a named template (percentages or fixed amounts).</summary>
    public SplitTemplate SaveFromTxn(long txnId, string name, TemplateLineKind kind)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Template name is required.");
        var now = clock.GetUtcNow().UtcDateTime;
        using var s = db.Open();
        return s.InTransaction(() =>
        {
            var t = s.Txns.Get(txnId) ?? throw new KeyNotFoundException($"Transaction {txnId} not found.");
            var splits = s.Splits.ForTxn(txnId);
            if (splits.Count < 2) throw new TxnStateException("Save the splits first; a template needs at least two lines.");
            if (s.Templates.GetAll().Any(x => x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"A template named \"{name.Trim()}\" already exists.");
            var template = new SplitTemplate { Name = name.Trim(), Lines = SplitTemplateMath.FromSplits(splits, t.SignedAmountCents, kind).ToList() };
            s.Templates.Insert(template, now);
            s.Audit.Write("SplitTemplate", template.Id, "Created", new { template.Name, Kind = kind.ToString(), Lines = template.Lines.Count }, now);
            return template;
        });
    }

    public void Delete(long id)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        using var s = db.Open();
        s.InTransaction(() =>
        {
            s.Templates.Delete(id);
            s.Audit.Write("SplitTemplate", id, "Deleted", null, now);
        });
    }
}

public sealed class AuditService(Database db)
{
    public IReadOnlyList<AuditLogEntry> Search(DateOnly? from, DateOnly? to, string? entity, string? action, int limit = 5000)
    {
        using var s = db.Open();
        return s.Audit.Search(from, to, string.IsNullOrWhiteSpace(entity) ? null : entity, string.IsNullOrWhiteSpace(action) ? null : action, limit);
    }

    public (IReadOnlyList<string> Entities, IReadOnlyList<string> Actions) Filters()
    {
        using var s = db.Open();
        return (s.Audit.Entities(), s.Audit.Actions());
    }
}
