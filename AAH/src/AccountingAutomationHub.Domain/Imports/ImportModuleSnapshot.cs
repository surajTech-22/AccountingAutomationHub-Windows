namespace AccountingAutomationHub.Domain.Imports;

public sealed class ImportModuleSnapshot
{
    public string ImportType { get; init; } = string.Empty;

    public bool IsImported { get; init; }

    public string Status { get; init; } = "Not imported";

    public string PeriodLabel { get; init; } = "No period imported";

    public string FilingStatus { get; init; } = "Not checked";

    public int RecordCount { get; init; }

    public int BankCount { get; init; }

    public DateTime? LastImportedUtc { get; init; }

    public string Details { get; init; } = string.Empty;
}
