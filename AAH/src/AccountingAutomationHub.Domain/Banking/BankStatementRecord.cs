namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankStatementRecord
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public long BankAccountId { get; set; }
    public string SourceFile { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceSheet { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public decimal? OpeningBalance { get; set; }
    public string OpeningBalanceSource { get; set; } = string.Empty;
    // Immutable source-statement balances. OpeningBalance may later be verified/overridden,
    // but these preserve what was actually detected from the imported statement.
    public decimal? SourceOpeningBalance { get; set; }
    public decimal? SourceClosingBalance { get; set; }
    // Explicit provenance prevents legacy calculated ClosingBalance values from being
    // promoted to source evidence during schema migration.
    public string ClosingBalanceSource { get; set; } = string.Empty;
    public decimal? ClosingBalance { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public int TransactionCount { get; set; }
    public string ReconciliationStatus { get; set; } = string.Empty;
    public decimal Difference { get; set; }
    public double OpenSeconds { get; set; }
    public double ProcessSeconds { get; set; }
    public DateTime ImportedUtc { get; set; }
}
