using System;
using System.Threading.Tasks;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Infrastructure.Tally;

namespace AccountingAutomationHub.Desktop.ViewModels;

/// <summary>
/// Dedicated consolidated import surface. The embedded Dashboard retains the
/// SAME original Tally link/settings panel. Only the branch master-first
/// action is added here; unverified voucher consolidation is never posted.
/// </summary>
public sealed class ConsolidatedTallyImportViewModel : PageViewModel
{
    private readonly Func<bool, bool, Task<TallyBranchMasterImportResult>> _importMasters;
    private readonly Func<bool, bool, Task<TallyConsolidatedPreparedXmlResult>> _prepareXml;
    private readonly Func<bool, bool, Task<TallyConsolidatedVoucherImportResult>> _importVouchers;
    private bool _separateVoucherTypes;
    private bool _separateCashLedgers;
    private string _masterImportStatus =
        "Prepare branch groups in the linked consolidated Tally company. " +
        "Separate voucher types and cash ledgers are optional. No vouchers will be posted.";
    public ConsolidatedTallyImportViewModel(
        long parentClientId, string financialYear,
        DashboardViewModel existingDashboardWorkspace,
        Func<bool, bool, Task<TallyBranchMasterImportResult>> importMasters,
        Func<bool, bool, Task<TallyConsolidatedPreparedXmlResult>> prepareXml,
        Func<bool, bool, Task<TallyConsolidatedVoucherImportResult>> importVouchers)
        : base("Tally Import", "Consolidated · existing Tally link, settings and import design")
    {
        if (parentClientId <= 0 || string.IsNullOrWhiteSpace(financialYear))
            throw new ArgumentException("Saved consolidated Client/FY is required.");
        ParentClientId = parentClientId;
        FinancialYear = financialYear;
        ExistingDashboardWorkspace = existingDashboardWorkspace
            ?? throw new ArgumentNullException(nameof(existingDashboardWorkspace));
        _importMasters = importMasters ?? throw new ArgumentNullException(nameof(importMasters));
        _prepareXml = prepareXml ?? throw new ArgumentNullException(nameof(prepareXml));
        _importVouchers = importVouchers ?? throw new ArgumentNullException(nameof(importVouchers));
        ImportConsolidatedVouchersCommand = new AsyncRelayCommand(ImportVouchersAsync);
        PrepareConsolidatedXmlCommand = new AsyncRelayCommand(PrepareXmlAsync);
        ImportBranchMastersCommand = new AsyncRelayCommand(ImportMastersAsync);
    }

    public long ParentClientId { get; }
    public string FinancialYear { get; }
    public DashboardViewModel ExistingDashboardWorkspace { get; }
    public AsyncRelayCommand ImportBranchMastersCommand { get; }
    public AsyncRelayCommand PrepareConsolidatedXmlCommand { get; }
    public AsyncRelayCommand ImportConsolidatedVouchersCommand { get; }
    private string _voucherImportStatus = "Import requires a verified Tally link, master-first readback and collision-free prepared branch vouchers. A failed readback will stop without retry.";
    public string VoucherImportStatus { get => _voucherImportStatus; private set => SetProperty(ref _voucherImportStatus, value); }
    private string _preparedXmlStatus = "Collects the existing prepared XML from every branch. No calculations except explicitly matched interbank transfers.";
    public string PreparedXmlStatus { get => _preparedXmlStatus; private set => SetProperty(ref _preparedXmlStatus, value); }
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
    public string MasterImportStatus
    {
        get => _masterImportStatus;
        private set => SetProperty(ref _masterImportStatus, value);
    }

    private async Task PrepareXmlAsync()
    {
        PreparedXmlStatus = "Checking each branch's existing XML and original ledger authority...";
        try
        {
            var result = await _prepareXml(SeparateVoucherTypes, SeparateCashLedgers);
            PreparedXmlStatus = result.Status;
        }
        catch (Exception exception)
        {
            PreparedXmlStatus = "Consolidated XML not generated: " + exception.Message;
        }
    }

    private async Task ImportVouchersAsync()
    {
        VoucherImportStatus = "Checking live consolidated masters, duplicate voucher identities and prepared branch DR/CR...";
        try
        {
            var result = await _importVouchers(SeparateVoucherTypes, SeparateCashLedgers);
            VoucherImportStatus =
                $"{result.ImportedVouchers} voucher(s) imported and read back; " +
                $"{result.CreatedMasters} missing master(s) created. " + result.Status;
        }
        catch (Exception ex)
        {
            VoucherImportStatus =
                "Import stopped. DO NOT retry blindly: verify live Tally after any partial acceptance. " + ex.Message;
        }
    }

    private async Task ImportMastersAsync()
    {
        MasterImportStatus = "Verifying the existing Tally link and live destination masters...";
        try
        {
            var result = await _importMasters(SeparateVoucherTypes, SeparateCashLedgers);
            MasterImportStatus =
                $"{result.RequiredGroups} compulsory groups verified; " +
                $"{result.CreatedGroups} groups, {result.CreatedCashLedgers} cash ledgers, " +
                $"{result.CreatedVoucherTypes} voucher types created. " + result.Status;
        }
        catch (Exception ex)
        {
            MasterImportStatus = "Master import stopped. Verify before retrying: " + ex.Message;
        }
    }
}
