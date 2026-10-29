namespace AccountingAutomationHub.Domain.Gst;

public sealed class GstLedgerPeriodBalance
{
    public long PersistentId { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string LedgerType { get; set; } = string.Empty;
    public string PeriodKey { get; set; } = string.Empty;
    public string PeriodLabel { get; set; } = string.Empty;
    public decimal OpeningIgst { get; set; }
    public decimal OpeningCgst { get; set; }
    public decimal OpeningSgst { get; set; }
    public decimal OpeningCess { get; set; }
    public decimal ClosingIgst { get; set; }
    public decimal ClosingCgst { get; set; }
    public decimal ClosingSgst { get; set; }
    public decimal ClosingCess { get; set; }
    public decimal OpeningTotal =>
        OpeningIgst + OpeningCgst + OpeningSgst + OpeningCess;
    public decimal ClosingTotal =>
        ClosingIgst + ClosingCgst + ClosingSgst + ClosingCess;
    public string SourcePath { get; set; } = string.Empty;
    public DateTime ImportedUtc { get; set; }
}
