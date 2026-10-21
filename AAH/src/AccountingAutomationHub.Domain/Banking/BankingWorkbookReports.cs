namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankingWorkbookReports
{
    public List<StatementMetadataRow> StatementMetadata { get; } = [];
    public List<TransactionRegistryRow> TransactionRegistry { get; } = [];
    public List<TransferMapRow> TransferMap { get; } = [];
    public List<ContraDebugRow> ContraDebug { get; } = [];
    public List<ChainReportRow> ChainReport { get; } = [];
    public List<ChainAccountSummaryRow> ChainAccountSummary { get; } = [];
    public List<DuplicateReportRow> DuplicateReport { get; } = [];
    public List<ReportMetricRow> RegistryReport { get; } = [];
    public List<IntegrityReportRow> IntegrityReport { get; } = [];
    public List<ImportLogRow> ImportLog { get; } = [];
    public List<ImportPerformanceLogRow> ImportPerformanceLog { get; } = [];
    public List<CashEntryRegistryRow> CashEntryRegistry { get; } = [];
    public List<BalanceAuthorityReportRow> BalanceAuthority { get; } = [];
}

public sealed class StatementMetadataRow
{
    public long StatementDatabaseId { get; set; }
    public long BankAccountDatabaseId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }
    public decimal? CalculatedClosingBalance { get; set; }
    public decimal? BalanceDifference { get; set; }
    public string OpeningBalanceText => BankBusinessBalance.Format(OpeningBalance);
    public string ClosingBalanceText => BankBusinessBalance.Format(ClosingBalance);
    public string CalculatedClosingBalanceText => BankBusinessBalance.Format(CalculatedClosingBalance);
    public string BalanceStatus { get; set; } = string.Empty;
    public string MissingPeriods { get; set; } = string.Empty;
    public string OverlapPeriods { get; set; } = string.Empty;
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ObSource { get; set; } = string.Empty;
    public string CbSource { get; set; } = string.Empty;
    public decimal? SuggestedOb { get; set; }
    public string VerificationStatus { get; set; } = string.Empty;
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string StatementId { get; set; } = string.Empty;
    public string BankLedgerName { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string IfscCode { get; set; } = string.Empty;
    public string MicrCode { get; set; } = string.Empty;
    public string SwiftCode { get; set; } = string.Empty;
    public string BankAddress { get; set; } = string.Empty;
    public string BankCity { get; set; } = string.Empty;
    public string BankState { get; set; } = string.Empty;
    public string BankPincode { get; set; } = string.Empty;
    public string BankPhone { get; set; } = string.Empty;
    public string BankEmail { get; set; } = string.Empty;
    public string BankWebsite { get; set; } = string.Empty;
    public string AccountHolderName { get; set; } = string.Empty;
    public string AccountCategory { get; set; } = string.Empty;
    public decimal OdLimit { get; set; }
    public decimal DrawingPower { get; set; }
    public decimal CreditLimit { get; set; }
    public DateTime? SanctionDate { get; set; }
    public DateTime? AccountOpenDate { get; set; }
    public string PanNumber { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string TanNumber { get; set; } = string.Empty;
    public string CinNumber { get; set; } = string.Empty;
    public string BankId { get; set; } = string.Empty;
    public int MetadataVersion { get; set; } = 1;
    public DateTime LastUpdated { get; set; }
    public string VerifiedBy { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class TransactionRegistryRow
{
    public long TransactionDatabaseId { get; set; }
    public string TransactionId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public int SheetRow { get; set; }
    public DateTime TransactionDate { get; set; }
    public string Narration { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Bank { get; set; } = string.Empty;
    public string ImportStatus { get; set; } = string.Empty;
    public string DuplicateOf { get; set; } = string.Empty;
    public string MatchStatus { get; set; } = string.Empty;
    public string MatchedTxnId { get; set; } = string.Empty;
    public string MatchId { get; set; } = string.Empty;
    public string ArchiveReason { get; set; } = string.Empty;
    public DateTime? ArchivedOn { get; set; }
    public int OriginalSheetRow { get; set; }
    public int CurrentSheetRow { get; set; }
    public string BankStatementLink { get; set; } = string.Empty;
    public decimal? AccountOpeningBalance { get; set; }
    public decimal? AccountClosingBalance { get; set; }
    public string AccountBalanceStatus { get; set; } = string.Empty;
    public string MissingPeriods { get; set; } = string.Empty;
}

public sealed class TransferMapRow
{
    public string MatchId { get; set; } = string.Empty;
    public string SourceTxnId { get; set; } = string.Empty;
    public string TargetTxnId { get; set; } = string.Empty;
    public string SourceAccount { get; set; } = string.Empty;
    public string TargetAccount { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedOn { get; set; }
    public int SourceRow { get; set; }
    public int TargetRow { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string MatchMethod { get; set; } = string.Empty;
    public string SourceLink { get; set; } = string.Empty;
    public string TargetLink { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
    public DateTime LastUpdated2 { get; set; }
    public string Reserved { get; set; } = string.Empty;
    public decimal? SourceOpeningBalance { get; set; }
    public decimal? SourceClosingBalance { get; set; }
    public decimal? TargetOpeningBalance { get; set; }
    public decimal? TargetClosingBalance { get; set; }
}

public sealed class ContraDebugRow
{
    public string RunId { get; set; } = string.Empty;
    public int SourceRegistryRow { get; set; }
    public int TargetRegistryRow { get; set; }
    public int SourceSheetRow { get; set; }
    public int TargetSheetRow { get; set; }
    public DateTime? Date { get; set; }
    public decimal Amount { get; set; }
    public string SourceBank { get; set; } = string.Empty;
    public string TargetBank { get; set; } = string.Empty;
    public string SourceMethod { get; set; } = string.Empty;
    public string TargetMethod { get; set; } = string.Empty;
    public string SourceReferenceGroups { get; set; } = string.Empty;
    public string TargetReferenceGroups { get; set; } = string.Empty;
    public string MatchedReference { get; set; } = string.Empty;
    public string SourceTxn { get; set; } = string.Empty;
    public string TargetTxn { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string SourceNarration { get; set; } = string.Empty;
    public string TargetNarration { get; set; } = string.Empty;
    public DateTime CheckedOn { get; set; }
    public decimal? SourceOpeningBalance { get; set; }
    public decimal? SourceClosingBalance { get; set; }
    public decimal? TargetOpeningBalance { get; set; }
    public decimal? TargetClosingBalance { get; set; }
}

public sealed class ChainReportRow
{
    public string Account { get; set; } = string.Empty;
    public string PreviousFile { get; set; } = string.Empty;
    public DateTime? PreviousEndDate { get; set; }
    public string NextFile { get; set; } = string.Empty;
    public DateTime? NextStartDate { get; set; }
    public decimal? PreviousCb { get; set; }
    public decimal? NextOb { get; set; }
    public decimal? Difference { get; set; }
    public string Status { get; set; } = string.Empty;
    public string GapCheck { get; set; } = string.Empty;
}

public sealed class ChainAccountSummaryRow
{
    public string Account { get; set; } = string.Empty;
    public decimal? OverallOb { get; set; }
    public decimal? OverallCb { get; set; }
    public decimal? CalculatedCb { get; set; }
    public decimal? Difference { get; set; }
    public string MissingPeriods { get; set; } = string.Empty;
    public string OverlapPeriods { get; set; } = string.Empty;
    public int Statements { get; set; }
    public int FailCount { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class DuplicateReportRow
{
    public string TransactionId { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string DuplicateOf { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal? AccountOpeningBalance { get; set; }
    public decimal? AccountClosingBalance { get; set; }
    public string AccountBalanceStatus { get; set; } = string.Empty;
}

public sealed class ReportMetricRow
{
    public string Metric { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class IntegrityReportRow
{
    public string Check { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
}

public sealed class ImportLogRow
{
    public long StatementDatabaseId { get; set; }
    public DateTime Timestamp { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public int RowsImported { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }
    public string BalanceStatus { get; set; } = string.Empty;
    public string MissingPeriods { get; set; } = string.Empty;
}

public sealed class ImportPerformanceLogRow
{
    public string RecordType { get; set; } = string.Empty;
    public DateTime DateTime { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Value1 { get; set; } = string.Empty;
    public string Value2 { get; set; } = string.Empty;
    public string Value3 { get; set; } = string.Empty;
    public string Value4 { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class CashEntryRegistryRow
{
    public string MasterLedger { get; set; } = string.Empty;
    public string MasterGroup { get; set; } = string.Empty;
    public string CashLedger { get; set; } = string.Empty;
    public string ParentGroup { get; set; } = string.Empty;
}

public sealed class BalanceAuthorityReportRow
{
    public string Account { get; set; } = string.Empty;
    public decimal? OpeningBalance { get; set; }
    public string OpeningSource { get; set; } = string.Empty;
    public decimal? ClosingBalance { get; set; }
    public string ClosingSource { get; set; } = string.Empty;
    public decimal? CalculatedClosingBalance { get; set; }
    public decimal? Difference { get; set; }
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }
    public int StatementCount { get; set; }
    public string MissingPeriods { get; set; } = string.Empty;
    public string OverlapPeriods { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
