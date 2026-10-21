using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Domain.Tax;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AccountingAutomationHub.Infrastructure.Tax;

public sealed partial class Form26AsPdfImportService(SqliteTaxCreditRepository repository)
{
    public TaxCreditImportResult Import(string path, long clientId, string financialYear, string expectedPan)
    {
        var parsed = Parse(path, clientId, financialYear);
        var warnings = new List<string>(parsed.Warnings);

        if (parsed.Statement is null)
            return parsed;

        var normalizedExpectedPan = NormalizeCode(expectedPan);
        if (!IsValidPan(normalizedExpectedPan))
        {
            return new TaxCreditImportResult
            {
                Imported = false,
                Message = "The active Client has no valid verified PAN (and no valid PAN could be derived from GSTIN). Set the Client PAN before replacing 26AS tax-credit authority.",
                Statement = parsed.Statement,
                Rows = parsed.Rows,
                Warnings = warnings
            };
        }
        if (!IsValidPan(parsed.Statement.Pan))
        {
            return new TaxCreditImportResult
            {
                Imported = false,
                Message = "The 26AS PAN could not be read reliably from the Annual Tax Statement header. Current tax-credit authority was not replaced.",
                Statement = parsed.Statement,
                Rows = parsed.Rows,
                Warnings = warnings
            };
        }
        if (!parsed.Statement.Pan.Equals(normalizedExpectedPan, StringComparison.OrdinalIgnoreCase))
        {
            return new TaxCreditImportResult
            {
                Imported = false,
                Message = $"26AS PAN {parsed.Statement.Pan} does not match active Client PAN {normalizedExpectedPan}. No tax-credit authority was replaced.",
                Statement = parsed.Statement,
                Rows = parsed.Rows,
                Warnings = warnings
            };
        }

        if (!IsValidFinancialYear(parsed.Statement.FinancialYear))
        {
            return new TaxCreditImportResult
            {
                Imported = false,
                Message = "The 26AS Financial Year could not be read reliably from the Annual Tax Statement header. Current tax-credit authority was not replaced.",
                Statement = parsed.Statement,
                Rows = parsed.Rows,
                Warnings = warnings
            };
        }
        if (!FinancialYearsEquivalent(financialYear, parsed.Statement.FinancialYear))
        {
            return new TaxCreditImportResult
            {
                Imported = false,
                Message = $"26AS Financial Year {parsed.Statement.FinancialYear} does not match active Client FY {financialYear}. No tax-credit authority was replaced.",
                Statement = parsed.Statement,
                Rows = parsed.Rows,
                Warnings = warnings
            };
        }
        if (!parsed.Statement.IsCompleteAuthority)
        {
            return new TaxCreditImportResult
            {
                Imported = false,
                Message = "This 26AS contains transaction(s) in a special tax-credit part that Fix88 does not yet parse completely. To avoid incomplete authority, the current 26AS authority was not replaced.",
                Statement = parsed.Statement,
                Rows = parsed.Rows,
                Warnings = warnings
            };
        }

        var duplicate = repository.FindByHash(clientId, financialYear, parsed.Statement.StatementType, parsed.Statement.SourceHash);
        if (duplicate is not null)
        {
            parsed.Duplicate = true;
            parsed.Imported = false;
            parsed.Message = $"This {parsed.Statement.StatementType} PDF was already imported for {financialYear}. Current tax-credit authority is unchanged.";
            return parsed;
        }

        if (parsed.Rows.Count == 0)
        {
            parsed.Message = "26AS was recognized, but no supported TDS/TCS transaction rows were found. Current authority was not replaced.";
            return parsed;
        }

        parsed.Statement.ClientId = clientId;
        parsed.Statement.FinancialYear = financialYear;
        parsed.Statement.ImportedUtc = DateTime.UtcNow;
        parsed.Statement.IsCurrent = true;
        foreach (var row in parsed.Rows)
        {
            row.ClientId = clientId;
            row.FinancialYear = financialYear;
        }

        var id = repository.ReplaceCurrent(parsed.Statement, parsed.Rows);
        parsed.Statement.Id = id;
        parsed.Imported = true;
        parsed.Message = $"Imported {parsed.Rows.Count:N0} 26AS tax-credit row(s) for {financialYear}.";
        return parsed;
    }

    public TaxCreditImportResult Parse(string path, long clientId, string fallbackFinancialYear)
    {
        _ = fallbackFinancialYear; // Never substitute the active FY for an unreadable 26AS header; authority validation must fail closed.
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new TaxCreditImportResult { Message = "The selected 26AS PDF is unavailable." };

        var text = ReadPdfText(path);
        if (string.IsNullOrWhiteSpace(text))
            return new TaxCreditImportResult { Message = "The PDF does not contain readable embedded text." };

        if (!LooksLike26As(text))
            return new TaxCreditImportResult { Message = "The selected PDF is not recognized as an Annual Tax Statement / Form 26AS." };

        var financialYear = FirstGroup(text,
            @"Financial\s+Year\s*[:\-]?\s*(\d{4}\s*[-–]\s*\d{2,4})",
            @"Financial\s+Year\s*[:\-]?\s*(\d{4}\s+\d{2,4})");
        financialYear = NormalizeFinancialYear(financialYear);

        var pan = NormalizeCode(FirstGroup(text,
            @"Permanent\s+Account\s+Number\s*\(PAN\)\s*([A-Z]{5}\d{4}[A-Z])",
            @"Assessee\s+PAN\s*[:\-]?\s*([A-Z]{5}\d{4}[A-Z])"));
        var assessmentYear = NormalizeFinancialYear(FirstGroup(text,
            @"Assessment\s+Year\s*[:\-]?\s*(\d{4}\s*[-–]\s*\d{2,4})"));
        var assesseeName = CleanName(FirstGroup(text,
            @"Name\s+of\s+Assessee\s+(.+?)(?=\s+Address\s+of\s+Assessee|\r?$)",
            @"Assessee\s+Name\s*[:\-]?\s*([^\r\n]+)"));
        var dataUpdatedTill = ParseDate(FirstGroup(text,
            @"Data\s+updated\s+till\s+(\d{1,2}-[A-Za-z]{3}-\d{4})"));

        var rows = new List<TaxCreditStatementRow>();
        var warnings = new List<string>();
        ParsePartI(text, clientId, financialYear, rows, warnings);
        ParsePartVI(text, clientId, financialYear, rows, warnings);

        var unsupportedParts = FindUnsupportedTaxCreditParts(text);
        foreach (var part in unsupportedParts)
            warnings.Add($"{part} contains transaction(s) that are not yet fully parsed by Fix88; authority replacement is blocked until that part is supported.");

        var statement = new TaxCreditStatementImport
        {
            ClientId = clientId,
            FinancialYear = financialYear,
            StatementType = "26AS",
            Pan = pan,
            AssesseeName = assesseeName,
            AssessmentYear = assessmentYear,
            DataUpdatedTill = dataUpdatedTill,
            SourcePath = Path.GetFullPath(path),
            SourceHash = ComputeHash(path),
            ParseNotes = BuildParseNotes(rows, warnings),
            RawText = text,
            IsCompleteAuthority = unsupportedParts.Count == 0,
            ImportedUtc = DateTime.UtcNow,
            IsCurrent = true
        };

        if (pan.Length == 0) warnings.Add("PAN could not be read from the Annual Tax Statement header.");
        if (financialYear.Length == 0) warnings.Add("Financial Year could not be read from the Annual Tax Statement header.");

        return new TaxCreditImportResult
        {
            Statement = statement,
            Rows = rows,
            Warnings = warnings,
            Message = $"Recognized Form 26AS with {rows.Count:N0} supported transaction row(s)."
        };
    }

    private static void ParsePartI(
        string text,
        long clientId,
        string financialYear,
        ICollection<TaxCreditStatementRow> output,
        ICollection<string> warnings)
    {
        var segment = Segment(text, "PART-I - Details of Tax Deducted at Source", "PART-II");
        if (segment.Length == 0) return;
        ParseTanBasedRows(segment, "I", "TDS", clientId, financialYear, output, warnings);
    }

    private static void ParsePartVI(
        string text,
        long clientId,
        string financialYear,
        ICollection<TaxCreditStatementRow> output,
        ICollection<string> warnings)
    {
        var segment = Segment(text, "PART-VI-Details of Tax Collected at Source", "PART-VII");
        if (segment.Length == 0)
            segment = Segment(text, "PART-VI - Details of Tax Collected at Source", "PART-VII");
        if (segment.Length == 0) return;
        ParseTanBasedRows(segment, "VI", "TCS", clientId, financialYear, output, warnings);
    }

    private static void ParseTanBasedRows(
        string segment,
        string part,
        string taxKind,
        long clientId,
        string financialYear,
        ICollection<TaxCreditStatementRow> output,
        ICollection<string> warnings)
    {
        string currentName = string.Empty;
        string currentTan = string.Empty;
        var detailOrdinal = 0;

        foreach (var rawLine in SplitLines(segment))
        {
            var line = CollapseWhitespace(rawLine);
            if (line.Length == 0 || IsHeaderLine(line) || line.Contains("No Transactions Present", StringComparison.OrdinalIgnoreCase))
                continue;

            var summary = DeductorSummaryRegex().Match(line);
            if (summary.Success)
            {
                currentName = CleanName(summary.Groups["name"].Value);
                currentTan = NormalizeCode(summary.Groups["tan"].Value);
                continue;
            }

            var detail = DetailRegex().Match(line);
            if (!detail.Success) continue;

            if (currentTan.Length == 0)
            {
                warnings.Add($"Part {part}: a transaction row was found before a deductor/collector TAN header and was skipped.");
                continue;
            }

            detailOrdinal++;
            var transactionDate = ParseDate(detail.Groups["date"].Value);
            var bookingDate = ParseDate(detail.Groups["bookingdate"].Value);
            var amount = ParseMoneyPaise(detail.Groups["amount"].Value);
            var deducted = ParseMoneyPaise(detail.Groups["tax"].Value);
            var deposited = ParseMoneyPaise(detail.Groups["deposited"].Value);
            var section = detail.Groups["section"].Value.Trim();
            var status = detail.Groups["status"].Value.Trim().ToUpperInvariant();
            var remarks = detail.Groups["remarks"].Value.Trim();
            if (remarks == "-") remarks = string.Empty;

            var row = new TaxCreditStatementRow
            {
                ClientId = clientId,
                FinancialYear = financialYear,
                StatementType = "26AS",
                Part = part,
                TaxKind = taxKind,
                DeductorName = currentName,
                DeductorTan = currentTan,
                Section = section,
                TransactionDate = transactionDate,
                BookingStatus = status,
                BookingDate = bookingDate,
                Remarks = remarks,
                AmountPaidPaise = amount,
                TaxDeductedPaise = deducted,
                TaxDepositedPaise = deposited
            };
            row.RowKey = BuildRowKey(row, detailOrdinal);
            output.Add(row);
        }
    }

    private static bool IsHeaderLine(string line) =>
        line.StartsWith("Sr. No.", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Name of Deductor", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Name of Collector", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Amount Paid", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Tax Deducted", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Tax Collected", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("TDS Deposited", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("TCS Deposited", StringComparison.OrdinalIgnoreCase);

    private static string BuildRowKey(TaxCreditStatementRow row, int ordinal) => string.Join("|",
        row.Part,
        row.TaxKind,
        row.DeductorTan.ToUpperInvariant(),
        row.Section.ToUpperInvariant(),
        row.TransactionDate?.ToString("yyyyMMdd") ?? string.Empty,
        row.BookingDate?.ToString("yyyyMMdd") ?? string.Empty,
        row.AmountPaidPaise.ToString(CultureInfo.InvariantCulture),
        row.TaxDeductedPaise.ToString(CultureInfo.InvariantCulture),
        row.TaxDepositedPaise.ToString(CultureInfo.InvariantCulture),
        ordinal.ToString(CultureInfo.InvariantCulture));

    private static string BuildParseNotes(IReadOnlyCollection<TaxCreditStatementRow> rows, IReadOnlyCollection<string> warnings)
    {
        var tds = rows.Count(row => row.TaxKind.Equals("TDS", StringComparison.OrdinalIgnoreCase));
        var tcs = rows.Count(row => row.TaxKind.Equals("TCS", StringComparison.OrdinalIgnoreCase));
        var text = $"Part-I TDS rows={tds}; Part-VI TCS rows={tcs}.";
        if (warnings.Count > 0) text += $" Warnings: {string.Join(" | ", warnings.Take(4))}";
        return text;
    }

    private static IReadOnlyList<string> FindUnsupportedTaxCreditParts(string text)
    {
        var unsupported = new List<string>();
        foreach (var (part, endPart) in new[] { ("PART-III", "PART-IV"), ("PART-IV", "PART-V"), ("PART-V", "PART-VI") })
        {
            var segment = Segment(text, part, endPart);
            if (segment.Length == 0 || segment.Contains("No Transactions Present", StringComparison.OrdinalIgnoreCase)) continue;
            // Official empty 26AS parts explicitly say "No Transactions Present". If that marker is absent,
            // hold authority fail-closed rather than silently assuming a wrapped/unfamiliar transaction layout is empty.
            unsupported.Add(part);
        }
        return unsupported;
    }

    private static bool IsValidPan(string value) =>
        Regex.IsMatch(value ?? string.Empty, @"^[A-Z]{5}\d{4}[A-Z]$", RegexOptions.IgnoreCase);

    private static bool IsValidFinancialYear(string value) =>
        Regex.IsMatch(NormalizeFinancialYear(value), @"^\d{4}-\d{2}$");

    private static bool LooksLike26As(string text) =>
        text.Contains("Annual Tax Statement", StringComparison.OrdinalIgnoreCase) &&
        (text.Contains("PART-I - Details of Tax Deducted at Source", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("PART-I", StringComparison.OrdinalIgnoreCase)) &&
        (text.Contains("Permanent Account Number", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("Assessee PAN", StringComparison.OrdinalIgnoreCase));

    private static string ReadPdfText(string path)
    {
        using var document = PdfDocument.Open(path);
        var builder = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page);
            if (!string.IsNullOrWhiteSpace(text)) builder.AppendLine(text);
        }
        return builder.ToString();
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string Segment(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0) return string.Empty;
        var endIndex = text.IndexOf(end, startIndex + start.Length, StringComparison.OrdinalIgnoreCase);
        if (endIndex < 0) endIndex = text.Length;
        return text[startIndex..endIndex];
    }

    private static string FirstGroup(string text, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline);
            if (match.Success && match.Groups.Count > 1) return CollapseWhitespace(match.Groups[1].Value);
        }
        return string.Empty;
    }

    private static string CleanName(string value)
    {
        value = CollapseWhitespace(value).Trim(' ', ':', '-', '|');
        return value.Length > 160 ? value[..160].Trim() : value;
    }

    private static string CollapseWhitespace(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string NormalizeCode(string value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string NormalizeFinancialYear(string value)
    {
        var normalized = (value ?? string.Empty).Replace('–', '-').Trim();
        var match = Regex.Match(normalized, @"(?<start>\d{4})\s*[- ]\s*(?<end>\d{2,4})");
        if (!match.Success) return normalized;
        var start = match.Groups["start"].Value;
        var end = match.Groups["end"].Value;
        if (end.Length == 4) end = end[^2..];
        return $"{start}-{end}";
    }

    private static bool FinancialYearsEquivalent(string left, string right) =>
        NormalizeFinancialYear(left).Equals(NormalizeFinancialYear(right), StringComparison.OrdinalIgnoreCase);

    private static DateTime? ParseDate(string value)
    {
        var cleaned = CollapseWhitespace(value);
        foreach (var format in new[] { "dd-MMM-yyyy", "d-MMM-yyyy", "dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd" })
        {
            if (DateTime.TryParseExact(cleaned, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;
        }
        return DateTime.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fallback) ? fallback : null;
    }

    private static long ParseMoneyPaise(string value)
    {
        var text = (value ?? string.Empty).Trim();
        var negative = text.StartsWith('(') && text.EndsWith(')');
        text = text.Trim('(', ')').Replace(",", string.Empty, StringComparison.Ordinal);
        if (!decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
            return 0;
        if (negative) amount = -Math.Abs(amount);
        return checked((long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
    }

    private static string ComputeHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [GeneratedRegex(@"^\s*\d+\s+(?<name>.+?)\s+(?<tan>[A-Z]{4}\d{5}[A-Z])\s+\(?[\d,]+(?:\.\d+)?\)?\s+\(?[\d,]+(?:\.\d+)?\)?\s+\(?[\d,]+(?:\.\d+)?\)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DeductorSummaryRegex();

    [GeneratedRegex(@"^\s*\d+\s+(?<section>[0-9A-Z()\-/]+)\s+(?<date>\d{1,2}-[A-Za-z]{3}-\d{4})\s+(?<status>[UMPFOZ])\s+(?<bookingdate>\d{1,2}-[A-Za-z]{3}-\d{4})\s+(?<remarks>.*?)\s+(?<amount>\(?[\d,]+(?:\.\d+)?\)?)\s+(?<tax>\(?[\d,]+(?:\.\d+)?\)?)\s+(?<deposited>\(?[\d,]+(?:\.\d+)?\)?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DetailRegex();
}
