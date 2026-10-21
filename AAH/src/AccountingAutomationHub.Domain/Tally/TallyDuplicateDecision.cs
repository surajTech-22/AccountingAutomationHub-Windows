namespace AccountingAutomationHub.Domain.Tally;

public enum TallyDuplicateKind
{
    New,
    ExactDuplicate,
    Conflict
}

public sealed class TallyDuplicateDecision
{
    public TallyDuplicateKind Kind { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string ExistingHash { get; set; } = string.Empty;
    public string IncomingHash { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
