namespace AccountingAutomationHub.Domain.Tally;

public sealed class TallyBankStatementLinkRecord
{
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string CompanyKey { get; set; } = string.Empty;
    public string TransferMatchId { get; set; } = string.Empty;
    public long SourceTransactionId { get; set; }
    public long TargetTransactionId { get; set; }
    public DateTime TransactionDate { get; set; }
    public string TallyMasterId { get; set; } = string.Empty;
    public string TallyAlterId { get; set; } = string.Empty;
    public int PostingSequence { get; set; }
    public string ConvertedBankLedgerMasterId { get; set; } = string.Empty;
    public string ConvertedBankLedgerGuid { get; set; } = string.Empty;
    public long ConvertedBankLedgerAlterId { get; set; }
    public string SourceBankLedgerMasterId { get; set; } = string.Empty;
    public string TargetBankLedgerMasterId { get; set; } = string.Empty;
    public string OriginalVoucherType { get; set; } = string.Empty;
    public string OriginalReference { get; set; } = string.Empty;
    public string OriginalNarration { get; set; } = string.Empty;
    public string OriginalDebitLedgers { get; set; } = string.Empty;
    public string OriginalCreditLedgers { get; set; } = string.Empty;
    public decimal OriginalAmount { get; set; }
    // Completion 31 projection for exact Tally Bank-transaction conversion rows. The durable
    // schema still stores the converted Bank ledger in OriginalDebitLedgers or
    // OriginalCreditLedgers plus one OriginalAmount; these properties expose a
    // normal Bank-transaction view while TransactionDate is persisted separately.
    public string ConvertedBankLedger => OriginalDebitLedgers.Length > 0 ? OriginalDebitLedgers : OriginalCreditLedgers;
    public decimal ConvertedDebitAmount => OriginalDebitLedgers.Length > 0 ? OriginalAmount : 0m;
    public decimal ConvertedCreditAmount => OriginalCreditLedgers.Length > 0 ? OriginalAmount : 0m;
    public string FinalVoucherType { get; set; } = string.Empty;
    public string FinalDebitLedger { get; set; } = string.Empty;
    public string FinalCreditLedger { get; set; } = string.Empty;
    public string MatchEvidence { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
