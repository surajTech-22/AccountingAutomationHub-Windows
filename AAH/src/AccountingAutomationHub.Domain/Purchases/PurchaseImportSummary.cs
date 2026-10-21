namespace AccountingAutomationHub.Domain.Purchases;

public sealed class PurchaseImportSummary
{
    public int Source2ARows { get; init; }
    public int Source2BRows { get; init; }
    public int PurchaseRows { get; init; }
    public int RcmRows { get; init; }
    public int DebitNoteRows { get; init; }
    public int DebitNoteReverseRows { get; init; }
    public int MatchedRows { get; init; }
    public int TwoAOnlyRows { get; init; }
    public int TwoBOnlyRows { get; init; }
    public int DifferenceRows { get; init; }
    public int DuplicateRowsSkipped { get; init; }
    public int InvalidRowsSkipped { get; init; }

    public int TotalOutputRows =>
        PurchaseRows + RcmRows + DebitNoteRows + DebitNoteReverseRows;
}
