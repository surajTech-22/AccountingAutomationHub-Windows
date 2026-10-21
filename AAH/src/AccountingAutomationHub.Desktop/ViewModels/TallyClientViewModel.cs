using System.Collections.ObjectModel;
using System.ComponentModel;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Tally;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class TallyRecoveryModuleSummary
{
    public string Module { get; init; } = string.Empty;
    public int MissingCount { get; init; }
    public int ReimportableCount { get; init; }
    public int ReviewBlockedCount { get; init; }
    public decimal Amount { get; init; }
    public string StatusText => ReviewBlockedCount > 0
        ? $"{ReimportableCount:N0} ready · {ReviewBlockedCount:N0} conflict/review blocked"
        : $"{ReimportableCount:N0} ready for explicit re-import";
}

public sealed class TallyClientViewModel : PageViewModel, IDisposable
{
    private readonly ActiveClientContext _clientContext;
    private readonly SqliteTallyRepository _repository;
    private readonly TallyLiveConnectionCoordinator _coordinator;
    private readonly SqliteGovernanceRepository _governanceRepository;
    private TallyVoucherRecoveryCandidate? _selectedRecoveryItem;
    private TallyRecoveryModuleSummary? _selectedRecoveryModule;
    private TallyCompanyInfo? _selectedCompany;
    private string _statusText = "Open a client to use Tally.";
    private string _uiConnectionStatus = string.Empty;
    private string _uiLastTallyOperation = string.Empty;
    private bool _uiIsConnected;
    private bool _uiIsBusy;
    private bool _disposed;

    public TallyClientViewModel(
        ActiveClientContext clientContext,
        SqliteTallyRepository repository,
        TallyLiveConnectionCoordinator coordinator,
        SqliteGovernanceRepository governanceRepository)
        : base("Tally", "Client-specific Tally connection, authority and deleted-voucher recovery")
    {
        _clientContext = clientContext;
        _repository = repository;
        _coordinator = coordinator;
        _governanceRepository = governanceRepository;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanViewTally);
        LinkSelectedCompanyCommand = new AsyncRelayCommand(LinkSelectedCompanyAsync, CanLink);
        UnlinkCommand = new RelayCommand(Unlink, CanUnlink);
        ReimportSelectedCommand = new AsyncRelayCommand(ReimportSelectedAsync, CanReimportSelected);
        ReimportSelectedModuleCommand = new AsyncRelayCommand(ReimportSelectedModuleAsync, CanReimportSelectedModule);
        ReimportAllDeletedCommand = new AsyncRelayCommand(ReimportAllDeletedAsync, CanReimportAny);

        _clientContext.PropertyChanged += OnClientContextChanged;
        _coordinator.Changed += OnCoordinatorChanged;
        SyncCoordinatorPresentation(includeOperation: true);
        RefreshLocal();
    }

    // R38: page-owned snapshots isolate the visible Tally page from the coordinator's
    // five-second heartbeat. Background authority may refresh; these collections do not
    // get cleared/repopulated until the user explicitly refreshes or changes Client/FY.
    public ObservableCollection<TallyCompanyInfo> OpenCompanies { get; } = [];
    public ObservableCollection<TallyVoucherRecoveryCandidate> RecoveryItems { get; } = [];
    public ObservableCollection<TallyRecoveryModuleSummary> RecoveryModules { get; } = [];
    public ObservableCollection<TallyVoucherRecoveryCandidate> SelectedModuleTransactions { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand LinkSelectedCompanyCommand { get; }
    public RelayCommand UnlinkCommand { get; }
    public AsyncRelayCommand ReimportSelectedCommand { get; }
    public AsyncRelayCommand ReimportSelectedModuleCommand { get; }
    public AsyncRelayCommand ReimportAllDeletedCommand { get; }

    public TallyCompanyInfo? SelectedCompany
    {
        get => _selectedCompany;
        set
        {
            if (!SetProperty(ref _selectedCompany, value)) return;
            if (!ReferenceEquals(_coordinator.SelectedCompany, value)) _coordinator.SelectedCompany = value;
            OnPropertyChanged(nameof(ShowLinkAction));
            OnPropertyChanged(nameof(LinkActionText));
            RaiseCommandStates();
        }
    }

    public TallyRecoveryModuleSummary? SelectedRecoveryModule
    {
        get => _selectedRecoveryModule;
        set
        {
            if (!SetProperty(ref _selectedRecoveryModule, value)) return;
            LoadSelectedModuleTransactions();
            OnPropertyChanged(nameof(SelectedModuleTitle));
            OnPropertyChanged(nameof(HasSelectedModule));
            ReimportSelectedModuleCommand.RaiseCanExecuteChanged();
        }
    }

    public TallyVoucherRecoveryCandidate? SelectedRecoveryItem
    {
        get => _selectedRecoveryItem;
        set
        {
            if (!SetProperty(ref _selectedRecoveryItem, value)) return;
            ReimportSelectedCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(SelectedRecoveryDetail));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentClientScopeText => _clientContext.CurrentClient is null
        ? "No client selected"
        : $"{_clientContext.CurrentClient.ClientCode} · {_clientContext.CurrentClient.ClientName} · FY {_clientContext.CurrentClient.FinancialYear}";

    public string ConnectionStatus => _uiConnectionStatus;
    public string LastTallyOperation => _uiLastTallyOperation;
    public bool IsConnected => _uiIsConnected;
    public bool IsBusy => _uiIsBusy;

    public bool HasLinkedCompany
    {
        get
        {
            var client = _clientContext.CurrentClient;
            return client is not null && _repository.GetLinkForClient(client.Id) is not null;
        }
    }

    public bool ShowLinkAction
    {
        get
        {
            var client = _clientContext.CurrentClient;
            var company = SelectedCompany;
            if (client is null || company is null) return false;
            var link = _repository.GetLinkForClient(client.Id);
            return link is null || !link.CompanyKey.Equals(company.CompanyKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    public string LinkActionText => HasLinkedCompany ? "Change Link" : "Confirm Link";

    public string LinkedCompanyText
    {
        get
        {
            var client = _clientContext.CurrentClient;
            if (client is null) return "No client selected";
            var link = _repository.GetLinkForClient(client.Id);
            if (link is null) return "Not linked to a Tally company";
            return $"{link.CompanyName} · GSTIN {Display(link.Gstin)} · Company {Display(link.CompanyNumber)}";
        }
    }

    public string AuthorityText
    {
        get
        {
            var client = _clientContext.CurrentClient;
            if (client is null) return "No Tally authority loaded";
            var link = _repository.GetLinkForClient(client.Id);
            if (link is null) return "Select an open company and confirm its link once";
            return $"Masters {link.MasterCount:N0} · Vouchers {link.VoucherCount:N0} · " +
                   $"Duplicate vouchers {link.DuplicateVoucherCount:N0} · Conflicts {link.ConflictingVoucherCount:N0}";
        }
    }

    public int MissingOrDeletedCount => RecoveryItems.Count;
    public int ReimportableCount => RecoveryItems.Count(item => item.CanReimport && item.VoucherDate is not null);
    public bool HasRecoveryItems => RecoveryItems.Count > 0;
    public bool HasSelectedModule => SelectedRecoveryModule is not null;
    public string RecoverySummary => RecoveryItems.Count == 0
        ? "No previously verified AAH vouchers are currently missing from the linked Tally company."
        : $"{RecoveryModules.Count:N0} affected module(s) · {RecoveryItems.Count:N0} missing/deleted transaction(s) · {ReimportableCount:N0} available for explicit re-import.";

    public string SelectedModuleTitle => SelectedRecoveryModule is null
        ? "Select a module to open its missing/deleted transactions"
        : $"{SelectedRecoveryModule.Module} · {SelectedRecoveryModule.MissingCount:N0} transaction(s)";

    public string SelectedRecoveryDetail => SelectedRecoveryItem is null
        ? "Select a transaction to inspect its last verified Tally identity."
        : $"{SelectedRecoveryItem.StatusText} · {SelectedRecoveryItem.Module} · " +
          $"{SelectedRecoveryItem.VoucherDate:dd-MMM-yyyy} · Ref {Display(SelectedRecoveryItem.Reference)} · " +
          $"Amount {SelectedRecoveryItem.Amount:N2} · Previous MASTERID {Display(SelectedRecoveryItem.PreviousMasterId)}";

    public void RefreshLocal()
    {
        var previousModule = SelectedRecoveryModule?.Module ?? string.Empty;
        var previousAnchor = SelectedRecoveryItem?.SourceAnchor ?? string.Empty;
        RefreshCompanyChoices();
        RecoveryItems.Clear();
        var client = _clientContext.CurrentClient;
        if (client is not null)
        {
            var link = _repository.GetLinkForClient(client.Id);
            if (link is not null)
            {
                foreach (var row in _repository.GetVoucherRecoveryCandidates(client.Id, client.FinancialYear, link.CompanyKey))
                    RecoveryItems.Add(row);
            }
        }
        RebuildRecoveryModules(previousModule, previousAnchor);
        NotifyAll();
        if (client is null) StatusText = "Open a client workspace to use the client-specific Tally module.";
        else if (_repository.GetLinkForClient(client.Id) is null) StatusText = "Select the open Tally company for this client and confirm the link.";
        else StatusText = RecoverySummary;
    }

    private void RefreshCompanyChoices()
    {
        var client = _clientContext.CurrentClient;
        var link = client is null ? null : _repository.GetLinkForClient(client.Id);
        var previousKey = _selectedCompany?.CompanyKey ?? string.Empty;
        OpenCompanies.Clear();
        foreach (var company in _coordinator.OpenCompanies) OpenCompanies.Add(company);
        var preferredKey = previousKey.Length > 0
            ? previousKey
            : link?.CompanyKey ?? _coordinator.SelectedCompany?.CompanyKey ?? string.Empty;
        _selectedCompany = OpenCompanies.FirstOrDefault(item => item.CompanyKey.Equals(preferredKey, StringComparison.OrdinalIgnoreCase))
            ?? (link is not null ? OpenCompanies.FirstOrDefault(item => item.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase)) : null)
            ?? (OpenCompanies.Count == 1 ? OpenCompanies[0] : null);
        OnPropertyChanged(nameof(SelectedCompany));
        OnPropertyChanged(nameof(ShowLinkAction));
        OnPropertyChanged(nameof(LinkActionText));
        OnPropertyChanged(nameof(HasLinkedCompany));
    }

    private void RebuildRecoveryModules(string preferredModule, string preferredAnchor)
    {
        RecoveryModules.Clear();
        foreach (var group in RecoveryItems.GroupBy(item => item.Module, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            RecoveryModules.Add(new TallyRecoveryModuleSummary
            {
                Module = group.Key,
                MissingCount = group.Count(),
                ReimportableCount = group.Count(item => item.CanReimport && item.VoucherDate is not null),
                ReviewBlockedCount = group.Count(item => !item.CanReimport || item.VoucherDate is null),
                Amount = group.Sum(item => Math.Abs(item.Amount))
            });
        }
        _selectedRecoveryModule = RecoveryModules.FirstOrDefault(item => item.Module.Equals(preferredModule, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(SelectedRecoveryModule));
        LoadSelectedModuleTransactions(preferredAnchor);
        OnPropertyChanged(nameof(SelectedModuleTitle));
        OnPropertyChanged(nameof(HasSelectedModule));
    }

    private void LoadSelectedModuleTransactions(string preferredAnchor = "")
    {
        SelectedModuleTransactions.Clear();
        if (SelectedRecoveryModule is not null)
        {
            foreach (var item in RecoveryItems
                         .Where(item => item.Module.Equals(SelectedRecoveryModule.Module, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(item => item.VoucherDate ?? DateTime.MaxValue)
                         .ThenBy(item => item.Reference, StringComparer.OrdinalIgnoreCase))
                SelectedModuleTransactions.Add(item);
        }
        _selectedRecoveryItem = SelectedModuleTransactions.FirstOrDefault(item => item.SourceAnchor.Equals(preferredAnchor, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(SelectedRecoveryItem));
        OnPropertyChanged(nameof(SelectedRecoveryDetail));
        RaiseCommandStates();
    }

    private async Task RefreshAsync()
    {
        try
        {
            StatusText = "Refreshing the selected Client/FY from complete live Tally authority…";
            await _coordinator.RefreshNowAsync(forceLinkedSynchronization: true);
            SyncCoordinatorPresentation(includeOperation: true);
            RefreshLocal();
            StatusText = RecoverySummary;
        }
        catch (Exception exception)
        {
            StatusText = "Tally refresh failed: " + exception.Message;
        }
    }

    private async Task LinkSelectedCompanyAsync()
    {
        try
        {
            await _coordinator.ConfirmSelectedCompanyAsync();
            SyncCoordinatorPresentation(includeOperation: true);
            RefreshLocal();
            StatusText = "Tally company link confirmed for " + CurrentClientScopeText + ".";
        }
        catch (Exception exception)
        {
            StatusText = "Tally link failed: " + exception.Message;
        }
    }

    private void Unlink()
    {
        if (System.Windows.MessageBox.Show(
                "Unlink this client from its Tally company? AAH accounting data and previous Tally audit history are retained.",
                "Unlink Tally company", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes)
            return;
        _coordinator.UnlinkCurrentClient();
        SyncCoordinatorPresentation(includeOperation: true);
        RefreshLocal();
        StatusText = "Tally company link removed for the selected client.";
    }

    private async Task ReimportSelectedAsync()
    {
        var selectedAnchor = SelectedRecoveryItem?.SourceAnchor ?? string.Empty;
        if (selectedAnchor.Length == 0) return;
        await ReimportAsync([selectedAnchor], "selected transaction");
    }

    private async Task ReimportSelectedModuleAsync()
    {
        if (SelectedRecoveryModule is null) return;
        var anchors = SelectedModuleTransactions
            .Where(item => item.CanReimport && item.VoucherDate is not null)
            .Select(item => item.SourceAnchor)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (anchors.Length == 0) return;
        await ReimportAsync(anchors, $"entire {SelectedRecoveryModule.Module} module");
    }

    private async Task ReimportAllDeletedAsync()
    {
        var anchors = RecoveryItems
            .Where(item => item.CanReimport && item.VoucherDate is not null)
            .Select(item => item.SourceAnchor)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (anchors.Length == 0) return;
        await ReimportAsync(anchors, "all eligible modules");
    }

    private async Task ReimportAsync(IReadOnlyCollection<string> requestedAnchors, string scopeLabel)
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        if (!CanExportTally())
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, GovernancePermissions.TallyExport, "Re-import deleted Tally vouchers");
            return;
        }
        try
        {
            // R37/R38 safety: one fresh full-FY baseline is mandatory immediately before
            // any explicit restore. A RebindRequired row can proceed only after this fresh
            // check and user confirmation; conflicts/pending local changes remain blocked.
            StatusText = "Checking current Tally authority before re-import…";
            await _coordinator.RefreshNowAsync(forceLinkedSynchronization: true);
            SyncCoordinatorPresentation(includeOperation: true);
            var link = _repository.GetLinkForClient(client.Id)
                ?? throw new InvalidOperationException("This client is not linked to a Tally company.");
            var latest = _repository.GetVoucherRecoveryCandidates(client.Id, client.FinancialYear, link.CompanyKey)
                .Where(item => requestedAnchors.Contains(item.SourceAnchor, StringComparer.OrdinalIgnoreCase))
                .Where(item => item.CanReimport && item.VoucherDate is not null)
                .ToArray();
            if (latest.Length == 0)
            {
                RefreshLocal();
                StatusText = "The selected recovery scope is no longer missing/deleted after the fresh Tally check. Nothing was recreated.";
                return;
            }

            var preview = string.Join("\n", latest.Take(8).Select(item =>
                $"{item.Module} · {item.StatusText} · {item.VoucherDate:dd-MMM-yyyy} · {Display(item.Reference)} · {item.Amount:N2}"));
            if (latest.Length > 8) preview += $"\n…and {latest.Length - 8:N0} more";
            var rebindCount = latest.Count(item => item.State.Equals("RebindRequired", StringComparison.OrdinalIgnoreCase));
            var rebindWarning = rebindCount > 0
                ? $"\n\n{rebindCount:N0} row(s) are identity-rebuild/rebind cases. Your confirmation explicitly adopts them for duplicate-safe recovery after this fresh full-FY check; exact live matches will be rebound instead of CREATED."
                : string.Empty;
            var editedConflictCount = latest.Count(item => item.State.Equals("ExternalDeleteConflict", StringComparison.OrdinalIgnoreCase));
            var changedSourceWarning = editedConflictCount > 0
                ? $"\n\n{editedConflictCount:N0} deleted Voucher(s) also contain unverified local edits. Approving reimport will archive their old and pending versions, check the live voucher authority and use CURRENT AAH values for a NEW CREATE. If the old MASTERID still exists, they will not be recreated."
                : string.Empty;
            var answer = System.Windows.MessageBox.Show(
                $"Re-import {latest.Length:N0} missing/deleted AAH Voucher(s) from {scopeLabel} to the linked Tally company?\n\n{preview}{rebindWarning}{changedSourceWarning}\n\n" +
                "AAH checks live duplicates again and sends only these source anchors. Unverified or changed entries require this explicit approval; ambiguous live matches still block CREATE.",
                "Re-import Deleted from AAH", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                StatusText = "Re-import cancelled. No Tally data was changed.";
                return;
            }

            var approved = new List<TallyVoucherRecoveryCandidate>();
            foreach (var item in latest)
            {
                if (item.State.Equals("RestoreRequested", StringComparison.OrdinalIgnoreCase))
                {
                    approved.Add(item);
                    continue;
                }

                var expectedState = item.State;
                if (expectedState.Equals("ExternalDeleteConflict", StringComparison.OrdinalIgnoreCase))
                {
                    // AAH may have changed after the Tally deletion. Only a complete
                    // fresh FY snapshot proving the OLD MASTERID is still absent
                    // permits explicit user recovery; pending edits remain auditable.
                    if (item.PreviousMasterId.Length == 0 ||
                        !_repository.HasCompleteVoucherXmlAuthority(link.CompanyKey, client.FinancialYear) ||
                        _repository.FindVoucherByMasterId(link.CompanyKey, client.FinancialYear, item.PreviousMasterId) is not null)
                        continue;
                }
                if (expectedState.Equals("RebindRequired", StringComparison.OrdinalIgnoreCase))
                {
                    var adopted = _repository.ConfirmRebindRequiredAsDeletedForExplicitRestore(
                        client.Id, client.FinancialYear, link.CompanyKey, item.EntityType, item.SourceAnchor,
                        $"User explicitly approved generation/rebind recovery after fresh full-FY authority for {item.Module} {item.VoucherDate:dd-MMM-yyyy} {item.Reference}.");
                    if (!adopted) continue;
                    expectedState = "DeletedInTally";
                }

                if (_repository.RequestVoucherRestoreToTally(
                        client.Id, client.FinancialYear, link.CompanyKey, item.EntityType,
                        item.SourceAnchor, expectedState,
                        $"User approved Restore-to-Tally from client-specific Tally module for {item.Module} {item.VoucherDate:dd-MMM-yyyy} {item.Reference}.",
                        allowExplicitReviewedLocalChanges: expectedState.Equals("ExternalDeleteConflict", StringComparison.OrdinalIgnoreCase)))
                    approved.Add(item);
            }
            if (approved.Count == 0)
            {
                RefreshLocal();
                StatusText = "The selected recovery rows changed while approval was open. Nothing was recreated; refresh and review again.";
                return;
            }

            var request = BuildRestoreRequest(approved, client.FinancialYear);
            StatusText = $"Re-importing {approved.Count:N0} explicitly approved Voucher(s)…";
            var result = await _coordinator.ExportSelectedAsync(request);
            SyncCoordinatorPresentation(includeOperation: true);
            RefreshLocal();
            StatusText = result.Status;
        }
        catch (Exception exception)
        {
            RefreshLocal();
            StatusText = "Re-import failed safely: " + exception.Message + " The restore request remains auditable and duplicate checks will run again on retry.";
        }
    }

    private static TallyVoucherExportRequest BuildRestoreRequest(
        IReadOnlyCollection<TallyVoucherRecoveryCandidate> items, string financialYear)
    {
        // Source date may have changed in AAH after its Voucher was deleted from Tally.
        // One FY-scoped preparation per module finds CURRENT source rows; the strict
        // RestoreSourceAnchors filter ensures no other source can be sent.
        var startPart = (financialYear ?? string.Empty).Split('-')[0];
        if (!int.TryParse(startPart, out var startYear) || startYear < 1900 || startYear > 9998)
            throw new InvalidOperationException("Invalid Client financial year for deleted-Voucher recovery.");
        var yearStart = new DateTime(startYear, 4, 1);
        var yearEnd = yearStart.AddYears(1).AddDays(-1);
        TallyVoucherExportRange Range() => new() { FromDate = yearStart, ToDate = yearEnd };
        var bank = new List<TallyVoucherExportRange>();
        var purchase = new List<TallyVoucherExportRange>();
        var sales = new List<TallyVoucherExportRange>();
        var cash = new List<TallyVoucherExportRange>();
        var journal = new List<TallyVoucherExportRange>();
        foreach (var item in items)
        {
            if (item.VoucherDate is null) continue;
            var range = Range();
            switch (item.EntityType)
            {
                case "Bank Voucher": bank.Add(range); break;
                case "Purchase Voucher": purchase.Add(range); break;
                case "Sales Voucher": sales.Add(range); break;
                case "Cash Voucher": cash.Add(range); break;
                case "Manual Journal":
                case "GST Journal": journal.Add(range); break;
            }
        }
        static IReadOnlyList<TallyVoucherExportRange> Unique(IEnumerable<TallyVoucherExportRange> values) =>
            values.GroupBy(value => (value.FromDate.Date, value.ToDate.Date)).Select(group => group.First()).OrderBy(value => value.FromDate).ToArray();
        return new TallyVoucherExportRequest
        {
            BankStatementRanges = Unique(bank),
            PurchaseRanges = Unique(purchase),
            SalesRanges = Unique(sales),
            CashEntryRanges = Unique(cash),
            JournalRanges = Unique(journal),
            RestoreSourceAnchors = items.Select(item => item.SourceAnchor).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            // Only the explicit Tally Recovery/Restore path enables missing Final Ledger
            // to use the normal Bank CREATE suspense fallback. Manual Export is unchanged.
            AllowBankSuspenseForUnmapped = bank.Count > 0
        };
    }

    private bool CanViewTally() =>
        !NetworkClientRuntime.IsNetworkClient &&
        _clientContext.CurrentClient is not null &&
        DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.TallyView) &&
        !_uiIsBusy;

    private bool CanExportTally() =>
        !NetworkClientRuntime.IsNetworkClient &&
        _clientContext.CurrentClient is not null &&
        DesktopPermissionService.CanMutateAccounting(_governanceRepository, GovernancePermissions.TallyExport) &&
        _uiIsConnected && !_uiIsBusy &&
        _repository.GetLinkForClient(_clientContext.CurrentClient.Id) is not null;

    private bool CanLink() => CanViewTally() && SelectedCompany is not null && ShowLinkAction;
    private bool CanUnlink() => CanViewTally() && HasLinkedCompany;
    private bool CanReimportSelected() => CanExportTally() && SelectedRecoveryItem?.CanReimport == true && SelectedRecoveryItem.VoucherDate is not null;
    private bool CanReimportSelectedModule() => CanExportTally() && SelectedModuleTransactions.Any(item => item.CanReimport && item.VoucherDate is not null);
    private bool CanReimportAny() => CanExportTally() && RecoveryItems.Any(item => item.CanReimport && item.VoucherDate is not null);

    private void OnClientContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveClientContext.CurrentClient)) return;
        SyncCoordinatorPresentation(includeOperation: true);
        RefreshLocal();
    }

    private void OnCoordinatorChanged(object? sender, EventArgs e)
    {
        // R38 invisible heartbeat: coordinator status text, company discovery and ALTERID
        // timestamps are background authority only. Never mirror those pulse-by-pulse
        // changes into the page. Explicit page actions call SyncCoordinatorPresentation
        // themselves. The only unsolicited UI signal allowed is a real online/offline
        // transition, and even that updates only the connection fields/command state.
        if (_uiIsConnected == _coordinator.IsConnected) return;
        _uiIsConnected = _coordinator.IsConnected;
        _uiConnectionStatus = _coordinator.ConnectionStatus;
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionStatus));
        RaiseCommandStates();
    }

    private void SyncCoordinatorPresentation(bool includeOperation)
    {
        if (!_uiConnectionStatus.Equals(_coordinator.ConnectionStatus, StringComparison.Ordinal))
        {
            _uiConnectionStatus = _coordinator.ConnectionStatus;
            OnPropertyChanged(nameof(ConnectionStatus));
        }
        if (_uiIsConnected != _coordinator.IsConnected)
        {
            _uiIsConnected = _coordinator.IsConnected;
            OnPropertyChanged(nameof(IsConnected));
        }
        if (_uiIsBusy != _coordinator.IsBusy)
        {
            _uiIsBusy = _coordinator.IsBusy;
            OnPropertyChanged(nameof(IsBusy));
        }
        if (includeOperation && !_uiLastTallyOperation.Equals(_coordinator.LastOperationStatus, StringComparison.Ordinal))
        {
            _uiLastTallyOperation = _coordinator.LastOperationStatus;
            OnPropertyChanged(nameof(LastTallyOperation));
        }
        OnPropertyChanged(nameof(LinkedCompanyText));
        OnPropertyChanged(nameof(AuthorityText));
        OnPropertyChanged(nameof(HasLinkedCompany));
        OnPropertyChanged(nameof(ShowLinkAction));
        OnPropertyChanged(nameof(LinkActionText));
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(CurrentClientScopeText));
        OnPropertyChanged(nameof(LinkedCompanyText));
        OnPropertyChanged(nameof(AuthorityText));
        OnPropertyChanged(nameof(HasLinkedCompany));
        OnPropertyChanged(nameof(ShowLinkAction));
        OnPropertyChanged(nameof(LinkActionText));
        OnPropertyChanged(nameof(MissingOrDeletedCount));
        OnPropertyChanged(nameof(ReimportableCount));
        OnPropertyChanged(nameof(HasRecoveryItems));
        OnPropertyChanged(nameof(RecoverySummary));
        OnPropertyChanged(nameof(SelectedRecoveryDetail));
        OnPropertyChanged(nameof(SelectedModuleTitle));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        LinkSelectedCompanyCommand.RaiseCanExecuteChanged();
        UnlinkCommand.RaiseCanExecuteChanged();
        ReimportSelectedCommand.RaiseCanExecuteChanged();
        ReimportSelectedModuleCommand.RaiseCanExecuteChanged();
        ReimportAllDeletedCommand.RaiseCanExecuteChanged();
    }

    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clientContext.PropertyChanged -= OnClientContextChanged;
        _coordinator.Changed -= OnCoordinatorChanged;
    }
}
