namespace AccountingAutomationHub.Domain.Sales;

public sealed class SalesSourceVerification
{
    public string EReturnsRoot { get; set; } = string.Empty;
    public string SourceOrigin { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string ClientRoot { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public IReadOnlyList<string> SelectedSources { get; set; } = [];
    public IReadOnlyList<string> SelectedPeriods { get; set; } = [];
    public string CustomerMasterStatus { get; set; } = string.Empty;
    public string CustomerMasterSourceOrigin { get; set; } = string.Empty;
    public string CustomerMasterSourceBranch { get; set; } = string.Empty;
    public string CustomerMasterSourcePath { get; set; } = string.Empty;
    public string CustomerMasterSourceHash { get; set; } = string.Empty;
    public int CustomerMasterRecords { get; set; }
}
