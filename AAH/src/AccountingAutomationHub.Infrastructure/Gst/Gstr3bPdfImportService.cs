using System.Globalization;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Domain.Gst;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AccountingAutomationHub.Infrastructure.Gst;

public sealed partial class Gstr3bPdfImportService(SqliteGstRepository repository)
{
    public GstImportResult Import(
        IEnumerable<string> paths,
        long clientId,
        string financialYear,
        string fallbackGstin,
        IReadOnlyList<string>? additionalGstins = null)
    {
        var accepted = new[] { fallbackGstin }.Concat(additionalGstins ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var processed = 0;
        var imported = 0;
        var rejected = 0;

        foreach (var path in paths.Where(File.Exists))
        {
            processed++;
            try
            {
                // Existing single-GSTIN imports retain their usual fallback.
                // For multi-GSTIN clients require the source PDF's actual
                // printed GSTIN, never assign an unlabeled PDF to the primary.
                var record = Parse(path, clientId, financialYear,
                    additionalGstins is { Count: > 0 } ? string.Empty : fallbackGstin);
                if (record.PeriodKey.Length == 0)
                {
                    rejected++;
                    warnings.Add($"{Path.GetFileName(path)}: return period could not be identified.");
                    continue;
                }

                if (accepted.Count > 0 && !accepted.Contains(record.Gstin.Trim().ToUpperInvariant()))
                    throw new InvalidDataException("GSTR-3B GSTIN does not belong to the selected client.");
                repository.ReplaceReturn(record);
                imported++;
                if (!record.ParseValid)
                {
                    warnings.Add(
                        $"{Path.GetFileName(path)}: imported for review because one or more GSTR-3B tables were incomplete.");
                }
            }
            catch (Exception exception)
            {
                rejected++;
                warnings.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        return new GstImportResult
        {
            ImportType = "GSTR-3B PDF",
            FilesProcessed = processed,
            PeriodsImported = imported,
            PeriodsRejected = rejected,
            Warnings = warnings
        };
    }

    public Gstr3bReturnRecord Parse(
        string path,
        long clientId,
        string financialYear,
        string fallbackGstin)
    {
        var text = ReadPdfText(path);
        if (text.Length == 0)
        {
            throw new InvalidDataException("The PDF did not contain readable text.");
        }

        var periodInfo = ExtractPeriod(text, financialYear, path);
        var yearDisplay = FirstMatchGroup(text, [@"^\s*Year\s*[:\-]?\s*(.+?)\s*$"]);
        if (yearDisplay.Length == 0)
        {
            yearDisplay = financialYear;
        }

        var gstin = Match(text, @"\b\d{2}[A-Z]{5}\d{4}[A-Z][A-Z0-9]Z[A-Z0-9]\b");
        if (gstin.Length == 0)
        {
            gstin = fallbackGstin;
        }

        var legalName = CleanName(FirstMatchGroup(
            text,
            [
                @"^\s*2\s*\(\s*a\s*\)\.?\s*Legal\s+name\s+of\s+the\s+registered\s+person\s*[:\-]?\s*(.+?)\s*$",
                @"^\s*Legal\s+Name\s*[:\-]?\s*(.+?)\s*$"
            ]));
        var tradeName = CleanName(FirstMatchGroup(
            text,
            [
                @"^\s*2\s*\(\s*b\s*\)\.?\s*Trade\s+name(?:\s*,\s*if\s+any)?\s*[:\-]?\s*(.+?)\s*$",
                @"^\s*Trade\s+Name\s*[:\-]?\s*(.+?)\s*$"
            ]));
        var arn = FirstMatchGroup(
            text,
            [
                @"^\s*2\s*\(\s*c\s*\)\.?\s*ARN\s*[:\-]?\s*([A-Z0-9]{10,30})\s*$",
                @"^\s*ARN\s*[:\-]?\s*([A-Z0-9]{10,30})\s*$"
            ]);
        var arnDate = ParseDate(FirstMatchGroup(
            text,
            [
                @"^\s*2\s*\(\s*d\s*\)\.?\s*Date\s+of\s+ARN\s*[:\-]?\s*([^\r\n]+?)\s*$",
                @"^\s*Date\s+of\s+ARN\s*[:\-]?\s*([^\r\n]+?)\s*$"
            ]));

        const string outwardMarker = @"^\s*\(\s*a\s*\)\s*outward\s+taxable\s+supplies";
        const string zeroRatedMarker = @"^\s*\(\s*b\s*\)\s*outward\s+taxable\s+supplies\s*\(\s*zero\s+rated\s*\)";
        const string nilExemptMarker = @"^\s*\(\s*c\s*\)\s*other\s+outward\s+supplies";
        const string rcmMarker = @"^\s*\(\s*d\s*\)\s*inward\s+supplies";
        const string nonGstOutwardMarker = @"^\s*\(\s*e\s*\)\s*non[- ]?gst\s+outward\s+supplies";

        var outward = ExtractRowVector(text, outwardMarker, zeroRatedMarker, 5);
        var zeroRated = ExtractRowVector(text, zeroRatedMarker, nilExemptMarker, 5);
        var nilExempt = ExtractRowVector(text, nilExemptMarker, rcmMarker, 5);
        var rcm = ExtractRowVector(text, rcmMarker, nonGstOutwardMarker, 5);
        var nonGstOutward = ExtractRowVector(
            text,
            nonGstOutwardMarker,
            @"^\s*3\.1\.1\s+Details",
            5);

        var ecommerceOperator = ExtractRowVector(
            text,
            @"^\s*\(\s*i\s*\)\s*Taxable\s+supplies\s+on\s+which\s+electronic\s+commerce\s+operator\s+pays\s+tax\s+u/s\s+9\s*\(\s*5\s*\)",
            @"^\s*\(\s*ii\s*\)\s*Taxable\s+supplies\s+made\s+by\s+registered\s+person",
            5);
        var ecommerceSupplier = ExtractRowVector(
            text,
            @"^\s*\(\s*ii\s*\)\s*Taxable\s+supplies\s+made\s+by\s+registered\s+person",
            @"^\s*3\.2\s+Out\s+of\s+supplies",
            5);

        var unregistered = ExtractRowVector(
            text,
            @"^\s*Supplies\s+made\s+to\s+Unregistered\s+Persons",
            @"^\s*Supplies\s+made\s+to\s+Composition\s+Taxable",
            2);
        var compositionTaxablePersons = ExtractRowVector(
            text,
            @"^\s*Supplies\s+made\s+to\s+Composition\s+Taxable(?:\s+Persons)?",
            @"^\s*Supplies\s+made\s+to\s+UIN\s+holders",
            2);
        var uinHolders = ExtractRowVector(
            text,
            @"^\s*Supplies\s+made\s+to\s+UIN\s+holders",
            @"^\s*4\.\s*Eligible\s+ITC",
            2);

        var importGoods = ExtractRowVector(
            text,
            @"^\s*\(\s*1\s*\)\s*Import\s+of\s+goods",
            @"^\s*\(\s*2\s*\)\s*Import\s+of\s+services",
            4);
        var importServices = ExtractRowVector(
            text,
            @"^\s*\(\s*2\s*\)\s*Import\s+of\s+services",
            @"^\s*\(\s*3\s*\)\s*Inward\s+supplies\s+liable\s+to\s+reverse\s+charge",
            4);
        var inwardRcmItc = ExtractRowVector(
            text,
            @"^\s*\(\s*3\s*\)\s*Inward\s+supplies\s+liable\s+to\s+reverse\s+charge\s*\(\s*other\s+than\s+1\s*&\s*2\s+above\s*\)",
            @"^\s*\(\s*4\s*\)\s*Inward\s+supplies\s+from\s+ISD",
            4);
        var isd = ExtractRowVector(
            text,
            @"^\s*\(\s*4\s*\)\s*Inward\s+supplies\s+from\s+ISD",
            @"^\s*\(\s*5\s*\)\s*All\s+other\s+ITC",
            4);
        var allOtherItc = ExtractRowVector(
            text,
            @"^\s*\(\s*5\s*\)\s*All\s+other\s+ITC",
            @"^\s*B\s*\.\s*ITC\s+Reversed",
            4);
        var reversedRules = ExtractRowVector(
            text,
            @"^\s*\(\s*1\s*\)\s*As\s+per\s+rules\s+38\s*,\s*42\s*&\s*43\s+of\s+CGST\s+Rules\s+and\s+section\s+17\s*\(\s*5\s*\)",
            @"^\s*\(\s*2\s*\)\s*Others",
            4);
        var reversedOthers = ExtractRowVector(
            text,
            @"^\s*\(\s*2\s*\)\s*Others",
            @"^\s*C\s*\.\s*Net\s+ITC\s+available",
            4);
        var netItc = ExtractRowVector(
            text,
            @"^\s*C\s*\.\s*Net\s+ITC\s+available\s*\(\s*A\s*-\s*B\s*\)",
            @"^\s*\(\s*D\s*\)\s*Other\s+Details",
            4);
        var otherDetails = ExtractRowVector(
            text,
            @"^\s*\(\s*D\s*\)\s*Other\s+Details",
            @"^\s*\(\s*1\s*\)\s*ITC\s+reclaimed",
            4);
        var itcReclaimed = ExtractRowVector(
            text,
            @"^\s*\(\s*1\s*\)\s*ITC\s+reclaimed\s+which\s+was\s+reversed\s+under\s+Table\s+4\s*\(\s*B\s*\)\s*\(\s*2\s*\)\s+in\s+earlier\s+tax\s+period",
            @"^\s*\(\s*2\s*\)\s*Ineligible\s+ITC",
            4);
        var ineligibleItc = ExtractRowVector(
            text,
            @"^\s*\(\s*2\s*\)\s*Ineligible\s+ITC\s+under\s+section\s+16\s*\(\s*4\s*\)\s*&\s*ITC\s+restricted\s+due\s+to\s+PoS\s+rules",
            @"^\s*5\s+Values\s+of\s+exempt",
            4);

        var tableText = NormalizeTableText(text);
        var composition = ExtractTokenVector(
            tableText,
            @"From\s+a\s+supplier\s+under\s+composition\s+scheme\s*,?\s*Exempt\s*,?\s*Nil\s+rated\s+supply",
            @"Non\s+GST\s+supply",
            2);
        var nonGst = ExtractTokenVector(
            tableText,
            @"Non\s+GST\s+supply",
            @"5\.1\s+Interest",
            2);
        var table51 = Segment(
            tableText,
            @"5\.1\s+Interest\s+and\s+Late\s+fee\s+for\s+previous\s+tax\s+period",
            @"6\.1\s+Payment\s+of\s+tax");
        var systemInterest = ExtractTokenVector(
            table51,
            @"System\s+computed\s+Interest\s+(?=(?:-|\(?-?\d))",
            @"Interest\s+Paid\s+(?=(?:-|\(?-?\d))",
            4);
        var interestPaid = ExtractTokenVector(
            table51,
            @"Interest\s+Paid\s+(?=(?:-|\(?-?\d))",
            @"Late\s+fee\s+(?=(?:-|\(?-?\d))",
            4);
        var lateFee = ExtractTokenVector(
            table51,
            @"Late\s+fee\s+(?=(?:-|\(?-?\d))",
            @"$",
            4);
        var paymentRows = ExtractPaymentRows(tableText);
        var liabilityBreakup = ExtractLiabilityBreakup(tableText);

        // Fix44: classify a legal Nil Return directly from affirmative GSTR-3B
        // evidence. A bare "Nil Return" label (including "Nil Return: No") is never
        // sufficient. PDF text extraction can split labels/values across lines, so use
        // a whitespace-normalized identity/body string while still requiring YES/Y,
        // an explicit filed-as-Nil phrase, or an explicit Nil return/filing type.
        var nilEvidenceText = Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant);
        var isNil = Regex.IsMatch(
            nilEvidenceText,
            @"(?:do\s+you\s+want\s+to\s+file\s+(?:a\s+)?nil\s+return|nil\s+return)\s*[:\-?]?\s*(?:yes|y)\b|(?:filed|filing)\s+(?:as\s+)?(?:a\s+)?nil\s+return\b|(?:return|filing)\s+type\s*[:\-]?\s*nil\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var coreEvidence =
            HasRow(text, outwardMarker) &&
            HasRow(text, rcmMarker) &&
            HasRow(text, @"^\s*C\s*\.\s*Net\s+ITC\s+available");
        var supplementaryEvidence =
            Regex.IsMatch(tableText, @"5\s+Values\s+of\s+exempt", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(tableText, @"5\.1\s+Interest", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(tableText, @"6\.1\s+Payment\s+of\s+tax", RegexOptions.IgnoreCase) &&
            paymentRows.Count == 8;

        var notes = new List<string>();
        if (!coreEvidence)
        {
            notes.Add("Table 3.1 or Table 4(C) could not be read completely.");
        }
        if (!supplementaryEvidence)
        {
            notes.Add("Table 5, Table 5.1 or the complete Table 6.1 matrix requires review.");
        }
        if (liabilityBreakup.Count == 0)
        {
            notes.Add("Tax-liability breakup was not present or could not be read.");
        }
        if (notes.Count == 0)
        {
            notes.Add(
                "Identity, period, Tables 3.1, 3.1.1, 3.2, 4, 5, 5.1, 6.1 and liability breakup were retained.");
        }

        var record = new Gstr3bReturnRecord
        {
            ClientId = clientId,
            FinancialYear = financialYear,
            Gstin = gstin,
            LegalName = legalName,
            TradeName = tradeName,
            PeriodKey = periodInfo.EndKey,
            PeriodLabel = periodInfo.Label,
            FilingFrequency = periodInfo.Frequency,
            Arn = arn,
            ArnDate = arnDate,
            OutwardTaxable = Value(outward, 0),
            OutwardIgst = Value(outward, 1),
            OutwardCgst = Value(outward, 2),
            OutwardSgst = Value(outward, 3),
            OutwardCess = Value(outward, 4),
            RcmTaxable = Value(rcm, 0),
            RcmIgst = Value(rcm, 1),
            RcmCgst = Value(rcm, 2),
            RcmSgst = Value(rcm, 3),
            RcmCess = Value(rcm, 4),
            NetItcIgst = Value(netItc, 0),
            NetItcCgst = Value(netItc, 1),
            NetItcSgst = Value(netItc, 2),
            NetItcCess = Value(netItc, 3),
            CompositionExemptNilInterState = Value(composition, 0),
            CompositionExemptNilIntraState = Value(composition, 1),
            NonGstInterState = Value(nonGst, 0),
            NonGstIntraState = Value(nonGst, 1),
            SystemInterestIgst = Value(systemInterest, 0),
            SystemInterestCgst = Value(systemInterest, 1),
            SystemInterestSgst = Value(systemInterest, 2),
            SystemInterestCess = Value(systemInterest, 3),
            InterestPaidIgst = Value(interestPaid, 0),
            InterestPaidCgst = Value(interestPaid, 1),
            InterestPaidSgst = Value(interestPaid, 2),
            InterestPaidCess = Value(interestPaid, 3),
            LateFeeIgst = Value(lateFee, 0),
            LateFeeCgst = Value(lateFee, 1),
            LateFeeSgst = Value(lateFee, 2),
            LateFeeCess = Value(lateFee, 3),
            PaymentRows = paymentRows,
            LiabilityBreakup = liabilityBreakup,
            IsNilReturn = isNil,
            ParseValid = periodInfo.EndKey.Length == 6 && coreEvidence && supplementaryEvidence,
            ParseNotes = string.Join(" ", notes),
            SourcePath = path,
            RawText = text,
            ImportedUtc = DateTime.UtcNow
        };

        record.RawFields = BuildRawFields(
            record,
            zeroRated,
            nilExempt,
            nonGstOutward,
            ecommerceOperator,
            ecommerceSupplier,
            unregistered,
            compositionTaxablePersons,
            uinHolders,
            importGoods,
            importServices,
            inwardRcmItc,
            isd,
            allOtherItc,
            reversedRules,
            reversedOthers,
            otherDetails,
            itcReclaimed,
            ineligibleItc);
        record.RawFields["year"] = yearDisplay;
        record.RawFields["filing_frequency"] = record.FilingFrequency;
        record.RawFields["period_start_key"] = periodInfo.StartKey;
        record.RawFields["period_end_key"] = periodInfo.EndKey;
        record.RawFields["is_nil_return"] = record.IsNilReturn ? "YES" : "NO";
        record.RawFields["nil_detection_source"] = record.IsNilReturn ? "EXPLICIT TEXT" : string.Empty;
        record.RawFields["parse_valid"] = record.ParseValid ? "YES" : "NO";

        // Excel's GSTR3B store exposes a distinct nil-SALES flag.  A legally nil
        // return is strongest evidence; otherwise a valid return with no outward
        // liability is a nil-outward period for the Journal period gate.
        var noOutwardLiability =
            Math.Abs(record.OutwardTaxable) <= 0.005m &&
            Math.Abs(record.OutwardIgst) <= 0.005m &&
            Math.Abs(record.OutwardCgst) <= 0.005m &&
            Math.Abs(record.OutwardSgst) <= 0.005m &&
            Math.Abs(record.OutwardCess) <= 0.005m &&
            zeroRated.All(value => Math.Abs(value) <= 0.005m) &&
            nilExempt.All(value => Math.Abs(value) <= 0.005m) &&
            nonGstOutward.All(value => Math.Abs(value) <= 0.005m) &&
            ecommerceOperator.All(value => Math.Abs(value) <= 0.005m) &&
            ecommerceSupplier.All(value => Math.Abs(value) <= 0.005m);
        var nilSales = record.IsNilReturn || record.ParseValid && noOutwardLiability;
        record.RawFields["is_nil_sales_period"] = nilSales ? "YES" : "NO";
        record.RawFields["nil_sales_detection_source"] = record.IsNilReturn
            ? "EXPLICIT NIL RETURN"
            : nilSales ? "NO OUTWARD LIABILITY" : string.Empty;
        record.RawFields["nil_parser_version"] = "FIX44";
        return record;
    }

    private static string ReadPdfText(string path)
    {
        using var document = PdfDocument.Open(path);
        var pages = document
            .GetPages()
            .Select(page => ContentOrderTextExtractor.GetText(page))
            .Where(value => !string.IsNullOrWhiteSpace(value));
        return string.Join("\n", pages);
    }

    private static bool HasRow(string text, string marker) =>
        Regex.IsMatch(
            text,
            marker,
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant |
            RegexOptions.Multiline);

    private static decimal[] ExtractRowVector(
        string text,
        string rowMarker,
        string nextRowMarker,
        int expectedCount)
    {
        var row = Regex.Match(
            text,
            rowMarker,
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant |
            RegexOptions.Multiline);
        if (!row.Success)
        {
            return new decimal[expectedCount];
        }

        var remainder = text[(row.Index + row.Length)..];
        var next = Regex.Match(
            remainder,
            nextRowMarker,
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant |
            RegexOptions.Multiline);
        var segment = next.Success
            ? remainder[..next.Index]
            : remainder[..Math.Min(remainder.Length, 1000)];
        return ParseVector(segment, expectedCount);
    }

    private static decimal[] ExtractTokenVector(
        string normalizedText,
        string rowMarker,
        string nextRowMarker,
        int expectedCount)
    {
        var row = Regex.Match(
            normalizedText,
            rowMarker,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!row.Success)
        {
            return new decimal[expectedCount];
        }

        var remainder = normalizedText[(row.Index + row.Length)..];
        var next = Regex.Match(
            remainder,
            nextRowMarker,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var segment = next.Success
            ? remainder[..next.Index]
            : remainder[..Math.Min(remainder.Length, 1000)];
        return ParseVector(segment, expectedCount);
    }

    private static decimal[] ParseVector(string segment, int expectedCount)
    {
        var values = AmountOrDashRegex()
            .Matches(segment)
            .Select(match => match.Value == "-" ? 0m : ParseAmount(match.Value))
            .Take(expectedCount)
            .ToList();
        while (values.Count < expectedCount)
        {
            values.Add(0m);
        }
        return values.ToArray();
    }

    private static List<Gstr3bPaymentRow> ExtractPaymentRows(string normalizedText)
    {
        var payment = Segment(
            normalizedText,
            @"6\.1\s+Payment\s+of\s+tax",
            @"Breakup\s+of\s+tax\s+liability");
        if (payment.Length == 0)
        {
            return [];
        }

        var other = Segment(
            payment,
            @"\(\s*A\s*\)\s*Other\s+than\s+reverse\s+charge",
            @"\(\s*B\s*\)\s*Reverse\s+charge");
        var reverse = Segment(
            payment,
            @"\(\s*B\s*\)\s*Reverse\s+charge(?:\s+and\s+supplies\s+made\s+u/s\s+9\s*\(\s*5\s*\))?",
            @"$");

        var rows = new List<Gstr3bPaymentRow>();
        rows.AddRange(ParsePaymentSection(other, "Other than reverse charge"));
        rows.AddRange(ParsePaymentSection(reverse, "Reverse charge and supplies made u/s 9(5)"));
        return rows;
    }

    private static IEnumerable<Gstr3bPaymentRow> ParsePaymentSection(
        string section,
        string sectionName)
    {
        if (section.Length == 0)
        {
            return [];
        }

        // Current filed GST portal PDFs can place the word "tax" after the row values
        // (for example "Integrated 991.00 ... tax Central 202697.0 ...") instead of
        // emitting a contiguous "Integrated tax" row label. Parse by the stable tax-head
        // words and stop at the next head so both portal text layouts are accepted.
        var definitions = new[]
        {
            (TaxHead: "IGST", Marker: @"\bIntegrated\b", Next: @"\bCentral\b"),
            (TaxHead: "CGST", Marker: @"\bCentral\b", Next: @"\bState\s*/\s*UT\b"),
            (TaxHead: "SGST", Marker: @"\bState\s*/\s*UT\b", Next: @"\bCess\b"),
            (TaxHead: "CESS", Marker: @"\bCess\b", Next: @"$")
        };

        var result = new List<Gstr3bPaymentRow>();
        foreach (var definition in definitions)
        {
            var marker = Regex.Match(
                section,
                definition.Marker,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!marker.Success) continue;

            var remainder = section[(marker.Index + marker.Length)..];
            var nextIndex = remainder.Length;
            if (definition.Next != "$")
            {
                var nextMarker = Regex.Match(
                    remainder,
                    definition.Next,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (nextMarker.Success)
                {
                    nextIndex = nextMarker.Index;
                }
            }
            var rowText = remainder[..nextIndex];
            var values = ParsePortalPaymentVector(rowText, 10);

            result.Add(new Gstr3bPaymentRow
            {
                Section = sectionName,
                TaxHead = definition.TaxHead,
                TaxPayable = Value(values, 0),
                NegativeLiabilityAdjustment = Value(values, 1),
                NetTaxPayable = Value(values, 2),
                ItcIntegrated = Value(values, 3),
                ItcCentral = Value(values, 4),
                ItcState = Value(values, 5),
                ItcCess = Value(values, 6),
                PaidCash = Value(values, 7),
                InterestPaidCash = Value(values, 8),
                LateFeePaidCash = Value(values, 9)
            });
        }
        return result;
    }

    private static decimal[] ParsePortalPaymentVector(string segment, int expectedCount)
    {
        var tokens = AmountOrDashRegex()
            .Matches(segment)
            .Select(match => match.Value)
            .ToList();

        // Some official filed PDFs split the final decimal digit of wide values onto
        // the visual line containing the row label. Content-order extraction then looks
        // like: "202697.0 ... 125.00 tax 0 0 0". The trailing single digits belong to
        // the earlier one-decimal values. Reattach them before converting the vector.
        if (tokens.Count > expectedCount)
        {
            var primary = tokens.Take(expectedCount).ToList();
            var extras = tokens.Skip(expectedCount).ToList();
            var oneDecimalIndexes = primary
                .Select((token, index) => (token, index))
                .Where(item => Regex.IsMatch(item.token, @"^\(?-?\d[\d,]*\.\d\)?$", RegexOptions.CultureInvariant))
                .Select(item => item.index)
                .ToArray();

            var repairCount = Math.Min(
                oneDecimalIndexes.Length,
                extras.TakeWhile(value => Regex.IsMatch(value, @"^\d$", RegexOptions.CultureInvariant)).Count());
            for (var index = 0; index < repairCount; index++)
            {
                var primaryIndex = oneDecimalIndexes[index];
                var token = primary[primaryIndex];
                if (token.EndsWith(")", StringComparison.Ordinal))
                    primary[primaryIndex] = token[..^1] + extras[index] + ")";
                else
                    primary[primaryIndex] = token + extras[index];
            }
            tokens = primary;
        }

        var values = tokens
            .Take(expectedCount)
            .Select(token => token == "-" ? 0m : ParseAmount(token))
            .ToList();
        while (values.Count < expectedCount) values.Add(0m);
        return values.ToArray();
    }

    private static List<Gstr3bLiabilityBreakupRow> ExtractLiabilityBreakup(string normalizedText)
    {
        var section = Segment(
            normalizedText,
            @"Breakup\s+of\s+tax\s+liability\s+declared(?:\s*\(\s*for\s+interest\s+computation\s*\))?",
            @"Verification\s*:");
        if (section.Length == 0)
        {
            return [];
        }

        var result = new List<Gstr3bLiabilityBreakupRow>();
        var matches = Regex.Matches(
            section,
            @"(?<period>Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+(?<year>20\d{2})\s+(?<values>(?:\(?-?\d[\d,]*(?:\.\d+)?\)?|-)(?:\s+(?:\(?-?\d[\d,]*(?:\.\d+)?\)?|-)){3})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (Match match in matches)
        {
            var values = ParseVector(match.Groups["values"].Value, 4);
            var month = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                match.Groups["period"].Value.ToLowerInvariant());
            result.Add(new Gstr3bLiabilityBreakupRow
            {
                Period = $"{month} {match.Groups["year"].Value}",
                Igst = Value(values, 0),
                Cgst = Value(values, 1),
                Sgst = Value(values, 2),
                Cess = Value(values, 3)
            });
        }
        return result;
    }

    private static Dictionary<string, string> BuildRawFields(
        Gstr3bReturnRecord record,
        decimal[] zeroRated,
        decimal[] nilExempt,
        decimal[] nonGstOutward,
        decimal[] ecommerceOperator,
        decimal[] ecommerceSupplier,
        decimal[] unregistered,
        decimal[] compositionTaxablePersons,
        decimal[] uinHolders,
        decimal[] importGoods,
        decimal[] importServices,
        decimal[] inwardRcmItc,
        decimal[] isd,
        decimal[] allOtherItc,
        decimal[] reversedRules,
        decimal[] reversedOthers,
        decimal[] otherDetails,
        decimal[] itcReclaimed,
        decimal[] ineligibleItc)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["file_name"] = record.SourceFileName,
            ["file_path"] = record.SourcePath,
            ["gstin"] = record.Gstin,
            ["legal_name"] = record.LegalName,
            ["trade_name"] = record.TradeName,
            ["year"] = record.FinancialYear,
            ["period"] = record.PeriodLabel,
            ["month_key"] = record.PeriodKey,
            ["arn"] = record.Arn,
            ["arn_date"] = record.ArnDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty
        };

        AddTaxableVector(fields, "table_3_1.outward_taxable", [
            record.OutwardTaxable, record.OutwardIgst, record.OutwardCgst, record.OutwardSgst, record.OutwardCess]);
        AddTaxableVector(fields, "table_3_1.zero_rated", zeroRated);
        AddTaxableVector(fields, "table_3_1.nil_exempted", nilExempt);
        AddTaxableVector(fields, "table_3_1.inward_rcm", [
            record.RcmTaxable, record.RcmIgst, record.RcmCgst, record.RcmSgst, record.RcmCess]);
        AddTaxableVector(fields, "table_3_1.non_gst_outward", nonGstOutward);
        AddTaxableVector(fields, "table_3_1_1.ecommerce_operator_9_5", ecommerceOperator);
        AddTaxableVector(fields, "table_3_1_1.registered_supplier_9_5", ecommerceSupplier);
        AddTwoTaxVector(fields, "table_3_2.unregistered_persons", unregistered);
        AddTwoTaxVector(fields, "table_3_2.composition_taxable_persons", compositionTaxablePersons);
        AddTwoTaxVector(fields, "table_3_2.uin_holders", uinHolders);

        AddFourTaxVector(fields, "table_4_itc.import_goods", importGoods);
        AddFourTaxVector(fields, "table_4_itc.import_services", importServices);
        AddFourTaxVector(fields, "table_4_itc.inward_rcm", inwardRcmItc);
        AddFourTaxVector(fields, "table_4_itc.isd", isd);
        AddFourTaxVector(fields, "table_4_itc.all_other_itc", allOtherItc);
        AddFourTaxVector(fields, "table_4_itc.reversed_rules_38_42_43_17_5", reversedRules);
        AddFourTaxVector(fields, "table_4_itc.reversed_others", reversedOthers);
        AddFourTaxVector(fields, "table_4_itc.net_itc", [
            record.NetItcIgst, record.NetItcCgst, record.NetItcSgst, record.NetItcCess]);
        AddFourTaxVector(fields, "table_4_itc.other_details", otherDetails);
        AddFourTaxVector(fields, "table_4_itc.itc_reclaimed", itcReclaimed);
        AddFourTaxVector(fields, "table_4_itc.ineligible_itc", ineligibleItc);

        fields["table_5.composition_exempt_nil.inter_state"] = Raw(record.CompositionExemptNilInterState);
        fields["table_5.composition_exempt_nil.intra_state"] = Raw(record.CompositionExemptNilIntraState);
        fields["table_5.non_gst_supply.inter_state"] = Raw(record.NonGstInterState);
        fields["table_5.non_gst_supply.intra_state"] = Raw(record.NonGstIntraState);
        AddFourTaxVector(fields, "table_5_1.system_computed_interest", [
            record.SystemInterestIgst, record.SystemInterestCgst, record.SystemInterestSgst, record.SystemInterestCess]);
        AddFourTaxVector(fields, "table_5_1.interest_paid", [
            record.InterestPaidIgst, record.InterestPaidCgst, record.InterestPaidSgst, record.InterestPaidCess]);
        AddFourTaxVector(fields, "table_5_1.late_fee", [
            record.LateFeeIgst, record.LateFeeCgst, record.LateFeeSgst, record.LateFeeCess]);

        foreach (var row in record.PaymentRows)
        {
            var section = row.Section.Contains("Other", StringComparison.OrdinalIgnoreCase)
                ? "other_than_reverse_charge"
                : "reverse_charge_9_5";
            var head = row.TaxHead.ToUpperInvariant() switch
            {
                "IGST" => "integrated_tax",
                "CGST" => "central_tax",
                "SGST" => "state_ut_tax",
                _ => "cess"
            };
            var prefix = $"table_6_1.{section}.{head}";
            fields[$"{prefix}.tax_payable"] = Raw(row.TaxPayable);
            fields[$"{prefix}.adjustment_negative_liability"] = Raw(row.NegativeLiabilityAdjustment);
            fields[$"{prefix}.net_tax_payable"] = Raw(row.NetTaxPayable);
            fields[$"{prefix}.itc_igst"] = Raw(row.ItcIntegrated);
            fields[$"{prefix}.itc_cgst"] = Raw(row.ItcCentral);
            fields[$"{prefix}.itc_sgst"] = Raw(row.ItcState);
            fields[$"{prefix}.itc_cess"] = Raw(row.ItcCess);
            fields[$"{prefix}.itc_total"] = Raw(row.ItcTotal);
            fields[$"{prefix}.paid_cash"] = Raw(row.PaidCash);
            fields[$"{prefix}.interest_paid_cash"] = Raw(row.InterestPaidCash);
            fields[$"{prefix}.late_fee_paid_cash"] = Raw(row.LateFeePaidCash);
            fields[$"{prefix}.cash_total"] = Raw(row.PaidCash + row.InterestPaidCash + row.LateFeePaidCash);
            fields[$"{prefix}.offset_total"] = Raw(row.OffsetTotal);
        }

        for (var index = 0; index < record.LiabilityBreakup.Count; index++)
        {
            var liability = record.LiabilityBreakup[index];
            var prefix = $"liability_breakup[{index}]";
            fields[$"{prefix}.period"] = liability.Period;
            fields[$"{prefix}.igst"] = Raw(liability.Igst);
            fields[$"{prefix}.cgst"] = Raw(liability.Cgst);
            fields[$"{prefix}.sgst"] = Raw(liability.Sgst);
            fields[$"{prefix}.cess"] = Raw(liability.Cess);
        }
        fields["raw_text_length"] = record.RawText.Length.ToString(CultureInfo.InvariantCulture);
        return fields;
    }

    private static void AddTaxableVector(
        IDictionary<string, string> fields,
        string prefix,
        IReadOnlyList<decimal> values)
    {
        fields[$"{prefix}.taxable_value"] = Raw(Value(values, 0));
        fields[$"{prefix}.igst"] = Raw(Value(values, 1));
        fields[$"{prefix}.cgst"] = Raw(Value(values, 2));
        fields[$"{prefix}.sgst"] = Raw(Value(values, 3));
        fields[$"{prefix}.cess"] = Raw(Value(values, 4));
    }

    private static void AddTwoTaxVector(
        IDictionary<string, string> fields,
        string prefix,
        IReadOnlyList<decimal> values)
    {
        fields[$"{prefix}.taxable_value"] = Raw(Value(values, 0));
        fields[$"{prefix}.igst"] = Raw(Value(values, 1));
    }

    private static void AddFourTaxVector(
        IDictionary<string, string> fields,
        string prefix,
        IReadOnlyList<decimal> values)
    {
        fields[$"{prefix}.igst"] = Raw(Value(values, 0));
        fields[$"{prefix}.cgst"] = Raw(Value(values, 1));
        fields[$"{prefix}.sgst"] = Raw(Value(values, 2));
        fields[$"{prefix}.cess"] = Raw(Value(values, 3));
    }

    private static string Raw(decimal value) =>
        value.ToString("0.00########", CultureInfo.InvariantCulture);

    private static string Segment(string text, string startMarker, string endMarker)
    {
        var start = Regex.Match(
            text,
            startMarker,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!start.Success)
        {
            return string.Empty;
        }
        var remainder = text[(start.Index + start.Length)..];
        if (endMarker == "$")
        {
            return remainder;
        }
        var end = Regex.Match(
            remainder,
            endMarker,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return end.Success ? remainder[..end.Index] : remainder;
    }

    private static string NormalizeTableText(string text) =>
        Regex.Replace(
            text.Replace('\r', ' ').Replace('\n', ' ').Replace('\f', ' '),
            @"\s+",
            " ",
            RegexOptions.CultureInvariant).Trim();

    private static (string Label, string Frequency, string StartKey, string EndKey)
        ExtractPeriod(string text, string financialYear, string sourcePath)
    {
        // Identity-section period ownership only. Never search the entire GSTR-3B body
        // for an arbitrary six-digit YYYYMM token: tax values such as 202709.00 are
        // legitimate amounts and previously could be misread as Sep-2027.
        var identityText = text.Length <= 6000 ? text : text[..6000];
        var yearText = FirstMatchGroup(
            identityText,
            [
                @"^\s*Year\s*[:\-]?\s*(20\d{2}\s*[-/]\s*\d{2,4})\s*$",
                @"\bYear\s*[:\-]?\s*(20\d{2}\s*[-/]\s*\d{2,4})\b"
            ]);
        var fy = ParseFinancialYear(
            string.IsNullOrWhiteSpace(financialYear) ? yearText : financialYear);

        var explicitPeriod = Regex.Match(
            identityText,
            @"(?:^|\n)\s*(?:Tax\s+Period|Return\s+Period|Period)\s*[:\-]?\s*(?<month>Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)(?:\s+(?<year>20\d{2}))?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);
        if (explicitPeriod.Success)
        {
            var month = MonthNumber(explicitPeriod.Groups["month"].Value);
            var explicitYear = 0;
            if (explicitPeriod.Groups["year"].Success)
                int.TryParse(explicitPeriod.Groups["year"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out explicitYear);
            var year = explicitYear > 0
                ? explicitYear
                : month >= 4 ? fy.StartYear : fy.EndYear;
            if (IsPeriodInsideFinancialYear(year, month, fy))
                return BuildMonthlyPeriod(year.ToString(CultureInfo.InvariantCulture), month.ToString("00", CultureInfo.InvariantCulture));
        }

        // Some PDF text extractors keep the label and value on separate lines.
        // Inspect only the identity block and require the month to occur immediately
        // after a Period label; do not accept month names from later liability tables.
        var periodText = FirstMatchGroup(
            identityText,
            [
                @"^\s*Tax\s+Period\s*[:\-]?\s*(.+?)\s*$",
                @"^\s*Return\s+Period\s*[:\-]?\s*(.+?)\s*$",
                @"^\s*Period\s*[:\-]?\s*(.+?)\s*$"
            ]);
        periodText = Regex.Replace(periodText, @"[^A-Za-z0-9\-/ ]", " ");
        var months = Regex.Matches(
                periodText,
                @"Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => MonthNumber(match.Value))
            .Where(month => month > 0)
            .ToArray();
        var years = Regex.Matches(periodText, @"\b20\d{2}\b")
            .Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture))
            .ToArray();

        if (months.Length > 0)
        {
            var startMonth = months.First();
            var endMonth = months.Last();
            var startYear = years.FirstOrDefault();
            var endYear = years.LastOrDefault();
            if (startYear == 0) startYear = startMonth >= 4 ? fy.StartYear : fy.EndYear;
            if (endYear == 0) endYear = endMonth >= 4 ? fy.StartYear : fy.EndYear;

            if (IsPeriodInsideFinancialYear(startYear, startMonth, fy) &&
                IsPeriodInsideFinancialYear(endYear, endMonth, fy))
            {
                var startKey = $"{startYear:0000}{startMonth:00}";
                var endKey = $"{endYear:0000}{endMonth:00}";
                var frequency = months.Length > 1 || startMonth != endMonth ? "Quarterly" : "Monthly";
                var label = frequency == "Quarterly"
                    ? $"{new DateTime(startYear, startMonth, 1):MMM-yy} to {new DateTime(endYear, endMonth, 1):MMM-yy}"
                    : $"{new DateTime(endYear, endMonth, 1):MMM-yyyy}";
                return (label, frequency, startKey, endKey);
            }
        }

        // Filename is the final trusted fallback. Portal files commonly end in
        // _MMYYYY (for example _112025). It is much safer than scanning the PDF body.
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        var fileMonthYear = Regex.Match(
            fileName,
            @"(?:^|[_\-\s])(0[1-9]|1[0-2])(20\d{2})(?:$|[_\-\s])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (fileMonthYear.Success)
        {
            var month = int.Parse(fileMonthYear.Groups[1].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(fileMonthYear.Groups[2].Value, CultureInfo.InvariantCulture);
            if (IsPeriodInsideFinancialYear(year, month, fy))
                return BuildMonthlyPeriod(fileMonthYear.Groups[2].Value, fileMonthYear.Groups[1].Value);
        }

        var fileYearMonth = Regex.Match(
            fileName,
            @"(?:^|[_\-\s])(20\d{2})(0[1-9]|1[0-2])(?:$|[_\-\s])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (fileYearMonth.Success)
        {
            var year = int.Parse(fileYearMonth.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(fileYearMonth.Groups[2].Value, CultureInfo.InvariantCulture);
            if (IsPeriodInsideFinancialYear(year, month, fy))
                return BuildMonthlyPeriod(fileYearMonth.Groups[1].Value, fileYearMonth.Groups[2].Value);
        }

        return (periodText, "Monthly", string.Empty, string.Empty);
    }

    private static bool IsPeriodInsideFinancialYear(
        int year,
        int month,
        (int StartYear, int EndYear) financialYear)
    {
        if (month is < 1 or > 12) return false;
        return month >= 4
            ? year == financialYear.StartYear
            : year == financialYear.EndYear;
    }

    private static (string Label, string Frequency, string StartKey, string EndKey)
        BuildMonthlyPeriod(string yearText, string monthText)
    {
        var year = int.Parse(yearText, CultureInfo.InvariantCulture);
        var month = int.Parse(monthText, CultureInfo.InvariantCulture);
        var key = $"{year:0000}{month:00}";
        return (new DateTime(year, month, 1).ToString("MMM-yyyy"), "Monthly", key, key);
    }

    private static (int StartYear, int EndYear) ParseFinancialYear(string value)
    {
        var match = Regex.Match(value, @"(20\d{2}).*?(\d{2,4})");
        if (!match.Success)
        {
            return (DateTime.Today.Year, DateTime.Today.Year + 1);
        }
        var start = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var rawEnd = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return (start, rawEnd < 100 ? 2000 + rawEnd : rawEnd);
    }

    private static string FirstMatchGroup(string text, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(
                text,
                pattern,
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant |
                RegexOptions.Multiline);
            if (match.Success && match.Groups.Count > 1)
            {
                var value = Normalize(match.Groups[1].Value);
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }
        return string.Empty;
    }

    private static string Match(string text, string pattern)
    {
        var match = Regex.Match(
            text,
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? Normalize(match.Value) : string.Empty;
    }

    private static string CleanName(string value) =>
        Regex.Replace(
            value,
            @"\s+(?:GSTIN|Trade Name|Legal Name|ARN|Tax Period).*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        .Trim(' ', ':', '-', '.');

    private static string Normalize(string value) =>
        string.Join(
            " ",
            value.Replace('\r', ' ').Replace('\n', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static decimal ParseAmount(string value)
    {
        var cleaned = value.Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        var parenthesized = cleaned.StartsWith("(", StringComparison.Ordinal) &&
                            cleaned.EndsWith(")", StringComparison.Ordinal);
        cleaned = cleaned.Trim('(', ')');
        if (!decimal.TryParse(
                cleaned,
                NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            return 0m;
        }
        return parenthesized ? -amount : amount;
    }

    private static DateTime? ParseDate(string value)
    {
        string[] formats =
        [
            "dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd",
            "dd/MM/yy", "dd-MM-yy", "d/M/yyyy", "d-M-yyyy"
        ];
        if (DateTime.TryParseExact(
                value.Trim(),
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var exact))
        {
            return exact.Date;
        }
        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed) ? parsed.Date : null;
    }

    private static int MonthNumber(string value) =>
        value[..3].ToUpperInvariant() switch
        {
            "JAN" => 1,
            "FEB" => 2,
            "MAR" => 3,
            "APR" => 4,
            "MAY" => 5,
            "JUN" => 6,
            "JUL" => 7,
            "AUG" => 8,
            "SEP" => 9,
            "OCT" => 10,
            "NOV" => 11,
            "DEC" => 12,
            _ => 0
        };

    private static decimal Value(IReadOnlyList<decimal> values, int index) =>
        index >= 0 && index < values.Count ? values[index] : 0m;

    [GeneratedRegex(@"(?<!\w)(?:\(?-?\d[\d,]*(?:\.\d+)?\)?|-)(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex AmountOrDashRegex();
}
