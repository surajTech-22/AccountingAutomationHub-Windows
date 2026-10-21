namespace AccountingAutomationHub.Desktop.Models;

public sealed record DashboardRecentImport(
    string ImportType,
    string Period,
    string FilingStatus,
    string CountText,
    string ImportedText,
    string Status);
