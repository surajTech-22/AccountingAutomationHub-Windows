using System.Globalization;
using System.Text;
using AccountingAutomationHub.Domain.Gst;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Spreadsheets;

namespace AccountingAutomationHub.Infrastructure.Gst;

public sealed class GstLedgerImportService(SqliteGstRepository repository)
{
    public GstImportResult Import(
        string path,
        string ledgerType,
        long clientId,
        string financialYear,
        string expectedGstin = "",
        IReadOnlyList<string>? additionalGstins = null)
    {
        var normalizedType = NormalizeLedgerType(ledgerType);
        var registrationGstin = expectedGstin;
        if (additionalGstins is { Count: > 0 })
        {
            registrationGstin = new[] { expectedGstin }.Concat(additionalGstins)
                .FirstOrDefault(gstin => IsValidSourceForClient(path, gstin, financialYear))
                ?? throw new InvalidDataException("GST ledger does not match any registered GSTIN for this client.");
        }
        var periods = Parse(
            path,
            normalizedType,
            clientId,
            financialYear,
            registrationGstin);

        foreach (var period in periods)
            period.Gstin = registrationGstin.Trim().ToUpperInvariant();
        repository.ReplaceLedgerPeriods(
            clientId,
            financialYear,
            normalizedType,
            periods,
            registrationGstin);

        return new GstImportResult
        {
            ImportType = normalizedType,
            FilesProcessed = 1,
            PeriodsImported = periods.Count,
            PeriodsRejected = 0,
            Warnings = []
        };
    }

    public bool IsValidSourceForClient(
        string path,
        string expectedGstin,
        string financialYear)
    {
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(expectedGstin)) return false;
        var rows = ReadRows(path);
        var sample = string.Join(
            " ",
            rows.Take(80).SelectMany(row => row).Where(value => !string.IsNullOrWhiteSpace(value)));
        var gstins = System.Text.RegularExpressions.Regex.Matches(
                sample.ToUpperInvariant(),
                @"\b\d{2}[A-Z]{5}\d{4}[A-Z][A-Z0-9]Z[A-Z0-9]\b")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expected = expectedGstin.Trim().ToUpperInvariant();
        if (gstins.Length == 0 || !gstins.Contains(expected, StringComparer.OrdinalIgnoreCase)) return false;

        if (!TryFinancialYearDates(financialYear, out var fyStart, out var fyEnd)) return true;
        DateTime? sourceFrom = null;
        DateTime? sourceTo = null;
        foreach (var row in rows.Take(30))
        {
            for (var index = 0; index < row.Length; index++)
            {
                var label = NormalizeHeader(row[index]);
                if (label is not ("FROM" or "TO")) continue;
                var value = index + 1 < row.Length ? row[index + 1] : string.Empty;
                var parsed = ParseDate(value);
                if (!parsed.HasValue) continue;
                if (label == "FROM") sourceFrom = parsed;
                else sourceTo = parsed;
            }
        }

        // Official GST portal exports always carry a From/To range. When one is present,
        // require actual calendar overlap with this Client/FY before moving the evidence.
        // If an alternate export omits those labels, exact GSTIN remains the fail-closed
        // identity authority and the normal transaction parser performs the FY filtering.
        if (sourceFrom.HasValue || sourceTo.HasValue)
        {
            var from = sourceFrom ?? sourceTo!.Value;
            var to = sourceTo ?? sourceFrom!.Value;
            if (to < from) (from, to) = (to, from);
            return from <= fyEnd && to >= fyStart;
        }
        return true;
    }

    private static bool TryFinancialYearDates(string financialYear, out DateTime start, out DateTime end)
    {
        start = default; end = default;
        var token = (financialYear ?? string.Empty).Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!int.TryParse(token, out var startYear)) return false;
        start = new DateTime(startYear, 4, 1);
        end = new DateTime(startYear + 1, 3, 31);
        return true;
    }

    public IReadOnlyList<GstLedgerPeriodBalance> Parse(
        string path,
        string ledgerType,
        long clientId,
        string financialYear,
        string expectedGstin = "")
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The selected GST ledger file was not found.", path);
        }

        var rows = ReadRows(path);
        ValidateGstin(rows, expectedGstin);
        var normalizedType = NormalizeLedgerType(ledgerType);

        // GST portal ledger exports use a stable multi-row heading and fixed
        // transaction columns. Their merged headings do not always produce a
        // single usable header row, so prefer the official positional layout
        // and use header mapping only for alternate exports.
        var transactions = ParseOfficialPortalTransactions(
            rows,
            normalizedType,
            financialYear);
        if (transactions.Count == 0)
        {
            transactions = ParseMappedTransactions(rows, financialYear);
        }

        if (transactions.Count == 0)
        {
            throw new InvalidDataException(
                $"No dated {normalizedType} transactions for FY {financialYear} were found.");
        }

        return transactions
            .OrderBy(transaction => transaction.Date)
            .GroupBy(transaction => $"{transaction.Date.Year:0000}{transaction.Date.Month:00}")
            .Select(group => BuildPeriod(
                group.Key,
                group.ToArray(),
                normalizedType,
                clientId,
                financialYear,
                path))
            .ToArray();
    }


    private static List<LedgerTransaction> ParseMappedTransactions(
        IReadOnlyList<string[]> rows,
        string financialYear)
    {
        var headerIndex = FindHeaderRow(rows);
        if (headerIndex < 0)
        {
            return [];
        }

        var header = rows[headerIndex];
        var map = header
            .Select((value, index) => (Key: NormalizeHeader(value), Index: index))
            .Where(item => item.Key.Length > 0)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Index,
                StringComparer.OrdinalIgnoreCase);

        var transactions = new List<LedgerTransaction>();
        foreach (var row in rows.Skip(headerIndex + 1))
        {
            var date = ParseDate(Value(row, map,
                "DATE", "TRANSACTIONDATE", "POSTINGDATE", "TAXPERIOD"));
            if (!date.HasValue || !IsDateInFinancialYear(date.Value, financialYear))
            {
                continue;
            }

            var transactionType = Value(row, map,
                "TRANSACTIONTYPE", "TYPE", "DESCRIPTION").ToUpperInvariant();

            transactions.Add(new LedgerTransaction
            {
                Date = date.Value,
                TransactionType = transactionType,
                IgstTransaction = Amount(row, map,
                    "IGST", "IGSTTAX", "TXNIGST", "IGSTTOTAL"),
                CgstTransaction = Amount(row, map,
                    "CGST", "CGSTTAX", "TXNCGST", "CGSTTOTAL"),
                SgstTransaction = Amount(row, map,
                    "SGST", "SGSTTAX", "UTGST", "TXNSGST", "SGSTTOTAL"),
                CessTransaction = Amount(row, map,
                    "CESS", "CESSTAX", "TXNCESS", "CESSTOTAL"),
                IgstBalance = Amount(row, map,
                    "BALIGST", "BALIGSTTOTAL", "CLOSINGIGST", "IGSTBALANCE"),
                CgstBalance = Amount(row, map,
                    "BALCGST", "BALCGSTTOTAL", "CLOSINGCGST", "CGSTBALANCE"),
                SgstBalance = Amount(row, map,
                    "BALSGST", "BALSGSTTOTAL", "CLOSINGSGST", "SGSTBALANCE", "BALUTGST"),
                CessBalance = Amount(row, map,
                    "BALCESS", "BALCESSTOTAL", "CLOSINGCESS", "CESSBALANCE"),
                TotalBalance = Amount(row, map,
                    "BALTOTAL", "CLOSINGBALANCE", "BALANCE", "TOTALBALANCE")
            });
        }
        return transactions;
    }

    private static List<LedgerTransaction> ParseOfficialPortalTransactions(
        IReadOnlyList<string[]> rows,
        string ledgerType,
        string financialYear)
    {
        return ledgerType.Equals(
                "Electronic Credit Ledger",
                StringComparison.OrdinalIgnoreCase)
            ? ParseOfficialCreditLedger(rows, financialYear)
            : ParseOfficialCashLedger(rows, financialYear);
    }

    private static List<LedgerTransaction> ParseOfficialCreditLedger(
        IReadOnlyList<string[]> rows,
        string financialYear)
    {
        var result = new List<LedgerTransaction>();
        foreach (var row in rows.Skip(8))
        {
            var description = Cell(row, 5);
            var date = ParseDate(Cell(row, 4)) ?? ParseDate(Cell(row, 2));
            if (description.Length == 0 ||
                !date.HasValue ||
                !IsDateInFinancialYear(date.Value, financialYear))
            {
                continue;
            }

            result.Add(new LedgerTransaction
            {
                Date = date.Value,
                TransactionType = Cell(row, 6).ToUpperInvariant(),
                IgstTransaction = ParseAmount(Cell(row, 7)),
                CgstTransaction = ParseAmount(Cell(row, 8)),
                SgstTransaction = ParseAmount(Cell(row, 9)),
                CessTransaction = ParseAmount(Cell(row, 10)),
                IgstBalance = ParseAmount(Cell(row, 12)),
                CgstBalance = ParseAmount(Cell(row, 13)),
                SgstBalance = ParseAmount(Cell(row, 14)),
                CessBalance = ParseAmount(Cell(row, 15)),
                TotalBalance = ParseAmount(Cell(row, 16))
            });
        }
        return result;
    }

    private static List<LedgerTransaction> ParseOfficialCashLedger(
        IReadOnlyList<string[]> rows,
        string financialYear)
    {
        var result = new List<LedgerTransaction>();
        foreach (var row in rows.Skip(8))
        {
            var description = Cell(row, 7);
            var date = ParseDate(Cell(row, 6)) ??
                       ParseDate(Cell(row, 2)) ??
                       ParseDate(Cell(row, 4));
            if (description.Length == 0 ||
                !date.HasValue ||
                !IsDateInFinancialYear(date.Value, financialYear))
            {
                continue;
            }

            result.Add(new LedgerTransaction
            {
                Date = date.Value,
                TransactionType = Cell(row, 8).ToUpperInvariant(),
                IgstTransaction = ParseAmount(Cell(row, 14)),
                CgstTransaction = ParseAmount(Cell(row, 20)),
                SgstTransaction = ParseAmount(Cell(row, 26)),
                CessTransaction = ParseAmount(Cell(row, 32)),
                IgstBalance = ParseAmount(Cell(row, 38)),
                CgstBalance = ParseAmount(Cell(row, 44)),
                SgstBalance = ParseAmount(Cell(row, 50)),
                CessBalance = ParseAmount(Cell(row, 56)),
                TotalBalance =
                    ParseAmount(Cell(row, 38)) +
                    ParseAmount(Cell(row, 44)) +
                    ParseAmount(Cell(row, 50)) +
                    ParseAmount(Cell(row, 56))
            });
        }
        return result;
    }

    private static string Cell(IReadOnlyList<string> row, int oneBasedColumn)
    {
        var index = oneBasedColumn - 1;
        return index >= 0 && index < row.Count
            ? row[index].Trim()
            : string.Empty;
    }

    private static string NormalizeLedgerType(string ledgerType) =>
        ledgerType.Contains("Credit", StringComparison.OrdinalIgnoreCase)
            ? "Electronic Credit Ledger"
            : "Electronic Cash Ledger";

    private static void ValidateGstin(
        IReadOnlyList<string[]> rows,
        string expectedGstin)
    {
        if (string.IsNullOrWhiteSpace(expectedGstin))
        {
            return;
        }

        var sample = string.Join(
            " ",
            rows.Take(80).SelectMany(row => row).Where(value => !string.IsNullOrWhiteSpace(value)));
        var matches = System.Text.RegularExpressions.Regex.Matches(
            sample.ToUpperInvariant(),
            @"\b\d{2}[A-Z]{5}\d{4}[A-Z][A-Z0-9]Z[A-Z0-9]\b");
        if (matches.Count == 0)
        {
            return;
        }

        var expected = expectedGstin.Trim().ToUpperInvariant();
        if (!matches.Cast<System.Text.RegularExpressions.Match>().Select(match => match.Value).Contains(expected, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The GST ledger belongs to a different GSTIN. Expected {expectedGstin}.");
        }
    }

    private static bool IsDateInFinancialYear(DateTime date, string financialYear)
    {
        var startText = financialYear.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!int.TryParse(startText, out var startYear))
        {
            return true;
        }
        return date.Month >= 4
            ? date.Year == startYear
            : date.Year == startYear + 1;
    }

    private static GstLedgerPeriodBalance BuildPeriod(
        string periodKey,
        IReadOnlyList<LedgerTransaction> rows,
        string ledgerType,
        long clientId,
        string financialYear,
        string path)
    {
        var first = rows.First();
        var last = rows.Last();

        var firstBalance = ResolveBalances(first);
        var firstTransaction = (
            first.IgstTransaction,
            first.CgstTransaction,
            first.SgstTransaction,
            first.CessTransaction);

        var sign = first.TransactionType.Contains("DEBIT", StringComparison.OrdinalIgnoreCase)
            ? 1m
            : first.TransactionType.Contains("CREDIT", StringComparison.OrdinalIgnoreCase)
                ? -1m
                : 0m;

        var opening = (
            Igst: firstBalance.Igst + sign * firstTransaction.Item1,
            Cgst: firstBalance.Cgst + sign * firstTransaction.Item2,
            Sgst: firstBalance.Sgst + sign * firstTransaction.Item3,
            Cess: firstBalance.Cess + sign * firstTransaction.Item4);

        var closing = ResolveBalances(last);

        return new GstLedgerPeriodBalance
        {
            ClientId = clientId,
            FinancialYear = financialYear,
            LedgerType = ledgerType,
            PeriodKey = periodKey,
            PeriodLabel = GstJournalGenerationService.PeriodLabel(periodKey, "Monthly"),
            OpeningIgst = opening.Igst,
            OpeningCgst = opening.Cgst,
            OpeningSgst = opening.Sgst,
            OpeningCess = opening.Cess,
            ClosingIgst = closing.Igst,
            ClosingCgst = closing.Cgst,
            ClosingSgst = closing.Sgst,
            ClosingCess = closing.Cess,
            SourcePath = path,
            ImportedUtc = DateTime.UtcNow
        };
    }

    private static (decimal Igst, decimal Cgst, decimal Sgst, decimal Cess)
        ResolveBalances(LedgerTransaction row)
    {
        var heads = row.IgstBalance + row.CgstBalance + row.SgstBalance + row.CessBalance;
        if (heads != 0m || row.TotalBalance == 0m)
        {
            return (row.IgstBalance, row.CgstBalance, row.SgstBalance, row.CessBalance);
        }

        // Some credit-ledger exports provide only a total. Preserve it under IGST
        // rather than losing the monthly opening/closing evidence.
        return (row.TotalBalance, 0m, 0m, 0m);
    }

    private static IReadOnlyList<string[]> ReadRows(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return File.ReadLines(path)
                .Where(line => line.Length > 0)
                .Select(ParseCsvLine)
                .ToArray();
        }

        var workbook = new OpenXmlWorkbookReader().Read(path, CancellationToken.None);
        var worksheet = workbook.Worksheets.Values
            .OrderByDescending(sheet => sheet.Rows.Count)
            .FirstOrDefault()
            ?? throw new InvalidDataException("The workbook does not contain a readable worksheet.");

        var maxColumn = worksheet.Rows.Values
            .SelectMany(row => row.Keys)
            .DefaultIfEmpty(1)
            .Max();

        return Enumerable.Range(1, worksheet.MaxRow)
            .Select(rowNumber =>
                Enumerable.Range(1, maxColumn)
                    .Select(column => worksheet.GetCell(rowNumber, column))
                    .ToArray())
            .ToArray();
    }

    private static int FindHeaderRow(IReadOnlyList<string[]> rows)
    {
        for (var index = 0; index < Math.Min(rows.Count, 50); index++)
        {
            var normalized = rows[index].Select(NormalizeHeader).ToArray();
            var hasDate = normalized.Any(value =>
                value is "DATE" or "TRANSACTIONDATE" or "POSTINGDATE");
            var hasBalance = normalized.Any(value =>
                value.Contains("BAL", StringComparison.Ordinal) ||
                value.Contains("CLOSING", StringComparison.Ordinal));
            if (hasDate && hasBalance)
            {
                return index;
            }
        }
        return -1;
    }

    private static string Value(
        IReadOnlyList<string> row,
        IReadOnlyDictionary<string, int> map,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out var index) &&
                index >= 0 &&
                index < row.Count)
            {
                return row[index].Trim();
            }
        }
        return string.Empty;
    }

    private static decimal Amount(
        IReadOnlyList<string> row,
        IReadOnlyDictionary<string, int> map,
        params string[] keys) =>
        ParseAmount(Value(row, map, keys));

    private static DateTime? ParseDate(string value)
    {
        value = value.Trim();
        if (double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var serialDate) &&
            serialDate is >= 1d and <= 2958465d)
        {
            try
            {
                return DateTime.FromOADate(serialDate);
            }
            catch
            {
                // Continue with text date parsing.
            }
        }

        if (value.Length == 6 && int.TryParse(value, out _))
        {
            return new DateTime(
                int.Parse(value[..4], CultureInfo.InvariantCulture),
                int.Parse(value[4..], CultureInfo.InvariantCulture),
                1);
        }

        string[] formats =
        [
            "dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd",
            "dd/MM/yy", "dd-MM-yy", "MMM-yyyy", "MMM-yy"
        ];

        return DateTime.TryParseExact(
            value,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var exact)
                ? exact
                : DateTime.TryParse(value, out var parsed)
                    ? parsed
                    : null;
    }

    private static decimal ParseAmount(string value)
    {
        var cleaned = value
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("₹", string.Empty, StringComparison.Ordinal)
            .Trim();

        var parenthesized = cleaned.StartsWith("(") && cleaned.EndsWith(")");
        cleaned = cleaned.Trim('(', ')');

        if (!decimal.TryParse(
                cleaned,
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            return 0m;
        }
        return parenthesized ? -amount : amount;
    }

    private static string NormalizeHeader(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
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
                values.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(character);
            }
        }

        values.Add(builder.ToString());
        return values.ToArray();
    }

    private sealed class LedgerTransaction
    {
        public DateTime Date { get; set; }
        public string TransactionType { get; set; } = string.Empty;
        public decimal IgstTransaction { get; set; }
        public decimal CgstTransaction { get; set; }
        public decimal SgstTransaction { get; set; }
        public decimal CessTransaction { get; set; }
        public decimal IgstBalance { get; set; }
        public decimal CgstBalance { get; set; }
        public decimal SgstBalance { get; set; }
        public decimal CessBalance { get; set; }
        public decimal TotalBalance { get; set; }
    }
}
