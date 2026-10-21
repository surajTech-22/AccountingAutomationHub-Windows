namespace AccountingAutomationHub.Domain.Purchases;

public sealed class PurchaseImportRequest
{
    public required string Gstr2AFilePath { get; init; }
    public string? Gstr2BFilePath { get; init; }
    public string? CompanyName { get; init; }
    public string? ClientGstin { get; init; }
}
