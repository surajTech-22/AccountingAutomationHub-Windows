using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Models;

public sealed class AuditEntryEditDraft
{
    public DateTime? Date { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string VoucherType { get; set; } = string.Empty;
    public string PartyOrLedger { get; set; } = string.Empty;
    public string BusinessLedger { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Amount { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal GstRate { get; set; }
    public decimal Igst { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Cess { get; set; }
    public string Narration { get; set; } = string.Empty;
    public string CostCentre { get; set; } = string.Empty;
    public string Reason { get; set; } = "Wrong Ledger";
    public string Action { get; set; } = "Suggestion";

    public static readonly string[] Reasons =
    [
        "Wrong Ledger", "GST Rate", "Date", "Amount", "Party", "Voucher Type",
        "Duplicate", "Missing Tax", "Audit Adjustment", "Other"
    ];

    public static AuditEntryEditDraft From(AuditRecordRow row) => new()
    {
        Date = row.Date, Reference = row.Reference, VoucherType = row.VoucherType, PartyOrLedger = row.PartyOrLedger, BusinessLedger = row.BusinessLedger,
        Debit = row.Debit, Credit = row.Credit, Amount = row.Amount, TaxableValue = row.TaxableValue, GstRate = row.GstRate,
        Igst = row.Igst, Cgst = row.Cgst, Sgst = row.Sgst, Cess = row.Cess, Narration = row.OriginalNarration.Length > 0 ? row.OriginalNarration : row.Details,
        CostCentre = row.CostCentre
    };
}

public sealed class AuditGstr3bVerificationRow
{
    public string Period { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal BooksOutput { get; set; }
    public decimal Gstr3bOutput { get; set; }
    public decimal OutputDifference => BooksOutput - Gstr3bOutput;
    public decimal BooksRcmTaxable { get; set; }
    public decimal Gstr3bRcmTaxable { get; set; }
    public decimal RcmTaxableDifference => BooksRcmTaxable - Gstr3bRcmTaxable;
    public decimal BooksRcmGst { get; set; }
    public decimal Gstr3bRcmGst { get; set; }
    public decimal RcmGstDifference => BooksRcmGst - Gstr3bRcmGst;
    public decimal BooksItc { get; set; }
    public decimal Gstr3bItc { get; set; }
    public decimal ItcDifference => BooksItc - Gstr3bItc;
    public decimal TaxPayable { get; set; }
    public decimal ItcUsed { get; set; }
    public decimal CashPaid { get; set; }
    public decimal PaymentDifference { get; set; }
    public decimal BooksLedgerBalance { get; set; }
    public decimal PortalLedgerBalance { get; set; }
    public decimal LedgerDifference => BooksLedgerBalance - PortalLedgerBalance;
    public bool HasJournal { get; set; }
    public bool HasReturn { get; set; }
    public bool HasCreditLedger { get; set; }
    public bool HasCashLedger { get; set; }
    public string ReviewNotes { get; set; } = string.Empty;
    public bool IsException => Math.Abs(OutputDifference) > 0.01m || Math.Abs(RcmTaxableDifference) > 0.01m || Math.Abs(RcmGstDifference) > 0.01m || Math.Abs(ItcDifference) > 0.01m || Math.Abs(PaymentDifference) > 0.01m || (HasCreditLedger && HasCashLedger && Math.Abs(LedgerDifference) > 0.01m) || !HasReturn || !HasJournal;
    public string Verification => IsException ? "Review" : "Verified";
}
