namespace AccountingAutomationHub.Domain.Purchases;

public sealed class PurchaseImportResult
{
    public string CompanyName { get; init; } = string.Empty;
    public string ClientGstin { get; init; } = string.Empty;
    public string FinancialYear { get; init; } = string.Empty;
    public required IReadOnlyList<PurchaseImportRecord> Records { get; init; }
    public required IReadOnlyList<PurchaseImportIssue> Issues { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required PurchaseImportSummary Summary { get; init; }
}
