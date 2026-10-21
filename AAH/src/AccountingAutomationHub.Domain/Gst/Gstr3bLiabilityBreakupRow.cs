namespace AccountingAutomationHub.Domain.Gst;

public sealed class Gstr3bLiabilityBreakupRow
{
    public string Period { get; set; } = string.Empty;
    public decimal Igst { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Cess { get; set; }
    public decimal Total => Igst + Cgst + Sgst + Cess;
}
