namespace AccountingAutomationHub.Domain.Banking;

/// <summary>
/// Immutable audit row for every Bank Statement file import attempt. Duplicate files are
/// retained here even when no duplicate accounting transactions are inserted.
/// </summary>
public sealed class BankStatementImportAttemptRecord
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public long BankAccountId { get; set; }
    public long? StatementId { get; set; }
    public long? DuplicateOfStatementId { get; set; }
    public string SourceFile { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceSheet { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public int TransactionCount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime ImportedUtc { get; set; }
}
