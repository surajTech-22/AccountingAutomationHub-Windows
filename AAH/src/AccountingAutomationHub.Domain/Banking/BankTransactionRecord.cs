namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankTransactionRecord
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public long StatementId { get; set; }
    public long BankAccountId { get; set; }
    public string BankAccountName { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; }
    public DateTime? ValueDate { get; set; }
    public string Narration { get; set; } = string.Empty;
    public string NormalizedNarration { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal? RunningBalance { get; set; }
    // SOURCE/MANUAL are independent evidence; CALCULATED is display-only and must never
    // be promoted into statement balance authority on a later restart.
    public string RunningBalanceSource { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string LedgerName { get; set; } = string.Empty;
    public string MappingStatus { get; set; } = "Mapping Required";
    public string TransactionId { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string RecordStatus { get; set; } = "Active";
    public string MatchId { get; set; } = string.Empty;
    public long? MatchedTransactionId { get; set; }
    public string SourceFile { get; set; } = string.Empty;
    public string SourceSheet { get; set; } = string.Empty;
    public int SourceRow { get; set; }
    public DateTime ImportedUtc { get; set; }

    public decimal Amount => Debit > 0 ? Debit : Credit;
}
