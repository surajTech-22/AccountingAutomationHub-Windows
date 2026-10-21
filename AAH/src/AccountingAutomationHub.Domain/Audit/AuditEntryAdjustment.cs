namespace AccountingAutomationHub.Domain.Audit;

public sealed class AuditEntryAdjustment
{
    public long Id { get; set; }
    public long AuditClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public long SourceRecordId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string SourceIdentity { get; set; } = string.Empty;
    public string TallyMasterId { get; set; } = string.Empty;
    public string TallyGuid { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Status { get; set; } = "Suggested";
    public string OriginalJson { get; set; } = string.Empty;
    public string ProposedJson { get; set; } = string.Empty;
    public string Failure { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
