namespace AccountingAutomationHub.Domain.Journals;

public sealed class JournalGenerationSettings
{
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string FilingFrequency { get; set; } = "Monthly";
    public string SettlementLedgerMode { get; set; } = "Single Ledger";
    public string BalanceType { get; set; } = "Payable";
    public string SingleSettlementLedger { get; set; } = "GST Payable / Receivable";
    public string IgstSettlementLedger { get; set; } = "IGST Payable / Receivable";
    public string CgstSettlementLedger { get; set; } = "CGST Payable / Receivable";
    public string SgstSettlementLedger { get; set; } = "SGST Payable / Receivable";
    public string TemporaryRcmIgstLedger { get; set; } = "Input IGST RCM";
    public string TemporaryRcmCgstLedger { get; set; } = "Input CGST RCM";
    public string TemporaryRcmSgstLedger { get; set; } = "Input SGST RCM";
    public string RcmExpenseIgstLedger { get; set; } = "RCM Input Expenses";
    public string RcmExpenseCgstLedger { get; set; } = "RCM Input Expenses";
    public string RcmExpenseSgstLedger { get; set; } = "RCM Input Expenses";
    // Current Excel GST Control Panel stores pipe-delimited period keys in Journal!Z4/Z5.
    // Keep the same representation so period-membership semantics stay exact.
    public string NilSalesPeriods { get; set; } = "|";
    public string NilSalesConflicts { get; set; } = "|";
    public decimal OpeningBooksBalance { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
