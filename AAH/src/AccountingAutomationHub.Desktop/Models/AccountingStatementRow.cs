namespace AccountingAutomationHub.Desktop.Models;

public sealed class AccountingStatementRow
{
    public string RootGroup { get; init; } = string.Empty;
    public string ParentGroup { get; init; } = string.Empty;
    public string LedgerName { get; init; } = string.Empty;
    public decimal OpeningBalance { get; init; }
    public decimal TotalDebit { get; init; }
    public decimal TotalCredit { get; init; }
    public decimal ClosingBalance { get; init; }
    public int MovementCount { get; init; }

    public string OpeningBalanceText => FormatBalance(OpeningBalance);
    public string ClosingBalanceText => FormatBalance(ClosingBalance);

    private static string FormatBalance(decimal value)
    {
        if (Math.Abs(value) < 0.005m) return "0.00";
        return $"{Math.Abs(value):N2} {(value > 0m ? "Dr" : "Cr")}";
    }
}
