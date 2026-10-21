namespace AccountingAutomationHub.Desktop.Models;

public sealed class AccountingReportRow
{
    public DateTime? Date { get; init; }
    public string VoucherType { get; init; } = string.Empty;
    public string VoucherNumber { get; init; } = string.Empty;
    public string Particulars { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public decimal Opening { get; init; }
    public decimal Debit { get; init; }
    public decimal Credit { get; init; }
    public decimal Amount { get; init; }
    public decimal Closing { get; init; }
    public decimal Quantity { get; init; }
    public int Count { get; init; }
}
