using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Xml;
using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Application.Journals;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Spreadsheets;
using AccountingAutomationHub.Infrastructure.Tally;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Client review export/import bridge.
///
/// The visible workbook contains only client/accounting sheets that are useful for
/// review. Internal Auto Import history, Action Required history and Import Activity
/// audit sheets are deliberately excluded. A hidden metadata sheet binds the workbook
/// to one Client/FY so edits cannot be applied to the wrong workspace.
///
/// The workbook is application data only. It never reads, writes, copies, backs up or
/// modifies the external Python runtime.
/// </summary>
public sealed class ClientDiagnosticExportService
{
    public const string ReviewWorkbookPrefix = "AAH_REVIEW_";
    public const string ReviewCsvPrefix = "AAH_REVIEW_BANK_";
    public const string CashEntryWorkbookPrefix = "AAH_CASH_ENTRY_";
    public const string FinancialStatementsExportPrefix = "AAH_FINANCIAL_STATEMENTS_";
    private const string MetadataSheet = "__AAH_META";
    private const string WorkbookKind = "Accounting Automation Hub Client Review";
    private const string SchemaVersion = "7";

    private readonly IBankingRepository _banking;
    private readonly IPurchaseRecordRepository _purchase;
    private readonly ISalesRecordRepository _sales;
    private readonly IJournalRepository _journal;
    private readonly IMasterRepository _masters;
    private readonly IStockMasterRepository _stockMasters;
    private readonly BankLedgerMappingService _ledgerMappingService;
    private readonly BackgroundAutomationService _automationService;
    private readonly SqliteTallyRepository _tallyRepository;
    private readonly FlexibleWorkbookReader _reader = new();

    public ClientDiagnosticExportService(
        IBankingRepository banking,
        IPurchaseRecordRepository purchase,
        ISalesRecordRepository sales,
        IJournalRepository journal,
        IMasterRepository masters,
        IStockMasterRepository stockMasters,
        BankLedgerMappingService ledgerMappingService,
        BackgroundAutomationService automationService,
        SqliteTallyRepository tallyRepository)
    {
        _banking = banking;
        _purchase = purchase;
        _sales = sales;
        _journal = journal;
        _masters = masters;
        _stockMasters = stockMasters;
        _ledgerMappingService = ledgerMappingService;
        _automationService = automationService;
        _tallyRepository = tallyRepository;
    }

    public string ExportExcelAuto(ClientProfile client, bool overwriteExisting)
    {
        var folder = GetExportFolder(client);
        Directory.CreateDirectory(folder);
        var path = BuildExportPath(folder, $"{ReviewWorkbookPrefix}{SafeName(client.ClientCode)}_{SafeName(client.FinancialYear)}", ".xlsx", overwriteExisting);
        ExportExcel(client, path);
        return path;
    }

    public string ExportCsvAuto(ClientProfile client, bool overwriteExisting)
    {
        var folder = GetExportFolder(client);
        Directory.CreateDirectory(folder);
        var path = BuildExportPath(folder, $"{ReviewCsvPrefix}{SafeName(client.ClientCode)}_{SafeName(client.FinancialYear)}", ".csv", overwriteExisting);
        ExportCsv(client, path);
        return path;
    }

    public string GetExportFolder(ClientProfile client)
    {
        var linked = _automationService.GetFolders()
            .Where(folder => folder.ClientId == client.Id &&
                             folder.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(folder => Directory.Exists(folder.FolderPath))
            .ThenBy(folder => folder.FolderPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (linked is null || string.IsNullOrWhiteSpace(linked.FolderPath))
            throw new InvalidOperationException("This client/FY has no linked Automation Folder. Link the client folder first.");
        if (!Directory.Exists(linked.FolderPath))
            throw new DirectoryNotFoundException($"The linked Automation Folder is unavailable: {linked.FolderPath}");
        return linked.FolderPath;
    }

    public string? FindLatestReviewWorkbook(ClientProfile client)
    {
        var folder = GetExportFolder(client);
        if (!Directory.Exists(folder)) return null;
        return Directory.EnumerateFiles(folder, $"{ReviewWorkbookPrefix}*.xlsx", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.CreationTimeUtc)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    public void ExportExcel(ClientProfile client, string path)
    {
        SimpleXlsxWriter.Write(path, BuildSheets(client));
        InitializeReviewSyncState(path);
    }

    public void ExportCsv(ClientProfile client, string path)
    {
        var accounts = Safe(() => _banking.GetAccounts(client.Id, client.FinancialYear), []);
        var accountNames = accounts.ToDictionary(row => row.Id, row => row.AuthorityName);
        var rows = Safe(() => _banking.GetTransactions(client.Id, client.FinancialYear, true), []);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        WriteCsvRow(writer, ["__TransactionId", "Date", "Narration", "Debit", "Credit", "Mode", "Ledger", "Bank", "Mapping Status", "Reference", "Running Balance", "Record Status", "Source File", "Source Sheet", "Source Row"]);
        foreach (var row in rows.OrderBy(row => row.TransactionDate).ThenBy(row => row.Id))
        {
            WriteCsvRow(writer,
            [
                row.Id.ToString(CultureInfo.InvariantCulture),
                row.TransactionDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                row.Narration,
                row.Debit.ToString("0.00", CultureInfo.InvariantCulture),
                row.Credit.ToString("0.00", CultureInfo.InvariantCulture),
                row.Mode,
                row.LedgerName,
                accountNames.TryGetValue(row.BankAccountId, out var bank) ? bank : row.BankAccountName,
                row.MappingStatus,
                row.ReferenceNumber,
                row.RunningBalance?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
                row.RecordStatus,
                row.SourceFile,
                row.SourceSheet,
                row.SourceRow.ToString(CultureInfo.InvariantCulture)
            ]);
        }
    }

    public ClientReviewImportResult ImportLatestExcelChanges(ClientProfile client)
    {
        var path = FindLatestReviewWorkbook(client);
        if (string.IsNullOrWhiteSpace(path))
            return new ClientReviewImportResult(0, 0, 0, "No auto-saved client review workbook exists yet. Generate Excel first.", string.Empty);
        return ImportExcelChanges(client, path);
    }

    public ClientReviewImportResult ImportExcelChanges(ClientProfile client, string path)
    {
        if (!File.Exists(path))
            return new ClientReviewImportResult(0, 0, 1, "The review workbook was not found.", path);

        try
        {
            var workbook = _reader.Read(path, CancellationToken.None);
            ValidateWorkbookIdentity(client, workbook);
            var syncState = LoadReviewSyncState(path);
            var changed = 0;
            var skipped = 0;
            var errors = new List<string>();

            changed += ImportBankStatement(client, workbook, syncState, ref skipped, errors);
            changed += ImportLedgerMapping(client, workbook, syncState, ref skipped, errors);
            changed += ImportLedgerMaster(client, workbook, syncState, ref skipped, errors);
            changed += ImportUnitMasters(client, workbook, syncState, ref skipped, errors);
            changed += ImportStockMaster(client, workbook, syncState, ref skipped, errors);
            changed += ImportPurchase(client, workbook, syncState, ref skipped, errors);
            changed += ImportSales(client, workbook, syncState, ref skipped, errors);
            changed += ImportJournal(client, workbook, syncState, ref skipped, errors);
            changed += ImportCashEntry(client, workbook, syncState, ref skipped, errors);

            // Only advance the delta baseline after a completely clean pass. If one
            // edited row fails validation OR can no longer be resolved safely, keep
            // the old baseline so that edit remains pending for the next save/retry.
            if (errors.Count == 0 && skipped == 0)
            {
                if (changed > 0) syncState.HasImportedChanges = true;
                CaptureReviewSyncState(workbook, syncState);
                SaveReviewSyncState(path, syncState);
            }

            var message = errors.Count == 0 && skipped == 0
                ? $"Imported {changed:N0} changed row(s) from the client review workbook."
                : errors.Count > 0
                    ? $"Imported {changed:N0} changed row(s); {errors.Count:N0} row(s) could not be applied and {skipped:N0} row(s) were unresolved. First error: {errors[0]}"
                    : $"Imported {changed:N0} changed row(s); {skipped:N0} edited row(s) could not be resolved safely and remain pending.";
            return new ClientReviewImportResult(changed, skipped, errors.Count, message, path);
        }
        catch (Exception exception)
        {
            return new ClientReviewImportResult(0, 0, 1, $"Client review import failed: {exception.Message}", path);
        }
    }

    public static bool IsReservedReviewExportPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith(ReviewWorkbookPrefix, StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith(ReviewCsvPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every Hub-generated workbook/CSV that can live beside accounting source files.
    /// Review workbooks have their own delta-sync route; legacy Diagnostic/BankStatement
    /// exports are read-only troubleshooting artifacts and must be ignored completely by
    /// normal Auto Import and Action Required.
    /// </summary>
    public static bool IsHubGeneratedExportPath(string path)
    {
        if (IsReservedReviewExportPath(path)) return true;
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith(CashEntryWorkbookPrefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.StartsWith(FinancialStatementsExportPrefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (!fileName.StartsWith("AAH_", StringComparison.OrdinalIgnoreCase)) return false;

        var extension = Path.GetExtension(fileName);
        if (!extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            return false;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.EndsWith("_Diagnostic", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith("_BankStatement", StringComparison.OrdinalIgnoreCase) ||
               stem.Contains("_Diagnostic_", StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<DiagnosticSheet> BuildSheets(ClientProfile client)
    {
        var result = new List<DiagnosticSheet>
        {
            new(MetadataSheet,
            [
                ["Key", "Value"],
                ["Kind", WorkbookKind],
                ["SchemaVersion", SchemaVersion],
                ["ClientId", client.Id.ToString(CultureInfo.InvariantCulture)],
                ["ClientCode", client.ClientCode],
                ["FinancialYear", client.FinancialYear],
                ["ExportedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)]
            ], Hidden: true),
            new("Client",
            [
                ["Field", "Value"],
                ["Client Code", client.ClientCode],
                ["Client Name", client.ClientName],
                ["Trade Name", client.TradeName],
                ["Legal Name", client.LegalName],
                ["GSTIN", client.Gstin],
                ["Additional GSTINs", string.Join(", ", client.AdditionalGstins ?? [])],
                ["Financial Year", client.FinancialYear],
                ["Review workbook", "Edit application data in the module sheets and save the workbook. The Hub applies only changed rows automatically. Do not change __ID columns."]
            ])
        };

        var accounts = Safe(() => _banking.GetAccounts(client.Id, client.FinancialYear), []);
        var accountNames = accounts.ToDictionary(row => row.Id, row => row.AuthorityName);
        var reviewTransactions = Safe(() => _banking.GetTransactions(client.Id, client.FinancialYear, true), []).ToArray();
        var activeReviewTransactionsById = reviewTransactions
            .Where(row => row.Id > 0 && !row.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => row.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var bankRows = new List<IReadOnlyList<string>>
        {
            new[] { "__TransactionId", "Date", "Narration", "Debit", "Credit", "Mode", "Ledger", "Bank", "Mapping Status", "Reference", "Running Balance", "Record Status", "Source File", "Source Sheet", "Source Row" }
        };
        foreach (var row in reviewTransactions.OrderBy(row => row.TransactionDate).ThenBy(row => row.Id))
            bankRows.Add([
                row.Id.ToString(CultureInfo.InvariantCulture), Date(row.TransactionDate), row.Narration,
                Num(row.Debit), Num(row.Credit), row.Mode, row.LedgerName,
                accountNames.TryGetValue(row.BankAccountId, out var bank) ? bank : row.BankAccountName,
                row.MappingStatus, row.ReferenceNumber, row.RunningBalance?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
                row.RecordStatus, row.SourceFile, row.SourceSheet, row.SourceRow.ToString(CultureInfo.InvariantCulture)]);
        result.Add(new DiagnosticSheet("Bank Statement", bankRows, HiddenLeadingColumns: 1));

        var archivedInterbankCount = reviewTransactions.Count(row => row.RecordStatus.Equals("Archived Interbank Counterpart", StringComparison.OrdinalIgnoreCase));
        var currentBankCount = reviewTransactions.Count(row => !row.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase));
        var otherArchivedBankCount = Math.Max(0, reviewTransactions.Length - currentBankCount - archivedInterbankCount);
        result.Add(new DiagnosticSheet("Bank Review Summary",
        [
            ["Metric", "Count", "Meaning"],
            ["Retained Bank rows", reviewTransactions.Length.ToString(CultureInfo.InvariantCulture), "All current + retained historical/inter-bank counterpart rows in the review workbook"],
            ["Current active Bank transactions", currentBankCount.ToString(CultureInfo.InvariantCulture), "Current accounting Bank rows used by the Banking module"],
            ["Archived Interbank Counterpart", archivedInterbankCount.ToString(CultureInfo.InvariantCulture), "Retained counterpart rows; included in review history but not the current active Bank transaction count"],
            ["Other archived Bank rows", otherArchivedBankCount.ToString(CultureInfo.InvariantCulture), "Other retained archived Bank history"]
        ]));

        var mappingRows = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "__MappingId", "__AAH_MemberMappingIds", "Type", "Summary Name", "Source Ledger",
                "Existing Ledger Match", "Suggested Match", "Final Ledger", "Ledger Group", "Status",
                "Count", "Payment Count", "Receipt Count", "Bank Filter", "Bank Identity"
            }
        };
        var ledgerMappings = Safe(() => _banking.GetLedgerMappings(client.Id, client.FinancialYear), []);
        var activeReviewTransactionIds = activeReviewTransactionsById.Values
            .Where(row => !ExcelLedgerMappingParser.IsLedgerMappingCoverageExcludedMode(row.Mode))
            .Select(row => row.Id)
            .Distinct()
            .ToArray();
        // Review export is an audit boundary: rebuild once from the current Bank Account
        // authority names so stale aliases are migrated and the workbook never reports
        // an obsolete OD/CA label after the account has been corrected.
        ledgerMappings = Safe(
            () => _ledgerMappingService.Rebuild(client.Id, client.FinancialYear, activeReviewTransactionIds),
            ledgerMappings);

        // The review workbook must show the SAME logical Ledger Mapping rows as the
        // application, not the lower-level per-bank persistence rows. ALL BANKS in the
        // workbench combines the same Summary + Source Ledger identity across banks.
        var allReviewMasterRecords = Safe(() => _masters.Get(client.Id, client.FinancialYear, true), []).ToList();
        var reviewMasterRecords = allReviewMasterRecords
            .Where(row => !row.IsArchived)
            .ToList();
        var reviewMasterLedgers = reviewMasterRecords
            .Where(row => row.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(row.Name))
            .ToList();
        var reviewMasterLedgerGroups = reviewMasterLedgers
            .Select(master => new { Master = master, Key = BankNarrationParser.NormalizeLedgerExactMatchKey(master.Name) })
            .Where(item => item.Key.Length > 0)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Master).ToArray(), StringComparer.OrdinalIgnoreCase);
        var reviewMasterByExactKey = reviewMasterLedgerGroups
            .Where(pair => pair.Value.Length == 1)
            .ToDictionary(
                pair => pair.Key,
                pair => (Name: pair.Value[0].Name.Trim(), Group: pair.Value[0].Parent?.Trim() ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

        static string ReviewMappingGroupKey(BankLedgerMapping mapping)
        {
            var summary = BankNarrationParser.Normalize(mapping.SummaryName ?? string.Empty);
            var source = AccountingText.DuplicateKey(mapping.SourceLedgerName);
            return $"{summary}\u001f{(source.Length == 0 ? "<BLANK>" : "<MISSING>" + source)}";
        }

        foreach (var members in ledgerMappings
                     .Where(row => !ExcelLedgerMappingParser.IsLedgerMappingNoiseSummary(row.SummaryName ?? string.Empty))
                     .GroupBy(ReviewMappingGroupKey, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.ToList())
                     .OrderByDescending(group => group.Sum(row => Math.Max(0, row.MatchCount)))
                     .ThenBy(group => group[0].SummaryName ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var representative = members.OrderByDescending(row => row.MatchCount).ThenBy(row => row.Id).First();
            var summary = representative.SummaryName?.Trim() ?? string.Empty;
            if (summary.Length == 0) continue;

            var exactKey = BankNarrationParser.NormalizeLedgerExactMatchKey(summary);
            var duplicateSummaryMasters = exactKey.Length > 0 &&
                                          reviewMasterLedgerGroups.TryGetValue(exactKey, out var summaryMatches) &&
                                          summaryMatches.Length > 1;
            var exactMatch = string.Empty;
            var exactGroup = string.Empty;
            if (exactKey.Length > 0 && reviewMasterByExactKey.TryGetValue(exactKey, out var exactInfo))
            {
                exactMatch = exactInfo.Name;
                exactGroup = exactInfo.Group;
            }

            var frozenMemberIds = members
                .SelectMany(row => row.TransactionIds)
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
            var frozenTransactions = frozenMemberIds
                .Where(activeReviewTransactionsById.ContainsKey)
                .Select(id => activeReviewTransactionsById[id])
                .ToArray();
            var hasFrozenMembership = frozenMemberIds.Length > 0;
            var paymentCount = hasFrozenMembership
                ? frozenTransactions.Count(row => row.Mode.Equals("Payment", StringComparison.OrdinalIgnoreCase))
                : members.Sum(row => row.PaymentMatchCount > 0
                    ? row.PaymentMatchCount
                    : row.VoucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase) ? row.MatchCount : 0);
            var receiptCount = hasFrozenMembership
                ? frozenTransactions.Count(row => row.Mode.Equals("Receipt", StringComparison.OrdinalIgnoreCase))
                : members.Sum(row => row.ReceiptMatchCount > 0
                    ? row.ReceiptMatchCount
                    : row.VoucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase) ? row.MatchCount : 0);
            var count = hasFrozenMembership
                ? frozenTransactions.Length
                : members.Sum(row => Math.Max(0, row.MatchCount));
            var displayType = paymentCount > 0 && receiptCount > 0 ? "Mixed"
                : paymentCount > 0 ? "Payment"
                : receiptCount > 0 ? "Receipt"
                : representative.VoucherType;

            var distinctFinals = members
                .Where(row => !string.IsNullOrWhiteSpace(row.LedgerName))
                .GroupBy(row => BankNarrationParser.NormalizeLedgerExactMatchKey(row.LedgerName), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First().LedgerName.Trim())
                .Where(value => value.Length > 0)
                .ToList();

            // Match the application's workbench exactly: an existing saved/source
            // Ledger is resolved first, then Summary Name exact identity, then only a
            // unique conservative party-equivalent Master. Master provenance is not
            // restricted to Tally; Purchase/Sales/Manual/application Ledgers qualify.
            var explicitExistingLedger = distinctFinals.Count == 1
                ? distinctFinals[0]
                : representative.SourceLedgerName;
            var existingResolution = LedgerMappingMasterResolver.Resolve(summary, explicitExistingLedger, reviewMasterLedgers);
            if (existingResolution.HasMultipleMatches) duplicateSummaryMasters = true;
            if (existingResolution.Match is not null)
            {
                exactMatch = existingResolution.Match.Name.Trim();
                exactGroup = existingResolution.Match.Parent?.Trim() ?? string.Empty;
            }

            var mappedMembers = members.Count(row => row.Status.Equals("Mapped", StringComparison.OrdinalIgnoreCase));

            string finalLedger;
            string ledgerGroup;
            string status;
            if (distinctFinals.Count > 1)
            {
                finalLedger = string.Empty;
                ledgerGroup = string.Empty;
                status = "Split";
            }
            else if (distinctFinals.Count == 1)
            {
                finalLedger = distinctFinals[0];
                ledgerGroup = members.FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.MasterGroup))?.MasterGroup?.Trim() ?? string.Empty;
                var finalKey = BankNarrationParser.NormalizeLedgerExactMatchKey(finalLedger);
                if (finalKey.Length > 0 && reviewMasterLedgerGroups.TryGetValue(finalKey, out var finalMatches) && finalMatches.Length > 1)
                {
                    finalLedger = AccountingText.DisplayName(finalLedger);
                    ledgerGroup = string.Empty;
                    status = "Multiple Master Matches";
                }
                else if (finalKey.Length > 0 && reviewMasterByExactKey.TryGetValue(finalKey, out var finalInfo))
                {
                    finalLedger = finalInfo.Name;
                    ledgerGroup = finalInfo.Group;
                    status = mappedMembers == members.Count ? "Mapped" : mappedMembers > 0 ? "In Progress" : "Ready";
                }
                else if (ledgerGroup.Length == 0)
                {
                    finalLedger = AccountingText.DisplayName(finalLedger);
                    status = "Select Ledger Group";
                }
                else
                {
                    finalLedger = AccountingText.DisplayName(finalLedger);
                    ledgerGroup = AccountingText.DisplayName(ledgerGroup);
                    status = "Ready - New Ledger";
                }
            }
            else if (duplicateSummaryMasters)
            {
                finalLedger = string.Empty;
                ledgerGroup = string.Empty;
                status = "Multiple Master Matches";
            }
            else if (exactMatch.Length > 0)
            {
                finalLedger = exactMatch;
                ledgerGroup = exactGroup;
                status = "Ready";
            }
            else if (!string.IsNullOrWhiteSpace(representative.SourceLedgerName))
            {
                finalLedger = AccountingText.DisplayName(representative.SourceLedgerName);
                ledgerGroup = string.Empty;
                status = "Select Ledger Group";
            }
            else
            {
                finalLedger = string.Empty;
                ledgerGroup = string.Empty;
                status = "Pending";
            }

            var displaySummary = AccountingText.DisplayName(summary);
            var suggestedMatch = exactMatch.Length > 0 ? string.Empty : displaySummary;
            var banks = members.Select(row => row.BankFilter?.Trim() ?? string.Empty)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var bankFilter = banks.Length == 1 ? banks[0] : "ALL BANKS";
            var memberIds = string.Join(",", members.Select(row => row.Id).Where(id => id > 0).OrderBy(id => id));

            mappingRows.Add([
                representative.Id.ToString(CultureInfo.InvariantCulture), memberIds, displayType, displaySummary,
                representative.SourceLedgerName, exactMatch, suggestedMatch, finalLedger, ledgerGroup, status,
                count.ToString(CultureInfo.InvariantCulture), paymentCount.ToString(CultureInfo.InvariantCulture),
                receiptCount.ToString(CultureInfo.InvariantCulture), bankFilter, BankIdentity(bankFilter)]);
        }
        result.Add(new DiagnosticSheet("Ledger Mapping", mappingRows, HiddenLeadingColumns: 2));

        var normalizedDuplicateGroups = Safe(
            () => _masters.GetNormalizedDuplicateGroups(client.Id, client.FinancialYear),
            Array.Empty<IReadOnlyList<MasterRecord>>());
        var duplicateMasterIds = normalizedDuplicateGroups
            .SelectMany(group => group)
            .Select(row => row.PersistentId)
            .ToHashSet();
        var activeTallyLedgers = allReviewMasterRecords
            .Where(row => !row.IsArchived && row.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && MasterAuthority.IsTallySource(row.Source))
            .ToArray();
        var tallyLedgersByIdentity = activeTallyLedgers
            .Where(row => TallyMasterIdentity.Key(row.Name).Length > 0)
            .GroupBy(row => TallyMasterIdentity.Key(row.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var ledgerRows = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "__MasterId", "Name", "Ledger Group", "Opening Balance", "Archived", "Source",
                "Authority", "Canonical Name", "Identity Key", "Duplicate Review", "Tally Identity"
            }
        };
        foreach (var row in allReviewMasterRecords
                     .Where(row => row.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(row => row.Name))
        {
            var identityKey = TallyMasterIdentity.Key(row.Name);
            var canonicalName = _masters.ResolveCanonicalName(client.Id, client.FinancialYear, "Ledger", row.Name);
            var tallyIdentity = row.IsArchived ? "Archived"
                : MasterAuthority.IsTallySource(row.Source) ? "Tally authority"
                : identityKey.Length > 0 && tallyLedgersByIdentity.TryGetValue(identityKey, out var tallyMatches)
                    ? $"Equivalent Tally Master: {string.Join(" | ", tallyMatches.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase))}"
                    : "Local only";
            ledgerRows.Add([
                row.PersistentId.ToString(CultureInfo.InvariantCulture), row.Name, row.Parent,
                row.OpeningBalance?.ToString("0.########", CultureInfo.InvariantCulture) ?? string.Empty,
                row.IsArchived ? "Yes" : "No", row.Source, MasterAuthority.EditAuthorityStatus(row.Source),
                canonicalName, identityKey, duplicateMasterIds.Contains(row.PersistentId) ? "Review" : "Unique", tallyIdentity]);
        }
        result.Add(new DiagnosticSheet("Ledger Master", ledgerRows, HiddenLeadingColumns: 1));

        var duplicateReviewRows = new List<IReadOnlyList<string>>
        {
            new[] { "Evidence", "Key / GSTIN", "Type", "Master IDs", "Names", "Sources", "Authority", "Action" }
        };
        foreach (var group in normalizedDuplicateGroups)
        {
            var active = group.Where(row => !row.IsArchived).ToArray();
            var hasTally = active.Any(row => MasterAuthority.IsTallySource(row.Source));
            var action = active.Length <= 1
                ? "Historical duplicate - active canonical is unique"
                : hasTally
                    ? "Review only - Tally-protected; Hub will not auto-merge"
                    : "Resolve with Merge + Update";
            duplicateReviewRows.Add([
                "Normalized Master identity", TallyMasterIdentity.Key(group[0].Name), group[0].Type,
                string.Join(",", group.Select(row => row.PersistentId)),
                string.Join(" | ", group.Select(row => row.Name).Distinct(StringComparer.OrdinalIgnoreCase)),
                string.Join(" | ", group.Select(row => row.Source).Distinct(StringComparer.OrdinalIgnoreCase)),
                hasTally ? "Contains Tally authority" : "Local / Manual", action]);
        }

        void AddGstinDuplicateEvidence(string evidence, IEnumerable<(string Gstin, string Name)> sourceRows)
        {
            foreach (var group in sourceRows
                         .Select(row => (Gstin: NormalizeGstin(row.Gstin), Name: AccountingText.DisplayName(row.Name)))
                         .Where(row => row.Gstin.Length == 15 && row.Name.Length > 0)
                         .GroupBy(row => row.Gstin, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Select(row => TallyMasterIdentity.Key(row.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            {
                var names = group.Select(row => row.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var matchingMasters = reviewMasterLedgers
                    .Where(master => names.Any(name => TallyMasterIdentity.Equivalent(master.Name, name)))
                    .ToArray();
                var matchingTallyCount = matchingMasters.Count(master => MasterAuthority.IsTallySource(master.Source));
                var action = matchingTallyCount > 1
                    ? "Review only - multiple Tally Masters share this GSTIN evidence; Hub will not auto-merge"
                    : matchingMasters.Length > 1
                        ? "Review duplicate evidence; local records may be resolved with Merge + Update"
                        : "Review spelling/canonical mapping; do not auto-create another Master";
                duplicateReviewRows.Add([
                    evidence, group.Key, "Ledger",
                    string.Join(",", matchingMasters.Select(row => row.PersistentId)),
                    string.Join(" | ", names),
                    string.Join(" | ", matchingMasters.Select(row => row.Source).Distinct(StringComparer.OrdinalIgnoreCase)),
                    matchingTallyCount > 0 ? "Contains Tally authority" : "Source/local evidence", action]);
            }
        }

        var stockItems = Safe(() => _stockMasters.GetStockItems(client.Id, client.FinancialYear, true, string.Empty), []).OrderBy(row => row.Name).ToArray();
        var stockUnits = Safe(() => _stockMasters.GetUnits(client.Id, client.FinancialYear, true, string.Empty), []).OrderBy(row => row.Name).ToArray();
        var gstDirectory = Safe(() => _stockMasters.GetGstHsnSacDirectory(string.Empty, 50000), []).ToArray();
        var unitsByName = stockUnits
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .GroupBy(row => AccountingText.DuplicateKey(row.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var gstDescriptionByCode = gstDirectory
            .Where(row => !string.IsNullOrWhiteSpace(row.Code))
            .GroupBy(row => row.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Description, StringComparer.OrdinalIgnoreCase);
        var stockById = stockItems.Where(row => row.PersistentId > 0).ToDictionary(row => row.PersistentId);

        static bool IsDoubtStock(string? itemName) =>
            (itemName ?? string.Empty).Trim().Equals("Others", StringComparison.OrdinalIgnoreCase);

        string StockReviewStatus(StockItemRecord item)
        {
            if (IsDoubtStock(item.Name))
                return "Doubt Stock / clarification required - retained intentionally";
            if (string.IsNullOrWhiteSpace(item.BaseUnit))
                return "Blocked: Inventory / Transaction Unit missing";
            var unitKey = AccountingText.DuplicateKey(item.BaseUnit);
            if (!unitsByName.ContainsKey(unitKey))
                return $"Blocked: Unit Master '{item.BaseUnit}' is not saved";
            if (string.IsNullOrWhiteSpace(item.HsnSac))
                return "Review: HSN/SAC not assigned";
            if (string.IsNullOrWhiteSpace(item.TallyMasterId))
                return "AAH-ready; Tally MASTERID pending / company not linked";
            return "Ready / linked to Tally Stock MASTERID";
        }

        string InventoryReviewStatus(string itemName, long stockItemId, string tallyStockMasterId, string unit)
        {
            if (string.IsNullOrWhiteSpace(itemName))
                return "Accounting-only / no Stock Item in source";
            if (IsDoubtStock(itemName))
                return "Doubt Stock / clarification required - retained intentionally";
            if (stockItemId <= 0)
                return "Blocked: not linked to AAH Stock Master";
            if (!stockById.TryGetValue(stockItemId, out var item))
                return $"Blocked: AAH Stock Item ID {stockItemId} no longer exists";
            if (string.IsNullOrWhiteSpace(item.BaseUnit))
                return "Blocked: Stock Master Unit missing";
            if (string.IsNullOrWhiteSpace(unit))
                return $"Blocked: transaction Unit missing; expected '{item.BaseUnit}'";
            if (!AccountingText.DuplicateKey(unit).Equals(AccountingText.DuplicateKey(item.BaseUnit), StringComparison.OrdinalIgnoreCase))
                return $"Review: transaction Unit '{unit}' differs from Base Unit '{item.BaseUnit}'";
            if (string.IsNullOrWhiteSpace(tallyStockMasterId) && string.IsNullOrWhiteSpace(item.TallyMasterId))
                return "AAH-linked; Tally MASTERID pending";
            return "Ready / linked";
        }

        var unitRows = new List<IReadOnlyList<string>>
        {
            new[] { "__UnitId", "Symbol", "Formal Name", "Decimal Places", "Reporting UQC", "Conversion", "Company", "Archived", "Source", "Tally Readiness" }
        };
        foreach (var row in stockUnits)
        {
            var formalName = row.FormalName;
            var reportingUqc = row.ReportingUqc;
            if (AahUnitCatalog.TryGetDefinition(row.Name, out var definition))
            {
                if (string.IsNullOrWhiteSpace(formalName)) formalName = definition.Description;
                if (string.IsNullOrWhiteSpace(reportingUqc)) reportingUqc = definition.ReportingUqc;
            }
            var readiness = string.IsNullOrWhiteSpace(row.Name)
                ? "Blocked: Unit Symbol missing"
                : row.DecimalPlaces is < 0 or > 4
                    ? "Blocked: Decimal Places must be 0-4"
                    : string.IsNullOrWhiteSpace(formalName)
                        ? "Ready; Formal Name blank (optional in Tally)"
                        : "Ready";
            unitRows.Add([
                row.PersistentId.ToString(CultureInfo.InvariantCulture), row.Name, formalName,
                Math.Clamp(row.DecimalPlaces, 0, 4).ToString(CultureInfo.InvariantCulture), reportingUqc,
                row.Conversion?.ToString("0.########", CultureInfo.InvariantCulture) ?? string.Empty,
                row.Company, YesNo(row.IsArchived), row.Source, readiness]);
        }
        result.Add(new DiagnosticSheet("Unit Masters", unitRows, HiddenLeadingColumns: 1));

        var stockRows = new List<IReadOnlyList<string>>
        {
            new[] { "__StockItemId", "Name", "Part No/SKU", "Stock Group", "Stock Category", "Inventory / Transaction Unit", "Unit Decimal Places", "Rate Unit", "Tally Alternate Unit", "Default Godown", "Default Purchase Ledger", "Default Sales Ledger", "Reorder Point", "Minimum Stock", "Maximum Stock", "Tally MASTERID", "Tally GUID", "Tally ALTERID", "HSN/SAC", "GSTN Description", "GST Rate", "Type of Supply", "Batch Wise", "Perishable", "Manufacturing Date", "Costing Method", "Valuation Method", "Archived", "Source", "Inventory Review Status" }
        };
        foreach (var row in stockItems)
        {
            unitsByName.TryGetValue(AccountingText.DuplicateKey(row.BaseUnit), out var baseUnit);
            gstDescriptionByCode.TryGetValue(row.HsnSac?.Trim() ?? string.Empty, out var hsnDescription);
            stockRows.Add([
                row.PersistentId.ToString(CultureInfo.InvariantCulture), row.Name, row.PartNumberSku, row.StockGroup, row.StockCategory, row.BaseUnit,
                baseUnit is null ? string.Empty : Math.Clamp(baseUnit.DecimalPlaces, 0, 4).ToString(CultureInfo.InvariantCulture),
                row.RateUnit, row.AdditionalUnit, row.DefaultGodown, row.DefaultPurchaseLedger, row.DefaultSalesLedger,
                Num(row.ReorderPoint), Num(row.MinimumStock), Num(row.MaximumStock), row.TallyMasterId, row.TallyGuid, row.TallyAlterId.ToString(CultureInfo.InvariantCulture),
                row.HsnSac ?? string.Empty, hsnDescription ?? string.Empty, row.GstRate?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty, row.GstTypeOfSupply,
                YesNo(row.BatchWise), YesNo(row.Perishable), YesNo(row.HasManufacturingDate), row.CostingMethod, row.ValuationMethod, YesNo(row.IsArchived), row.Source,
                StockReviewStatus(row)]);
        }
        if (stockRows.Count > 1) result.Add(new DiagnosticSheet("Stock Master", stockRows, HiddenLeadingColumns: 1));

        var stockUnitProfileRows = new List<IReadOnlyList<string>>
        {
            new[] { "__UnitProfileId", "__StockItemId", "Stock Item", "Allowed Unit", "Factor to Base", "Allowed", "Tally Alternate", "Package Conversion", "Source", "Updated" }
        };
        foreach (var row in Safe(() => _stockMasters.GetStockItemUnitProfiles(client.Id, client.FinancialYear), []).OrderBy(row => row.StockItemName).ThenBy(row => row.UnitCode))
            stockUnitProfileRows.Add([row.PersistentId.ToString(CultureInfo.InvariantCulture), row.StockItemId.ToString(CultureInfo.InvariantCulture), row.StockItemName, row.UnitCode, Num(row.FactorToBase), YesNo(row.IsAllowed), YesNo(row.IsTallyAlternateUnit), YesNo(row.IsPackageConversion), row.Source, row.UpdatedUtc.ToString("O")]);
        if (stockUnitProfileRows.Count > 1) result.Add(new DiagnosticSheet("Stock Item Unit Profiles", stockUnitProfileRows, HiddenLeadingColumns: 2));

        var gstRows = new List<IReadOnlyList<string>> { new[] { "Type", "HSN / SAC Code", "Official GSTN Description", "Source", "Updated" } };
        foreach (var row in gstDirectory)
            gstRows.Add([row.Kind, row.Code, row.Description, row.Source, row.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture)]);
        if (gstDirectory.Length == 0)
            gstRows.Add(["STATUS", string.Empty, "Official GSTN HSN/SAC directory is not loaded yet. Use Stock Master > Refresh GST HSN/SAC or Import GST Directory.", GstHsnSacDirectoryService.OfficialDirectoryUrl, string.Empty]);
        result.Add(new DiagnosticSheet("GST HSN SAC Directory", gstRows));

        var purchaseRecords = Safe(() => _purchase.GetRecords(client.Id, client.FinancialYear), []).ToArray();
        var salesRecords = Safe(() => _sales.GetRecords(client.Id, client.FinancialYear), []).ToArray();
        AddGstinDuplicateEvidence("Purchase GSTIN", purchaseRecords.Select(row => (row.SupplierGstin, row.SupplierName)));
        AddGstinDuplicateEvidence("Sales GSTIN", salesRecords.Select(row => (row.CustomerGstin, row.CustomerName)));
        if (duplicateReviewRows.Count > 1)
            result.Add(new DiagnosticSheet("Master Duplicate Review", duplicateReviewRows));

        var purchaseRows = new List<IReadOnlyList<string>>
        {
            new[] { "__PurchaseId", "Client GSTIN", "GST Date", "Document Date", "Supplier GSTIN", "Supplier", "Document Type", "Document No", "Document Value", "Rate", "Taxable Value", "IGST", "CGST", "SGST", "Cess", "Round Off", "Purchase Ledger", "IGST Ledger", "CGST Ledger", "SGST Ledger", "GST Applicability", "Reconciliation Status", "Place of Supply", "Reverse Charge", "AAH Stock Item ID", "Tally Stock MASTERID", "Item", "HSN/SAC", "Actual Qty", "Billed Qty", "Free Qty", "Unit", "Rate Unit", "Item Rate", "Item Amount", "Item Purchase Ledger", "Godown", "Batch", "Part No/SKU", "Inventory Review Status" }
        };
        foreach (var row in purchaseRecords)
            purchaseRows.Add([
                row.PersistentId.ToString(CultureInfo.InvariantCulture), row.ClientGstin, Date(row.GstDate), Date(row.DocumentDate), row.SupplierGstin, row.SupplierName,
                row.DocumentType, row.DocumentNumber, Num(row.DocumentValue), Num(row.Rate), Num(row.TaxableValue), Num(row.Igst), Num(row.Cgst), Num(row.Sgst), Num(row.Cess), Num(row.RoundOff),
                row.PurchaseLedger, row.IgstLedger, row.CgstLedger, row.SgstLedger, row.GstApplicability, row.ReconciliationStatus, row.PlaceOfSupply, row.ReverseCharge,
                row.StockItemPersistentId > 0 ? row.StockItemPersistentId.ToString(CultureInfo.InvariantCulture) : string.Empty, row.TallyStockMasterId, row.ItemName, row.HsnCode,
                Num(row.ActualQuantity), Num(row.BilledQuantity), Num(row.FreeQuantity), row.Unit, row.RateUnit, Num(row.ItemRate), Num(row.ItemAmount), row.ItemPurchaseLedger,
                row.Godown, row.BatchName, row.PartNumberSku, InventoryReviewStatus(row.ItemName, row.StockItemPersistentId, row.TallyStockMasterId, row.Unit)]);
        if (purchaseRows.Count > 1) result.Add(new DiagnosticSheet("Purchase", purchaseRows, HiddenLeadingColumns: 1));

        var salesRows = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "__SalesId", "Client GSTIN", "GST Date", "Document Date", "Customer GSTIN", "Customer", "Document Type", "Document No", "Document Value", "Rate", "Taxable Value",
                "IGST", "CGST", "SGST", "Cess", "Cess Rate", "Round Off", "Sales Ledger", "IGST Ledger", "CGST Ledger", "SGST Ledger", "Place of Supply", "Return Period",
                "AAH Stock Item ID", "Tally Stock MASTERID", "Item", "HSN/SAC", "Actual Qty", "Billed Qty", "Free Qty", "Unit", "Rate Unit", "Item Rate", "Item Amount",
                "Item Sales Ledger", "Accounting Amount", "Godown", "Batch", "Batch Actual Qty", "Batch Billed Qty", "Batch Amount", "Discount %", "Discount Amount", "Order No",
                "Tracking No", "Manufacturing Date", "Expiry Date", "Item Description", "Part No/SKU", "MRP", "Serial/Lot No", "Cost Centre", "Price Level", "Alternate Qty",
                "Alternate Unit", "Delivery Note No", "Buyer Order No", "Buyer Order Date", "Narration", "Import Source", "Inventory Review Status"
            }
        };
        foreach (var row in salesRecords)
            salesRows.Add([
                row.PersistentId.ToString(CultureInfo.InvariantCulture), row.ClientGstin, Date(row.GstDate), Date(row.DocumentDate), row.CustomerGstin, row.CustomerName,
                row.DocumentType, row.DocumentNumber, Num(row.DocumentValue), Num(row.Rate), Num(row.TaxableValue), Num(row.Igst), Num(row.Cgst), Num(row.Sgst), Num(row.Cess), Num(row.CessRate), Num(row.RoundOff),
                row.SalesLedger, row.IgstLedger, row.CgstLedger, row.SgstLedger, row.PlaceOfSupply, row.ReturnPeriod,
                row.StockItemPersistentId > 0 ? row.StockItemPersistentId.ToString(CultureInfo.InvariantCulture) : string.Empty, row.TallyStockMasterId, row.ItemName, row.HsnCode,
                Num(row.ActualQuantity), Num(row.BilledQuantity), Num(row.FreeQuantity), row.Unit, row.RateUnit, Num(row.ItemRate), Num(row.ItemAmount), row.ItemSalesLedger, Num(row.AccountingAmount),
                row.Godown, row.BatchName, Num(row.BatchActualQuantity), Num(row.BatchBilledQuantity), Num(row.BatchAmount), Num(row.DiscountPercent), Num(row.DiscountAmount), row.OrderNumber,
                row.TrackingNumber, Date(row.ManufacturingDate), Date(row.ExpiryDate), row.ItemDescription, row.PartNumberSku, Num(row.Mrp), row.SerialLotNumber, row.CostCentre, row.PriceLevel,
                Num(row.AlternateQuantity), row.AlternateUnit, row.DeliveryNoteNumber, row.BuyerOrderNumber, Date(row.BuyerOrderDate), row.Narration, row.ImportSource,
                InventoryReviewStatus(row.ItemName, row.StockItemPersistentId, row.TallyStockMasterId, row.Unit)]);
        if (salesRows.Count > 1) result.Add(new DiagnosticSheet("Sales", salesRows, HiddenLeadingColumns: 1));

        var tallyLink = Safe(() => _tallyRepository.GetLinkForClient(client.Id), (TallyClientLink?)null);
        var companyKey = tallyLink?.CompanyKey?.Trim() ?? string.Empty;
        var tallyAudit = companyKey.Length > 0
            ? Safe(() => _tallyRepository.GetImportFingerprintAudit(client.Id, client.FinancialYear, companyKey), Array.Empty<TallyImportFingerprintAuditRow>())
            : Array.Empty<TallyImportFingerprintAuditRow>();
        var auditBySource = tallyAudit
            .Where(row => !string.IsNullOrWhiteSpace(row.SourceKey))
            .GroupBy(row => row.SourceKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.LastSeenUtc).First(), StringComparer.OrdinalIgnoreCase);
        var masterGroupByLedgerKey = reviewMasterLedgers
            .Where(row => TallyMasterIdentity.Key(row.Name).Length > 0)
            .GroupBy(row => TallyMasterIdentity.Key(row.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var parents = group.Select(row => row.Parent?.Trim() ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    return parents.Length == 1 ? parents[0] : "Multiple Master Groups";
                },
                StringComparer.OrdinalIgnoreCase);

        var journalRows = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "__JournalId", "Date", "Number", "Type", "Period", "Narration", "Mode",
                "Lines", "Debit", "Credit", "Posting Lines", "Posting Debit", "Posting Credit",
                "Tally Source Key", "Tally Status", "Tally Master ID", "Tally Candidates", "Last Export Status"
            }
        };
        var journalLineRows = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "__JournalId", "Line No", "Date", "Number", "Period", "Mode", "Ledger", "Ledger Group",
                "Dr/Cr", "Amount", "Tax Head", "Line Role", "Tally Source Key", "Tally Status"
            }
        };
        foreach (var row in Safe(() => _journal.GetVouchers(client.Id, client.FinancialYear), []))
        {
            var debit = row.Lines.Where(line => line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase)).Sum(line => line.Amount);
            var credit = row.Lines.Where(line => line.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase)).Sum(line => line.Amount);
            var postingLines = row.Lines.ToArray();
            var postingDebit = debit;
            var postingCredit = credit;
            var automatic = row.GenerationMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase);
            var identity = row.VoucherNumber.Trim().Length > 0
                ? row.VoucherNumber.Trim()
                : row.PersistentId.ToString(CultureInfo.InvariantCulture);
            var sourceKey = automatic
                ? $"AAH|GSTJ|{client.Id}|{client.FinancialYear}|{row.PeriodKey}"
                : $"AAH|JOURNAL|{client.Id}|{client.FinancialYear}|{identity}|{row.VoucherDate:yyyyMMdd}";
            var reference = automatic ? sourceKey : identity;
            var candidates = companyKey.Length > 0
                ? Safe(() => _tallyRepository.FindVoucherCandidates(companyKey, client.FinancialYear, row.VoucherDate.Date, "Journal", reference), Array.Empty<TallyVoucherIndexRecord>())
                : Array.Empty<TallyVoucherIndexRecord>();
            auditBySource.TryGetValue(sourceKey, out var audit);
            var tallyStatus = companyKey.Length == 0 ? "No linked Tally company"
                : candidates.Count == 1 ? "Live in Tally"
                : candidates.Count > 1 ? "Multiple live candidates"
                : audit is not null && audit.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase)
                    ? "Imported history only - not live"
                    : !string.IsNullOrWhiteSpace(audit?.Status) ? audit!.Status.Trim() : "Not exported / not live";
            var masterId = candidates.Count == 1 ? candidates[0].MasterId : string.Empty;

            journalRows.Add([
                row.PersistentId.ToString(CultureInfo.InvariantCulture), Date(row.VoucherDate), row.VoucherNumber, row.VoucherType,
                row.PeriodLabel, row.Narration, row.GenerationMode, row.Lines.Count.ToString(CultureInfo.InvariantCulture),
                Num(debit), Num(credit), postingLines.Length.ToString(CultureInfo.InvariantCulture), Num(postingDebit), Num(postingCredit),
                sourceKey, tallyStatus, masterId, candidates.Count.ToString(CultureInfo.InvariantCulture), audit?.Status ?? string.Empty]);

            foreach (var line in row.Lines.OrderBy(line => line.LineNumber))
            {
                var ledgerKey = TallyMasterIdentity.Key(line.LedgerName);
                var ledgerGroup = ledgerKey.Length > 0 && masterGroupByLedgerKey.TryGetValue(ledgerKey, out var groupName)
                    ? groupName
                    : string.Empty;
                journalLineRows.Add([
                    row.PersistentId.ToString(CultureInfo.InvariantCulture), line.LineNumber.ToString(CultureInfo.InvariantCulture),
                    Date(row.VoucherDate), row.VoucherNumber, row.PeriodLabel, row.GenerationMode, line.LedgerName, ledgerGroup,
                    line.DrCr, Num(line.Amount), line.TaxHead, line.LineRole, sourceKey, tallyStatus]);
            }
        }
        if (journalRows.Count > 1) result.Add(new DiagnosticSheet("Journal", journalRows, HiddenLeadingColumns: 1));
        if (journalLineRows.Count > 1) result.Add(new DiagnosticSheet("Journal Lines", journalLineRows, HiddenLeadingColumns: 1));

        var cashRows = new List<IReadOnlyList<string>>
        {
            new[] { "__CashEntryId", "Date", "Entry Ledger", "Debit", "Credit", "Narration", "Common Ledger", "Mapped Master Ledger", "Voucher Type", "Status" }
        };
        foreach (var row in Safe(() => _banking.GetCashEntries(client.Id, client.FinancialYear), []))
            cashRows.Add([row.Id.ToString(CultureInfo.InvariantCulture), Date(row.EntryDate), row.EntryLedger, Num(row.Debit), Num(row.Credit), row.Narration, row.CommonLedger, row.MappedMasterLedger, row.VoucherType, row.Status]);
        if (cashRows.Count > 1) result.Add(new DiagnosticSheet("Cash Entry", cashRows, HiddenLeadingColumns: 1));

        return result;
    }

    private int ImportBankStatement(ClientProfile client, OpenXmlWorkbookData workbook, ReviewSyncState syncState, ref int skipped, List<string> errors)
    {
        if (!workbook.TryGetWorksheet("Bank Statement", out var sheet)) return 0;
        var headers = Headers(sheet);
        var changedRows = GetChangedRowNumbers(sheet, headers, "Bank Statement", "__TransactionId", syncState);
        if (changedRows.Count == 0) return 0;
        var existing = Safe(() => _banking.GetTransactions(client.Id, client.FinancialYear, true), []).ToDictionary(row => row.Id);
        var changed = 0;
        foreach (var r in changedRows)
        {
            if (!TryLong(Cell(sheet, r, headers, "__TransactionId"), out var id) || !existing.TryGetValue(id, out var row)) { skipped++; continue; }
            try
            {
                var before = BankComparable(row);
                if (TryDate(Cell(sheet, r, headers, "Date"), out var date)) row.TransactionDate = date;
                row.Narration = Cell(sheet, r, headers, "Narration", row.Narration);
                row.NormalizedNarration = BankNarrationParser.Normalize(row.Narration);
                row.Debit = Decimal(Cell(sheet, r, headers, "Debit"), row.Debit);
                row.Credit = Decimal(Cell(sheet, r, headers, "Credit"), row.Credit);
                row.Mode = Cell(sheet, r, headers, "Mode", row.Mode);
                row.LedgerName = Cell(sheet, r, headers, "Ledger", row.LedgerName);
                row.ReferenceNumber = Cell(sheet, r, headers, "Reference", row.ReferenceNumber);
                var runningBalanceText = Cell(sheet, r, headers, "Running Balance");
                if (!string.IsNullOrWhiteSpace(runningBalanceText))
                {
                    row.RunningBalance = NullableDecimal(runningBalanceText, row.RunningBalance);
                    row.RunningBalanceSource = "MANUAL";
                }
                row.RecordStatus = Cell(sheet, r, headers, "Record Status", row.RecordStatus);
                var requestedMappingStatus = Cell(sheet, r, headers, "Mapping Status", row.MappingStatus);
                row.MappingStatus = requestedMappingStatus.Length > 0 ? requestedMappingStatus : row.LedgerName.Length > 0 ? "Mapped" : "Mapping Required";
                if (BankComparable(row) == before) continue;
                _banking.SaveTransaction(row);
                changed++;
            }
            catch (Exception ex) { errors.Add($"Bank Statement row {r}: {ex.Message}"); }
        }
        return changed;
    }

    private int ImportLedgerMapping(ClientProfile client, OpenXmlWorkbookData workbook, ReviewSyncState syncState, ref int skipped, List<string> errors)
    {
        if (!workbook.TryGetWorksheet("Ledger Mapping", out var sheet)) return 0;
        var headers = Headers(sheet);
        var changedRows = GetChangedRowNumbers(sheet, headers, "Ledger Mapping", "__MappingId", syncState);
        if (changedRows.Count == 0) return 0;
        var existing = Safe(() => _banking.GetLedgerMappings(client.Id, client.FinancialYear), []).ToDictionary(row => row.Id);
        var changed = 0;
        foreach (var r in changedRows)
        {
            if (!TryLong(Cell(sheet, r, headers, "__MappingId"), out var representativeId)) { skipped++; continue; }
            var memberIds = ParseMappingIds(Cell(sheet, r, headers, "__AAH_MemberMappingIds"));
            if (memberIds.Count == 0) memberIds.Add(representativeId);
            var members = memberIds.Where(existing.ContainsKey).Select(id => existing[id]).ToArray();

            // A parser repair can replace persisted mapping IDs after the workbook was
            // generated. Recover the logical workbench identity so an older copied-back
            // review file can still apply its actual Final Ledger/Group change safely.
            if (members.Length == 0)
            {
                var workbookSummary = Cell(sheet, r, headers, "Summary Name");
                var sourceKey = AccountingText.DuplicateKey(Cell(sheet, r, headers, "Source Ledger"));
                var workbookBank = Cell(sheet, r, headers, "Bank Filter").Trim();
                members = existing.Values.Where(mapping =>
                        AccountingText.DuplicateKey(mapping.SourceLedgerName).Equals(sourceKey, StringComparison.OrdinalIgnoreCase) &&
                        (workbookBank.Length == 0 || workbookBank.Equals("ALL BANKS", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(mapping.BankFilter, workbookBank, StringComparison.OrdinalIgnoreCase)) &&
                        ReviewSummaryCompatible(workbookSummary, mapping.SummaryName ?? string.Empty))
                    .ToArray();
            }
            if (members.Length == 0) { skipped++; continue; }

            try
            {
                var first = members[0];
                var ledger = Cell(sheet, r, headers, "Final Ledger", first.LedgerName).Trim();
                var group = Cell(sheet, r, headers, "Ledger Group", first.MasterGroup).Trim();
                var rowChanged = false;

                foreach (var mapping in members)
                {
                    if (ledger.Equals(mapping.LedgerName, StringComparison.Ordinal) &&
                        group.Equals(mapping.MasterGroup, StringComparison.Ordinal)) continue;

                    if (ledger.Length == 0)
                    {
                        _ledgerMappingService.Clear(mapping);
                        rowChanged = true;
                        continue;
                    }

                    mapping.LedgerName = ledger;
                    mapping.MasterGroup = group;
                    _ledgerMappingService.Apply(mapping, createMaster: true);
                    rowChanged = true;
                }

                if (rowChanged) changed++;
            }
            catch (Exception ex) { errors.Add($"Ledger Mapping row {r}: {ex.Message}"); }
        }
        return changed;
    }

    private static List<long> ParseMappingIds(string value)
    {
        var result = new List<long>();
        foreach (var token in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 && !result.Contains(id))
                result.Add(id);
        return result;
    }

    private static bool ReviewSummaryCompatible(string workbookSummary, string currentSummary)
    {
        var left = BankNarrationParser.NormalizeLedgerExactMatchKey(workbookSummary ?? string.Empty);
        var right = BankNarrationParser.NormalizeLedgerExactMatchKey(currentSummary ?? string.Empty);
        if (left.Length == 0 || right.Length == 0) return false;
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase)) return true;

        var workbookComparable = BankNarrationParser.NormalizeLedgerMappingComparisonName(workbookSummary ?? string.Empty);
        var currentComparable = BankNarrationParser.NormalizeLedgerMappingComparisonName(currentSummary ?? string.Empty);
        if (workbookComparable.Equals(currentComparable, StringComparison.OrdinalIgnoreCase)) return true;
        if (ExcelLedgerMappingParser.IsSameParty(workbookComparable, currentComparable)) return true;

        // Parser repairs often replace a bank-field truncation with its stronger full
        // canonical form (RATA -> RATAN PAPE, SANTHOSHPA -> SANTHOSHPAPERPRODU).
        // Allow that old review row to reconnect only through a strong exact prefix;
        // Source Ledger and Bank Filter are checked by the caller as additional guards.
        var shortKey = left.Length <= right.Length ? left : right;
        var longKey = left.Length <= right.Length ? right : left;
        return shortKey.Length >= 4 && longKey.Length - shortKey.Length >= 2 &&
               longKey.StartsWith(shortKey, StringComparison.OrdinalIgnoreCase);
    }

    private int ImportLedgerMaster(ClientProfile client, OpenXmlWorkbookData workbook, ReviewSyncState syncState, ref int skipped, List<string> errors)
    {
        if (!workbook.TryGetWorksheet("Ledger Master", out var sheet)) return 0;
        var headers = Headers(sheet);
        var changedRows = GetChangedRowNumbers(sheet, headers, "Ledger Master", "__MasterId", syncState);
        if (changedRows.Count == 0) return 0;
        var existing = Safe(() => _masters.Get(client.Id, client.FinancialYear, true), [])
            .Where(row => row.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)).ToDictionary(row => row.PersistentId);
        var changed = 0;
        foreach (var r in changedRows)
        {
            if (!TryLong(Cell(sheet, r, headers, "__MasterId"), out var id) || !existing.TryGetValue(id, out var row)) { skipped++; continue; }
            try
            {
                var before = $"{row.Name}\u001f{row.Parent}\u001f{row.OpeningBalance}\u001f{row.IsArchived}";
                row.Name = Cell(sheet, r, headers, "Name", row.Name);
                row.Parent = Cell(sheet, r, headers, "Ledger Group", row.Parent);
                row.OpeningBalance = NullableDecimal(Cell(sheet, r, headers, "Opening Balance"), row.OpeningBalance);
                row.IsArchived = Bool(Cell(sheet, r, headers, "Archived"), row.IsArchived);
                var after = $"{row.Name}\u001f{row.Parent}\u001f{row.OpeningBalance}\u001f{row.IsArchived}";
                if (before == after) continue;
                _masters.Save(row);
                changed++;
            }
            catch (Exception ex) { errors.Add($"Ledger Master row {r}: {ex.Message}"); }
        }
        return changed;
    }

    private int ImportUnitMasters(ClientProfile client, OpenXmlWorkbookData workbook, ReviewSyncState syncState, ref int skipped, List<string> errors)
    {
        if (!workbook.TryGetWorksheet("Unit Masters", out var sheet)) return 0;
        var headers = Headers(sheet);
        var changedRows = GetChangedRowNumbers(sheet, headers, "Unit Masters", "__UnitId", syncState);
        if (changedRows.Count == 0) return 0;
        var existing = Safe(() => _stockMasters.GetUnits(client.Id, client.FinancialYear, true, string.Empty), []).ToDictionary(row => row.PersistentId);
        var changed = 0;
        foreach (var r in changedRows)
        {
            if (!TryLong(Cell(sheet, r, headers, "__UnitId"), out var id) || !existing.TryGetValue(id, out var row)) { skipped++; continue; }
            try
            {
                var before = UnitComparable(row);
                row.Name = Cell(sheet, r, headers, "Symbol", row.Name);
                row.FormalName = Cell(sheet, r, headers, "Formal Name", row.FormalName);
                row.DecimalPlaces = Math.Clamp(Int(Cell(sheet, r, headers, "Decimal Places"), row.DecimalPlaces), 0, 4);
                row.ReportingUqc = Cell(sheet, r, headers, "Reporting UQC", row.ReportingUqc);
                row.Conversion = NullableDecimal(Cell(sheet, r, headers, "Conversion"), row.Conversion);
                row.Company = Cell(sheet, r, headers, "Company", row.Company);
                row.IsArchived = Bool(Cell(sheet, r, headers, "Archived"), row.IsArchived);
                if (UnitComparable(row) == before) continue;
                _stockMasters.SaveUnit(row);
                changed++;
            }
            catch (Exception ex) { errors.Add($"Unit Masters row {r}: {ex.Message}"); }
        }
        return changed;
    }

    private int ImportStockMaster(ClientProfile client, OpenXmlWorkbookData workbook, ReviewSyncState syncState, ref int skipped, List<string> errors)
    {
        if (!workbook.TryGetWorksheet("Stock Master", out var sheet)) return 0;
        var headers = Headers(sheet);
        var changedRows = GetChangedRowNumbers(sheet, headers, "Stock Master", "__StockItemId", syncState);
        if (changedRows.Count == 0) return 0;
        var existing = Safe(() => _stockMasters.GetStockItems(client.Id, client.FinancialYear, true, string.Empty), []).ToDictionary(row => row.PersistentId);
        var changed = 0;
        foreach (var r in changedRows)
        {
            if (!TryLong(Cell(sheet, r, headers, "__StockItemId"), out var id) || !existing.TryGetValue(id, out var row)) { skipped++; continue; }
            try
            {
                var before = StockComparable(row);
                row.Name = Cell(sheet, r, headers, "Name", row.Name);
                row.StockGroup = Cell(sheet, r, headers, "Stock Group", row.StockGroup);
                row.StockCategory = Cell(sheet, r, headers, "Stock Category", row.StockCategory);
                row.PartNumberSku = Cell(sheet,r,headers,"Part No/SKU",row.PartNumberSku);
                var transactionUnit = Cell(sheet, r, headers, "Inventory / Transaction Unit", string.Empty);
                if (string.IsNullOrWhiteSpace(transactionUnit)) transactionUnit = Cell(sheet, r, headers, "Base Unit", row.BaseUnit);
                row.BaseUnit = transactionUnit;
                row.DefaultPurchaseUnit = transactionUnit; // compatibility storage: Purchase/Sales share one authoritative Unit.
                row.DefaultSalesUnit = transactionUnit;
                row.RateUnit=Cell(sheet,r,headers,"Rate Unit",row.RateUnit);
                var tallyAlternate = Cell(sheet, r, headers, "Tally Alternate Unit", string.Empty);
                row.AdditionalUnit = string.IsNullOrWhiteSpace(tallyAlternate) ? Cell(sheet, r, headers, "Additional Unit", row.AdditionalUnit) : tallyAlternate;
                row.DefaultGodown=Cell(sheet,r,headers,"Default Godown",row.DefaultGodown);
                row.DefaultPurchaseLedger=Cell(sheet,r,headers,"Default Purchase Ledger",row.DefaultPurchaseLedger);
                row.DefaultSalesLedger=Cell(sheet,r,headers,"Default Sales Ledger",row.DefaultSalesLedger);
                row.ReorderPoint=Decimal(Cell(sheet,r,headers,"Reorder Point"),row.ReorderPoint);
                row.MinimumStock=Decimal(Cell(sheet,r,headers,"Minimum Stock"),row.MinimumStock);
                row.MaximumStock=Decimal(Cell(sheet,r,headers,"Maximum Stock"),row.MaximumStock);
                row.HsnSac = Cell(sheet, r, headers, "HSN/SAC", row.HsnSac);
                row.GstRate = NullableDecimal(Cell(sheet, r, headers, "GST Rate"), row.GstRate);
                row.GstTypeOfSupply = Cell(sheet, r, headers, "Type of Supply", row.GstTypeOfSupply);
                row.BatchWise = Bool(Cell(sheet, r, headers, "Batch Wise"), row.BatchWise);
                row.Perishable = Bool(Cell(sheet, r, headers, "Perishable"), row.Perishable);
                row.HasManufacturingDate = Bool(Cell(sheet, r, headers, "Manufacturing Date"), row.HasManufacturingDate);
                row.CostingMethod = Cell(sheet, r, headers, "Costing Method", row.CostingMethod);
                row.ValuationMethod = Cell(sheet, r, headers, "Valuation Method", row.ValuationMethod);
                row.IsArchived = Bool(Cell(sheet, r, headers, "Archived"), row.IsArchived);
                if (StockComparable(row) == before) continue;
                _stockMasters.SaveStockItem(row);
                changed++;
            }
            catch (Exception ex) { errors.Add($"Stock Master row {r}: {ex.Message}"); }
        }
        return changed;
    }

    private int ImportPurchase(ClientProfile client, OpenXmlWorkbookData workbook, ReviewSyncState syncState, ref int skipped, List<string> errors)
    {
        if (!workbook.TryGetWorksheet("Purchase", out var sheet)) return 0;
        var headers = Headers(sheet);
        var changedRows = GetChangedRowNumbers(sheet, headers, "Purchase", "__PurchaseId", syncState);
        if (changedRows.Count == 0) return 0;
        var existing = Safe(() => _purchase.GetRecords(client.Id, client.FinancialYear), []).ToDictionary(row => row.PersistentId);
        var updates = new List<PurchaseImportRecord>();
        foreach (var r in changedRows)
        {
            if (!TryLong(Cell(sheet, r, headers, "__PurchaseId"), out var id) || !existing.TryGetValue(id, out var row)) { skipped++; continue; }
            try
            {
                // Originating registration is source-owned. Review workbook edits may
                // change transaction fields, never transfer a row to another GSTIN.
                if (headers.ContainsKey("Client GSTIN") &&
                    !NormalizeGstin(Cell(sheet, r, headers, "Client GSTIN"))
                        .Equals(NormalizeGstin(row.ClientGstin), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Client GSTIN is read-only; reimport the correct registration instead.");
                var before = PurchaseComparable(row);
                row.GstDate = DateOrNull(Cell(sheet, r, headers, "GST Date"), row.GstDate);
                row.DocumentDate = DateOrNull(Cell(sheet, r, headers, "Document Date"), row.DocumentDate);
                row.SupplierGstin = Cell(sheet, r, headers, "Supplier GSTIN", row.SupplierGstin);
                row.SupplierName = Cell(sheet, r, headers, "Supplier", row.SupplierName);
                row.DocumentType = Cell(sheet, r, headers, "Document Type", row.DocumentType);
                row.DocumentNumber = Cell(sheet, r, headers, "Document No", row.DocumentNumber);
                row.DocumentValue = Decimal(Cell(sheet, r, headers, "Document Value"), row.DocumentValue);
                row.Rate = Decimal(Cell(sheet, r, headers, "Rate"), row.Rate);
                row.TaxableValue = Decimal(Cell(sheet, r, headers, "Taxable Value"), row.TaxableValue);
                row.Igst = Decimal(Cell(sheet, r, headers, "IGST"), row.Igst);
                row.Cgst = Decimal(Cell(sheet, r, headers, "CGST"), row.Cgst);
                row.Sgst = Decimal(Cell(sheet, r, headers, "SGST"), row.Sgst);
                row.Cess = Decimal(Cell(sheet, r, headers, "Cess"), row.Cess);
                row.RoundOff = Decimal(Cell(sheet, r, headers, "Round Off"), row.RoundOff);
                row.PurchaseLedger = Cell(sheet, r, headers, "Purchase Ledger", row.PurchaseLedger);
                row.IgstLedger = Cell(sheet, r, headers, "IGST Ledger", row.IgstLedger);
                row.CgstLedger = Cell(sheet, r, headers, "CGST Ledger", row.CgstLedger);
                row.SgstLedger = Cell(sheet, r, headers, "SGST Ledger", row.SgstLedger);
                row.GstApplicability = Cell(sheet, r, headers, "GST Applicability", row.GstApplicability);
                row.ReconciliationStatus = Cell(sheet, r, headers, "Reconciliation Status", row.ReconciliationStatus);
                row.PlaceOfSupply = Cell(sheet, r, headers, "Place of Supply", row.PlaceOfSupply);
                row.ReverseCharge = Cell(sheet, r, headers, "Reverse Charge", row.ReverseCharge);
                var previousItemName = row.ItemName;
                row.ItemName = Cell(sheet, r, headers, "Item", row.ItemName);
                row.HsnCode = Cell(sheet, r, headers, "HSN/SAC", row.HsnCode);
                row.ActualQuantity = Decimal(Cell(sheet, r, headers, "Actual Qty"), row.ActualQuantity);
                row.BilledQuantity = Decimal(Cell(sheet, r, headers, "Billed Qty"), row.BilledQuantity);
                row.FreeQuantity = Decimal(Cell(sheet, r, headers, "Free Qty"), row.FreeQuantity);
                row.Unit = Cell(sheet, r, headers, "Unit", row.Unit);
                row.RateUnit = Cell(sheet, r, headers, "Rate Unit", row.RateUnit);
                row.ItemRate = Decimal(Cell(sheet, r, headers, "Item Rate"), row.ItemRate);
                row.ItemAmount = Decimal(Cell(sheet, r, headers, "Item Amount"), row.ItemAmount);
                row.ItemPurchaseLedger = Cell(sheet, r, headers, "Item Purchase Ledger", row.ItemPurchaseLedger);
                row.Godown = Cell(sheet, r, headers, "Godown", row.Godown);
                row.BatchName = Cell(sheet, r, headers, "Batch", row.BatchName);
                row.PartNumberSku = Cell(sheet, r, headers, "Part No/SKU", row.PartNumberSku);
                if (!row.ItemName.Equals(previousItemName, StringComparison.OrdinalIgnoreCase))
                    RebindPurchaseStockIdentity(client, row);
                if (PurchaseComparable(row) != before) updates.Add(row);
            }
            catch (Exception ex) { errors.Add($"Purchase row {r}: {ex.Message}"); }
        }
        if (updates.Count == 0) return 0;
        return _purchase.UpdateRecords(client.Id, updates);
    }

    private int ImportSales(ClientProfile client, OpenXmlWorkbookData workbook, ReviewSyncState syncState, ref int skipped, List<string> errors)
    {
        if (!workbook.TryGetWorksheet("Sales", out var sheet)) return 0;
        var headers = Headers(sheet);
        var changedRows = GetChangedRowNumbers(sheet, headers, "Sales", "__SalesId", syncState);
        if (changedRows.Count == 0) return 0;
        var existing = Safe(() => _sales.GetRecords(client.Id, client.FinancialYear), []).ToDictionary(row => row.PersistentId);
        var updates = new List<SalesImportRecord>();
        foreach (var r in changedRows)
        {
            if (!TryLong(Cell(sheet, r, headers, "__SalesId"), out var id) || !existing.TryGetValue(id, out var row)) { skipped++; continue; }
            try
            {
                // Originating registration is source-owned. Review workbook edits may
                // change transaction fields, never transfer a row to another GSTIN.
                if (headers.ContainsKey("Client GSTIN") &&
                    !NormalizeGstin(Cell(sheet, r, headers, "Client GSTIN"))
                        .Equals(NormalizeGstin(row.ClientGstin), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Client GSTIN is read-only; reimport the correct registration instead.");
                var before = SalesComparable(row);
                row.GstDate = DateOrNull(Cell(sheet, r, headers, "GST Date"), row.GstDate);
                row.DocumentDate = DateOrNull(Cell(sheet, r, headers, "Document Date"), row.DocumentDate);
                row.CustomerGstin = Cell(sheet, r, headers, "Customer GSTIN", row.CustomerGstin);
                row.CustomerName = Cell(sheet, r, headers, "Customer", row.CustomerName);
                row.DocumentType = Cell(sheet, r, headers, "Document Type", row.DocumentType);
                row.DocumentNumber = Cell(sheet, r, headers, "Document No", row.DocumentNumber);
                row.DocumentValue = Decimal(Cell(sheet, r, headers, "Document Value"), row.DocumentValue);
                row.Rate = Decimal(Cell(sheet, r, headers, "Rate"), row.Rate);
                row.TaxableValue = Decimal(Cell(sheet, r, headers, "Taxable Value"), row.TaxableValue);
                row.Igst = Decimal(Cell(sheet, r, headers, "IGST"), row.Igst);
                row.Cgst = Decimal(Cell(sheet, r, headers, "CGST"), row.Cgst);
                row.Sgst = Decimal(Cell(sheet, r, headers, "SGST"), row.Sgst);
                row.Cess = Decimal(Cell(sheet, r, headers, "Cess"), row.Cess);
                row.CessRate = Decimal(Cell(sheet, r, headers, "Cess Rate"), row.CessRate);
                row.RoundOff = Decimal(Cell(sheet, r, headers, "Round Off"), row.RoundOff);
                row.SalesLedger = Cell(sheet, r, headers, "Sales Ledger", row.SalesLedger);
                row.IgstLedger = Cell(sheet, r, headers, "IGST Ledger", row.IgstLedger);
                row.CgstLedger = Cell(sheet, r, headers, "CGST Ledger", row.CgstLedger);
                row.SgstLedger = Cell(sheet, r, headers, "SGST Ledger", row.SgstLedger);
                row.PlaceOfSupply = Cell(sheet, r, headers, "Place of Supply", row.PlaceOfSupply);
                row.ReturnPeriod = Cell(sheet, r, headers, "Return Period", row.ReturnPeriod);
                var previousItemName = row.ItemName;
                row.ItemName = Cell(sheet, r, headers, "Item", row.ItemName);
                row.HsnCode = Cell(sheet, r, headers, "HSN/SAC", row.HsnCode);
                var actualQuantityText = Cell(sheet, r, headers, "Actual Qty", string.Empty);
                row.ActualQuantity = Decimal(string.IsNullOrWhiteSpace(actualQuantityText) ? Cell(sheet, r, headers, "Qty") : actualQuantityText, row.ActualQuantity);
                row.BilledQuantity = Decimal(Cell(sheet, r, headers, "Billed Qty"), row.BilledQuantity);
                row.FreeQuantity = Decimal(Cell(sheet, r, headers, "Free Qty"), row.FreeQuantity);
                row.Unit = Cell(sheet, r, headers, "Unit", row.Unit);
                row.RateUnit = Cell(sheet, r, headers, "Rate Unit", row.RateUnit);
                row.ItemRate = Decimal(Cell(sheet, r, headers, "Item Rate"), row.ItemRate);
                row.ItemAmount = Decimal(Cell(sheet, r, headers, "Item Amount"), row.ItemAmount);
                row.ItemSalesLedger = Cell(sheet, r, headers, "Item Sales Ledger", row.ItemSalesLedger);
                row.AccountingAmount = Decimal(Cell(sheet, r, headers, "Accounting Amount"), row.AccountingAmount);
                row.Godown = Cell(sheet, r, headers, "Godown", row.Godown);
                row.BatchName = Cell(sheet, r, headers, "Batch", row.BatchName);
                row.BatchActualQuantity = Decimal(Cell(sheet, r, headers, "Batch Actual Qty"), row.BatchActualQuantity);
                row.BatchBilledQuantity = Decimal(Cell(sheet, r, headers, "Batch Billed Qty"), row.BatchBilledQuantity);
                row.BatchAmount = Decimal(Cell(sheet, r, headers, "Batch Amount"), row.BatchAmount);
                row.DiscountPercent = Decimal(Cell(sheet, r, headers, "Discount %"), row.DiscountPercent);
                row.DiscountAmount = Decimal(Cell(sheet, r, headers, "Discount Amount"), row.DiscountAmount);
                row.OrderNumber = Cell(sheet, r, headers, "Order No", row.OrderNumber);
                row.TrackingNumber = Cell(sheet, r, headers, "Tracking No", row.TrackingNumber);
                row.ManufacturingDate = DateOrNull(Cell(sheet, r, headers, "Manufacturing Date"), row.ManufacturingDate);
                row.ExpiryDate = DateOrNull(Cell(sheet, r, headers, "Expiry Date"), row.ExpiryDate);
                row.ItemDescription = Cell(sheet, r, headers, "Item Description", row.ItemDescription);
                row.PartNumberSku = Cell(sheet, r, headers, "Part No/SKU", row.PartNumberSku);
                row.Mrp = Decimal(Cell(sheet, r, headers, "MRP"), row.Mrp);
                row.SerialLotNumber = Cell(sheet, r, headers, "Serial/Lot No", row.SerialLotNumber);
                row.CostCentre = Cell(sheet, r, headers, "Cost Centre", row.CostCentre);
                row.PriceLevel = Cell(sheet, r, headers, "Price Level", row.PriceLevel);
                row.AlternateQuantity = Decimal(Cell(sheet, r, headers, "Alternate Qty"), row.AlternateQuantity);
                row.AlternateUnit = Cell(sheet, r, headers, "Alternate Unit", row.AlternateUnit);
                row.DeliveryNoteNumber = Cell(sheet, r, headers, "Delivery Note No", row.DeliveryNoteNumber);
                row.BuyerOrderNumber = Cell(sheet, r, headers, "Buyer Order No", row.BuyerOrderNumber);
                row.BuyerOrderDate = DateOrNull(Cell(sheet, r, headers, "Buyer Order Date"), row.BuyerOrderDate);
                row.Narration = Cell(sheet, r, headers, "Narration", row.Narration);
                row.ImportSource = Cell(sheet, r, headers, "Import Source", row.ImportSource);
                if (!row.ItemName.Equals(previousItemName, StringComparison.OrdinalIgnoreCase))
                    RebindSalesStockIdentity(client, row);
                if (SalesComparable(row) != before) updates.Add(row);
            }
            catch (Exception ex) { errors.Add($"Sales row {r}: {ex.Message}"); }
        }
        if (updates.Count == 0) return 0;
        return _sales.UpdateRecords(client.Id, updates);
    }

    private void RebindPurchaseStockIdentity(ClientProfile client, PurchaseImportRecord row)
    {
        var item = Safe(() => _stockMasters.GetStockItems(client.Id, client.FinancialYear, false, row.ItemName), [])
            .FirstOrDefault(candidate => candidate.Name.Equals(row.ItemName.Trim(), StringComparison.OrdinalIgnoreCase));
        row.StockItemPersistentId = item?.PersistentId ?? 0;
        row.TallyStockMasterId = item?.TallyMasterId ?? string.Empty;
        if (item is not null && string.IsNullOrWhiteSpace(row.Unit)) row.Unit = item.BaseUnit;
        if (item is not null && string.IsNullOrWhiteSpace(row.RateUnit)) row.RateUnit = string.IsNullOrWhiteSpace(item.RateUnit) ? item.BaseUnit : item.RateUnit;
    }

    private void RebindSalesStockIdentity(ClientProfile client, SalesImportRecord row)
    {
        var item = Safe(() => _stockMasters.GetStockItems(client.Id, client.FinancialYear, false, row.ItemName), [])
            .FirstOrDefault(candidate => candidate.Name.Equals(row.ItemName.Trim(), StringComparison.OrdinalIgnoreCase));
        row.StockItemPersistentId = item?.PersistentId ?? 0;
        row.TallyStockMasterId = item?.TallyMasterId ?? string.Empty;
        if (item is not null && string.IsNullOrWhiteSpace(row.Unit)) row.Unit = item.BaseUnit;
        if (item is not null && string.IsNullOrWhiteSpace(row.RateUnit)) row.RateUnit = string.IsNullOrWhiteSpace(item.RateUnit) ? item.BaseUnit : item.RateUnit;
    }

    private int ImportJournal(ClientProfile client, OpenXmlWorkbookData workbook, ReviewSyncState syncState, ref int skipped, List<string> errors)
    {
        if (!workbook.TryGetWorksheet("Journal", out var sheet)) return 0;
        var headers = Headers(sheet);
        var changedRows = GetChangedRowNumbers(sheet, headers, "Journal", "__JournalId", syncState);
        if (changedRows.Count == 0) return 0;
        var existing = Safe(() => _journal.GetVouchers(client.Id, client.FinancialYear), []).ToDictionary(row => row.PersistentId);
        var changed = 0;
        foreach (var r in changedRows)
        {
            if (!TryLong(Cell(sheet, r, headers, "__JournalId"), out var id) || !existing.TryGetValue(id, out var row)) { skipped++; continue; }
            try
            {
                var before = $"{row.VoucherDate:O}\u001f{row.VoucherNumber}\u001f{row.VoucherType}\u001f{row.PeriodLabel}\u001f{row.Narration}";
                if (TryDate(Cell(sheet, r, headers, "Date"), out var date)) row.VoucherDate = date;
                row.VoucherNumber = Cell(sheet, r, headers, "Number", row.VoucherNumber);
                row.VoucherType = Cell(sheet, r, headers, "Type", row.VoucherType);
                row.PeriodLabel = Cell(sheet, r, headers, "Period", row.PeriodLabel);
                row.Narration = Cell(sheet, r, headers, "Narration", row.Narration);
                var after = $"{row.VoucherDate:O}\u001f{row.VoucherNumber}\u001f{row.VoucherType}\u001f{row.PeriodLabel}\u001f{row.Narration}";
                if (before == after) continue;
                _journal.SaveVoucher(row); // Existing lines are preserved from the loaded voucher.
                changed++;
            }
            catch (Exception ex) { errors.Add($"Journal row {r}: {ex.Message}"); }
        }
        return changed;
    }

    private int ImportCashEntry(ClientProfile client, OpenXmlWorkbookData workbook, ReviewSyncState syncState, ref int skipped, List<string> errors)
    {
        if (!workbook.TryGetWorksheet("Cash Entry", out var sheet)) return 0;
        var headers = Headers(sheet);
        var changedRows = GetChangedRowNumbers(sheet, headers, "Cash Entry", "__CashEntryId", syncState);
        if (changedRows.Count == 0) return 0;
        var existing = Safe(() => _banking.GetCashEntries(client.Id, client.FinancialYear), []).ToDictionary(row => row.Id);
        var changed = 0;
        foreach (var r in changedRows)
        {
            if (!TryLong(Cell(sheet, r, headers, "__CashEntryId"), out var id) || !existing.TryGetValue(id, out var row)) { skipped++; continue; }
            try
            {
                var before = CashComparable(row);
                if (TryDate(Cell(sheet, r, headers, "Date"), out var date)) row.EntryDate = date;
                row.EntryLedger = Cell(sheet, r, headers, "Entry Ledger", row.EntryLedger);
                row.Debit = Decimal(Cell(sheet, r, headers, "Debit"), row.Debit);
                row.Credit = Decimal(Cell(sheet, r, headers, "Credit"), row.Credit);
                row.Narration = Cell(sheet, r, headers, "Narration", row.Narration);
                row.CommonLedger = Cell(sheet, r, headers, "Common Ledger", row.CommonLedger);
                row.MappedMasterLedger = Cell(sheet, r, headers, "Mapped Master Ledger", row.MappedMasterLedger);
                row.VoucherType = Cell(sheet, r, headers, "Voucher Type", row.VoucherType);
                row.Status = Cell(sheet, r, headers, "Status", row.Status);
                if (CashComparable(row) == before) continue;
                _banking.SaveCashEntry(row);
                changed++;
            }
            catch (Exception ex) { errors.Add($"Cash Entry row {r}: {ex.Message}"); }
        }
        return changed;
    }

    private void InitializeReviewSyncState(string path)
    {
        try
        {
            var workbook = _reader.Read(path, CancellationToken.None);
            var state = new ReviewSyncState
            {
                CreatedUtc = DateTime.UtcNow,
                OriginalFileName = Path.GetFileName(path),
                OriginalWorkbookHash = ComputeWorkbookHash(path)
            };
            CaptureReviewSyncState(workbook, state);
            SaveReviewSyncState(path, state);
        }
        catch
        {
            // Export itself remains valid even if the optional delta baseline cannot
            // be written. The first later sync will fall back to a full compare once.
        }
    }

    private static IReadOnlyList<int> GetChangedRowNumbers(
        OpenXmlWorksheetData sheet,
        IReadOnlyDictionary<string, int> headers,
        string sheetName,
        string idHeader,
        ReviewSyncState syncState)
    {
        var result = new List<int>();
        if (!headers.TryGetValue(idHeader, out var idColumn)) return result;
        for (var row = 2; row <= sheet.MaxRow; row++)
        {
            var id = sheet.GetCell(row, idColumn).Trim();
            if (id.Length == 0) continue;
            var currentHash = WorksheetRowHash(sheet, row, headers, idHeader);
            var key = ReviewSyncKey(sheetName, id);
            if (!syncState.Hashes.TryGetValue(key, out var baseline) ||
                !baseline.Equals(currentHash, StringComparison.Ordinal))
                result.Add(row);
        }
        return result;
    }

    private static void CaptureReviewSyncState(OpenXmlWorkbookData workbook, ReviewSyncState state)
    {
        state.Hashes.Clear();
        foreach (var (sheetName, idHeader) in ReviewSheetIdentityColumns)
        {
            if (!workbook.TryGetWorksheet(sheetName, out var sheet)) continue;
            var headers = Headers(sheet);
            if (!headers.TryGetValue(idHeader, out var idColumn)) continue;
            for (var row = 2; row <= sheet.MaxRow; row++)
            {
                var id = sheet.GetCell(row, idColumn).Trim();
                if (id.Length == 0) continue;
                state.Hashes[ReviewSyncKey(sheetName, id)] = WorksheetRowHash(sheet, row, headers, idHeader);
            }
        }
        state.UpdatedUtc = DateTime.UtcNow;
    }

    private static string WorksheetRowHash(
        OpenXmlWorksheetData sheet,
        int row,
        IReadOnlyDictionary<string, int> headers,
        string idHeader)
    {
        var ordered = headers
            .Where(pair => !pair.Key.Equals(idHeader, StringComparison.OrdinalIgnoreCase) &&
                           !pair.Key.StartsWith("__AAH_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Value)
            .Select(pair => sheet.GetCell(row, pair.Value).Trim());
        return HashText(string.Join("\u001f", ordered));
    }

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)));

    private static string ReviewSyncKey(string sheetName, string id) =>
        $"{sheetName.Trim().ToUpperInvariant()}\u001f{id.Trim()}";

    private static readonly (string SheetName, string IdHeader)[] ReviewSheetIdentityColumns =
    [
        ("Bank Statement", "__TransactionId"),
        ("Ledger Mapping", "__MappingId"),
        ("Ledger Master", "__MasterId"),
        ("Unit Masters", "__UnitId"),
        ("Stock Master", "__StockItemId"),
        ("Purchase", "__PurchaseId"),
        ("Sales", "__SalesId"),
        ("Journal", "__JournalId"),
        ("Cash Entry", "__CashEntryId")
    ];

    private static ReviewSyncState LoadReviewSyncState(string workbookPath)
    {
        foreach (var statePath in new[] { ReviewSyncStatePath(workbookPath), ReviewSyncAliasStatePath(workbookPath) })
        {
            try
            {
                if (!File.Exists(statePath)) continue;
                var state = JsonSerializer.Deserialize<ReviewSyncState>(File.ReadAllText(statePath));
                if (state is not null) return state;
            }
            catch
            {
                // Try the path-independent filename alias next.
            }
        }
        return new ReviewSyncState();
    }

    private static void SaveReviewSyncState(string workbookPath, ReviewSyncState state)
    {
        WriteReviewSyncState(ReviewSyncStatePath(workbookPath), state);
        // Keep a filename alias even after the generated workbook is deleted. If the
        // same review workbook is later copied back from any location, the Hub can
        // recover the row baseline and apply only actual edits instead of importing
        // the whole workbook. Metadata validation still protects Client/FY identity.
        WriteReviewSyncState(ReviewSyncAliasStatePath(workbookPath), state);
    }

    private static void WriteReviewSyncState(string statePath, ReviewSyncState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var temp = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, statePath, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public static bool TryDeleteUnchangedReviewWorkbook(string workbookPath, TimeSpan minimumAge)
    {
        try
        {
            if (!IsReservedReviewExportPath(workbookPath) ||
                !Path.GetExtension(workbookPath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(workbookPath)) return false;

            var state = LoadReviewSyncState(workbookPath);
            if (string.IsNullOrWhiteSpace(state.OriginalWorkbookHash) || state.HasImportedChanges) return false;

            var created = state.CreatedUtc == default
                ? File.GetCreationTimeUtc(workbookPath)
                : state.CreatedUtc;
            if (DateTime.UtcNow - created < minimumAge) return false;

            // Excel can rewrite package metadata even when the user opens/saves without
            // changing any accounting cells. Binary equality is the fastest path; if
            // the package hash changed, compare the remembered row baseline so an
            // effectively untouched workbook can still be cleaned up. An actually
            // edited workbook is retained (and successful changed-row sync marks it).
            var currentHash = ComputeWorkbookHash(workbookPath);
            if (!currentHash.Equals(state.OriginalWorkbookHash, StringComparison.OrdinalIgnoreCase) &&
                !IsReviewWorkbookSemanticallyUnchanged(workbookPath, state)) return false;

            // Delete only the generated file. The remembered name, row baseline and
            // original hash intentionally remain in LocalAppData for future copy/paste.
            File.Delete(workbookPath);
            return !File.Exists(workbookPath);
        }
        catch (IOException)
        {
            // Excel may still have the workbook open. Folder reconciliation retries.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReviewWorkbookSemanticallyUnchanged(string workbookPath, ReviewSyncState state)
    {
        try
        {
            var workbook = new FlexibleWorkbookReader().Read(workbookPath, CancellationToken.None);
            var seen = 0;
            foreach (var (sheetName, idHeader) in ReviewSheetIdentityColumns)
            {
                if (!workbook.TryGetWorksheet(sheetName, out var sheet)) continue;
                var headers = Headers(sheet);
                if (!headers.TryGetValue(idHeader, out var idColumn)) continue;
                for (var row = 2; row <= sheet.MaxRow; row++)
                {
                    var id = sheet.GetCell(row, idColumn).Trim();
                    if (id.Length == 0) continue;
                    var key = ReviewSyncKey(sheetName, id);
                    if (!state.Hashes.TryGetValue(key, out var baseline)) return false;
                    if (!baseline.Equals(WorksheetRowHash(sheet, row, headers, idHeader), StringComparison.Ordinal)) return false;
                    seen++;
                }
            }
            return seen > 0 && seen == state.Hashes.Count;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeWorkbookHash(string workbookPath)
    {
        using var stream = new FileStream(workbookPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ReviewSyncStatePath(string workbookPath)
    {
        var root = ReviewSyncStateRoot();
        var full = Path.GetFullPath(workbookPath).ToUpperInvariant();
        return Path.Combine(root, HashText(full) + ".json");
    }

    private static string ReviewSyncAliasStatePath(string workbookPath)
    {
        var root = ReviewSyncStateRoot();
        var fileName = Path.GetFileName(workbookPath).Trim().ToUpperInvariant();
        return Path.Combine(root, "NAME-" + HashText(fileName) + ".json");
    }

    private static string ReviewSyncStateRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Accounting Automation Hub", "Cache", "ReviewSync");

    private sealed class ReviewSyncState
    {
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public string OriginalFileName { get; set; } = string.Empty;
        public string OriginalWorkbookHash { get; set; } = string.Empty;
        public bool HasImportedChanges { get; set; }
        public Dictionary<string, string> Hashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateWorkbookIdentity(ClientProfile client, OpenXmlWorkbookData workbook)
    {
        if (!workbook.TryGetWorksheet(MetadataSheet, out var meta))
            throw new InvalidOperationException("This is not an Accounting Automation Hub client review workbook.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var r = 2; r <= meta.MaxRow; r++)
        {
            var key = meta.GetCell(r, 1).Trim();
            if (key.Length > 0) values[key] = meta.GetCell(r, 2).Trim();
        }
        if (!values.TryGetValue("Kind", out var kind) || !kind.Equals(WorkbookKind, StringComparison.Ordinal))
            throw new InvalidOperationException("The workbook marker is invalid.");
        if (!values.TryGetValue("ClientId", out var clientId) || clientId != client.Id.ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException("The workbook belongs to a different client.");
        if (!values.TryGetValue("FinancialYear", out var fy) || !fy.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The workbook belongs to a different Financial Year.");
    }

    private static Dictionary<string, int> Headers(OpenXmlWorksheetData sheet)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!sheet.Rows.TryGetValue(1, out var row)) return result;
        foreach (var pair in row)
        {
            var name = pair.Value.Trim();
            if (name.Length > 0 && !result.ContainsKey(name)) result[name] = pair.Key;
        }
        return result;
    }

    private static string Cell(OpenXmlWorksheetData sheet, int row, IReadOnlyDictionary<string, int> headers, string header, string fallback = "") =>
        headers.TryGetValue(header, out var column) ? sheet.GetCell(row, column).Trim() : fallback;

    private static string NormalizeGstin(string? value) =>
        new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string BankIdentity(string? value)
    {
        var accountNumber = BankLedgerMappingService.BankAccountNumberIdentity(value);
        return accountNumber.Length >= 6 ? accountNumber : TallyMasterIdentity.Key(value ?? string.Empty);
    }

    private static bool TryLong(string text, out long value) => long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    private static int Int(string text, int fallback) => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static decimal Decimal(string text, decimal fallback) => decimal.TryParse(text.Replace(",", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static decimal? NullableDecimal(string text, decimal? fallback) => string.IsNullOrWhiteSpace(text) ? null : decimal.TryParse(text.Replace(",", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static bool Bool(string text, bool fallback) => text.Trim().ToUpperInvariant() switch { "YES" or "Y" or "TRUE" or "1" => true, "NO" or "N" or "FALSE" or "0" => false, _ => fallback };
    private static DateTime? DateOrNull(string text, DateTime? fallback) => string.IsNullOrWhiteSpace(text) ? null : TryDate(text, out var value) ? value : fallback;
    private static bool TryDate(string text, out DateTime value)
    {
        var formats = new[] { "dd.MM.yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "d.M.yyyy", "d/M/yyyy", "dd-MM-yyyy" };
        return DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value) || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out value);
    }

    private static string BankComparable(BankTransactionRecord row) => $"{row.TransactionDate:O}\u001f{row.Narration}\u001f{row.Debit}\u001f{row.Credit}\u001f{row.Mode}\u001f{row.LedgerName}\u001f{row.MappingStatus}\u001f{row.ReferenceNumber}\u001f{row.RunningBalance}\u001f{row.RunningBalanceSource}\u001f{row.RecordStatus}";
    private static string UnitComparable(StockUnitRecord row) => $"{row.Name}\u001f{row.FormalName}\u001f{row.DecimalPlaces}\u001f{row.ReportingUqc}\u001f{row.Conversion}\u001f{row.Company}\u001f{row.IsArchived}";
    private static string StockComparable(StockItemRecord row) => $"{row.Name}\u001f{row.PartNumberSku}\u001f{row.StockGroup}\u001f{row.StockCategory}\u001f{row.BaseUnit}\u001f{row.RateUnit}\u001f{row.AdditionalUnit}\u001f{row.DefaultGodown}\u001f{row.DefaultPurchaseLedger}\u001f{row.DefaultSalesLedger}\u001f{row.ReorderPoint}\u001f{row.MinimumStock}\u001f{row.MaximumStock}\u001f{row.HsnSac}\u001f{row.GstRate}\u001f{row.GstTypeOfSupply}\u001f{row.BatchWise}\u001f{row.Perishable}\u001f{row.HasManufacturingDate}\u001f{row.CostingMethod}\u001f{row.ValuationMethod}\u001f{row.IsArchived}";
    private static string PurchaseComparable(PurchaseImportRecord row) => $"{DateKey(row.GstDate)}\u001f{DateKey(row.DocumentDate)}\u001f{row.SupplierGstin}\u001f{row.SupplierName}\u001f{row.DocumentType}\u001f{row.DocumentNumber}\u001f{row.DocumentValue}\u001f{row.Rate}\u001f{row.TaxableValue}\u001f{row.Igst}\u001f{row.Cgst}\u001f{row.Sgst}\u001f{row.Cess}\u001f{row.RoundOff}\u001f{row.PurchaseLedger}\u001f{row.IgstLedger}\u001f{row.CgstLedger}\u001f{row.SgstLedger}\u001f{row.GstApplicability}\u001f{row.ReconciliationStatus}\u001f{row.PlaceOfSupply}\u001f{row.ReverseCharge}\u001f{row.ItemName}\u001f{row.HsnCode}\u001f{row.ActualQuantity}\u001f{row.BilledQuantity}\u001f{row.FreeQuantity}\u001f{row.Unit}\u001f{row.RateUnit}\u001f{row.ItemRate}\u001f{row.ItemAmount}\u001f{row.ItemPurchaseLedger}\u001f{row.Godown}\u001f{row.BatchName}\u001f{row.PartNumberSku}\u001f{row.StockItemPersistentId}";
    private static string SalesComparable(SalesImportRecord row) => $"{DateKey(row.GstDate)}\u001f{DateKey(row.DocumentDate)}\u001f{row.CustomerGstin}\u001f{row.CustomerName}\u001f{row.DocumentType}\u001f{row.DocumentNumber}\u001f{row.DocumentValue}\u001f{row.Rate}\u001f{row.TaxableValue}\u001f{row.Igst}\u001f{row.Cgst}\u001f{row.Sgst}\u001f{row.Cess}\u001f{row.CessRate}\u001f{row.RoundOff}\u001f{row.SalesLedger}\u001f{row.IgstLedger}\u001f{row.CgstLedger}\u001f{row.SgstLedger}\u001f{row.PlaceOfSupply}\u001f{row.ReturnPeriod}\u001f{row.ItemName}\u001f{row.HsnCode}\u001f{row.ActualQuantity}\u001f{row.BilledQuantity}\u001f{row.FreeQuantity}\u001f{row.Unit}\u001f{row.RateUnit}\u001f{row.ItemRate}\u001f{row.ItemAmount}\u001f{row.ItemSalesLedger}\u001f{row.AccountingAmount}\u001f{row.Godown}\u001f{row.BatchName}\u001f{row.BatchActualQuantity}\u001f{row.BatchBilledQuantity}\u001f{row.BatchAmount}\u001f{row.DiscountPercent}\u001f{row.DiscountAmount}\u001f{row.OrderNumber}\u001f{row.TrackingNumber}\u001f{DateKey(row.ManufacturingDate)}\u001f{DateKey(row.ExpiryDate)}\u001f{row.ItemDescription}\u001f{row.PartNumberSku}\u001f{row.Mrp}\u001f{row.SerialLotNumber}\u001f{row.CostCentre}\u001f{row.PriceLevel}\u001f{row.AlternateQuantity}\u001f{row.AlternateUnit}\u001f{row.DeliveryNoteNumber}\u001f{row.BuyerOrderNumber}\u001f{DateKey(row.BuyerOrderDate)}\u001f{row.Narration}\u001f{row.ImportSource}\u001f{row.StockItemPersistentId}";
    private static string DateKey(DateTime? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string CashComparable(CashEntryRecord row) => $"{row.EntryDate:O}\u001f{row.EntryLedger}\u001f{row.Debit}\u001f{row.Credit}\u001f{row.Narration}\u001f{row.CommonLedger}\u001f{row.MappedMasterLedger}\u001f{row.VoucherType}\u001f{row.Status}";

    private static string BuildExportPath(string folder, string baseName, string extension, bool overwriteExisting)
    {
        if (overwriteExisting) return Path.Combine(folder, baseName + extension);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(folder, $"{baseName}_{stamp}{extension}");
        if (!File.Exists(candidate)) return candidate;
        for (var suffix = 1; suffix < 1000; suffix++)
        {
            candidate = Path.Combine(folder, $"{baseName}_{stamp}_{suffix:000}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{baseName}_{stamp}_{Guid.NewGuid():N}{extension}");
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var text = new string((value ?? string.Empty).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return text.Length == 0 ? "Unknown" : text.Replace(' ', '_');
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private static string Date(DateTime? value) => value?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Date(DateTime value) => value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    private static string Num(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static void WriteCsvRow(TextWriter writer, IEnumerable<string> cells) => writer.WriteLine(string.Join(",", cells.Select(Csv)));
    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }

    private sealed record DiagnosticSheet(string Name, IReadOnlyList<IReadOnlyList<string>> Rows, bool Hidden = false, int HiddenLeadingColumns = 0);

    private static class SimpleXlsxWriter
    {
        public static void Write(string path, IReadOnlyList<DiagnosticSheet> sheets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            if (File.Exists(path)) File.Delete(path);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            WriteEntry(archive, "[Content_Types].xml", ContentTypes(sheets.Count));
            WriteEntry(archive, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            WriteEntry(archive, "xl/workbook.xml", Workbook(sheets));
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships(sheets.Count));
            WriteEntry(archive, "xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills><borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs><cellXfs count=\"2\"><xf xfId=\"0\"/><xf xfId=\"0\" fontId=\"1\" applyFont=\"1\"/></cellXfs></styleSheet>");
            for (var i = 0; i < sheets.Count; i++) WriteEntry(archive, $"xl/worksheets/sheet{i + 1}.xml", Worksheet(sheets[i]));
        }

        private static string ContentTypes(int count)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (var i = 1; i <= count; i++) sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            return sb.Append("</Types>").ToString();
        }

        private static string Workbook(IReadOnlyList<DiagnosticSheet> sheets)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            for (var i = 0; i < sheets.Count; i++)
            {
                var state = sheets[i].Hidden ? " state=\"hidden\"" : string.Empty;
                sb.Append($"<sheet name=\"{Xml(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"{state}/>");
            }
            return sb.Append("</sheets></workbook>").ToString();
        }

        private static string WorkbookRelationships(int count)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (var i = 1; i <= count; i++) sb.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
            sb.Append($"<Relationship Id=\"rId{count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            return sb.Append("</Relationships>").ToString();
        }

        private static string Worksheet(DiagnosticSheet sheet)
        {
            var rows = sheet.Rows;
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (sheet.HiddenLeadingColumns > 0)
                sb.Append($"<cols><col min=\"1\" max=\"{sheet.HiddenLeadingColumns}\" hidden=\"1\" width=\"2\" customWidth=\"1\"/></cols>");
            sb.Append("<sheetData>");
            for (var r = 0; r < rows.Count; r++)
            {
                sb.Append($"<row r=\"{r + 1}\">");
                for (var c = 0; c < rows[r].Count; c++)
                {
                    var cellRef = ColumnName(c + 1) + (r + 1).ToString(CultureInfo.InvariantCulture);
                    var style = r == 0 ? " s=\"1\"" : string.Empty;
                    sb.Append($"<c r=\"{cellRef}\" t=\"inlineStr\"{style}><is><t xml:space=\"preserve\">{Xml(rows[r][c])}</t></is></c>");
                }
                sb.Append("</row>");
            }
            return sb.Append("</sheetData></worksheet>").ToString();
        }

        private static string ColumnName(int column)
        {
            var name = string.Empty;
            while (column > 0) { column--; name = (char)('A' + column % 26) + name; column /= 26; }
            return name;
        }

        private static string Xml(string? value)
        {
            var text = value ?? string.Empty;
            var sb = new StringBuilder(text.Length);
            foreach (var ch in text) sb.Append(XmlConvert.IsXmlChar(ch) ? ch : ' ');
            return System.Security.SecurityElement.Escape(sb.ToString()) ?? string.Empty;
        }

        private static void WriteEntry(ZipArchive archive, string name, string text)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(text);
        }
    }
}

public sealed record ClientReviewImportResult(int ChangedRows, int SkippedRows, int ErrorRows, string Message, string WorkbookPath);
