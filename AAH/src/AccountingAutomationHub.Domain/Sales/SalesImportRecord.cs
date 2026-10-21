namespace AccountingAutomationHub.Domain.Sales;

public sealed class SalesImportRecord
{
    public long PersistentId { get; set; }
    public long BatchId { get; set; }
    public bool IsUserEdited { get; set; }

    public string Destination { get; set; } = string.Empty;
    public string CustomerGstin { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    // Fix125: immutable source party name for Manual/B2C grouping. CustomerName may
    // later be changed by the B2C naming panel, but this source identity must survive.
    public string OriginalCustomerName { get; set; } = string.Empty;
    public string CustomerNameSource { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public DateTime? DocumentDate { get; set; }
    public DateTime? GstDate { get; set; }

    public decimal SourceDocumentValue { get; set; }
    public decimal DocumentValue { get; set; }
    public decimal Rate { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal Igst { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Cess { get; set; }
    public decimal CessRate { get; set; }
    public decimal RoundOff { get; set; }

    // R9 FIXED16: Tally voucher XML can contain more than one GST slab inside one
    // voucher. Rate then carries the weighted/effective percentage only for display
    // and reconciliation; the imported IGST/CGST/SGST amounts and exact ledger names
    // remain accounting authority. Companion Tally Sales Register sheets enrich the
    // evidence without inventing a single statutory slab for a mixed voucher.
    public bool IsEffectiveMixedGstRate { get; set; }
    public string GstRateEvidence { get; set; } = string.Empty;

    public string SalesLedger { get; set; } = string.Empty;
    public string IgstLedger { get; set; } = string.Empty;
    public string CgstLedger { get; set; } = string.Empty;
    public string SgstLedger { get; set; } = string.Empty;

    // Inventory / item-invoice fields. These mirror the refined Excel Sales and
    // Credit Note model and are intentionally optional so accounting-only GSTR-1
    // imports continue to work without fabricated stock data.
    public string ItemName { get; set; } = string.Empty;
    public string HsnCode { get; set; } = string.Empty;
    public decimal ActualQuantity { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal ItemRate { get; set; }
    public decimal BilledQuantity { get; set; }
    public decimal ItemAmount { get; set; }
    public string ItemSalesLedger { get; set; } = string.Empty;
    public decimal AccountingAmount { get; set; }
    public string Godown { get; set; } = string.Empty;
    public string BatchName { get; set; } = string.Empty;
    public decimal BatchActualQuantity { get; set; }
    public decimal BatchBilledQuantity { get; set; }
    public decimal BatchAmount { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal DiscountAmount { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string TrackingNumber { get; set; } = string.Empty;
    public DateTime? ManufacturingDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public string PartNumberSku { get; set; } = string.Empty;
    // Fix117: durable link back to the authoritative AAH/Tally Stock Master.
    public long StockItemPersistentId { get; set; }
    public string TallyStockMasterId { get; set; } = string.Empty;
    public string RateUnit { get; set; } = string.Empty;
    public decimal Mrp { get; set; }
    public string SerialLotNumber { get; set; } = string.Empty;
    public string CostCentre { get; set; } = string.Empty;
    public string PriceLevel { get; set; } = string.Empty;
    public decimal AlternateQuantity { get; set; }
    public string AlternateUnit { get; set; } = string.Empty;
    public decimal FreeQuantity { get; set; }
    public string DeliveryNoteNumber { get; set; } = string.Empty;
    public string BuyerOrderNumber { get; set; } = string.Empty;
    public DateTime? BuyerOrderDate { get; set; }
    public string Narration { get; set; } = string.Empty;
    // Source tag carried by linked accounting workbooks (for example AT 25 26).
    // This is evidence only and does not replace SourcePath/SourceHash identity.
    public string ImportSource { get; set; } = string.Empty;

    // Original/imported values are retained so unit/quantity corrections never
    // compound a previous conversion and can be safely recalculated or reversed.
    public string OriginalItemName { get; set; } = string.Empty;
    public string OriginalUnit { get; set; } = string.Empty;
    public decimal? OriginalActualQuantity { get; set; }
    public decimal? OriginalBilledQuantity { get; set; }
    public decimal? OriginalItemRate { get; set; }
    public decimal? OriginalItemAmount { get; set; }
    public decimal QuantityConversionFactor { get; set; } = 1m;
    public string QuantityConversionKey { get; set; } = string.Empty;
    public bool IsQuantityConverted { get; set; }
    public bool ItemAliasApplied { get; set; }
    public bool UnitMappingApplied { get; set; }

    // Party / buyer / consignee / dispatch fields used by the Excel XML route.
    public string PartyMailingName { get; set; } = string.Empty;
    public string PartyState { get; set; } = string.Empty;
    public string PartyCountry { get; set; } = string.Empty;
    public string PartyPincode { get; set; } = string.Empty;
    public string PartyAddressLine1 { get; set; } = string.Empty;
    public string PartyAddressLine2 { get; set; } = string.Empty;
    public string PartyAddressLine3 { get; set; } = string.Empty;
    public string BuyerName { get; set; } = string.Empty;
    public string BuyerAddressLine1 { get; set; } = string.Empty;
    public string BuyerAddressLine2 { get; set; } = string.Empty;
    public string BuyerAddressLine3 { get; set; } = string.Empty;
    public string ConsigneeName { get; set; } = string.Empty;
    public string ConsigneeGstin { get; set; } = string.Empty;
    public string ConsigneeState { get; set; } = string.Empty;
    public string ConsigneeCountry { get; set; } = string.Empty;
    public string ConsigneePincode { get; set; } = string.Empty;
    public string ConsigneeAddressLine1 { get; set; } = string.Empty;
    public string ConsigneeAddressLine2 { get; set; } = string.Empty;
    public string ConsigneeAddressLine3 { get; set; } = string.Empty;
    public int PaymentDueDays { get; set; }
    public DateTime? DeliveryNoteDate { get; set; }
    public string DeliveryNoteNumber2 { get; set; } = string.Empty;
    public DateTime? DeliveryNoteDate2 { get; set; }
    public string BuyerOrderReference { get; set; } = string.Empty;
    public string BuyerOrderTerms { get; set; } = string.Empty;
    public string DispatchDocumentNumber { get; set; } = string.Empty;
    public string DispatchThrough { get; set; } = string.Empty;
    public string FinalDestination { get; set; } = string.Empty;
    public string DestinationCountry { get; set; } = string.Empty;
    public string VehicleVesselNumber { get; set; } = string.Empty;
    public string LrBillOfLadingNumber { get; set; } = string.Empty;
    public DateTime? LrBillOfLadingDate { get; set; }
    public string PlaceOfReceipt { get; set; } = string.Empty;
    public string PortOfLoading { get; set; } = string.Empty;
    public string PortOfDischarge { get; set; } = string.Empty;
    public string ShippingBillNumber { get; set; } = string.Empty;
    public DateTime? ShippingBillDate { get; set; }
    public string PortCode { get; set; } = string.Empty;
    public string CheckPost { get; set; } = string.Empty;

    // e-Invoice / e-Way Bill business fields.
    public string CompanyGstRegistration { get; set; } = string.Empty;
    public string CompanyGstRegistrationType { get; set; } = string.Empty;
    public string CompanyGstState { get; set; } = string.Empty;
    public string TaxUnitName { get; set; } = string.Empty;
    public DateTime? InvoiceDateTime { get; set; }
    public string Irn { get; set; } = string.Empty;
    public string IrnAcknowledgementNumber { get; set; } = string.Empty;
    public DateTime? IrnAcknowledgementDate { get; set; }
    public string BillToPlace { get; set; } = string.Empty;
    public string ShipToPlace { get; set; } = string.Empty;
    public string DispatchFromName { get; set; } = string.Empty;
    public string DispatchFromAddressLine1 { get; set; } = string.Empty;
    public string DispatchFromAddressLine2 { get; set; } = string.Empty;
    public string DispatchFromAddressLine3 { get; set; } = string.Empty;
    public string DispatchFromState { get; set; } = string.Empty;
    public string DispatchFromPincode { get; set; } = string.Empty;
    public string DispatchFromPlace { get; set; } = string.Empty;
    public string EWayBillNumber { get; set; } = string.Empty;
    public DateTime? EWayBillDate { get; set; }
    public string TransporterId { get; set; } = string.Empty;
    public string TransporterName { get; set; } = string.Empty;
    public string TransportMode { get; set; } = string.Empty;
    public string TransportDocumentNumber { get; set; } = string.Empty;
    public DateTime? TransportDocumentDate { get; set; }
    public string EWayVehicleNumber { get; set; } = string.Empty;
    public string VehicleType { get; set; } = string.Empty;
    public string TypeOfGoodsGold { get; set; } = string.Empty;
    public bool ExcludeFromEWayBill { get; set; }

    public string SourceSection { get; set; } = string.Empty;
    public bool IsAmendment { get; set; }
    public string OriginalDocumentNumber { get; set; } = string.Empty;
    public DateTime? OriginalDocumentDate { get; set; }
    public string PlaceOfSupply { get; set; } = string.Empty;
    public string ReturnPeriod { get; set; } = string.Empty;

    public string SourceFile { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceOrigin { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string SourceScope { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public string ResponseTimestamp { get; set; } = string.Empty;
    public string SalesMasterFile { get; set; } = string.Empty;

    public string ClientGstin { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string ImportMode { get; set; } = string.Empty;
    public int ItemSequence { get; set; }

    public bool HasInventory =>
        ItemName.Trim().Length > 0 &&
        (Math.Abs(ActualQuantity) > 0.000001m || Math.Abs(BilledQuantity) > 0.000001m) &&
        Unit.Trim().Length > 0 &&
        (ItemSalesLedger.Trim().Length > 0 || SalesLedger.Trim().Length > 0) &&
        (Math.Abs(ItemAmount) > 0.005m || Math.Abs(TaxableValue) > 0.005m);
}
