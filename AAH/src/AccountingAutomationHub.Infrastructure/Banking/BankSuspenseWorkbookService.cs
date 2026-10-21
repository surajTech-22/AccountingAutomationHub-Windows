using System.Globalization;
using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

namespace AccountingAutomationHub.Infrastructure.Banking;

/// <summary>
/// Controlled importer for AAH Bank Suspense Mapping workbooks.  The workbook is
/// identified by its hidden metadata sheet and may change only the Ledger mapping
/// of an existing Bank Statement transaction.  Date/Narration/Debit/Credit are
/// authority fields and are never imported back into the transaction.
/// </summary>
public sealed class BankSuspenseWorkbookService
{
    public const string MappingSheetName = "Suspense Mapping";
    public const string MetaSheetName = "__AAH_SUSPENSE_META";
    public const string WorkbookKind = "Accounting Automation Hub Bank Suspense Mapping";

    private readonly IBankingRepository _repository;
    private readonly IMasterRepository _masterRepository;
    private readonly BankLedgerMappingService _mappingService;

    public BankSuspenseWorkbookService(
        IBankingRepository repository,
        IMasterRepository masterRepository,
        BankLedgerMappingService mappingService)
    {
        _repository = repository;
        _masterRepository = masterRepository;
        _mappingService = mappingService;
    }

    public static bool IsSuspenseWorkbook(string path)
    {
        try
        {
            var workbook = new FlexibleWorkbookReader().Read(path, CancellationToken.None);
            return ReadWorkbookMeta(workbook, MetaSheetName, "Kind")
                .Equals(WorkbookKind, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public BankSuspenseImportResult Import(
        string path,
        long clientId,
        string financialYear)
    {
        if (clientId <= 0) throw new InvalidOperationException("A valid client is required for Suspense import.");
        if (string.IsNullOrWhiteSpace(financialYear)) throw new InvalidOperationException("Financial Year is required for Suspense import.");

        var reader = new FlexibleWorkbookReader();
        var workbook = reader.Read(path, CancellationToken.None);
        if (!workbook.TryGetWorksheet(MappingSheetName, out var sheet))
            throw new InvalidOperationException("Suspense Mapping sheet is missing.");

        var kind = ReadWorkbookMeta(workbook, MetaSheetName, "Kind");
        var clientIdText = ReadWorkbookMeta(workbook, MetaSheetName, "ClientId");
        var workbookFinancialYear = ReadWorkbookMeta(workbook, MetaSheetName, "FinancialYear");
        if (!kind.Equals(WorkbookKind, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This is not an AAH Bank Suspense Mapping workbook.");
        if (!long.TryParse(clientIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var workbookClientId) ||
            workbookClientId != clientId ||
            !workbookFinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This Suspense workbook belongs to a different Client/FY.");

        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (sheet.Rows.TryGetValue(1, out var headerRow))
        {
            foreach (var pair in headerRow)
            {
                var header = pair.Value.Trim();
                if (header.Length > 0 && !headers.ContainsKey(header)) headers[header] = pair.Key;
            }
        }

        string Cell(int row, string name) =>
            headers.TryGetValue(name, out var col) ? sheet.GetCell(row, col).Trim() : string.Empty;

        var ledgerColumn = headers.ContainsKey("Ledger")
            ? "Ledger"
            : headers.ContainsKey("New Ledger")
                ? "New Ledger"
                : string.Empty;
        if (!headers.ContainsKey("Transaction DB ID") || ledgerColumn.Length == 0)
            throw new InvalidOperationException("Required Transaction DB ID / Ledger columns are missing.");

        var currentTransactions = _repository.GetTransactions(clientId, financialYear, includeArchived: false)
            .Where(row => row.Id > 0)
            .ToDictionary(row => row.Id);
        var ledgerMasters = _masterRepository.Get(clientId, financialYear, includeArchived: false, string.Empty)
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !master.IsArchived)
            .Where(master => !string.IsNullOrWhiteSpace(master.Name))
            .GroupBy(master => TallyMasterIdentity.Key(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var updated = 0;
        var unchanged = 0;
        var conflicts = 0;
        var errors = new List<string>();

        for (var rowNumber = 2; rowNumber <= sheet.MaxRow; rowNumber++)
        {
            var idText = Cell(rowNumber, "Transaction DB ID");
            var requested = AccountingText.DisplayName(Cell(rowNumber, ledgerColumn));
            if (idText.Length == 0 && requested.Length == 0) continue;
            if (requested.Length == 0) { unchanged++; continue; }

            if (!long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0 ||
                !currentTransactions.TryGetValue(id, out var transaction))
            {
                errors.Add($"row {rowNumber}: transaction ID is not valid for this Client/FY");
                continue;
            }

            var exportedTransactionId = Cell(rowNumber, "Transaction ID");
            if (exportedTransactionId.Length > 0 &&
                !exportedTransactionId.Equals(transaction.TransactionId, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"row {rowNumber}: permanent Transaction ID no longer matches");
                continue;
            }

            // A later in-app mapping always wins over an older exported workbook.
            if (!IsEmptyLedgerForExport(transaction))
            {
                if (TallyMasterIdentity.Equivalent(transaction.LedgerName, requested)) unchanged++;
                else conflicts++;
                continue;
            }

            var ledgerKey = TallyMasterIdentity.Key(requested);
            if (ledgerKey.Length == 0 || !ledgerMasters.TryGetValue(ledgerKey, out var matches) || matches.Length == 0)
            {
                errors.Add($"row {rowNumber}: Ledger '{requested}' does not exist in Master");
                continue;
            }
            if (matches.Length != 1)
            {
                errors.Add($"row {rowNumber}: Ledger '{requested}' has duplicate Master identities and was not guessed");
                continue;
            }

            var canonical = matches[0].Name.Trim();
            if (canonical.Length == 0 || IsSuspenseGroupName(canonical))
            {
                errors.Add($"row {rowNumber}: choose a non-Suspense Ledger");
                continue;
            }

            transaction.LedgerName = canonical;
            transaction.MappingStatus = "Mapped";
            _repository.SaveTransaction(transaction);
            updated++;
        }

        if (updated > 0)
            _mappingService.Rebuild(clientId, financialYear);

        return new BankSuspenseImportResult(updated, unchanged, conflicts, errors);
    }

    public static string ReadWorkbookMeta(OpenXmlWorkbookData workbook, string sheetName, string key)
    {
        if (!workbook.TryGetWorksheet(sheetName, out var meta)) return string.Empty;
        for (var row = 1; row <= meta.MaxRow; row++)
            if (meta.GetCell(row, 1).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return meta.GetCell(row, 2).Trim();
        return string.Empty;
    }

    private static bool IsEmptyLedgerForExport(BankTransactionRecord row)
    {
        var ledger = row.LedgerName?.Trim() ?? string.Empty;
        return ledger.Length == 0 || IsSuspenseGroupName(ledger);
    }

    private static bool IsSuspenseGroupName(string value) =>
        value.Equals("Suspense A/c", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Suspense A/C", StringComparison.OrdinalIgnoreCase);
}

public sealed record BankSuspenseImportResult(
    int Updated,
    int Unchanged,
    int Conflicts,
    IReadOnlyList<string> Errors)
{
    public bool HasReview => Conflicts > 0 || Errors.Count > 0;
    public string Summary =>
        $"Suspense import • mapped {Updated:N0}, unchanged {Unchanged:N0}, conflicts {Conflicts:N0}, errors {Errors.Count:N0}.";
}
