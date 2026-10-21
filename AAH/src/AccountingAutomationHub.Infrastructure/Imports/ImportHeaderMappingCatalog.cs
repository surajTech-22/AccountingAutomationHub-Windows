using AccountingAutomationHub.Infrastructure.Sales;

namespace AccountingAutomationHub.Infrastructure.Imports;

public sealed record ImportHeaderFieldDefinition(
    string Module,
    string Key,
    string DisplayName,
    string RequiredGroup = "",
    bool Required = false);

public static class ImportHeaderMappingCatalog
{
    public const string Banking = "Banking";
    public const string Sales = "Sales";
    public const string Purchase = "Purchase";
    public const string Journal = "Journal";
    public const string Cash = "Cash";
    public const string Suspense = "Suspense";

    private static readonly string[] ModuleNames = [Banking, Sales, Purchase, Journal, Cash];
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ImportHeaderFieldDefinition>> Fields = BuildFields();
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuiltInAliases = BuildAliases();

    public static IReadOnlyList<string> Modules => ModuleNames;

    public static IReadOnlyList<ImportHeaderFieldDefinition> GetFields(string module) =>
        Fields.TryGetValue(NormalizeModule(module), out var fields) ? fields : Array.Empty<ImportHeaderFieldDefinition>();

    public static ImportHeaderFieldDefinition? FindField(string module, string key) =>
        GetFields(module).FirstOrDefault(item => item.Key.Equals(key?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string ResolveBuiltIn(string module, string sourceHeader)
    {
        var normalizedModule = NormalizeModule(module);
        var normalizedHeader = NormalizeHeader(sourceHeader);
        if (normalizedHeader.Length == 0) return string.Empty;
        if (normalizedModule.Equals(Sales, StringComparison.OrdinalIgnoreCase))
        {
            var sales = SalesManualFieldCatalog.ResolveBuiltInOnly(sourceHeader);
            if (sales.Length > 0) return sales;
        }
        return BuiltInAliases.TryGetValue(normalizedModule, out var aliases) && aliases.TryGetValue(normalizedHeader, out var canonical)
            ? canonical
            : string.Empty;
    }

    public static bool RequiredFieldsSatisfied(string module, IEnumerable<string> mappedCanonicalFields)
    {
        var normalizedModule = NormalizeModule(module);
        var set = mappedCanonicalFields.Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return normalizedModule switch
        {
            Banking => set.Contains("Date") && set.Contains("Narration") &&
                       (set.Contains("Debit") || set.Contains("Credit") || set.Contains("Amount")),
            Sales => set.Contains("DocumentDate") && set.Contains("DocumentNumber") &&
                     (set.Contains("DocumentValue") || set.Contains("TaxableValue") || set.Contains("ItemAmount")),
            Purchase => set.Contains("GSTIN") && set.Contains("InvoiceNo") && set.Contains("InvoiceDate") &&
                        (set.Contains("TaxableValue") || set.Contains("InvoiceValue")),
            Journal => set.Contains("Date") && set.Contains("Ledger") &&
                       (set.Contains("Amount") || set.Contains("Debit") || set.Contains("Credit")),
            Cash => set.Contains("Date") && set.Contains("EntryLedger") &&
                    (set.Contains("Amount") || set.Contains("Debit") || set.Contains("Credit")),
            Suspense => set.Contains("TransactionId") && set.Contains("MappedLedger"),
            _ => false
        };
    }

    public static IReadOnlyList<string> GetMissingRequiredDescriptions(string module, IEnumerable<string> mappedCanonicalFields)
    {
        var set = mappedCanonicalFields.Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        switch (NormalizeModule(module))
        {
            case Banking:
                if (!set.Contains("Date")) missing.Add("Date");
                if (!set.Contains("Narration")) missing.Add("Narration");
                if (!set.Contains("Debit") && !set.Contains("Credit") && !set.Contains("Amount")) missing.Add("Debit/Credit or Amount");
                break;
            case Sales:
                if (!set.Contains("DocumentDate")) missing.Add("Invoice / Note Date");
                if (!set.Contains("DocumentNumber")) missing.Add("Invoice / Note Number");
                if (!set.Contains("DocumentValue") && !set.Contains("TaxableValue") && !set.Contains("ItemAmount")) missing.Add("Invoice/Taxable/Item Amount");
                break;
            case Purchase:
                if (!set.Contains("GSTIN")) missing.Add("Supplier GSTIN");
                if (!set.Contains("InvoiceNo")) missing.Add("Invoice Number");
                if (!set.Contains("InvoiceDate")) missing.Add("Invoice Date");
                if (!set.Contains("TaxableValue") && !set.Contains("InvoiceValue")) missing.Add("Taxable/Invoice Value");
                break;
            case Journal:
                if (!set.Contains("Date")) missing.Add("Date");
                if (!set.Contains("Ledger")) missing.Add("Ledger");
                if (!set.Contains("Amount") && !set.Contains("Debit") && !set.Contains("Credit")) missing.Add("Amount or Debit/Credit");
                break;
            case Cash:
                if (!set.Contains("Date")) missing.Add("Date");
                if (!set.Contains("EntryLedger")) missing.Add("Entry Ledger");
                if (!set.Contains("Amount") && !set.Contains("Debit") && !set.Contains("Credit")) missing.Add("Amount or Debit/Credit");
                break;
            case Suspense:
                if (!set.Contains("TransactionId")) missing.Add("Transaction ID");
                if (!set.Contains("MappedLedger")) missing.Add("Mapped Ledger");
                break;
        }
        return missing;
    }

    public static string NormalizeModule(string? module)
    {
        var value = (module ?? string.Empty).Trim();
        if (value.Equals("Bank", StringComparison.OrdinalIgnoreCase)) return Banking;
        if (value.Equals("Banking", StringComparison.OrdinalIgnoreCase)) return Banking;
        if (value.Equals("Sales", StringComparison.OrdinalIgnoreCase)) return Sales;
        if (value.Equals("Purchase", StringComparison.OrdinalIgnoreCase) || value.Equals("Purchases", StringComparison.OrdinalIgnoreCase)) return Purchase;
        if (value.Equals("Journal", StringComparison.OrdinalIgnoreCase) || value.Equals("Journals", StringComparison.OrdinalIgnoreCase)) return Journal;
        if (value.Equals("Cash", StringComparison.OrdinalIgnoreCase)) return Cash;
        if (value.Contains("Suspense", StringComparison.OrdinalIgnoreCase)) return Suspense;
        return value;
    }

    public static string NormalizeHeader(string? value) =>
        string.Concat((value ?? string.Empty).ToUpperInvariant().Where(char.IsLetterOrDigit));

    private static IReadOnlyDictionary<string, IReadOnlyList<ImportHeaderFieldDefinition>> BuildFields()
    {
        var result = new Dictionary<string, IReadOnlyList<ImportHeaderFieldDefinition>>(StringComparer.OrdinalIgnoreCase)
        {
            [Banking] =
            [
                new(Banking, "Date", "Transaction Date", "date", true),
                new(Banking, "ValueDate", "Value Date"),
                new(Banking, "Narration", "Narration / Particulars", "narration", true),
                new(Banking, "Debit", "Debit / Withdrawal", "money"),
                new(Banking, "Credit", "Credit / Deposit", "money"),
                new(Banking, "Amount", "Combined Amount", "money"),
                new(Banking, "Balance", "Running / Closing Balance"),
                new(Banking, "Reference", "Reference / Cheque / UTR"),
                new(Banking, "DrCr", "Dr / Cr Indicator")
            ],
            [Purchase] =
            [
                new(Purchase, "GSTIN", "Supplier GSTIN", "gstin", true),
                new(Purchase, "SupplierName", "Supplier Name"),
                new(Purchase, "InvoiceNo", "Invoice Number", "number", true),
                new(Purchase, "InvoiceDate", "Invoice Date", "date", true),
                new(Purchase, "InvoiceValue", "Invoice Value", "amount"),
                new(Purchase, "TaxableValue", "Taxable Value", "amount"),
                new(Purchase, "Rate", "GST Rate"),
                new(Purchase, "IGST", "IGST"), new(Purchase, "CGST", "CGST"), new(Purchase, "SGST", "SGST / UTGST"), new(Purchase, "Cess", "Cess"),
                new(Purchase, "PlaceOfSupply", "Place of Supply"), new(Purchase, "ReverseCharge", "Reverse Charge"),
                new(Purchase, "ITCAvailability", "ITC Availability"), new(Purchase, "GSTTreatment", "GST Treatment / GST Applicability"), new(Purchase, "Reason", "Reason / Remarks"),
                new(Purchase, "NoteNo", "Credit/Debit Note Number"), new(Purchase, "NoteDate", "Credit/Debit Note Date"),
                new(Purchase, "NoteValue", "Credit/Debit Note Value"),
                new(Purchase, "OriginalInvoiceNo", "Original Invoice Number"), new(Purchase, "OriginalInvoiceDate", "Original Invoice Date"),
                new(Purchase, "OriginalNoteNo", "Original Credit/Debit Note Number"), new(Purchase, "OriginalNoteDate", "Original Credit/Debit Note Date"),
                new(Purchase, "ReturnPeriod", "Return / GSTR Period"), new(Purchase, "FilingDate", "GSTR-1 Filing Date"),
                new(Purchase, "SupplyType", "Supply Type"), new(Purchase, "DocumentType", "Document Type"),
                new(Purchase, "ItemName", "Stock Item / Item Name"), new(Purchase, "HsnCode", "HSN Code"),
                new(Purchase, "ActualQuantity", "Actual Quantity / Qty"), new(Purchase, "Unit", "Unit / UOM"),
                new(Purchase, "ItemRate", "Item / Unit Rate"), new(Purchase, "BilledQuantity", "Billed Quantity"),
                new(Purchase, "ItemAmount", "Item Amount"), new(Purchase, "ItemPurchaseLedger", "Item Purchase Ledger"),
                new(Purchase, "Godown", "Godown / Location"), new(Purchase, "BatchName", "Batch Name"),
                new(Purchase, "DiscountPercent", "Discount %"), new(Purchase, "DiscountAmount", "Discount Amount"),
                new(Purchase, "OrderNumber", "Order Number"), new(Purchase, "TrackingNumber", "Tracking Number"),
                new(Purchase, "ManufacturingDate", "Manufacturing Date"), new(Purchase, "ExpiryDate", "Expiry Date"),
                new(Purchase, "ItemDescription", "Item Description"), new(Purchase, "CostCentre", "Cost Centre"),
                new(Purchase, "FreeQuantity", "Free Quantity")
            ],
            [Journal] =
            [
                new(Journal, "VoucherNo", "Voucher Number"), new(Journal, "Date", "Voucher Date", "date", true),
                new(Journal, "Ledger", "Ledger / Particulars", "ledger", true), new(Journal, "Amount", "Amount", "money"),
                new(Journal, "Debit", "Debit", "money"), new(Journal, "Credit", "Credit", "money"), new(Journal, "DrCr", "Dr / Cr"),
                new(Journal, "Narration", "Narration"), new(Journal, "TaxHead", "Tax Head"), new(Journal, "Role", "Line Role"), new(Journal, "VoucherType", "Voucher Type")
            ],
            [Cash] =
            [
                new(Cash, "Date", "Date", "date", true), new(Cash, "VoucherNo", "Voucher / Reference Number"),
                new(Cash, "EntryLedger", "Entry Ledger", "ledger", true), new(Cash, "CommonLedger", "Common / Cash Ledger"),
                new(Cash, "Amount", "Amount", "money"), new(Cash, "Debit", "Debit", "money"), new(Cash, "Credit", "Credit", "money"),
                new(Cash, "DrCr", "Dr / Cr"), new(Cash, "Narration", "Narration"), new(Cash, "Reference", "Reference")
            ],
            [Suspense] =
            [
                new(Suspense, "TransactionId", "Transaction ID", "id", true), new(Suspense, "Date", "Date"),
                new(Suspense, "Narration", "Narration"), new(Suspense, "Amount", "Amount"),
                new(Suspense, "MappedLedger", "Mapped Ledger", "ledger", true), new(Suspense, "MasterId", "MASTERID"),
                new(Suspense, "Remarks", "Remarks")
            ]
        };

        var salesFields = SalesManualFieldCatalog.TargetFields
            .Select(field => new ImportHeaderFieldDefinition(
                Sales,
                field,
                SalesManualFieldCatalog.DisplayName(field),
                field.Equals("DocumentDate", StringComparison.OrdinalIgnoreCase) ? "date" :
                field.Equals("DocumentNumber", StringComparison.OrdinalIgnoreCase) ? "number" :
                field is "DocumentValue" or "TaxableValue" or "ItemAmount" ? "amount" : string.Empty,
                field is "DocumentDate" or "DocumentNumber"))
            .ToArray();
        result[Sales] = salesFields;
        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildAliases()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> Map(params (string Canonical, string[] Aliases)[] entries)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                foreach (var alias in entry.Aliases.Append(entry.Canonical))
                {
                    var key = NormalizeHeader(alias);
                    if (key.Length > 0 && !map.ContainsKey(key)) map[key] = entry.Canonical;
                }
            return map;
        }

        result[Banking] = Map(
            ("Date", ["Date", "Transaction Date", "Txn Date", "Tran Date", "Post Date", "Booking Date", "Posting Date"]),
            ("ValueDate", ["Value Date", "Val Date"]),
            ("Narration", ["Narration", "Particulars", "Details", "Description", "Transaction Details", "Remarks", "Memo"]),
            ("Debit", ["Debit", "Debit Amount", "Amount Dr", "Amt Dr", "Withdrawal", "Withdrawals", "Paid Out", "Payment"]),
            ("Credit", ["Credit", "Credit Amount", "Amount Cr", "Amt Cr", "Deposit", "Deposits", "Received In", "Receipt"]),
            ("Amount", ["Amount", "Transaction Amount", "Txn Amt", "Net Amt", "Total Amt", "Figure"]),
            ("Balance", ["Balance", "Running Balance", "Closing Balance", "Available Balance"]),
            ("Reference", ["Reference", "Ref No", "Cheque No", "Chq No", "UTR", "Transaction ID", "Txn ID"]),
            ("DrCr", ["Dr/Cr", "Dr Cr", "Type", "Transaction Type"]));

        result[Purchase] = Map(
            ("GSTIN", ["GSTIN", "Supplier GSTIN", "GSTIN of Supplier", "Vendor GSTIN"]),
            ("SupplierName", ["Supplier Name", "Vendor Name", "Trade/Legal Name", "Trade Name"]),
            ("InvoiceNo", ["Invoice No", "Invoice Number", "Document No", "Bill No"]),
            ("InvoiceDate", ["Invoice Date", "Document Date", "Bill Date"]),
            ("InvoiceValue", ["Invoice Value", "Invoice Total", "Document Value"]),
            ("TaxableValue", ["Taxable Value", "Taxable Amount"]),
            ("Rate", ["Rate", "GST Rate", "Rate %", "Tax Rate", "Tax Rate(%)", "Tax Rate %"]), ("IGST", ["IGST", "Integrated Tax"]),
            ("CGST", ["CGST", "Central Tax"]), ("SGST", ["SGST", "State Tax", "State/UT Tax", "UTGST"]), ("Cess", ["Cess"]),
            ("PlaceOfSupply", ["Place of Supply", "State of Supply", "Supply State", "POS"]), ("ReverseCharge", ["Reverse Charge", "RCM"]),
            ("ITCAvailability", ["ITC Availability", "ITC Available"]), ("GSTTreatment", ["GST Treatment", "GST Applicability", "Tax Treatment", "ITC Treatment", "2B Treatment"]), ("Reason", ["Reason", "Remarks"]),
            ("NoteNo", ["CR/DR Note No", "Credit Note No", "Debit Note No"]), ("NoteDate", ["CR/DR Note Date", "Credit Note Date", "Debit Note Date"]),
            ("NoteValue", ["CR/DR Note Value", "Credit Note Value", "Debit Note Value"]),
            ("OriginalInvoiceNo", ["Original Invoice No", "Original Invoice Number"]), ("OriginalInvoiceDate", ["Original Invoice Date"]),
            ("OriginalNoteNo", ["Original CR/DR Note No", "Original Credit Note No", "Original Debit Note No"]), ("OriginalNoteDate", ["Original CR/DR Note Date", "Original Credit Note Date", "Original Debit Note Date"]),
            ("ReturnPeriod", ["Return Period", "GSTR2A Period", "GSTR2B Period", "Return Tax Period"]), ("FilingDate", ["Filing Date", "GSTR1 Filing Date", "Date of Filing"]),
            ("SupplyType", ["Supply Type", "Place Supply Type"]), ("DocumentType", ["Document Type", "Doc Type"]),
            ("ItemName", ["Item Name", "Stock Item", "Stock Item Name", "Product"]),
            ("HsnCode", ["HSN", "HSN Code", "HSN/SAC"]),
            ("ActualQuantity", ["Quantity", "Qty", "Actual Qty", "Actual Quantity", "Bags"]),
            ("Unit", ["Unit", "Units", "UOM", "UQC"]),
            ("ItemRate", ["Item Rate", "Unit Rate", "Price", "Rate Per Unit"]),
            ("BilledQuantity", ["Billed Qty", "Billed Quantity"]),
            ("ItemAmount", ["Item Amount", "Line Amount"]),
            ("ItemPurchaseLedger", ["Item Purchase Ledger", "Purchase Ledger"]),
            ("Godown", ["Godown", "Location", "Warehouse"]), ("BatchName", ["Batch", "Batch Name"]),
            ("DiscountPercent", ["Discount %", "Discount Percent"]), ("DiscountAmount", ["Discount Amount"]),
            ("OrderNumber", ["Order No", "Order Number", "PO No", "PO Number"]), ("TrackingNumber", ["Tracking No", "Tracking Number"]),
            ("ManufacturingDate", ["Manufacturing Date", "Mfg Date"]), ("ExpiryDate", ["Expiry Date", "Exp Date"]),
            ("ItemDescription", ["Item Description", "Description"]), ("CostCentre", ["Cost Centre", "Cost Center"]),
            ("FreeQuantity", ["Free Qty", "Free Quantity"]));

        result[Journal] = Map(
            ("VoucherNo", ["Voucher No", "Voucher Number", "Vch No", "Voucher"]), ("Date", ["Date", "Voucher Date"]),
            ("Ledger", ["Ledger", "Ledger Name", "Particulars", "Account"]), ("Amount", ["Amount", "Amount INR", "Value"]),
            ("Debit", ["Debit", "Debit Amount", "Dr", "Dr Amount"]), ("Credit", ["Credit", "Credit Amount", "Cr", "Cr Amount"]),
            ("DrCr", ["Dr/Cr", "Debit/Credit", "Type"]), ("Narration", ["Narration", "Description", "Remarks"]),
            ("TaxHead", ["Tax Head", "GST Head", "Tax Type"]), ("Role", ["Role", "Line Role", "Journal Role"]), ("VoucherType", ["Voucher Type", "Vch Type"]));

        result[Cash] = Map(
            ("Date", ["Date", "Voucher Date", "Cash Date"]), ("VoucherNo", ["Voucher No", "Reference No", "Ref No"]),
            ("EntryLedger", ["Entry Ledger", "Ledger", "Ledger Name", "Particulars"]), ("CommonLedger", ["Common Ledger", "Cash Ledger", "Cash-in-Hand", "Cash In Hand"]),
            ("Amount", ["Amount", "Value"]), ("Debit", ["Debit", "Dr", "Debit Amount"]), ("Credit", ["Credit", "Cr", "Credit Amount"]),
            ("DrCr", ["Dr/Cr", "Type"]), ("Narration", ["Narration", "Description", "Remarks"]), ("Reference", ["Reference", "Reference No", "Ref No"]));

        result[Suspense] = Map(
            ("TransactionId", ["Transaction ID", "TransactionId", "ID"]), ("Date", ["Date"]), ("Narration", ["Narration", "Description", "Particulars"]),
            ("Amount", ["Amount"]), ("MappedLedger", ["Mapped Ledger", "Ledger", "Ledger Name"]), ("MasterId", ["MASTERID", "Master ID"]), ("Remarks", ["Remarks", "Reason"]));
        return result;
    }
}
