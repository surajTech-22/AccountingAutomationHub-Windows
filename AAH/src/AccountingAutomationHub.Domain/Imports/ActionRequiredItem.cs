namespace AccountingAutomationHub.Domain.Imports;

public sealed class ActionRequiredItem
{
    public long PersistentId { get; set; }
    public long ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
