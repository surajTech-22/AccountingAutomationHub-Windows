namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankReconciliationSummary
{
    public long StatementId { get; set; }
    public long BankAccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string PeriodLabel { get; set; } = string.Empty;

    // AAH/business-books convention for every balance: +Dr / -Cr.
    // Bank-statement Debit/Credit movement columns are deliberately exposed again below
    // as books movement sides because a statement Credit is a books Debit and vice versa.
    public decimal? OpeningBalance { get; set; }
    public decimal? SuggestedOpeningBalance { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public decimal? ExpectedClosingBalance { get; set; }
    public decimal? StatementClosingBalance { get; set; }
    public decimal? Difference { get; set; }
    public string Status { get; set; } = string.Empty;

    public string OpeningBalanceText => BankBusinessBalance.Format(OpeningBalance);
    public string SuggestedOpeningBalanceText => BankBusinessBalance.Format(SuggestedOpeningBalance);
    public string ExpectedClosingBalanceText => BankBusinessBalance.Format(ExpectedClosingBalance);
    public string StatementClosingBalanceText => BankBusinessBalance.Format(StatementClosingBalance);
    public decimal BooksDebitMovements => TotalCredit;
    public decimal BooksCreditMovements => TotalDebit;
}
