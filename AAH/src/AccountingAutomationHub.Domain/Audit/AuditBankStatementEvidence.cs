namespace AccountingAutomationHub.Domain.Audit;

/// <summary>
/// Audit-owned bank statement evidence. The source file stays outside the database;
/// Audit stores its identity, detected balances/totals and normalized transaction rows.
/// This authority never writes Accounting Banking tables.
/// </summary>
public sealed class AuditBankStatementEvidence
{
    public long Id { get; set; }
    public long AuditClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string AccountCategory { get; set; } = "Bank";
    public string AccountIdentity { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string SourceSheet { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public int TransactionCount { get; set; }
    public string ParseStatus { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime ImportedUtc { get; set; }
}

public sealed class AuditBankStatementTransaction
{
    public long Id { get; set; }
    public long StatementId { get; set; }
    public long AuditClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string AccountCategory { get; set; } = "Bank";
    public DateTime TransactionDate { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string Narration { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal? RunningBalance { get; set; }
    public int SourceRow { get; set; }
    public int SourcePage { get; set; }
    public string SourceLocation { get; set; } = string.Empty;
    public string OriginKey { get; set; } = string.Empty;
    public decimal Amount => Debit != 0m ? System.Math.Abs(Debit) : System.Math.Abs(Credit);
}

public sealed class AuditBankStatementImportPreview
{
    public string SourcePath { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string SourceSheet { get; set; } = string.Empty;
    public string DetectedAccountIdentity { get; set; } = string.Empty;
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public string ParseStatus { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public List<AuditBankStatementTransaction> Transactions { get; } = [];
}
