namespace AccountingAutomationHub.Domain.Sales;

public sealed class SalesSourceManifestItem
{
    public string FileType { get; set; } = string.Empty;
    public string Usage { get; set; } = string.Empty;
    public string ReturnPeriod { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string InternalJsonName { get; set; } = string.Empty;
    public string SourceOrigin { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string SourceScope { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = string.Empty;
    public string EmbeddedTimestamp { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public bool ReadOnlySource { get; set; } = true;
}
