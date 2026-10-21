namespace AccountingAutomationHub.Domain.Tally;

public sealed class TallyMasterSyncResult
{
    public int ParsedObjects { get; set; }
    public int UniqueObjects { get; set; }
    public int DuplicateObjects { get; set; }
    public int ConflictingObjects { get; set; }
    public int UpsertedObjects { get; set; }
    public int ArchivedObjects { get; set; }
    public int GroupCount { get; set; }
    public int LedgerCount { get; set; }
    public int VoucherTypeCount { get; set; }
    public int StockItemCount { get; set; }
    public int UnitCount { get; set; }
    public int GodownCount { get; set; }
    public string Status { get; set; } = string.Empty;
}
