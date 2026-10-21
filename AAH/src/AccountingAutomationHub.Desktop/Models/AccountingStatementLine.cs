namespace AccountingAutomationHub.Desktop.Models;

public sealed class AccountingStatementLine
{
    public string Label { get; init; } = string.Empty;
    public string RowType { get; init; } = "Ledger";
    public int IndentLevel { get; init; }
    public double IndentPixels => Math.Max(0, IndentLevel) * 16d;
    public decimal OpeningBalance { get; init; }
    public decimal TotalDebit { get; init; }
    public decimal TotalCredit { get; init; }
    public decimal ClosingBalance { get; init; }
    public int MovementCount { get; init; }

    public string OpeningBalanceText => FormatSignedBalance(OpeningBalance);
    public string ClosingBalanceText => FormatSignedBalance(ClosingBalance);
    public string DebitText => Math.Abs(TotalDebit) < 0.005m ? string.Empty : TotalDebit.ToString("N2");
    public string CreditText => Math.Abs(TotalCredit) < 0.005m ? string.Empty : TotalCredit.ToString("N2");
    public string EntriesText => MovementCount <= 0 ? string.Empty : MovementCount.ToString("N0");

    private static string FormatSignedBalance(decimal value)
    {
        if (Math.Abs(value) < 0.005m) return string.Empty;
        return $"{Math.Abs(value):N2} {(value > 0m ? "Dr" : "Cr")}";
    }
}
