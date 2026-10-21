namespace AccountingAutomationHub.Domain.Tally;

/// <summary>
/// One exact ledger posting returned inside a live Tally voucher.
/// SignedAmount preserves Tally's native sign. IsDebit is derived from
/// ISDEEMEDPOSITIVE when supplied, otherwise from the signed amount.
/// </summary>
public sealed class TallyLedgerPostingRecord
{
    public int Sequence { get; set; }
    public string LedgerName { get; set; } = string.Empty;
    public decimal SignedAmount { get; set; }
    public bool IsDebit { get; set; }
    public string Narration { get; set; } = string.Empty;
    public decimal Amount => Math.Abs(SignedAmount);
}
