namespace AccountingAutomationHub.Domain.Tally;

/// <summary>
/// Durable linked-company Tally authority checkpoint plus the independent Bank input token.
/// FIXED41 makes the 5-second Tally coordinator the sole owner of company ALTERID,
/// Master/Voucher counts and baseline timestamps; Bank reconciliation may update only
/// its Bank input state and must never consume a company ALTERID on behalf of authority sync.
/// </summary>
public sealed class TallyBankSyncState
{
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string CompanyKey { get; set; } = string.Empty;
    public string LastTallyChangeToken { get; set; } = string.Empty;
    public int LastVoucherCount { get; set; }
    // FIXED41: active-FY Voucher authority has its own durable count. $$NumVouchers is
    // company-wide and cannot prove that this FY cache is complete, especially when the
    // same Tally company contains previous years. -1 means a legacy checkpoint that has
    // not yet proved the active-FY Voucher population.
    public int LastFyVoucherCount { get; set; } = -1;
    public long LastAlterId { get; set; }
    public string LastBankInputToken { get; set; } = string.Empty;
    // Fix125: independent proof that live Stock Master authority has been checked.
    // ERP 9 can report an unknown/zero lightweight StockItem count even when inventory
    // masters exist, so Ledger/Voucher baseline state cannot stand in for this.
    public int LastStockItemCount { get; set; }
    public DateTime LastStockAuthorityUtc { get; set; }
    public DateTime LastFullSyncUtc { get; set; }
    public DateTime LastIncrementalSyncUtc { get; set; }
    public string LastStatus { get; set; } = string.Empty;
}
