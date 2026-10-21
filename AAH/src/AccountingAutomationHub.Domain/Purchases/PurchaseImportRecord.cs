using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AccountingAutomationHub.Domain.Purchases;

public sealed class PurchaseImportRecord : INotifyPropertyChanged
{
    private static readonly string[] RcmTreatmentValues =
    [
        "Claimed in Same Month",
        "Claimed in Next Month",
        "Reversed"
    ];

    private string _gstApplicability = string.Empty;
    private string _purchaseLedger = string.Empty;
    private string _reconciliationStatus = string.Empty;
    private decimal _rate;
    private bool _rateKnown = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public long PersistentId { get; set; }
    public long BatchId { get; set; }
    public bool IsUserEdited { get; set; }
    public string Destination { get; set; } = string.Empty;
    public string ReconciliationStatus
    {
        get => _reconciliationStatus;
        set
        {
            if (!SetField(ref _reconciliationStatus, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(GstTreatmentOptions));
            OnPropertyChanged(nameof(AccountingGstApplicability));
            OnPropertyChanged(nameof(AccountingGstTreatmentOptions));
            OnPropertyChanged(nameof(ReconciliationGstApplicability));
            OnPropertyChanged(nameof(ReconciliationGstTreatmentOptions));
            OnPropertyChanged(nameof(IsTwoBOnlyEvidence));
        }
    }

    public string GstApplicability
    {
        get => _gstApplicability;
        set
        {
            if (!SetField(ref _gstApplicability, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(GstTreatmentOptions));
            OnPropertyChanged(nameof(AccountingGstApplicability));
            OnPropertyChanged(nameof(AccountingGstTreatmentOptions));
            OnPropertyChanged(nameof(ReconciliationGstApplicability));
            OnPropertyChanged(nameof(ReconciliationGstTreatmentOptions));
            OnPropertyChanged(nameof(IsGstIncludedInCost));
            OnPropertyChanged(nameof(IsRcmRow));
        }
    }

    [JsonIgnore]
    public bool IsGstIncludedInCost =>
        !IsRcmRow &&
        PurchaseTreatmentPolicy.IsGstIncludedInCost(GstApplicability);

    [JsonIgnore]
    public bool IsRcmRow =>
        Destination.Equals("RCM", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsTwoBOnlyEvidence =>
        ReconciliationStatus.Equals("2B Only", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public IReadOnlyList<string> GstTreatmentOptions
    {
        get
        {
            if (IsTwoBOnlyEvidence && !IsRcmRow)
                return new[]
                {
                    PurchaseTreatmentPolicy.NotIncludedInPurchase,
                    PurchaseTreatmentPolicy.IncludedInPurchase
                };

            if (!IsRcmRow)
                return PurchaseTreatmentPolicy.GetAllowedPurchaseTreatments(this);

            var standard = RcmTreatmentValues;
            if (string.IsNullOrWhiteSpace(GstApplicability) ||
                standard.Contains(GstApplicability, StringComparer.OrdinalIgnoreCase))
            {
                return standard;
            }

            return new[] { GstApplicability }
                .Concat(standard)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>
    /// Accounting-tab treatment for a GSTR-2B-only invoice after it has been promoted.
    /// Reconciliation keeps the explicit Included/Not Included state, while Purchase
    /// must expose the ordinary source-backed GST applicability choices.
    /// </summary>
    [JsonIgnore]
    public string AccountingGstApplicability
    {
        get
        {
            if (IsTwoBOnlyEvidence &&
                !Destination.Equals("2B Only", StringComparison.OrdinalIgnoreCase) &&
                !IsRcmRow &&
                PurchaseTreatmentPolicy.IsTwoBIncluded(GstApplicability))
            {
                return PurchaseTreatmentPolicy.ShownInTwoB;
            }

            return GstApplicability;
        }
        set => GstApplicability = value ?? string.Empty;
    }

    [JsonIgnore]
    public IReadOnlyList<string> AccountingGstTreatmentOptions
    {
        get
        {
            if (IsTwoBOnlyEvidence &&
                !Destination.Equals("2B Only", StringComparison.OrdinalIgnoreCase) &&
                !IsRcmRow)
            {
                return PurchaseTreatmentPolicy.GetAllowedPurchaseTreatments(this);
            }

            return GstTreatmentOptions;
        }
    }

    [JsonIgnore]
    public string ReconciliationGstApplicability =>
        IsTwoBOnlyEvidence
            ? Destination.Equals("2B Only", StringComparison.OrdinalIgnoreCase)
                ? PurchaseTreatmentPolicy.NotIncludedInPurchase
                : PurchaseTreatmentPolicy.IncludedInPurchase
            : GstApplicability;

    [JsonIgnore]
    public IReadOnlyList<string> ReconciliationGstTreatmentOptions =>
        IsTwoBOnlyEvidence
            ? new[]
            {
                PurchaseTreatmentPolicy.NotIncludedInPurchase,
                PurchaseTreatmentPolicy.IncludedInPurchase
            }
            : GstTreatmentOptions;

    public string SupplierGstin { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public DateTime? DocumentDate { get; set; }
    public DateTime? GstDate { get; set; }
    public decimal SourceDocumentValue { get; set; }
    public decimal DocumentValue { get; set; }
    public decimal Rate
    {
        get => _rate;
        set
        {
            if (!SetField(ref _rate, value)) return;
            OnPropertyChanged(nameof(RateText));
        }
    }

    /// <summary>
    /// False only when a source (normally GSTR-2B) did not carry a usable rate and
    /// strict reverse calculation could not prove one. A genuine explicit 0% rate
    /// remains RateKnown=true and therefore displays as 0 instead of blank.
    /// </summary>
    public bool RateKnown
    {
        get => _rateKnown;
        set
        {
            if (!SetField(ref _rateKnown, value)) return;
            OnPropertyChanged(nameof(RateText));
        }
    }

    [JsonIgnore]
    public string RateText
    {
        get => RateKnown
            ? Rate.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        set
        {
            var text = (value ?? string.Empty).Trim().Replace(",", string.Empty, StringComparison.Ordinal);
            if (text.Length == 0)
            {
                RateKnown = false;
                Rate = 0m;
                return;
            }

            if (!decimal.TryParse(
                    text,
                    System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return;
            }

            Rate = parsed;
            RateKnown = true;
        }
    }
    public decimal TaxableValue { get; set; }
    public decimal Igst { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Cess { get; set; }
    public decimal RoundOff { get; set; }

    public string PurchaseLedger
    {
        get => _purchaseLedger;
        set => SetField(ref _purchaseLedger, value ?? string.Empty);
    }

    public string IgstLedger { get; set; } = string.Empty;
    public string CgstLedger { get; set; } = string.Empty;
    public string SgstLedger { get; set; } = string.Empty;

    // Fix94: Purchase/Debit Note inventory parity with Sales. These fields are optional,
    // so GST-return-only Purchase rows continue to work as accounting-only vouchers.
    public string ItemName { get; set; } = string.Empty;
    public string HsnCode { get; set; } = string.Empty;
    public decimal ActualQuantity { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal ItemRate { get; set; }
    public decimal BilledQuantity { get; set; }
    public decimal ItemAmount { get; set; }
    public string ItemPurchaseLedger { get; set; } = string.Empty;
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
    public string Narration { get; set; } = string.Empty;

    // Keep original import values so unit conversion is reversible and never compounds.
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

    [JsonIgnore]
    public bool HasInventory =>
        ItemName.Trim().Length > 0 &&
        (Math.Abs(ActualQuantity) > 0.000001m || Math.Abs(BilledQuantity) > 0.000001m) &&
        Unit.Trim().Length > 0 &&
        (ItemPurchaseLedger.Trim().Length > 0 || PurchaseLedger.Trim().Length > 0) &&
        (Math.Abs(ItemAmount) > 0.005m || Math.Abs(TaxableValue) > 0.005m);

    public string SourceSection { get; set; } = string.Empty;
    public bool IsAmendment { get; set; }
    public string OriginalDocumentNumber { get; set; } = string.Empty;
    public DateTime? OriginalDocumentDate { get; set; }
    public string PlaceOfSupply { get; set; } = string.Empty;
    public string ReverseCharge { get; set; } = string.Empty;
    public string InvoiceType { get; set; } = string.Empty;
    public string ItcAvailability { get; set; } = string.Empty;
    public string Source2AFile { get; set; } = string.Empty;
    public string Source2BFile { get; set; } = string.Empty;
    public decimal? TwoBInvoiceValue { get; set; }
    public decimal? TwoBTaxableValue { get; set; }
    public decimal? TwoBIgst { get; set; }
    public decimal? TwoBCgst { get; set; }
    public decimal? TwoBSgst { get; set; }
    public decimal? DifferenceInvoiceValue { get; set; }
    public decimal? DifferenceTaxableValue { get; set; }
    public decimal? DifferenceIgst { get; set; }
    public decimal? DifferenceCgst { get; set; }
    public decimal? DifferenceSgst { get; set; }
    public string DifferenceDetails { get; set; } = string.Empty;
    public string ClientGstin { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string ImportMode { get; set; } = string.Empty;
    public string TwoAReturnPeriod { get; set; } = string.Empty;
    public string TwoBReturnPeriod { get; set; } = string.Empty;
    public string ImsStatus { get; set; } = string.Empty;
    public string TwoBReason { get; set; } = string.Empty;
    public string Source2APath { get; set; } = string.Empty;
    public string Source2BPath { get; set; } = string.Empty;
    public string TwoBAcceptance { get; set; } = string.Empty;
    public int ItemSequence { get; set; }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
