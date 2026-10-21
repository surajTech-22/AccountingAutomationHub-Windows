namespace AccountingAutomationHub.Desktop.Models;

public sealed class DashboardMonthActivityRow
{
    public string PeriodKey { get; init; } = string.Empty;
    public string Month { get; init; } = string.Empty;
    public int BankTransactions { get; init; }
    public int PurchaseTransactions { get; init; }
    public int SalesTransactions { get; init; }
    public int JournalVouchers { get; init; }
    public int CashEntries { get; init; }
    public string Gstr3bMatchStatus { get; init; } = "Not available";
}
