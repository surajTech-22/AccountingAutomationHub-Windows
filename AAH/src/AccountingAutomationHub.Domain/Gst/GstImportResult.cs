namespace AccountingAutomationHub.Domain.Gst;

public sealed class GstImportResult
{
    public string ImportType { get; set; } = string.Empty;
    public int FilesProcessed { get; set; }
    public int PeriodsImported { get; set; }
    public int PeriodsRejected { get; set; }
    public IReadOnlyList<string> Warnings { get; set; } = [];
}
