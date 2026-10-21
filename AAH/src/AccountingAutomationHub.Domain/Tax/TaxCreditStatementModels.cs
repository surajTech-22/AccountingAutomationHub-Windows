namespace AccountingAutomationHub.Domain.Tax;

public sealed class TaxCreditStatementImport
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string StatementType { get; set; } = "26AS";
    public string Pan { get; set; } = string.Empty;
    public string AssesseeName { get; set; } = string.Empty;
    public string AssessmentYear { get; set; } = string.Empty;
    public DateTime? DataUpdatedTill { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public string ParseNotes { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
    public bool IsCompleteAuthority { get; set; } = true;
    public DateTime ImportedUtc { get; set; }
}

public sealed class TaxCreditStatementRow
{
    public long Id { get; set; }
    public long ImportId { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string StatementType { get; set; } = "26AS";
    public string Part { get; set; } = string.Empty;
    public string TaxKind { get; set; } = "TDS";
    public string DeductorName { get; set; } = string.Empty;
    public string DeductorTan { get; set; } = string.Empty;
    public string DeductorPan { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
    public DateTime? TransactionDate { get; set; }
    public string BookingStatus { get; set; } = string.Empty;
    public DateTime? BookingDate { get; set; }
    public string Remarks { get; set; } = string.Empty;
    public string AcknowledgementNumber { get; set; } = string.Empty;
    public string CertificateNumber { get; set; } = string.Empty;
    public long AmountPaidPaise { get; set; }
    public long TaxDeductedPaise { get; set; }
    public long TaxDepositedPaise { get; set; }
    public string RowKey { get; set; } = string.Empty;

    public decimal AmountPaid => AmountPaidPaise / 100m;
    public decimal TaxDeducted => TaxDeductedPaise / 100m;
    public decimal TaxDeposited => TaxDepositedPaise / 100m;
}

public sealed class TaxCreditImportResult
{
    public bool Imported { get; set; }
    public bool Duplicate { get; set; }
    public string Message { get; set; } = string.Empty;
    public TaxCreditStatementImport? Statement { get; set; }
    public List<TaxCreditStatementRow> Rows { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class TaxCreditReconciliationRow
{
    public long StatementRowId { get; set; }
    public string Part { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
    public string TaxKind { get; set; } = "TDS";
    public string DeductorName { get; set; } = string.Empty;
    public string DeductorTan { get; set; } = string.Empty;
    public string BookingStatus { get; set; } = string.Empty;
    public DateTime? BookingDate { get; set; }
    public DateTime? StatementDate { get; set; }
    public decimal StatementAmount { get; set; }
    public decimal StatementTds { get; set; }
    public decimal StatementDeposited { get; set; }
    public long? BooksSourceId { get; set; }
    public string BooksSourceType { get; set; } = string.Empty;
    public string BooksReference { get; set; } = string.Empty;
    public string BooksParty { get; set; } = string.Empty;
    public DateTime? BooksDate { get; set; }
    public decimal BooksAmount { get; set; }
    public decimal BooksTds { get; set; }
    public decimal Difference => StatementAmount - BooksAmount;
    public decimal TdsDifference => StatementTds - BooksTds;
    public int Confidence { get; set; }
}

public sealed class TaxCreditReconciliationSummary
{
    public int StatementRows { get; set; }
    public int Matched { get; set; }
    public int Probable { get; set; }
    public int MissingInBooks { get; set; }
    public int Review { get; set; }
    public decimal StatementGross { get; set; }
    public decimal StatementTds { get; set; }
    public decimal StatementTcs { get; set; }
    public decimal StatementDeposited { get; set; }
    public decimal MatchedGross { get; set; }
}
