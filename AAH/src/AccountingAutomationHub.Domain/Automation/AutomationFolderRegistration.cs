namespace AccountingAutomationHub.Domain.Automation;

public sealed class AutomationFolderRegistration
{
    public long PersistentId { get; set; }
    public string FolderId { get; set; } = string.Empty;
    public long ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public bool IsPaused { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime LastImportUtc { get; set; }
    public string LastStatus { get; set; } = string.Empty;
}

public sealed class AutomationFileHistoryRecord
{
    public long PersistentId { get; set; }
    public string FolderId { get; set; } = string.Empty;
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public int RecordCount { get; set; }
    public long FileSize { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public DateTime ProcessedUtc { get; set; }
}
