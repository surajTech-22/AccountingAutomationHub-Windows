using System;

namespace AccountingAutomationHub.Desktop.ViewModels;

/// <summary>
/// Dedicated Consolidated Tally Import navigation surface. Reuses the original
/// Dashboard's Tally link/export/settings presenter and its coordinator without
/// creating a second Tally session, sync loop, or XML exporter.
/// The guarded consolidated voucher route remains unavailable until real
/// branch master-first/voucher readback is proved.
/// </summary>
public sealed class ConsolidatedTallyImportViewModel : PageViewModel
{
    public ConsolidatedTallyImportViewModel(long parentClientId, string financialYear,
        DashboardViewModel existingDashboardWorkspace)
        : base("Tally Import", "Consolidated · existing Tally link, settings and import design")
    {
        if (parentClientId <= 0 || string.IsNullOrWhiteSpace(financialYear))
            throw new ArgumentException("Saved consolidated Client/FY is required.");
        ParentClientId = parentClientId;
        FinancialYear = financialYear;
        ExistingDashboardWorkspace = existingDashboardWorkspace
            ?? throw new ArgumentNullException(nameof(existingDashboardWorkspace));
    }

    public long ParentClientId { get; }
    public string FinancialYear { get; }
    public DashboardViewModel ExistingDashboardWorkspace { get; }
}
