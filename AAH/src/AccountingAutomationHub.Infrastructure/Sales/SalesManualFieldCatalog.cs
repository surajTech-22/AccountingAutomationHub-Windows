using System.Text.RegularExpressions;
using AccountingAutomationHub.Infrastructure.Imports;

namespace AccountingAutomationHub.Infrastructure.Sales;

public static class SalesManualFieldCatalog
{
    public static IReadOnlyList<string> TargetFields { get; } =
    [
        "CustomerGstin", "CustomerName", "DocumentType", "DocumentNumber", "DocumentDate", "DocumentValue",
        "Rate", "TaxableValue", "Igst", "Cgst", "Sgst", "Cess", "CessRate", "RoundOff", "SalesLedger", "IgstLedger",
        "CgstLedger", "SgstLedger", "GstDate", "PlaceOfSupply", "Narration", "ImportSource",
        "ItemName", "HsnCode", "ActualQuantity", "Unit", "ItemRate", "BilledQuantity", "ItemAmount",
        "ItemSalesLedger", "AccountingAmount", "Godown", "BatchName", "BatchActualQuantity", "BatchBilledQuantity",
        "BatchAmount", "DiscountPercent", "DiscountAmount", "OrderNumber", "TrackingNumber", "ManufacturingDate",
        "ExpiryDate", "ItemDescription", "PartNumberSku", "Mrp", "SerialLotNumber", "CostCentre", "PriceLevel",
        "AlternateQuantity", "AlternateUnit", "FreeQuantity", "DeliveryNoteNumber", "BuyerOrderNumber", "BuyerOrderDate",
        "PartyMailingName", "PartyState", "PartyCountry", "PartyPincode", "PartyAddressLine1", "PartyAddressLine2", "PartyAddressLine3",
        "BuyerName", "BuyerAddressLine1", "BuyerAddressLine2", "BuyerAddressLine3",
        "ConsigneeName", "ConsigneeGstin", "ConsigneeState", "ConsigneeCountry", "ConsigneePincode", "ConsigneeAddressLine1", "ConsigneeAddressLine2", "ConsigneeAddressLine3",
        "PaymentDueDays", "DeliveryNoteDate", "DeliveryNoteNumber2", "DeliveryNoteDate2", "BuyerOrderReference", "BuyerOrderTerms",
        "DispatchDocumentNumber", "DispatchThrough", "FinalDestination", "DestinationCountry", "VehicleVesselNumber",
        "LrBillOfLadingNumber", "LrBillOfLadingDate", "PlaceOfReceipt", "PortOfLoading", "PortOfDischarge", "ShippingBillNumber",
        "ShippingBillDate", "PortCode", "CheckPost", "CompanyGstRegistration", "CompanyGstRegistrationType", "CompanyGstState",
        "TaxUnitName", "InvoiceDateTime", "Irn", "IrnAcknowledgementNumber", "IrnAcknowledgementDate", "BillToPlace", "ShipToPlace",
        "DispatchFromName", "DispatchFromAddressLine1", "DispatchFromAddressLine2", "DispatchFromAddressLine3", "DispatchFromState",
        "DispatchFromPincode", "DispatchFromPlace", "EWayBillNumber", "EWayBillDate", "TransporterId", "TransporterName", "TransportMode",
        "TransportDocumentNumber", "TransportDocumentDate", "EWayVehicleNumber", "VehicleType", "TypeOfGoodsGold", "ExcludeFromEWayBill"
    ];

    private static readonly Dictionary<string, string> Aliases = BuildAliases();

    public static string Resolve(string sourceHeader)
    {
        var learned = ImportHeaderMappingRuntime.ResolveDetailed(ImportHeaderMappingCatalog.Sales, sourceHeader);
        if (learned.IsMatched && !learned.MatchType.Equals("Built-in", StringComparison.OrdinalIgnoreCase)) return learned.CanonicalField;
        return ResolveBuiltInOnly(sourceHeader);
    }

    public static string ResolveBuiltInOnly(string sourceHeader)
    {
        var key = Normalize(sourceHeader);
        return key.Length > 0 && Aliases.TryGetValue(key, out var value) ? value : string.Empty;
    }

    public static string DisplayName(string field) => field switch
    {
        "CustomerGstin" => "GSTIN",
        "CustomerName" => "Trade / Legal Name",
        "DocumentNumber" => "Invoice / Note Number",
        "DocumentDate" => "Invoice / Note Date",
        "DocumentValue" => "Invoice Value",
        "Rate" => "GST Rate %",
        "TaxableValue" => "Taxable Value",
        "Igst" => "IGST",
        "Cgst" => "CGST",
        "Sgst" => "SGST",
        "SalesLedger" => "Sales Ledger",
        "HsnCode" => "HSN / SAC",
        "ActualQuantity" => "Actual Quantity",
        "ItemRate" => "Item Rate",
        "ItemSalesLedger" => "Item Sales Ledger",
        "ImportSource" => "Import Source",
        "PartNumberSku" => "Part No / SKU",
        "LrBillOfLadingNumber" => "LR / Bill of Lading No",
        "Irn" => "IRN",
        _ => Regex.Replace(field, "([a-z0-9])([A-Z])", "$1 $2")
    };

    public static string Normalize(string value) =>
        string.Concat((value ?? string.Empty)
            .ToUpperInvariant()
            .Replace("₹", "")
            .Where(char.IsLetterOrDigit));

    private static Dictionary<string, string> BuildAliases()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string field, params string[] aliases)
        {
            foreach (var alias in aliases.Append(field))
            {
                var key = Normalize(alias);
                if (key.Length > 0) map[key] = field;
            }
        }

        Add("CustomerGstin", "GSTIN", "GSTIN/UIN", "Customer GSTIN", "Recipient GSTIN", "GSTIN of recipient", "Party GSTIN");
        Add("CustomerName", "Trade/Legal name", "Trade Legal Name", "Customer Name", "Party Name", "Recipient Name", "Receiver Name", "Buyer Name / Party");
        Add("DocumentType", "Document Type", "Invoice Type", "Note Type");
        Add("DocumentNumber", "Invoice number", "Invoice No", "Invoice No.", "Bill No", "Bill Nos", "Voucher No", "Note number", "Credit Note No", "Document No");
        Add("DocumentDate", "Invoice Date", "Date", "Month", "Bill Date", "Note Date", "Voucher Date", "Document Date");
        Add("DocumentValue", "Invoice Value", "Invoice Value(₹)", "Bill Total", "Invoice Total", "Grand Total", "Net Invoice Value", "Amount");
        Add("Rate", "Rate(%)", "GST Rate", "GST %", "Tax Rate", "Tax Rate (%)", "Rate of Tax");
        Add("TaxableValue", "Taxable Value", "Taxable Value (₹)", "Total Taxable Value", "Assessable Value", "Taxable Amount", "Value");
        Add("Igst", "Integrated Tax", "Integrated Tax(₹)", "IGST", "IGST Amount");
        Add("Cgst", "Central Tax", "Central Tax(₹)", "CGST", "CGST Amount");
        Add("Sgst", "State/UT Tax", "State Tax", "State/UT Tax(₹)", "SGST", "SGST Amount", "UTGST");
        Add("Cess", "Cess", "Cess Amount");
        Add("CessRate", "Cess Rate", "Cess %", "Cess Rate (%)");
        Add("RoundOff", "Round Off", "Roundoff", "Rounding");
        Add("SalesLedger", "Sales", "Sales Ledger", "Sales Account", "Ledger");
        Add("IgstLedger", "IGST Ledger", "Output IGST Ledger");
        Add("CgstLedger", "CGST Ledger", "Output CGST Ledger");
        Add("SgstLedger", "SGST Ledger", "Output SGST Ledger");
        Add("GstDate", "GST Date", "Return Date");
        Add("PlaceOfSupply", "Place of Supply", "State of supply", "POS", "Place of Supply (Auto)");
        Add("Narration", "Narration", "Remarks", "Description / Narration");
        Add("ImportSource", "IMPORT_SOURCE", "Import Source", "Source Import", "Source Tag");

        Add("ItemName", "Item Name", "Stock Item", "Product", "Product Name", "Description of Goods");
        Add("HsnCode", "HSN Code", "HSN", "HSN/SAC", "HSN SAC", "SAC");
        Add("ActualQuantity", "Actual Quantity", "Quantity", "Qty", "Actual Qty");
        Add("Unit", "Unit", "UOM", "Unit of Measure", "Unit of Measurement");
        Add("ItemRate", "Item Rate", "Rate / Unit", "Price", "Unit Rate");
        Add("BilledQuantity", "Billed Quantity", "Billed Qty", "Invoice Quantity");
        Add("ItemAmount", "Item Amount", "Line Amount", "Item Value", "Line Value");
        Add("ItemSalesLedger", "Item Sales Ledger", "Item Ledger", "Sales Ledger Name");
        Add("AccountingAmount", "Accounting Amount", "Ledger Amount");
        Add("Godown", "Godown", "Location", "Warehouse");
        Add("BatchName", "Batch Name", "Batch", "Lot Name");
        Add("BatchActualQuantity", "Batch Actual Quantity", "Batch Actual Qty");
        Add("BatchBilledQuantity", "Batch Billed Quantity", "Batch Billed Qty");
        Add("BatchAmount", "Batch Amount", "Batch Value");
        Add("DiscountPercent", "Discount %", "Discount Percent", "Disc %");
        Add("DiscountAmount", "Discount Amount", "Discount Amt", "Disc Amount");
        Add("OrderNumber", "Order No", "Order Number");
        Add("TrackingNumber", "Tracking No", "Tracking Number");
        Add("ManufacturingDate", "Manufacturing Date", "Mfg Date", "MFD");
        Add("ExpiryDate", "Expiry Date", "Exp Date", "Best Before");
        Add("ItemDescription", "Item Description", "Stock Description");
        Add("PartNumberSku", "Part No / SKU", "Part Number", "SKU");
        Add("Mrp", "MRP", "Maximum Retail Price");
        Add("SerialLotNumber", "Serial / Lot No", "Serial No", "Lot No");
        Add("CostCentre", "Cost Centre", "Cost Center");
        Add("PriceLevel", "Price Level");
        Add("AlternateQuantity", "Alternate Quantity", "Alt Quantity", "Alt Qty");
        Add("AlternateUnit", "Alternate Unit", "Alt Unit");
        Add("FreeQuantity", "Free Quantity", "Free Qty");
        Add("DeliveryNoteNumber", "Delivery Note No", "Delivery Note Number");
        Add("BuyerOrderNumber", "Buyer Order No", "Buyer Order Number", "PO No", "Purchase Order No");
        Add("BuyerOrderDate", "Buyer Order Date", "PO Date", "Purchase Order Date");

        Add("PartyMailingName", "Party Mailing Name"); Add("PartyState", "Party State"); Add("PartyCountry", "Party Country"); Add("PartyPincode", "Party Pincode", "Party Pin Code");
        Add("PartyAddressLine1", "Party Address Line 1"); Add("PartyAddressLine2", "Party Address Line 2"); Add("PartyAddressLine3", "Party Address Line 3");
        Add("BuyerName", "Buyer Name"); Add("BuyerAddressLine1", "Buyer Address Line 1"); Add("BuyerAddressLine2", "Buyer Address Line 2"); Add("BuyerAddressLine3", "Buyer Address Line 3");
        Add("ConsigneeName", "Consignee Name"); Add("ConsigneeGstin", "Consignee GSTIN"); Add("ConsigneeState", "Consignee State"); Add("ConsigneeCountry", "Consignee Country"); Add("ConsigneePincode", "Consignee Pincode");
        Add("ConsigneeAddressLine1", "Consignee Address Line 1"); Add("ConsigneeAddressLine2", "Consignee Address Line 2"); Add("ConsigneeAddressLine3", "Consignee Address Line 3");
        Add("PaymentDueDays", "Payment Due Days", "Credit Days"); Add("DeliveryNoteDate", "Delivery Note Date"); Add("DeliveryNoteNumber2", "Delivery Note No 2"); Add("DeliveryNoteDate2", "Delivery Note Date 2");
        Add("BuyerOrderReference", "Buyer Order Reference"); Add("BuyerOrderTerms", "Buyer Order Terms"); Add("DispatchDocumentNumber", "Dispatch Document No"); Add("DispatchThrough", "Dispatch Through");
        Add("FinalDestination", "Final Destination"); Add("DestinationCountry", "Destination Country"); Add("VehicleVesselNumber", "Vehicle / Vessel No"); Add("LrBillOfLadingNumber", "LR / Bill of Lading No");
        Add("LrBillOfLadingDate", "LR / Bill of Lading Date"); Add("PlaceOfReceipt", "Place of Receipt"); Add("PortOfLoading", "Port of Loading"); Add("PortOfDischarge", "Port of Discharge");
        Add("ShippingBillNumber", "Shipping Bill No"); Add("ShippingBillDate", "Shipping Bill Date"); Add("PortCode", "Port Code"); Add("CheckPost", "Check Post");

        Add("CompanyGstRegistration", "Company GST Registration"); Add("CompanyGstRegistrationType", "Company GST Registration Type"); Add("CompanyGstState", "Company GST State"); Add("TaxUnitName", "Tax Unit Name");
        Add("InvoiceDateTime", "Invoice Date Time"); Add("Irn", "IRN"); Add("IrnAcknowledgementNumber", "IRN Acknowledgement Number"); Add("IrnAcknowledgementDate", "IRN Acknowledgement Date");
        Add("BillToPlace", "Bill To Place"); Add("ShipToPlace", "Ship To Place"); Add("DispatchFromName", "Dispatch From Name"); Add("DispatchFromAddressLine1", "Dispatch From Address Line 1");
        Add("DispatchFromAddressLine2", "Dispatch From Address Line 2"); Add("DispatchFromAddressLine3", "Dispatch From Address Line 3"); Add("DispatchFromState", "Dispatch From State");
        Add("DispatchFromPincode", "Dispatch From Pincode"); Add("DispatchFromPlace", "Dispatch From Place"); Add("EWayBillNumber", "E-Way Bill Number", "Eway Bill No"); Add("EWayBillDate", "E-Way Bill Date");
        Add("TransporterId", "Transporter ID"); Add("TransporterName", "Transporter Name"); Add("TransportMode", "Transport Mode"); Add("TransportDocumentNumber", "Transport Document Number");
        Add("TransportDocumentDate", "Transport Document Date"); Add("EWayVehicleNumber", "E-Way Vehicle Number"); Add("VehicleType", "Vehicle Type"); Add("TypeOfGoodsGold", "Type of Goods Gold"); Add("ExcludeFromEWayBill", "Exclude From E-Way Bill");
        return map;
    }
}
