namespace AccountingAutomationHub.Domain.Tally;

public sealed class TallyVoucherChangeMarker
{
    public string Token { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public int VoucherCount { get; set; }
    public int LedgerCount { get; set; }
    public int GroupCount { get; set; }
    public int VoucherTypeCount { get; set; }
    public int StockItemCount { get; set; }
    public int UnitCount { get; set; }
    public int GodownCount { get; set; }
    public long LastAlterId { get; set; }
    public bool HasReliableAlterId { get; set; }
}
