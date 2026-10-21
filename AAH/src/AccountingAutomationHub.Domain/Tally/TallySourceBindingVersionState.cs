namespace AccountingAutomationHub.Domain.Tally;

/// <summary>
/// Durable AAH -> Tally object version authority. VerifiedVersion is the last state that
/// AAH proved live in Tally. PendingVersion is the desired next state and is never promoted
/// until the same Tally MASTERID has been read back and verified after the write.
/// </summary>
public sealed class TallySourceBindingVersionState
{
    public string MasterId { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public string VerifiedContentHash { get; set; } = string.Empty;
    public string PendingContentHash { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string VerifiedVersion { get; set; } = string.Empty;
    public string PendingVersion { get; set; } = string.Empty;
    public long LastVerifiedAlterId { get; set; }
    public DateTime LastVerifiedUtc { get; set; }
}
