namespace AccountingAutomationHub.Domain.Purchases;

public sealed class PurchaseImportIssue
{
    public string SourceFile { get; init; } = string.Empty;
    public string SheetName { get; init; } = string.Empty;
    public int RowNumber { get; init; }
    public string Message { get; init; } = string.Empty;
}
