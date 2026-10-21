namespace AccountingAutomationHub.Domain.Sales;

public sealed class SalesAutoImportResult
{
    public string CompanyName { get; set; } = string.Empty;
    public string ClientGstin { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public IReadOnlyList<SalesImportRecord> Records { get; set; } = [];
    public IReadOnlyList<SalesImportIssue> Issues { get; set; } = [];
    public IReadOnlyList<string> Warnings { get; set; } = [];
    public SalesImportSummary Summary { get; set; } = new();
    public SalesSourceVerification Verification { get; set; } = new();
    public IReadOnlyList<SalesSourceManifestItem> SourceManifest { get; set; } = [];
    public string SourceFingerprint { get; set; } = string.Empty;
    public int PersistedRows { get; set; }
}
