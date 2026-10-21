using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AccountingAutomationHub.Infrastructure.Governance;

public sealed record UniversalPdfInspection(
    string Kind,
    int Confidence,
    string Reason,
    string TextSample);

/// <summary>
/// Lightweight, local-only PDF classification for Universal Dump. It reads embedded
/// text only; it never OCRs, uploads, or changes the source PDF. Unknown/non-text PDFs
/// remain Review Required instead of being guessed into Banking.
/// </summary>
public static class UniversalPdfContentInspector
{
    public const string BankStatement = "Bank Statement";
    public const string Gstr3b = "GSTR-3B";
    public const string Form26As = "Form 26AS";
    public const string Invoice = "Invoice";
    public const string Unknown = "Unknown";

    public static UniversalPdfInspection Inspect(string path)
    {
        try
        {
            using var document = PdfDocument.Open(path);
            var builder = new StringBuilder();
            foreach (var page in document.GetPages().Take(6))
            {
                if (builder.Length >= 100_000) break;
                var pageText = ContentOrderTextExtractor.GetText(page);
                if (!string.IsNullOrWhiteSpace(pageText)) builder.AppendLine(pageText);
            }

            var raw = builder.ToString();
            if (string.IsNullOrWhiteSpace(raw))
                return new(Unknown, 0, "The PDF has no readable embedded text. It requires review or the existing interactive PDF pipeline.", string.Empty);

            var normalizedText = Normalize(raw);
            var gstr = 0;
            if (normalizedText.Contains("GSTR 3B", StringComparison.Ordinal) ||
                normalizedText.Contains("GSTR-3B", StringComparison.Ordinal)) gstr += 8;
            if (normalizedText.Contains("FORM GSTR", StringComparison.Ordinal)) gstr += 4;
            if (normalizedText.Contains("RETURN PERIOD", StringComparison.Ordinal) ||
                normalizedText.Contains("PERIOD ", StringComparison.Ordinal)) gstr += 2;
            if (normalizedText.Contains("OUTWARD TAXABLE SUPPLIES", StringComparison.Ordinal)) gstr += 3;
            if (normalizedText.Contains("ELIGIBLE ITC", StringComparison.Ordinal)) gstr += 2;
            if (normalizedText.Contains("GSTIN", StringComparison.Ordinal)) gstr += 1;

            var form26As = 0;
            if (normalizedText.Contains("ANNUAL TAX STATEMENT", StringComparison.Ordinal)) form26As += 9;
            if (normalizedText.Contains("PERMANENT ACCOUNT NUMBER PAN", StringComparison.Ordinal) ||
                normalizedText.Contains("ASSESSEE PAN", StringComparison.Ordinal)) form26As += 3;
            if (normalizedText.Contains("DETAILS OF TAX DEDUCTED AT SOURCE", StringComparison.Ordinal)) form26As += 5;
            if (normalizedText.Contains("TAN OF DEDUCTOR", StringComparison.Ordinal)) form26As += 3;
            if (normalizedText.Contains("TDS DEPOSITED", StringComparison.Ordinal)) form26As += 2;

            var bank = 0;
            if (ContainsAny(normalizedText, "ACCOUNT STATEMENT", "STATEMENT OF ACCOUNT", "BANK STATEMENT")) bank += 6;
            if (ContainsAny(normalizedText, "TRANSACTION DATE", "VALUE DATE", "BOOKING DATE")) bank += 2;
            if (ContainsAny(normalizedText, "NARRATION", "TRANSACTION DETAILS", "PARTICULARS")) bank += 2;
            if (ContainsAny(normalizedText, "OPENING BALANCE", "CLOSING BALANCE", "RUNNING BALANCE")) bank += 3;
            if (ContainsAny(normalizedText, "WITHDRAWAL", "DEPOSIT", "DEBIT", "CREDIT")) bank += 2;
            if (ContainsAny(normalizedText, "ACCOUNT NUMBER", "A/C NO", "IFSC")) bank += 2;

            var invoice = 0;
            if (ContainsAny(normalizedText, "TAX INVOICE", "INVOICE NUMBER", "INVOICE NO")) invoice += 6;
            if (ContainsAny(normalizedText, "BILL TO", "SHIP TO", "SUPPLIER", "CUSTOMER")) invoice += 2;
            if (normalizedText.Contains("GSTIN", StringComparison.Ordinal)) invoice += 2;
            if (ContainsAny(normalizedText, "CGST", "SGST", "IGST", "TAXABLE VALUE")) invoice += 2;

            var ranked = new[]
            {
                (Kind: Form26As, Score: form26As),
                (Kind: Gstr3b, Score: gstr),
                (Kind: BankStatement, Score: bank),
                (Kind: Invoice, Score: invoice)
            }.OrderByDescending(x => x.Score).ToArray();

            var first = ranked[0];
            var second = ranked[1];
            if (first.Score < 6)
                return new(Unknown, Math.Min(50, first.Score * 5), "PDF content did not reach a safe accounting-module confidence threshold.", Clip(raw));
            if (second.Score >= 6 && first.Score - second.Score <= 2)
                return new(Unknown, Math.Min(75, first.Score * 5), $"PDF content is ambiguous between {first.Kind} and {second.Kind}; Universal Dump will not guess.", Clip(raw));

            var confidence = Math.Min(100, 55 + first.Score * 4);
            var reason = first.Kind switch
            {
                Form26As => "Annual Tax Statement / Form 26AS markers were detected in the PDF text.",
                Gstr3b => "GSTR-3B return markers were detected in the PDF text.",
                BankStatement => "Bank statement/account/balance/transaction markers were detected in the PDF text.",
                Invoice => "Tax-invoice/GST invoice markers were detected; PDF invoice ingestion is not enabled yet, so this file must be reviewed instead of being sent to Banking.",
                _ => "PDF content requires review."
            };
            return new(first.Kind, confidence, reason, Clip(raw));
        }
        catch (Exception exception)
        {
            return new(Unknown, 0, $"The PDF could not be inspected locally: {exception.Message}", string.Empty);
        }
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var wasSpace = false;
        foreach (var ch in value.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '/' or '-' or '&')
            {
                builder.Append(ch);
                wasSpace = false;
            }
            else if (!wasSpace)
            {
                builder.Append(' ');
                wasSpace = true;
            }
        }
        return builder.ToString();
    }

    private static string Clip(string value) => value.Length <= 4_000 ? value : value[..4_000];
}
