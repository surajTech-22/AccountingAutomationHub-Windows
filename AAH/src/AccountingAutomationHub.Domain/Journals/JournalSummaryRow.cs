namespace AccountingAutomationHub.Domain.Journals;

public sealed class JournalSummaryRow
{
    public string PeriodKey { get; set; } = string.Empty;
    public string Particulars { get; set; } = string.Empty;
    public decimal OutputGst { get; set; }
    public decimal InputGst { get; set; }
    public decimal RcmGst { get; set; }
    public decimal RcmClaimed { get; set; }
    public decimal RcmReversed { get; set; }
    public decimal NetGst { get; set; }
    public decimal ClosingBalance { get; set; }
    public decimal Difference { get; set; }
    public string EvidenceStatus { get; set; } = string.Empty;
}
