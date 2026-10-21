using System.Globalization;
using System.Text;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Infrastructure.Spreadsheets;
using AccountingAutomationHub.Infrastructure.Imports;

namespace AccountingAutomationHub.Infrastructure.Journals;

public sealed class ManualJournalBulkImportService
{
    private const decimal BalanceTolerance = 0.005m;

    public string PasteTemplate =>
        "Voucher No\tDate\tLedger\tAmount\tDr/Cr\tNarration\tTax Head\tRole\tVoucher Type\r\n" +
        "JV-001\t31.03.2026\tOutput CGST\t1000.00\tDr\tGST adjustment\tCGST\tOutput Tax\tJournal\r\n" +
        "\t\tGST Payable / Receivable\t1000.00\tCr\t\tCGST\tSettlement Payable\t";

    public string LinePasteTemplate =>
        "Ledger\tDebit\tCredit\tTax Head\tRole\r\n" +
        "Output CGST\t1000.00\t\tCGST\tOutput Tax\r\n" +
        "GST Payable / Receivable\t\t1000.00\tCGST\tSettlement Payable";

    public ManualJournalBulkImportResult ParseFile(
        string path,
        long clientId,
        string financialYear,
        string filingFrequency)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The selected manual-journal file was not found.", path);
        }

        using var headerMappingScope = ImportHeaderMappingRuntime.BeginSource(path);
        return ParseVoucherRows(
            ReadRows(path),
            clientId,
            financialYear,
            filingFrequency,
            Path.GetFileName(path));
    }

    public ManualJournalBulkImportResult ParseClipboard(
        string text,
        long clientId,
        string financialYear,
        string filingFrequency)
    {
        return ParseVoucherRows(
            ParseClipboardRows(text),
            clientId,
            financialYear,
            filingFrequency,
            "Excel clipboard");
    }

    public ManualJournalLinePasteResult ParseClipboardLines(string text)
    {
        var rows = ParseClipboardRows(text);
        if (rows.Count == 0)
        {
            throw new InvalidDataException("The clipboard does not contain journal lines.");
        }

        var warnings = new List<string>();
        var headerIndex = FindLineHeaderRow(rows);
        var startIndex = headerIndex >= 0 ? headerIndex + 1 : 0;
        var map = headerIndex >= 0
            ? BuildLineHeaderMap(rows[headerIndex])
            : DetectLinePositionalMap(rows.Skip(startIndex).ToArray());
        var result = new List<JournalLine>();

        for (var rowIndex = startIndex; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (IsBlankRow(row) || IsRepeatedHeader(row))
            {
                continue;
            }

            var ledger = Value(row, map, "LEDGER");
            if (string.IsNullOrWhiteSpace(ledger) || IsTotalRow(ledger))
            {
                continue;
            }

            if (!TryResolveAmountAndDirection(row, map, out var amount, out var drCr, out var issue))
            {
                if (!string.IsNullOrWhiteSpace(issue))
                {
                    warnings.Add($"Row {rowIndex + 1}: {issue}");
                }
                continue;
            }

            result.Add(new JournalLine
            {
                LineNumber = result.Count + 1,
                LedgerName = ledger.Trim(),
                Amount = Math.Abs(amount),
                DrCr = drCr,
                TaxHead = NormalizeTaxHead(Value(row, map, "TAXHEAD"), ledger),
                LineRole = NormalizeRole(Value(row, map, "ROLE"), ledger, drCr)
            });
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException(
                "No valid journal lines were found. Copy Ledger with Debit/Credit columns, or Ledger with Amount and Dr/Cr.");
        }

        return new ManualJournalLinePasteResult(result, warnings);
    }

    public bool ClipboardContainsVoucherMetadata(string text)
    {
        var rows = ParseClipboardRows(text);
        var headerIndex = FindHeaderRow(rows);
        if (headerIndex >= 0)
        {
            var header = rows[headerIndex].Select(NormalizeHeader).ToArray();
            return header.Any(value => value is "VOUCHERNO" or "VOUCHERNUMBER" or "VCHNO" or "VOUCHERDATE" or "DATE");
        }

        return rows.Take(8).Any(row =>
            row.Length >= 4 &&
            (LooksLikeDateCell(Cell(row, 1)) ||
             (LooksLikeVoucherNumber(Cell(row, 1)) && LooksLikeDateCell(Cell(row, 2)))));
    }

    private static ManualJournalBulkImportResult ParseVoucherRows(
        IReadOnlyList<string[]> rows,
        long clientId,
        string financialYear,
        string filingFrequency,
        string sourceName)
    {
        if (rows.Count == 0)
        {
            throw new InvalidDataException("The source does not contain journal rows.");
        }

        var headerIndex = FindHeaderRow(rows);
        var map = headerIndex >= 0
            ? BuildHeaderMap(rows[headerIndex])
            : DetectVoucherPositionalMap(rows);
        var startIndex = headerIndex >= 0 ? headerIndex + 1 : 0;

        var vouchers = new Dictionary<string, JournalVoucher>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var autoSequence = 0;
        string currentAutoNumber = string.Empty;
        string lastVoucherNumber = string.Empty;
        DateTime? lastDate = null;
        string lastNarration = string.Empty;
        string lastVoucherType = "Journal";

        for (var rowIndex = startIndex; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (IsBlankRow(row))
            {
                currentAutoNumber = string.Empty;
                lastVoucherNumber = string.Empty;
                lastDate = null;
                lastNarration = string.Empty;
                lastVoucherType = "Journal";
                continue;
            }
            if (IsRepeatedHeader(row))
            {
                continue;
            }

            var voucherNumber = Value(row, map, "VOUCHERNO");
            var dateText = Value(row, map, "DATE");
            var ledger = Value(row, map, "LEDGER");
            var narration = Value(row, map, "NARRATION");
            var taxHead = Value(row, map, "TAXHEAD");
            var role = Value(row, map, "ROLE");
            var voucherType = Value(row, map, "VOUCHERTYPE");

            var hasExplicitVoucherNumber = !string.IsNullOrWhiteSpace(voucherNumber);
            var hasExplicitDate = TryParseDate(dateText, out var parsedDate);
            var startsNewVoucher =
                (hasExplicitVoucherNumber &&
                 !voucherNumber.Trim().Equals(lastVoucherNumber, StringComparison.OrdinalIgnoreCase)) ||
                (hasExplicitDate &&
                 lastDate.HasValue &&
                 parsedDate.Date != lastDate.Value.Date) ||
                (!hasExplicitVoucherNumber &&
                 !string.IsNullOrWhiteSpace(narration) &&
                 !string.IsNullOrWhiteSpace(lastNarration) &&
                 !narration.Trim().Equals(lastNarration, StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(currentAutoNumber));

            if (startsNewVoucher)
            {
                currentAutoNumber = string.Empty;
                if (!hasExplicitVoucherNumber)
                {
                    lastVoucherNumber = string.Empty;
                }
                if (string.IsNullOrWhiteSpace(narration))
                {
                    lastNarration = string.Empty;
                }
                if (string.IsNullOrWhiteSpace(voucherType))
                {
                    lastVoucherType = "Journal";
                }
            }

            if (hasExplicitVoucherNumber)
            {
                lastVoucherNumber = voucherNumber.Trim();
                currentAutoNumber = string.Empty;
            }

            if (hasExplicitDate)
            {
                lastDate = parsedDate.Date;
            }

            if (!string.IsNullOrWhiteSpace(narration))
            {
                lastNarration = narration.Trim();
            }
            if (!string.IsNullOrWhiteSpace(voucherType))
            {
                lastVoucherType = voucherType.Trim();
            }

            if (string.IsNullOrWhiteSpace(ledger) || IsTotalRow(ledger))
            {
                continue;
            }

            if (!lastDate.HasValue)
            {
                warnings.Add($"Row {rowIndex + 1}: skipped because no voucher date was available.");
                continue;
            }

            if (!TryResolveAmountAndDirection(row, map, out var amount, out var drCr, out var issue))
            {
                warnings.Add($"Row {rowIndex + 1}: {issue}");
                continue;
            }

            var effectiveNumber = lastVoucherNumber;
            if (string.IsNullOrWhiteSpace(effectiveNumber))
            {
                if (string.IsNullOrWhiteSpace(currentAutoNumber))
                {
                    autoSequence++;
                    currentAutoNumber = $"MANUAL-{lastDate:yyyyMMdd}-{autoSequence:000}";
                }
                effectiveNumber = currentAutoNumber;
            }

            var groupKey = $"{effectiveNumber}|{lastDate:yyyyMMdd}";
            if (!vouchers.TryGetValue(groupKey, out var voucher))
            {
                var periodKey = GstJournalGenerationService.PeriodKey(lastDate.Value, filingFrequency);
                voucher = new JournalVoucher
                {
                    ClientId = clientId,
                    FinancialYear = financialYear,
                    PeriodKey = periodKey,
                    PeriodLabel = GstJournalGenerationService.PeriodLabel(periodKey, filingFrequency),
                    VoucherDate = lastDate.Value,
                    VoucherNumber = effectiveNumber,
                    VoucherType = string.IsNullOrWhiteSpace(lastVoucherType) ? "Journal" : lastVoucherType,
                    Narration = string.IsNullOrWhiteSpace(lastNarration)
                        ? $"Manual journal imported from {sourceName}"
                        : lastNarration,
                    GenerationMode = "Manual",
                    IsUserEdited = true
                };
                vouchers[groupKey] = voucher;
            }

            voucher.Lines.Add(new JournalLine
            {
                LineNumber = voucher.Lines.Count + 1,
                LedgerName = ledger.Trim(),
                Amount = Math.Abs(amount),
                DrCr = drCr,
                TaxHead = NormalizeTaxHead(taxHead, ledger),
                LineRole = NormalizeRole(role, ledger, drCr)
            });
        }

        var valid = new List<JournalVoucher>();
        foreach (var voucher in vouchers.Values
                     .Where(voucher => voucher.Lines.Count > 0)
                     .OrderBy(voucher => voucher.VoucherDate)
                     .ThenBy(voucher => voucher.VoucherNumber, StringComparer.OrdinalIgnoreCase))
        {
            var debit = voucher.Lines
                .Where(line => line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase))
                .Sum(line => Math.Abs(line.Amount));
            var credit = voucher.Lines
                .Where(line => line.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase))
                .Sum(line => Math.Abs(line.Amount));
            var difference = debit - credit;

            if (Math.Abs(difference) > BalanceTolerance)
            {
                warnings.Add(
                    $"Voucher {voucher.VoucherNumber} dated {voucher.VoucherDate:dd.MM.yyyy} was not imported because Debit {debit:N2} and Credit {credit:N2} differ by {difference:N2}.");
                continue;
            }

            valid.Add(voucher);
        }

        if (valid.Count == 0)
        {
            var detail = warnings.Count > 0 ? " " + warnings[0] : string.Empty;
            throw new InvalidDataException(
                "No balanced manual-journal vouchers were found. Expected Voucher No, Date, Ledger and either Debit/Credit or Amount with Dr/Cr." + detail);
        }

        return new ManualJournalBulkImportResult(valid, warnings);
    }

    private static IReadOnlyList<string[]> ReadRows(string path)
    {
        if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return File.ReadLines(path)
                .Select(ParseCsvLine)
                .ToArray();
        }

        var workbook = new OpenXmlWorkbookReader().Read(path, CancellationToken.None);
        var worksheet = workbook.Worksheets.Values
            .Select(sheet => new
            {
                Sheet = sheet,
                Score = ScoreJournalWorksheet(sheet)
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Sheet.Rows.Count)
            .Select(item => item.Sheet)
            .FirstOrDefault()
            ?? throw new InvalidDataException("The workbook does not contain a readable worksheet.");
        var maxColumn = worksheet.Rows.Values
            .SelectMany(row => row.Keys)
            .DefaultIfEmpty(1)
            .Max();

        return Enumerable.Range(1, worksheet.MaxRow)
            .Select(rowNumber => Enumerable.Range(1, maxColumn)
                .Select(column => worksheet.GetCell(rowNumber, column))
                .ToArray())
            .ToArray();
    }


    private static int ScoreJournalWorksheet(OpenXmlWorksheetData worksheet)
    {
        var score = worksheet.Name.Contains("JOURNAL", StringComparison.OrdinalIgnoreCase) ? 50 : 0;
        foreach (var row in worksheet.Rows
                     .OrderBy(pair => pair.Key)
                     .Take(25)
                     .Select(pair => pair.Value))
        {
            var headers = row.Values.Select(NormalizeHeader).ToArray();
            if (headers.Any(value => value is "VOUCHER" or "VOUCHERNO" or "VOUCHERNUMBER" or "VCHNO")) score += 12;
            if (headers.Any(value => value is "DATE" or "VOUCHERDATE")) score += 12;
            if (headers.Any(value => value is "LEDGER" or "LEDGERNAME" or "PARTICULARS" or "ACCOUNT")) score += 18;
            if (headers.Any(value => value is "AMOUNT" or "DEBIT" or "CREDIT" or "DRCR")) score += 18;
            if (headers.Any(value => value is "NARRATION" or "DESCRIPTION" or "REMARKS")) score += 8;
        }
        return score;
    }

    private static IReadOnlyList<string[]> ParseClipboardRows(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("The clipboard does not contain journal rows.");
        }

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var rows = lines
            .Select(line => line.Contains('\t') ? line.Split('\t') : ParseCsvLine(line))
            .ToList();

        while (rows.Count > 0 && IsBlankRow(rows[^1]))
        {
            rows.RemoveAt(rows.Count - 1);
        }
        return rows;
    }

    private static int FindHeaderRow(IReadOnlyList<string[]> rows)
    {
        for (var index = 0; index < Math.Min(rows.Count, 25); index++)
        {
            var headers = rows[index].Select(NormalizeHeader).ToArray();
            var hasDate = headers.Any(value => value is "DATE" or "VOUCHERDATE");
            var hasLedger = headers.Any(value => value is "LEDGER" or "LEDGERNAME" or "PARTICULARS" or "ACCOUNT");
            var hasAmount = headers.Any(value =>
                value.Contains("AMOUNT", StringComparison.Ordinal) ||
                value is "DEBIT" or "CREDIT" or "DR" or "CR");
            if (hasDate && hasLedger && hasAmount)
            {
                return index;
            }
        }
        return -1;
    }

    private static int FindLineHeaderRow(IReadOnlyList<string[]> rows)
    {
        for (var index = 0; index < Math.Min(rows.Count, 15); index++)
        {
            var headers = rows[index].Select(NormalizeHeader).ToArray();
            var hasLedger = headers.Any(value => value is "LEDGER" or "LEDGERNAME" or "PARTICULARS" or "ACCOUNT");
            var hasAmount = headers.Any(value =>
                value.Contains("AMOUNT", StringComparison.Ordinal) ||
                value is "DEBIT" or "CREDIT" or "DR" or "CR");
            if (hasLedger && hasAmount)
            {
                return index;
            }
        }
        return -1;
    }

    private static Dictionary<string, int> BuildHeaderMap(IReadOnlyList<string> header)
    {
        var raw = BuildRawHeaderMap(header);
        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["VOUCHERNO"] = Find(raw, "VOUCHERNO", "VOUCHERNUMBER", "VCHNO", "VOUCHER"),
            ["DATE"] = Find(raw, "DATE", "VOUCHERDATE"),
            ["LEDGER"] = Find(raw, "LEDGER", "LEDGERNAME", "PARTICULARS", "ACCOUNT"),
            ["AMOUNT"] = Find(raw, "AMOUNT", "AMOUNTINR", "VALUE"),
            ["DEBIT"] = Find(raw, "DEBIT", "DEBITAMOUNT", "DR", "DRAMOUNT"),
            ["CREDIT"] = Find(raw, "CREDIT", "CREDITAMOUNT", "CR", "CRAMOUNT"),
            ["DRCR"] = Find(raw, "DRCR", "DEBITCREDIT", "DEBITORCREDIT", "TYPE"),
            ["NARRATION"] = Find(raw, "NARRATION", "DESCRIPTION", "REMARKS"),
            ["TAXHEAD"] = Find(raw, "TAXHEAD", "GSTHEAD", "TAXTYPE"),
            ["ROLE"] = Find(raw, "ROLE", "LINEROLE", "JOURNALROLE"),
            ["VOUCHERTYPE"] = Find(raw, "VOUCHERTYPE", "VCHTYPE")
        };
    }

    private static Dictionary<string, int> BuildLineHeaderMap(IReadOnlyList<string> header)
    {
        var raw = BuildRawHeaderMap(header);
        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["LEDGER"] = Find(raw, "LEDGER", "LEDGERNAME", "PARTICULARS", "ACCOUNT"),
            ["AMOUNT"] = Find(raw, "AMOUNT", "AMOUNTINR", "VALUE"),
            ["DEBIT"] = Find(raw, "DEBIT", "DEBITAMOUNT", "DR", "DRAMOUNT"),
            ["CREDIT"] = Find(raw, "CREDIT", "CREDITAMOUNT", "CR", "CRAMOUNT"),
            ["DRCR"] = Find(raw, "DRCR", "DEBITCREDIT", "DEBITORCREDIT", "TYPE"),
            ["TAXHEAD"] = Find(raw, "TAXHEAD", "GSTHEAD", "TAXTYPE"),
            ["ROLE"] = Find(raw, "ROLE", "LINEROLE", "JOURNALROLE")
        };
    }

    private static Dictionary<string, int> BuildRawHeaderMap(IReadOnlyList<string> header) =>
        header
            .Select((value, index) => (Header: NormalizeHeader(value), Index: index))
            .Where(item => item.Header.Length > 0)
            .GroupBy(item => item.Header, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, int> DetectVoucherPositionalMap(IReadOnlyList<string[]> rows)
    {
        var sample = rows.FirstOrDefault(row => !IsBlankRow(row)) ?? [];

        // Legacy Journal sheet layout:
        // Voucher No | Date | Ledger | Amount | Dr/Cr | Narration
        if (sample.Length >= 5 && LooksLikeDateCell(Cell(sample, 2)))
        {
            return IsDirectionText(Cell(sample, 5))
                ? PositionalMap(0, 1, 2, 3, -1, -1, 4, 5, 6, 7, 8)
                : PositionalMap(0, 1, 2, -1, 3, 4, -1, 5, 6, 7, 8);
        }

        // Date | Ledger | Amount | Dr/Cr | Narration
        // or Date | Ledger | Debit | Credit | Narration
        if (sample.Length >= 4 && LooksLikeDateCell(Cell(sample, 1)))
        {
            return IsDirectionText(Cell(sample, 4))
                ? PositionalMap(-1, 0, 1, 2, -1, -1, 3, 4, 5, 6, 7)
                : PositionalMap(-1, 0, 1, -1, 2, 3, -1, 4, 5, 6, 7);
        }

        return PositionalMap(0, 1, 2, 3, -1, -1, 4, 5, 6, 7, 8);
    }

    private static Dictionary<string, int> DetectLinePositionalMap(IReadOnlyList<string[]> rows)
    {
        var sample = rows.FirstOrDefault(row => !IsBlankRow(row)) ?? [];
        var offset = sample.Length > 2 && int.TryParse(Cell(sample, 1), out _) && !decimal.TryParse(Cell(sample, 2), out _)
            ? 1
            : 0;
        var remaining = sample.Length - offset;

        if (remaining >= 3 && IsDirectionText(Cell(sample, offset + 3)))
        {
            return LineMap(offset, offset + 1, -1, -1, offset + 2, offset + 3, offset + 4);
        }
        if (remaining >= 3)
        {
            return LineMap(offset, -1, offset + 1, offset + 2, -1, offset + 3, offset + 4);
        }
        return LineMap(offset, offset + 1, -1, -1, -1, offset + 2, offset + 3);
    }

    private static Dictionary<string, int> PositionalMap(
        int voucherNo, int date, int ledger, int amount, int debit, int credit,
        int drCr, int narration, int taxHead, int role, int voucherType) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["VOUCHERNO"] = voucherNo,
            ["DATE"] = date,
            ["LEDGER"] = ledger,
            ["AMOUNT"] = amount,
            ["DEBIT"] = debit,
            ["CREDIT"] = credit,
            ["DRCR"] = drCr,
            ["NARRATION"] = narration,
            ["TAXHEAD"] = taxHead,
            ["ROLE"] = role,
            ["VOUCHERTYPE"] = voucherType
        };

    private static Dictionary<string, int> LineMap(
        int ledger, int amount, int debit, int credit, int drCr, int taxHead, int role) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["LEDGER"] = ledger,
            ["AMOUNT"] = amount,
            ["DEBIT"] = debit,
            ["CREDIT"] = credit,
            ["DRCR"] = drCr,
            ["TAXHEAD"] = taxHead,
            ["ROLE"] = role
        };

    private static bool TryResolveAmountAndDirection(
        IReadOnlyList<string> row,
        IReadOnlyDictionary<string, int> map,
        out decimal amount,
        out string drCr,
        out string issue)
    {
        amount = 0m;
        drCr = "Dr";
        issue = string.Empty;

        var amountText = Value(row, map, "AMOUNT");
        var debitText = Value(row, map, "DEBIT");
        var creditText = Value(row, map, "CREDIT");
        var directionText = Value(row, map, "DRCR");
        var hasDebit = TryParseAmount(debitText, out var debit) && debit != 0m;
        var hasCredit = TryParseAmount(creditText, out var credit) && credit != 0m;

        if (hasDebit && hasCredit)
        {
            issue = "both Debit and Credit contain amounts; only one side is allowed.";
            return false;
        }
        if (hasDebit)
        {
            amount = Math.Abs(debit);
            drCr = "Dr";
            return true;
        }
        if (hasCredit)
        {
            amount = Math.Abs(credit);
            drCr = "Cr";
            return true;
        }
        if (TryParseAmount(amountText, out amount) && amount != 0m)
        {
            if (string.IsNullOrWhiteSpace(directionText) && amount > 0m)
            {
                issue = "Amount was found but Dr/Cr is blank. Use a Dr/Cr column or separate Debit/Credit columns.";
                return false;
            }
            drCr = NormalizeDrCr(directionText, amount);
            amount = Math.Abs(amount);
            return true;
        }

        issue = "no non-zero Amount, Debit or Credit was found.";
        return false;
    }

    private static int Find(IReadOnlyDictionary<string, int> map, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out var index))
            {
                return index;
            }
        }
        return -1;
    }

    private static string Value(
        IReadOnlyList<string> row,
        IReadOnlyDictionary<string, int> map,
        string key) =>
        map.TryGetValue(key, out var index) && index >= 0 && index < row.Count
            ? row[index].Trim()
            : string.Empty;

    private static string Cell(IReadOnlyList<string> row, int oneBasedColumn)
    {
        var index = oneBasedColumn - 1;
        return index >= 0 && index < row.Count ? row[index].Trim() : string.Empty;
    }

    private static bool LooksLikeVoucherNumber(string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || text.Length > 50)
        {
            return false;
        }

        // A voucher number may be numeric, but a ledger name containing spaces
        // should not make a Debit amount in the next cell look like an Excel date.
        return !text.Contains(' ') || text.Any(char.IsDigit);
    }

    private static bool LooksLikeDateCell(string value)
    {
        var text = value.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            // Modern accounting workbooks normally use Excel serials above 20,000.
            // This avoids mistaking ordinary Debit/Credit amounts for dates while
            // still supporting serial-date paste from Excel.
            return serial is >= 20000d and <= 80000d;
        }
        return TryParseDate(text, out _);
    }

    private static bool TryParseDate(string value, out DateTime date)
    {
        if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var serialDate) &&
            serialDate is >= 1d and <= 2958465d)
        {
            try
            {
                date = DateTime.FromOADate(serialDate);
                return true;
            }
            catch
            {
                // Continue with text parsing.
            }
        }

        string[] formats =
        [
            "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "yyyy-MM-dd",
            "dd/MM/yy", "dd-MM-yy", "dd.MM.yy", "d/M/yyyy", "d-M-yyyy", "d.M.yyyy"
        ];
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out date))
        {
            return true;
        }
        return DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static bool TryParseAmount(string value, out decimal amount)
    {
        amount = 0m;
        var text = value.Trim();
        if (text.Length == 0 || text is "-" or "—")
        {
            return false;
        }

        var parenthesized = text.StartsWith('(') && text.EndsWith(')');
        text = text
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("₹", string.Empty, StringComparison.Ordinal)
            .Replace("INR", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim('(', ')', ' ');

        var parsed = decimal.TryParse(
            text,
            NumberStyles.Float | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out amount);
        if (parenthesized)
        {
            amount *= -1m;
        }
        return parsed;
    }

    private static string NormalizeDrCr(string value, decimal amount)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.StartsWith("C", StringComparison.Ordinal) || normalized.Contains("CREDIT", StringComparison.Ordinal))
        {
            return "Cr";
        }
        if (normalized.StartsWith("D", StringComparison.Ordinal) || normalized.Contains("DEBIT", StringComparison.Ordinal))
        {
            return "Dr";
        }
        return amount < 0m ? "Cr" : "Dr";
    }

    private static string NormalizeTaxHead(string value, string ledger)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Contains("UTGST", StringComparison.Ordinal)) return "UTGST";
        if (normalized.Contains("IGST", StringComparison.Ordinal)) return "IGST";
        if (normalized.Contains("CGST", StringComparison.Ordinal)) return "CGST";
        if (normalized.Contains("SGST", StringComparison.Ordinal)) return "SGST";

        var ledgerText = ledger.ToUpperInvariant();
        if (ledgerText.Contains("UTGST", StringComparison.Ordinal)) return "UTGST";
        if (ledgerText.Contains("IGST", StringComparison.Ordinal)) return "IGST";
        if (ledgerText.Contains("CGST", StringComparison.Ordinal)) return "CGST";
        if (ledgerText.Contains("SGST", StringComparison.Ordinal)) return "SGST";
        return string.Empty;
    }

    private static string NormalizeRole(string value, string ledger, string drCr)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        var text = ledger.ToUpperInvariant();
        if ((text.Contains("RCM", StringComparison.Ordinal) || text.Contains("REVERSE CHARGE", StringComparison.Ordinal)) &&
            (text.Contains("TEMP", StringComparison.Ordinal) || text.Contains("TEMPORARY", StringComparison.Ordinal)))
        {
            return "RCM Input Transfer";
        }
        if ((text.Contains("RCM", StringComparison.Ordinal) || text.Contains("REVERSE CHARGE", StringComparison.Ordinal)) &&
            text.Contains("EXPENSE", StringComparison.Ordinal))
        {
            return "RCM ITC Reversal";
        }
        if ((text.Contains("RCM", StringComparison.Ordinal) || text.Contains("REVERSE CHARGE", StringComparison.Ordinal)) &&
            (text.Contains("INPUT", StringComparison.Ordinal) || text.Contains("ITC", StringComparison.Ordinal)))
        {
            return drCr.Equals("Dr", StringComparison.OrdinalIgnoreCase)
                ? "RCM ITC Claim"
                : "RCM Input Transfer";
        }
        if (text.Contains("INPUT", StringComparison.Ordinal) || text.Contains("ITC", StringComparison.Ordinal))
        {
            return "Normal Input Tax";
        }
        if (text.Contains("OUTPUT", StringComparison.Ordinal))
        {
            return "Output Tax";
        }
        if (text.Contains("PAYABLE", StringComparison.Ordinal))
        {
            return "Settlement Payable";
        }
        if (text.Contains("RECEIVABLE", StringComparison.Ordinal))
        {
            return "Settlement Receivable";
        }
        if (text.Contains("IGST", StringComparison.Ordinal) ||
            text.Contains("CGST", StringComparison.Ordinal) ||
            text.Contains("SGST", StringComparison.Ordinal) ||
            text.Contains("UTGST", StringComparison.Ordinal))
        {
            // The legacy GST Journal posts Output GST on the Debit side
            // and Input GST on the Credit side.
            return drCr.Equals("Dr", StringComparison.OrdinalIgnoreCase)
                ? "Output Tax"
                : "Input Tax";
        }
        return "Manual Note";
    }

    private static bool IsBlankRow(IReadOnlyList<string> row) =>
        row.All(string.IsNullOrWhiteSpace);

    private static bool IsRepeatedHeader(IReadOnlyList<string> row)
    {
        var headers = row.Select(NormalizeHeader).ToArray();
        return headers.Any(value => value is "LEDGER" or "LEDGERNAME" or "PARTICULARS") &&
               headers.Any(value => value is "AMOUNT" or "DEBIT" or "CREDIT" or "DRCR");
    }

    private static bool IsTotalRow(string ledger)
    {
        var normalized = NormalizeToken(ledger);
        return normalized is "TOTAL" or "GRANDTOTAL" or "BALANCE" or "DIFFERENCE";
    }

    private static bool IsDirectionText(string value)
    {
        var normalized = NormalizeToken(value);
        return normalized is "DR" or "CR" or "DEBIT" or "CREDIT";
    }

    private static string NormalizeHeader(string value)
    {
        var mapped = ImportHeaderMappingRuntime.Resolve(ImportHeaderMappingCatalog.Journal, value);
        var effective = mapped.Length > 0 ? mapped : value;
        return string.Concat(effective.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }

    private static string NormalizeToken(string value) =>
        string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var builder = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                result.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(character);
            }
        }
        result.Add(builder.ToString());
        return result.ToArray();
    }
}

public sealed record ManualJournalBulkImportResult(
    IReadOnlyList<JournalVoucher> Vouchers,
    IReadOnlyList<string> Warnings);

public sealed record ManualJournalLinePasteResult(
    IReadOnlyList<JournalLine> Lines,
    IReadOnlyList<string> Warnings);
