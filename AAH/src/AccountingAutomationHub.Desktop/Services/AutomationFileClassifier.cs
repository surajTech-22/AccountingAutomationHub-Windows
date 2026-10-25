using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Infrastructure.Spreadsheets;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Imports;

namespace AccountingAutomationHub.Desktop.Services;

internal enum AutomationFileKind
{
    BankPdf,
    BankOcrSource,
    Gstr3bPdf,
    Form26AsPdf,
    BankSpreadsheet,
    BankSuspenseMapping,
    AccountingWorkbook,
    Sales,
    Purchase,
    Cash,
    Journal,
    Mt940,
    Camt053,
    Ofx,
    Qif,
    Unknown,
    Unsupported
}

internal sealed record AutomationFileClassification(
    AutomationFileKind Kind,
    int Confidence,
    string Reason,
    int BankScore = 0,
    int SalesScore = 0,
    int PurchaseScore = 0,
    int CashScore = 0,
    int JournalScore = 0);

/// <summary>
/// Content-first classifier for Automation Folder sources. File extension identifies
/// only the container. Accounting type is inferred from the actual headers/content.
/// Ambiguous inputs are never guessed as Bank.
/// </summary>
internal sealed class AutomationFileClassifier
{
    private readonly FlexibleWorkbookReader _reader = new();

    public AutomationFileClassification Classify(string path, string password = "")
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".pdf")
        {
            // A scanned statement must never enter the normal digital-PDF/Python pipeline.
            // Inspect physical pages first, not only extracted text or filename. This
            // catches image-only HDFC statements and mixed scanned/digital documents,
            // including scanners that add a misleading searchable text overlay.
            var pageKind = BankPdfDocumentClassifier.Classify(path);
            // Specialized tax-return formats retain their own import authority
            // if native content unambiguously identifies them. Generic Bank PDF
            // parsing MUST remain downstream of the physical scan detector.
            var pdf = UniversalPdfContentInspector.Inspect(path);
            if (pdf.Kind.Equals(UniversalPdfContentInspector.Gstr3b, StringComparison.OrdinalIgnoreCase))
                return new(AutomationFileKind.Gstr3bPdf, pdf.Confidence, pdf.Reason);
            if (pdf.Kind.Equals(UniversalPdfContentInspector.Form26As, StringComparison.OrdinalIgnoreCase))
                return new(AutomationFileKind.Form26AsPdf, pdf.Confidence, pdf.Reason);
            if (pageKind is BankPdfDocumentKind.Scanned or BankPdfDocumentKind.Mixed)
                return new(
                    AutomationFileKind.BankOcrSource,
                    85,
                    $"Physical PDF classification: {pageKind}. Route directly to the existing Bank PaddleOCR-VL and AAH verified OCR workbook flow; never run digital PDF extraction.",
                    BankScore: 8);

            if (pdf.Kind.Equals(UniversalPdfContentInspector.BankStatement, StringComparison.OrdinalIgnoreCase))
                return new(AutomationFileKind.BankPdf, pdf.Confidence, pdf.Reason, BankScore: Math.Max(6, pdf.Confidence / 10));

            // R32: a password-protected statement cannot expose embedded text to the
            // lightweight PdfPig classifier before the banking PDF pipeline unlocks it.
            // Do not mislabel a clearly bank-statement-named encrypted source as generic
            // Unknown Format merely because header inspection happened before decryption.
            // This only applies to strong bank statement filename patterns; ordinary
            // encrypted PDFs remain Unknown instead of being guessed into Banking.
            if (pdf.TextSample.Length == 0 &&
                IsPasswordOrEncryptionInspectionFailure(pdf.Reason) &&
                HasStrongBankStatementFileName(path))
            {
                return new(
                    AutomationFileKind.BankPdf,
                    70,
                    "Password-protected bank statement filename detected. Banking will unlock the PDF before transaction/header parsing.",
                    BankScore: 7);
            }

            if (pdf.TextSample.Length == 0 &&
                pdf.Reason.Contains("no readable embedded text", StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    AutomationFileKind.BankOcrSource,
                    60,
                    "The PDF has no embedded text. PaddleOCR-VL 1.6 production OCR will process it locally, verify the statement, convert it to normal Bank Statement Excel, and pass that workbook through the existing importer.",
                    BankScore: 6);
            }

            return new(AutomationFileKind.Unknown, pdf.Confidence, pdf.Reason);
        }

        if (extension is ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp" or ".webp")
        {
            return new(
                AutomationFileKind.BankOcrSource,
                60,
                "Image source will be processed locally through PaddleOCR-VL 1.6 production OCR, statement verification, and the normal Bank Statement Excel importer.",
                BankScore: 6);
        }

        if (extension is ".mt940" or ".940" or ".sta")
            return LooksLikeMt940(path)
                ? new(AutomationFileKind.Mt940, 100, "SWIFT MT940 statement tags detected.")
                : new(AutomationFileKind.Unknown, 0, "The file extension suggests MT940 but required statement tags were not detected.");

        if (extension is ".ofx" or ".qbo")
            return new(AutomationFileKind.Ofx, 100, "OFX/MS Money-compatible financial exchange container detected.");
        if (extension is ".qif")
            return new(AutomationFileKind.Qif, 100, "QIF financial exchange container detected.");

        if (extension == ".xml")
        {
            var text = SafeReadText(path, 1_000_000);
            if (text.Contains("BkToCstmrStmt", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("camt.053", StringComparison.OrdinalIgnoreCase))
                return new(AutomationFileKind.Camt053, 100, "ISO 20022 camt.053 bank statement detected.");
            if (text.Contains("<ID>Vouchers</ID>", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("<VOUCHERTYPENAME>Sales</VOUCHERTYPENAME>", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("<VOUCHERTYPENAME>Credit Note</VOUCHERTYPENAME>", StringComparison.OrdinalIgnoreCase)))
                return new(AutomationFileKind.Sales, 100, "Tally Vouchers XML containing Sales/Credit Note vouchers detected; AAH will use the dedicated Tally voucher parser instead of spreadsheet header mapping.", SalesScore: 10);
            return new(AutomationFileKind.Unknown, 0, "XML source is not a recognized camt.053 statement or supported Tally Sales voucher import.");
        }

        if (extension is ".json" or ".zip")
            return ClassifyPurchaseContainer(path, extension);

        if (extension is ".txt" or ".tsv")
        {
            if (LooksLikeMt940(path))
                return new(AutomationFileKind.Mt940, 100, "MT940 tags detected inside a text statement.");
            return ClassifyWorkbookLike(path, password);
        }

        if (extension is ".csv" or ".xlsx" or ".xlsm" or ".xls")
            return ClassifyWorkbookLike(path, password);

        return new(AutomationFileKind.Unsupported, 0, $"The '{extension}' source container is not enabled for automatic accounting import.");
    }

    /// <summary>
    /// Returns a bounded content sample used only for strict per-client Auto Import identity checks.
    /// It does not classify the accounting module. The caller may look only for an already-known
    /// Client GSTIN or bank account number before allowing an unattended import.
    /// </summary>
    public string GetIdentitySearchText(string path, string password = "")
    {
        string? temporaryWorkbook = null;
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".pdf")
                return UniversalPdfContentInspector.Inspect(path).TextSample ?? string.Empty;
            if (extension == ".zip")
            {
                using var zipStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);
                var sb = new StringBuilder();
                foreach (var entry in archive.Entries.Take(50))
                {
                    sb.Append(' ').Append(entry.FullName);
                    if (entry.Length <= 0 || entry.Length > 1_000_000) continue;
                    var ext = Path.GetExtension(entry.FullName).ToLowerInvariant();
                    if (ext is not (".json" or ".txt" or ".csv" or ".xml")) continue;
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true, 4096, leaveOpen: false);
                    var buffer = new char[Math.Min(250_000, (int)Math.Max(0, entry.Length))];
                    var read = reader.ReadBlock(buffer, 0, buffer.Length);
                    if (read > 0) sb.Append(' ').Append(buffer, 0, read);
                    if (sb.Length >= 750_000) break;
                }
                return sb.ToString();
            }
            if (extension is ".json" or ".txt" or ".tsv" or ".csv" or ".xml" or ".mt940" or ".940" or ".sta" or ".ofx" or ".qbo" or ".qif")
                return SafeReadText(path, 1_000_000);
            if (extension is ".xlsx" or ".xlsm" or ".xls")
            {
                var readPath = path;
                var inspection = SpreadsheetSourceInspector.Inspect(path);
                if (inspection.Kind is SpreadsheetSourceKind.LegacyOleWorkbook or SpreadsheetSourceKind.XmlSpreadsheet or SpreadsheetSourceKind.UnknownBinary)
                {
                    temporaryWorkbook = ConvertLegacyXlsForInspection(path, password);
                    readPath = temporaryWorkbook;
                }
                var workbook = _reader.Read(readPath, CancellationToken.None);
                var values = workbook.Worksheets.Values
                    .SelectMany(sheet => sheet.Rows.OrderBy(pair => pair.Key).Take(250).SelectMany(pair => pair.Value.Values).Concat(new[] { sheet.Name }))
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Take(20_000);
                return string.Join(' ', values);
            }
            return Path.GetFileName(path);
        }
        catch
        {
            return Path.GetFileName(path);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryWorkbook))
            {
                try { File.Delete(temporaryWorkbook); } catch { }
            }
        }
    }

    public string GetLearningSignature(string path)
    {
        string? temporaryWorkbook = null;
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var readPath = path;
            if (extension is ".xls" or ".xlsx" or ".xlsm")
            {
                var inspection = SpreadsheetSourceInspector.Inspect(path);
                if (inspection.Kind is SpreadsheetSourceKind.LegacyOleWorkbook or
                    SpreadsheetSourceKind.XmlSpreadsheet or
                    SpreadsheetSourceKind.UnknownBinary)
                {
                    temporaryWorkbook = ConvertLegacyXlsForInspection(path);
                    readPath = temporaryWorkbook;
                }
            }
            if (extension is not (".csv" or ".txt" or ".tsv" or ".xlsx" or ".xlsm" or ".xls"))
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(extension)));

            var workbook = _reader.Read(readPath, CancellationToken.None);
            var signatures = new List<string>();
            foreach (var sheet in workbook.Worksheets.Values)
            {
                var best = sheet.Rows.OrderBy(pair => pair.Key).Take(35)
                    .Select(pair => pair.Value.OrderBy(cell => cell.Key).Select(cell => Normalize(cell.Value)).Where(value => value.Length > 0).ToArray())
                    .Select(values => (Values: values, Score: values.Sum(HeaderKeywordScore)))
                    .OrderByDescending(item => item.Score)
                    .ThenByDescending(item => item.Values.Length)
                    .FirstOrDefault();
                if (best.Values is { Length: > 0 })
                    signatures.Add($"{Normalize(sheet.Name)}|{string.Join('|', best.Values)}");
            }
            var canonical = $"{extension}|{string.Join("||", signatures.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
        catch
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetExtension(path).ToLowerInvariant())));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryWorkbook))
            {
                try { File.Delete(temporaryWorkbook); } catch { }
            }
        }
    }

    private static int HeaderKeywordScore(string value)
    {
        var keywords = new[]
        {
            "DATE", "NARRATION", "PARTICULAR", "DESCRIPTION", "DEBIT", "CREDIT", "BALANCE",
            "INVOICE", "DOCUMENT", "GSTIN", "TAXABLE", "IGST", "CGST", "SGST", "ITEM", "HSN",
            "SUPPLIER", "CUSTOMER", "VENDOR", "QUANTITY", "UNIT", "RATE", "AMOUNT"
        };
        return keywords.Count(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    public AutomationFileClassification ClassifyWorkbookData(OpenXmlWorkbookData workbook, string fileName)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        if (IsLinkedAccountingWorkbook(workbook))
        {
            return new(
                AutomationFileKind.AccountingWorkbook,
                100,
                "AAH linked multi-module accounting workbook detected (Bank Statement + Sales template authority).",
                BankScore: 10, SalesScore: 10, PurchaseScore: 10, JournalScore: 10);
        }
        if (GeneratedAccountingWorkbookIdentity.IsJournalWorkbook(workbook))
        {
            return new(
                AutomationFileKind.Journal,
                100,
                "AAH Journal Import workbook metadata detected.",
                JournalScore: 10);
        }
        if (GeneratedAccountingWorkbookIdentity.IsCashWorkbook(workbook))
        {
            return new(
                AutomationFileKind.Cash,
                100,
                "AAH Cash Entry Import workbook metadata detected.",
                CashScore: 10);
        }
        if (BankSuspenseWorkbookService.ReadWorkbookMeta(
                workbook,
                BankSuspenseWorkbookService.MetaSheetName,
                "Kind")
            .Equals(BankSuspenseWorkbookService.WorkbookKind, StringComparison.OrdinalIgnoreCase))
        {
            return new(
                AutomationFileKind.BankSuspenseMapping,
                100,
                "AAH Bank Suspense Mapping workbook metadata detected.",
                BankScore: 10);
        }

        var values = workbook.Worksheets.Values
            .SelectMany(sheet => sheet.Rows.OrderBy(pair => pair.Key).Take(35)
                .SelectMany(pair => pair.Value.Values)
                .Concat(new[] { sheet.Name }))
            .Select(Normalize)
            .Where(value => value.Length > 0)
            .ToArray();
        return PreferMappedAccountingType(workbook, fileName, ScoreAccountingType(values, fileName));
    }

    public string GetIdentitySearchText(OpenXmlWorkbookData workbook)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        var values = workbook.Worksheets.Values
            .SelectMany(sheet => sheet.Rows.OrderBy(pair => pair.Key).Take(250)
                .SelectMany(pair => pair.Value.Values)
                .Concat(new[] { sheet.Name }))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(20_000);
        return string.Join(' ', values);
    }

    private AutomationFileClassification ClassifyWorkbookLike(string path, string password)
    {
        string? temporaryWorkbook = null;
        try
        {
            var readPath = path;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".xls" or ".xlsx" or ".xlsm")
            {
                var inspection = SpreadsheetSourceInspector.Inspect(path);
                if (inspection.Kind is SpreadsheetSourceKind.LegacyOleWorkbook or
                    SpreadsheetSourceKind.XmlSpreadsheet or
                    SpreadsheetSourceKind.UnknownBinary)
                {
                    temporaryWorkbook = ConvertLegacyXlsForInspection(path, password);
                    readPath = temporaryWorkbook;
                }
            }

            var workbook = _reader.Read(readPath, CancellationToken.None);
            if (IsLinkedAccountingWorkbook(workbook))
            {
                return new(
                    AutomationFileKind.AccountingWorkbook,
                    100,
                    "AAH linked multi-module accounting workbook detected (Bank Statement + Sales template authority).",
                    BankScore: 10, SalesScore: 10, PurchaseScore: 10, JournalScore: 10);
            }
            if (GeneratedAccountingWorkbookIdentity.IsJournalWorkbook(workbook))
            {
                return new(
                    AutomationFileKind.Journal,
                    100,
                    "AAH Journal Import workbook metadata detected.",
                    JournalScore: 10);
            }
            if (GeneratedAccountingWorkbookIdentity.IsCashWorkbook(workbook))
            {
                return new(
                    AutomationFileKind.Cash,
                    100,
                    "AAH Cash Entry Import workbook metadata detected.",
                    CashScore: 10);
            }
            if (BankSuspenseWorkbookService.ReadWorkbookMeta(
                    workbook,
                    BankSuspenseWorkbookService.MetaSheetName,
                    "Kind")
                .Equals(BankSuspenseWorkbookService.WorkbookKind, StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    AutomationFileKind.BankSuspenseMapping,
                    100,
                    "AAH Bank Suspense Mapping workbook metadata detected.",
                    BankScore: 10);
            }

            var values = workbook.Worksheets.Values
                .SelectMany(sheet => sheet.Rows.OrderBy(pair => pair.Key).Take(35)
                    .SelectMany(pair => pair.Value.Values)
                    .Concat(new[] { sheet.Name }))
                .Select(Normalize)
                .Where(value => value.Length > 0)
                .ToArray();

            return PreferMappedAccountingType(workbook, Path.GetFileName(path), ScoreAccountingType(values, Path.GetFileName(path)), path);
        }
        catch (Exception exception)
        {
            return new(AutomationFileKind.Unknown, 0, $"The source could not be inspected safely: {exception.Message}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryWorkbook))
            {
                try { File.Delete(temporaryWorkbook); } catch { }
            }
        }
    }

    internal static bool IsLinkedAccountingWorkbook(OpenXmlWorkbookData workbook)
    {
        if (!workbook.TryGetWorksheet("Bank Statement", out var bank) ||
            !workbook.TryGetWorksheet("Sales", out var sales)) return false;

        static HashSet<string> HeaderKeys(OpenXmlWorksheetData sheet)
        {
            if (!sheet.Rows.TryGetValue(1, out var row)) return new(StringComparer.OrdinalIgnoreCase);
            return row.Values
                .Select(value => Normalize(value))
                .Where(value => value.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var bankHeaders = HeaderKeys(bank);
        var salesHeaders = HeaderKeys(sales);
        var bankContract = new[] { "DATE", "NARRATION", "DEBIT", "CREDIT", "MODE", "LEDGER", "BANK" };
        var salesContract = new[] { "GSTIN", "TRADELEGALNAME", "INVOICENUMBER", "INVOICEDATE", "ITEMNAME", "ACTUALQUANTITY", "UNIT", "ITEMRATE", "ITEMAMOUNT" };
        if (!bankContract.All(bankHeaders.Contains) || !salesContract.All(salesHeaders.Contains)) return false;

        // These additional names distinguish the supplied accounting workbook from an
        // ordinary Bank+Sales workbook and allow Universal Dump/Auto Import to route all
        // modules in one pass without guessing one accounting type.
        var supporting = new[] { "Master", "Purchase", "Debit note", "Credit Note", "Journal", "Ledger Mapping" };
        return supporting.Count(name => workbook.Worksheets.ContainsKey(name)) >= 4;
    }

    private static AutomationFileClassification PreferMappedAccountingType(OpenXmlWorkbookData workbook, string fileName, AutomationFileClassification detected, string sourcePath = "")
    {
        try
        {
            using var scope = ImportHeaderMappingRuntime.BeginSource(string.IsNullOrWhiteSpace(sourcePath) ? workbook.FilePath : sourcePath);
            var candidates = new[]
            {
                (Module: ImportHeaderMappingCatalog.Banking, Kind: AutomationFileKind.BankSpreadsheet),
                (Module: ImportHeaderMappingCatalog.Sales, Kind: AutomationFileKind.Sales),
                (Module: ImportHeaderMappingCatalog.Purchase, Kind: AutomationFileKind.Purchase),
                (Module: ImportHeaderMappingCatalog.Cash, Kind: AutomationFileKind.Cash),
                (Module: ImportHeaderMappingCatalog.Journal, Kind: AutomationFileKind.Journal)
            };
            var scored = candidates.Select(candidate =>
            {
                var canonical = workbook.Worksheets.Values
                    .SelectMany(sheet => sheet.Rows.OrderBy(pair => pair.Key).Take(35).SelectMany(pair => pair.Value.Values))
                    .Select(value => ImportHeaderMappingRuntime.Resolve(candidate.Module, value))
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return (candidate.Module, candidate.Kind, Count: canonical.Length, Complete: ImportHeaderMappingCatalog.RequiredFieldsSatisfied(candidate.Module, canonical));
            }).OrderByDescending(item => item.Complete).ThenByDescending(item => item.Count).ToArray();
            var best = scored.FirstOrDefault();
            if (best.Count < 2 || (!best.Complete && detected.Kind is not AutomationFileKind.Unknown and not AutomationFileKind.Unsupported)) return detected;
            var tied = scored.Count(item => item.Complete == best.Complete && item.Count == best.Count);
            if (tied > 1 && !best.Complete) return detected;
            if (best.Complete || detected.Kind is AutomationFileKind.Unknown or AutomationFileKind.Unsupported)
                return new AutomationFileClassification(best.Kind, best.Complete ? 98 : 82, $"Global/built-in header mapping identified {best.Module} from {best.Count:N0} canonical header(s).",
                    BankScore: best.Kind == AutomationFileKind.BankSpreadsheet ? best.Count : 0,
                    SalesScore: best.Kind == AutomationFileKind.Sales ? best.Count : 0,
                    PurchaseScore: best.Kind == AutomationFileKind.Purchase ? best.Count : 0,
                    CashScore: best.Kind == AutomationFileKind.Cash ? best.Count : 0,
                    JournalScore: best.Kind == AutomationFileKind.Journal ? best.Count : 0);
        }
        catch { }
        return detected;
    }

    private static AutomationFileClassification ScoreAccountingType(IReadOnlyList<string> values, string fileName)
    {
        var all = values.Concat(new[] { Normalize(fileName) }).ToArray();
        var bank = 0;
        var sales = 0;
        var purchase = 0;
        var cash = 0;
        var journal = 0;

        bank += HasAny(all, "NARRATION", "PARTICULARS", "TRANSACTION DESCRIPTION", "TRANSACTION DETAILS", "REMARKS") ? 3 : 0;
        bank += HasAny(all, "DEBIT", "WITHDRAWAL", "PAID OUT", "AMOUNT DR", "AMT DR") ? 2 : 0;
        bank += HasAny(all, "CREDIT", "DEPOSIT", "RECEIVED IN", "AMOUNT CR", "AMT CR") ? 2 : 0;
        bank += HasAny(all, "RUNNING BALANCE", "CLOSING BALANCE", "AVAILABLE BALANCE", "BALANCE") ? 2 : 0;
        bank += HasAny(all, "VALUE DATE", "TRANSACTION DATE", "BOOKING DATE") ? 1 : 0;
        bank += HasAny(all, "ACCOUNT NUMBER", "A/C NO", "AC NO", "IFSC") ? 1 : 0;

        var invoiceSignal = HasAny(all, "INVOICE NO", "INVOICE NUMBER", "DOCUMENT NO", "NOTE NO", "VOUCHER NO");
        var taxableSignal = HasAny(all, "TAXABLE VALUE", "TAXABLE AMOUNT", "TAXABLE");
        var taxSignal = HasAny(all, "IGST", "CGST", "SGST", "UTGST", "GST RATE", "RATE %");
        var itemSignal = HasAny(all, "ITEM NAME", "ITEM", "HSN", "SAC", "QUANTITY", "QTY", "UNIT");

        sales += invoiceSignal ? 2 : 0;
        sales += taxableSignal ? 2 : 0;
        sales += taxSignal ? 1 : 0;
        sales += itemSignal ? 1 : 0;
        sales += HasAny(all, "CUSTOMER GSTIN", "RECIPIENT GSTIN", "GSTIN/UIN OF RECIPIENT", "CUSTOMER NAME", "BUYER", "SALES REGISTER", "CREDIT NOTE") ? 4 : 0;
        sales += HasAny(all, "B2CS", "B2CL", "OUTWARD SUPPLY", "PLACE OF SUPPLY") ? 2 : 0;

        purchase += invoiceSignal ? 2 : 0;
        purchase += taxableSignal ? 2 : 0;
        purchase += taxSignal ? 1 : 0;
        purchase += itemSignal ? 1 : 0;
        purchase += HasAny(all, "SUPPLIER GSTIN", "GSTIN OF SUPPLIER", "VENDOR GSTIN", "SUPPLIER NAME", "VENDOR NAME", "PURCHASE REGISTER") ? 5 : 0;
        purchase += HasAny(all, "GSTR 2A", "GSTR2A", "GSTR 2B", "GSTR2B", "ITC AVAILABLE", "REVERSE CHARGE", "RCM") ? 4 : 0;

        // Universal Dump Cash/Journal routing is deliberately conservative. A generic
        // Debit/Credit spreadsheet is not enough because that would collide with bank
        // statements. We require accounting-specific Cash/Journal headers or sheet names.
        cash += HasAny(all, "CASH ENTRY", "CASH BOOK", "CASH RECEIPT", "CASH PAYMENT") ? 6 : 0;
        cash += HasAny(all, "COMMON LEDGER", "CASH LEDGER", "CASH-IN-HAND", "CASH IN HAND") ? 4 : 0;
        cash += HasAny(all, "ENTRY LEDGER", "LEDGER NAME") ? 2 : 0;
        cash += HasAny(all, "DR/CR", "DEBIT", "CREDIT", "AMOUNT") ? 1 : 0;
        cash += HasAny(all, "NARRATION", "REFERENCE NO", "REFERENCE NUMBER") ? 1 : 0;

        journal += HasAny(all, "JOURNAL ENTRY", "JOURNAL REGISTER", "JOURNAL VOUCHER", "MANUAL JOURNAL") ? 6 : 0;
        journal += HasAny(all, "VOUCHER NUMBER", "VOUCHER NO", "VOUCHER DATE") ? 3 : 0;
        journal += HasAny(all, "LEDGER NAME", "PARTICULARS") ? 2 : 0;
        journal += HasAny(all, "DR/CR", "DEBIT", "CREDIT") ? 2 : 0;
        journal += HasAny(all, "JOURNAL") ? 2 : 0;

        var ranked = new[]
        {
            (Kind: AutomationFileKind.BankSpreadsheet, Score: bank),
            (Kind: AutomationFileKind.Sales, Score: sales),
            (Kind: AutomationFileKind.Purchase, Score: purchase),
            (Kind: AutomationFileKind.Cash, Score: cash),
            (Kind: AutomationFileKind.Journal, Score: journal)
        }.OrderByDescending(item => item.Score).ToArray();

        var winner = ranked[0];
        var second = ranked[1];
        var threshold = winner.Kind == AutomationFileKind.BankSpreadsheet ? 6 : 6;
        if (winner.Score < threshold)
            return new(AutomationFileKind.Unknown, winner.Score,
                $"No accounting type reached a safe confidence threshold (Bank {bank}, Sales {sales}, Purchase {purchase}, Cash {cash}, Journal {journal}).",
                bank, sales, purchase, cash, journal);

        if (second.Score >= threshold && winner.Score - second.Score <= 2)
            return new(AutomationFileKind.Unknown, winner.Score,
                $"The source is ambiguous (Bank {bank}, Sales {sales}, Purchase {purchase}, Cash {cash}, Journal {journal}); automatic import will not guess.",
                bank, sales, purchase, cash, journal);

        return new(winner.Kind, Math.Min(100, 50 + winner.Score * 5),
            $"Content classifier selected {winner.Kind} (Bank {bank}, Sales {sales}, Purchase {purchase}, Cash {cash}, Journal {journal}).",
            bank, sales, purchase, cash, journal);
    }

    private static AutomationFileClassification ClassifyPurchaseContainer(string path, string extension)
    {
        try
        {
            var fileName = Normalize(Path.GetFileName(path));
            if (fileName.Contains("GSTR 2A") || fileName.Contains("GSTR2A") || fileName.Contains("GSTR 2B") || fileName.Contains("GSTR2B"))
                return new(AutomationFileKind.Purchase, 100, "GSTR-2A/2B source name detected.", PurchaseScore: 10);

            string content;
            if (extension == ".json")
            {
                content = SafeReadText(path, 1_000_000);
            }
            else
            {
                using var zipStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);
                var relevant = archive.Entries.Where(entry =>
                        entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    .Take(20)
                    .Select(entry => entry.FullName)
                    .ToArray();
                content = string.Join(' ', relevant);
            }

            var normalized = Normalize(content);
            if ((normalized.Contains("GSTR") || normalized.Contains("B2B") || normalized.Contains("CDN")) &&
                (normalized.Contains("CTIN") || normalized.Contains("GSTIN") || normalized.Contains("2A") || normalized.Contains("2B")))
                return new(AutomationFileKind.Purchase, 90, "GST Purchase/GSTR source structure detected.", PurchaseScore: 9);

            return new(AutomationFileKind.Unknown, 0, "JSON/ZIP source was not recognized as a supported Purchase/GSTR container.");
        }
        catch (Exception exception)
        {
            return new(AutomationFileKind.Unknown, 0, $"The JSON/ZIP source could not be inspected safely: {exception.Message}");
        }
    }

    private static bool LooksLikeMt940(string path)
    {
        try
        {
            var text = SafeReadText(path, 256_000);
            return Regex.IsMatch(text, @"(?m)^:20:.+") &&
                   Regex.IsMatch(text, @"(?m)^:25:.+") &&
                   Regex.IsMatch(text, @"(?m)^:61:.+");
        }
        catch
        {
            return false;
        }
    }

    internal static string ConvertLegacyXlsForInspection(string sourcePath, string password = "")
    {
        Exception? firstFailure = null;
        try
        {
            return ConvertLegacyXlsForInspectionCore(sourcePath, password);
        }
        catch (Exception exception)
        {
            firstFailure = exception;
        }

        foreach (var candidate in LocalPasswordCandidateDiscovery.Discover(sourcePath))
        {
            if (candidate.Equals(password ?? string.Empty, StringComparison.Ordinal)) continue;
            try { return ConvertLegacyXlsForInspectionCore(sourcePath, candidate); }
            catch { /* continue through deterministic candidate order */ }
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var encryptedOpenXmlContainer = SpreadsheetSourceInspector.IsEncryptedOpenXmlContainer(sourcePath);
        if (encryptedOpenXmlContainer || IsPasswordInspectionFailure(firstFailure))
            throw new InvalidDataException(
                "The workbook is password protected. Automatic candidates from the file name and smallest nearby local files did not unlock it; manual password entry is required.",
                firstFailure);

        throw firstFailure ?? new InvalidDataException("Microsoft Excel could not inspect the workbook.");
    }

    private static string ConvertLegacyXlsForInspectionCore(string sourcePath, string password)
    {
        try
        {
            return ExcelComWorkbookMaterializer.ConvertToOpenXml(
                sourcePath,
                password ?? string.Empty,
                corruptLoad: 0,
                purpose: "AutoClassify");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("Microsoft Excel could not inspect the workbook safely.", exception);
        }
    }

    private static bool IsPasswordInspectionFailure(Exception? exception)
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

    private static bool HasAny(IEnumerable<string> values, params string[] needles) =>
        values.Any(value => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase)));

    private static string Normalize(string? value) =>
        Regex.Replace((value ?? string.Empty).Trim().ToUpperInvariant(), @"[^A-Z0-9%/]+", " ").Trim();

    private static bool IsPasswordOrEncryptionInspectionFailure(string? reason)
    {
        var value = reason ?? string.Empty;
        return value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("encrypted", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("encryption", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStrongBankStatementFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
        var compact = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return compact.Contains("downloadstatement", StringComparison.Ordinal) ||
               compact.Contains("bankstatement", StringComparison.Ordinal) ||
               compact.Contains("accountstatement", StringComparison.Ordinal) ||
               compact.StartsWith("stmt", StringComparison.Ordinal) ||
               compact.EndsWith("stmt", StringComparison.Ordinal);
    }

    private static string SafeReadText(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = (int)Math.Min(maxBytes, stream.Length);
        var bytes = new byte[length];
        var read = stream.Read(bytes, 0, length);
        if (read < bytes.Length) Array.Resize(ref bytes, read);
        try { return new System.Text.UTF8Encoding(false, true).GetString(bytes); }
        catch { return System.Text.Encoding.Latin1.GetString(bytes); }
    }
}
