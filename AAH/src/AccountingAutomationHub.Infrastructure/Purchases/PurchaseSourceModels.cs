using System.Globalization;

namespace AccountingAutomationHub.Infrastructure.Purchases;

internal enum PurchaseSourceKind
{
    TwoA,
    TwoB
}

internal sealed class PurchaseSourceRow
{
    public required PurchaseSourceKind SourceKind { get; init; }
    public required string SourceFilePath { get; init; }
    public required string SheetName { get; init; }
    public required int RowNumber { get; init; }
    public required string Section { get; init; }
    public required string SupplierName { get; init; }
    public required string SupplierGstin { get; init; }
    public required string DocumentType { get; init; }
    public required string DocumentNumber { get; init; }
    public required DateTime DocumentDate { get; init; }
    public DateTime? FilingDate { get; init; }
    public string ReturnPeriod { get; init; } = string.Empty;
    public required decimal DocumentValue { get; init; }
    public required decimal Rate { get; init; }
    public bool RateKnown { get; init; } = true;
    public required decimal TaxableValue { get; init; }
    public required decimal Igst { get; init; }
    public required decimal Cgst { get; init; }
    public required decimal Sgst { get; init; }
    public required decimal Cess { get; init; }
    public required string PlaceOfSupply { get; init; }
    public required string ReverseCharge { get; init; }
    public required string InvoiceType { get; init; }
    public required string ItcAvailability { get; init; }
    public required string OriginalDocumentNumber { get; init; }
    public DateTime? OriginalDocumentDate { get; init; }
    public required string Remarks { get; init; }

    // Fix94 optional inventory fields. GST-return sources normally leave these blank;
    // Winman/custom Purchase workbooks can carry them through the same parser.
    public string ItemName { get; init; } = string.Empty;
    public string HsnCode { get; init; } = string.Empty;
    public decimal ActualQuantity { get; init; }
    public string Unit { get; init; } = string.Empty;
    public decimal ItemRate { get; init; }
    public decimal BilledQuantity { get; init; }
    public decimal ItemAmount { get; init; }
    public string ItemPurchaseLedger { get; init; } = string.Empty;
    public string Godown { get; init; } = string.Empty;
    public string BatchName { get; init; } = string.Empty;
    public decimal DiscountPercent { get; init; }
    public decimal DiscountAmount { get; init; }
    public string OrderNumber { get; init; } = string.Empty;
    public string TrackingNumber { get; init; } = string.Empty;
    public DateTime? ManufacturingDate { get; init; }
    public DateTime? ExpiryDate { get; init; }
    public string ItemDescription { get; init; } = string.Empty;
    public string CostCentre { get; init; } = string.Empty;
    public decimal FreeQuantity { get; init; }

    public string DocumentKey =>
        string.Join(
            "|",
            Section.ToUpperInvariant(),
            SupplierGstin,
            PurchaseImportUtilities.NormalizeDocumentNumber(
                DocumentNumber),
            DocumentDate.ToString(
                "yyyyMMdd",
                CultureInfo.InvariantCulture),
            PurchaseImportUtilities.NormalizeComparisonText(
                DocumentType));
}

internal sealed record PurchaseLedgerSet(
    string Purchase,
    string Igst,
    string Cgst,
    string Sgst);
