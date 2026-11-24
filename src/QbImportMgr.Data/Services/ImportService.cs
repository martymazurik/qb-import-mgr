using System.Security.Cryptography;
using QbImportMgr.Core;
using QbImportMgr.Core.Domain;
using QbImportMgr.Core.Import;
using QbImportMgr.Core.Ofx;
using QbImportMgr.Core.Rules;
using QbImportMgr.Data.Db;

namespace QbImportMgr.Data.Services;

public enum FileImportStatus { Imported, AlreadyImported, Failed }

public sealed record StatementImportSummary(
    long BankAccountId,
    string AccountDisplay,
    bool NewAccount,
    int Parsed,
    int Inserted,
    int SkippedExisting,
    int FlaggedDuplicates,
    int Categorized,
    int Excluded);

public sealed record FileImportResult(
    string FileName,
    FileImportStatus Status,
    IReadOnlyList<StatementImportSummary> Statements,
    IReadOnlyList<string> Warnings,
    string? Error,
    string? ArchivedTo);

public sealed class ImportService(Database db, AppPaths paths, TimeProvider clock)
{
    public static readonly string[] Extensions = [".qbo", ".qfx", ".ofx"];

    public IReadOnlyList<string> ScanInbox()
    {
        if (!Directory.Exists(paths.Inbox)) return [];
        return Directory.EnumerateFiles(paths.Inbox)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(File.GetLastWriteTimeUtc)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<FileImportResult> ImportInbox() => ScanInbox().Select(ImportFile).ToList();

    /// <summary>
    /// Imports one file in a single transaction, then moves it to the archive. Files that fail to
    /// parse stay in the inbox. An already-imported file (same SHA-256) is archived without changes.
    /// </summary>
    public FileImportResult ImportFile(string path)
    {
        var fileName = Path.GetFileName(path);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (IOException ex) { return Failed(fileName, $"Cannot read file: {ex.Message}"); }

        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        using var s = db.Open();

        if (s.Files.ExistsBySha(sha))
            return new(fileName, FileImportStatus.AlreadyImported, [], [], null, Archive(path));

        OfxDocument doc;
        try { doc = OfxParser.Parse(bytes); }
        catch (OfxParseException ex) { return Failed(fileName, ex.Message); }
        if (doc.Statements.Count == 0)
            return new(fileName, FileImportStatus.Failed, [], doc.Warnings, "No bank or credit card statements in file.", null);

        var now = clock.GetUtcNow().UtcDateTime;
        var summaries = s.InTransaction(() =>
        {
            var file = new QboFile { FileName = fileName, Sha256 = sha, RawBytes = bytes, ImportedUtc = now };
            s.Files.Insert(file);

            var engine = new RuleEngine(s.Rules.GetAll());
            var aliases = s.Aliases.GetAll();
            var hits = new Dictionary<long, int>();
            var result = doc.Statements.Select(st => ImportStatement(s, file.Id, doc, st, engine, aliases, hits, now)).ToList();

            s.Rules.AddHits(hits, now);
            s.Audit.Write("QboFile", file.Id, "Imported", new
            {
                fileName, doc.EncodingName, Format = doc.Format.ToString(),
                Statements = result.Select(r => new { r.AccountDisplay, r.Inserted, r.SkippedExisting, r.FlaggedDuplicates }),
            }, now);
            return result;
        });

        return new(fileName, FileImportStatus.Imported, summaries, doc.Warnings, null, Archive(path));
    }

    private static StatementImportSummary ImportStatement(DbSession s, long fileId, OfxDocument doc, OfxStatement st, RuleEngine engine,
        IReadOnlyList<PayeeAlias> aliases, Dictionary<long, int> hits, DateTime now)
    {
        var hash = AccountIdentity.Hash(st.Kind, st.BankId, st.AcctId);
        var account = s.Accounts.GetByHash(hash);
        bool isNew = account is null;
        if (account is null)
        {
            account = new BankAccount
            {
                OfxAcctIdMasked = AccountIdentity.Mask(st.AcctId),
                AcctIdHash = hash,
                BankId = st.BankId,
                Kind = st.Kind,
                DisplayName = DefaultDisplayName(st),
                CreatedUtc = now,
                FiId = doc.FiId,
                FiOrg = doc.FiOrg,
            };
            s.Accounts.Insert(account);
            s.Audit.Write("BankAccount", account.Id, "Created", new { account.DisplayName, account.OfxAcctIdMasked, Kind = account.Kind.ToCode() }, now);
        }
        else if (account.FiId is null || account.FiOrg is null)
        {
            s.Accounts.FillInstitution(account.Id, doc.FiId, doc.FiOrg);
        }

        var statement = new QboStatement
        {
            QboFileId = fileId, BankAccountId = account.Id, DtStart = st.DtStart, DtEnd = st.DtEnd,
            LedgerBalCents = st.LedgerBalCents, LedgerBalDate = st.LedgerBalDate, TxnCount = st.Transactions.Count,
        };
        s.Files.InsertStatement(statement);

        int inserted = 0, skipped = 0, flagged = 0, categorized = 0, excluded = 0;
        if (st.Transactions.Count > 0)
        {
            var from = st.Transactions.Min(t => t.DtPosted).AddDays(-DuplicateDetector.DateWindowDays);
            var to = st.Transactions.Max(t => t.DtPosted).AddDays(DuplicateDetector.DateWindowDays);
            var fitIds = st.Transactions.Where(t => !string.IsNullOrEmpty(t.FitId)).Select(t => t.FitId!).ToList();
            var existing = s.Txns.DupCandidates(account.Id, from, to, fitIds);

            foreach (var o in st.Transactions)
            {
                long signed = account.InvertSign ? -o.AmountCents : o.AmountCents;
                var candidate = new DupCandidate(0, account.Id, fileId, o.FitId, o.DtPosted, signed, o.Name, TxnStatus.New);
                var dup = DuplicateDetector.Check(candidate, existing);
                if (dup.Kind == DupKind.ExactFitId) { skipped++; continue; }

                var txn = new Txn
                {
                    BankAccountId = account.Id, QboStatementId = statement.Id, FitId = o.FitId, TrnType = o.TrnType,
                    DtPosted = o.DtPosted, DtUser = o.DtUser, AmountCents = o.AmountCents, Name = o.Name, Memo = o.Memo,
                    CheckNum = o.CheckNum, SignedAmountCents = signed, UpdatedUtc = now,
                    Status = dup.Kind == DupKind.Possible ? TxnStatus.Duplicate : TxnStatus.New,
                    DuplicateOfTxnId = dup.Kind == DupKind.Possible ? dup.MatchTxnId : null,
                };
                ApplyAlias(txn, aliases);
                if (engine.Apply(txn) is { } outcome)
                {
                    hits[outcome.Rule.Id] = hits.GetValueOrDefault(outcome.Rule.Id) + 1;
                    if (outcome.NewStatus == TxnStatus.Categorized) categorized++;
                    if (outcome.NewStatus == TxnStatus.Excluded) excluded++;
                }

                s.Txns.Insert(txn);
                inserted++;
                if (txn.Status == TxnStatus.Duplicate) flagged++;
                existing.Add(candidate with { TxnId = txn.Id, Status = txn.Status });
            }
        }

        return new StatementImportSummary(account.Id, account.DisplayName, isNew, st.Transactions.Count, inserted, skipped, flagged, categorized, excluded);
    }

    internal static void ApplyAlias(Txn txn, IReadOnlyList<PayeeAlias> aliases)
    {
        if (PayeeAliasResolver.Resolve(aliases, txn.Name) is not { } alias) return;
        txn.OvrPayee ??= alias.CleanPayee;
        txn.QbNameId ??= alias.QbNameId;
    }

    private static string DefaultDisplayName(OfxStatement st)
    {
        var mask = AccountIdentity.Mask(st.AcctId);
        if (st.Kind == AccountKind.CreditCard) return $"Credit Card {mask}";
        var type = st.AcctType is { Length: > 0 } t ? char.ToUpperInvariant(t[0]) + t[1..].ToLowerInvariant() : "Bank";
        return $"{type} {mask}";
    }

    private string Archive(string path)
    {
        Directory.CreateDirectory(paths.Archive);
        var stamp = clock.GetLocalNow().ToString("yyyyMMdd_HHmmss");
        var dest = Path.Combine(paths.Archive, $"{stamp}_{Path.GetFileName(path)}");
        for (int i = 2; File.Exists(dest); i++)
            dest = Path.Combine(paths.Archive, $"{stamp}_{i}_{Path.GetFileName(path)}");
        File.Move(path, dest);
        return dest;
    }

    private static FileImportResult Failed(string fileName, string error) =>
        new(fileName, FileImportStatus.Failed, [], [], error, null);
}
