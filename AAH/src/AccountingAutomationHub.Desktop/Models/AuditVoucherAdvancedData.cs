using System;
using System.Collections.Generic;

namespace AccountingAutomationHub.Desktop.Models;

/// <summary>
/// Additive, Audit-owned voucher detail persisted inside AuditRecordRow.PayloadJson.
/// Older Audit rows deserialize safely because every property has a default.
/// No Accounting-client record or external source evidence is mutated by this model.
/// </summary>
public sealed class AuditVoucherAdvancedData
{
    public DateTime? EffectiveDate { get; set; }
    public DateTime? ReferenceDate { get; set; }
    public string GstRegistrationType { get; set; } = string.Empty;
    public string TransactionNature { get; set; } = string.Empty;
    public string Pan { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool IsReverseCharge { get; set; }
    public bool IsNilRated { get; set; }
    public bool IsExempt { get; set; }
    public bool IsNonGst { get; set; }
    public string TdsSection { get; set; } = string.Empty;
    public decimal TdsRate { get; set; }
    public decimal TdsAmount { get; set; }
    public string TcsSection { get; set; } = string.Empty;
    public decimal TcsRate { get; set; }
    public decimal TcsAmount { get; set; }
    public bool AccountingLinesAuthoritative { get; set; }
    public bool InventoryLinesAuthoritative { get; set; }
    public List<AuditVoucherLedgerLineData> LedgerLines { get; set; } = [];
    public List<AuditInventoryLineData> InventoryLines { get; set; } = [];
    public List<AuditBillAllocationData> BillAllocations { get; set; } = [];
    public List<AuditCostCentreAllocationData> CostCentreAllocations { get; set; } = [];
    public List<AuditBankAllocationData> BankAllocations { get; set; } = [];
}

public sealed class AuditVoucherLedgerLineData
{
    public string LineId { get; set; } = Guid.NewGuid().ToString("N");
    public string Ledger { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Direction { get; set; } = "Dr";
    public decimal Amount { get; set; }
    public string GstTreatment { get; set; } = string.Empty;
    public string CostCentre { get; set; } = string.Empty;
    public string Narration { get; set; } = string.Empty;
    public string Role { get; set; } = "Other";
    public string TallyMasterId { get; set; } = string.Empty;
}

public sealed class AuditInventoryLineData
{
    public string LineId { get; set; } = Guid.NewGuid().ToString("N");
    public string StockItem { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string HsnSac { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal GstRate { get; set; }
    public decimal Igst { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Cess { get; set; }
    public decimal LineTotal { get; set; }
}

public sealed class AuditBillAllocationData
{
    public string LineId { get; set; } = Guid.NewGuid().ToString("N");
    public string Ledger { get; set; } = string.Empty;
    public string ReferenceType { get; set; } = "On Account";
    public string ReferenceNumber { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public decimal Amount { get; set; }
}

public sealed class AuditCostCentreAllocationData
{
    public string LineId { get; set; } = Guid.NewGuid().ToString("N");
    public string Ledger { get; set; } = string.Empty;
    public string CostCentre { get; set; } = string.Empty;
    public string Direction { get; set; } = "Dr";
    public decimal Amount { get; set; }
}

public sealed class AuditBankAllocationData
{
    public string LineId { get; set; } = Guid.NewGuid().ToString("N");
    public string Ledger { get; set; } = string.Empty;
    public string TransactionType { get; set; } = string.Empty;
    public string InstrumentNumber { get; set; } = string.Empty;
    public string Utr { get; set; } = string.Empty;
    public DateTime? BankDate { get; set; }
    public decimal Amount { get; set; }
}
