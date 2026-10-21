namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankStatementImportResult
{
    public int FilesSelected { get; set; }
    public int FilesImported { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesRetried { get; set; }
    public int DuplicateStatementsSkipped { get; set; }
    public int TransactionsImported { get; set; }
    public int TransactionsMatchedBeforeImport { get; set; }
    public int DuplicateTransactionsSkipped { get; set; }
    public int RowsOutsideFinancialYearSkipped { get; set; }
    public int AccountsCreated { get; set; }
    public string PreImportTallyStatus { get; set; } = string.Empty;
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public List<BankStatementImportIssue> Issues { get; } = [];
    public int ErrorCount => Issues.Count;

    public string Summary
    {
        get
        {
            var text = $"Files selected: {FilesSelected:N0}; imported: {FilesImported:N0}; skipped: {FilesSkipped:N0}; " +
                       $"transactions imported: {TransactionsImported:N0}; duplicate transactions skipped: {DuplicateTransactionsSkipped:N0}; " +
                       $"duplicate statements skipped: {DuplicateStatementsSkipped:N0}.";
            if (TransactionsMatchedBeforeImport > 0) text += $" Tally Inter-Bank matched before active import: {TransactionsMatchedBeforeImport:N0}.";
            if (FilesRetried > 0) text += $" Trouble-queue retries: {FilesRetried:N0}.";
            if (RowsOutsideFinancialYearSkipped > 0) text += $" Rows outside the selected Financial Year skipped: {RowsOutsideFinancialYearSkipped:N0}.";
            if (!string.IsNullOrWhiteSpace(PreImportTallyStatus)) text += $" {PreImportTallyStatus}";
            if (ErrorCount > 0) text += $" Errors requiring review: {ErrorCount:N0}.";
            return text;
        }
    }
}
