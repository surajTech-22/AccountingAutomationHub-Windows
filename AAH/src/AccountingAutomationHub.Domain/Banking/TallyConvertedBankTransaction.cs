namespace AccountingAutomationHub.Domain.Banking;

/// <summary>
/// Bank-Statement-shaped transaction converted from one live Tally
/// Bank/OD/Secured-Loan ledger posting. It is never inserted into BankTransactions; its stable ConversionId is persisted in Tally audit storage.
/// Tally accounting direction is reversed exactly once so the result follows the
/// Bank Statement convention used by AAH: Tally Bank DR => Statement Credit,
/// Tally Bank CR => Statement Debit.
/// </summary>
public sealed class TallyConvertedBankTransaction
{
    public string ConversionId { get; set; } = string.Empty;
    public string CompanyKey { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public string TallyMasterId { get; set; } = string.Empty;
    public string TallyAlterId { get; set; } = string.Empty;
    public string TallyGuid { get; set; } = string.Empty;
    public string VoucherType { get; set; } = string.Empty;
    public string VoucherNumber { get; set; } = string.Empty;
    public int PostingSequence { get; set; }
    public string BankLedgerName { get; set; } = string.Empty;
    // Fix74: exact financial Ledger identity resolved from the already-synchronized Master layer.
    public string BankLedgerMasterId { get; set; } = string.Empty;
    public string BankLedgerGuid { get; set; } = string.Empty;
    public long BankLedgerAlterId { get; set; }
    public DateTime TransactionDate { get; set; }
    public string Narration { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal TallySignedAmount { get; set; }
    public bool TallyIsDebit { get; set; }
    public decimal Amount => Debit > 0m ? Debit : Credit;
    public string Mode => Debit > 0m ? "Payment" : "Receipt";
    public string TallySide => TallyIsDebit ? "Debit" : "Credit";
}
