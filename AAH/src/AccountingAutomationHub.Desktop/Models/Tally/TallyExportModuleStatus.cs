namespace AccountingAutomationHub.Desktop.Models.Tally;

public sealed class TallyExportModuleStatus
{
    public string Module { get; init; } = string.Empty;
    public int TotalCount { get; init; }
    public int SelectedCount { get; init; }
    public int ReadyCount { get; init; }
    public int ImportedCount { get; init; }
    public bool IsMasterStatus { get; init; }
    public bool IsAutoImportEnabled { get; init; }
    public bool ShowReadyCount => !IsMasterStatus;
    public bool ShowImportedCount => !IsMasterStatus;
    public string State { get; init; } = "Neutral";
    public string StatusState => State;
    public string Detail { get; init; } = string.Empty;
}
