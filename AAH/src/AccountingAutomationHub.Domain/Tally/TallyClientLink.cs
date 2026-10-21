namespace AccountingAutomationHub.Domain.Tally;

public sealed class TallyClientLink
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string ClientCode { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string CompanyKey { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string CompanyNumber { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string StateName { get; set; } = string.Empty;
    public string BooksFrom { get; set; } = string.Empty;
    public string StartingFrom { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public DateTime LinkedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime LastMasterSyncUtc { get; set; }
    public DateTime LastReportRefreshUtc { get; set; }
    public string LastChangeToken { get; set; } = string.Empty;
    public int GroupCount { get; set; }
    public int LedgerCount { get; set; }
    public int VoucherTypeCount { get; set; }
    public int StockItemCount { get; set; }
    public int UnitCount { get; set; }
    public int GodownCount { get; set; }
    public int VoucherCount { get; set; }
    public int MasterCount { get; set; }
    public int DuplicateMasterCount { get; set; }
    public int ConflictingMasterCount { get; set; }
    public int DuplicateVoucherCount { get; set; }
    public int ConflictingVoucherCount { get; set; }
    public DateTime? BankImportFrom { get; set; }
    public DateTime? BankImportTo { get; set; }
    public string LastSyncStatus { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
}
