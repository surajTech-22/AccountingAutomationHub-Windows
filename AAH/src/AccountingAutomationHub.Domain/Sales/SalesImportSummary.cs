namespace AccountingAutomationHub.Domain.Sales;

public sealed class SalesImportSummary
{
    public int SalesRows { get; set; }
    public int CreditNoteRows { get; set; }
    public int B2BRows { get; set; }
    public int B2CRows { get; set; }
    public int ExportRows { get; set; }
    public int AmendmentRows { get; set; }
    public int InvalidRowsSkipped { get; set; }

    public int TotalOutputRows =>
        SalesRows + CreditNoteRows;
}
