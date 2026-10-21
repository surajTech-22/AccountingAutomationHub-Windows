namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankBalanceGateResult
{
    public bool IsExact { get; set; }
    public int StatementsChecked { get; set; }
    public int StatementsMissingBalances { get; set; }
    public decimal MaximumDifference { get; set; }
    public string Detail { get; set; } = string.Empty;
}
