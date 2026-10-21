namespace AccountingAutomationHub.Domain.Tally;

public sealed class TallyVoucherIndexRecord
{
    public string CompanyKey { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public string MasterId { get; set; } = string.Empty;
    public string AlterId { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public DateTime? VoucherDate { get; set; }
    public string VoucherType { get; set; } = string.Empty;
    public string VoucherNumber { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string PartyLedger { get; set; } = string.Empty;
    // Normalized live accounting sides captured from Tally voucher Ledger Entries.
    // These are used for Bank duplicate identity when Reference/PartyLedger are blank.
    public string DebitLedgers { get; set; } = string.Empty;
    public string CreditLedgers { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Narration { get; set; } = string.Empty;
    // Exact per-ledger postings are populated only from the current live Tally read.
    // They are deliberately not reconstructed from a voucher total because the
    // Bank conversion workflow must preserve the real Tally posting amount/direction.
    public IReadOnlyList<TallyLedgerPostingRecord> LedgerPostings { get; set; } = Array.Empty<TallyLedgerPostingRecord>();
    public string ContentHash { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
}
