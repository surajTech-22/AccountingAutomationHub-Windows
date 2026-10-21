namespace AccountingAutomationHub.Domain.Sales;

public sealed class SalesImportIssue
{
    public string SourceFile { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
    public int RowNumber { get; set; }
    public string Message { get; set; } = string.Empty;
}
