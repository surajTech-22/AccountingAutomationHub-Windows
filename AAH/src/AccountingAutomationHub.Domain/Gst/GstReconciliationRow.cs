namespace AccountingAutomationHub.Domain.Gst;

public sealed class GstReconciliationRow
{
    public long PersistentId { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string PeriodKey { get; set; } = string.Empty;
    public string PeriodLabel { get; set; } = string.Empty;

    public decimal BooksOutput { get; set; }
    public decimal PortalOutput { get; set; }
    public decimal OutputDifference => BooksOutput - PortalOutput;

    public decimal BooksRcmTaxable { get; set; }
    public decimal PortalRcmTaxable { get; set; }
    public decimal RcmTaxableDifference => BooksRcmTaxable - PortalRcmTaxable;

    public decimal BooksRcmLiabilityGst { get; set; }
    public decimal PortalRcmGst { get; set; }
    public decimal RcmGstDifference => BooksRcmLiabilityGst - PortalRcmGst;

    public decimal BooksItc { get; set; }
    public decimal PortalItc { get; set; }
    public decimal ItcDifference => BooksItc - PortalItc;

    public decimal PortalNet { get; set; }
    public decimal BooksNetWithOpening { get; set; }
    public decimal PortalLedgerBalance { get; set; }
    public decimal LedgerDifference => BooksNetWithOpening - PortalLedgerBalance;
    public string BooksPosition { get; set; } = string.Empty;

    public decimal TaxPayable { get; set; }
    public decimal ItcUsed { get; set; }
    public decimal CashPaid { get; set; }
    public decimal Interest { get; set; }
    public decimal LateFee { get; set; }
    public decimal RcmCash { get; set; }
    public decimal OffsetTotal { get; set; }
    public decimal PaymentDifference { get; set; }

    public string Status { get; set; } = string.Empty;
    public bool HasJournal { get; set; }
    public bool HasReturn { get; set; }
    public bool HasCreditLedger { get; set; }
    public bool HasCashLedger { get; set; }
    public string ReviewNotes { get; set; } = string.Empty;
    public DateTime RefreshedUtc { get; set; }

    // Compatibility aliases retained for older bindings/database fixtures.
    public decimal BooksNet { get => BooksNetWithOpening; set => BooksNetWithOpening = value; }
    public decimal BooksClosing { get => BooksNetWithOpening; set => BooksNetWithOpening = value; }
    public decimal PortalOpening { get; set; }
    public decimal PortalClosing { get => PortalLedgerBalance; set => PortalLedgerBalance = value; }
    public decimal NetDifference => BooksNetWithOpening - PortalNet;
    public decimal ClosingDifference => LedgerDifference;

    public decimal FinalDifference =>
        Math.Abs(OutputDifference) +
        Math.Abs(RcmTaxableDifference) +
        Math.Abs(RcmGstDifference) +
        Math.Abs(ItcDifference) +
        (HasCreditLedger && HasCashLedger ? Math.Abs(LedgerDifference) : 0m) +
        Math.Abs(PaymentDifference);
}
