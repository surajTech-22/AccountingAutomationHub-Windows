namespace AccountingAutomationHub.Domain.Journals;

public sealed class JournalVoucher
{
    public long PersistentId { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string PeriodKey { get; set; } = string.Empty;
    public string PeriodLabel { get; set; } = string.Empty;
    public DateTime VoucherDate { get; set; }
    public string VoucherNumber { get; set; } = string.Empty;
    public string VoucherType { get; set; } = "Journal";
    public string Narration { get; set; } = string.Empty;
    public string GenerationMode { get; set; } = "Automatic";
    public string WorkflowStatus { get; set; } = "Draft";
    public string ReferenceNumber { get; set; } = string.Empty;
    public string AttachmentPath { get; set; } = string.Empty;
    public long? ReversalOfId { get; set; }
    public long? ClonedFromId { get; set; }
    public long? RecurringProfileId { get; set; }
    public string SourceRunId { get; set; } = string.Empty;
    public string SourceJobId { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime? ApprovedUtc { get; set; }
    public bool IsUserEdited { get; set; }

    // Normal output tax cleared through this GST adjustment voucher.
    public decimal OutputIgst { get; set; }
    public decimal OutputCgst { get; set; }
    public decimal OutputSgst { get; set; }

    // RCM liability is retained as reconciliation metadata only. The source
    // RCM purchase voucher already contains RCM payable and temporary ITC.
    public decimal RcmIgst { get; set; }
    public decimal RcmCgst { get; set; }
    public decimal RcmSgst { get; set; }

    // Total ITC reported for reconciliation = normal ITC + RCM ITC claimed.
    public decimal InputIgst { get; set; }
    public decimal InputCgst { get; set; }
    public decimal InputSgst { get; set; }

    // Workbook-parity components used to build the actual journal lines.
    public decimal NormalInputIgst { get; set; }
    public decimal NormalInputCgst { get; set; }
    public decimal NormalInputSgst { get; set; }
    public decimal RcmClaimIgst { get; set; }
    public decimal RcmClaimCgst { get; set; }
    public decimal RcmClaimSgst { get; set; }
    public decimal RcmReverseIgst { get; set; }
    public decimal RcmReverseCgst { get; set; }
    public decimal RcmReverseSgst { get; set; }

    // GST reconciliation net: ITC - normal output - RCM liability.
    public decimal NetTax =>
        InputIgst + InputCgst + InputSgst -
        OutputIgst - OutputCgst - OutputSgst -
        RcmIgst - RcmCgst - RcmSgst;

    // Journal settlement excludes RCM transfers because they are self-balancing.
    public decimal NormalSettlementNet =>
        OutputIgst + OutputCgst + OutputSgst -
        NormalInputIgst - NormalInputCgst - NormalInputSgst;

    public List<JournalLine> Lines { get; set; } = [];
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
