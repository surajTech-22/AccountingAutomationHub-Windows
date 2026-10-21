namespace AccountingAutomationHub.Desktop.Models;

public sealed class DashboardAutoImportFileRow
{
    public DateTime ProcessedLocal { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string Module { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int RecordCount { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
}
