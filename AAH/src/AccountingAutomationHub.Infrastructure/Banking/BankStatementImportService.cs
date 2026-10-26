using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Spreadsheets;
using AccountingAutomationHub.Infrastructure.Imports;

namespace AccountingAutomationHub.Infrastructure.Banking;

public sealed partial class BankStatementImportService(
    IBankingRepository repository,
    SqliteMasterRepository masterRepository) : IBankStatementImportService
{
    private readonly FlexibleWorkbookReader _workbookReader = new();

    private static readonly string[] DateHeaders = ["DATE","VALUE DATE","TXN DATE","TRAN DATE","TRANSACTION DATE","POST DATE","BOOKING DATE","VAL DATE","POSTING DATE"];
    private static readonly string[] ValueDateHeaders = ["VALUE DATE","VAL DATE"];
    private static readonly string[] DebitHeaders = ["PAID OUT","AMT DR","AMOUNT DR","DEBIT AMOUNT","WITHDRAWALS","WITHDRAWAL","DEBIT","DR","PAYMENT"];
    private static readonly string[] CreditHeaders = ["RECEIVED IN","AMT CR","AMOUNT CR","CREDIT AMOUNT","DEPOSITS","DEPOSIT","CREDIT","CR","RECEIPT"];
    private static readonly string[] AmountHeaders = ["TRANSACTION AMOUNT","TXN AMT","NET AMT","TOTAL AMT","AMOUNT","FIGURE","AMT"];
    private static readonly string[] BalanceHeaders = ["RUNNING BALANCE","CLOSING BALANCE","AVAILABLE BALANCE","BALANCE"];
    private static readonly string[] NarrationHeaders = ["DETAILS","NARRATION","PARTICULARS","DESCRIPTION","TRANSACTION DETAILS","TRANSACTION DESCRIPTION","TXN DESCRIPTION","TRANSACTION REMARKS","REMARKS","MEMO","REMARK NARRATION","ORIGINAL NARRATION","ORIGINAL DESCRIPTION","TRANSACTION NARRATION","TRANSACTION PARTICULARS"];
    private static readonly string[] ReferenceHeaders = ["REFERENCE","REF NO","REF.NO","CHEQUE NO","CHQ NO","UTR","TRANSACTION ID","TRANSACTION NO","TRANSACTION NUMBER","TXN ID","TXN NO","TXN NUMBER"];
    private static readonly string[] DrCrHeaders = ["DR/CR","DR CR","TYPE","TRANSACTION TYPE"];

    // Excel bank-statement parsing is intentionally role based. A column is not treated as
    // narration merely because it was not mapped to another field. Header meaning and sampled
    // cell content must both support the role, which prevents Txn No., balances, dates and
    // placeholder columns from leaking into Narration.
    private static readonly string[] NarrationNegativeHeaders =
    [
        "ACCOUNT", "ACCOUNT NO", "ACCOUNT NUMBER", "IFSC", "BRANCH", "CURRENCY",
        "SERIAL", "SERIAL NO", "S NO", "SL NO", "ROW NO", "LINE NO",
        "STATUS", "CATEGORY", "CHANNEL", "CURRENCY CODE"
    ];

    private static readonly HashSet<string> NarrationPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "-", "--", "---", ".", "..", "NA", "N/A", "N.A.", "NIL", "NONE", "NULL"
    };

    private const int ExcelHeaderScanLimit = 120;
    private const int ExcelProfileSampleRows = 80;



    public sealed class BankStatementSourceCache
    {
        public required string OriginalPath { get; init; }
        public required string IdentityPath { get; init; }
        public required string DisplayName { get; init; }
        public required string FileHash { get; init; }
        public long FileSize { get; init; }
        public DateTime LastWriteUtc { get; init; }
        public double SourceAccessMilliseconds { get; init; }
        public string ResolvedPassword { get; init; } = string.Empty;
        public required OpenXmlWorkbookData Workbook { get; init; }
    }

    public Task<BankStatementSourceCache> ExtractSourceAsync(
        string sourcePath,
        string identityPath,
        string displayName,
        string fileHash,
        string? initialPassword = null,
        bool recoveryMode = false,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ExtractSource(sourcePath, identityPath, displayName, fileHash, initialPassword ?? string.Empty, recoveryMode, cancellationToken), cancellationToken);

    public bool TryGetBankIdentity(
        BankStatementSourceCache source,
        out string bankName,
        out string accountNumber,
        out string accountType)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var headerMappingScope = ImportHeaderMappingRuntime.BeginSource(source.OriginalPath);
        var sheets = ConvertWorkbookData(source.Workbook);
        var candidate = sheets
            .Select(sheet => AnalyzeSheet(sheet))
            .Where(item => item.HeaderRow > 0)
            .OrderByDescending(item => item.Score)
            .FirstOrDefault();
        if (candidate is null)
        {
            bankName = string.Empty;
            accountNumber = string.Empty;
            accountType = string.Empty;
            return false;
        }
        var metadata = DetectMetadata(sheets, candidate, source.DisplayName);
        bankName = metadata.BankName?.Trim() ?? string.Empty;
        accountNumber = metadata.AccountNumber?.Trim() ?? string.Empty;
        accountType = metadata.AccountType?.Trim() ?? string.Empty;
        return bankName.Length > 0 && !bankName.Equals("Unknown Bank", StringComparison.OrdinalIgnoreCase) &&
               accountNumber.Length > 0 && !accountNumber.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    public Task<BankStatementImportResult> ImportCachedAsync(
        BankStatementImportRequest request,
        IReadOnlyList<BankStatementSourceCache> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sources);
        var cache = sources
            .GroupBy(item => Path.GetFullPath(item.OriginalPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        return Task.Run(() => Import(request, cancellationToken, cache), cancellationToken);
    }

    private BankStatementSourceCache ExtractSource(
        string sourcePath,
        string identityPath,
        string displayName,
        string fileHash,
        string password,
        bool recoveryMode,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("The Bank Statement source no longer exists.", sourcePath);
        var info = new FileInfo(sourcePath);
        var sourceInspection = SpreadsheetSourceInspector.Inspect(sourcePath);
        if (sourceInspection.Kind == SpreadsheetSourceKind.Empty)
            throw new InvalidDataException("The selected spreadsheet file is empty.");

        string? convertedPath = null;
        var path = sourcePath;
        var resolvedPassword = password ?? string.Empty;
        try
        {
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (sourceInspection.Kind is SpreadsheetSourceKind.LegacyOleWorkbook or SpreadsheetSourceKind.XmlSpreadsheet ||
                (extension == ".xls" && sourceInspection.Kind == SpreadsheetSourceKind.UnknownBinary))
            {
                convertedPath = ConvertExcelSourceWithPasswordDiscovery(
                    sourcePath, resolvedPassword, recoveryMode,
                    SpreadsheetSourceInspector.IsEncryptedOpenXmlContainer(sourcePath),
                    out resolvedPassword);
                path = convertedPath;
            }

            OpenXmlWorkbookData workbook;
            try
            {
                workbook = _workbookReader.Read(path, cancellationToken);
            }
            catch when (Path.GetExtension(sourcePath).ToLowerInvariant() is ".xlsx" or ".xlsm" or ".xltx" or ".xltm")
            {
                convertedPath = ConvertExcelSourceWithPasswordDiscovery(
                    sourcePath, resolvedPassword, recoveryMode, sourceLooksEncryptedOpenXml: true,
                    out resolvedPassword);
                workbook = _workbookReader.Read(convertedPath, cancellationToken);
            }

            watch.Stop();
            return new BankStatementSourceCache
            {
                OriginalPath = Path.GetFullPath(sourcePath),
                IdentityPath = string.IsNullOrWhiteSpace(identityPath) ? Path.GetFullPath(sourcePath) : Path.GetFullPath(identityPath),
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(sourcePath) : displayName,
                // Cache-first Auto Import never re-opens the source merely to hash it.
                // The deterministic workbook-content hash is calculated from the RAM snapshot
                // after the source-access timer has stopped and every workbook handle is closed.
                FileHash = string.IsNullOrWhiteSpace(fileHash) ? ComputeWorkbookCacheHash(workbook) : fileHash,
                FileSize = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                SourceAccessMilliseconds = watch.Elapsed.TotalMilliseconds,
                ResolvedPassword = resolvedPassword,
                Workbook = workbook
            };
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(convertedPath))
            {
                try { File.Delete(convertedPath); } catch { }
            }
        }
    }

    public Task<BankStatementImportResult> ImportAsync(BankStatementImportRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() => Import(request, cancellationToken, null), cancellationToken);

    public Task<int> RepairPersistedBalanceEvidenceAsync(
        long clientId,
        string financialYear,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => RepairPersistedBalanceEvidence(clientId, financialYear, cancellationToken), cancellationToken);

    private int RepairPersistedBalanceEvidence(long clientId, string financialYear, CancellationToken cancellationToken)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear)) return 0;
        repository.Initialize();

        var statements = repository.GetStatements(clientId, financialYear);
        var transactions = repository.GetTransactions(clientId, financialYear, includeArchived: true);
        var accountsById = repository.GetAccounts(clientId, financialYear).ToDictionary(account => account.Id);

        // BR8 first repairs the statement registry itself. Historical overlap/delta paths
        // could persist valid BankTransactions from a physical workbook without preserving
        // a matching BankStatements row, which made Statement Metadata omit the file and made
        // whole-account totals/coverage ignore real imported transactions.
        var repaired = RepairMissingStatementRegistryEntries(
            clientId,
            financialYear,
            statements,
            transactions,
            cancellationToken);
        if (repaired > 0)
        {
            statements = repository.GetStatements(clientId, financialYear);
            transactions = repository.GetTransactions(clientId, financialYear, includeArchived: true);
        }
        if (statements.Count == 0) return repaired;

        var sourceCache = new Dictionary<string, BankStatementSourceCache>(StringComparer.OrdinalIgnoreCase);
        var rawSourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var statement in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(statement.SourcePath) || !File.Exists(statement.SourcePath))
                continue;

            var statementRows = BankBalanceAuthority.GetStatementSourceTransactions(statement, transactions);
            if (statementRows.Count == 0) continue;

            var hasIndependentRunningBalance = statementRows.Any(BankBalanceAuthority.IsIndependentRunningBalanceEvidence);
            var hasTrustedOpening = statement.OpeningBalance.HasValue &&
                                    (statement.OpeningBalanceSource.Equals("SOURCE", StringComparison.OrdinalIgnoreCase) ||
                                     statement.OpeningBalanceSource.Equals("MANUAL", StringComparison.OrdinalIgnoreCase) ||
                                     statement.OpeningBalanceSource.Equals("RUNNING BALANCE", StringComparison.OrdinalIgnoreCase));
            var hasTrustedClosing = statement.ClosingBalance.HasValue &&
                                    (statement.ClosingBalanceSource.Equals("SOURCE", StringComparison.OrdinalIgnoreCase) ||
                                     statement.ClosingBalanceSource.Equals("MANUAL", StringComparison.OrdinalIgnoreCase) ||
                                     statement.ClosingBalanceSource.Equals("RUNNING BALANCE", StringComparison.OrdinalIgnoreCase));
            var needsSourceIntegrityRepair = Math.Abs(statement.Difference) > 0.01m ||
                                             statement.ReconciliationStatus.Contains("Difference", StringComparison.OrdinalIgnoreCase) ||
                                             statement.ReconciliationStatus.Contains("Missing Period", StringComparison.OrdinalIgnoreCase) ||
                                             statement.ReconciliationStatus.Contains("Running Balance", StringComparison.OrdinalIgnoreCase);
            if (hasIndependentRunningBalance && hasTrustedOpening && hasTrustedClosing &&
                statement.PeriodFrom.HasValue && statement.PeriodTo.HasValue && !needsSourceIntegrityRepair)
                continue;

            try
            {
                var fullPath = Path.GetFullPath(statement.SourcePath);
                if (!sourceCache.TryGetValue(fullPath, out var source))
                {
                    source = ExtractSource(
                        fullPath,
                        fullPath,
                        string.IsNullOrWhiteSpace(statement.SourceFile) ? Path.GetFileName(fullPath) : statement.SourceFile,
                        string.Empty,
                        string.Empty,
                        recoveryMode: true,
                        cancellationToken);
                    sourceCache[fullPath] = source;
                    rawSourceHashes[fullPath] = ComputeHash(fullPath);
                }

                if (!string.IsNullOrWhiteSpace(statement.FileHash))
                {
                    rawSourceHashes.TryGetValue(fullPath, out var rawHash);
                    var sourceMatchesImportedHash =
                        statement.FileHash.Equals(source.FileHash, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(rawHash) && statement.FileHash.Equals(rawHash, StringComparison.OrdinalIgnoreCase));
                    if (!sourceMatchesImportedHash) continue;
                }

                var sheets = ConvertWorkbookData(source.Workbook);
                var analyses = sheets
                    .Select(sheet => AnalyzeSheet(sheet))
                    .Where(item => item.HeaderRow > 0)
                    .OrderByDescending(item => item.Sheet.Name.Equals(statement.SourceSheet, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(item => item.Score)
                    .ToArray();
                var candidate = analyses.FirstOrDefault();
                if (candidate is null) continue;

                var metadata = DetectMetadata(sheets, candidate, statement.SourceFile);
                var parsedRows = ParseTransactions(candidate, metadata, statement.SourceFile, cancellationToken)
                    .Where(item => IsInsideFinancialYear(item.TransactionDate, financialYear))
                    .ToList();
                if (parsedRows.Count == 0) continue;

                // CR3 source-integrity repair. A legacy fingerprint could collapse two genuine
                // same-date/same-narration/same-amount Bank rows. When this statement is already
                // in Difference/Missing-Period review and the original source hash still matches,
                // compare the source against persisted canonical identities and restore only rows
                // that are genuinely absent. Existing/archived rows are never duplicated.
                accountsById.TryGetValue(statement.BankAccountId, out var sourceAccount);
                var fingerprintBankMetadata = sourceAccount?.DisplayName ?? string.Empty;
                var persistedIdentities = transactions
                    .Where(row => row.BankAccountId == statement.BankAccountId)
                    .Select(row => BuildFingerprint(row, fingerprintBankMetadata.Length > 0 ? fingerprintBankMetadata : row.BankAccountName))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missingSourceRows = new List<BankTransactionRecord>();
                foreach (var parsed in parsedRows)
                {
                    parsed.ClientId = clientId;
                    parsed.FinancialYear = financialYear;
                    parsed.BankAccountId = statement.BankAccountId;
                    parsed.BankAccountName = fingerprintBankMetadata;
                    parsed.StatementId = statement.Id;
                    parsed.Fingerprint = BuildFingerprint(parsed, fingerprintBankMetadata);
                    if (!persistedIdentities.Add(parsed.Fingerprint)) continue;
                    parsed.TransactionId = $"BTX-{parsed.TransactionDate:yyyyMMdd}-{ShortHash(parsed.Fingerprint)}";
                    parsed.ImportedUtc = DateTime.UtcNow;
                    missingSourceRows.Add(parsed);
                }
                if (missingSourceRows.Count > 0)
                {
                    var inserted = repository.InsertTransactions(missingSourceRows);
                    if (inserted > 0)
                    {
                        if (missingSourceRows.Any(row => row.Mode.Equals("Contra", StringComparison.OrdinalIgnoreCase)))
                        {
                            masterRepository.Upsert(clientId, financialYear,
                            [
                                new MasterRecord { ClientId=clientId,FinancialYear=financialYear,Name="Cash",Parent="Cash-in-Hand",Type="Ledger",Source="Bank Statement Contra" }
                            ]);
                        }
                        transactions = repository.GetTransactions(clientId, financialYear, includeArchived: true);
                    }
                }

                var runningBalances = parsedRows
                    .Where(BankBalanceAuthority.IsIndependentRunningBalanceEvidence)
                    .GroupBy(item => item.SourceRow)
                    .ToDictionary(group => group.Key, group => group.Last().RunningBalance!.Value);
                var coverage = ResolveDetectedCoverage(metadata, parsedRows, financialYear);

                var balanceEvidenceRows = parsedRows
                    .Where(item => item.TransactionDate.Date >= coverage.From.Date && item.TransactionDate.Date <= coverage.To.Date)
                    .ToList();
                if (balanceEvidenceRows.Count == 0) balanceEvidenceRows = parsedRows;

                var sourceOpening = coverage.MetadataBalancesTrusted ? metadata.OpeningBalance : null;
                var runningOpening = BankBalanceAuthority.DeriveOpeningFromRunningBalance(balanceEvidenceRows);
                var opening = sourceOpening ?? runningOpening;
                var openingSource = sourceOpening.HasValue
                    ? "SOURCE"
                    : runningOpening.HasValue ? "RUNNING BALANCE" : string.Empty;

                var orderedBalanceEvidenceRows = BankBalanceAuthority.OrderTransactionsChronologically(balanceEvidenceRows);
                var finalBalanceEvidenceRow = orderedBalanceEvidenceRows.LastOrDefault();
                var runningClosing = finalBalanceEvidenceRow is not null && BankBalanceAuthority.IsIndependentRunningBalanceEvidence(finalBalanceEvidenceRow)
                    ? finalBalanceEvidenceRow.RunningBalance
                    : null;
                var sourceClosing = coverage.MetadataBalancesTrusted ? metadata.ClosingBalance : null;
                var closing = sourceClosing ?? runningClosing;
                var closingSource = sourceClosing.HasValue
                    ? "SOURCE"
                    : runningClosing.HasValue ? "RUNNING BALANCE" : string.Empty;

                var isDelta = BankBalanceAuthority.IsBalanceChainExcludedStatement(statement);
                repository.RepairStatementBalanceEvidence(
                    statement.Id,
                    isDelta ? statement.PeriodFrom : coverage.From,
                    isDelta ? statement.PeriodTo : coverage.To,
                    isDelta ? null : opening,
                    isDelta ? string.Empty : openingSource,
                    isDelta ? null : sourceOpening,
                    isDelta ? null : sourceClosing,
                    isDelta ? null : closing,
                    isDelta ? string.Empty : closingSource,
                    statement.SourceFile,
                    statement.SourceSheet,
                    runningBalances);
                repaired++;
            }
            catch
            {
                // Evidence repair is best-effort and never blocks normal Banking refresh.
                // The original statement/accounting rows remain untouched when a source
                // file cannot be reopened or parsed.
            }
        }

        return repaired;
    }

    private int RepairMissingStatementRegistryEntries(
        long clientId,
        string financialYear,
        IReadOnlyList<BankStatementRecord> statements,
        IReadOnlyList<BankTransactionRecord> transactions,
        CancellationToken cancellationToken)
    {
        var recovered = 0;
        var sourceGroups = transactions
            .Where(BankBalanceAuthority.IsSourceStatementTransaction)
            .Where(row => row.BankAccountId > 0 && !string.IsNullOrWhiteSpace(row.SourceFile))
            .GroupBy(
                row => BankBalanceAuthority.SourceFileKey(row.BankAccountId, row.SourceFile, row.SourceSheet),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var group in sourceGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = group.ToArray();
            if (rows.Length == 0) continue;

            var sample = rows[0];
            var sourceFile = (sample.SourceFile ?? string.Empty).Trim();
            var sourceSheet = (sample.SourceSheet ?? string.Empty).Trim();
            var registeredStatement = statements
                .Where(statement =>
                    statement.BankAccountId == sample.BankAccountId &&
                    string.Equals((statement.SourceFile ?? string.Empty).Trim(), sourceFile, StringComparison.OrdinalIgnoreCase) &&
                    (sourceSheet.Length == 0 ||
                     string.IsNullOrWhiteSpace(statement.SourceSheet) ||
                     string.Equals((statement.SourceSheet ?? string.Empty).Trim(), sourceSheet, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(statement => statement.Id)
                .FirstOrDefault();

            // A physical SourceFile/SourceSheet group is stronger than a stale historical StatementId.
            // Physical source identity is stronger than a stale historical StatementId.
            // If the registry row exists but some rows point at a neighbouring statement,
            // relink only this exact SourceFile/SourceSheet group. This fixes per-file debit/
            // credit verification without inserting or deleting any accounting transaction.
            if (registeredStatement is not null)
            {
                if (rows.Any(row => row.StatementId != registeredStatement.Id))
                {
                    repository.RecoverStatementRegistry(registeredStatement, sourceFile, sourceSheet);
                    recovered++;
                }
                continue;
            }

            var coverage = BankBalanceAuthority.InferTransactionFileCoverage(rows);
            if (!coverage.HasValue) continue;

            var sourcePath = ResolveRecoveredSourcePath(sample.BankAccountId, sourceFile, statements);
            var importedUtc = rows
                .Select(row => row.ImportedUtc)
                .Where(value => value != default)
                .DefaultIfEmpty(DateTime.UtcNow)
                .Min();

            var recoveredStatement = new BankStatementRecord
            {
                ClientId = clientId,
                FinancialYear = financialYear,
                BankAccountId = sample.BankAccountId,
                SourceFile = sourceFile,
                SourcePath = sourcePath,
                SourceSheet = sourceSheet,
                FileHash = sourcePath.Length > 0 && File.Exists(sourcePath) ? ComputeHash(sourcePath) : string.Empty,
                PeriodFrom = coverage.Value.From,
                PeriodTo = coverage.Value.To,
                OpeningBalance = null,
                OpeningBalanceSource = string.Empty,
                SourceOpeningBalance = null,
                SourceClosingBalance = null,
                ClosingBalance = null,
                ClosingBalanceSource = string.Empty,
                TotalDebit = rows.Sum(row => row.Debit),
                TotalCredit = rows.Sum(row => row.Credit),
                TransactionCount = rows.Length,
                ReconciliationStatus = sourcePath.Length > 0
                    ? "Registry Recovered - Source Evidence Pending"
                    : "Registry Recovered - Balance Evidence Pending",
                Difference = 0m,
                ImportedUtc = importedUtc
            };

            repository.RecoverStatementRegistry(recoveredStatement, sourceFile, sourceSheet);
            recovered++;
        }

        return recovered;
    }

    private static string ResolveRecoveredSourcePath(
        long bankAccountId,
        string sourceFile,
        IReadOnlyList<BankStatementRecord> statements)
    {
        if (string.IsNullOrWhiteSpace(sourceFile)) return string.Empty;
        if (Path.IsPathRooted(sourceFile) && File.Exists(sourceFile))
            return Path.GetFullPath(sourceFile);

        foreach (var directory in statements
                     .Where(statement => statement.BankAccountId == bankAccountId)
                     .Select(statement => Path.GetDirectoryName(statement.SourcePath))
                     .Where(directory => !string.IsNullOrWhiteSpace(directory))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var candidate = Path.Combine(directory!, sourceFile);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch
            {
                // Keep registry recovery non-destructive when an old path is malformed.
            }
        }

        return string.Empty;
    }

    private BankStatementImportResult Import(
        BankStatementImportRequest request,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, BankStatementSourceCache>? sourceCache)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ClientId <= 0) throw new InvalidOperationException("Open a client before importing Bank Statements.");
        if (request.FilePaths.Count == 0) throw new InvalidOperationException("No Bank Statement file was selected.");

        repository.Initialize();
        if (request.ClearExisting) repository.ClearClientYear(request.ClientId, request.FinancialYear);

        var result = new BankStatementImportResult { FilesSelected = request.FilePaths.Count };
        var existingAccountRecords = repository.GetAccounts(request.ClientId, request.FinancialYear).ToList();
        var existingAccounts = existingAccountRecords.Count;
        // Cache-first Auto Import takes one authoritative DB snapshot before workbook
        // processing. No per-file Statement/Transaction re-query is needed while the
        // source caches are being finalized.
        var existingStatements = sourceCache is null
            ? null
            : repository.GetStatements(request.ClientId, request.FinancialYear).ToList();
        // CR3 transaction identity authority: keep one transaction snapshot for both manual
        // and cache-first imports. Rebuild the current canonical fingerprint from the persisted
        // transaction fields so rows imported by the legacy Date/Narration/Amount fingerprint
        // remain duplicate-safe after the stronger Reference/Running-Balance identity is enabled.
        var existingTransactions = repository.GetTransactions(request.ClientId, request.FinancialYear, includeArchived: true).ToList();
        var accountDisplayNames = existingAccountRecords.ToDictionary(account => account.Id, account => account.DisplayName);
        var fingerprintCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var autoImportFallbackCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        string ResolveFingerprintBankMetadata(BankTransactionRecord row) =>
            accountDisplayNames.TryGetValue(row.BankAccountId, out var displayName) && !string.IsNullOrWhiteSpace(displayName)
                ? displayName
                : row.BankAccountName;

        void AddFingerprint(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) return;
            fingerprintCounts.TryGetValue(fingerprint, out var count);
            fingerprintCounts[fingerprint] = count + 1;
        }

        void RemoveFingerprint(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint) || !fingerprintCounts.TryGetValue(fingerprint, out var count)) return;
            if (count <= 1) fingerprintCounts.Remove(fingerprint);
            else fingerprintCounts[fingerprint] = count - 1;
        }

        bool TryReserveFingerprint(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) return false;
            if (fingerprintCounts.TryGetValue(fingerprint, out var count) && count > 0) return false;
            fingerprintCounts[fingerprint] = 1;
            return true;
        }

        void AddAutoImportFallback(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity)) return;
            autoImportFallbackCounts.TryGetValue(identity, out var count);
            autoImportFallbackCounts[identity] = count + 1;
        }

        void RemoveAutoImportFallback(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity) || !autoImportFallbackCounts.TryGetValue(identity, out var count)) return;
            if (count <= 1) autoImportFallbackCounts.Remove(identity);
            else autoImportFallbackCounts[identity] = count - 1;
        }

        bool TryReserveAutoImportFallback(string identity)
        {
            if (!request.AutoImportTransactionDuplicateFallback) return true;
            if (string.IsNullOrWhiteSpace(identity)) return true;
            if (autoImportFallbackCounts.TryGetValue(identity, out var count) && count > 0) return false;
            autoImportFallbackCounts[identity] = 1;
            return true;
        }

        foreach (var existingRow in existingTransactions)
        {
            foreach (var alias in BuildFingerprintAliases(existingRow, ResolveFingerprintBankMetadata(existingRow)))
                AddFingerprint(alias);
            if (request.AutoImportTransactionDuplicateFallback)
                AddAutoImportFallback(BuildAutoImportTransactionIdentity(existingRow));
        }
        var cachedPrepared = new List<PreparedCachedStatement>();

        foreach (var originalPath in request.FilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var headerMappingScope = ImportHeaderMappingRuntime.BeginSource(originalPath);
            var fileWatch = Stopwatch.StartNew();
            var processWatch = new Stopwatch();
            var openSeconds = 0d;
            var displayFile = request.SourceDisplayNames.TryGetValue(originalPath, out var sourceDisplayName) && !string.IsNullOrWhiteSpace(sourceDisplayName)
                ? sourceDisplayName
                : Path.GetFileName(originalPath);
            var identityPath = request.SourceIdentityPaths.TryGetValue(originalPath, out var sourceIdentityPath) && !string.IsNullOrWhiteSpace(sourceIdentityPath)
                ? sourceIdentityPath
                : originalPath;
            var importedBefore = result.FilesImported;
            var filePreparedForCachedCommit = false;
            try
            {
                BankStatementSourceCache? cachedSource = null;
                if (sourceCache is not null)
                {
                    try { sourceCache.TryGetValue(Path.GetFullPath(originalPath), out cachedSource); }
                    catch { cachedSource = null; }
                }
                if (cachedSource is null && !File.Exists(originalPath))
                {
                    result.Issues.Add(BankStatementImportIssue.Create(displayFile, "Workbook Open", BankImportErrorCode.E001_WorkbookOpenFailure, "The selected source file no longer exists.", request.Attempt));
                    continue;
                }
                var hash = cachedSource?.FileHash ?? ComputeHash(File.Exists(identityPath) ? identityPath : originalPath);
                var hashAlreadyAuthoritative = cachedSource is not null
                    ? existingStatements!.Any(statement => statement.FileHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    : repository.StatementHashExists(request.ClientId, request.FinancialYear, hash);
                if (hashAlreadyAuthoritative && !request.ForceSourceReplacement)
                {
                    var duplicateOf = (existingStatements ?? repository.GetStatements(request.ClientId, request.FinancialYear).ToList())
                        .Where(statement => statement.FileHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(statement => statement.ImportedUtc)
                        .ThenByDescending(statement => statement.Id)
                        .FirstOrDefault();
                    SaveImportAttempt(repository, request, displayFile, identityPath, string.Empty, hash, duplicateOf, "DUPLICATE",
                        "Exact file hash already imported. Accounting transactions were not inserted again.");
                    result.DuplicateStatementsSkipped++;
                    continue;
                }

                string? convertedPath = null;
                try
                {
                    IReadOnlyList<SheetData> sheets;
                    if (cachedSource is not null)
                    {
                        // BR4 cache-first authority: source file handles are already closed.
                        // Everything below operates on the immutable RAM workbook snapshot.
                        sheets = ConvertWorkbookData(cachedSource.Workbook);
                        openSeconds = cachedSource.SourceAccessMilliseconds / 1000d;
                    }
                    else
                    {
                        var extension = Path.GetExtension(originalPath).ToLowerInvariant();
                        var path = originalPath;
                        var password = request.Passwords.TryGetValue(originalPath, out var suppliedPassword) ? suppliedPassword : string.Empty;
                        var sourceInspection = SpreadsheetSourceInspector.Inspect(originalPath);
                        if (sourceInspection.Kind == SpreadsheetSourceKind.Empty)
                            throw new InvalidDataException("The selected spreadsheet file is empty.");

                        // Manual/direct imports keep the established compatibility path.
                        if (sourceInspection.Kind == SpreadsheetSourceKind.DelimitedText)
                            extension = ".txt";
                        else if (sourceInspection.Kind == SpreadsheetSourceKind.HtmlTable)
                            extension = ".html";
                        else if (sourceInspection.Kind == SpreadsheetSourceKind.OpenXmlZip)
                            extension = ".xlsx";
                        else if ((sourceInspection.Kind is SpreadsheetSourceKind.LegacyOleWorkbook or SpreadsheetSourceKind.XmlSpreadsheet) ||
                                 (extension == ".xls" && sourceInspection.Kind == SpreadsheetSourceKind.UnknownBinary))
                        {
                            var sourceLooksEncryptedOpenXml = SpreadsheetSourceInspector.IsEncryptedOpenXmlContainer(originalPath);
                            convertedPath = ConvertExcelSourceWithPasswordDiscovery(
                                originalPath, password, request.RecoveryMode, sourceLooksEncryptedOpenXml, out var resolvedPassword);
                            password = resolvedPassword;
                            path = convertedPath;
                            extension = ".xlsx";
                        }

                        try
                        {
                            sheets = extension is ".csv" or ".txt" or ".tsv"
                                ? new[] { ReadCsv(path) }
                                : ReadWorkbookWithRecovery(path, originalPath, password, request.RecoveryMode, cancellationToken, ref convertedPath);
                        }
                        catch (Exception exception)
                        {
                            if (extension is ".xlsx" or ".xlsm" or ".xltx" or ".xltm")
                            {
                                try
                                {
                                    convertedPath = ConvertExcelSourceWithPasswordDiscovery(
                                        originalPath, password, request.RecoveryMode, sourceLooksEncryptedOpenXml: true, out var recoveredPassword);
                                    password = recoveredPassword;
                                    path = convertedPath;
                                    sheets = ReadWorkbookWithRecovery(path, originalPath, password, request.RecoveryMode, cancellationToken, ref convertedPath);
                                }
                                catch (Exception recoveryException)
                                {
                                    var code = ClassifyOpenFailure(extension, recoveryException, password, request.RecoveryMode);
                                    result.Issues.Add(BankStatementImportIssue.Create(displayFile, request.RecoveryMode ? "Recovery" : "Workbook Open", code, recoveryException.Message, request.Attempt));
                                    continue;
                                }
                            }
                            else
                            {
                                var code = ClassifyOpenFailure(extension, exception, password, request.RecoveryMode);
                                result.Issues.Add(BankStatementImportIssue.Create(displayFile, request.RecoveryMode ? "Recovery" : "Workbook Open", code, exception.Message, request.Attempt));
                                continue;
                            }
                        }
                        openSeconds = fileWatch.Elapsed.TotalSeconds;
                    }
                    processWatch.Start();

                    var candidate = sheets
                        .Select(sheet => AnalyzeSheet(sheet))
                        .Where(item => item.HeaderRow > 0)
                        .OrderByDescending(item => item.Score)
                        .FirstOrDefault();

                    if (candidate is null)
                    {
                        var diagnosis = DiagnoseHeaderFailure(sheets);
                        result.Issues.Add(BankStatementImportIssue.Create(displayFile, diagnosis.Stage, diagnosis.Code, diagnosis.Message, request.Attempt));
                        continue;
                    }

                    var metadata = DetectMetadata(sheets, candidate, displayFile);
                    ApplyTrustedMetadataFallback(metadata, request, originalPath);
                    if (request.StrictMetadata &&
                        (string.IsNullOrWhiteSpace(metadata.BankName) ||
                         metadata.BankName.Equals("Unknown Bank", StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Issues.Add(BankStatementImportIssue.Create(displayFile, "Bank Detection", BankImportErrorCode.E010_BankDetectionFailure, "A reliable bank name or IFSC bank prefix was not detected. The file was not imported with a guessed bank.", request.Attempt));
                        continue;
                    }
                    if (request.StrictMetadata &&
                        (string.IsNullOrWhiteSpace(metadata.AccountNumber) ||
                         metadata.AccountNumber.Equals("Unknown", StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Issues.Add(BankStatementImportIssue.Create(displayFile, "Account Detection", BankImportErrorCode.E011_AccountDetectionFailure, "A reliable account number was not detected. The file was not imported with a blank/file-based account identifier.", request.Attempt));
                        continue;
                    }
                    if (candidate.BalanceColumn <= 0 && !metadata.OpeningBalance.HasValue && !metadata.ClosingBalance.HasValue)
                    {
                        result.Issues.Add(BankStatementImportIssue.Create(displayFile, "Balance Detection", BankImportErrorCode.E009_BalanceDetectionFailure, "No running, opening, or closing balance evidence was detected for verification.", request.Attempt));
                        continue;
                    }
                    var allParsedRows = ParseTransactions(candidate, metadata, displayFile, cancellationToken).ToList();
                    var parsedRows = allParsedRows.Where(item => IsInsideFinancialYear(item.TransactionDate, request.FinancialYear)).ToList();
                    result.RowsOutsideFinancialYearSkipped += allParsedRows.Count - parsedRows.Count;
                    if (parsedRows.Count == 0)
                    {
                        result.Issues.Add(BankStatementImportIssue.Create(displayFile, "Transaction Sheet", BankImportErrorCode.E004_TransactionSheetNotFound, "The detected table contained no valid transaction rows.", request.Attempt));
                        continue;
                    }

                    if (IsExcelStatementSource(originalPath))
                    {
                        var usableNarrations = parsedRows.Count(row => IsUsableNarration(row.Narration));
                        var minimumUsable = parsedRows.Count >= 10 ? Math.Max(2, (int)Math.Ceiling(parsedRows.Count * 0.20m)) : 1;
                        if (usableNarrations < minimumUsable)
                        {
                            result.Issues.Add(BankStatementImportIssue.Create(
                                displayFile,
                                "Narration Detection",
                                BankImportErrorCode.E005_HeaderDetectionFailure,
                                $"Excel statement narration validation failed: only {usableNarrations:N0} of {parsedRows.Count:N0} transaction rows contained usable narration after role-based column analysis. The statement was not imported with placeholder or mis-mapped narration.",
                                request.Attempt));
                            continue;
                        }
                    }

                    // BR4 cache-first path does all expensive parsing/normalization above this
                    // point before any authoritative Bank write. For normal/manual imports the
                    // established repository path remains available.
                    var detectedAccount = new BankAccountRecord
                    {
                        ClientId = request.ClientId,
                        FinancialYear = request.FinancialYear,
                        BankName = metadata.BankName,
                        AccountNumber = metadata.AccountNumber,
                        AccountType = metadata.AccountType
                    };
                    // Fix22: the complete account number is the physical-account authority.
                    // Bank display wording and SB/CA/OD metadata may vary between statement
                    // exports and must never split one real account into two balance chains.
                    var bankAccount = existingAccountRecords.FirstOrDefault(account =>
                        BankAccountIdentity.Same(account, detectedAccount));
                    if (bankAccount is null)
                    {
                        bankAccount = new BankAccountRecord
                        {
                            ClientId = request.ClientId,
                            FinancialYear = request.FinancialYear,
                            BankName = metadata.BankName,
                            AccountNumber = metadata.AccountNumber,
                            AccountType = metadata.AccountType,
                            BranchName = metadata.BranchName,
                            IfscCode = metadata.IfscCode,
                            OdLimit = metadata.OdLimit,
                            DrawingPower = metadata.DrawingPower,
                            LedgerName = BuildBankLedgerName(metadata)
                        };
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(bankAccount.BankName) && !string.IsNullOrWhiteSpace(metadata.BankName))
                            bankAccount.BankName = metadata.BankName;
                        if (string.IsNullOrWhiteSpace(bankAccount.AccountNumber) && !string.IsNullOrWhiteSpace(metadata.AccountNumber))
                            bankAccount.AccountNumber = metadata.AccountNumber;
                        if (string.IsNullOrWhiteSpace(bankAccount.AccountType) && !string.IsNullOrWhiteSpace(metadata.AccountType))
                            bankAccount.AccountType = metadata.AccountType;
                        if (!string.IsNullOrWhiteSpace(metadata.BranchName)) bankAccount.BranchName = metadata.BranchName;
                        if (!string.IsNullOrWhiteSpace(metadata.IfscCode)) bankAccount.IfscCode = metadata.IfscCode;
                        bankAccount.OdLimit ??= metadata.OdLimit;
                        bankAccount.DrawingPower ??= metadata.DrawingPower;
                        if (string.IsNullOrWhiteSpace(bankAccount.LedgerName)) bankAccount.LedgerName = BuildBankLedgerName(metadata);
                    }

                    var detectedCoverage = ResolveDetectedCoverage(metadata, parsedRows, request.FinancialYear);
                    var detectedFrom = detectedCoverage.From;
                    var detectedTo = detectedCoverage.To;
                    var metadataBalancesTrusted = detectedCoverage.MetadataBalancesTrusted;
                    var sameSourceStatements = cachedSource is not null
                        ? existingStatements!
                            .Where(statement => SameSourcePath(statement.SourcePath, identityPath))
                            .OrderByDescending(statement => statement.ImportedUtc)
                            .ThenByDescending(statement => statement.Id)
                            .ToList()
                        : bankAccount.Id <= 0
                            ? new List<BankStatementRecord>()
                            : repository.GetStatements(request.ClientId, request.FinancialYear)
                                .Where(statement => statement.BankAccountId == bankAccount.Id && SameSourcePath(statement.SourcePath, identityPath))
                                .OrderByDescending(statement => statement.ImportedUtc)
                                .ThenByDescending(statement => statement.Id)
                                .ToList();
                    var existingSourceStatement = sameSourceStatements.FirstOrDefault();

                    // Whole-file replacement is source-path authoritative even if the changed
                    // workbook is now detected as another Bank/account. Remove only the prior
                    // source contribution from the in-memory duplicate authority; SQLite is
                    // untouched until the finalized bank cache is committed.
                    if (cachedSource is not null && sameSourceStatements.Count > 0 && !request.AutoImportTransactionDuplicateFallback)
                    {
                        var oldStatementIds = sameSourceStatements.Select(statement => statement.Id).ToHashSet();
                        foreach (var oldRow in existingTransactions.Where(row => oldStatementIds.Contains(row.StatementId)))
                        {
                            foreach (var alias in BuildFingerprintAliases(oldRow, ResolveFingerprintBankMetadata(oldRow)))
                                RemoveFingerprint(alias);
                        }
                    }

                    var periodAlreadyAuthoritative = cachedSource is not null
                        ? (bankAccount.Id > 0 && existingStatements!.Any(statement =>
                               statement.BankAccountId == bankAccount.Id &&
                               !SameSourcePath(statement.SourcePath, identityPath) &&
                               NullableDateEqual(statement.PeriodFrom, detectedFrom) &&
                               NullableDateEqual(statement.PeriodTo, detectedTo))) ||
                          cachedPrepared.Any(prepared =>
                               SameAccount(prepared.Account, bankAccount) &&
                               !SameSourcePath(prepared.SourcePath, identityPath) &&
                               NullableDateEqual(prepared.Statement.PeriodFrom, detectedFrom) &&
                               NullableDateEqual(prepared.Statement.PeriodTo, detectedTo))
                        : bankAccount.Id > 0 &&
                          repository.StatementPeriodExists(request.ClientId, request.FinancialYear, bankAccount.Id, detectedFrom, detectedTo) &&
                          existingSourceStatement is null;
                    if (periodAlreadyAuthoritative && existingSourceStatement is null && !request.AutoImportTransactionDuplicateFallback)
                    {
                        var duplicateOf = (existingStatements ?? repository.GetStatements(request.ClientId, request.FinancialYear).ToList())
                            .Where(existing => existing.BankAccountId == bankAccount.Id &&
                                               NullableDateEqual(existing.PeriodFrom, detectedFrom) &&
                                               NullableDateEqual(existing.PeriodTo, detectedTo))
                            .OrderByDescending(existing => existing.ImportedUtc)
                            .ThenByDescending(existing => existing.Id)
                            .FirstOrDefault();
                        SaveImportAttempt(repository, request, displayFile, identityPath, candidate.Sheet.Name, hash, duplicateOf, "DUPLICATE",
                            "Statement period already exists for this Bank account. Duplicate source retained in Statement Metadata; accounting rows were not inserted again.",
                            detectedFrom, detectedTo, metadata.OpeningBalance, metadata.ClosingBalance, parsedRows.Sum(row => row.Debit), parsedRows.Sum(row => row.Credit), parsedRows.Count);
                        result.DuplicateStatementsSkipped++;
                        continue;
                    }

                    var duplicatesBeforeFile = result.DuplicateTransactionsSkipped;
                    var distinctRows = new List<BankTransactionRecord>();
                    foreach (var parsed in parsedRows)
                    {
                        parsed.ClientId = request.ClientId;
                        parsed.FinancialYear = request.FinancialYear;
                        parsed.BankAccountId = bankAccount.Id;
                        var autoImportIdentity = BuildAutoImportTransactionIdentity(parsed);
                        if (!TryReserveAutoImportFallback(autoImportIdentity))
                        {
                            result.DuplicateTransactionsSkipped++;
                            continue;
                        }
                        parsed.Fingerprint = BuildFingerprint(parsed, bankAccount.DisplayName);
                        if (!TryReserveFingerprint(parsed.Fingerprint))
                        {
                            if (request.AutoImportTransactionDuplicateFallback)
                                RemoveAutoImportFallback(autoImportIdentity);
                            result.DuplicateTransactionsSkipped++;
                            continue;
                        }
                        parsed.TransactionId = $"BTX-{parsed.TransactionDate:yyyyMMdd}-{ShortHash(parsed.Fingerprint)}";
                        distinctRows.Add(parsed);
                    }

                    if (distinctRows.Count == 0)
                    {
                        var duplicateOf = existingSourceStatement ?? (existingStatements ?? repository.GetStatements(request.ClientId, request.FinancialYear).ToList())
                            .Where(existing => existing.BankAccountId == bankAccount.Id)
                            .OrderByDescending(existing => existing.ImportedUtc)
                            .FirstOrDefault();
                        SaveImportAttempt(repository, request, displayFile, identityPath, candidate.Sheet.Name, hash, duplicateOf, "DUPLICATE",
                            "All parsed rows were already present. Duplicate file is retained in Statement Metadata; no accounting transaction was inserted twice.",
                            detectedFrom, detectedTo, metadata.OpeningBalance, metadata.ClosingBalance, parsedRows.Sum(row => row.Debit), parsedRows.Sum(row => row.Credit), parsedRows.Count);
                        result.DuplicateStatementsSkipped++;
                        continue;
                    }
                    var periodFrom = distinctRows.Min(item => item.TransactionDate);
                    var periodTo = distinctRows.Max(item => item.TransactionDate);
                    var totalDebit = distinctRows.Sum(item => item.Debit);
                    var totalCredit = distinctRows.Sum(item => item.Credit);

                    var balanceEvidenceRows = parsedRows
                        .Where(item => item.TransactionDate.Date >= detectedFrom.Date && item.TransactionDate.Date <= detectedTo.Date)
                        .ToList();
                    if (balanceEvidenceRows.Count == 0) balanceEvidenceRows = parsedRows;

                    var sourceOpening = metadataBalancesTrusted ? metadata.OpeningBalance : null;
                    var runningOpening = BankBalanceAuthority.DeriveOpeningFromRunningBalance(balanceEvidenceRows);
                    var opening = sourceOpening ?? runningOpening;
                    var openingSource = sourceOpening.HasValue
                        ? "SOURCE"
                        : runningOpening.HasValue ? "RUNNING BALANCE" : string.Empty;

                    var orderedBalanceEvidenceRows = BankBalanceAuthority.OrderTransactionsChronologically(balanceEvidenceRows);
                    var finalBalanceEvidenceRow = orderedBalanceEvidenceRows.LastOrDefault();
                    var runningClosing = finalBalanceEvidenceRow is not null && BankBalanceAuthority.IsIndependentRunningBalanceEvidence(finalBalanceEvidenceRow)
                        ? finalBalanceEvidenceRow.RunningBalance
                        : null;
                    // SourceClosingBalance means an explicit statement-level closing balance only.
                    // A transaction running balance is independent evidence, but it must keep its
                    // RUNNING BALANCE provenance instead of being written into the SOURCE field.
                    var sourceClosing = metadataBalancesTrusted ? metadata.ClosingBalance : null;
                    var independentClosing = sourceClosing ?? runningClosing;
                    var closingSource = sourceClosing.HasValue
                        ? "SOURCE"
                        : runningClosing.HasValue ? "RUNNING BALANCE" : string.Empty;
                    var calculatedClosing = opening.HasValue
                        ? BankBalanceAuthority.CalculateClosingBalance(opening.Value, totalDebit, totalCredit)
                        : (decimal?)null;
                    var closing = independentClosing ?? calculatedClosing;
                    var difference = independentClosing.HasValue && calculatedClosing.HasValue
                        ? Math.Round(independentClosing.Value - calculatedClosing.Value, 2, MidpointRounding.AwayFromZero)
                        : 0m;
                    var fileHadOverlappingRows = result.DuplicateTransactionsSkipped > duplicatesBeforeFile;
                    var isSourceDelta = fileHadOverlappingRows &&
                                        ((cachedSource is null && request.AllowSourceDelta && existingSourceStatement is not null) ||
                                         request.AutoImportTransactionDuplicateFallback);
                    var status = isSourceDelta
                        ? "Delta Imported"
                        : fileHadOverlappingRows
                            ? "Overlap Review Required"
                            : opening.HasValue && closing.HasValue && independentClosing.HasValue
                                ? (Math.Round(difference, 2, MidpointRounding.AwayFromZero) == 0m ? "Pending Transaction Balance Verification" : "Difference")
                                : "Balance Review Required";

                    var statementPeriodFrom = isSourceDelta ? periodFrom : detectedFrom;
                    var statementPeriodTo = isSourceDelta ? periodTo : detectedTo;
                    var statementOpening = isSourceDelta ? null : opening;
                    var statementClosing = isSourceDelta ? null : closing;
                    var statementDifference = isSourceDelta ? 0m : difference;
                    var statement = new BankStatementRecord
                    {
                        ClientId=request.ClientId,FinancialYear=request.FinancialYear,BankAccountId=bankAccount.Id,SourceFile=displayFile,SourcePath=identityPath,
                        SourceSheet=candidate.Sheet.Name,FileHash=hash,PeriodFrom=statementPeriodFrom,PeriodTo=statementPeriodTo,
                        OpeningBalance=statementOpening,OpeningBalanceSource=statementOpening.HasValue ? openingSource : string.Empty,
                        SourceOpeningBalance=isSourceDelta ? null : sourceOpening,SourceClosingBalance=isSourceDelta ? null : sourceClosing,ClosingBalanceSource=isSourceDelta ? string.Empty : closingSource,ClosingBalance=statementClosing,TotalDebit=totalDebit,TotalCredit=totalCredit,TransactionCount=distinctRows.Count,
                        ReconciliationStatus=status,Difference=statementDifference,OpenSeconds=openSeconds,ProcessSeconds=processWatch.Elapsed.TotalSeconds,ImportedUtc=DateTime.UtcNow
                    };
                    var preImportTally = request.PreImportReconciliationAsync is not null;
                    foreach (var row in distinctRows)
                    {
                        row.ImportedUtc = DateTime.UtcNow;
                        if (preImportTally)
                            row.RecordStatus = "Archived Pre-Import Staging";
                    }

                    if (cachedSource is not null)
                    {
                        // Nothing authoritative is written yet. The fully parsed source is
                        // staged in RAM; after every source for this bank has been prepared,
                        // balance/chain reconstruction runs against the combined bank snapshot
                        // and the bank is committed once.
                        cachedPrepared.Add(new PreparedCachedStatement(
                            bankAccount,
                            statement,
                            distinctRows,
                            identityPath,
                            parsedRows.Any(item => item.Mode.Equals("Contra", StringComparison.OrdinalIgnoreCase))));
                        filePreparedForCachedCommit = true;
                    }
                    else
                    {
                        bankAccount = repository.UpsertAccount(bankAccount);
                        EnsureBankLedger(request, bankAccount);
                        if (parsedRows.Any(item => item.Mode.Equals("Contra", StringComparison.OrdinalIgnoreCase)))
                            EnsureCashLedger(request);
                        statement.BankAccountId = bankAccount.Id;
                        statement.Id = repository.SaveStatement(statement);
                        foreach (var row in distinctRows)
                        {
                            row.BankAccountId = bankAccount.Id;
                            row.StatementId = statement.Id;
                        }
                        var rowsInserted = repository.InsertTransactions(distinctRows);
                        var finalizedActive = rowsInserted;
                        var preImportCheckpointReady = false;
                        if (preImportTally)
                        {
                            try
                            {
                                var preResult = request.PreImportReconciliationAsync!(cancellationToken)
                                    .GetAwaiter().GetResult();
                                preImportCheckpointReady = preResult.CheckpointReady;
                                if (!string.IsNullOrWhiteSpace(preResult.Status))
                                    result.PreImportTallyStatus = preResult.Status;
                            }
                            catch (Exception preImportException) when (preImportException is not OperationCanceledException)
                            {
                                result.PreImportTallyStatus = $"Tally pre-import reconciliation skipped without holding import: {preImportException.Message}";
                            }
                            finally
                            {
                                var rowsAfterTally = repository.GetTransactions(request.ClientId, request.FinancialYear, includeArchived: true)
                                    .Where(row => row.StatementId == statement.Id)
                                    .ToArray();
                                result.TransactionsMatchedBeforeImport += rowsAfterTally.Count(row =>
                                    row.RecordStatus.Equals("Archived Tally Reconciled", StringComparison.OrdinalIgnoreCase));
                                finalizedActive = repository.FinalizePreImportStaging(statement.Id);
                            }

                            if (preImportCheckpointReady && request.PostImportCheckpointAsync is not null)
                            {
                                try
                                {
                                    request.PostImportCheckpointAsync(cancellationToken).GetAwaiter().GetResult();
                                }
                                catch (Exception checkpointException) when (checkpointException is not OperationCanceledException)
                                {
                                    result.PreImportTallyStatus = string.IsNullOrWhiteSpace(result.PreImportTallyStatus)
                                        ? $"Bank import completed; linked-Tally checkpoint will retry later: {checkpointException.Message}"
                                        : result.PreImportTallyStatus + $" Checkpoint will retry later: {checkpointException.Message}";
                                }
                            }
                        }

                        result.TransactionsImported += finalizedActive;
                        result.FilesImported++;
                        SaveImportAttempt(repository, request, displayFile, identityPath, statement.SourceSheet, hash, statement, "IMPORTED",
                            "Statement imported and retained in the physical Statement Metadata registry.",
                            statement.PeriodFrom, statement.PeriodTo, statement.OpeningBalance, statement.ClosingBalance, statement.TotalDebit, statement.TotalCredit, statement.TransactionCount);
                        result.PeriodFrom = Min(result.PeriodFrom, statement.PeriodFrom);
                        result.PeriodTo = Max(result.PeriodTo, statement.PeriodTo);
                    }
                }
                finally
                {
                    if (convertedPath is not null)
                    {
                        try { File.Delete(convertedPath); } catch { }
                    }
                }
            }
            catch (Exception exception)
            {
                var code = ClassifyImportFailure(Path.GetExtension(originalPath), exception);
                result.Issues.Add(BankStatementImportIssue.Create(displayFile, "Import", code, exception.Message, request.Attempt));
            }
            finally
            {
                if (!filePreparedForCachedCommit &&
                    result.FilesImported == importedBefore &&
                    !result.Issues.Any(issue => issue.SourceFile.Equals(displayFile, StringComparison.OrdinalIgnoreCase)))
                {
                    // Duplicate statements are counted separately and are not errors, but they are skipped files.
                    result.FilesSkipped++;
                }
                else if (!filePreparedForCachedCommit &&
                         result.FilesImported == importedBefore &&
                         result.Issues.Any(issue => issue.SourceFile.Equals(displayFile, StringComparison.OrdinalIgnoreCase)))
                {
                    result.FilesSkipped++;
                }
            }
        }

        if (cachedPrepared.Count > 0)
        {
            try
            {
                FinalizeAndCommitCachedBankBatch(
                    request,
                    cachedPrepared,
                    existingStatements!,
                    existingTransactions!,
                    result,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                foreach (var prepared in cachedPrepared)
                {
                    if (!result.Issues.Any(issue => issue.SourceFile.Equals(prepared.Statement.SourceFile, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Issues.Add(BankStatementImportIssue.Create(
                            prepared.Statement.SourceFile,
                            "Bank Cache Commit",
                            BankImportErrorCode.E013_ReconciliationFailure,
                            $"The Bank cache was finalized in memory but the atomic bank write failed: {exception.Message}",
                            request.Attempt));
                        result.FilesSkipped++;
                    }
                }
            }
        }

        // Manual/direct compatibility imports still use the established repository
        // reconciliation pass. Cache-first Auto Import is already fully reconciled in RAM
        // before its single bank-wise transaction and must not immediately re-read/rewrite it.
        // Rebuild transaction running-balance, file, inter-file and whole-account checks
        // after the complete batch so no statement waits for another UI process to finish.
        if (sourceCache is null && result.FilesImported > 0)
        {
            try
            {
                new BankReconciliationService(repository).Reconcile(request.ClientId, request.FinancialYear);
            }
            catch (Exception exception)
            {
                result.Issues.Add(BankStatementImportIssue.Create(
                    "Bank Statement batch",
                    "Balance Control",
                    BankImportErrorCode.E013_ReconciliationFailure,
                    $"Transactions were imported, but transaction/file/inter-file/account balance verification failed: {exception.Message}",
                    request.Attempt));
            }
        }

        result.AccountsCreated = Math.Max(0, repository.GetAccounts(request.ClientId, request.FinancialYear).Count - existingAccounts);
        return result;
    }


    private void FinalizeAndCommitCachedBankBatch(
        BankStatementImportRequest request,
        IReadOnlyList<PreparedCachedStatement> prepared,
        IReadOnlyList<BankStatementRecord> existingStatements,
        IReadOnlyList<BankTransactionRecord> existingTransactions,
        BankStatementImportResult result,
        CancellationToken cancellationToken)
    {
        var syntheticStatementId = -1L;
        foreach (var accountGroup in prepared.GroupBy(item => AccountCacheKey(item.Account), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupItems = accountGroup.ToList();
            if (groupItems.Count == 0) continue;

            var targetAccount = groupItems[0].Account;
            // A history-unknown Auto Import source uses transaction-level fallback against
            // older/manual data. It is additive/delta authority, not a same-path replacement:
            // preserving the existing statement is essential after overlapping rows were removed.
            var replacementPaths = request.AutoImportTransactionDuplicateFallback
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : groupItems
                    .Select(item => Path.GetFullPath(item.SourcePath))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var replacedExistingStatements = existingStatements
                .Where(statement => !string.IsNullOrWhiteSpace(statement.SourcePath) &&
                                    replacementPaths.Contains(Path.GetFullPath(statement.SourcePath)))
                .ToList();
            var replacedExistingIds = replacedExistingStatements.Select(statement => statement.Id).ToHashSet();

            // Give new cached statements temporary negative IDs so all transaction/file/
            // inter-file/account reconciliation can be performed in RAM before SQLite assigns
            // real IDs. Only these new statements may have missing running balances reconstructed.
            var newSyntheticIds = new HashSet<long>();
            foreach (var item in groupItems)
            {
                item.Statement.Id = syntheticStatementId--;
                newSyntheticIds.Add(item.Statement.Id);
                foreach (var row in item.Transactions)
                    row.StatementId = item.Statement.Id;
            }

            var existingTargetStatements = targetAccount.Id > 0
                ? existingStatements
                    .Where(statement => statement.BankAccountId == targetAccount.Id &&
                                        !replacedExistingIds.Contains(statement.Id))
                    .ToList()
                : new List<BankStatementRecord>();
            var existingTargetIds = existingTargetStatements.Select(statement => statement.Id).ToHashSet();
            var targetTransactions = existingTransactions
                .Where(row => existingTargetIds.Contains(row.StatementId))
                .Concat(groupItems.SelectMany(item => item.Transactions))
                .ToList();
            var targetStatements = existingTargetStatements
                .Concat(groupItems.Select(item => item.Statement))
                .ToList();

            BankBalanceCacheEngine.FinalizeAccount(
                targetStatements,
                targetTransactions,
                newSyntheticIds);

            // If a changed file moved from one detected Bank/account to another, its old
            // account chain also changes when the prior whole-file contribution disappears.
            // Recalculate those neighbouring statements in memory and persist the resulting
            // chain status in the same atomic transaction.
            var existingStatementUpdates = new List<BankStatementRecord>(existingTargetStatements);
            foreach (var oldAccountId in replacedExistingStatements
                         .Select(statement => statement.BankAccountId)
                         .Where(id => id > 0 && id != targetAccount.Id)
                         .Distinct())
            {
                var remainingOldStatements = existingStatements
                    .Where(statement => statement.BankAccountId == oldAccountId &&
                                        !replacedExistingIds.Contains(statement.Id))
                    .ToList();
                if (remainingOldStatements.Count == 0) continue;
                var remainingOldIds = remainingOldStatements.Select(statement => statement.Id).ToHashSet();
                var remainingOldTransactions = existingTransactions
                    .Where(row => remainingOldIds.Contains(row.StatementId))
                    .ToList();
                BankBalanceCacheEngine.FinalizeAccount(
                    remainingOldStatements,
                    remainingOldTransactions);
                existingStatementUpdates.AddRange(remainingOldStatements);
            }

            var commitItems = groupItems
                .Select(item => new BankStatementBatchCommitItem
                {
                    Account = item.Account,
                    Statement = item.Statement,
                    Transactions = item.Transactions,
                    SourcePath = item.SourcePath
                })
                .ToArray();

            var commit = repository.CommitBankCacheBatch(commitItems, existingStatementUpdates);

            // Ledger master creation remains a one-time account authority, not a per-file
            // operation. Ledger Mapping/Contra reporting itself is still a later module phase.
            EnsureBankLedger(request, groupItems[0].Account);
            if (groupItems.Any(item => item.HasContra))
                EnsureCashLedger(request);

            var finalizedActive = commit.TransactionsInserted;
            var preImportCheckpointReady = false;
            if (request.PreImportReconciliationAsync is not null)
            {
                try
                {
                    var preResult = request.PreImportReconciliationAsync(cancellationToken)
                        .GetAwaiter().GetResult();
                    preImportCheckpointReady = preResult.CheckpointReady;
                    if (!string.IsNullOrWhiteSpace(preResult.Status))
                        result.PreImportTallyStatus = preResult.Status;
                }
                catch (Exception preImportException) when (preImportException is not OperationCanceledException)
                {
                    result.PreImportTallyStatus =
                        $"Tally pre-import reconciliation skipped without holding import: {preImportException.Message}";
                }
                finally
                {
                    var newStatementIds = commit.Items.Select(item => item.StatementId).ToHashSet();
                    var rowsAfterTally = repository
                        .GetTransactions(request.ClientId, request.FinancialYear, includeArchived: true)
                        .Where(row => newStatementIds.Contains(row.StatementId))
                        .ToArray();
                    result.TransactionsMatchedBeforeImport += rowsAfterTally.Count(row =>
                        row.RecordStatus.Equals("Archived Tally Reconciled", StringComparison.OrdinalIgnoreCase));
                    finalizedActive = commit.Items.Sum(item => repository.FinalizePreImportStaging(item.StatementId));
                }

                if (preImportCheckpointReady && request.PostImportCheckpointAsync is not null)
                {
                    try
                    {
                        request.PostImportCheckpointAsync(cancellationToken).GetAwaiter().GetResult();
                    }
                    catch (Exception checkpointException) when (checkpointException is not OperationCanceledException)
                    {
                        result.PreImportTallyStatus = string.IsNullOrWhiteSpace(result.PreImportTallyStatus)
                            ? $"Bank import completed; linked-Tally checkpoint will retry later: {checkpointException.Message}"
                            : result.PreImportTallyStatus + $" Checkpoint will retry later: {checkpointException.Message}";
                    }
                }
            }

            result.TransactionsImported += finalizedActive;
            result.FilesImported += groupItems.Count;
            for (var index = 0; index < groupItems.Count; index++)
            {
                var item = groupItems[index];
                var committedStatementId = index < commit.Items.Count ? commit.Items[index].StatementId : (long?)null;
                if (committedStatementId.HasValue) item.Statement.Id = committedStatementId.Value;
                SaveImportAttempt(repository, request, item.Statement.SourceFile, item.SourcePath, item.Statement.SourceSheet, item.Statement.FileHash, item.Statement, "IMPORTED",
                    "Cache-first statement imported and retained in the physical Statement Metadata registry.",
                    item.Statement.PeriodFrom, item.Statement.PeriodTo, item.Statement.OpeningBalance, item.Statement.ClosingBalance, item.Statement.TotalDebit, item.Statement.TotalCredit, item.Statement.TransactionCount);
                result.PeriodFrom = Min(result.PeriodFrom, item.Statement.PeriodFrom);
                result.PeriodTo = Max(result.PeriodTo, item.Statement.PeriodTo);
            }
        }
    }

    private static void SaveImportAttempt(
        IBankingRepository repository,
        BankStatementImportRequest request,
        string sourceFile,
        string sourcePath,
        string sourceSheet,
        string fileHash,
        BankStatementRecord? authorityStatement,
        string status,
        string notes,
        DateTime? periodFrom = null,
        DateTime? periodTo = null,
        decimal? openingBalance = null,
        decimal? closingBalance = null,
        decimal totalDebit = 0m,
        decimal totalCredit = 0m,
        int transactionCount = 0)
    {
        try
        {
            repository.SaveStatementImportAttempt(new BankStatementImportAttemptRecord
            {
                ClientId = request.ClientId,
                FinancialYear = request.FinancialYear,
                BankAccountId = authorityStatement?.BankAccountId ?? 0,
                StatementId = status.Equals("IMPORTED", StringComparison.OrdinalIgnoreCase) && authorityStatement?.Id > 0 ? authorityStatement.Id : null,
                DuplicateOfStatementId = status.Equals("DUPLICATE", StringComparison.OrdinalIgnoreCase) && authorityStatement?.Id > 0 ? authorityStatement.Id : null,
                SourceFile = sourceFile ?? string.Empty,
                SourcePath = sourcePath ?? string.Empty,
                SourceSheet = sourceSheet ?? string.Empty,
                FileHash = fileHash ?? string.Empty,
                PeriodFrom = periodFrom ?? authorityStatement?.PeriodFrom,
                PeriodTo = periodTo ?? authorityStatement?.PeriodTo,
                OpeningBalance = openingBalance ?? authorityStatement?.OpeningBalance,
                ClosingBalance = closingBalance ?? authorityStatement?.ClosingBalance,
                TotalDebit = totalDebit != 0m ? totalDebit : authorityStatement?.TotalDebit ?? 0m,
                TotalCredit = totalCredit != 0m ? totalCredit : authorityStatement?.TotalCredit ?? 0m,
                TransactionCount = transactionCount != 0 ? transactionCount : authorityStatement?.TransactionCount ?? 0,
                Status = status,
                Notes = notes,
                ImportedUtc = DateTime.UtcNow
            });
        }
        catch
        {
            // Import audit must never block accounting import. The authoritative Bank data
            // stays primary; a later registry repair can rebuild IMPORTED rows.
        }
    }

    private static string AccountCacheKey(BankAccountRecord account) =>
        BankAccountIdentity.BuildKey(account);

    private static bool SameAccount(BankAccountRecord left, BankAccountRecord right) =>
        BankAccountIdentity.Same(left, right);

    private static bool NullableDateEqual(DateTime? left, DateTime? right)
    {
        if (!left.HasValue || !right.HasValue) return left.HasValue == right.HasValue;
        return left.Value.Date == right.Value.Date;
    }

    private sealed record PreparedCachedStatement(
        BankAccountRecord Account,
        BankStatementRecord Statement,
        IReadOnlyList<BankTransactionRecord> Transactions,
        string SourcePath,
        bool HasContra);

    private IReadOnlyList<SheetData> ReadWorkbookWithRecovery(
        string path,
        string originalPath,
        string password,
        bool recoveryMode,
        CancellationToken cancellationToken,
        ref string? convertedPath)
    {
        // Invariant: an encrypted OpenXML envelope is an OLE compound file, not a ZIP workbook.
        // Never let it reach the raw OpenXML reader; doing so produces the misleading
        // "End of Central Directory" error before password discovery/decryption.
        if (SpreadsheetSourceInspector.IsEncryptedOpenXmlContainer(path))
            throw new InvalidDataException("Encrypted Excel workbook reached the OpenXML reader before password decryption. Retry through the password-first Excel recovery path.");

        try
        {
            return ReadWorkbook(path, cancellationToken);
        }
        catch (InvalidDataException) when (!string.IsNullOrWhiteSpace(password))
        {
            convertedPath = ConvertLegacyXls(originalPath, password);
            return ReadWorkbook(convertedPath, cancellationToken);
        }
        catch when (recoveryMode)
        {
            // Excel trouble-queue recovery sequence: retry from an isolated local copy.
            // This avoids network/share locks and partial-read interference without changing
            // the original statement. Legacy/password sources then use Excel data extraction.
            var extension = Path.GetExtension(path);
            var recoveryFolder = Path.Combine(Path.GetTempPath(), "AccountingAutomationHub", "BankRecovery");
            Directory.CreateDirectory(recoveryFolder);
            var localCopy = Path.Combine(recoveryFolder, $"AAH_Bank_Recovery_{Guid.NewGuid():N}{extension}");
            File.Copy(path, localCopy, true);
            try
            {
                try
                {
                    // Alternate/local open.
                    return ReadWorkbook(localCopy, cancellationToken);
                }
                catch when (OperatingSystem.IsWindows())
                {
                    Exception? lastRecoveryError = null;
                    foreach (var corruptLoad in new[] { 1, 2, 0 })
                    {
                        try
                        {
                            if (convertedPath is not null)
                            {
                                try { File.Delete(convertedPath); } catch { }
                                convertedPath = null;
                            }
                            // 1 = Excel repair, 2 = Excel data extraction, 0 = final normal load.
                            convertedPath = ConvertWorkbookWithExcel(originalPath, password, corruptLoad);
                            return ReadWorkbook(convertedPath, cancellationToken);
                        }
                        catch (Exception exception)
                        {
                            lastRecoveryError = exception;
                        }
                    }
                    throw new InvalidDataException("Excel repair, data extraction, alternate local open, and final normal-load recovery all failed.", lastRecoveryError);
                }
            }
            finally
            {
                try { File.Delete(localCopy); } catch { }
            }
        }
    }

    private static (BankImportErrorCode Code, string Stage, string Message) DiagnoseHeaderFailure(IReadOnlyList<SheetData> sheets)
    {
        if (sheets.Count == 0 || sheets.All(sheet => sheet.Rows.Count == 0))
            return (BankImportErrorCode.E004_TransactionSheetNotFound, "Transaction Sheet", "No readable worksheet or transaction rows were found.");

        var bestKnownHeaders = 0;
        var sawDate = false;
        var sawDebit = false;
        var sawCredit = false;
        var sawAmount = false;
        foreach (var sheet in sheets)
        {
            for (var row = 1; row <= Math.Min(sheet.MaxRow, 100); row++)
            {
                var headers = sheet.GetRow(row).ToDictionary(pair => pair.Key, pair => NormalizeHeader(pair.Value));
                var date = FindColumn(headers, DateHeaders) > 0;
                var debit = FindColumn(headers, DebitHeaders) > 0;
                var credit = FindColumn(headers, CreditHeaders) > 0;
                var amount = FindColumn(headers, AmountHeaders) > 0;
                var narration = FindColumn(headers, NarrationHeaders) > 0;
                var known = (date ? 1 : 0) + (debit ? 1 : 0) + (credit ? 1 : 0) + (amount ? 1 : 0) + (narration ? 1 : 0);
                if (known < bestKnownHeaders) continue;
                bestKnownHeaders = known;
                sawDate = date;
                sawDebit = debit;
                sawCredit = credit;
                sawAmount = amount;
            }
        }

        if (bestKnownHeaders == 0)
            return (BankImportErrorCode.E005_HeaderDetectionFailure, "Header Detection", "No reliable transaction header row was detected.");
        if (!sawDate)
            return (BankImportErrorCode.E006_DateColumnMissing, "Column Detection", "A transaction Date column was not detected.");
        if (!sawDebit && !sawAmount)
            return (BankImportErrorCode.E007_DebitColumnMissing, "Column Detection", "A Debit or Amount column was not detected.");
        if (!sawCredit && !sawAmount)
            return (BankImportErrorCode.E008_CreditColumnMissing, "Column Detection", "A Credit or Amount column was not detected.");
        return (BankImportErrorCode.E005_HeaderDetectionFailure, "Header Detection", "No transaction table with Date and Debit/Credit or Amount columns was found.");
    }

    private static string ConvertExcelSourceWithPasswordDiscovery(
        string sourcePath,
        string suppliedPassword,
        bool recoveryMode,
        bool sourceLooksEncryptedOpenXml,
        out string resolvedPassword)
    {
        resolvedPassword = suppliedPassword ?? string.Empty;
        var successfulPassword = resolvedPassword;
        Exception? firstFailure = null;
        Exception? lastFailure = null;
        var automaticCandidates = LocalPasswordCandidateDiscovery.Discover(sourcePath);
        var attempted = new HashSet<string>(StringComparer.Ordinal);

        bool TryConvert(string candidate, out string converted)
        {
            converted = string.Empty;
            if (!attempted.Add(candidate)) return false;
            try
            {
                converted = recoveryMode
                    ? ConvertLegacyXlsWithRecovery(sourcePath, candidate)
                    : ConvertLegacyXls(sourcePath, candidate);
                successfulPassword = candidate;
                return true;
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
                lastFailure = exception;
                return false;
            }
        }

        // Password-protected .xlsx/.xlsm files are OLE encrypted packages even though their
        // extension is OpenXML. If the file name itself says PW/Password/Pass/Pwd, or a tiny
        // companion password file exists beside it, try those discovered candidates BEFORE a
        // blank normal-open attempt. This avoids misclassifying the encrypted package as a broken
        // ZIP (for example "End of Central Directory record could not be found").
        if (sourceLooksEncryptedOpenXml && string.IsNullOrEmpty(resolvedPassword))
        {
            foreach (var candidate in automaticCandidates)
            {
                if (!TryConvert(candidate, out var converted)) continue;
                resolvedPassword = successfulPassword;
                return converted;
            }
        }

        if (TryConvert(resolvedPassword, out var suppliedConverted))
        {
            resolvedPassword = successfulPassword;
            return suppliedConverted;
        }

        foreach (var candidate in automaticCandidates)
        {
            if (!TryConvert(candidate, out var converted)) continue;
            resolvedPassword = successfulPassword;
            return converted;
        }

        var passwordRelated = sourceLooksEncryptedOpenXml ||
                              IsPasswordRelatedFailure(firstFailure) ||
                              IsPasswordRelatedFailure(lastFailure);
        if (passwordRelated)
        {
            throw new InvalidDataException(
                $"The workbook is password protected. {automaticCandidates.Count:N0} automatic password candidate(s) from the protected file name and nearby helper files were tried before manual entry, but none unlocked it. Manual password entry is required.",
                lastFailure ?? firstFailure);
        }

        throw firstFailure ?? new InvalidDataException("Excel could not open the workbook.");
    }

    private static bool IsPasswordRelatedFailure(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message ?? string.Empty;
            if (message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("encrypted", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("protected", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("decrypt", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static BankImportErrorCode ClassifyOpenFailure(string extension, Exception exception, string password, bool recoveryMode)
    {
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase)) return BankImportErrorCode.E014_CsvImportFailure;
        if (IsPasswordRelatedFailure(exception)) return BankImportErrorCode.E002_PasswordFailure;
        if (recoveryMode) return BankImportErrorCode.E003_RecoveryFailure;
        return BankImportErrorCode.E001_WorkbookOpenFailure;
    }

    private static BankImportErrorCode ClassifyImportFailure(string extension, Exception exception)
    {
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase)) return BankImportErrorCode.E014_CsvImportFailure;
        var message = exception.Message ?? string.Empty;
        if (IsPasswordRelatedFailure(exception))
            return BankImportErrorCode.E002_PasswordFailure;
        if (extension.Equals(".xls", StringComparison.OrdinalIgnoreCase)) return BankImportErrorCode.E001_WorkbookOpenFailure;
        if (message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) || message.Contains("fingerprint", StringComparison.OrdinalIgnoreCase))
            return BankImportErrorCode.E012_DuplicateDetectionFailure;
        if (message.Contains("reconcil", StringComparison.OrdinalIgnoreCase) || message.Contains("balance", StringComparison.OrdinalIgnoreCase))
            return BankImportErrorCode.E013_ReconciliationFailure;
        if (message.Contains("password", StringComparison.OrdinalIgnoreCase) || message.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
            return BankImportErrorCode.E002_PasswordFailure;
        return BankImportErrorCode.E015_UnknownFailure;
    }

    private static IReadOnlyList<SheetData> ConvertWorkbookData(OpenXmlWorkbookData workbook)
    {
        return workbook.Worksheets.Values.Select(sheet =>
        {
            var rows = new Dictionary<int, Dictionary<int, string>>();
            foreach (var sourceRow in sheet.Rows)
                rows[sourceRow.Key] = sourceRow.Value.ToDictionary(pair => pair.Key, pair => pair.Value);
            return new SheetData(sheet.Name, rows, sheet.MaxRow, workbook.Uses1904DateSystem);
        }).ToList();
    }

    private IReadOnlyList<SheetData> ReadWorkbook(string path, CancellationToken cancellationToken)
    {
        var workbook = _workbookReader.Read(path, cancellationToken);
        return workbook.Worksheets.Values.Select(sheet =>
        {
            var rows = new Dictionary<int, Dictionary<int, string>>();
            foreach (var sourceRow in sheet.Rows)
                rows[sourceRow.Key] = sourceRow.Value.ToDictionary(pair => pair.Key, pair => pair.Value);
            return new SheetData(sheet.Name, rows, sheet.MaxRow, workbook.Uses1904DateSystem);
        }).ToList();
    }

    private static SheetData ReadCsv(string path)
    {
        var rows = new Dictionary<int, Dictionary<int, string>>();
        var text = ReadTextWithEncodingFallback(path);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var delimiter = FlexibleWorkbookReader.DetectDelimiter(lines);
        var rowNumber = 0;
        foreach (var line in lines)
        {
            rowNumber++;
            var fields = ParseDelimitedLine(line, delimiter);
            var cells = new Dictionary<int, string>();
            for (var index = 0; index < fields.Count; index++)
            {
                if (fields[index].Length > 0) cells[index + 1] = fields[index];
            }
            if (cells.Count > 0) rows[rowNumber] = cells;
        }
        return new SheetData(Path.GetFileNameWithoutExtension(path), rows, rowNumber, false);
    }

    private static string ReadTextWithEncodingFallback(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static char DetectDelimiter(string line)
    {
        var candidates = new[] { ',', '\t', ';', '|' };
        return candidates.OrderByDescending(candidate => CountUnquoted(line, candidate)).First();
    }

    private static int CountUnquoted(string line, char delimiter)
    {
        var quoted = false;
        var count = 0;
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') index++;
                else quoted = !quoted;
            }
            else if (!quoted && line[index] == delimiter) count++;
        }
        return count;
    }

    private static List<string> ParseDelimitedLine(string line, char delimiter)
    {
        var result = new List<string>();
        var builder = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') { builder.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (character == delimiter && !quoted) { result.Add(builder.ToString().Trim()); builder.Clear(); }
            else builder.Append(character);
        }
        result.Add(builder.ToString().Trim());
        return result;
    }

    private static SheetAnalysis AnalyzeSheet(SheetData sheet)
    {
        var best = new SheetAnalysis(sheet);
        var lastHeaderRow = Math.Min(sheet.MaxRow, ExcelHeaderScanLimit);
        for (var row = 1; row <= lastHeaderRow; row++)
        {
            var candidate = AnalyzeHeaderCandidate(sheet, row);
            if (candidate.HeaderRow <= 0 || candidate.Score <= best.Score) continue;
            best = candidate;
        }
        return best;
    }

    private static SheetAnalysis AnalyzeHeaderCandidate(SheetData sheet, int headerRow)
    {
        var maxColumn = GetObservedMaxColumn(sheet, Math.Max(1, headerRow - 2), Math.Min(sheet.MaxRow, headerRow + ExcelProfileSampleRows));
        if (maxColumn <= 0) return new SheetAnalysis(sheet);

        var profiles = new List<ExcelColumnProfile>();
        for (var column = 1; column <= maxColumn; column++)
        {
            var header = BuildCompositeHeader(sheet, headerRow, column);
            profiles.Add(BuildColumnProfile(sheet, headerRow, column, header));
        }

        var dateColumn = SelectColumn(profiles, profile => ScoreDateColumn(profile), 55);
        if (dateColumn <= 0) return new SheetAnalysis(sheet);

        var valueDateColumn = SelectColumn(
            profiles.Where(profile => profile.Column != dateColumn),
            profile => ScoreValueDateColumn(profile),
            70);

        var balanceColumn = SelectColumn(profiles, ScoreBalanceColumn, 70);
        var debitColumn = SelectColumn(
            profiles.Where(profile => profile.Column != balanceColumn),
            ScoreDebitColumn,
            70);
        var creditColumn = SelectColumn(
            profiles.Where(profile => profile.Column != balanceColumn && profile.Column != debitColumn),
            ScoreCreditColumn,
            70);
        var amountColumn = SelectColumn(
            profiles.Where(profile => profile.Column != balanceColumn && profile.Column != debitColumn && profile.Column != creditColumn),
            ScoreAmountColumn,
            70);
        var drCrColumn = SelectColumn(profiles, ScoreDrCrColumn, 65);

        var hasMoneyStructure = (debitColumn > 0 && creditColumn > 0) || amountColumn > 0 || balanceColumn > 0;
        if (!hasMoneyStructure) return new SheetAnalysis(sheet);

        var mappedColumns = new HashSet<int>
        {
            dateColumn, valueDateColumn, debitColumn, creditColumn, amountColumn, balanceColumn, drCrColumn
        };
        mappedColumns.Remove(0);

        var referenceColumns = profiles
            .Where(profile => !mappedColumns.Contains(profile.Column))
            .Select(profile => new { profile.Column, Score = ScoreReferenceColumn(profile) })
            .Where(item => item.Score >= 75)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Column)
            .Take(4)
            .Select(item => item.Column)
            .ToList();
        foreach (var column in referenceColumns) mappedColumns.Add(column);

        var narrationScored = profiles
            .Where(profile => !mappedColumns.Contains(profile.Column))
            .Select(profile => new
            {
                Profile = profile,
                Score = ScoreNarrationColumn(profile),
                Explicit = HeaderAffinity(profile.Header, NarrationHeaders) >= 80
            })
            .Where(item => item.Score >= 45)
            .OrderByDescending(item => item.Explicit)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.Profile.Column)
            .ToList();

        // Prefer every genuinely descriptive explicit field, then add rich text columns that
        // carry transaction description under an unfamiliar bank-specific heading. Placeholder-
        // only fields such as PNB ONE's '-' column never pass this content score.
        var narrationColumns = narrationScored
            .Where(item => item.Explicit && item.Score >= 55)
            .Select(item => item.Profile.Column)
            .ToList();
        if (narrationColumns.Count == 0)
        {
            narrationColumns.AddRange(narrationScored
                .Where(item => !item.Explicit && item.Score >= 70)
                .Take(3)
                .Select(item => item.Profile.Column));
        }
        narrationColumns = narrationColumns.Distinct().OrderBy(column => column).Take(8).ToList();

        var pipelineHeaders = sheet.GetRow(headerRow).ToDictionary(pair => pair.Key, pair => NormalizeHeader(pair.Value));
        var isPipelineGenerated = LooksLikePipelineGeneratedSheet(sheet, headerRow, pipelineHeaders);
        if (isPipelineGenerated)
        {
            var dedicatedNarration = FindColumn(pipelineHeaders, NarrationHeaders);
            narrationColumns.Clear();
            if (dedicatedNarration > 0) narrationColumns.Add(dedicatedNarration);
        }

        var transactionLikeRows = CountTransactionLikeRows(
            sheet,
            headerRow,
            dateColumn,
            debitColumn,
            creditColumn,
            amountColumn,
            balanceColumn,
            drCrColumn);
        if (transactionLikeRows <= 0) return new SheetAnalysis(sheet);

        var roleScore = Math.Min(40, transactionLikeRows * 3)
                        + Math.Max(0, ScoreDateColumn(profiles.First(profile => profile.Column == dateColumn)) / 8)
                        + (debitColumn > 0 ? 8 : 0)
                        + (creditColumn > 0 ? 8 : 0)
                        + (amountColumn > 0 ? 6 : 0)
                        + (balanceColumn > 0 ? 5 : 0)
                        + (narrationColumns.Count > 0 ? 8 : 0)
                        + (referenceColumns.Count > 0 ? 2 : 0);

        return new SheetAnalysis(sheet)
        {
            HeaderRow = headerRow,
            DateColumn = dateColumn,
            ValueDateColumn = valueDateColumn,
            DebitColumn = debitColumn,
            CreditColumn = creditColumn,
            AmountColumn = amountColumn,
            BalanceColumn = balanceColumn,
            NarrationColumn = narrationColumns.FirstOrDefault(),
            NarrationColumns = narrationColumns,
            ReferenceColumn = referenceColumns.FirstOrDefault(),
            ReferenceColumns = referenceColumns,
            DrCrColumn = drCrColumn,
            Score = roleScore,
            IsPipelineGenerated = isPipelineGenerated
        };
    }

    private static int GetObservedMaxColumn(SheetData sheet, int fromRow, int toRow)
    {
        var max = 0;
        for (var row = fromRow; row <= toRow; row++)
        {
            var cells = sheet.GetRow(row);
            if (cells.Count > 0) max = Math.Max(max, cells.Keys.Max());
        }
        return Math.Min(max, 256);
    }

    private static string BuildCompositeHeader(SheetData sheet, int headerRow, int column)
    {
        var parts = new List<string>();
        var currentRaw = ExcelWorksheetTrim(Get(sheet.GetRow(headerRow), column));
        if (currentRaw.Length > 0 &&
            !IsNarrationPlaceholder(currentRaw) &&
            !TryParseDate(currentRaw, sheet.Uses1904DateSystem, out _) &&
            !(TryParseNullableDecimal(currentRaw).HasValue && currentRaw.Count(char.IsLetter) == 0))
        {
            parts.Add(NormalizeHeader(currentRaw));
        }

        // Two-level bank headers commonly use a parent such as Debit/Credit/Transaction
        // above a child such as Amount/Details. Only merge a previous-row cell when that
        // previous value itself looks like a column-role header; never merge account/branch
        // metadata from the rows above the table into the column identity.
        for (var row = Math.Max(1, headerRow - 2); row < headerRow; row++)
        {
            var raw = ExcelWorksheetTrim(Get(sheet.GetRow(row), column));
            if (!LooksLikeHeaderParent(raw)) continue;
            var normalized = NormalizeHeader(raw);
            if (normalized.Length == 0 || parts.Contains(normalized, StringComparer.OrdinalIgnoreCase)) continue;
            parts.Insert(0, normalized);
        }
        return string.Join(" ", parts);
    }

    private static bool LooksLikeHeaderParent(string value)
    {
        var header = NormalizeHeader(value);
        if (header.Length == 0 || header.Length > 45) return false;
        return HeaderAffinity(header, DateHeaders) > 0 ||
               HeaderAffinity(header, DebitHeaders) > 0 ||
               HeaderAffinity(header, CreditHeaders) > 0 ||
               HeaderAffinity(header, AmountHeaders) > 0 ||
               HeaderAffinity(header, BalanceHeaders) > 0 ||
               HeaderAffinity(header, NarrationHeaders) > 0 ||
               HeaderAffinity(header, ReferenceHeaders) > 0 ||
               HeaderAffinity(header, DrCrHeaders) > 0 ||
               header is "TRANSACTION" or "TRANSACTIONS" or "DETAIL" or "DETAILS" or "AMOUNT DETAILS";
    }

    private static ExcelColumnProfile BuildColumnProfile(SheetData sheet, int headerRow, int column, string header)
    {
        var profile = new ExcelColumnProfile(column, header);
        var end = Math.Min(sheet.MaxRow, headerRow + ExcelProfileSampleRows);
        for (var row = headerRow + 1; row <= end; row++)
        {
            var value = ExcelWorksheetTrim(Get(sheet.GetRow(row), column));
            if (value.Length == 0) continue;
            profile.NonEmpty++;
            if (IsNarrationPlaceholder(value))
            {
                profile.Placeholders++;
                continue;
            }

            if (TryParseDate(value, sheet.Uses1904DateSystem, out _)) profile.DateLike++;
            if (TryParseNullableDecimal(value).HasValue) profile.NumericLike++;
            var normalized = NormalizeHeader(value);
            if (normalized is "DR" or "CR" or "DEBIT" or "CREDIT" or "PAYMENT" or "RECEIPT") profile.DrCrLike++;

            var letters = value.Count(char.IsLetter);
            if (letters > 0) profile.TextLike++;
            if (letters >= 3 && value.Length >= 5 && !LooksLikeStatementSummaryContinuation(value))
            {
                profile.RichText++;
                profile.DistinctText.Add(NormalizeNarrationComparison(value));
            }
            if (LooksReferenceOnly(value)) profile.ReferenceLike++;
        }
        return profile;
    }

    private static int SelectColumn(IEnumerable<ExcelColumnProfile> profiles, Func<ExcelColumnProfile, int> scorer, int minimum)
    {
        var best = profiles
            .Select(profile => new { profile.Column, Score = scorer(profile) })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Column)
            .FirstOrDefault();
        return best is not null && best.Score >= minimum ? best.Column : 0;
    }

    private static int HeaderAffinity(string header, IEnumerable<string> aliases)
    {
        var normalized = ImportHeaderMappingCatalog.NormalizeHeader(header);
        if (normalized.Length == 0) return 0;
        var best = 0;
        foreach (var aliasValue in ExpandBankAliases(aliases))
        {
            var alias = ImportHeaderMappingCatalog.NormalizeHeader(aliasValue);
            if (alias.Length == 0) continue;
            if (normalized.Equals(alias, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 120);
            else if (alias.Length >= 4 && normalized.Contains(alias, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 90);
        }
        return best;
    }

    private static IEnumerable<string> ExpandBankAliases(IEnumerable<string> aliases)
    {
        var canonical = ReferenceEquals(aliases, DateHeaders) ? "Date"
            : ReferenceEquals(aliases, ValueDateHeaders) ? "ValueDate"
            : ReferenceEquals(aliases, DebitHeaders) ? "Debit"
            : ReferenceEquals(aliases, CreditHeaders) ? "Credit"
            : ReferenceEquals(aliases, AmountHeaders) ? "Amount"
            : ReferenceEquals(aliases, BalanceHeaders) ? "Balance"
            : ReferenceEquals(aliases, NarrationHeaders) ? "Narration"
            : ReferenceEquals(aliases, ReferenceHeaders) ? "Reference"
            : ReferenceEquals(aliases, DrCrHeaders) ? "DrCr"
            : string.Empty;
        return canonical.Length == 0
            ? aliases
            : ImportHeaderMappingRuntime.ExpandAliases(ImportHeaderMappingCatalog.Banking, canonical, aliases);
    }

    private static int ScoreDateColumn(ExcelColumnProfile profile)
    {
        var score = HeaderAffinity(profile.Header, DateHeaders);
        if (profile.Header.Contains("VALUE DATE", StringComparison.OrdinalIgnoreCase) ||
            profile.Header.Contains("VAL DATE", StringComparison.OrdinalIgnoreCase)) score -= 35;
        score += RatioScore(profile.DateLike, profile.NonEmpty, 70);
        score -= RatioScore(profile.NumericLike - profile.DateLike, profile.NonEmpty, 25);
        return score;
    }

    private static int ScoreValueDateColumn(ExcelColumnProfile profile) =>
        HeaderAffinity(profile.Header, ValueDateHeaders) + RatioScore(profile.DateLike, profile.NonEmpty, 55);

    private static int ScoreDebitColumn(ExcelColumnProfile profile) =>
        HeaderAffinity(profile.Header, DebitHeaders) + RatioScore(profile.NumericLike, profile.NonEmpty, 45) - HeaderAffinity(profile.Header, BalanceHeaders);

    private static int ScoreCreditColumn(ExcelColumnProfile profile) =>
        HeaderAffinity(profile.Header, CreditHeaders) + RatioScore(profile.NumericLike, profile.NonEmpty, 45) - HeaderAffinity(profile.Header, BalanceHeaders);

    private static int ScoreAmountColumn(ExcelColumnProfile profile) =>
        HeaderAffinity(profile.Header, AmountHeaders) + RatioScore(profile.NumericLike, profile.NonEmpty, 40) -
        HeaderAffinity(profile.Header, BalanceHeaders) - Math.Max(HeaderAffinity(profile.Header, DebitHeaders), HeaderAffinity(profile.Header, CreditHeaders));

    private static int ScoreBalanceColumn(ExcelColumnProfile profile) =>
        HeaderAffinity(profile.Header, BalanceHeaders) + RatioScore(profile.NumericLike, profile.NonEmpty, 45);

    private static int ScoreDrCrColumn(ExcelColumnProfile profile) =>
        HeaderAffinity(profile.Header, DrCrHeaders) + RatioScore(profile.DrCrLike, profile.NonEmpty, 60);

    private static int ScoreReferenceColumn(ExcelColumnProfile profile)
    {
        var score = HeaderAffinity(profile.Header, ReferenceHeaders) + RatioScore(profile.ReferenceLike, profile.NonEmpty, 35);
        if (HeaderAffinity(profile.Header, NarrationHeaders) > 0) score -= 75;
        return score;
    }

    private static int ScoreNarrationColumn(ExcelColumnProfile profile)
    {
        if (profile.NonEmpty == 0) return int.MinValue / 4;
        var explicitHeader = HeaderAffinity(profile.Header, NarrationHeaders);
        var negativeHeader = HeaderAffinity(profile.Header, NarrationNegativeHeaders);
        var mappedHeaderPenalty = Math.Max(
            Math.Max(HeaderAffinity(profile.Header, DateHeaders), HeaderAffinity(profile.Header, BalanceHeaders)),
            Math.Max(
                Math.Max(HeaderAffinity(profile.Header, DebitHeaders), HeaderAffinity(profile.Header, CreditHeaders)),
                Math.Max(HeaderAffinity(profile.Header, AmountHeaders), HeaderAffinity(profile.Header, ReferenceHeaders))));

        var score = explicitHeader
                    + RatioScore(profile.RichText, profile.NonEmpty, 80)
                    + RatioScore(profile.TextLike, profile.NonEmpty, 25)
                    + Math.Min(25, profile.DistinctText.Count)
                    - RatioScore(profile.Placeholders, profile.NonEmpty, 150)
                    - RatioScore(profile.DateLike, profile.NonEmpty, 80)
                    - RatioScore(profile.NumericLike, profile.NonEmpty, 70)
                    - RatioScore(profile.ReferenceLike, profile.NonEmpty, explicitHeader > 0 ? 10 : 55)
                    - negativeHeader
                    - mappedHeaderPenalty;
        return score;
    }

    private static int RatioScore(int numerator, int denominator, int weight)
    {
        if (denominator <= 0 || numerator <= 0) return 0;
        return (int)Math.Round(Math.Clamp((double)numerator / denominator, 0d, 1d) * weight, MidpointRounding.AwayFromZero);
    }

    private static int CountTransactionLikeRows(
        SheetData sheet,
        int headerRow,
        int dateColumn,
        int debitColumn,
        int creditColumn,
        int amountColumn,
        int balanceColumn,
        int drCrColumn)
    {
        var count = 0;
        var end = Math.Min(sheet.MaxRow, headerRow + ExcelProfileSampleRows);
        decimal? previousBalance = null;
        for (var row = headerRow + 1; row <= end; row++)
        {
            var cells = sheet.GetRow(row);
            if (!TryParseDate(Get(cells, dateColumn), sheet.Uses1904DateSystem, out _)) continue;
            var debit = Math.Abs(ParseDecimal(Get(cells, debitColumn)));
            var credit = Math.Abs(ParseDecimal(Get(cells, creditColumn)));
            var amount = ParseDecimal(Get(cells, amountColumn));
            var balance = TryParseBalance(Get(cells, balanceColumn));
            var drCr = NormalizeHeader(Get(cells, drCrColumn));
            var hasMovement = debit > 0.005m || credit > 0.005m || Math.Abs(amount) > 0.005m ||
                              (balance.HasValue && previousBalance.HasValue && Math.Abs(balance.Value - previousBalance.Value) > 0.005m) ||
                              drCr is "DR" or "CR" or "DEBIT" or "CREDIT" or "PAYMENT" or "RECEIPT";
            if (hasMovement) count++;
            previousBalance = balance ?? previousBalance;
        }
        return count;
    }

    private static IEnumerable<BankTransactionRecord> ParseTransactions(SheetAnalysis analysis, BankMetadata metadata, string sourceFile, CancellationToken cancellationToken)
    {
        decimal? previousBalance = null;
        BankTransactionRecord? pending = null;

        for (var row = analysis.HeaderRow + 1; row <= analysis.Sheet.MaxRow; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cells = analysis.Sheet.GetRow(row);
            var dateText = Get(cells, analysis.DateColumn);

            if (!TryParseDate(dateText, analysis.Sheet.Uses1904DateSystem, out var date))
            {
                if (pending is not null)
                {
                    var continuation = BuildNarration(cells, analysis, pending);
                    var hasTransactionAmount = Math.Abs(ParseDecimal(Get(cells, analysis.DebitColumn))) > 0.005m ||
                                               Math.Abs(ParseDecimal(Get(cells, analysis.CreditColumn))) > 0.005m ||
                                               Math.Abs(ParseDecimal(Get(cells, analysis.AmountColumn))) > 0.005m;
                    if (!hasTransactionAmount && continuation.Length > 0 && !LooksLikeStatementSummaryContinuation(continuation))
                    {
                        pending.Narration = MergeNarration(pending.Narration, continuation);
                        pending.NormalizedNarration = BankNarrationParser.Normalize(pending.Narration);
                    }

                    var continuationReference = BuildReference(cells, analysis);
                    if (string.IsNullOrWhiteSpace(pending.ReferenceNumber) && continuationReference.Length > 0)
                        pending.ReferenceNumber = continuationReference;
                    if (string.IsNullOrWhiteSpace(pending.ReferenceNumber) && pending.Narration.Length > 0)
                        pending.ReferenceNumber = BankNarrationParser.ExtractReference(pending.Narration);
                }
                continue;
            }

            if (pending is not null)
            {
                yield return pending;
                pending = null;
            }

            var debit = Math.Abs(ParseDecimal(Get(cells, analysis.DebitColumn)));
            var credit = Math.Abs(ParseDecimal(Get(cells, analysis.CreditColumn)));
            var amountText = Get(cells, analysis.AmountColumn);
            var amount = ParseDecimal(amountText);
            var drCr = NormalizeHeader(Get(cells, analysis.DrCrColumn));
            if (drCr.Length == 0) drCr = NormalizeHeader(amountText);
            var balance = TryParseBalance(Get(cells, analysis.BalanceColumn));

            if (debit == 0 && credit == 0 && amount != 0)
            {
                if (drCr.Contains("DR") || drCr.Contains("DEBIT") || drCr.Contains("PAYMENT")) debit = Math.Abs(amount);
                else if (drCr.Contains("CR") || drCr.Contains("CREDIT") || drCr.Contains("RECEIPT")) credit = Math.Abs(amount);
                else if (balance.HasValue && previousBalance.HasValue)
                {
                    if (balance.Value < previousBalance.Value) debit = Math.Abs(amount);
                    else if (balance.Value > previousBalance.Value) credit = Math.Abs(amount);
                }
                else if (amount < 0) debit = Math.Abs(amount);
                else credit = Math.Abs(amount);
            }
            if (debit == 0 && credit == 0 && balance.HasValue && previousBalance.HasValue)
            {
                var movement = balance.Value - previousBalance.Value;
                if (movement < -0.005m) debit = Math.Abs(movement);
                else if (movement > 0.005m) credit = movement;
            }
            if (debit == 0 && credit == 0)
            {
                previousBalance = balance ?? previousBalance;
                continue;
            }

            var valueDateForRow = TryParseDate(Get(cells, analysis.ValueDateColumn), analysis.Sheet.Uses1904DateSystem, out var parsedValueDate)
                ? parsedValueDate
                : (DateTime?)null;
            var explicitReference = BuildReference(cells, analysis);
            var draft = new BankTransactionRecord
            {
                TransactionDate = date,
                ValueDate = valueDateForRow,
                Debit = debit,
                Credit = credit,
                RunningBalance = balance,
                RunningBalanceSource = balance.HasValue ? "SOURCE" : string.Empty,
                ReferenceNumber = explicitReference
            };
            var narration = BuildNarration(cells, analysis, draft);
            var normalized = BankNarrationParser.Normalize(narration);
            var isContra = BankNarrationParser.IsCashContra(narration);
            var mode = isContra ? "Contra" : debit > 0 ? "Payment" : "Receipt";
            var ledger = isContra ? "Cash" : string.Empty;
            var mapping = isContra ? "Mapped" : "Mapping Required";

            pending = new BankTransactionRecord
            {
                TransactionDate = date,
                ValueDate = valueDateForRow,
                Narration = narration,
                NormalizedNarration = normalized,
                ReferenceNumber = explicitReference.Length > 0 ? explicitReference : BankNarrationParser.ExtractReference(narration),
                Debit = debit,
                Credit = credit,
                RunningBalance = balance,
                RunningBalanceSource = balance.HasValue ? "SOURCE" : string.Empty,
                Mode = mode,
                LedgerName = ledger,
                MappingStatus = mapping,
                RecordStatus = "Active",
                SourceFile = sourceFile,
                SourceSheet = analysis.Sheet.Name,
                SourceRow = row
            };
            previousBalance = balance ?? previousBalance;
        }

        if (pending is not null) yield return pending;
    }
    private static bool LooksLikeStatementSummaryContinuation(string value)
    {
        var text = NormalizeHeader(value);
        if (text.Length == 0) return true;
        return text.StartsWith("OPENING BALANCE", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("CLOSING BALANCE", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("TOTAL", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("BROUGHT FORWARD", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("CARRIED FORWARD", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("STATEMENT SUMMARY", StringComparison.OrdinalIgnoreCase) ||
               // PNB ONE legacy XLS footer/disclaimer text is emitted as blank-date rows.
               // It is statement metadata, not the narration of the final transaction.
               text.Contains("GENERATED THROUGH PNB ONE", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("UNLESS CONSTITUENT NOTIFIES THE BANK", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("COMPUTER GENERATED ENTRIES SHOWN IN THE STATEMENT", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("PLEASE DO NOT ACCEPT ANY MANUAL ENTRIES", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("CUSTOMERS ARE REQUESTED IN THEIR OWN INTEREST", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("ABBREVIATIONS ARE AS UNDER", StringComparison.OrdinalIgnoreCase);
    }

    private static string MergeNarration(string first, string continuation)
    {
        var left = ExcelWorksheetTrim(first);
        var right = ExcelWorksheetTrim(continuation);
        if (left.Length == 0) return right;
        if (right.Length == 0) return left;
        if (left.Contains(right, StringComparison.OrdinalIgnoreCase)) return left;
        return $"{left} {right}".Trim();
    }

    private static string BuildNarration(
        IReadOnlyDictionary<int,string> cells,
        SheetAnalysis analysis,
        BankTransactionRecord? mappedRow = null)
    {
        var columns = analysis.NarrationColumns.Count > 0
            ? analysis.NarrationColumns
            : analysis.NarrationColumn > 0
                ? new List<int> { analysis.NarrationColumn }
                : new List<int>();

        if (columns.Count == 0) return string.Empty;

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            var value = CleanNarrationCell(Get(cells, column));
            if (value.Length == 0) continue;
            if (LooksLikeStatementSummaryContinuation(value)) continue;
            if (mappedRow is not null && IsMappedTransactionEvidence(value, analysis.Sheet.Uses1904DateSystem, mappedRow)) continue;

            var key = NormalizeNarrationComparison(value);
            if (key.Length == 0 || !seen.Add(key)) continue;
            if (values.Any(existing => NarrationContainsEquivalent(existing, value))) continue;
            values.Add(value);
        }

        return ExcelWorksheetTrim(string.Join(" ", values));
    }

    private static string BuildReference(IReadOnlyDictionary<int,string> cells, SheetAnalysis analysis)
    {
        var columns = analysis.ReferenceColumns.Count > 0
            ? analysis.ReferenceColumns
            : analysis.ReferenceColumn > 0
                ? new List<int> { analysis.ReferenceColumn }
                : new List<int>();
        if (columns.Count == 0) return string.Empty;

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            var value = ExcelWorksheetTrim(Get(cells, column));
            if (value.Length == 0 || IsNarrationPlaceholder(value)) continue;
            var key = NormalizeNarrationComparison(value);
            if (key.Length == 0 || !seen.Add(key)) continue;
            values.Add(value);
        }
        return string.Join(" | ", values);
    }

    private static string CleanNarrationCell(string value)
    {
        var text = ExcelWorksheetTrim(value);
        if (text.Length == 0 || IsNarrationPlaceholder(text)) return string.Empty;
        return text;
    }

    private static bool IsNarrationPlaceholder(string value)
    {
        var text = ExcelWorksheetTrim(value);
        if (text.Length == 0) return true;
        return NarrationPlaceholders.Contains(text);
    }

    private static bool IsUsableNarration(string value)
    {
        var text = CleanNarrationCell(value);
        if (text.Length == 0 || LooksLikeStatementSummaryContinuation(text)) return false;
        return text.Any(char.IsLetter) || text.Length >= 4;
    }

    private static bool IsExcelStatementSource(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".xls" or ".xlsx" or ".xlsm" or ".xltx" or ".xltm";
    }

    private static bool IsMappedTransactionEvidence(string value, bool uses1904DateSystem, BankTransactionRecord row)
    {
        if (TryParseDate(value, uses1904DateSystem, out var date))
        {
            if (date.Date == row.TransactionDate.Date) return true;
            if (row.ValueDate.HasValue && date.Date == row.ValueDate.Value.Date) return true;
        }

        var numeric = TryParseNullableDecimal(value);
        if (numeric.HasValue)
        {
            var amount = Math.Abs(numeric.Value);
            if (row.Debit > 0.005m && Math.Abs(amount - row.Debit) <= 0.005m) return true;
            if (row.Credit > 0.005m && Math.Abs(amount - row.Credit) <= 0.005m) return true;
            if (row.RunningBalance.HasValue && Math.Abs(amount - Math.Abs(row.RunningBalance.Value)) <= 0.005m) return true;
        }

        if (!string.IsNullOrWhiteSpace(row.ReferenceNumber) &&
            NormalizeNarrationComparison(value).Equals(NormalizeNarrationComparison(row.ReferenceNumber), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    private static bool NarrationContainsEquivalent(string existing, string candidate)
    {
        var left = NormalizeNarrationComparison(existing);
        var right = NormalizeNarrationComparison(candidate);
        if (left.Length == 0 || right.Length == 0) return false;
        return left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
               (right.Length >= 6 && left.Contains(right, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeNarrationComparison(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return Regex.Replace(value.Trim(), @"\s+", " ").Trim(' ', '|', '-', ':', ';').ToUpperInvariant();
    }

    private static bool LooksReferenceOnly(string value)
    {
        var text = ExcelWorksheetTrim(value);
        if (text.Length == 0 || IsNarrationPlaceholder(text)) return false;
        var normalized = NormalizeHeader(text);
        if (normalized.Length == 0) return false;
        if (Regex.IsMatch(normalized, @"^(UTR|REF|REFERENCE|TXN|TRANSACTION|CHEQUE|CHQ)\s*(NO|NUMBER|ID)?\s*[A-Z0-9/-]+$", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(text, @"^\d{6,24}$")) return true;

        var compact = Regex.Replace(text, @"[^A-Za-z0-9]", string.Empty);
        var digits = compact.Count(char.IsDigit);
        var letters = compact.Count(char.IsLetter);
        return compact.Length is >= 10 and <= 40 && digits >= 6 && letters <= 5 && !text.Contains(' ');
    }

    private static string ExcelVbaTrim(string value) => (value ?? string.Empty).Trim(' ');

    private static string ExcelWorksheetTrim(string value)
    {
        // WorksheetFunction.Trim removes leading/trailing ASCII spaces and
        // collapses repeated ASCII spaces while preserving other characters.
        var text = ExcelVbaTrim(value);
        return Regex.Replace(text, " +", " ");
    }

    private static bool LooksLikePipelineGeneratedSheet(
        SheetData sheet,
        int headerRow,
        IReadOnlyDictionary<int, string> normalizedHeaders)
    {
        if (headerRow <= 1) return false;
        if (FindColumn(normalizedHeaders, NarrationHeaders) <= 0) return false;

        var hasRawData = normalizedHeaders.Values.Any(value =>
            string.Equals(value, "RAW DATA", StringComparison.OrdinalIgnoreCase));
        if (!hasRawData) return false;

        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var row = 1; row < headerRow; row++)
        {
            var key = ExcelWorksheetTrim(Get(sheet.GetRow(row), 1));
            if (key.Length > 0) labels.Add(key);
        }

        return labels.Contains("Source File") &&
               labels.Contains("Bank") &&
               labels.Contains("Format ID") &&
               labels.Contains("Transaction Count");
    }

    private static string GetPipelineMetadataValue(
        SheetData sheet,
        int headerRow,
        params string[] labels)
    {
        if (headerRow <= 1 || labels.Length == 0) return string.Empty;
        var wanted = new HashSet<string>(labels, StringComparer.OrdinalIgnoreCase);
        for (var row = 1; row < headerRow; row++)
        {
            var cells = sheet.GetRow(row);
            var key = ExcelWorksheetTrim(Get(cells, 1));
            if (!wanted.Contains(key)) continue;
            var value = ExcelWorksheetTrim(Get(cells, 2));
            if (value.Length > 0) return value;
        }
        return string.Empty;
    }

    private static void ApplyTrustedMetadataFallback(
        BankMetadata metadata,
        BankStatementImportRequest request,
        string filePath)
    {
        if (metadata.BankName.Equals("Unknown Bank", StringComparison.OrdinalIgnoreCase) &&
            request.MetadataFallbackBankNames.TryGetValue(filePath, out var bankName) &&
            !string.IsNullOrWhiteSpace(bankName))
            metadata.BankName = bankName.Trim();

        if (metadata.AccountNumber.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
            request.MetadataFallbackAccountNumbers.TryGetValue(filePath, out var accountNumber) &&
            !string.IsNullOrWhiteSpace(accountNumber))
            metadata.AccountNumber = accountNumber.Trim();

        if (string.IsNullOrWhiteSpace(metadata.AccountType) &&
            request.MetadataFallbackAccountTypes.TryGetValue(filePath, out var accountType) &&
            !string.IsNullOrWhiteSpace(accountType))
            metadata.AccountType = accountType.Trim();

        if (string.IsNullOrWhiteSpace(metadata.BranchName) &&
            request.MetadataFallbackBranchNames.TryGetValue(filePath, out var branchName) &&
            !string.IsNullOrWhiteSpace(branchName))
            metadata.BranchName = branchName.Trim();

        if (string.IsNullOrWhiteSpace(metadata.IfscCode) &&
            request.MetadataFallbackIfscCodes.TryGetValue(filePath, out var ifscCode) &&
            !string.IsNullOrWhiteSpace(ifscCode))
            metadata.IfscCode = ifscCode.Trim().ToUpperInvariant();
    }

    private static BankMetadata DetectMetadata(IReadOnlyList<SheetData> sheets, SheetAnalysis candidate, string fileName)
    {
        var metadataText = string.Join("\n", sheets.SelectMany(sheet => sheet.Rows.OrderBy(pair => pair.Key).Take(80).SelectMany(pair => pair.Value.OrderBy(cell => cell.Key).Select(cell => cell.Value))));
        var headerLimit = candidate.HeaderRow > 2 ? Math.Min(candidate.HeaderRow - 1, 50) : 50;
        var headerCells = candidate.Sheet.Rows
            .Where(pair => pair.Key >= 1 && pair.Key <= headerLimit)
            .OrderBy(pair => pair.Key)
            .SelectMany(pair => pair.Value.Where(cell => cell.Key is >= 1 and <= 8).OrderBy(cell => cell.Key).Select(cell => cell.Value))
            .ToList();
        var headerText = string.Join("\n", headerCells);
        var upperHeader = headerText.ToUpperInvariant();

        // The generated PDF workbook has explicit key/value metadata. Prefer it
        // over generic regex scanning so a statement date or other long number
        // can never be mistaken for the bank/account identity.
        var pipelineBank = candidate.IsPipelineGenerated
            ? GetPipelineMetadataValue(candidate.Sheet, candidate.HeaderRow, "Bank", "Bank Name")
            : string.Empty;
        var bankName = pipelineBank.Length > 0
            ? DetectBankName(pipelineBank.ToUpperInvariant(), fileName)
            : DetectBankName(upperHeader, fileName);
        if (pipelineBank.Length > 0 &&
            bankName.Equals("Unknown Bank", StringComparison.OrdinalIgnoreCase) &&
            !pipelineBank.Equals("Unknown Bank", StringComparison.OrdinalIgnoreCase))
        {
            // The pipeline already identified the bank. Preserve that explicit
            // name even when it is newer than this C# bank-name alias table.
            bankName = pipelineBank.Trim();
        }
        if (bankName.Equals("Unknown Bank", StringComparison.OrdinalIgnoreCase))
        {
            var ifsc = IfscRegex().Match(upperHeader);
            if (ifsc.Success) bankName = GetBankNameFromIfsc(ifsc.Value[..4]);
        }
        if (bankName.Equals("Unknown Bank", StringComparison.OrdinalIgnoreCase) && candidate.HeaderRow > 0)
        {
            var bodyEnd = Math.Min(candidate.Sheet.MaxRow, candidate.HeaderRow + 300);
            var KarnatakaHits = candidate.Sheet.Rows
                .Where(pair => pair.Key > candidate.HeaderRow && pair.Key <= bodyEnd)
                .SelectMany(pair => pair.Value.Where(cell => cell.Key is >= 1 and <= 8).Select(cell => cell.Value))
                .Count(value => value.Contains("KARBN", StringComparison.OrdinalIgnoreCase) || value.Contains("KARBH", StringComparison.OrdinalIgnoreCase));
            if (KarnatakaHits >= 3) bankName = "Karnataka Bank";
        }

        var pipelineAccount = candidate.IsPipelineGenerated
            ? GetPipelineMetadataValue(candidate.Sheet, candidate.HeaderRow, "Account Number", "Account No")
            : string.Empty;
        var accountNumber = pipelineAccount.Length > 0
            ? CleanAccount(pipelineAccount)
            : DetectAccountNumber(candidate.Sheet, headerLimit, fileName);

        var pipelineAccountType = candidate.IsPipelineGenerated
            ? GetPipelineMetadataValue(candidate.Sheet, candidate.HeaderRow, "Account Type")
            : string.Empty;
        if (pipelineAccountType.Length == 0 && candidate.IsPipelineGenerated)
            pipelineAccountType = GetPipelineMetadataValue(candidate.Sheet, candidate.HeaderRow, "Family");
        var accountType = NormalizePipelineAccountType(pipelineAccountType);
        if (accountType.Length == 0) accountType = DetectAccountType(metadataText.ToUpperInvariant());

        // Statement_Metadata parity: preserve the complete bank-master identity used by
        // modBankMetadata (Bank, Branch, IFSC, Account Type, OD Limit and Drawing Power).
        var branchName = candidate.IsPipelineGenerated
            ? GetPipelineMetadataValue(candidate.Sheet, candidate.HeaderRow, "Branch", "Branch Name")
            : string.Empty;
        if (branchName.Length == 0)
            branchName = DetectLabeledHeaderValue(candidate.Sheet, headerLimit, "BRANCH", "BRANCH NAME");

        var ifscCode = candidate.IsPipelineGenerated
            ? GetPipelineMetadataValue(candidate.Sheet, candidate.HeaderRow, "IFSC", "IFSC Code", "IFSC CODE")
            : string.Empty;
        if (ifscCode.Length == 0)
        {
            var ifscMatch = IfscRegex().Match(upperHeader);
            if (ifscMatch.Success) ifscCode = ifscMatch.Value;
        }
        ifscCode = ifscCode.Trim().ToUpperInvariant();

        var odLimitText = candidate.IsPipelineGenerated
            ? GetPipelineMetadataValue(candidate.Sheet, candidate.HeaderRow, "OD Limit", "OD LIMIT", "Overdraft Limit")
            : string.Empty;
        if (odLimitText.Length == 0)
            odLimitText = DetectLabeledHeaderValue(candidate.Sheet, headerLimit, "OD LIMIT", "OVERDRAFT LIMIT");

        var drawingPowerText = candidate.IsPipelineGenerated
            ? GetPipelineMetadataValue(candidate.Sheet, candidate.HeaderRow, "Drawing Power", "DRAWING POWER", "DP Limit", "DP LIMIT")
            : string.Empty;
        if (drawingPowerText.Length == 0)
            drawingPowerText = DetectLabeledHeaderValue(candidate.Sheet, headerLimit, "DRAWING POWER", "DP LIMIT");

        var period = DetectPeriod(metadataText);
        var pipelineStart = candidate.IsPipelineGenerated
            ? GetPipelineMetadataValue(candidate.Sheet, candidate.HeaderRow, "Statement Start Date")
            : string.Empty;
        var pipelineEnd = candidate.IsPipelineGenerated
            ? GetPipelineMetadataValue(candidate.Sheet, candidate.HeaderRow, "Statement End Date")
            : string.Empty;
        if (TryParseDate(pipelineStart, candidate.Sheet.Uses1904DateSystem, out var exactStart)) period.From = exactStart;
        if (TryParseDate(pipelineEnd, candidate.Sheet.Uses1904DateSystem, out var exactEnd)) period.To = exactEnd;

        return new BankMetadata
        {
            BankName=bankName,AccountNumber=accountNumber,AccountType=accountType,
            BranchName=branchName.Trim(),IfscCode=ifscCode,
            OdLimit=TryParseNullableDecimal(odLimitText),DrawingPower=TryParseNullableDecimal(drawingPowerText),
            PeriodFrom=period.From,PeriodTo=period.To,
            OpeningBalance=DetectLabeledBalance(metadataText, "OPENING"),ClosingBalance=DetectLabeledBalance(metadataText, "CLOSING")
        };
    }

    private static string DetectLabeledHeaderValue(SheetData sheet, int headerLimit, params string[] labels)
    {
        var wanted = labels.Select(NormalizeHeader).Where(value => value.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in sheet.Rows.Where(pair => pair.Key >= 1 && pair.Key <= headerLimit).OrderBy(pair => pair.Key))
        {
            var cells = row.Value.OrderBy(cell => cell.Key).ToArray();
            foreach (var cell in cells)
            {
                var raw = ExcelWorksheetTrim(cell.Value);
                var normalized = NormalizeHeader(raw);
                var exactLabel = wanted.Contains(normalized);
                var inlineLabel = wanted.FirstOrDefault(label => normalized.StartsWith(label + " ", StringComparison.OrdinalIgnoreCase));
                if (!exactLabel && inlineLabel is null) continue;

                if (inlineLabel is not null)
                {
                    var inlineRawIndex = raw.IndexOf(':');
                    if (inlineRawIndex >= 0 && inlineRawIndex + 1 < raw.Length)
                    {
                        var inlineValue = ExcelWorksheetTrim(raw[(inlineRawIndex + 1)..]);
                        if (inlineValue.Length > 0) return inlineValue;
                    }
                }

                for (var offset = 1; offset <= 3; offset++)
                {
                    if (!row.Value.TryGetValue(cell.Key + offset, out var candidate)) continue;
                    var value = ExcelWorksheetTrim(candidate);
                    if (value.Length > 0 && !wanted.Contains(NormalizeHeader(value))) return value;
                }
            }
        }
        return string.Empty;
    }

    private static string DetectBankName(string upper, string fileName)
    {
        var source = upper + " " + fileName.ToUpperInvariant();
        var mappings = new (string Key,string Name)[]
        {
            ("STATE BANK OF INDIA","State Bank of India"),("SBI","State Bank of India"),("CANARA","Canara Bank"),("HDFC","HDFC Bank"),
            ("ICICI","ICICI Bank"),("AXIS","Axis Bank"),("BANK OF BARODA","Bank of Baroda"),("BOB","Bank of Baroda"),
            ("UNION BANK","Union Bank of India"),("KARNATAKA BANK","Karnataka Bank"),("KOTAK","Kotak Mahindra Bank"),
            ("IDFC FIRST","IDFC FIRST Bank"),("IDFCFIRST","IDFC FIRST Bank"),("INDIAN BANK","Indian Bank"),
            ("PUNJAB NATIONAL","Punjab National Bank"),("UJJIVAN","Ujjivan Small Finance Bank"),("FEDERAL BANK","Federal Bank"),
            ("YES BANK","Yes Bank"),("BANK OF INDIA","Bank of India"),("CENTRAL BANK","Central Bank of India"),
            ("INDUSIND","IndusInd Bank"),("INDB","IndusInd Bank"),("IDBI","IDBI Bank"),("IBKL","IDBI Bank"),
            ("PUNJAB & SIND","Punjab & Sind Bank"),("PSIB","Punjab & Sind Bank"),("INDIAN OVERSEAS","Indian Overseas Bank"),("IOBA","Indian Overseas Bank"),
            ("UCO BANK","UCO Bank"),("UCBA","UCO Bank"),("BANDHAN","Bandhan Bank"),("BDBL","Bandhan Bank"),
            ("SBIN","State Bank of India"),("KARB","Karnataka Bank"),("CNRB","Canara Bank"),("UBIN","Union Bank of India"),
            ("FDRL","Federal Bank"),("BKID","Bank of India"),("IDIB","Indian Bank"),("KKBK","Kotak Mahindra Bank"),("UTIB","Axis Bank"),("PUNB","Punjab National Bank"),("BARB","Bank of Baroda")
        };
        return mappings.FirstOrDefault(item=>source.Contains(item.Key,StringComparison.OrdinalIgnoreCase)).Name ?? "Unknown Bank";
    }

    private static string GetBankNameFromIfsc(string prefix) => prefix.Trim().ToUpperInvariant() switch
    {
        "SBIN" => "State Bank of India", "KARB" => "Karnataka Bank", "HDFC" => "HDFC Bank", "ICIC" => "ICICI Bank",
        "KKBK" => "Kotak Mahindra Bank", "UTIB" => "Axis Bank", "PUNB" => "Punjab National Bank", "BARB" => "Bank of Baroda",
        "CNRB" => "Canara Bank", "UBIN" => "Union Bank of India", "INDB" => "IndusInd Bank", "IBKL" => "IDBI Bank",
        "FDRL" => "Federal Bank", "YESB" => "Yes Bank", "PSIB" => "Punjab & Sind Bank", "CBIN" => "Central Bank of India",
        "IDIB" => "Indian Bank", "IOBA" => "Indian Overseas Bank", "BKID" => "Bank of India", "UCBA" => "UCO Bank",
        "BDBL" => "Bandhan Bank", _ => "Unknown Bank"
    };

    private static string DetectAccountNumber(SheetData sheet, int headerLimit, string fileName)
    {
        var rows = sheet.Rows.Where(pair => pair.Key >= 1 && pair.Key <= headerLimit).OrderBy(pair => pair.Key);
        foreach (var row in rows)
        {
            foreach (var cell in row.Value.Where(cell => cell.Key is >= 1 and <= 8).OrderBy(cell => cell.Key))
            {
                var label = cell.Value.ToUpperInvariant();
                if (!label.Contains("ACCOUNT", StringComparison.OrdinalIgnoreCase) &&
                    !label.Contains("A/C", StringComparison.OrdinalIgnoreCase) &&
                    !label.Contains("AC NO", StringComparison.OrdinalIgnoreCase) &&
                    !label.Contains("ACNO", StringComparison.OrdinalIgnoreCase)) continue;
                for (var offset = 0; offset <= 4 && cell.Key + offset <= 8; offset++)
                {
                    if (!row.Value.TryGetValue(cell.Key + offset, out var candidate)) continue;
                    var labeled = AccountCandidateRegex().Match(candidate);
                    if (!labeled.Success) continue;
                    var cleaned = CleanAccount(labeled.Value);
                    if (cleaned.Length >= 4) return cleaned;
                }
            }
        }

        foreach (var row in sheet.Rows.Where(pair => pair.Key >= 1 && pair.Key <= headerLimit).OrderBy(pair => pair.Key))
        foreach (var cell in row.Value.Where(cell => cell.Key is >= 1 and <= 8).OrderBy(cell => cell.Key))
        {
            var match = HeaderAccountNumberRegex().Match(cell.Value);
            if (!match.Success) continue;
            var cleaned = CleanAccount(match.Value);
            if (cleaned.Length >= 4) return cleaned;
        }

        var fileMatch = LongNumberRegex().Match(fileName);
        return fileMatch.Success ? CleanAccount(fileMatch.Value) : "Unknown";
    }

    private static string CleanAccount(string value)
    {
        var retained = new string(value.Where(character=>char.IsDigit(character) || character is 'X' or 'x' or '*').ToArray());
        retained = retained.Replace("*",string.Empty).Replace("X",string.Empty,StringComparison.OrdinalIgnoreCase);
        return retained.Trim();
    }

    private static string NormalizePipelineAccountType(string value)
    {
        var upper = ExcelWorksheetTrim(value).ToUpperInvariant();
        return upper switch
        {
            "SB" or "SAVINGS" or "SAVING" or "SAVINGS ACCOUNT" => "SB",
            "CA" or "CURRENT" or "CURRENT ACCOUNT" => "CA",
            "OD" or "OVERDRAFT" or "OVERDRAFT ACCOUNT" => "OD",
            "CC" or "CASH CREDIT" or "CASH CREDIT ACCOUNT" => "CC",
            _ => string.Empty
        };
    }

    private static string DetectAccountType(string upper)
    {
        if (upper.Contains("CASH CREDIT") || upper.Contains("OCC ACCOUNT") || upper.Contains("CC ACCOUNT")) return "CC";
        if (upper.Contains("OVERDRAFT") || upper.Contains("OD ACCOUNT")) return "OD";
        if (upper.Contains("SAVINGS") || upper.Contains("SAVING BANK") || upper.Contains("SB ACCOUNT")) return "SB";
        if (upper.Contains("CURRENT ACCOUNT") || upper.Contains("CA ACCOUNT")) return "CA";
        return string.Empty;
    }

    private static (DateTime? From,DateTime? To) DetectPeriod(string text)
    {
        var match = PeriodRegex().Match(text);
        if (!match.Success) return (null,null);
        return (TryParseDate(match.Groups[1].Value,false,out var from)?from:null,TryParseDate(match.Groups[2].Value,false,out var to)?to:null);
    }

    private static decimal? DetectLabeledBalance(string text, string label)
    {
        var regex = new Regex($@"{label}\s+(?:BALANCE|BAL)\s*[:\-]?\s*(?:INR|RS\.?|₹)?\s*([\(\)\-0-9,\.]+\s*(?:DR|CR)?)",RegexOptions.IgnoreCase);
        var match = regex.Match(text);
        return match.Success ? TryParseBalance(match.Groups[1].Value) : null;
    }

    private void EnsureBankLedger(BankStatementImportRequest request, BankAccountRecord account)
    {
        masterRepository.Upsert(request.ClientId,request.FinancialYear,
        [
            new MasterRecord { ClientId=request.ClientId,FinancialYear=request.FinancialYear,Name=account.LedgerName,Parent="Bank Accounts",Type="Ledger",Source="Bank Statement" }
        ]);
    }

    private void EnsureCashLedger(BankStatementImportRequest request)
    {
        masterRepository.Upsert(request.ClientId, request.FinancialYear,
        [
            new MasterRecord { ClientId=request.ClientId,FinancialYear=request.FinancialYear,Name="Cash",Parent="Cash-in-Hand",Type="Ledger",Source="Bank Statement Contra" }
        ]);
    }

    private static string BuildBankLedgerName(BankMetadata metadata)
    {
        var bank = metadata.BankName.Trim();
        var type = metadata.AccountType.Trim();
        var account = metadata.AccountNumber.Trim();
        return string.Join(" ",new[]{bank,type}.Where(value=>value.Length>0))+(account.Length>0?$" - {account}":string.Empty);
    }

    public static string BuildAutoImportTransactionIdentity(BankTransactionRecord transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        // Auto Import fallback identity is intentionally transaction-level. Bank display
        // metadata, account name/number and source file/path are NOT part of this key.
        var narration = BankNarrationParser.Normalize(transaction.Narration ?? string.Empty).Trim().ToUpperInvariant();
        var reference = (transaction.ReferenceNumber ?? string.Empty).Trim().ToUpperInvariant();
        var builder = new StringBuilder(FormattableString.Invariant(
            $"{transaction.TransactionDate:yyyyMMdd}|{narration}|{transaction.Debit:0.00}|{transaction.Credit:0.00}"));
        if (reference.Length > 0) builder.Append("|REF:").Append(reference);
        if (BankBalanceAuthority.IsIndependentRunningBalanceEvidence(transaction))
            builder.Append("|BAL:").Append(transaction.RunningBalance!.Value.ToString("0.00", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static string BuildFingerprint(BankTransactionRecord transaction, string bankMetadata)
    {
        // CR3: Date/Narration/Debit/Credit/Bank alone is not a transaction identity. PNB can
        // legitimately contain two BY CASH rows for the same date and amount. Preserve those
        // rows by adding the Bank transaction/reference number and independent running balance.
        // Existing rows remain compatible because Import() rebuilds this canonical identity from
        // their persisted ReferenceNumber/RunningBalance instead of trusting only the legacy hash.
        var baseIdentity = FormattableString.Invariant(
            $"{transaction.TransactionDate:yyyyMMdd}|{transaction.Narration.Trim().ToUpperInvariant()}|{transaction.Debit:0.00}|{transaction.Credit:0.00}|{bankMetadata.Trim().ToUpperInvariant()}");
        var reference = (transaction.ReferenceNumber ?? string.Empty).Trim().ToUpperInvariant();
        var builder = new StringBuilder(baseIdentity);
        if (reference.Length > 0) builder.Append("|REF:").Append(reference);
        if (BankBalanceAuthority.IsIndependentRunningBalanceEvidence(transaction))
            builder.Append("|BAL:").Append(transaction.RunningBalance!.Value.ToString("0.00", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static IEnumerable<string> BuildFingerprintAliases(BankTransactionRecord transaction, string bankMetadata)
    {
        var stored = (transaction.Fingerprint ?? string.Empty).Trim();
        var canonical = BuildFingerprint(transaction, bankMetadata);
        if (stored.Length > 0) yield return stored;
        if (canonical.Length > 0 && !canonical.Equals(stored, StringComparison.OrdinalIgnoreCase)) yield return canonical;
    }

    private static string ComputeWorkbookCacheHash(OpenXmlWorkbookData workbook)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        static void Append(IncrementalHash target, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            target.AppendData(bytes);
            target.AppendData([0]);
        }

        Append(hash, workbook.Uses1904DateSystem ? "1904" : "1900");
        foreach (var sheet in workbook.Worksheets
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            Append(hash, sheet.Key);
            Append(hash, sheet.Value.State ?? string.Empty);
            foreach (var row in sheet.Value.Rows.OrderBy(pair => pair.Key))
            {
                Append(hash, row.Key.ToString(CultureInfo.InvariantCulture));
                foreach (var cell in row.Value.OrderBy(pair => pair.Key))
                {
                    Append(hash, cell.Key.ToString(CultureInfo.InvariantCulture));
                    Append(hash, cell.Value ?? string.Empty);
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ComputeHash(string path)
    {
        using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ShortHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    private static string ConvertLegacyXls(string sourcePath, string password) =>
        ConvertWorkbookWithExcel(sourcePath, password, 0);

    private static string ConvertLegacyXlsWithRecovery(string sourcePath, string password)
    {
        Exception? lastError = null;
        foreach (var corruptLoad in new[] { 1, 2, 0 })
        {
            try { return ConvertWorkbookWithExcel(sourcePath, password, corruptLoad); }
            catch (Exception exception) { lastError = exception; }
        }
        throw new InvalidDataException("Legacy workbook repair, data extraction, and final normal-load recovery all failed.", lastError);
    }

    private static string ConvertWorkbookWithExcel(string sourcePath, string password, int corruptLoad)
    {
        try
        {
            return ExcelComWorkbookMaterializer.ConvertToOpenXml(
                sourcePath,
                password ?? string.Empty,
                corruptLoad,
                purpose: "Bank");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                "Microsoft Excel could not recover or read this Bank Statement.",
                exception);
        }
    }

    private static int FindColumn(IReadOnlyDictionary<int,string> headers, IEnumerable<string> aliases)
    {
        var expanded = ExpandBankAliases(aliases).Select(ImportHeaderMappingCatalog.NormalizeHeader).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var alias in expanded)
        {
            var exact = headers.FirstOrDefault(pair => ImportHeaderMappingCatalog.NormalizeHeader(pair.Value).Equals(alias, StringComparison.OrdinalIgnoreCase));
            if (exact.Key > 0) return exact.Key;
        }
        foreach (var alias in expanded.Where(value => value.Length >= 4))
        {
            var partial = headers.FirstOrDefault(pair => ImportHeaderMappingCatalog.NormalizeHeader(pair.Value).Contains(alias, StringComparison.OrdinalIgnoreCase));
            if (partial.Key > 0) return partial.Key;
        }
        return 0;
    }

    private static string NormalizeHeader(string value) => Regex.Replace(value.Trim().ToUpperInvariant(),@"[^A-Z0-9]+"," ").Trim();
    private static string Get(IReadOnlyDictionary<int,string> row,int column) => column>0 && row.TryGetValue(column,out var value)?value:string.Empty;

    private static decimal ParseDecimal(string value) => TryParseNullableDecimal(value) ?? 0m;
    private static decimal? TryParseNullableDecimal(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text=value.Trim().Replace("₹",string.Empty).Replace("INR",string.Empty,StringComparison.OrdinalIgnoreCase).Replace("Rs.",string.Empty,StringComparison.OrdinalIgnoreCase).Replace(",",string.Empty).Trim();
        var negative = (text.StartsWith('(') && text.EndsWith(')')) || text.StartsWith('-') || text.EndsWith('-');
        text=text.Trim('(',')').TrimEnd('-');
        // Bank exports commonly write balances as "5093.59 Cr." / "1250.00 Dr.".
        // Remove the balance nature plus its punctuation before decimal parsing.
        text=Regex.Replace(text, @"(?i)\b(?:CR|DR|CREDIT|DEBIT)\b\s*[\.:;]*\s*$", string.Empty).Trim();
        text=text.TrimEnd('.', ':', ';').Trim();
        if (!decimal.TryParse(text,NumberStyles.Any,CultureInfo.InvariantCulture,out var result) && !decimal.TryParse(text,NumberStyles.Any,CultureInfo.CurrentCulture,out result)) return null;
        return negative ? -Math.Abs(result) : result;
    }

    private static decimal? TryParseBalance(string value)
    {
        var parsed = TryParseNullableDecimal(value);
        if (!parsed.HasValue) return null;
        var normalized = NormalizeHeader(value);
        if (normalized.EndsWith("DR", StringComparison.OrdinalIgnoreCase) || normalized.Contains(" DEBIT", StringComparison.OrdinalIgnoreCase))
            return -Math.Abs(parsed.Value);
        if (normalized.EndsWith("CR", StringComparison.OrdinalIgnoreCase) || normalized.Contains(" CREDIT", StringComparison.OrdinalIgnoreCase))
            return Math.Abs(parsed.Value);
        return parsed;
    }

    private static bool TryParseDate(string text, bool uses1904, out DateTime date) =>
        BankStatementDateClassification.TryParseImportedDate(text, uses1904, out date);

    private readonly record struct DetectedCoverage(DateTime From, DateTime To, bool MetadataBalancesTrusted);

    private static DetectedCoverage ResolveDetectedCoverage(
        BankMetadata metadata,
        IReadOnlyList<BankTransactionRecord> parsedRows,
        string financialYear)
    {
        // CR4 date authority: persisted statement coverage is always the minimum/maximum
        // ORIGINAL Bank Transaction Date parsed from the source rows. A printed/header period
        // may certify source OB/CB provenance, but it must never replace transaction chronology
        // or expand a quiet monthly file to artificial first/last calendar dates.
        var parsedFrom = parsedRows.Min(item => item.TransactionDate).Date;
        var parsedTo = parsedRows.Max(item => item.TransactionDate).Date;
        var (financialYearStart, financialYearEnd) = GetFinancialYearBounds(financialYear);
        var fyFrom = financialYearStart?.Date ?? parsedFrom;
        var fyTo = financialYearEnd?.Date ?? parsedTo;

        DateTime ClipFrom(DateTime value) => value.Date < fyFrom ? fyFrom : value.Date;
        DateTime ClipTo(DateTime value) => value.Date > fyTo ? fyTo : value.Date;

        var metadataFrom = metadata.PeriodFrom?.Date;
        var metadataTo = metadata.PeriodTo?.Date;
        var metadataValid = metadataFrom.HasValue && metadataTo.HasValue && metadataFrom.Value <= metadataTo.Value;
        var metadataBalancesTrusted = !metadataValid;
        if (metadataValid)
        {
            var clippedMetadataFrom = ClipFrom(metadataFrom!.Value);
            var clippedMetadataTo = ClipTo(metadataTo!.Value);
            var metadataSpanDays = (metadataTo.Value - metadataFrom.Value).TotalDays + 1d;
            var parsedSpanDays = (parsedTo - parsedFrom).TotalDays + 1d;
            metadataBalancesTrusted = clippedMetadataFrom <= parsedFrom &&
                                      clippedMetadataTo >= parsedTo &&
                                      (metadataSpanDays <= 62d ||
                                       (parsedSpanDays > 62d && parsedSpanDays >= metadataSpanDays * 0.70d));
        }

        return new DetectedCoverage(ClipFrom(parsedFrom), ClipTo(parsedTo), metadataBalancesTrusted);
    }

    private static bool IsInsideFinancialYear(DateTime date, string financialYear) =>
        BankStatementDateClassification.IsInsideFinancialYear(date, financialYear);

    private static (DateTime? Start, DateTime? End) GetFinancialYearBounds(string financialYear) =>
        BankStatementDateClassification.FinancialYearBounds(financialYear);

    private static bool SameSourcePath(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try
        {
            return Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return first.Trim().Equals(second.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static DateTime? Min(DateTime? left,DateTime? right) => !left.HasValue?right:!right.HasValue?left:left.Value<=right.Value?left:right;
    private static DateTime? Max(DateTime? left,DateTime? right) => !left.HasValue?right:!right.HasValue?left:left.Value>=right.Value?left:right;

    private sealed record SheetData(string Name,Dictionary<int,Dictionary<int,string>> Rows,int MaxRow,bool Uses1904DateSystem)
    {
        public IReadOnlyDictionary<int,string> GetRow(int row) => Rows.TryGetValue(row,out var values)?values:new Dictionary<int,string>();
    }
    private sealed class SheetAnalysis(SheetData sheet)
    {
        public SheetData Sheet { get; } = sheet;
        public int HeaderRow { get; set; }
        public int DateColumn { get; set; }
        public int ValueDateColumn { get; set; }
        public int DebitColumn { get; set; }
        public int CreditColumn { get; set; }
        public int AmountColumn { get; set; }
        public int BalanceColumn { get; set; }
        public int NarrationColumn { get; set; }
        public List<int> NarrationColumns { get; set; } = [];
        public int ReferenceColumn { get; set; }
        public List<int> ReferenceColumns { get; set; } = [];
        public int DrCrColumn { get; set; }
        public int Score { get; set; }
        public bool IsPipelineGenerated { get; set; }
    }

    private sealed class ExcelColumnProfile(int column, string header)
    {
        public int Column { get; } = column;
        public string Header { get; } = header;
        public int NonEmpty { get; set; }
        public int Placeholders { get; set; }
        public int DateLike { get; set; }
        public int NumericLike { get; set; }
        public int DrCrLike { get; set; }
        public int TextLike { get; set; }
        public int RichText { get; set; }
        public int ReferenceLike { get; set; }
        public HashSet<string> DistinctText { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class BankMetadata
    {
        public string BankName{get;set;}=string.Empty;public string AccountNumber{get;set;}=string.Empty;public string AccountType{get;set;}=string.Empty;public string BranchName{get;set;}=string.Empty;public string IfscCode{get;set;}=string.Empty;public decimal? OdLimit{get;set;}public decimal? DrawingPower{get;set;}public DateTime? PeriodFrom{get;set;}public DateTime? PeriodTo{get;set;}public decimal? OpeningBalance{get;set;}public decimal? ClosingBalance{get;set;}
    }

    [GeneratedRegex(@"(?:A/C|AC|ACCOUNT)(?:\s+(?:NO|NUMBER|NUM))?\s*[:\-]?\s*([Xx*0-9][Xx*0-9\s\-/]{3,30})",RegexOptions.IgnoreCase)] private static partial Regex AccountLabelRegex();
    [GeneratedRegex(@"[Xx*0-9][Xx*0-9\s\-/]{3,30}")] private static partial Regex AccountCandidateRegex();
    [GeneratedRegex(@"(?<!\d)\d{10,20}(?!\d)")] private static partial Regex HeaderAccountNumberRegex();
    [GeneratedRegex(@"(?<!\d)\d{8,24}(?!\d)")] private static partial Regex LongNumberRegex();
    [GeneratedRegex(@"\b[A-Z]{4}0[A-Z0-9]{6}\b", RegexOptions.IgnoreCase)] private static partial Regex IfscRegex();
    [GeneratedRegex(@"(?:STATEMENT\s+PERIOD|PERIOD|FROM)\s*[:\-]?\s*([0-9A-Za-z./-]{6,15})\s*(?:TO|TILL|-)\s*([0-9A-Za-z./-]{6,15})",RegexOptions.IgnoreCase)] private static partial Regex PeriodRegex();
}
