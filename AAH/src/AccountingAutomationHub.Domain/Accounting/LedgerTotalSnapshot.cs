namespace AccountingAutomationHub.Domain.Accounting;

/// <summary>
/// Read-only ledger movement/balance projection for one client and financial year.
/// OpeningBalance and balance values follow the Hub/Tally sign convention:
/// positive = Debit balance, negative = Credit balance.
/// </summary>
public sealed class LedgerTotalSnapshot
{
    public long ClientId { get; init; }
    public string FinancialYear { get; init; } = string.Empty;
    public string LedgerName { get; init; } = string.Empty;
    public string ParentGroup { get; init; } = string.Empty;
    public decimal OpeningBalance { get; init; }
    public decimal TotalDebit { get; init; }
    public decimal TotalCredit { get; init; }
    public decimal ClosingBalance { get; init; }
    public DateTime AsOnDate { get; init; }
    public decimal DebitToDate { get; init; }
    public decimal CreditToDate { get; init; }
    public decimal BalanceAsOnDate { get; init; }
    public int MovementCount { get; init; }
    public int MovementCountToDate { get; init; }
}
