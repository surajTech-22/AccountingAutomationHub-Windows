using System.Globalization;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Spreadsheets;
using AccountingAutomationHub.Infrastructure.Imports;

namespace AccountingAutomationHub.Infrastructure.Governance;

/// <summary>
/// Conservative content-first importer used by Universal Dump after the source has
/// already been classified as Cash or Journal. It does not manufacture Masters.
/// Unknown ledgers are persisted as Draft/Review Required so the user can resolve
/// them through the normal mapping/Master workflow.
/// </summary>
public sealed class UniversalCashJournalImportService
{
    private readonly FlexibleWorkbookReader _reader = new();
    private readonly CashEntryService _cashService;
    private readonly SqliteJournalRepository _journalRepository;
    private readonly SqliteMasterRepository _masterRepository;

    public UniversalCashJournalImportService(
        SqliteBankingRepository bankingRepository,
        SqliteMasterRepository masterRepository,
        SqliteJournalRepository journalRepository)
    {
        _cashService = new CashEntryService(bankingRepository, masterRepository);
        _masterRepository = masterRepository;
        _journalRepository = journalRepository;
    }

    public UniversalModuleImportResult ImportCash(
        long clientId,
        string financialYear,
        string path,
        string runId,
        string jobId,
        CancellationToken cancellationToken)
    {
        var workbook = _reader.Read(path, cancellationToken);
        return ImportCash(clientId, financialYear, workbook, path, runId, jobId, cancellationToken);
    }

    public UniversalModuleImportResult ImportCash(
        long clientId,
        string financialYear,
        OpenXmlWorkbookData workbook,
        string sourcePath,
        string runId,
        string jobId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        using var headerMappingScope = ImportHeaderMappingRuntime.BeginSource(sourcePath);
        var settings = _cashService.EnsureSettings(clientId, financialYear);
        var imported = 0;
        var review = 0;
        var skipped = 0;

        foreach (var sheet in workbook.Worksheets.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = FindHeader(sheet, CashHeaderScore);
            if (header is null) continue;
            var map = BuildHeaderMap(header.Value.Cells, ImportHeaderMappingCatalog.Cash);
            var dateColumn = FindColumn(map, "DATE", "ENTRY DATE", "VOUCHER DATE");
            var ledgerColumn = FindColumn(map, "ENTRY LEDGER", "LEDGER NAME", "LEDGER", "PARTICULARS");
            var debitColumn = FindColumn(map, "DEBIT", "DEBIT AMOUNT", "DR AMOUNT");
            var creditColumn = FindColumn(map, "CREDIT", "CREDIT AMOUNT", "CR AMOUNT");
            var amountColumn = FindColumn(map, "AMOUNT", "VALUE");
            var drCrColumn = FindColumn(map, "DR/CR", "DR CR", "TYPE", "DIRECTION");
            var narrationColumn = FindColumn(map, "NARRATION", "DESCRIPTION", "REMARKS");
            var commonColumn = FindColumn(map, "COMMON LEDGER", "CASH LEDGER", "CASH-IN-HAND", "CASH IN HAND");
            var referenceColumn = FindColumn(map, "REFERENCE NUMBER", "REFERENCE NO", "REF NO", "REFERENCE");

            if (dateColumn <= 0 || ledgerColumn <= 0 ||
                (debitColumn <= 0 && creditColumn <= 0 && amountColumn <= 0))
                continue;

            foreach (var pair in sheet.Rows.Where(pair => pair.Key > header.Value.RowNumber).OrderBy(pair => pair.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cells = pair.Value;
                var dateText = Cell(cells, dateColumn);
                var ledger = Cell(cells, ledgerColumn);
                if (ledger.Length == 0 && dateText.Length == 0) { skipped++; continue; }
                if (!TryDate(dateText, out var date)) { skipped++; continue; }

                decimal debit = 0m, credit = 0m;
                if (debitColumn > 0) TryAmount(Cell(cells, debitColumn), out debit);
                if (creditColumn > 0) TryAmount(Cell(cells, creditColumn), out credit);
                if (debit <= 0m && credit <= 0m && amountColumn > 0 && TryAmount(Cell(cells, amountColumn), out var amount))
                {
                    var direction = drCrColumn > 0 ? Cell(cells, drCrColumn) : string.Empty;
                    if (direction.StartsWith("C", StringComparison.OrdinalIgnoreCase) ||
                        direction.StartsWith("R", StringComparison.OrdinalIgnoreCase)) credit = Math.Abs(amount);
                    else debit = Math.Abs(amount);
                }
                if (debit <= 0m && credit <= 0m) { skipped++; continue; }

                var entry = new CashEntryRecord
                {
                    ClientId = clientId,
                    FinancialYear = financialYear,
                    EntryDate = date.Date,
                    EntryLedger = ledger,
                    Debit = Math.Abs(debit),
                    Credit = Math.Abs(credit),
                    Narration = narrationColumn > 0 ? Cell(cells, narrationColumn) : string.Empty,
                    CommonLedger = commonColumn > 0 ? Cell(cells, commonColumn) : settings.DefaultCommonLedger,
                    ReferenceNumber = referenceColumn > 0 ? Cell(cells, referenceColumn) : string.Empty,
                    SourceRunId = runId,
                    SourceJobId = jobId,
                    WorkflowStatus = "Draft",
                    CreatedBy = Environment.UserName,
                    IsUserEdited = false
                };
                try
                {
                    _cashService.ValidateAndSave(entry, settings);
                    imported++;
                    if (!entry.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase)) review++;
                }
                catch
                {
                    // Keep the importer non-destructive. A malformed individual row is
                    // skipped while the job detail reports the total skipped rows.
                    skipped++;
                }
            }
        }

        if (imported == 0)
            return new UniversalModuleImportResult(false, 0, review, skipped, "No Cash rows matching the supported Cash Entry columns were found.");
        return new UniversalModuleImportResult(true, imported, review, skipped,
            review > 0
                ? $"Imported {imported:N0} Cash row(s); {review:N0} require ledger/Master review."
                : $"Imported {imported:N0} Cash row(s) and all are Ready.");
    }

    public UniversalModuleImportResult ImportJournal(
        long clientId,
        string financialYear,
        string path,
        string runId,
        string jobId,
        CancellationToken cancellationToken)
    {
        var workbook = _reader.Read(path, cancellationToken);
        return ImportJournal(clientId, financialYear, workbook, path, runId, jobId, cancellationToken);
    }

    public UniversalModuleImportResult ImportJournal(
        long clientId,
        string financialYear,
        OpenXmlWorkbookData workbook,
        string sourcePath,
        string runId,
        string jobId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        using var headerMappingScope = ImportHeaderMappingRuntime.BeginSource(sourcePath);
        var rawLines = new List<JournalImportLine>();
        var skipped = 0;

        foreach (var sheet in workbook.Worksheets.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = FindHeader(sheet, JournalHeaderScore);
            if (header is null) continue;
            var map = BuildHeaderMap(header.Value.Cells, ImportHeaderMappingCatalog.Journal);
            var dateColumn = FindColumn(map, "VOUCHER DATE", "DATE", "JOURNAL DATE");
            var voucherColumn = FindColumn(map, "VOUCHER NUMBER", "VOUCHER NO", "JOURNAL NO", "DOCUMENT NO", "REFERENCE");
            var ledgerColumn = FindColumn(map, "LEDGER NAME", "LEDGER", "PARTICULARS");
            var debitColumn = FindColumn(map, "DEBIT", "DEBIT AMOUNT", "DR AMOUNT");
            var creditColumn = FindColumn(map, "CREDIT", "CREDIT AMOUNT", "CR AMOUNT");
            var amountColumn = FindColumn(map, "AMOUNT", "VALUE");
            var drCrColumn = FindColumn(map, "DR/CR", "DR CR", "TYPE");
            var narrationColumn = FindColumn(map, "NARRATION", "DESCRIPTION", "REMARKS");
            var referenceColumn = FindColumn(map, "REFERENCE NUMBER", "REFERENCE NO", "REF NO");
            if (dateColumn <= 0 || ledgerColumn <= 0 ||
                (debitColumn <= 0 && creditColumn <= 0 && amountColumn <= 0)) continue;

            foreach (var pair in sheet.Rows.Where(pair => pair.Key > header.Value.RowNumber).OrderBy(pair => pair.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cells = pair.Value;
                if (!TryDate(Cell(cells, dateColumn), out var date)) { skipped++; continue; }
                var ledger = Cell(cells, ledgerColumn);
                if (ledger.Length == 0) { skipped++; continue; }
                decimal debit = 0m, credit = 0m;
                if (debitColumn > 0) TryAmount(Cell(cells, debitColumn), out debit);
                if (creditColumn > 0) TryAmount(Cell(cells, creditColumn), out credit);
                string drCr;
                decimal amount;
                if (debit > 0m) { drCr = "Dr"; amount = debit; }
                else if (credit > 0m) { drCr = "Cr"; amount = credit; }
                else if (amountColumn > 0 && TryAmount(Cell(cells, amountColumn), out amount) && Math.Abs(amount) > 0m)
                {
                    var direction = drCrColumn > 0 ? Cell(cells, drCrColumn) : string.Empty;
                    drCr = direction.StartsWith("C", StringComparison.OrdinalIgnoreCase) ? "Cr" : "Dr";
                    amount = Math.Abs(amount);
                }
                else { skipped++; continue; }

                rawLines.Add(new JournalImportLine(
                    date.Date,
                    voucherColumn > 0 ? Cell(cells, voucherColumn) : string.Empty,
                    ledger,
                    Math.Abs(amount),
                    drCr,
                    narrationColumn > 0 ? Cell(cells, narrationColumn) : string.Empty,
                    referenceColumn > 0 ? Cell(cells, referenceColumn) : string.Empty,
                    sheet.Name,
                    pair.Key));
            }
        }

        if (rawLines.Count == 0)
            return new UniversalModuleImportResult(false, 0, 0, skipped, "No Journal lines matching the supported Journal columns were found.");

        var masterLedgers = _masterRepository.Get(clientId, financialYear, false, string.Empty)
            .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !item.IsArchived)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var imported = 0;
        var review = 0;
        var grouped = rawLines.GroupBy(line =>
        {
            var number = line.VoucherNumber.Trim();
            return number.Length > 0
                ? $"N|{line.Date:yyyyMMdd}|{number.ToUpperInvariant()}"
                : $"R|{line.Date:yyyyMMdd}|{line.Sheet.ToUpperInvariant()}|{line.RowNumber}";
        }, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = group.ToArray();
            var debitTotal = rows.Where(row => row.DrCr == "Dr").Sum(row => row.Amount);
            var creditTotal = rows.Where(row => row.DrCr == "Cr").Sum(row => row.Amount);
            var missingLedgers = rows.Select(row => row.Ledger).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(ledger => !masterLedgers.Contains(ledger)).ToArray();
            var balanced = Math.Abs(debitTotal - creditTotal) < 0.005m;
            var needsReview = !balanced || missingLedgers.Length > 0 || rows.Length < 2;
            var first = rows[0];
            var voucherNo = first.VoucherNumber.Trim();
            if (voucherNo.Length == 0) voucherNo = $"UJ-{first.Date:yyyyMMdd}-{Guid.NewGuid():N}"[..30];
            var voucher = new JournalVoucher
            {
                ClientId = clientId,
                FinancialYear = financialYear,
                VoucherDate = first.Date,
                PeriodKey = first.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                PeriodLabel = first.Date.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                VoucherNumber = voucherNo,
                VoucherType = "Journal",
                Narration = rows.Select(row => row.Narration).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
                ReferenceNumber = rows.Select(row => row.Reference).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
                GenerationMode = "Manual",
                WorkflowStatus = needsReview ? "Draft" : "Ready",
                SourceRunId = runId,
                SourceJobId = jobId,
                CreatedBy = Environment.UserName,
                IsUserEdited = false,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                Lines = rows.Select((row, index) => new JournalLine
                {
                    LineNumber = index + 1,
                    LedgerName = row.Ledger,
                    Amount = row.Amount,
                    DrCr = row.DrCr,
                    LineRole = "Universal Dump"
                }).ToList()
            };
            _journalRepository.SaveVoucher(voucher);
            imported++;
            if (needsReview) review++;
        }

        return new UniversalModuleImportResult(true, imported, review, skipped,
            review > 0
                ? $"Imported {imported:N0} Journal voucher(s); {review:N0} require balance/Master review."
                : $"Imported {imported:N0} balanced Journal voucher(s) and all are Ready.");
    }

    private static (int RowNumber, IReadOnlyDictionary<int, string> Cells)? FindHeader(
        OpenXmlWorksheetData sheet,
        Func<IReadOnlyDictionary<int, string>, int> scorer)
    {
        return sheet.Rows.OrderBy(pair => pair.Key).Take(80)
            .Select(pair => (RowNumber: pair.Key, Cells: pair.Value, Score: scorer(pair.Value)))
            .Where(item => item.Score >= 4)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.RowNumber)
            .Select(item => ((int RowNumber, IReadOnlyDictionary<int, string> Cells)?)(item.RowNumber, item.Cells))
            .FirstOrDefault();
    }

    private static int CashHeaderScore(IReadOnlyDictionary<int, string> cells)
    {
        var values = cells.Values.Select(value => NormalizeUniversalHeader(ImportHeaderMappingCatalog.Cash, value)).ToArray();
        var score = 0;
        if (values.Any(value => value is "DATE" or "ENTRY DATE" or "VOUCHER DATE")) score += 2;
        if (values.Any(value => value.Contains("LEDGER", StringComparison.OrdinalIgnoreCase) || value == "PARTICULARS")) score += 2;
        if (values.Any(value => value.Contains("DEBIT", StringComparison.OrdinalIgnoreCase) || value.Contains("CREDIT", StringComparison.OrdinalIgnoreCase) || value == "AMOUNT")) score += 2;
        if (values.Any(value => value.Contains("CASH", StringComparison.OrdinalIgnoreCase) || value.Contains("COMMON LEDGER", StringComparison.OrdinalIgnoreCase))) score += 2;
        return score;
    }

    private static int JournalHeaderScore(IReadOnlyDictionary<int, string> cells)
    {
        var values = cells.Values.Select(value => NormalizeUniversalHeader(ImportHeaderMappingCatalog.Journal, value)).ToArray();
        var score = 0;
        if (values.Any(value => value is "DATE" or "VOUCHER DATE" or "JOURNAL DATE")) score += 2;
        if (values.Any(value => value.Contains("VOUCHER", StringComparison.OrdinalIgnoreCase) || value.Contains("JOURNAL", StringComparison.OrdinalIgnoreCase))) score += 2;
        if (values.Any(value => value.Contains("LEDGER", StringComparison.OrdinalIgnoreCase) || value == "PARTICULARS")) score += 2;
        if (values.Any(value => value.Contains("DEBIT", StringComparison.OrdinalIgnoreCase) || value.Contains("CREDIT", StringComparison.OrdinalIgnoreCase) || value.Contains("DR/CR", StringComparison.OrdinalIgnoreCase))) score += 2;
        return score;
    }

    private static Dictionary<string, int> BuildHeaderMap(IReadOnlyDictionary<int, string> cells, string module) =>
        cells.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => (pair.Key, Header: NormalizeUniversalHeader(module, pair.Value)))
            .Where(pair => pair.Header.Length > 0)
            .GroupBy(pair => pair.Header, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeUniversalHeader(string module, string value)
    {
        var canonical = ImportHeaderMappingRuntime.Resolve(module, value);
        var effective = canonical.Length > 0 ? UniversalParserHeader(module, canonical) : value;
        return Normalize(effective);
    }

    private static string UniversalParserHeader(string module, string canonical)
    {
        if (module.Equals(ImportHeaderMappingCatalog.Cash, StringComparison.OrdinalIgnoreCase))
        {
            return canonical switch
            {
                "Date" => "DATE", "VoucherNo" => "REFERENCE NUMBER", "EntryLedger" => "ENTRY LEDGER",
                "CommonLedger" => "COMMON LEDGER", "Amount" => "AMOUNT", "Debit" => "DEBIT",
                "Credit" => "CREDIT", "DrCr" => "DR/CR", "Narration" => "NARRATION", "Reference" => "REFERENCE NUMBER",
                _ => canonical
            };
        }
        return canonical switch
        {
            "VoucherNo" => "VOUCHER NUMBER", "Date" => "VOUCHER DATE", "Ledger" => "LEDGER NAME",
            "Amount" => "AMOUNT", "Debit" => "DEBIT", "Credit" => "CREDIT", "DrCr" => "DR/CR",
            "Narration" => "NARRATION", "TaxHead" => "TAX HEAD", "Role" => "ROLE", "VoucherType" => "VOUCHER TYPE",
            _ => canonical
        };
    }

    private static int FindColumn(IReadOnlyDictionary<string, int> map, params string[] names)
    {
        foreach (var name in names)
            if (map.TryGetValue(Normalize(name), out var column)) return column;
        foreach (var pair in map)
            if (names.Any(name => pair.Key.Contains(Normalize(name), StringComparison.OrdinalIgnoreCase))) return pair.Value;
        return 0;
    }

    private static string Cell(IReadOnlyDictionary<int, string> cells, int column) =>
        column > 0 && cells.TryGetValue(column, out var value) ? value.Trim() : string.Empty;

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool TryDate(string value, out DateTime date)
    {
        var formats = new[] { "dd.MM.yyyy", "dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd", "d/M/yyyy", "d-M-yyyy", "dd-MMM-yyyy", "d-MMM-yyyy" };
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)) return true;
        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date)) return true;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 1 and < 100000)
        {
            try { date = DateTime.FromOADate(serial); return true; } catch { }
        }
        date = default;
        return false;
    }

    private static bool TryAmount(string value, out decimal amount)
    {
        var clean = value.Trim().Replace("₹", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal);
        var negative = clean.StartsWith('(') && clean.EndsWith(')');
        clean = clean.Trim('(', ')').Trim();
        if (clean.EndsWith("DR", StringComparison.OrdinalIgnoreCase) || clean.EndsWith("CR", StringComparison.OrdinalIgnoreCase)) clean = clean[..^2].Trim();
        if (decimal.TryParse(clean, NumberStyles.Number | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
        {
            if (negative) amount = -amount;
            amount = Math.Abs(amount);
            return true;
        }
        amount = 0m;
        return false;
    }

    private sealed record JournalImportLine(DateTime Date, string VoucherNumber, string Ledger, decimal Amount, string DrCr, string Narration, string Reference, string Sheet, int RowNumber);
}

public sealed record UniversalModuleImportResult(bool WasSuccessful, int ImportedCount, int ReviewCount, int SkippedCount, string Detail);
