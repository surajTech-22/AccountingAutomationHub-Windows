using System.IO;

namespace AccountingAutomationHub.Domain.Gst;

public sealed class Gstr3bReturnRecord
{
    public long PersistentId { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string TradeName { get; set; } = string.Empty;
    public string PeriodKey { get; set; } = string.Empty;
    public string PeriodLabel { get; set; } = string.Empty;
    public string FilingFrequency { get; set; } = "Monthly";
    public string Arn { get; set; } = string.Empty;
    public DateTime? ArnDate { get; set; }

    // GSTR-3B Table 3.1(a)
    public decimal OutwardTaxable { get; set; }
    public decimal OutwardIgst { get; set; }
    public decimal OutwardCgst { get; set; }
    public decimal OutwardSgst { get; set; }
    public decimal OutwardCess { get; set; }

    // GSTR-3B Table 3.1(d)
    public decimal RcmTaxable { get; set; }
    public decimal RcmIgst { get; set; }
    public decimal RcmCgst { get; set; }
    public decimal RcmSgst { get; set; }
    public decimal RcmCess { get; set; }

    // GSTR-3B Table 4(C)
    public decimal NetItcIgst { get; set; }
    public decimal NetItcCgst { get; set; }
    public decimal NetItcSgst { get; set; }
    public decimal NetItcCess { get; set; }

    // GSTR-3B Table 5
    public decimal CompositionExemptNilInterState { get; set; }
    public decimal CompositionExemptNilIntraState { get; set; }
    public decimal NonGstInterState { get; set; }
    public decimal NonGstIntraState { get; set; }

    // GSTR-3B Table 5.1
    public decimal SystemInterestIgst { get; set; }
    public decimal SystemInterestCgst { get; set; }
    public decimal SystemInterestSgst { get; set; }
    public decimal SystemInterestCess { get; set; }
    public decimal InterestPaidIgst { get; set; }
    public decimal InterestPaidCgst { get; set; }
    public decimal InterestPaidSgst { get; set; }
    public decimal InterestPaidCess { get; set; }
    public decimal LateFeeIgst { get; set; }
    public decimal LateFeeCgst { get; set; }
    public decimal LateFeeSgst { get; set; }
    public decimal LateFeeCess { get; set; }

    // GSTR-3B Table 6.1. The complete section/tax-head matrix is retained.
    public List<Gstr3bPaymentRow> PaymentRows { get; set; } = [];

    // Breakup of tax liability declared for interest computation.
    public List<Gstr3bLiabilityBreakupRow> LiabilityBreakup { get; set; } = [];

    // The workbook's Original GSTR-3B Full Raw Data panel is retained as
    // ordered flattened field/value pairs. This includes tables that do not
    // participate directly in the final reconciliation calculation.
    public Dictionary<string, string> RawFields { get; set; } = [];

    public bool IsNilReturn { get; set; }
    public bool ParseValid { get; set; }
    public string ParseNotes { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public DateTime ImportedUtc { get; set; }
    public DateTime? LastEditedUtc { get; set; }
    public string LastEditedDisplay => LastEditedUtc.HasValue ? LastEditedUtc.Value.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss") : "Never";

    public string SourceFileName => string.Join(
        "; ",
        SourcePath.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.GetFileName(path.Trim()))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    public string YearDisplay =>
        RawFields.TryGetValue("year", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : FinancialYear;
    public decimal OutwardGst => OutwardIgst + OutwardCgst + OutwardSgst;
    public decimal RcmGst => RcmIgst + RcmCgst + RcmSgst;
    public decimal NetItc => NetItcIgst + NetItcCgst + NetItcSgst;
    public decimal SystemInterest =>
        SystemInterestIgst + SystemInterestCgst + SystemInterestSgst + SystemInterestCess;
    public decimal InterestPaid =>
        InterestPaidIgst + InterestPaidCgst + InterestPaidSgst + InterestPaidCess;
    public decimal LateFee => LateFeeIgst + LateFeeCgst + LateFeeSgst + LateFeeCess;

    public decimal TaxPayableIgst => PaymentByTaxHead("IGST", row => row.NetTaxPayable);
    public decimal TaxPayableCgst => PaymentByTaxHead("CGST", row => row.NetTaxPayable);
    public decimal TaxPayableSgst => PaymentByTaxHead("SGST", row => row.NetTaxPayable);
    public decimal TaxPayableCess => PaymentByTaxHead("CESS", row => row.NetTaxPayable);
    // The workbook's Table 6.1 summary total intentionally shows IGST + CGST + SGST.
    public decimal TaxPayableTotal => TaxPayableIgst + TaxPayableCgst + TaxPayableSgst;
    public decimal AllTaxPayableTotal => PaymentRows.Sum(row => row.NetTaxPayable);

    public decimal CreditUsedIgst => PaymentByTaxHead("IGST", row => row.ItcTotal);
    public decimal CreditUsedCgst => PaymentByTaxHead("CGST", row => row.ItcTotal);
    public decimal CreditUsedSgst => PaymentByTaxHead("SGST", row => row.ItcTotal);
    public decimal CreditUsedCess => PaymentByTaxHead("CESS", row => row.ItcTotal);
    public decimal CreditUsedTotal => CreditUsedIgst + CreditUsedCgst + CreditUsedSgst;
    public decimal AllCreditUsedTotal => PaymentRows.Sum(row => row.ItcTotal);

    public decimal CashPaidIgst => PaymentByTaxHead("IGST", row => row.PaidCash);
    public decimal CashPaidCgst => PaymentByTaxHead("CGST", row => row.PaidCash);
    public decimal CashPaidSgst => PaymentByTaxHead("SGST", row => row.PaidCash);
    public decimal CashPaidCess => PaymentByTaxHead("CESS", row => row.PaidCash);
    public decimal CashPaidTotal => CashPaidIgst + CashPaidCgst + CashPaidSgst;
    public decimal AllCashPaidTotal => PaymentRows.Sum(row => row.PaidCash);

    public decimal PaymentInterest => PaymentRows.Sum(row => row.InterestPaidCash);
    public decimal PaymentLateFee => PaymentRows.Sum(row => row.LateFeePaidCash);
    public decimal RcmCash => PaymentRows
        .Where(row => row.Section.Contains("Reverse", StringComparison.OrdinalIgnoreCase))
        .Sum(row => row.PaidCash);
    public decimal OffsetTotal => PaymentRows.Sum(row => row.OffsetTotal);
    // Workbook Table 6.1 summary panel excludes Cess, interest and late fee.
    public decimal PaymentDifference =>
        TaxPayableTotal - CreditUsedTotal - CashPaidTotal;
    public string PaymentStatus => Math.Abs(PaymentDifference) <= 0.005m
        ? "Balanced"
        : "Check";

    private decimal PaymentByTaxHead(
        string taxHead,
        Func<Gstr3bPaymentRow, decimal> selector) =>
        PaymentRows
            .Where(row => row.TaxHead.Equals(taxHead, StringComparison.OrdinalIgnoreCase))
            .Sum(selector);
}
