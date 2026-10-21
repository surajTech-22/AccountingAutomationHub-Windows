namespace AccountingAutomationHub.Desktop.Services;

public sealed class AutomationHealthSnapshot
{
    public string FolderId { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public bool FolderAvailable { get; set; }
    public bool WatcherActive { get; set; }
    public bool FolderPaused { get; set; }
    public int ActiveFileCount { get; set; }
    public int QueuedFileCount { get; set; }
    public string CurrentStage { get; set; } = string.Empty;
    public string CurrentFileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CheckedUtc { get; set; }
}

public sealed record AutomationRuntimeHealth(
    bool AutomationEnabled,
    bool GloballyPaused,
    int RegisteredFolders,
    int AvailableFolders,
    int ActiveWatchers,
    int QueuedFiles,
    bool PdfRuntimeAvailable,
    string PdfRuntimePath,
    string Summary);
