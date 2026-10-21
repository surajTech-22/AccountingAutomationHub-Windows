namespace AccountingAutomationHub.Domain.Gst;

public sealed class Gstr3bPaymentRow
{
    public string Section { get; set; } = string.Empty;
    public string TaxHead { get; set; } = string.Empty;
    public decimal TaxPayable { get; set; }
    public decimal NegativeLiabilityAdjustment { get; set; }
    public decimal NetTaxPayable { get; set; }
    public decimal ItcIntegrated { get; set; }
    public decimal ItcCentral { get; set; }
    public decimal ItcState { get; set; }
    public decimal ItcCess { get; set; }
    public decimal PaidCash { get; set; }
    public decimal InterestPaidCash { get; set; }
    public decimal LateFeePaidCash { get; set; }

    public decimal ItcTotal =>
        ItcIntegrated + ItcCentral + ItcState + ItcCess;

    public decimal OffsetTotal =>
        ItcTotal + PaidCash + InterestPaidCash + LateFeePaidCash;
}
