namespace AccountingAutomationHub.Infrastructure.Audit;

public sealed class AuditTallySyncState
{
    public long AuditClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string CompanyKey { get; set; } = string.Empty;
    public string LastChangeToken { get; set; } = string.Empty;
    public int LastVoucherCount { get; set; }
    public int LastLedgerCount { get; set; }
    public int LastGroupCount { get; set; }
    public long LastAlterId { get; set; }
    public DateTime LastFullSyncUtc { get; set; }
    public DateTime LastDeltaSyncUtc { get; set; }
    public DateTime LastPulseUtc { get; set; }
    public string LastStatus { get; set; } = string.Empty;
}
