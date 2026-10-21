namespace AccountingAutomationHub.Domain.Accounting;

/// <summary>
/// Read-only transaction line for a proper ledger account presentation.
/// Balance follows the Hub/Tally sign convention: positive = Dr, negative = Cr.
/// </summary>
public sealed class LedgerMovementDetail
{
    public DateTime Date { get; init; }
    public string VoucherType { get; init; } = string.Empty;
    public string VoucherNumber { get; init; } = string.Empty;
    public string ReferenceNumber { get; init; } = string.Empty;
    public string VoucherOrReference => VoucherNumber.Trim().Length > 0 ? VoucherNumber : ReferenceNumber;
    public string Particulars { get; init; } = string.Empty;
    public string Narration { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public decimal Debit { get; init; }
    public decimal Credit { get; init; }
    public decimal RunningBalance { get; init; }
    public string RunningBalanceText => Math.Abs(RunningBalance) < 0.005m ? "0.00" : $"{Math.Abs(RunningBalance):N2} {(RunningBalance > 0m ? "Dr" : "Cr")}";
}
