using System;
using System.Linq;
using UglyToad.PdfPig;

namespace AccountingAutomationHub.Desktop.Services;

internal enum BankPdfDocumentKind { Digital, Scanned, Mixed, Unknown }

/// <summary>Read-only preflight; route all mixed or uncertain pages to OCR.</summary>
internal static class BankPdfDocumentClassifier
{
    public static BankPdfDocumentKind Classify(string path)
    {
        try
        {
            using var document = PdfDocument.Open(path);
            int textPages = 0, otherPages = 0;
            foreach (var page in document.GetPages())
            {
                // Logos and headers alone must not make scanned bank tables 'digital'.
                if (page.Text.Count(char.IsLetterOrDigit) >= 40) textPages++;
                else otherPages++;
            }
            if (textPages == 0 && otherPages == 0) return BankPdfDocumentKind.Unknown;
            if (textPages == 0) return BankPdfDocumentKind.Scanned;
            if (otherPages == 0) return BankPdfDocumentKind.Digital;
            return BankPdfDocumentKind.Mixed;
        }
        catch
        {
            return BankPdfDocumentKind.Unknown;
        }
    }
}
