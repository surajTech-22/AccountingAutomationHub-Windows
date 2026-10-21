namespace AccountingAutomationHub.Domain.Masters;

public sealed class MasterImportResult
{
    public string SourcePath { get; set; } = string.Empty;
    public string SourceFormat { get; set; } = string.Empty;
    public int ParsedObjects { get; set; }
    public int ImportedObjects { get; set; }
    public int DuplicateObjects { get; set; }
    public int ConflictingObjects { get; set; }
    public int IgnoredObjects { get; set; }
    public IReadOnlyList<string> Warnings { get; set; } = [];
    public IReadOnlyList<MasterRecord> Records { get; set; } = [];
}
