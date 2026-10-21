namespace AccountingAutomationHub.Domain.Purchases;

public sealed class PurchaseSourceManifestItem
{
    public string FileType { get; init; } = string.Empty;

    public string Usage { get; init; } = string.Empty;

    public string ReturnPeriod { get; init; } = string.Empty;

    public string SourceFile { get; init; } = string.Empty;

    public string SourcePath { get; init; } = string.Empty;

    public string InternalJsonName { get; init; } = string.Empty;

    public string SourceOrigin { get; init; } = string.Empty;

    public string SourceBranch { get; init; } = string.Empty;

    public string SourceScope { get; init; } = string.Empty;

    public bool ReadOnlySource { get; init; }

    public string VerificationStatus { get; init; } = string.Empty;

    public string EmbeddedTimestamp { get; init; } = string.Empty;

    public string PayloadHash { get; init; } = string.Empty;
}
