namespace AccountingAutomationHub.Domain.Imports;

public sealed class ImportActivity
{
    public long Id { get; set; }

    public long ClientId { get; set; }

    public string ImportType { get; set; } = string.Empty;

    public DateTime? PeriodFrom { get; set; }

    public DateTime? PeriodTo { get; set; }

    public string PeriodLabel { get; set; } = string.Empty;

    public string FilingStatus { get; set; } = string.Empty;

    public int RecordCount { get; set; }

    public int BankCount { get; set; }

    public string SourceSummary { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;

    public DateTime ImportedUtc { get; set; }
}
