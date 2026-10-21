namespace AccountingAutomationHub.Domain.Tally;

public sealed class TallyImportFingerprintAuditRow
{
    public string EntityType { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string ResponseSummary { get; init; } = string.Empty;
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastSeenUtc { get; init; }
    public int SeenCount { get; init; }
}
