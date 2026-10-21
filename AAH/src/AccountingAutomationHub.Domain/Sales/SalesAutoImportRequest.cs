namespace AccountingAutomationHub.Domain.Sales;

public sealed class SalesAutoImportRequest
{
    public long ClientId { get; init; }
    public string ClientGstin { get; init; } = string.Empty;
    public string FinancialYear { get; init; } = string.Empty;
    public string CompanyName { get; init; } = string.Empty;
    public string EReturnsRootPath { get; init; } = string.Empty;
    public IReadOnlyList<string> AutomationFolderPaths { get; init; } = [];
    public bool SearchWindowsDownloads { get; init; } = true;
    public bool SaveSourceFiles { get; init; }
}
