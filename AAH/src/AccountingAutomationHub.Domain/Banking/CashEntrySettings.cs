namespace AccountingAutomationHub.Domain.Banking;

public sealed class CashEntrySettings
{
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string LayoutMode { get; set; } = "Debit-Credit";
    public string DefaultCommonLedger { get; set; } = "Cash";
    public decimal OpeningBalance { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
