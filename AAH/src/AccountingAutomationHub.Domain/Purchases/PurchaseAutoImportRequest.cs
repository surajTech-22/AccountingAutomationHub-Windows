namespace AccountingAutomationHub.Domain.Purchases;

public sealed class PurchaseAutoImportRequest
{
    public long ClientId { get; init; }

    public required string ClientGstin { get; init; }

    public required string FinancialYear { get; init; }

    public string CompanyName { get; init; } = string.Empty;

    public required string EReturnsRootPath { get; init; }

    public bool SearchWindowsDownloads { get; init; } = true;

    // Reserved for the user's later source-file storage plan.
    // Version 2.5.0 never moves, copies, renames, deletes or extracts source files.
    public bool SaveSourceFiles { get; init; }
}
