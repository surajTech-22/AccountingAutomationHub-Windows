namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankLedgerMapping
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string BankFilter { get; set; } = string.Empty;
    public string VoucherType { get; set; } = string.Empty;
    public string SummaryName { get; set; } = string.Empty;
    public string NarrationPattern { get; set; } = string.Empty;
    /// <summary>Bank Statement ledger value used by the Excel-style Summary Name + current-ledger grouping key.</summary>
    public string SourceLedgerName { get; set; } = string.Empty;
    public string LedgerName { get; set; } = string.Empty;
    public string MasterGroup { get; set; } = string.Empty;
    public int MatchCount { get; set; }
    public int PaymentMatchCount { get; set; }
    public int ReceiptMatchCount { get; set; }
    public string Status { get; set; } = "Pending";
    /// <summary>Frozen Bank Statement transaction membership captured when Ledger Mapping is rebuilt, matching Excel's AA:AK cache.</summary>
    public List<long> TransactionIds { get; set; } = [];
    public DateTime UpdatedUtc { get; set; }
}
