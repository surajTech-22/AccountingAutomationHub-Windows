using System;
using System.Threading.Tasks;
using AccountingAutomationHub.Desktop.Commands;

namespace AccountingAutomationHub.Desktop.ViewModels;

/// <summary>
/// Separate Consolidated Tally Import surface. Reuses the SAME Dashboard Tally
/// link/status UI. The new explicit preview command only reads already-prepared
/// source vouchers and live destination masters; it cannot send to Tally.
/// </summary>
public sealed class ConsolidatedTallyImportViewModel : PageViewModel
{
    private readonly Func<bool, bool, Task<string>> _prepareXmlPreview;
    private bool _separateVoucherTypes;
    private bool _separateCashLedgers;
    private string _xmlPreviewStatus =
        "Prepare and validate same-PAN branch XML. Live Consolidated import is disabled until destination Tally MASTERID/voucher readback passes.";

    public ConsolidatedTallyImportViewModel(long parentClientId, string financialYear,
        DashboardViewModel existingDashboardWorkspace,
        Func<bool, bool, Task<string>> prepareXmlPreview)
        : base("Tally Import", "Consolidated · original Tally controls and source-owned XML preparation")
    {
        if (parentClientId <= 0 || string.IsNullOrWhiteSpace(financialYear))
            throw new ArgumentException("Saved consolidated Client/FY is required.");
        ParentClientId = parentClientId;
        FinancialYear = financialYear;
        ExistingDashboardWorkspace = existingDashboardWorkspace
            ?? throw new ArgumentNullException(nameof(existingDashboardWorkspace));
        _prepareXmlPreview = prepareXmlPreview
            ?? throw new ArgumentNullException(nameof(prepareXmlPreview));
        PrepareXmlPreviewCommand = new AsyncRelayCommand(PreparePreviewAsync);
    }

    public long ParentClientId { get; }
    public string FinancialYear { get; }
    public DashboardViewModel ExistingDashboardWorkspace { get; }
    public AsyncRelayCommand PrepareXmlPreviewCommand { get; }

    // Voucher types and separate cash ledgers are OPTIONAL. Branch accounting
    // groups are always mandatory in the production XML planner.
    public bool SeparateVoucherTypes
    {
        get => _separateVoucherTypes;
        set => SetProperty(ref _separateVoucherTypes, value);
    }
    public bool SeparateCashLedgers
    {
        get => _separateCashLedgers;
        set => SetProperty(ref _separateCashLedgers, value);
    }
    public string XmlPreviewStatus
    {
        get => _xmlPreviewStatus;
        private set => SetProperty(ref _xmlPreviewStatus, value);
    }

    private async Task PreparePreviewAsync()
    {
        try
        {
            XmlPreviewStatus = "Reading existing branch vouchers and destination masters; validating XML without posting.";
            var result = await _prepareXmlPreview(SeparateVoucherTypes, SeparateCashLedgers);
            XmlPreviewStatus = result;
        }
        catch (Exception exception)
        {
            // A failed preflight must never fall back to the ordinary parent
            // Client/FY importer or create GST journals.
            XmlPreviewStatus = "Consolidated XML preparation blocked: " + exception.Message;
        }
    }
}
