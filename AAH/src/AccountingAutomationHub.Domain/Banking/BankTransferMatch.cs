namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankTransferMatch
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public long SourceTransactionId { get; set; }
    public long TargetTransactionId { get; set; }
    public string MatchId { get; set; } = string.Empty;
    public DateTime MatchDate { get; set; }
    public decimal Amount { get; set; }
    public decimal ChargeAmount { get; set; }
    public string ChargeRule { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Matched";
    public string Reason { get; set; } = string.Empty;
    // Persisted for Tally-converted Inter-Bank matches so Transfer Matches, Contra Debug
    // and the workbook Inter-Bank report all consume the same durable match authority.
    public string MatchOrigin { get; set; } = "Bank Statement";
    public string TallyConversionId { get; set; } = string.Empty;
    public string TallyMasterId { get; set; } = string.Empty;
    public string TallyAlterId { get; set; } = string.Empty;
    public string TallyVoucherType { get; set; } = string.Empty;
    public string TallyVoucherNumber { get; set; } = string.Empty;
    public string TallyBankLedger { get; set; } = string.Empty;
    // Fix74: both financial-account sides are persisted by Ledger MASTERID so a rename,
    // merge display change or restart cannot break a finalized Tally <-> AAH Inter-Bank link.
    public string TallyBankLedgerMasterId { get; set; } = string.Empty;
    public string ImportedBankLedgerMasterId { get; set; } = string.Empty;
    public string SourceBankLedgerMasterId { get; set; } = string.Empty;
    public string TargetBankLedgerMasterId { get; set; } = string.Empty;
    public string TallyNarration { get; set; } = string.Empty;
    public string TallyReference { get; set; } = string.Empty;
    public decimal TallySignedAmount { get; set; }
    public bool TallyIsDebit { get; set; }
    public DateTime CreatedUtc { get; set; }
}
