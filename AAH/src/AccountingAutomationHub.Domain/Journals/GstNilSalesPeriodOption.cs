namespace AccountingAutomationHub.Domain.Journals;

/// <summary>
/// Active-client equivalent of the Excel GST Control Panel "Sales Missing / Nil Filing" rows.
/// The period key is always the reporting-period end key (yyyymm).
/// </summary>
public sealed class GstNilSalesPeriodOption
{
    public string PeriodKey { get; set; } = string.Empty;
    public string PeriodLabel { get; set; } = string.Empty;
    public decimal InputGst { get; set; }
    public bool IsSelected { get; set; }
    public bool IsConflict { get; set; }
    public bool IsAutomatic { get; set; }
    public string Source { get; set; } = string.Empty;
    public bool CanSelect => !IsConflict && !IsAutomatic;
    public string Status => IsConflict
        ? "Conflict - Sales Exists"
        : IsAutomatic ? "Auto-classified from GSTR-3B"
        : IsSelected ? "Nil Filing Selected" : "Sales Missing";
}
