using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using AccountingAutomationHub.Application.Imports;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Desktop.Collections;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Desktop.Views;
using AccountingAutomationHub.Domain.Imports;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Domain.Gst;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Infrastructure.Sales;
using AccountingAutomationHub.Infrastructure.Automation;
using AccountingAutomationHub.Infrastructure.Gst;
using AccountingAutomationHub.Infrastructure.Governance;
using Microsoft.Win32;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class SalesImportViewModel :
    PageViewModel
{
    private readonly ISalesAutoImportService _autoImportService;
    private readonly ISalesManualImportService _manualImportService;
    private readonly ISalesRecordRepository _salesRecordRepository;
    private readonly ISalesMappingRepository _salesMappingRepository;
    private readonly IMasterRepository _masterRepository;
    private readonly SalesStockNormalizationService _stockNormalizationService;
    private readonly IStockMasterRepository _stockRepository;
    private readonly IActionRequiredRepository _actionRepository;
    private readonly EReturnsRootContext _eReturnsRootContext;
    private readonly ActiveClientContext _clientContext;
    private readonly IImportActivityRepository _activityRepository;
    private readonly ImportActivityContext _activityContext;
    private readonly ApplicationPreferenceService? _applicationPreferences;
    private readonly AccountingRefreshCoordinator _refreshCoordinator;
    private readonly AccountingLiveChangeHub _liveChangeHub;
    private readonly BackgroundAutomationService? _automationService;
    private readonly SqliteGovernanceRepository _governanceRepository;
    private readonly SqliteAutomationRepository _automationRepository;

    private CancellationTokenSource? _cancellationSource;
    private long _automaticImportGeneration;
    private readonly DispatcherTimer _liveEditSaveTimer;
    private readonly HashSet<long> _pendingLiveEditIds = [];
    // Fix121: a source is checked at most once per app session for the legacy
    // cross-voucher manual-Sales GSTIN carry defect. The repair re-parses the
    // original source and updates ONLY CustomerGstin when row identity agrees.
    private readonly HashSet<string> _fix121GstinRepairAttempts = new(StringComparer.OrdinalIgnoreCase);
    private bool _liveEditSaveRunning;

    private string _eReturnsRootPath = string.Empty;
    private string _companyName = string.Empty;
    private string _clientGstin = string.Empty;
    private string _financialYear = string.Empty;
    private string _sourceVerificationText = string.Empty;
    private string _statusText =
        "Automatic Sales import is waiting for an active client.";
    private string _warningText = string.Empty;
    private string _errorText = string.Empty;
    private string _editStatusText = string.Empty;
    private int _progressPercent;
    private bool _isBusy;
    private bool _isImportPanelExpanded;
    private bool _isB2BPanelExpanded;
    private SalesB2BCustomerMatchRow? _selectedB2BCustomer;
    private string _b2bManualName = string.Empty;
    private string _b2bSearchText = string.Empty;
    private string _b2bPanelStatusText = string.Empty;
    private string _b2cSearchText = string.Empty;
    private string _b2cPanelStatusText = string.Empty;
    private Dictionary<string, SalesCustomerMasterNameDetail> _b2bSourceNameDetails = new(StringComparer.OrdinalIgnoreCase);
    private bool _b2bDuplicateCleanupRunning;
    private bool _b2bRenameSessionActive;
    private bool _b2bRenameRefreshPending;
    private ClientWorkspaceSnapshot? _deferredSalesSnapshot;
    private string _presentedSalesFingerprint = string.Empty;
    private bool _saveSourceFiles;
    private long _preparedClientId;
    private string _preparedFinancialYear = string.Empty;
    private SalesImportSummary _summary = new();
    private int _inventoryRows;
    private int _actionRequiredCount;

    public SalesImportViewModel(
        ISalesAutoImportService autoImportService,
        ISalesManualImportService manualImportService,
        ISalesRecordRepository salesRecordRepository,
        ISalesMappingRepository salesMappingRepository,
        IMasterRepository masterRepository,
        SalesStockNormalizationService stockNormalizationService,
        IStockMasterRepository stockRepository,
        IActionRequiredRepository actionRepository,
        EReturnsRootContext eReturnsRootContext,
        ActiveClientContext clientContext,
        IImportActivityRepository activityRepository,
        ImportActivityContext activityContext,
        AccountingRefreshCoordinator refreshCoordinator,
        AccountingLiveChangeHub liveChangeHub,
        ApplicationPreferenceService? applicationPreferences = null,
        BackgroundAutomationService? automationService = null)
        : base(
            "Sales",
            "Automatic GSTR-1 plus manual inventory Sales / Credit Note")
    {
        _autoImportService = autoImportService;
        _manualImportService = manualImportService;
        _salesRecordRepository = salesRecordRepository;
        _salesMappingRepository = salesMappingRepository;
        _masterRepository = masterRepository;
        _stockNormalizationService = stockNormalizationService;
        _stockRepository = stockRepository;
        _actionRepository = actionRepository;
        _eReturnsRootContext = eReturnsRootContext;
        _clientContext = clientContext;
        _activityRepository = activityRepository;
        _activityContext = activityContext;
        _refreshCoordinator = refreshCoordinator;
        _liveChangeHub = liveChangeHub;
        _applicationPreferences = applicationPreferences;
        _automationService = automationService;
        _governanceRepository = new SqliteGovernanceRepository();
        _governanceRepository.Initialize();
        _automationRepository = new SqliteAutomationRepository();
        _automationRepository.Initialize();

        _liveEditSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _liveEditSaveTimer.Tick += OnLiveEditSaveTimerTick;

        _clientContext.PropertyChanged +=
            OnClientContextPropertyChanged;

        _eReturnsRootContext.PropertyChanged +=
            OnEReturnsRootPropertyChanged;

        BrowseEReturnsRootCommand = new RelayCommand(
            BrowseEReturnsRoot,
            () => !IsBusy && CanImportSales);

        CancelCommand = new RelayCommand(
            Cancel,
            () => IsBusy);

        SaveChangesCommand = new RelayCommand(
            SaveChanges,
            CanSaveChanges);

        RefreshCommand = new RelayCommand(
            ScheduleAutomaticImport,
            () => !IsBusy && CanImportSales);

        ReimportCommand = new AsyncRelayCommand(
            ReimportAutomaticSourcesAsync,
            () => !IsBusy && _clientContext.CurrentClient is not null && CanImportSales);

        ManualImportCommand = new RelayCommand(
            ManualImportSalesRegister,
            () => !IsBusy && _clientContext.CurrentClient is not null && CanImportSales);

        RemapSalesRegisterCommand = new RelayCommand(
            RemapSalesRegister,
            () => !IsBusy && _clientContext.CurrentClient is not null && CanImportSales);

        ClearModuleCommand = new RelayCommand(
            ClearSalesModule,
            () => !IsBusy && _clientContext.CurrentClient is not null && CanClearSales);

        ItemNameMappingCommand = new RelayCommand(
            OpenItemNameMapping,
            () => !IsBusy && CanEditSales && AllRows.Any(HasInventoryEvidenceForItemName));

        UnitMappingCommand = new RelayCommand(
            OpenUnitMapping,
            () => !IsBusy && CanEditSales && AllRows.Any(row => !string.IsNullOrWhiteSpace(row.Unit)));

        QuantityConversionCommand = new RelayCommand(
            OpenQuantityConversion,
            () => !IsBusy && CanEditSales && AllRows.Any(row => !string.IsNullOrWhiteSpace(row.ItemName) && !string.IsNullOrWhiteSpace(row.Unit)));

        ApplyB2BManualNameCommand = new RelayCommand(
            ApplyB2BManualName,
            CanApplyB2BManualName);

        ApplyClientContext(loadRows: false);
        // Fix38: hidden Sales construction is passive. One application-level
        // Sales/Purchase source watcher owns unattended discovery and Auto Import.
    }

    public string EReturnsRootPath
    {
        get => _eReturnsRootPath;
        private set => SetProperty(
            ref _eReturnsRootPath,
            value);
    }

    public string CompanyName
    {
        get => _companyName;
        private set => SetProperty(
            ref _companyName,
            value);
    }

    public string ClientGstin
    {
        get => _clientGstin;
        private set => SetProperty(
            ref _clientGstin,
            value);
    }

    public string FinancialYear
    {
        get => _financialYear;
        private set => SetProperty(
            ref _financialYear,
            value);
    }

    public string SourceVerificationText
    {
        get => _sourceVerificationText;
        private set => SetProperty(
            ref _sourceVerificationText,
            value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(
            ref _statusText,
            value);
    }

    public string WarningText
    {
        get => _warningText;
        private set => SetProperty(
            ref _warningText,
            value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(
            ref _errorText,
            value);
    }

    public string EditStatusText
    {
        get => _editStatusText;
        private set => SetProperty(
            ref _editStatusText,
            value);
    }

    public int ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(
            ref _progressPercent,
            value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(
                    ref _isBusy,
                    value))
            {
                RefreshCommands();
            }
        }
    }

    public bool IsImportPanelExpanded
    {
        get => _isImportPanelExpanded;
        set => SetProperty(
            ref _isImportPanelExpanded,
            value);
    }

    public bool IsB2BPanelExpanded
    {
        get => _isB2BPanelExpanded;
        set => SetProperty(ref _isB2BPanelExpanded, value);
    }

    public SalesB2BCustomerMatchRow? SelectedB2BCustomer
    {
        get => _selectedB2BCustomer;
        set
        {
            if (!SetProperty(ref _selectedB2BCustomer, value)) return;
            B2BManualName = value?.CustomerName ?? string.Empty;
            ApplyB2BManualNameCommand.RaiseCanExecuteChanged();
        }
    }

    public string B2BManualName
    {
        get => _b2bManualName;
        set
        {
            if (!SetProperty(ref _b2bManualName, value)) return;
            ApplyB2BManualNameCommand.RaiseCanExecuteChanged();
        }
    }

    public string B2BSearchText
    {
        get => _b2bSearchText;
        set
        {
            if (!SetProperty(ref _b2bSearchText, value ?? string.Empty)) return;
            RefreshB2BFilteredLists();
        }
    }


    public string B2CSearchText
    {
        get => _b2cSearchText;
        set { if (SetProperty(ref _b2cSearchText, value ?? string.Empty)) RefreshB2CFilteredLists(); }
    }
    public string B2CPanelStatusText { get => _b2cPanelStatusText; private set => SetProperty(ref _b2cPanelStatusText, value); }
    public int B2CMatchedCount => B2CCustomers.Count(row => row.IsMatched);
    public int B2CUnmatchedCount => B2CCustomers.Count(row => !row.IsMatched);
    public bool AreAllB2CCustomersMatched => B2CCustomers.Count > 0 && B2CUnmatchedCount == 0;
    public string B2CCompletionText => B2CCustomers.Count == 0 ? "No B2C source names" : AreAllB2CCustomersMatched ? "All B2C source names renamed" : $"{B2CUnmatchedCount:N0} B2C source name(s) need a debtor ledger";
    public string B2BPanelStatusText
    {
        get => _b2bPanelStatusText;
        private set => SetProperty(ref _b2bPanelStatusText, value);
    }

    public int B2BMatchedCount => B2BCustomers.Count(row => row.IsMatched);
    public int B2BUnmatchedCount => B2BCustomers.Count(row => !row.IsMatched);
    public bool AreAllB2BCustomersMatched => B2BCustomers.Count > 0 && B2BUnmatchedCount == 0;
    public string B2BCompletionText => B2BCustomers.Count == 0
        ? "No registered B2B GSTINs"
        : AreAllB2BCustomersMatched
            ? "All B2B GSTINs matched"
            : $"{B2BUnmatchedCount:N0} GSTIN(s) still need a customer name";

    public bool SaveSourceFiles
    {
        get => _saveSourceFiles;
        set => SetProperty(
            ref _saveSourceFiles,
            value);
    }

    public bool HasWarning =>
        WarningText.Length > 0;

    public bool HasError =>
        ErrorText.Length > 0;

    public bool HasPersistedRows =>
        AllRows.Any(record =>
            record.PersistentId > 0);

    public SalesImportSummary Summary
    {
        get => _summary;
        private set => SetProperty(
            ref _summary,
            value);
    }

    public int InventoryRows
    {
        get => _inventoryRows;
        private set => SetProperty(ref _inventoryRows, value);
    }

    public int ActionRequiredCount
    {
        get => _actionRequiredCount;
        private set => SetProperty(ref _actionRequiredCount, value);
    }

    public bool HasActionRequired => ActionRequiredCount > 0;

    public ObservableCollection<SalesImportRecord>
        SalesRows { get; } = new BulkObservableCollection<SalesImportRecord>();

    public ObservableCollection<SalesImportRecord>
        CreditNoteRows { get; } = new BulkObservableCollection<SalesImportRecord>();

    public ObservableCollection<SalesImportRecord>
        AllRows { get; } = new BulkObservableCollection<SalesImportRecord>();

    public ObservableCollection<SalesImportIssue>
        Issues { get; } = new BulkObservableCollection<SalesImportIssue>();

    public ObservableCollection<SalesB2BCustomerMatchRow>
        B2BCustomers { get; } = new BulkObservableCollection<SalesB2BCustomerMatchRow>();

    public ObservableCollection<SalesB2BCustomerMatchRow>
        B2BMatchedCustomers { get; } = new BulkObservableCollection<SalesB2BCustomerMatchRow>();

    public ObservableCollection<SalesB2BCustomerMatchRow>
        B2BUnmatchedCustomers { get; } = new BulkObservableCollection<SalesB2BCustomerMatchRow>();

    private bool _b2cRenameSessionActive;
    private bool _b2cRenameRefreshPending;

    public ObservableCollection<SalesB2CCustomerMatchRow> B2CCustomers { get; } = new BulkObservableCollection<SalesB2CCustomerMatchRow>();
    public ObservableCollection<SalesB2CCustomerMatchRow> B2CVisibleCustomers { get; } = new BulkObservableCollection<SalesB2CCustomerMatchRow>();
    public ObservableCollection<SalesB2CCustomerMatchRow> B2CMatchedCustomers { get; } = new BulkObservableCollection<SalesB2CCustomerMatchRow>();
    public ObservableCollection<SalesB2CCustomerMatchRow> B2CUnmatchedCustomers { get; } = new BulkObservableCollection<SalesB2CCustomerMatchRow>();
    public ObservableCollection<string> B2CDebtorLedgerOptions { get; } = new BulkObservableCollection<string>();

    public RelayCommand BrowseEReturnsRootCommand { get; }
    public RelayCommand CancelCommand { get; }
    public bool CanImportSales => DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.Import) &&
        (!NetworkClientRuntime.IsNetworkClient || !NetworkClientRuntime.AccountingAccessSuspended);

    public bool CanReimportSales => !IsBusy && _clientContext.CurrentClient is not null && CanImportSales;

    public bool CanEditSales => (!NetworkClientRuntime.IsNetworkClient || !NetworkClientRuntime.AccountingAccessSuspended) &&
        (DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.SalesEdit) ||
         DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.EditAccounting));

    public bool IsSalesReadOnly => !CanEditSales;

    public bool CanClearSales => (!NetworkClientRuntime.IsNetworkClient || !NetworkClientRuntime.AccountingAccessSuspended) &&
        (DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.DeleteAccounting) ||
         DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.SalesEdit));

    public string SalesPermissionSummary => $"Role: {DesktopPermissionService.EffectiveRoleName(_governanceRepository)} • {(NetworkClientRuntime.IsNetworkClient ? "Windows processing node • " : string.Empty)}Import {(CanImportSales ? "allowed" : "denied")} • Edit {(CanEditSales ? "allowed" : "denied")}";

    public RelayCommand SaveChangesCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ReimportCommand { get; }
    public RelayCommand ManualImportCommand { get; }
    public RelayCommand RemapSalesRegisterCommand { get; }
    public RelayCommand ClearModuleCommand { get; }
    public RelayCommand ItemNameMappingCommand { get; }
    public RelayCommand UnitMappingCommand { get; }
    public RelayCommand QuantityConversionCommand { get; }
    public RelayCommand ApplyB2BManualNameCommand { get; }

    public void ToggleB2BPanel()
    {
        IsB2BPanelExpanded = !IsB2BPanelExpanded;
        if (IsB2BPanelExpanded && SelectedB2BCustomer is null)
            SelectedB2BCustomer = B2BCustomers.FirstOrDefault(row => !row.IsMatched) ?? B2BCustomers.FirstOrDefault();
    }

    private bool CanApplyB2BManualName() =>
        !IsBusy && CanEditSales &&
        SelectedB2BCustomer is not null &&
        SelectedB2BCustomer.Gstin.Length == 15 &&
        !string.IsNullOrWhiteSpace(B2BManualName);

    public bool SaveB2BCustomerNameFromPanel(string gstin, string customerName)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || !CanEditSales || IsBusy) return false;

        gstin = NormalizeGstin(gstin);
        var name = AccountingText.DisplayName(customerName);
        if (gstin.Length != 15 || string.IsNullOrWhiteSpace(name)) return false;

        var current = B2BCustomers.FirstOrDefault(row =>
            row.Gstin.Equals(gstin, StringComparison.OrdinalIgnoreCase));
        if (current is not null && current.IsMatched &&
            AccountingText.DisplayName(current.CustomerName).Equals(name, StringComparison.Ordinal))
        {
            B2BPanelStatusText = $"{gstin} already uses {name}.";
            return true;
        }

        // An exact manual copy of this GSTIN's authoritative Recipient/Trade name is redundant.
        // Remove the override and keep the source name instead.
        if (IsOwnAuthoritativeB2BName(gstin, name))
        {
            var removed = RemoveB2BManualOverrideAndRestoreOriginal(client.Id, client.FinancialYear, gstin);
            if (removed)
            {
                var issues = Issues.ToArray();
                LoadSavedRows(client.Id, client.FinancialYear, issues);
                B2BPanelStatusText = $"Manual name removed for {gstin}; the original Recipient/Trade name is retained.";
                EditStatusText = B2BPanelStatusText;
                _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Sales, "B2B duplicate manual name removed");
                _ = RefreshAccountingAfterSalesChangeAsync(client);
                return true;
            }
        }

        var duplicate = FindB2BNameDuplicate(gstin, name);
        if (duplicate is not null)
        {
            var hadManualOverride = _salesRecordRepository
                .GetB2BCustomerNameOverrides(client.Id, client.FinancialYear)
                .ContainsKey(gstin);

            if (hadManualOverride)
            {
                RemoveB2BManualOverrideAndRestoreOriginal(client.Id, client.FinancialYear, gstin);
                var issues = Issues.ToArray();
                LoadSavedRows(client.Id, client.FinancialYear, issues);
                B2BPanelStatusText = $"Duplicate blocked: {name} already belongs to {duplicate.Value.Gstin} ({duplicate.Value.Field}). The manual name for {gstin} was deleted and its original name was restored.";
                EditStatusText = B2BPanelStatusText;
                _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Sales, "B2B duplicate manual name removed");
                _ = RefreshAccountingAfterSalesChangeAsync(client);
                return true;
            }

            B2BPanelStatusText = $"Duplicate blocked: {name} already belongs to {duplicate.Value.Gstin} ({duplicate.Value.Field}). Enter a different customer name.";
            EditStatusText = B2BPanelStatusText;
            return false;
        }

        var issuesBeforeSave = Issues.ToArray();
        var updated = _salesRecordRepository.SaveB2BCustomerNameOverride(
            client.Id,
            client.FinancialYear,
            gstin,
            name);

        LoadSavedRows(client.Id, client.FinancialYear, issuesBeforeSave);
        SelectedB2BCustomer = B2BCustomers.FirstOrDefault(row =>
            row.Gstin.Equals(gstin, StringComparison.OrdinalIgnoreCase));
        B2BManualName = name;
        B2BPanelStatusText = $"Saved {name} for {gstin}. {updated:N0} Sales/Credit Note row(s) updated automatically.";
        EditStatusText = B2BPanelStatusText + " This did not create a Customer Master.";
        _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Sales, "B2B customer name assigned");
        _ = RefreshAccountingAfterSalesChangeAsync(client);
        return true;
    }

    public async Task<bool> SaveB2BCustomerNameFromPanelAsync(string gstin, string customerName)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || !CanEditSales || IsBusy) return false;
        gstin = NormalizeGstin(gstin);
        var name = AccountingText.DisplayName(customerName);
        if (gstin.Length != 15 || string.IsNullOrWhiteSpace(name)) return false;

        // Duplicate/source-authority corrections are uncommon and retain the existing exact
        // path. Normal renames use an immediate cache update + background SQLite commit so
        // the Sales window never freezes while hundreds of rows are propagated.
        if (IsOwnAuthoritativeB2BName(gstin, name) || FindB2BNameDuplicate(gstin, name) is not null)
            return SaveB2BCustomerNameFromPanel(gstin, name);

        var current = B2BCustomers.FirstOrDefault(row => row.Gstin.Equals(gstin, StringComparison.OrdinalIgnoreCase));
        if (current is not null)
        {
            current.CustomerName = name;
            current.NameSource = "Manual B2B Panel";
            current.Status = "Matched";
            current.IsMatched = true;
        }
        B2BManualName = name;
        OnPropertyChanged(nameof(B2BMatchedCount));
        OnPropertyChanged(nameof(B2BUnmatchedCount));
        OnPropertyChanged(nameof(AreAllB2BCustomersMatched));
        OnPropertyChanged(nameof(B2BCompletionText));
        RefreshB2BFilteredLists();
        B2BPanelStatusText = $"Saving {name} for {gstin} in background...";
        var issuesBeforeSave = Issues.ToArray();
        try
        {
            var updated = await Task.Run(() => _salesRecordRepository.SaveB2BCustomerNameOverride(client.Id, client.FinancialYear, gstin, name));
            // Keep the edited row stable until the editor closes. The durable repository
            // override already updates every matching Sales/CDNR record. A single cache
            // refresh is allowed after the rename session ends instead of rebuilding the
            // DataGrid in the middle of LostFocus/Enter handling.
            if (!_b2bRenameSessionActive)
            {
                LoadSavedRows(client.Id, client.FinancialYear, issuesBeforeSave);
                _presentedSalesFingerprint = BuildSalesPresentationFingerprint(AllRows.ToArray());
            }
            SelectedB2BCustomer = B2BCustomers.FirstOrDefault(row => row.Gstin.Equals(gstin, StringComparison.OrdinalIgnoreCase));
            B2BPanelStatusText = $"Saved {name} for {gstin}. {updated:N0} Sales/Credit Note row(s) updated automatically.";
            EditStatusText = B2BPanelStatusText + " This did not create a Customer Master.";
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Sales, "B2B customer name assigned");
            _ = RefreshAccountingAfterSalesChangeAsync(client);
            return true;
        }
        catch (Exception ex)
        {
            LoadSavedRows(client.Id, client.FinancialYear, issuesBeforeSave);
            B2BPanelStatusText = "Could not save B2B name: " + ex.Message;
            return false;
        }
    }

    public void BeginB2BCustomerRenameSession()
    {
        _b2bRenameSessionActive = true;
        _b2bRenameRefreshPending = false;
        _deferredSalesSnapshot = null;
    }

    public void EndB2BCustomerRenameSession()
    {
        if (!_b2bRenameSessionActive) return;
        _b2bRenameSessionActive = false;

        var refreshPending = _b2bRenameRefreshPending;
        _b2bRenameRefreshPending = false;
        var deferred = _deferredSalesSnapshot;
        _deferredSalesSnapshot = null;
        if (refreshPending && deferred is not null)
        {
            WarmFromPreparedSnapshot(deferred);
            return;
        }

        // Close is the one deliberate synchronization point for the editor. Even if the
        // prepared-cache event is delayed or failed, reload the durable override once so the
        // underlying Sales grid and the next B2B open cannot retain a stale customer name.
        var client = _clientContext.CurrentClient;
        if (client is not null)
        {
            LoadSavedRows(client.Id, client.FinancialYear, Issues.ToArray());
            _presentedSalesFingerprint = BuildSalesPresentationFingerprint(AllRows.ToArray());
        }
        else RebuildB2BCustomerPanel(AllRows.ToArray());
    }

    public int DeleteAllB2BManualNames()
    {
        var client = _clientContext.CurrentClient;
        if (client is null || !CanEditSales || IsBusy) return 0;

        var overrides = _salesRecordRepository
            .GetB2BCustomerNameOverrideEntries(client.Id, client.FinancialYear)
            .GroupBy(entry => NormalizeGstin(entry.Gstin), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .Where(gstin => gstin.Length == 15)
            .ToArray();

        if (overrides.Length == 0)
        {
            B2BPanelStatusText = "There are no manually entered B2B names to delete.";
            EditStatusText = B2BPanelStatusText;
            return 0;
        }

        var affectedRows = 0;
        foreach (var gstin in overrides)
        {
            var replacement = string.Empty;
            var source = string.Empty;
            if (_b2bSourceNameDetails.TryGetValue(gstin, out var detail))
            {
                replacement = AccountingText.DisplayName(detail.PreferredName);
                source = replacement.Length > 0 ? "GST Recipient Master" : string.Empty;
            }

            affectedRows += _salesRecordRepository.DeleteB2BCustomerNameOverride(
                client.Id,
                client.FinancialYear,
                gstin,
                replacement,
                source);
        }

        var issues = Issues.ToArray();
        LoadSavedRows(client.Id, client.FinancialYear, issues);
        SelectedB2BCustomer = B2BCustomers.FirstOrDefault(row => !row.IsMatched) ?? B2BCustomers.FirstOrDefault();
        B2BManualName = SelectedB2BCustomer?.CustomerName ?? string.Empty;
        B2BPanelStatusText = $"Deleted {overrides.Length:N0} manually entered B2B name(s). Original Recipient/Trade names were restored where available; {affectedRows:N0} Sales/Credit Note row(s) were refreshed.";
        EditStatusText = B2BPanelStatusText;
        _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Sales, "All manual B2B customer names deleted");
        _ = RefreshAccountingAfterSalesChangeAsync(client);
        return overrides.Length;
    }

    private void ApplyB2BManualName()
    {
        var selected = SelectedB2BCustomer;
        if (selected is null) return;
        SaveB2BCustomerNameFromPanel(selected.Gstin, B2BManualName);
    }

    private void ManualImportSalesRegister()
    {
        if (!CanImportSales)
        {
            EditStatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "import Sales", accountingMutation: true);
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Select Sales Register or Tally Voucher XML",
            Filter = "Sales sources (*.xlsx;*.xlsm;*.xls;*.xml)|*.xlsx;*.xlsm;*.xls;*.xml|Excel files (*.xlsx;*.xlsm;*.xls)|*.xlsx;*.xlsm;*.xls|Tally Voucher XML (*.xml)|*.xml",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            IsBusy = true;
            ErrorText = string.Empty;
            WarningText = string.Empty;
            EditStatusText = string.Empty;
            StatusText = "Inspecting the Sales Register and applying existing header mappings...";

            var firstInspection = _manualImportService.Inspect(dialog.FileName);
            if (firstInspection.IsTallyRateSummaryMapping)
            {
                var rateResult = _manualImportService.Import(
                    dialog.FileName,
                    client.Id,
                    ClientGstin,
                    CompanyName,
                    FinancialYear,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    "LOCAL",
                    "SINGLE");
                var updatedRows = _salesRecordRepository.ApplyTallySalesRegisterRateMappings(
                    client.Id, FinancialYear, rateResult.RateMappings, rateResult.SourcePath, rateResult.SourceHash);
                var summaryRows = 0;
                if (updatedRows == 0 && rateResult.Records.Count > 0)
                {
                    summaryRows = _salesRecordRepository.SaveManualImport(
                        client.Id,
                        FinancialYear,
                        rateResult,
                        replaceExisting: false,
                        replaceSourcePath: rateResult.SourcePath);
                }

                LoadSavedRows(client.Id, FinancialYear, rateResult.Issues);
                PublishCommittedImport(client.Id, FinancialYear, "Sales", updatedRows + summaryRows, dialog.FileName, "Tally Sales Register");
                StatusText =
                    $"Tally Sales Register imported: {rateResult.RateMappings.Count:N0} day/range mapping(s), " +
                    $"{updatedRows:N0} existing Tally XML Sales/Credit Note row(s) enriched, {summaryRows:N0} non-exportable statement summary row(s) saved. " +
                    "No individual voucher amounts were fabricated; exact Tally XML vouchers remain authoritative when available.";
                ProgressPercent = 100;
                return;
            }

            var savedMapping = _salesMappingRepository.Get(client.Id, FinancialYear, firstInspection.HeaderSignature);
            if (IsFix93StaleSalesRegisterProfile(firstInspection.HeaderSignature, savedMapping))
            {
                // Fix93 removes only the bad profile remembered by the older
                // Sales Register "Import & Remember Mapping" dialog for this exact
                // SNT header layout. Built-in aliases and Fix90 global mappings are untouched.
                _salesMappingRepository.Delete(client.Id, FinancialYear, firstInspection.HeaderSignature);
                savedMapping = null;
            }
            var options = _salesMappingRepository.GetOptions(client.Id, FinancialYear, firstInspection.HeaderSignature);
            var inspection = _manualImportService.Inspect(dialog.FileName, savedMapping);

            IReadOnlyDictionary<string, string> mapping = inspection.SuggestedMapping;
            var gstTreatment = string.IsNullOrWhiteSpace(options.GstTreatment) ? "LOCAL" : options.GstTreatment;
            var taxColumnMode = string.IsNullOrWhiteSpace(options.TaxColumnMode) ? "SINGLE" : options.TaxColumnMode;

            if (!inspection.IsComplete)
            {
                var mappingWindow = new SalesHeaderMappingWindow(inspection, gstTreatment, taxColumnMode)
                {
                    Owner = System.Windows.Application.Current?.MainWindow
                };
                if (mappingWindow.ShowDialog() != true)
                {
                    StatusText = "Manual Sales import cancelled before any saved Sales data was changed.";
                    return;
                }
                mapping = mappingWindow.Mapping;
                gstTreatment = mappingWindow.GstTreatmentCode;
                taxColumnMode = mappingWindow.TaxColumnModeCode;
                if (_applicationPreferences?.GetBool(ApplicationSettingKeys.SalesSaveConfirmedMappings) != false)
                    _salesMappingRepository.Save(client.Id, FinancialYear, inspection.HeaderSignature, mapping, gstTreatment, taxColumnMode);
            }
            else if (savedMapping is not null)
            {
                // Keep the existing saved mapping. Known formats remain fully automatic.
                mapping = inspection.SuggestedMapping;
            }

            var replaceChoice = System.Windows.MessageBox.Show(
                "Replace existing Manual Sales rows?\n\nYes = Replace Manual Sales rows\nNo = Append\nCancel = Do not import",
                "Manual Sales Import",
                System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Question);
            if (replaceChoice == System.Windows.MessageBoxResult.Cancel)
            {
                StatusText = "Manual Sales import cancelled.";
                return;
            }

            var result = _manualImportService.Import(
                dialog.FileName,
                client.Id,
                ClientGstin,
                CompanyName,
                FinancialYear,
                mapping,
                gstTreatment,
                taxColumnMode);

            var embeddedTallyMasters = 0;
            if (Path.GetExtension(dialog.FileName).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                var parsedMasters = new TallyMasterImportService().Parse(
                    dialog.FileName, client.Id, FinancialYear, CompanyName);
                var accountingMasters = parsedMasters.Records
                    .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ||
                                     master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                embeddedTallyMasters = _masterRepository.Upsert(client.Id, FinancialYear, accountingMasters, markUserEdited: false);
            }

            // Fix126: a successful Manual Sales import owns its missing accounting
            // Ledger Masters too. Create party/Sales/tax ledgers before Stock normalization
            // so Ledger Master and Sales share one committed authority immediately.
            var masterSummary = new SalesAccountingMasterSyncService(_masterRepository).Ensure(
                client.Id, FinancialYear, CompanyName, result.Records);

            var stockSummary = _stockNormalizationService.Normalize(
                client.Id,
                FinancialYear,
                CompanyName,
                result.Records);

            var inserted = _salesRecordRepository.SaveManualImport(
                client.Id,
                FinancialYear,
                result,
                replaceChoice == System.Windows.MessageBoxResult.Yes);

            var committedSalesRows = _salesRecordRepository.GetRecords(client.Id, FinancialYear);
            if (inserted > 0 && committedSalesRows.Count < inserted)
                throw new InvalidOperationException(
                    $"Manual Sales committed {inserted:N0} new row(s), but the durable AAH Sales repository read-back returned only {committedSalesRows.Count:N0} total row(s). " +
                    "AAH did not mark this import as complete; the source workbook is unchanged.");

            // Fix127: file selection is only an intake method. After the repository commit,
            // Manual Sales enters the exact same committed-import bridge as Auto Import.
            // That bridge owns Dashboard output, distributed/LAN propagation, live-change
            // invalidation and priority persisted-cache preparation.
            PublishCommittedImport(client.Id, FinancialYear, "Sales", inserted, dialog.FileName, "Manual Import");
            if (embeddedTallyMasters + masterSummary.CreatedOrUpdated > 0)
                PublishCommittedImport(client.Id, FinancialYear, "Masters", embeddedTallyMasters + masterSummary.CreatedOrUpdated, dialog.FileName, "Manual Import");
            if (stockSummary.InventoryRows > 0)
                PublishCommittedImport(client.Id, FinancialYear, "Stock", stockSummary.InventoryRows, dialog.FileName, "Manual Import");
            _ = RefreshAccountingAfterSalesChangeAsync(client);

            LoadSavedRows(client.Id, FinancialYear, result.Issues);
            RecordManualDashboardActivity(client.Id, result);
            StatusText =
                $"Manual Sales import completed and verified in AAH: {committedSalesRows.Count:N0} saved row(s) ({inserted:N0} inserted this run), " +
                $"{embeddedTallyMasters + masterSummary.CreatedOrUpdated:N0} accounting Master(s), {stockSummary.InventoryRows:N0} inventory rows, " +
                $"{stockSummary.ActionsRequired:N0} stock review item(s).";
            ProgressPercent = 100;
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            StatusText = "Manual Sales import failed.";
        }
        finally
        {
            IsBusy = false;
            NotifyStatusVisibility();
            RefreshCommands();
        }
    }

    private void RemapSalesRegister()
    {
        if (!CanImportSales)
        {
            EditStatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "remap Sales", accountingMutation: true);
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Select Sales Register to Remap",
            Filter = "Excel files (*.xlsx;*.xlsm;*.xls)|*.xlsx;*.xlsm;*.xls|All supported files|*.xlsx;*.xlsm;*.xls",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            IsBusy = true;
            ErrorText = string.Empty;
            StatusText = "Opening the saved Sales mapping profile for remap...";
            var first = _manualImportService.Inspect(dialog.FileName);
            var saved = _salesMappingRepository.Get(client.Id, FinancialYear, first.HeaderSignature);
            var options = _salesMappingRepository.GetOptions(client.Id, FinancialYear, first.HeaderSignature);
            var inspection = _manualImportService.Inspect(dialog.FileName, saved);
            var window = new SalesHeaderMappingWindow(
                inspection,
                string.IsNullOrWhiteSpace(options.GstTreatment) ? "LOCAL" : options.GstTreatment,
                string.IsNullOrWhiteSpace(options.TaxColumnMode) ? "SINGLE" : options.TaxColumnMode)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (window.ShowDialog() != true)
            {
                StatusText = "Sales remap cancelled; the saved mapping was not changed.";
                return;
            }

            // Fix116 R1: Remap applies immediately to the selected source, just like
            // Purchase. Parse first; only then replace this source's previous Manual rows.
            var result = _manualImportService.Import(
                dialog.FileName,
                client.Id,
                ClientGstin,
                CompanyName,
                FinancialYear,
                window.Mapping,
                window.GstTreatmentCode,
                window.TaxColumnModeCode);

            _salesMappingRepository.Save(
                client.Id,
                FinancialYear,
                inspection.HeaderSignature,
                window.Mapping,
                window.GstTreatmentCode,
                window.TaxColumnModeCode);

            var masterSummary = new SalesAccountingMasterSyncService(_masterRepository).Ensure(
                client.Id, FinancialYear, CompanyName, result.Records);
            var stockSummary = _stockNormalizationService.Normalize(
                client.Id,
                FinancialYear,
                CompanyName,
                result.Records);
            var inserted = _salesRecordRepository.SaveManualImport(
                client.Id,
                FinancialYear,
                result,
                replaceExisting: false,
                replaceSourcePath: result.SourcePath);

            var committedSalesRows = _salesRecordRepository.GetRecords(client.Id, FinancialYear);
            if (inserted > 0 && committedSalesRows.Count < inserted)
                throw new InvalidOperationException(
                    $"Sales remap committed {inserted:N0} row(s), but the durable AAH Sales repository read-back returned only {committedSalesRows.Count:N0} total row(s). " +
                    "AAH did not publish an empty prepared-cache success.");

            PublishCommittedImport(client.Id, FinancialYear, "Sales", inserted, dialog.FileName, "Manual Remap");
            if (masterSummary.CreatedOrUpdated > 0)
                PublishCommittedImport(client.Id, FinancialYear, "Masters", masterSummary.CreatedOrUpdated, dialog.FileName, "Manual Remap");
            if (stockSummary.InventoryRows > 0)
                PublishCommittedImport(client.Id, FinancialYear, "Stock", stockSummary.InventoryRows, dialog.FileName, "Manual Remap");
            _ = RefreshAccountingAfterSalesChangeAsync(client);
            LoadSavedRows(client.Id, FinancialYear, result.Issues);
            RecordManualDashboardActivity(client.Id, result);
            StatusText =
                $"Sales remap + source reimport completed and verified for '{Path.GetFileName(dialog.FileName)}': {committedSalesRows.Count:N0} total saved row(s), {inserted:N0} inserted this run, " +
                $"{stockSummary.InventoryRows:N0} inventory row(s), {stockSummary.ActionsRequired:N0} stock review item(s). Other Manual Sales sources were preserved.";
            ProgressPercent = 100;
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            StatusText = "Sales remap failed.";
        }
        finally
        {
            IsBusy = false;
            NotifyStatusVisibility();
            RefreshCommands();
        }
    }

    private const string Fix93SntSalesHeaderSignature =
        "EEB42DA0CA6DD4289D8B64C7CFCD9602ED198A7231BDA1F9CB420A2B9233B108";

    private static bool IsFix93StaleSalesRegisterProfile(
        string headerSignature,
        IReadOnlyDictionary<string, string>? savedMapping)
    {
        if (!headerSignature.Equals(Fix93SntSalesHeaderSignature, StringComparison.OrdinalIgnoreCase) ||
            savedMapping is null)
            return false;

        return savedMapping.Any(pair =>
            SalesManualFieldCatalog.Normalize(pair.Key).Equals("RATE", StringComparison.OrdinalIgnoreCase) &&
            pair.Value.Equals("Rate", StringComparison.OrdinalIgnoreCase));
    }

    private void OpenItemNameMapping()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var clientId = client.Id;
        var fy = FinancialYear;
        var company = CompanyName;
        // Capture the actual persisted revisions, not an old presentation cache.
        // The transaction refuses to overwrite any import/edit made after this read.
        var snapshots = _salesRecordRepository.GetStockProjectionSnapshots(clientId, fy).ToArray();
        if (snapshots.Any(snapshot => snapshot.Record is null))
        {
            EditStatusText = "A saved Sales row is unreadable. Repair the source before changing Stock Item mappings.";
            return;
        }
        var savedRows = snapshots.Select(snapshot => snapshot.Record!).ToArray();
        var revisions = snapshots.ToDictionary(snapshot => snapshot.Record!.PersistentId,
            snapshot => new StockItemMappingSourceRevision(snapshot.OriginalRecordJson,
                snapshot.OriginalUpdatedUtc, snapshot.OriginalBatchId, snapshot.OriginalIsUserEdited));
        var stockItems = _stockRepository.GetStockItems(clientId, fy).ToArray();
        var aliases = _stockRepository.GetItemAliases(clientId, fy).ToArray();
        var window = new SalesStockBulkReviewWindow(
            SalesStockToolsWindow.ItemAliasMode, savedRows, stockItems,
            AahUnitCatalog.CanonicalUnitCodes, aliases)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        window.SetSourceRevisions(revisions);
        window.ApplyItemMappingsAsync = async selections =>
        {
            // The database commit is independent of presentation refresh and page lifetime.
            var result = await Task.Run(() => StockItemMappingCommitService.ApplySales(
                clientId, fy, company, selections));
            var warnings = new List<string>();
            try
            {
                if (_clientContext.CurrentClient?.Id == clientId && FinancialYear == fy)
                {
                    LoadSavedRows(clientId, fy, []);
                    EditStatusText = result.Message;
                }
            }
            catch (Exception exception)
            {
                warnings.Add("Presentation refresh: " + exception.Message);
            }
            try
            {
                _activityContext.NotifyChanged();
                _liveChangeHub.Publish(clientId, fy, AccountingLiveArea.StockMasters, "Sales Stock mapping committed and verified");
                _liveChangeHub.Publish(clientId, fy, AccountingLiveArea.Sales, "Sales Stock mapping committed and verified");
            }
            catch (Exception exception)
            {
                warnings.Add("Background refresh notification: " + exception.Message);
            }
            if (_clientContext.CurrentClient?.Id == clientId && FinancialYear == fy)
                _ = RefreshAccountingAfterSalesChangeAsync(client);
            if (warnings.Count > 0)
                result = result with { Warning = result.Warning + " " + string.Join(" ", warnings) };
            EditStatusText = result.Message;
            return result;
        };
        window.ShowDialog();
    }

    private StockItemRecord EnsureAssignedSalesStockItem(
        long clientId,
        string requestedName,
        string currentStockItemName,
        bool sourceOwnsCurrentMaster,
        IReadOnlyCollection<SalesImportRecord> targets,
        IReadOnlyCollection<StockItemRecord> existingItems)
    {
        var name = requestedName.Trim();
        var existing = existingItems.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        // Fix123: when this imported source is the only source using its current
        // Stock Master, a typed NEW Final Stock Item is a true Stock Master rename:
        // preserve PersistentId/Tally MASTERID and change the Master name in place.
        // If several imported aliases share the current Master, create a separate
        // target instead so renaming one source cannot rename the others.
        var currentMaster = existingItems.FirstOrDefault(item =>
            item.Name.Equals(currentStockItemName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (sourceOwnsCurrentMaster && currentMaster is not null)
        {
            currentMaster.Name = name;
            currentMaster.Source = "Manual Sales Item Rename";
            currentMaster.IsUserEdited = true;
            _stockRepository.SaveStockItem(currentMaster);
            // The saved object already carries the durable PersistentId and the repository
            // normalizes the same instance in-place. Re-reading with First() could crash
            // the whole UI if a concurrent background refresh briefly changes the visible
            // Stock snapshot between Save and readback.
            return currentMaster;
        }

        var units = targets.Select(row =>
            {
                var imported = string.IsNullOrWhiteSpace(row.OriginalUnit) ? row.Unit : row.OriginalUnit;
                return AahUnitCatalog.TryResolvePreferred(imported, out var canonical, out var factor) && factor == 1m
                    ? canonical
                    : string.Empty;
            })
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hsns = targets.Select(row => row.HsnCode.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var importedUnit = units.Length == 1 ? units[0] : string.Empty;
        var saved = _stockRepository.UpsertStockItemAndGet(clientId, FinancialYear,
            new StockItemRecord
            {
                ClientId = clientId,
                FinancialYear = FinancialYear,
                Name = name,
                StockGroup = "Primary",
                BaseUnit = importedUnit,
                DefaultPurchaseUnit = importedUnit,
                DefaultSalesUnit = importedUnit,
                RateUnit = importedUnit,
                HsnSac = hsns.Length == 1 ? hsns[0] : string.Empty,
                GstTypeOfSupply = "Goods",
                Company = CompanyName,
                Source = "Manual Sales Item Name Assignment",
                IsUserEdited = true
            }, markUserEdited: true);
        return saved;
    }

    private static bool HasInventoryEvidenceForItemName(SalesImportRecord row) =>
        row.ItemName.Trim().Length > 0 || row.Unit.Trim().Length > 0 ||
        Math.Abs(row.ActualQuantity) > 0.000001m || Math.Abs(row.BilledQuantity) > 0.000001m ||
        Math.Abs(row.ItemRate) > 0.000001m || Math.Abs(row.ItemAmount) > 0.005m;

    private void OpenUnitMapping()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var stockItems = _stockRepository.GetStockItems(client.Id, FinancialYear).ToArray();
        var units = AahUnitCatalog.CanonicalUnitCodes;
        var window = new SalesStockBulkReviewWindow(SalesStockToolsWindow.UnitMappingMode, AllRows, stockItems, units)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (window.ShowDialog() != true) return;

        var applied = 0;
        var mappingsApplied = 0;
        try
        {
            foreach (var mapping in window.Rows.Where(row => !string.IsNullOrWhiteSpace(row.FinalValue)))
            {
                var finalUnit = units.FirstOrDefault(value => value.Equals(mapping.FinalValue.Trim(), StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(finalUnit))
                    throw new InvalidOperationException($"Unit '{mapping.FinalValue}' is no longer available in the controlled Unit list. Refresh and retry.");
                if (!AahUnitCatalog.TryGetFactor(mapping.SourceValue, finalUnit, out var deterministicFactor) || deterministicFactor != 1m)
                    throw new InvalidOperationException($"{mapping.SourceValue} → {finalUnit} changes quantity. Units only accepts equivalent/canonical names. Use Quantity Conversion to confirm the factor first.");

                // Fix123: Units is a factor-1 canonicalization tool only. It may align
                // imported text such as Qtls to QTL and make that actual imported Unit
                // the common Stock Master Base Unit, but it never performs a quantity
                // conversion. Any factor other than 1 belongs to Quantity Conversion.
                _stockRepository.ChangeStockItemInventoryUnit(
                    client.Id, FinancialYear, mapping.StockItemName, finalUnit, mapping.SourceValue,
                    deterministicFactor, "Manual Sales Unit Mapping");
                _stockNormalizationService.SaveUnitMapping(client.Id, FinancialYear, mapping.StockItemName, mapping.SourceValue, finalUnit);
                mappingsApplied++;

                foreach (var row in AllRows.Where(row => row.ItemName.Equals(mapping.StockItemName, StringComparison.OrdinalIgnoreCase)))
                {
                    var imported = string.IsNullOrWhiteSpace(row.OriginalUnit) ? row.Unit : row.OriginalUnit;
                    if (!imported.Equals(mapping.SourceValue, StringComparison.OrdinalIgnoreCase)) continue;
                    applied++;
                }
            }
        }
        catch (Exception exception)
        {
            EditStatusText = exception.Message;
            return;
        }
        ReapplyStockRulesAndSave(client.Id, $"Unit Mapping Apply All completed: {mappingsApplied:N0} common Stock Unit mapping(s), {applied:N0} Sales/Credit Note row(s) re-evaluated live. Purchase uses the same Stock Item Unit. Any real Unit difference requires Quantity Conversion confirmation.");
    }

    private void OpenQuantityConversion()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var stockItems = _stockRepository.GetStockItems(client.Id, FinancialYear).ToArray();
        var units = AahUnitCatalog.CanonicalUnitCodes;
        var window = new SalesStockBulkReviewWindow(SalesStockToolsWindow.QuantityConversionMode, AllRows, stockItems, units)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (window.Rows.Count == 0)
        {
            EditStatusText = "No Quantity Conversion is required. Every imported Sales/Credit Note Unit already matches its Stock Master Inventory/Transaction Unit.";
            return;
        }
        if (window.ShowDialog() != true) return;

        var affected = 0;
        var conversions = 0;
        try
        {
            foreach (var mapping in window.Rows.Where(row => !string.IsNullOrWhiteSpace(row.FinalValue)))
            {
                var finalUnit = units.FirstOrDefault(value => value.Equals(mapping.FinalValue.Trim(), StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(finalUnit))
                    throw new InvalidOperationException($"Unit '{mapping.FinalValue}' is no longer available in the controlled Unit list. Refresh and retry.");

                // Fix121: Quantity Conversion is an explicit change to the Stock Item's
                // single common Inventory/Transaction Unit. Do NOT store it as a simple
                // Unit Mapping; that would discard the quantity factor on the next live
                // reprojection. The Stock Master is rebased first, then both Purchase and
                // Sales are reprojected by the shared StockMasters live-change pipeline.
                _stockRepository.ChangeStockItemInventoryUnit(
                    client.Id, FinancialYear, mapping.StockItemName, finalUnit, mapping.SourceValue, mapping.Factor,
                    "Manual Sales Quantity Conversion");

                foreach (var row in AllRows.Where(row => row.ItemName.Equals(mapping.StockItemName, StringComparison.OrdinalIgnoreCase)))
                {
                    var imported = string.IsNullOrWhiteSpace(row.OriginalUnit) ? row.Unit : row.OriginalUnit;
                    if (!imported.Equals(mapping.SourceValue, StringComparison.OrdinalIgnoreCase)) continue;
                    _stockNormalizationService.ApplyAndRememberConversion(client.Id, FinancialYear, row, finalUnit, mapping.Factor, true);
                    affected++;
                }
                conversions++;
            }
        }
        catch (Exception exception)
        {
            EditStatusText = exception.Message;
            return;
        }
        _salesRecordRepository.UpdateStockIdentityReferences(client.Id, AllRows);
        LoadSavedRows(client.Id, FinancialYear, []);
        EditStatusText = $"Quantity Conversion Apply All completed: {conversions:N0} common Stock Unit change(s), {affected:N0} Sales/Credit Note row(s) recalculated from original values. Purchase uses the same Stock Item Unit and is reprojected live.";
        _activityContext.NotifyChanged();
        _liveChangeHub.Publish(client.Id, FinancialYear, AccountingLiveArea.StockMasters, "Sales quantity conversion changed Stock rules");
        _ = RefreshAccountingAfterSalesChangeAsync(client);
    }

    private void ReapplyStockRulesAndSave(long clientId, string successMessage)
    {
        _stockNormalizationService.Normalize(clientId, FinancialYear, CompanyName, AllRows);
        _salesRecordRepository.UpdateRecords(clientId, AllRows);
        LoadSavedRows(clientId, FinancialYear, []);
        EditStatusText = successMessage;
        _activityContext.NotifyChanged();
        _liveChangeHub.Publish(clientId, FinancialYear, AccountingLiveArea.StockMasters, "Sales Stock mapping Apply All");
        var client = _clientContext.CurrentClient;
        if (client is not null && client.Id == clientId)
            _ = RefreshAccountingAfterSalesChangeAsync(client);
    }

    private void PublishCommittedImport(
        long clientId,
        string financialYear,
        string module,
        int recordCount,
        string sourcePath,
        string importMethod)
    {
        if (_automationService is not null)
        {
            _automationService.PublishCommittedAccountingImport(
                clientId, financialYear, module, recordCount, sourcePath, importMethod);
            return;
        }

        // Compatibility fallback for isolated/design-time construction. Normal Desktop
        // runtime always supplies BackgroundAutomationService so all import methods share
        // the common committed-import pipeline.
        var area = module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Sales
            : module.Equals("Masters", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Masters
            : module.Equals("Stock", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.StockMasters
            : (AccountingLiveArea?)null;
        if (area.HasValue)
            _liveChangeHub.Publish(clientId, financialYear, area.Value, $"{importMethod} committed {module}");
    }

    private void RecordManualDashboardActivity(long clientId, SalesManualImportResult result)
    {
        var dates = result.Records.Where(row => row.DocumentDate.HasValue).Select(row => row.DocumentDate!.Value.Date).ToArray();
        var from = dates.Length == 0 ? FinancialYearStartDate(FinancialYear) : dates.Min();
        var to = dates.Length == 0 ? FinancialYearEndDate(FinancialYear) : dates.Max();
        _activityRepository.Record(new ImportActivity
        {
            ClientId = clientId,
            ImportType = "Sales",
            PeriodFrom = from,
            PeriodTo = to,
            PeriodLabel = BuildPeriodLabel(from, to, FinancialYear),
            FilingStatus = "Manual Sales Register",
            RecordCount = result.Records.Count,
            BankCount = 0,
            SourceSummary = Path.GetFileName(result.SourcePath),
            Status = "Manual Imported",
            Details = $"{result.SalesRows:N0} Sales, {result.CreditNoteRows:N0} Credit Notes; header signature {result.HeaderSignature}.",
            ImportedUtc = DateTime.UtcNow
        });
        _activityContext.NotifyChanged();
    }

    private void BrowseEReturnsRoot()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the E-returns root folder",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _eReturnsRootContext.SetRoot(
            dialog.FolderName);
    }

    private async Task RunAutomaticImportAsync(
        long generation,
        bool forceReimport = false,
        DateTime? reimportMonth = null)
    {
        if (!CanImportSales)
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "import Sales", accountingMutation: true);
            return;
        }

        while (IsBusy &&
               generation == Interlocked.Read(
                   ref _automaticImportGeneration))
        {
            await Task.Delay(100);
        }

        if (generation != Interlocked.Read(
                ref _automaticImportGeneration))
        {
            return;
        }

        var client = _clientContext.CurrentClient;

        if (client is null)
        {
            StatusText =
                "Select an active client. Automatic Sales import is waiting.";
            return;
        }

        if (ClientGstin.Length != 15)
        {
            StatusText =
                "Automatic Sales import is waiting for a valid client GSTIN.";
            return;
        }

        if (string.IsNullOrWhiteSpace(
                FinancialYear))
        {
            StatusText =
                "Automatic Sales import is waiting for the Financial Year.";
            return;
        }

        var automationFolderPaths = GetLinkedAutomationFolderPaths(client.Id, client.FinancialYear);
        var hasEReturnsRoot = !string.IsNullOrWhiteSpace(EReturnsRootPath) && Directory.Exists(EReturnsRootPath);
        if (!hasEReturnsRoot && automationFolderPaths.Count == 0)
        {
            IsImportPanelExpanded = true;
            StatusText =
                "Neither the E-returns root nor a linked Automation Folder is available for automatic Sales import.";
            WarningText = EReturnsRootPath;
            NotifyStatusVisibility();
            return;
        }

        if (client.AdditionalGstins is { Count: > 0 })
        {
            await ImportAllRegisteredSalesAsync(client, generation, forceReimport,
                reimportMonth, automationFolderPaths);
            return;
        }

        IsBusy = true;
        ErrorText = string.Empty;
        WarningText = string.Empty;
        EditStatusText = string.Empty;
        SourceVerificationText = string.Empty;
        ProgressPercent = 0;
        StatusText = reimportMonth.HasValue
            ? $"Checking GSTR-1 Sales sources for {reimportMonth.Value:MMMM yyyy} reimport..."
            : "Checking GSTR-1 Sales sources automatically...";
        NotifyStatusVisibility();

        using var cancellationSource =
            new CancellationTokenSource();

        _cancellationSource = cancellationSource;

        try
        {
            var progress =
                new Progress<SalesImportProgress>(
                    update =>
                    {
                        ProgressPercent = update.Percent;
                        StatusText = update.Stage;
                    });

            var result =
                await _autoImportService.ImportAsync(
                    new SalesAutoImportRequest
                    {
                        ClientId = client.Id,
                        ClientGstin = ClientGstin,
                        FinancialYear = FinancialYear,
                        CompanyName = CompanyName,
                        EReturnsRootPath = EReturnsRootPath,
                        AutomationFolderPaths = automationFolderPaths,
                        SearchWindowsDownloads = true,
                        SaveSourceFiles = false
                    },
                    progress,
                    cancellationSource.Token);

            if (generation != Interlocked.Read(
                    ref _automaticImportGeneration))
            {
                return;
            }

            result.SourceFingerprint =
                BuildSourceFingerprint(
                    result);

            CompanyName = result.CompanyName;
            ClientGstin = result.ClientGstin;
            FinancialYear = result.FinancialYear;
            Summary = result.Summary;
            WarningText = string.Join(
                Environment.NewLine,
                result.Warnings);
            SourceVerificationText =
                BuildSourceVerificationText(
                    result.Verification);

            var hasSalesSource =
                result.SourceManifest.Any(source =>
                    string.Equals(
                        source.FileType,
                        "GSTR1",
                        StringComparison.OrdinalIgnoreCase));

            if (!hasSalesSource)
            {
                LoadSavedRows(
                    client.Id,
                    result.FinancialYear,
                    result.Issues);

                StatusText =
                    "No valid GSTR-1 source was found. Existing saved Sales records were retained.";
                return;
            }

            if (reimportMonth.HasValue &&
                !HasSalesSourceCoverageForMonth(result.Verification, reimportMonth.Value))
            {
                LoadSavedRows(
                    client.Id,
                    result.FinancialYear,
                    result.Issues);

                StatusText =
                    $"{reimportMonth.Value:MMMM yyyy} was not reimported because no verified GSTR-1 source covers that month. Existing saved Sales rows were retained.";
                return;
            }

            if (!forceReimport &&
                _salesRecordRepository.IsAutomaticImportCurrent(
                    client.Id,
                    result.FinancialYear,
                    result.SourceFingerprint))
            {
                LoadSavedRows(
                    client.Id,
                    result.FinancialYear,
                    result.Issues);

                ProgressPercent = 100;
                StatusText =
                    $"Sales is up to date: {AllRows.Count:N0} saved rows.";
                return;
            }

            var stockSummary = _stockNormalizationService.Normalize(
                client.Id,
                result.FinancialYear,
                CompanyName,
                result.Records);

            result.PersistedRows = reimportMonth.HasValue
                ? _salesRecordRepository.ReplaceAutomaticImportMonth(
                    client.Id,
                    result.FinancialYear,
                    result,
                    reimportMonth.Value)
                : _salesRecordRepository.ReplaceAutomaticImport(
                    client.Id,
                    result.FinancialYear,
                    result);

            var automaticSourcePath = result.SourceManifest
                .Select(source => source.SourcePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
                ?? result.Verification.EReturnsRoot;
            if (stockSummary.InventoryRows > 0)
                PublishCommittedImport(client.Id, result.FinancialYear, "Stock", stockSummary.InventoryRows, automaticSourcePath, "Automatic GST Import");

            await _refreshCoordinator.RefreshAsync(client, cancellationSource.Token);
            PublishCommittedImport(
                client.Id,
                result.FinancialYear,
                "Sales",
                result.PersistedRows,
                automaticSourcePath,
                reimportMonth.HasValue ? "Automatic GST Reimport" : "Automatic GST Import");

            if (reimportMonth.HasValue)
            {
                // Fix13: the selected Sales month has committed and the refresh coordinator
                // has rebuilt the dependent Journal/GSTR-3B books. The common committed-import
                // bridge above invalidates/prepares the Sales stream; reload the merged saved FY
                // so the other eleven months retain their IDs and edits.

                LoadSavedRows(
                    client.Id,
                    result.FinancialYear,
                    result.Issues);
            }
            else
            {
                ReplaceResultRows(
                    result.Records,
                    result.Issues);

                RecordDashboardActivity(
                    result);
            }

            ProgressPercent = 100;
            StatusText = reimportMonth.HasValue
                ? $"Sales reimport completed for {reimportMonth.Value:MMMM yyyy}: {result.PersistedRows:N0} automatic row(s) rebuilt; Journal and GSTR-3B books auto-rebuilt and refreshed; all other Sales months were retained unchanged."
                : $"Automatic Sales import completed: " +
                  $"{result.PersistedRows:N0} saved rows, " +
                  $"{result.Summary.SalesRows:N0} Sales, " +
                  $"{result.Summary.CreditNoteRows:N0} Credit Notes.";
        }
        catch (OperationCanceledException)
        {
            StatusText =
                "Automatic Sales import cancelled.";
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            StatusText =
                "Automatic Sales import failed.";
        }
        finally
        {
            IsBusy = false;
            _cancellationSource = null;
            NotifyStatusVisibility();
        }
    }

    // The existing Sales module and source parser run once per client registration.
    // Persist each result under its GSTIN, then reload the shared client/FY view.
    private async Task ImportAllRegisteredSalesAsync(
        AccountingAutomationHub.Domain.Clients.ClientProfile client,
        long generation,
        bool forceReimport,
        DateTime? reimportMonth,
        IReadOnlyList<string> automationFolderPaths)
    {
        IsBusy = true;
        ErrorText = string.Empty;
        WarningText = string.Empty;
        SourceVerificationText = string.Empty;
        using var cancellation = new CancellationTokenSource();
        _cancellationSource = cancellation;
        var imported = 0;
        var skipped = new List<string>();
        try
        {
            var registrations = new[] { client.Gstin }.Concat(client.AdditionalGstins ?? [])
                .Select(value => value.Trim().ToUpperInvariant())
                .Where(value => value.Length == 15)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var gstin in registrations)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (generation != Interlocked.Read(ref _automaticImportGeneration)) return;
                StatusText = $"Sales: checking {gstin} ({imported + skipped.Count + 1}/{registrations.Length})...";
                var result = await _autoImportService.ImportAsync(new SalesAutoImportRequest
                {
                    ClientId = client.Id,
                    ClientGstin = gstin,
                    FinancialYear = client.FinancialYear,
                    CompanyName = CompanyName,
                    EReturnsRootPath = EReturnsRootPath,
                    AutomationFolderPaths = automationFolderPaths,
                    SearchWindowsDownloads = true,
                    SaveSourceFiles = false
                }, cancellationToken: cancellation.Token);
                if (generation != Interlocked.Read(ref _automaticImportGeneration)) return;
                if (!result.SourceManifest.Any(source => source.FileType.Equals("GSTR1", StringComparison.OrdinalIgnoreCase)) ||
                    (reimportMonth.HasValue && !HasSalesSourceCoverageForMonth(result.Verification, reimportMonth.Value)))
                {
                    skipped.Add(gstin + " (no matching source)");
                    continue;
                }
                result.SourceFingerprint = BuildSourceFingerprint(result);
                if (!forceReimport && _salesRecordRepository.IsAutomaticImportCurrent(
                    client.Id, client.FinancialYear, result.SourceFingerprint, gstin))
                    continue;
                _stockNormalizationService.Normalize(client.Id, client.FinancialYear,
                    CompanyName, result.Records);
                result.PersistedRows = reimportMonth.HasValue
                    ? _salesRecordRepository.ReplaceAutomaticImportMonth(
                        client.Id, client.FinancialYear, result, reimportMonth.Value)
                    : _salesRecordRepository.ReplaceAutomaticImport(
                        client.Id, client.FinancialYear, result);
                imported += result.PersistedRows;
            }
            await _refreshCoordinator.RefreshAsync(client, cancellation.Token);
            LoadSavedRows(client.Id, client.FinancialYear, []);
            PublishCommittedImport(client.Id, client.FinancialYear, "Sales",
                imported, EReturnsRootPath, "Registered GSTIN Auto Import");
            ProgressPercent = 100;
            WarningText = string.Join("; ", skipped);
            StatusText = $"Sales registration import complete: {AllRows.Count:N0} combined saved row(s) across " +
                $"{registrations.Length} GSTIN(s). " +
                (skipped.Count > 0 ? $"Source unavailable for: {string.Join(", ", skipped)}." : "");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Sales registration import cancelled.";
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            StatusText = "Sales registration import failed; earlier registration rows retained.";
        }
        finally
        {
            IsBusy = false;
            _cancellationSource = null;
            NotifyStatusVisibility();
        }
    }

    private async Task ReimportAutomaticSourcesAsync()
    {
        if (!CanImportSales)
        {
            EditStatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "reimport Sales", accountingMutation: true);
            return;
        }
        _cancellationSource?.Cancel();

        var generation = Interlocked.Increment(
            ref _automaticImportGeneration);

        StatusText = "Reimporting Sales from verified GSTR-1 sources...";

        await RunAutomaticImportAsync(
            generation,
            forceReimport: true);
    }

    public async Task ReimportAutomaticSourcesForMonthAsync(int month)
    {
        if (!CanReimportSales)
        {
            EditStatusText = CanImportSales
                ? "Select an active client and wait for the current Sales operation to finish."
                : DesktopPermissionService.DeniedMessage(_governanceRepository, "reimport Sales", accountingMutation: true);
            return;
        }

        var financialYearStart = FinancialYearStartDate(FinancialYear);
        if (month is < 1 or > 12 || !financialYearStart.HasValue)
        {
            EditStatusText = "Select a valid Financial Year before choosing a Sales reimport month.";
            return;
        }

        var fyStart = financialYearStart.Value;
        var year = month >= 4
            ? fyStart.Year
            : fyStart.Year + 1;
        var selectedMonth = new DateTime(year, month, 1);

        _cancellationSource?.Cancel();

        var generation = Interlocked.Increment(
            ref _automaticImportGeneration);

        StatusText = $"Reimporting Sales for {selectedMonth:MMMM yyyy} from verified GSTR-1 sources...";

        await RunAutomaticImportAsync(
            generation,
            forceReimport: true,
            reimportMonth: selectedMonth);
    }

    private static bool HasSalesSourceCoverageForMonth(
        SalesSourceVerification verification,
        DateTime month)
    {
        var monthKey = month.ToString("yyyyMM", CultureInfo.InvariantCulture);
        return verification.SelectedPeriods.Any(period =>
            string.Equals(
                NormalizeSalesPeriod(period),
                monthKey,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSalesPeriod(string? value)
    {
        var digits = string.Concat((value ?? string.Empty).Where(char.IsDigit));
        if (digits.Length != 6)
            return string.Empty;

        if (int.TryParse(digits[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var yyyy) &&
            int.TryParse(digits[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var mm) &&
            yyyy is >= 2000 and <= 2200 && mm is >= 1 and <= 12)
        {
            return $"{yyyy:0000}{mm:00}";
        }

        if (int.TryParse(digits[..2], NumberStyles.None, CultureInfo.InvariantCulture, out mm) &&
            int.TryParse(digits[2..], NumberStyles.None, CultureInfo.InvariantCulture, out yyyy) &&
            yyyy is >= 2000 and <= 2200 && mm is >= 1 and <= 12)
        {
            return $"{yyyy:0000}{mm:00}";
        }

        return string.Empty;
    }

    private void Cancel() =>
        _cancellationSource?.Cancel();

    public void QueueAutomaticSave(SalesImportRecord? row)
    {
        if (!CanEditSales) return;
        if (row is null || row.PersistentId <= 0)
            return;

        _pendingLiveEditIds.Add(row.PersistentId);

        // Fix122: GSTIN/Destination edits immediately repaint the B2B/B2C KPI tiles.
        // Persistence still uses the existing debounced save pipeline.
        Summary = BuildSummaryFromRecords(AllRows.ToArray());

        EditStatusText = "Saving changes automatically…";
        RestartAutomaticSaveTimer();
    }

    private void SaveChanges()
    {
        if (!CanEditSales)
        {
            EditStatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "edit Sales", accountingMutation: true);
            return;
        }
        foreach (var row in AllRows.Where(row => row.PersistentId > 0))
            _pendingLiveEditIds.Add(row.PersistentId);

        if (_pendingLiveEditIds.Count == 0)
        {
            EditStatusText = "No saved Sales rows were available to update.";
            return;
        }

        EditStatusText = "Saving changes automatically…";
        RestartAutomaticSaveTimer(immediate: true);
    }

    private void RestartAutomaticSaveTimer(bool immediate = false)
    {
        _liveEditSaveTimer.Stop();
        _liveEditSaveTimer.Interval = immediate
            ? TimeSpan.FromMilliseconds(1)
            : TimeSpan.FromMilliseconds(350);
        _liveEditSaveTimer.Start();
    }

    private void ResetAutomaticSaveQueue()
    {
        _liveEditSaveTimer.Stop();
        _pendingLiveEditIds.Clear();
    }

    private async void OnLiveEditSaveTimerTick(object? sender, EventArgs e)
    {
        _liveEditSaveTimer.Stop();
        if (!CanEditSales)
        {
            _pendingLiveEditIds.Clear();
            return;
        }
        _liveEditSaveTimer.Interval = TimeSpan.FromMilliseconds(350);

        if (_liveEditSaveRunning || IsBusy)
        {
            if (_pendingLiveEditIds.Count > 0)
                RestartAutomaticSaveTimer();
            return;
        }

        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            _pendingLiveEditIds.Clear();
            EditStatusText = "Select an active client before editing Sales rows.";
            return;
        }

        var ids = _pendingLiveEditIds.ToHashSet();
        _pendingLiveEditIds.ExceptWith(ids);
        var rows = AllRows
            .Where(row => row.PersistentId > 0 && ids.Contains(row.PersistentId))
            .ToArray();

        if (rows.Length == 0)
            return;

        _liveEditSaveRunning = true;
        try
        {
            NormalizeRowsForSave(client.Id, client.FinancialYear, rows);
            var updated = _salesRecordRepository.UpdateRecords(client.Id, rows);

            if (updated > 0)
            {
                // Rebuild the registered-customer projection after a saved GSTIN edit so
                // the B2B tile completion text and customer window stay in sync as well.
                var currentRows = AllRows.ToArray();
                Summary = BuildSummaryFromRecords(currentRows);
                RebuildB2BCustomerPanel(currentRows);
                RebuildB2CCustomerPanel(currentRows);

                await _refreshCoordinator.RefreshAsync(client);
                EditStatusText =
                    $"Auto-saved {updated:N0} Sales/Credit Note row(s); Journal and GST refreshed.";
            }
            else
            {
                EditStatusText = "No saved Sales rows were available to update.";
            }
        }
        catch (Exception exception)
        {
            EditStatusText =
                "Automatic Sales save/refresh needs attention: " +
                exception.Message;
        }
        finally
        {
            _liveEditSaveRunning = false;
            OnPropertyChanged(nameof(HasPersistedRows));
            SaveChangesCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();

            if (_pendingLiveEditIds.Count > 0)
                RestartAutomaticSaveTimer();
        }
    }

    private void ClearSalesModule()
    {
        var client = _clientContext.CurrentClient;
        if (client is null || !CanClearSales) return;

        var answer = System.Windows.MessageBox.Show(
            $"Clear all Sales module transactions for {client.ClientName} / FY {client.FinancialYear}?\n\n" +
            "This removes Sales/Credit Note records and their import batches from AAH only. " +
            "Stock Masters, Unit/Item mappings, built-in/global mappings, Automation Folder links and Tally vouchers are preserved.",
            "Clear Sales Module",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        _liveEditSaveTimer.Stop();
        _pendingLiveEditIds.Clear();
        var removed = _salesRecordRepository.ClearModule(client.Id, client.FinancialYear);
        ReplaceResultRows([], []);
        Summary = new SalesImportSummary();
        EditStatusText = $"Cleared {removed:N0} Sales module row(s). Masters, mappings and Tally vouchers were preserved.";
        StatusText = "Sales module cleared for the selected Client + FY.";
        _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Sales, "Sales module cleared");
        _activityContext.NotifyChanged();
        _ = _refreshCoordinator.RefreshAsync(client);
        OnPropertyChanged(nameof(HasPersistedRows));
        RefreshCommands();
    }

    private void NormalizeRowsForSave(
        long clientId,
        string financialYear,
        IEnumerable<SalesImportRecord> rows)
    {
        var ledgerMasters = BuildLedgerMasterLookup(clientId, financialYear);
        var stockItems = _stockRepository.GetStockItems(clientId, financialYear)
            .Where(item => !item.IsArchived && !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => AccountingText.DuplicateKey(item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var units = _stockRepository.GetUnits(clientId, financialYear)
            .Where(unit => !unit.IsArchived && !string.IsNullOrWhiteSpace(unit.Name))
            .GroupBy(unit => AccountingText.DuplicateKey(unit.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var godowns = _stockRepository.GetSimpleMasters(clientId, financialYear, "Godown")
            .Where(master => !master.IsArchived && !string.IsNullOrWhiteSpace(master.Name))
            .GroupBy(master => AccountingText.DuplicateKey(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (GstRateCatalog.TryNormalize(row.Rate, out var normalizedGstRate))
            {
                row.Rate = normalizedGstRate;
                if (GstRateCatalog.TryDeriveFromTaxAmounts(row.TaxableValue, row.Igst, row.Cgst, row.Sgst, out var provedRate) &&
                    Math.Abs(provedRate - normalizedGstRate) <= 0.005m)
                    row.IsEffectiveMixedGstRate = false;
            }
            else if (IsTallyXmlEffectiveMixedRate(row, out var effectiveRate))
            {
                // The numeric Rate is display/reconciliation evidence only for a
                // mixed Tally voucher. Exact GST amounts/ledgers remain authority.
                row.Rate = effectiveRate;
                row.IsEffectiveMixedGstRate = true;
                if (string.IsNullOrWhiteSpace(row.GstRateEvidence))
                    row.GstRateEvidence = "Weighted GST rate derived from exact Tally XML tax ledgers.";
            }
            else
            {
                throw new InvalidOperationException(
                    $"GST Rate {row.Rate:0.####}% is not allowed. Select one of: {string.Join(", ", GstRateCatalog.AllowedRates.Select(rate => rate.ToString("0.##", CultureInfo.InvariantCulture)))}%, or retain a verified mixed Tally XML effective rate.");
            }

            var itemRateQuantity = row.ActualQuantity != 0m ? row.ActualQuantity : row.BilledQuantity;
            if (itemRateQuantity != 0m && row.TaxableValue != 0m)
                row.ItemRate = Math.Round(row.TaxableValue / itemRateQuantity, 6, MidpointRounding.AwayFromZero);

            row.CustomerName = ResolveLedgerEntry(row.CustomerName, ledgerMasters, "Customer");
            row.SalesLedger = ResolveLedgerEntry(row.SalesLedger, ledgerMasters, "Sales Ledger");
            row.ItemSalesLedger = ResolveLedgerEntry(row.ItemSalesLedger, ledgerMasters, "Item Sales Ledger");
            row.IgstLedger = ResolveLedgerEntry(row.IgstLedger, ledgerMasters, "IGST Ledger");
            row.CgstLedger = ResolveLedgerEntry(row.CgstLedger, ledgerMasters, "CGST Ledger");
            row.SgstLedger = ResolveLedgerEntry(row.SgstLedger, ledgerMasters, "SGST Ledger");
            row.PlaceOfSupply = AccountingText.DisplayName(row.PlaceOfSupply);
            row.ItemName = ResolveStockEntry(row.ItemName, stockItems, "Stock Item", item => item.Name);
            row.Unit = ResolveStockEntry(row.Unit, units, "Unit", unit => unit.Name, useUnitCase: true);
            row.AlternateUnit = ResolveStockEntry(row.AlternateUnit, units, "Alternate Unit", unit => unit.Name, useUnitCase: true);
            row.Godown = ResolveStockEntry(row.Godown, godowns, "Godown", godown => godown.Name);
            row.PartyMailingName = AccountingText.DisplayName(row.PartyMailingName);
            row.BuyerName = AccountingText.DisplayName(row.BuyerName);
            row.ConsigneeName = AccountingText.DisplayName(row.ConsigneeName);
        }
    }

    private static bool IsTallyXmlEffectiveMixedRate(SalesImportRecord row, out decimal rate)
    {
        rate = 0m;
        if (!row.SourceOrigin.Equals("Tally Voucher XML", StringComparison.OrdinalIgnoreCase)) return false;
        var taxable = Math.Abs(row.TaxableValue);
        var tax = Math.Abs(row.Igst) + Math.Abs(row.Cgst) + Math.Abs(row.Sgst);
        if (taxable <= 0.005m || tax <= 0.005m) return false;
        if (GstRateCatalog.TryDeriveFromTaxAmounts(row.TaxableValue, row.Igst, row.Cgst, row.Sgst, out _)) return false;
        rate = decimal.Round(tax * 100m / taxable, 4, MidpointRounding.AwayFromZero);
        return rate > 0m && Math.Abs(row.Rate - rate) <= 0.02m;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<AccountingAutomationHub.Domain.Masters.MasterRecord>> BuildLedgerMasterLookup(
        long clientId,
        string financialYear) =>
        _masterRepository.Get(clientId, financialYear, includeArchived: false)
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(master.Name))
            .GroupBy(master => TallyMasterIdentity.Key(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<AccountingAutomationHub.Domain.Masters.MasterRecord>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

    private static string ResolveLedgerEntry(
        string value,
        IReadOnlyDictionary<string, IReadOnlyList<AccountingAutomationHub.Domain.Masters.MasterRecord>> masterLookup,
        string fieldName)
    {
        var clean = AccountingText.CleanWhitespace(value);
        if (clean.Length == 0) return string.Empty;
        var key = TallyMasterIdentity.Key(clean);
        if (!masterLookup.TryGetValue(key, out var matches)) return AccountingText.DisplayName(clean);
        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"Duplicate Ledger Master detected for '{clean}' while saving {fieldName}. " +
                "Resolve the duplicate in Ledger Master before saving this entry.");
        return matches[0].Name.Trim();
    }

    private static string ResolveStockEntry<T>(
        string value,
        IReadOnlyDictionary<string, T[]> lookup,
        string fieldName,
        Func<T, string> nameSelector,
        bool useUnitCase = false)
    {
        var clean = AccountingText.CleanWhitespace(value);
        if (clean.Length == 0) return string.Empty;
        var key = AccountingText.DuplicateKey(clean);
        if (!lookup.TryGetValue(key, out var matches))
            return useUnitCase ? AccountingText.UnitName(clean) : AccountingText.DisplayName(clean);
        if (matches.Length > 1)
            throw new InvalidOperationException(
                $"Duplicate {fieldName} Master detected for '{clean}'. " +
                "Resolve the duplicate in Stock Master before saving this Sales entry.");
        return nameSelector(matches[0]).Trim();
    }

    private async Task RefreshAccountingAfterSalesChangeAsync(AccountingAutomationHub.Domain.Clients.ClientProfile client)
    {
        try
        {
            await _refreshCoordinator.RefreshAsync(client);
        }
        catch (Exception exception)
        {
            EditStatusText = $"Sales saved, but live accounting refresh needs attention: {exception.Message}";
        }
    }

    private bool CanSaveChanges() =>
        CanEditSales &&
        !IsBusy &&
        HasPersistedRows;

    private void ApplyClientContext(bool loadRows = true)
    {
        ResetAutomaticSaveQueue();

        var client = _clientContext.CurrentClient;

        EReturnsRootPath =
            _eReturnsRootContext.RootPath;

        IsImportPanelExpanded =
            string.IsNullOrWhiteSpace(
                EReturnsRootPath);

        if (client is null)
        {
            CompanyName = string.Empty;
            ClientGstin = string.Empty;
            FinancialYear = string.Empty;
            _preparedClientId = 0;
            _preparedFinancialYear = string.Empty;
            _presentedSalesFingerprint = string.Empty;
            Summary = new SalesImportSummary();
            ReplaceResultRows([], []);
            RefreshCommands();
            return;
        }

        if (_preparedClientId != client.Id || !_preparedFinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            _presentedSalesFingerprint = string.Empty;

        CompanyName =
            string.IsNullOrWhiteSpace(
                client.LegalName)
                ? client.ClientName
                : client.LegalName;

        ClientGstin = client.Gstin;
        FinancialYear = client.FinancialYear;
        EditStatusText = string.Empty;

        if (loadRows)
        {
            var savedRows =
                _salesRecordRepository.GetRecords(
                    client.Id,
                    FinancialYear);

            ReplaceResultRows(savedRows, []);
            Summary = BuildSummaryFromRecords(savedRows);
            _preparedClientId = client.Id;
            _preparedFinancialYear = FinancialYear;
        }
        else
        {
            ReplaceResultRows([], []);
            Summary = new SalesImportSummary();
            _preparedClientId = 0;
            _preparedFinancialYear = string.Empty;
            StatusText = "Sales is preparing in the background.";
        }

        RefreshCommands();
    }

    public void PrepareForDisplay()
    {
        // Fix132: restore the proven Fix118 passive page-open contract. Opening Sales
        // only consumes the already prepared Client/FY snapshot; it must never start
        // Stock reprojection, legacy Master repair, workbook/GSTR-1 parsing or any cache
        // invalidation. Automatic GSTR-1 and Manual Sales Register imports remain on
        // their existing independent import/commit paths.
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        if (_preparedClientId != client.Id ||
            !_preparedFinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            StatusText = "Sales stream is preparing in the background…";
    }

    public void WarmFromPreparedSnapshot(ClientWorkspaceSnapshot snapshot)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != snapshot.ClientId ||
            !client.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase))
            return;

        // R22: a prepared-cache generation may be rebuilt several times by Journal/GST/
        // Actions work even when the saved Sales rows are byte-for-byte the same. Repainting
        // the Sales grid for an identical presentation caused the visible refresh loop and
        // could replace a B2B editor row while the user was typing.
        var fingerprint = BuildSalesPresentationFingerprint(snapshot.Sales);
        if (_b2bRenameSessionActive)
        {
            _deferredSalesSnapshot = snapshot;
            _b2bRenameRefreshPending = true;
            return;
        }
        if (_preparedClientId == client.Id &&
            _preparedFinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase) &&
            _presentedSalesFingerprint.Equals(fingerprint, StringComparison.Ordinal))
        {
            StatusText = $"Sales ready • {snapshot.Sales.Count:N0} saved row(s).";
            return;
        }

        CompanyName = string.IsNullOrWhiteSpace(client.LegalName) ? client.ClientName : client.LegalName;
        ClientGstin = client.Gstin;
        FinancialYear = client.FinancialYear;
        ReplaceResultRows(snapshot.Sales, []);
        Summary = BuildSummaryFromRecords(snapshot.Sales);
        _presentedSalesFingerprint = fingerprint;
        _preparedClientId = client.Id;
        _preparedFinancialYear = client.FinancialYear;
        StatusText = $"Sales ready • {snapshot.Sales.Count:N0} saved row(s).";
        RefreshCommands();
    }

    private static string BuildSalesPresentationFingerprint(IReadOnlyCollection<SalesImportRecord> records)
    {
        var hash = new HashCode();
        hash.Add(records.Count);
        foreach (var row in records.OrderBy(x => x.PersistentId).ThenBy(x => x.ItemSequence))
        {
            hash.Add(row.PersistentId);
            hash.Add(row.ItemSequence);
            hash.Add(row.Destination, StringComparer.OrdinalIgnoreCase);
            hash.Add(row.CustomerGstin, StringComparer.OrdinalIgnoreCase);
            hash.Add(row.CustomerName, StringComparer.Ordinal);
            hash.Add(row.CustomerNameSource, StringComparer.Ordinal);
            hash.Add(row.DocumentNumber, StringComparer.OrdinalIgnoreCase);
            hash.Add(row.DocumentDate);
            hash.Add(row.DocumentValue);
            hash.Add(row.TaxableValue);
            hash.Add(row.Igst);
            hash.Add(row.Cgst);
            hash.Add(row.Sgst);
            hash.Add(row.Cess);
            hash.Add(row.RoundOff);
            hash.Add(row.SalesLedger, StringComparer.OrdinalIgnoreCase);
        }
        return hash.ToHashCode().ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }

    public void RefreshSavedRowsAfterExternalImport()
    {
        // Completion 61: MainWindow owns module-scoped cache invalidation after an
        // external import. Do not re-enter SQLite synchronously from the Sales page.
        StatusText = "Sales stream is synchronizing in the background…";
    }

    public void NotifyPreparedCacheFailure(string error, int attempt)
    {
        var detail = string.IsNullOrWhiteSpace(error) ? "unknown cache/repository error" : error.Trim();
        StatusText = attempt <= 3
            ? $"Sales saved data is still available in AAH. Prepared-cache read failed (retry {attempt}/3): {detail}"
            : $"Sales prepared-cache read failed after retries: {detail}. AAH did not treat this as an empty Sales module.";
    }

    private void OnClientContextPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(
                ActiveClientContext.CurrentClient))
        {
            return;
        }

        ApplyClientContext(loadRows: false);
        // Fix38: changing/opening a client never starts a per-client GSTIN/file scan.
        // The global Sales/Purchase watcher classifies sources once and routes them.
    }

    private void OnEReturnsRootPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(
                EReturnsRootContext.RootPath))
        {
            return;
        }

        EReturnsRootPath =
            _eReturnsRootContext.RootPath;

        IsImportPanelExpanded =
            string.IsNullOrWhiteSpace(
                EReturnsRootPath);

        // Fix38: root changes are picked up by the central background watcher.
        // Explicit Refresh/Reimport commands may still call ScheduleAutomaticImport.
    }

    private void ScheduleAutomaticImport()
    {
        if (!CanImportSales) return;
        _cancellationSource?.Cancel();

        var generation =
            Interlocked.Increment(
                ref _automaticImportGeneration);

        System.Windows.Application.Current?
            .Dispatcher
            .InvokeAsync(
                () =>
                {
                    _ = RunAutomaticImportAsync(
                        generation);
                },
                DispatcherPriority.ApplicationIdle);
    }

    private IReadOnlyList<string> GetLinkedAutomationFolderPaths(long clientId, string financialYear)
    {
        try
        {
            return _automationRepository.GetFolders()
                .Where(folder => !folder.FolderId.StartsWith("SYSTEM-DUMP-", StringComparison.OrdinalIgnoreCase))
                .Where(folder => folder.ClientId == clientId &&
                                 folder.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase) &&
                                 !folder.IsPaused && Directory.Exists(folder.FolderPath))
                .Select(folder => folder.FolderPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void LoadSavedRows(
        long clientId,
        string financialYear,
        IEnumerable<SalesImportIssue> issues)
    {
        var savedRows =
            _salesRecordRepository.GetRecords(
                clientId,
                financialYear).ToArray();

        var gstinRepairCount = RepairLegacyManualSalesGstinFromSource(clientId, financialYear, savedRows);
        if (gstinRepairCount > 0)
        {
            savedRows = _salesRecordRepository.GetRecords(clientId, financialYear).ToArray();
            var repairMessage = $"Fix121 repaired {gstinRepairCount:N0} saved Sales row GSTIN value(s) from the original source without reimporting or changing other accounting fields.";
            EditStatusText = string.IsNullOrWhiteSpace(EditStatusText) ? repairMessage : $"{EditStatusText} {repairMessage}";
        }

        ReplaceResultRows(
            savedRows,
            issues);

        Summary = BuildSummaryFromRecords(
            savedRows);
    }

    private int RepairLegacyManualSalesGstinFromSource(
        long clientId,
        string financialYear,
        IReadOnlyCollection<SalesImportRecord> savedRows)
    {
        var changed = new List<SalesImportRecord>();
        var candidateGroups = savedRows
            .Where(row => row.PersistentId > 0 && row.ItemSequence > 0 &&
                          !string.IsNullOrWhiteSpace(row.SourcePath) &&
                          (row.SourceOrigin.Contains("Manual Sales", StringComparison.OrdinalIgnoreCase) ||
                           row.SourceScope.Equals("Manual", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(row => row.SourcePath.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var sourceGroup in candidateGroups)
        {
            var sourcePath = sourceGroup.Key;
            var sourceRows = sourceGroup.ToArray();
            // Do not reopen every historical source merely because it exists. The old
            // defect is evidenced when one valid GSTIN is attached to multiple visibly
            // different party names inside the same imported source.
            var suspicious = sourceRows
                .Where(row => NormalizeComparableGstin(row.CustomerGstin).Length == 15)
                .GroupBy(row => NormalizeComparableGstin(row.CustomerGstin), StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Select(row => CanonicalPartyName(row.CustomerName))
                    .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() > 1);
            if (!suspicious) continue;

            var attemptKey = $"{clientId}|{financialYear}|{sourcePath}|{sourceRows.FirstOrDefault()?.SourceHash}";
            if (!_fix121GstinRepairAttempts.Add(attemptKey) || !File.Exists(sourcePath)) continue;

            try
            {
                // The repair must use the exact workbook that produced these saved rows.
                // If the file was edited/replaced after import, fail closed rather than
                // treating its current GSTIN cells as historical authority.
                var storedHashes = sourceRows.Select(row => row.SourceHash.Trim())
                    .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (storedHashes.Length != 1) continue;
                using (var hashStream = File.OpenRead(sourcePath))
                {
                    var currentHash = Convert.ToHexString(SHA256.HashData(hashStream));
                    if (!currentHash.Equals(storedHashes[0], StringComparison.OrdinalIgnoreCase)) continue;
                }

                var firstInspection = _manualImportService.Inspect(sourcePath);
                var savedMapping = _salesMappingRepository.Get(clientId, financialYear, firstInspection.HeaderSignature);
                var options = _salesMappingRepository.GetOptions(clientId, financialYear, firstInspection.HeaderSignature);
                var inspection = _manualImportService.Inspect(sourcePath, savedMapping);
                if (!inspection.IsComplete) continue;

                var gstTreatment = string.IsNullOrWhiteSpace(options.GstTreatment) ? "LOCAL" : options.GstTreatment;
                var taxColumnMode = string.IsNullOrWhiteSpace(options.TaxColumnMode) ? "SINGLE" : options.TaxColumnMode;
                var reparsed = _manualImportService.Import(
                    sourcePath,
                    clientId,
                    ClientGstin,
                    CompanyName,
                    financialYear,
                    inspection.SuggestedMapping,
                    gstTreatment,
                    taxColumnMode);
                var correctedBySequence = reparsed.Records
                    .Where(row => row.ItemSequence > 0)
                    .GroupBy(row => row.ItemSequence)
                    .ToDictionary(group => group.Key, group => group.First());

                foreach (var persisted in sourceRows)
                {
                    if (!correctedBySequence.TryGetValue(persisted.ItemSequence, out var corrected)) continue;
                    // ItemSequence is source-order identity, but also require voucher/party/date
                    // agreement before touching the saved row. This makes the live migration
                    // fail closed if the original workbook changed after it was imported.
                    if (!persisted.DocumentNumber.Trim().Equals(corrected.DocumentNumber.Trim(), StringComparison.OrdinalIgnoreCase) ||
                        !CanonicalPartyName(persisted.CustomerName).Equals(CanonicalPartyName(corrected.CustomerName), StringComparison.OrdinalIgnoreCase) ||
                        persisted.DocumentDate?.Date != corrected.DocumentDate?.Date ||
                        !persisted.Destination.Equals(corrected.Destination, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var correctedGstin = NormalizeComparableGstin(corrected.CustomerGstin);
                    var currentGstin = NormalizeComparableGstin(persisted.CustomerGstin);
                    if (currentGstin.Equals(correctedGstin, StringComparison.OrdinalIgnoreCase)) continue;
                    persisted.CustomerGstin = correctedGstin;
                    changed.Add(persisted);
                }
            }
            catch
            {
                // Source may have moved, been locked or changed. Never guess a GSTIN.
                // A normal Sales reimport remains the explicit recovery path in that case.
            }
        }

        return changed.Count == 0 ? 0 : _salesRecordRepository.UpdateCustomerGstinReferences(clientId, changed);
    }

    private static string NormalizeComparableGstin(string value)
    {
        var normalized = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return normalized.Length == 15 ? normalized : string.Empty;
    }

    private static string CanonicalPartyName(string value) =>
        string.Join(" ", (value ?? string.Empty).ToUpperInvariant()
            .Split(new[] { ' ', '-', '.', ',', '/', '\\', '(', ')', '&' }, StringSplitOptions.RemoveEmptyEntries));

    private void ReplaceResultRows(
        IEnumerable<SalesImportRecord> records,
        IEnumerable<SalesImportIssue> issues)
    {
        var materialized = records.ToArray();

        ReplaceRows(
            AllRows,
            materialized);

        ReplaceRows(
            SalesRows,
            materialized.Where(record =>
                record.Destination == "Sales"));

        ReplaceRows(
            CreditNoteRows,
            materialized.Where(record =>
                record.Destination == "Credit Note"));

        ReplaceRows(
            Issues,
            issues);

        InventoryRows = materialized.Count(record => record.HasInventory);
        var client = _clientContext.CurrentClient;
        ActionRequiredCount = client is null
            ? 0
            : _actionRepository.GetOpen(client.Id, FinancialYear, "Sales").Count(ActionRequiredPolicy.IsImportBlocking)
              + _actionRepository.GetOpen(client.Id, FinancialYear, "Credit Note").Count(ActionRequiredPolicy.IsImportBlocking);
        OnPropertyChanged(nameof(HasActionRequired));

        RebuildB2BCustomerPanel(materialized);
        RebuildB2CCustomerPanel(materialized);

        OnPropertyChanged(
            nameof(HasPersistedRows));

        SaveChangesCommand
            .RaiseCanExecuteChanged();
    }

    private void RebuildB2CCustomerPanel(IReadOnlyCollection<SalesImportRecord> records, bool force = false)
    {
        if (_b2cRenameSessionActive && !force)
        {
            _b2cRenameRefreshPending = true;
            return;
        }

        B2CCustomers.Clear();
        var client = _clientContext.CurrentClient;
        var overrides = client is not null
            ? _salesRecordRepository.GetB2CCustomerNameOverrides(client.Id, client.FinancialYear)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        RefreshB2CDebtorLedgerOptions();

        var candidates = records
            .Where(IsSalesB2CRow)
            .Select(record => new
            {
                Record = record,
                SourceKey = SqliteSalesRecordRepository.BuildB2CSourceKey(record),
                SourceName = AccountingText.DisplayName(
                    string.IsNullOrWhiteSpace(record.OriginalCustomerName)
                        ? record.CustomerName
                        : record.OriginalCustomerName)
            })
            .Where(item => item.SourceKey.Length > 0)
            .Select(item => new
            {
                item.Record,
                item.SourceKey,
                SourceName = item.SourceName.Length == 0 ? "B2C Sales" : item.SourceName
            })
            .GroupBy(item => TallyMasterIdentity.Key(item.SourceName), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.First().SourceName, StringComparer.CurrentCultureIgnoreCase);

        foreach (var group in candidates)
        {
            var materialized = group.ToArray();
            var sourceName = materialized[0].SourceName;
            var sourceKeys = materialized.Select(item => item.SourceKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var assignedNames = sourceKeys
                .Select(key => overrides.TryGetValue(key, out var manual) ? AccountingText.DisplayName(manual) : string.Empty)
                .Concat(materialized.Select(item => AccountingText.DisplayName(item.Record.CustomerName)))
                .Where(name => name.Length > 0 && !name.StartsWith("B2C Sales", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var debtorLedgerNames = new HashSet<string>(B2CDebtorLedgerOptions, StringComparer.OrdinalIgnoreCase);
            var validAssignedNames = assignedNames.Where(debtorLedgerNames.Contains).ToArray();
            var targetName = validAssignedNames.Length == 1
                ? validAssignedNames[0]
                : debtorLedgerNames.Contains(sourceName) ? sourceName : string.Empty;
            var first = materialized[0].Record;
            B2CCustomers.Add(new SalesB2CCustomerMatchRow
            {
                SourceKey = sourceKeys[0],
                SourceKeys = sourceKeys,
                SourceSection = string.IsNullOrWhiteSpace(first.SourceSection) ? "Manual Sales" : first.SourceSection,
                PlaceOfSupply = first.PlaceOfSupply,
                SourceName = sourceName,
                CustomerName = targetName,
                TransactionCount = materialized.Length
            });
        }

        OnPropertyChanged(nameof(B2CMatchedCount));
        OnPropertyChanged(nameof(B2CUnmatchedCount));
        OnPropertyChanged(nameof(AreAllB2CCustomersMatched));
        OnPropertyChanged(nameof(B2CCompletionText));
        RefreshB2CFilteredLists();
    }

    private void RefreshB2CFilteredLists()
    {
        var q = (B2CSearchText ?? string.Empty).Trim();
        bool Match(SalesB2CCustomerMatchRow row) =>
            q.Length == 0 ||
            row.SourceName.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
            row.CustomerName.Contains(q, StringComparison.CurrentCultureIgnoreCase);

        ReplaceRows(B2CVisibleCustomers, B2CCustomers.Where(Match));
        ReplaceRows(B2CMatchedCustomers, B2CCustomers.Where(row => row.IsMatched && Match(row)));
        ReplaceRows(B2CUnmatchedCustomers, B2CCustomers.Where(row => !row.IsMatched && Match(row)));
    }

    public async Task<bool> SaveB2CCustomerNameFromPanelAsync(string sourceKey, string customerName)
    {
        var client = _clientContext.CurrentClient;
        var name = AccountingText.DisplayName(customerName);
        if (client is null || !CanEditSales || string.IsNullOrWhiteSpace(sourceKey) || name.Length == 0) return false;

        var row = B2CCustomers.FirstOrDefault(item =>
            item.SourceKey.Equals(sourceKey, StringComparison.OrdinalIgnoreCase) ||
            item.SourceKeys.Any(key => key.Equals(sourceKey, StringComparison.OrdinalIgnoreCase)));
        if (row is null) return false;

        B2CPanelStatusText = $"Saving {name} in background...";
        try
        {
            var result = await Task.Run(() =>
            {
                var debtor = EnsureB2CDebtorLedger(client.Id, client.FinancialYear, name);
                if (!debtor.IsValid) return (Updated: 0, MasterChanged: false, Error: debtor.Error);

                var updated = 0;
                foreach (var key in row.SourceKeys.DefaultIfEmpty(row.SourceKey).Distinct(StringComparer.OrdinalIgnoreCase))
                    updated += _salesRecordRepository.SaveB2CCustomerNameOverride(client.Id, client.FinancialYear, key, name);
                return (Updated: updated, MasterChanged: debtor.MasterChanged, Error: string.Empty);
            });

            if (result.Error.Length > 0)
            {
                B2CPanelStatusText = result.Error;
                RebuildB2CCustomerPanel(AllRows.ToArray());
                return false;
            }

            row.CustomerName = name;
            EnsureB2CDebtorLedgerOption(name);
            OnPropertyChanged(nameof(B2CMatchedCount));
            OnPropertyChanged(nameof(B2CUnmatchedCount));
            OnPropertyChanged(nameof(AreAllB2CCustomersMatched));
            OnPropertyChanged(nameof(B2CCompletionText));
            B2CPanelStatusText = $"Saved {name}; {result.Updated:N0} B2C Sales row(s) renamed. Target ledger is under Sundry Debtors.";
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Sales, "B2C customer name assigned");
            if (result.MasterChanged)
                _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, "B2C customer Ledger created/repaired under Sundry Debtors");
            _ = RefreshAccountingAfterSalesChangeAsync(client);
            return true;
        }
        catch (Exception ex)
        {
            B2CPanelStatusText = "Could not save B2C name: " + ex.Message;
            RebuildB2CCustomerPanel(AllRows.ToArray());
            return false;
        }
    }

    public void RefreshB2CCustomerPanelForDisplay() =>
        RebuildB2CCustomerPanel(AllRows.ToArray(), force: true);

    public void BeginB2CCustomerRenameSession()
    {
        _b2cRenameSessionActive = true;
        _b2cRenameRefreshPending = false;
    }

    public void EndB2CCustomerRenameSession()
    {
        if (!_b2cRenameSessionActive) return;
        _b2cRenameSessionActive = false;
        if (!_b2cRenameRefreshPending) return;

        _b2cRenameRefreshPending = false;
        RebuildB2CCustomerPanel(AllRows.ToArray(), force: true);
    }

    public bool SaveB2CCustomerNameFromPanel(string sourceKey, string customerName) =>
        SaveB2CCustomerNameFromPanelAsync(sourceKey, customerName).GetAwaiter().GetResult();

    private void EnsureB2CDebtorLedgerOption(string customerName)
    {
        var name = AccountingText.DisplayName(customerName);
        if (name.Length == 0 || B2CDebtorLedgerOptions.Any(existing => existing.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return;

        var insertAt = 0;
        while (insertAt < B2CDebtorLedgerOptions.Count &&
               string.Compare(B2CDebtorLedgerOptions[insertAt], name, StringComparison.CurrentCultureIgnoreCase) < 0)
            insertAt++;
        B2CDebtorLedgerOptions.Insert(insertAt, name);
    }

    private void RefreshB2CDebtorLedgerOptions()
    {
        B2CDebtorLedgerOptions.Clear();
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var masters = _masterRepository.Get(client.Id, client.FinancialYear, false, string.Empty)
            .Where(master => !master.IsArchived)
            .ToArray();
        var groups = masters.Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            .GroupBy(master => TallyMasterIdentity.Key(master.Name))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var names = masters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .Where(master => IsUnderSundryDebtors(master.Parent, groups))
            .Select(master => AccountingText.DisplayName(master.Name))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        ReplaceRows(B2CDebtorLedgerOptions, names);
    }

    private static bool IsUnderSundryDebtors(string parent, IReadOnlyDictionary<string, MasterRecord> groups)
    {
        var current = AccountingText.DisplayName(parent);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (current.Length > 0 && seen.Add(TallyMasterIdentity.Key(current)))
        {
            if (current.Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase)) return true;
            if (!groups.TryGetValue(TallyMasterIdentity.Key(current), out var group)) break;
            current = AccountingText.DisplayName(group.Parent);
        }
        return false;
    }

    private (bool IsValid, bool MasterChanged, string Error) EnsureB2CDebtorLedger(long clientId, string financialYear, string customerName)
    {
        var name = AccountingText.DisplayName(customerName);
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear) || name.Length == 0)
            return (false, false, "Choose a Sundry Debtors ledger or enter a new customer name.");

        var masters = _masterRepository.Get(clientId, financialYear, false, string.Empty)
            .Where(master => !master.IsArchived)
            .ToArray();
        var groups = masters.Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            .GroupBy(master => TallyMasterIdentity.Key(master.Name))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var key = TallyMasterIdentity.Key(name);
        var matches = masters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && TallyMasterIdentity.Key(master.Name) == key)
            .GroupBy(master => master.PersistentId)
            .Select(group => group.First())
            .Take(2)
            .ToArray();

        if (matches.Length > 1)
            return (false, false, $"{name} has duplicate Ledger Masters. Resolve them in Ledger Master before using it for B2C rename.");

        if (matches.Length == 1)
        {
            var existing = matches[0];
            if (IsUnderSundryDebtors(existing.Parent, groups)) return (true, false, string.Empty);

            // Preserve the older safe repair only for a local, untouched Primary fallback.
            if(existing.IsUserEdited||existing.TallyMasterId.Trim().Length>0 ||
                (existing.Parent.Trim().Length > 0 && !existing.Parent.Equals("Primary", StringComparison.OrdinalIgnoreCase)))
                return (false, false, $"{name} already exists under {existing.Parent}. B2C rename can use only Sundry Debtors ledgers.");
        }

        // New B2C customer Ledgers are always direct children of Sundry Debtors.
        _masterRepository.Upsert(clientId, financialYear, [new MasterRecord
        {
            ClientId = clientId,
            FinancialYear = financialYear,
            Name = name,
            Parent="Sundry Debtors",
            Type = "Ledger",
            Company = _companyName,
            Source = "B2C Sales Rename",
            IsUserEdited = false
        }], markUserEdited: false);

        var resolvedMasters = _masterRepository.Get(clientId, financialYear, false, string.Empty)
            .Where(master => !master.IsArchived)
            .ToArray();
        var resolvedGroups = resolvedMasters.Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            .GroupBy(master => TallyMasterIdentity.Key(master.Name))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var resolved = resolvedMasters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && TallyMasterIdentity.Key(master.Name) == key)
            .Take(2)
            .ToArray();
        var valid = resolved.Length == 1 && IsUnderSundryDebtors(resolved[0].Parent, resolvedGroups);
        return valid
            ? (true, true, string.Empty)
            : (false, false, $"Could not create {name} under Sundry Debtors.");
    }

    private void RebuildB2BCustomerPanel(IReadOnlyCollection<SalesImportRecord> records)
    {
        var client = _clientContext.CurrentClient;
        var selectedGstin = SelectedB2BCustomer?.Gstin ?? string.Empty;
        var materialized = records.ToArray();

        _b2bSourceNameDetails = LoadB2BSourceNameDetails(materialized);
        if (client is not null && !_b2bDuplicateCleanupRunning)
        {
            var cleanupMessages = CleanupExistingB2BManualDuplicates(client.Id, client.FinancialYear, materialized);
            if (cleanupMessages.Count > 0)
            {
                materialized = _salesRecordRepository.GetRecords(client.Id, client.FinancialYear).ToArray();
                _b2bSourceNameDetails = LoadB2BSourceNameDetails(materialized);
                ReplaceRows(AllRows, materialized);
                ReplaceRows(SalesRows, materialized.Where(record => record.Destination == "Sales"));
                ReplaceRows(CreditNoteRows, materialized.Where(record => record.Destination == "Credit Note"));
                B2BPanelStatusText = string.Join(" ", cleanupMessages);
            }
        }

        B2BCustomers.Clear();

        foreach (var group in materialized
                     .Where(IsRegisteredB2BRow)
                     .Where(row => NormalizeGstin(row.CustomerGstin).Length == 15)
                     .GroupBy(row => NormalizeGstin(row.CustomerGstin), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var resolved = group
                .Where(IsResolvedB2BCustomerName)
                .OrderByDescending(row => string.Equals(row.CustomerNameSource, "Manual B2B Panel", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(row => (row.CustomerNameSource ?? string.Empty).Contains("Master", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            _b2bSourceNameDetails.TryGetValue(group.Key, out var sourceDetail);
            var customerName = resolved is null ? string.Empty : AccountingText.DisplayName(resolved.CustomerName);
            var recipientName = sourceDetail is null ? string.Empty : AccountingText.DisplayName(sourceDetail.RecipientName);
            var tradeName = sourceDetail is null ? string.Empty : AccountingText.DisplayName(sourceDetail.TradeName);

            B2BCustomers.Add(new SalesB2BCustomerMatchRow
            {
                Gstin = group.Key,
                CustomerName = customerName,
                RecipientName = recipientName,
                TradeName = tradeName,
                NameSource = resolved?.CustomerNameSource?.Trim() ?? "Fallback / name not resolved",
                Status = resolved is null ? "Not Matched" : "Matched",
                DuplicateStatus = sourceDetail?.SourceConflict ?? string.Empty,
                TransactionCount = group.Count(),
                IsMatched = resolved is not null
            });
        }

        MarkB2BOriginalDuplicateNames();

        OnPropertyChanged(nameof(B2BMatchedCount));
        OnPropertyChanged(nameof(B2BUnmatchedCount));
        OnPropertyChanged(nameof(AreAllB2BCustomersMatched));
        OnPropertyChanged(nameof(B2BCompletionText));
        RefreshB2BFilteredLists();

        SelectedB2BCustomer = B2BCustomers.FirstOrDefault(row =>
            row.Gstin.Equals(selectedGstin, StringComparison.OrdinalIgnoreCase));
    }

    private Dictionary<string, SalesCustomerMasterNameDetail> LoadB2BSourceNameDetails(
        IReadOnlyCollection<SalesImportRecord> records)
    {
        var result = new Dictionary<string, SalesCustomerMasterNameDetail>(StringComparer.OrdinalIgnoreCase);
        var candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var masterReferences = records
            .Select(record => record.SalesMasterFile?.Trim() ?? string.Empty)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var path in masterReferences)
        {
            if (File.Exists(path)) candidatePaths.Add(path);
        }

        // Customer Master evidence is moved into the managed Sales/Customer Master folder.
        // Resolve the moved copy when older Sales rows still hold the pre-move source path.
        var client = _clientContext.CurrentClient;
        if (client is not null)
        {
            foreach (var root in GetLinkedAutomationFolderPaths(client.Id, client.FinancialYear))
            {
                foreach (var reference in masterReferences)
                {
                    var fileName = Path.GetFileName(reference);
                    if (string.IsNullOrWhiteSpace(fileName)) continue;
                    var managed = Path.Combine(root, "Sales", "Customer Master", fileName);
                    if (File.Exists(managed)) candidatePaths.Add(managed);
                    var direct = Path.Combine(root, fileName);
                    if (File.Exists(direct)) candidatePaths.Add(direct);
                }
            }
        }

        foreach (var path in candidatePaths.OrderByDescending(path =>
                 {
                     try { return File.GetLastWriteTimeUtc(path); }
                     catch { return DateTime.MinValue; }
                 }))
        {
            try
            {
                foreach (var pair in SalesAutoImportService.ReadCustomerMasterNameDetailsForB2BPanel(path))
                {
                    if (!result.ContainsKey(pair.Key)) result[pair.Key] = pair.Value;
                }
            }
            catch
            {
                // The panel is diagnostic/editing UI; a temporarily unavailable Master file must not block Sales.
            }
        }

        // Keep a useful authoritative display even if an older Master file is unavailable.
        foreach (var group in records
                     .Where(IsRegisteredB2BRow)
                     .Where(record => (record.CustomerNameSource ?? string.Empty).Contains("Master", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(record => NormalizeGstin(record.CustomerGstin), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length != 15 || result.ContainsKey(group.Key)) continue;
            var name = group.Select(record => AccountingText.DisplayName(record.CustomerName))
                .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
            if (name.Length == 0) continue;
            result[group.Key] = new SalesCustomerMasterNameDetail
            {
                Gstin = group.Key,
                RecipientName = name,
                PreferredName = name
            };
        }

        return result;
    }

    private List<string> CleanupExistingB2BManualDuplicates(
        long clientId,
        string financialYear,
        IReadOnlyCollection<SalesImportRecord> records)
    {
        var messages = new List<string>();
        var entries = _salesRecordRepository.GetB2BCustomerNameOverrideEntries(clientId, financialYear)
            .OrderBy(entry => entry.UpdatedUtc)
            .ThenBy(entry => entry.Gstin, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (entries.Length == 0) return messages;

        var authoritativeOwners = BuildB2BAuthoritativeNameOwners(records);
        var acceptedManualNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            _b2bDuplicateCleanupRunning = true;
            foreach (var entry in entries)
            {
                var key = CanonicalB2BName(entry.CustomerName);
                if (key.Length == 0) continue;

                if (IsOwnAuthoritativeB2BName(entry.Gstin, entry.CustomerName))
                {
                    if (RemoveB2BManualOverrideAndRestoreOriginal(clientId, financialYear, entry.Gstin))
                        messages.Add($"Removed redundant manual name for {entry.Gstin}; original Recipient/Trade name retained.");
                    continue;
                }

                if (authoritativeOwners.TryGetValue(key, out var originalOwner) &&
                    !originalOwner.Gstin.Equals(entry.Gstin, StringComparison.OrdinalIgnoreCase))
                {
                    if (RemoveB2BManualOverrideAndRestoreOriginal(clientId, financialYear, entry.Gstin))
                        messages.Add($"Removed duplicate manual name {entry.CustomerName} from {entry.Gstin}; original name belongs to {originalOwner.Gstin} ({originalOwner.Field}).");
                    continue;
                }

                if (acceptedManualNames.TryGetValue(key, out var firstGstin) &&
                    !firstGstin.Equals(entry.Gstin, StringComparison.OrdinalIgnoreCase))
                {
                    if (RemoveB2BManualOverrideAndRestoreOriginal(clientId, financialYear, entry.Gstin))
                        messages.Add($"Removed later duplicate manual name {entry.CustomerName} from {entry.Gstin}; first entry on {firstGstin} was kept.");
                    continue;
                }

                acceptedManualNames[key] = entry.Gstin;
            }
        }
        finally
        {
            _b2bDuplicateCleanupRunning = false;
        }

        return messages;
    }

    private Dictionary<string, (string Gstin, string Field)> BuildB2BAuthoritativeNameOwners(
        IReadOnlyCollection<SalesImportRecord> records)
    {
        var owners = new Dictionary<string, (string Gstin, string Field)>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in _b2bSourceNameDetails)
        {
            AddOwner(pair.Key, pair.Value.RecipientName, "Recipient Name");
            AddOwner(pair.Key, pair.Value.TradeName, "Trade Name");
        }

        foreach (var record in records.Where(IsRegisteredB2BRow))
        {
            var gstin = NormalizeGstin(record.CustomerGstin);
            if (gstin.Length != 15 || string.Equals(record.CustomerNameSource, "Manual B2B Panel", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsResolvedB2BCustomerName(record)) continue;
            AddOwner(gstin, record.CustomerName, record.CustomerNameSource?.Trim() ?? "Original Customer Name");
        }

        return owners;

        void AddOwner(string gstin, string? value, string field)
        {
            var key = CanonicalB2BName(value);
            if (key.Length == 0 || owners.ContainsKey(key)) return;
            owners[key] = (gstin, field);
        }
    }

    private bool IsOwnAuthoritativeB2BName(string gstin, string name)
    {
        if (!_b2bSourceNameDetails.TryGetValue(gstin, out var detail)) return false;
        var key = CanonicalB2BName(name);
        return key.Length > 0 &&
               (key == CanonicalB2BName(detail.RecipientName) ||
                key == CanonicalB2BName(detail.TradeName));
    }

    private (string Gstin, string Field)? FindB2BNameDuplicate(string gstin, string name)
    {
        var key = CanonicalB2BName(name);
        if (key.Length == 0) return null;

        foreach (var pair in _b2bSourceNameDetails)
        {
            if (pair.Key.Equals(gstin, StringComparison.OrdinalIgnoreCase)) continue;
            if (key == CanonicalB2BName(pair.Value.RecipientName)) return (pair.Key, "Recipient Name");
            if (key == CanonicalB2BName(pair.Value.TradeName)) return (pair.Key, "Trade Name");
        }

        foreach (var row in B2BCustomers)
        {
            if (row.Gstin.Equals(gstin, StringComparison.OrdinalIgnoreCase)) continue;
            if (key == CanonicalB2BName(row.CustomerName)) return (row.Gstin, "Customer Name");
            if (key == CanonicalB2BName(row.RecipientName)) return (row.Gstin, "Recipient Name");
            if (key == CanonicalB2BName(row.TradeName)) return (row.Gstin, "Trade Name");
        }

        return null;
    }

    private bool RemoveB2BManualOverrideAndRestoreOriginal(long clientId, string financialYear, string gstin)
    {
        var overrides = _salesRecordRepository.GetB2BCustomerNameOverrides(clientId, financialYear);
        if (!overrides.ContainsKey(gstin)) return false;

        var replacement = string.Empty;
        var source = string.Empty;
        if (_b2bSourceNameDetails.TryGetValue(gstin, out var detail))
        {
            replacement = AccountingText.DisplayName(detail.PreferredName);
            source = replacement.Length > 0 ? "GST Recipient Master" : string.Empty;
        }

        _salesRecordRepository.DeleteB2BCustomerNameOverride(
            clientId,
            financialYear,
            gstin,
            replacement,
            source);
        return true;
    }

    private void MarkB2BOriginalDuplicateNames()
    {
        var ownerLists = new Dictionary<string, List<(string Gstin, string Field)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in B2BCustomers)
        {
            Add(row.Gstin, row.RecipientName, "Recipient Name");
            Add(row.Gstin, row.TradeName, "Trade Name");
            Add(row.Gstin, row.CustomerName, "Customer Name");
        }

        var duplicateByGstin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var owners in ownerLists.Values.Where(list => list.Select(item => item.Gstin).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            var gstins = owners.Select(item => item.Gstin).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var gstin in gstins)
            {
                var others = gstins.Where(other => !other.Equals(gstin, StringComparison.OrdinalIgnoreCase)).ToArray();
                duplicateByGstin[gstin] = $"Duplicate name also appears under {string.Join(", ", others)}";
            }
        }

        if (duplicateByGstin.Count == 0) return;

        var rebuilt = B2BCustomers.Select(row => new SalesB2BCustomerMatchRow
        {
            Gstin = row.Gstin,
            CustomerName = row.CustomerName,
            RecipientName = row.RecipientName,
            TradeName = row.TradeName,
            NameSource = row.NameSource,
            Status = row.Status,
            DuplicateStatus = duplicateByGstin.TryGetValue(row.Gstin, out var duplicate)
                ? string.Join(" | ", new[] { row.DuplicateStatus, duplicate }.Where(value => value.Length > 0))
                : row.DuplicateStatus,
            TransactionCount = row.TransactionCount,
            IsMatched = row.IsMatched
        }).ToArray();

        B2BCustomers.Clear();
        foreach (var row in rebuilt) B2BCustomers.Add(row);

        void Add(string gstin, string value, string field)
        {
            var key = CanonicalB2BName(value);
            if (key.Length == 0) return;
            if (!ownerLists.TryGetValue(key, out var list)) ownerLists[key] = list = [];
            if (!list.Any(item => item.Gstin.Equals(gstin, StringComparison.OrdinalIgnoreCase) && item.Field == field))
                list.Add((gstin, field));
        }
    }

    private static string CanonicalB2BName(string? value) =>
        new string(AccountingText.CleanWhitespace(value)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private void RefreshB2BFilteredLists()
    {
        var searchText = AccountingText.CleanWhitespace(B2BSearchText);
        var gstinSearch = NormalizeGstin(searchText);

        static bool Matches(SalesB2BCustomerMatchRow row, string searchText, string gstinSearch)
        {
            if (searchText.Length == 0) return true;
            return row.Gstin.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                   (gstinSearch.Length > 0 && row.Gstin.Contains(gstinSearch, StringComparison.OrdinalIgnoreCase)) ||
                   row.CustomerName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                   row.RecipientName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                   row.TradeName.Contains(searchText, StringComparison.OrdinalIgnoreCase);
        }

        B2BMatchedCustomers.Clear();
        foreach (var row in B2BCustomers.Where(row => row.IsMatched && Matches(row, searchText, gstinSearch)))
            B2BMatchedCustomers.Add(row);

        B2BUnmatchedCustomers.Clear();
        foreach (var row in B2BCustomers.Where(row => !row.IsMatched && Matches(row, searchText, gstinSearch)))
            B2BUnmatchedCustomers.Add(row);
    }

    private static bool IsRegisteredB2BRow(SalesImportRecord row)
    {
        var section = (row.SourceSection ?? string.Empty).Trim();

        // Fix122: GSTR-1 section remains first authority, but Manual Sales Registers
        // normally have no B2B/B2C SourceSection. A valid saved Customer GSTIN must
        // therefore still participate in the registered-customer panel.
        if (section.StartsWith("B2C", StringComparison.OrdinalIgnoreCase) ||
            section.StartsWith("EXP", StringComparison.OrdinalIgnoreCase) ||
            section.StartsWith("CDNUR", StringComparison.OrdinalIgnoreCase))
            return false;
        if (section.StartsWith("B2B", StringComparison.OrdinalIgnoreCase) ||
            section.StartsWith("CDNR", StringComparison.OrdinalIgnoreCase))
            return true;

        var destination = (row.Destination ?? string.Empty).Trim();
        return (destination.Equals("Sales", StringComparison.OrdinalIgnoreCase) ||
                destination.Equals("Credit Note", StringComparison.OrdinalIgnoreCase)) &&
               NormalizeGstin(row.CustomerGstin).Length == 15;
    }

    private static bool IsSalesB2BRow(SalesImportRecord row)
    {
        if (!(row.Destination ?? string.Empty).Trim().Equals("Sales", StringComparison.OrdinalIgnoreCase))
            return false;

        var section = (row.SourceSection ?? string.Empty).Trim();
        if (section.StartsWith("EXP", StringComparison.OrdinalIgnoreCase) ||
            section.StartsWith("B2C", StringComparison.OrdinalIgnoreCase))
            return false;
        if (section.StartsWith("B2B", StringComparison.OrdinalIgnoreCase))
            return true;

        // Manual Sales Registers do not carry GSTR-1 section labels. Partition those
        // saved Sales rows by the actual GSTIN evidence already visible in the grid.
        return NormalizeGstin(row.CustomerGstin).Length == 15;
    }

    private static bool IsSalesB2CRow(SalesImportRecord row)
    {
        if (!(row.Destination ?? string.Empty).Trim().Equals("Sales", StringComparison.OrdinalIgnoreCase))
            return false;

        var section = (row.SourceSection ?? string.Empty).Trim();
        if (section.StartsWith("EXP", StringComparison.OrdinalIgnoreCase) ||
            section.StartsWith("B2B", StringComparison.OrdinalIgnoreCase))
            return false;
        if (section.StartsWith("B2C", StringComparison.OrdinalIgnoreCase))
            return true;

        return NormalizeGstin(row.CustomerGstin).Length != 15;
    }

    private static bool IsResolvedB2BCustomerName(SalesImportRecord row)
    {
        var gstin = NormalizeGstin(row.CustomerGstin);
        var name = AccountingText.CleanWhitespace(row.CustomerName);
        var source = AccountingText.CleanWhitespace(row.CustomerNameSource);
        if (name.Length == 0 || name.Equals(gstin, StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("Customer -", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Customer", StringComparison.OrdinalIgnoreCase)) return false;
        if (source.Contains("Fallback", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string NormalizeGstin(string? value) =>
        new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static SalesImportSummary BuildSummaryFromRecords(
        IReadOnlyCollection<SalesImportRecord> records) =>
        new()
        {
            SalesRows = records.Count(record =>
                record.Destination == "Sales"),
            CreditNoteRows = records.Count(record =>
                record.Destination == "Credit Note"),
            B2BRows = records.Count(IsSalesB2BRow),
            B2CRows = records.Count(IsSalesB2CRow),
            ExportRows = records.Count(record =>
                record.SourceSection.StartsWith(
                    "EXP",
                    StringComparison.OrdinalIgnoreCase)),
            AmendmentRows = records.Count(record =>
                record.IsAmendment)
        };

    private void RecordDashboardActivity(
        SalesAutoImportResult result)
    {
        var client = _clientContext.CurrentClient;

        if (client is null)
        {
            return;
        }

        var dates = result.Records
            .Where(record =>
                record.DocumentDate.HasValue)
            .Select(record =>
                record.DocumentDate!.Value.Date)
            .ToArray();

        DateTime? periodFrom = dates.Length == 0
            ? FinancialYearStartDate(
                result.FinancialYear)
            : dates.Min();

        DateTime? periodTo = dates.Length == 0
            ? FinancialYearEndDate(
                result.FinancialYear)
            : dates.Max();

        _activityRepository.Record(
            new ImportActivity
            {
                ClientId = client.Id,
                ImportType = "Sales",
                PeriodFrom = periodFrom,
                PeriodTo = periodTo,
                PeriodLabel = BuildPeriodLabel(
                    periodFrom,
                    periodTo,
                    result.FinancialYear),
                FilingStatus =
                    $"GSTR-1: {result.Verification.Status}",
                RecordCount =
                    result.Summary.TotalOutputRows,
                BankCount = 0,
                SourceSummary = string.Join(
                    "; ",
                    result.SourceManifest
                        .Where(source =>
                            source.FileType == "GSTR1")
                        .Select(source =>
                            source.SourceFile)
                        .Distinct(
                            StringComparer.OrdinalIgnoreCase)),
                Status = "Auto Imported",
                Details =
                    $"{result.Summary.SalesRows:N0} Sales, " +
                    $"{result.Summary.CreditNoteRows:N0} Credit Notes, " +
                    $"{result.Summary.B2BRows:N0} B2B, " +
                    $"{result.Summary.B2CRows:N0} B2C, " +
                    $"{result.Summary.ExportRows:N0} Export.",
                ImportedUtc = DateTime.UtcNow
            });

        _activityContext.NotifyChanged();
    }

    private static string BuildSourceVerificationText(
        SalesSourceVerification verification)
    {
        var branch =
            string.IsNullOrWhiteSpace(
                verification.SourceBranch)
                ? verification.SourceOrigin
                : verification.SourceBranch;

        return
            $"Source verification: GSTR-1 {branch} - " +
            $"{verification.Status} | " +
            $"{verification.SelectedPeriods.Count:N0} period(s) | " +
            $"Customer master: {verification.CustomerMasterStatus}";
    }

    private static string BuildSourceFingerprint(
        SalesAutoImportResult result)
    {
        var builder = new StringBuilder();

        builder.Append(result.ClientGstin)
            .Append('|')
            .Append(result.FinancialYear)
            .Append('|')
            .Append(result.Verification.Status)
            .Append('|')
            .Append(result.Verification.CustomerMasterSourceHash)
            .AppendLine();

        foreach (var source in result.SourceManifest
                     .OrderBy(
                         item => item.FileType,
                         StringComparer.OrdinalIgnoreCase)
                     .ThenBy(
                         item => item.ReturnPeriod,
                         StringComparer.OrdinalIgnoreCase)
                     .ThenBy(
                         item => item.SourcePath,
                         StringComparer.OrdinalIgnoreCase)
                     .ThenBy(
                         item => item.InternalJsonName,
                         StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(source.FileType).Append('|')
                .Append(source.SourceBranch).Append('|')
                .Append(source.SourceScope).Append('|')
                .Append(source.ReturnPeriod).Append('|')
                .Append(source.SourcePath).Append('|')
                .Append(source.InternalJsonName).Append('|')
                .Append(source.EmbeddedTimestamp).Append('|')
                .Append(source.PayloadHash)
                .AppendLine();
        }

        return Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    builder.ToString())));
    }

    private static string BuildPeriodLabel(
        DateTime? periodFrom,
        DateTime? periodTo,
        string financialYear)
    {
        if (periodFrom is null &&
            periodTo is null)
        {
            return string.IsNullOrWhiteSpace(
                financialYear)
                ? "Period not available"
                : $"FY {financialYear}";
        }

        if (periodFrom.HasValue &&
            periodTo.HasValue &&
            periodFrom.Value.Year ==
            periodTo.Value.Year &&
            periodFrom.Value.Month ==
            periodTo.Value.Month)
        {
            return periodFrom.Value.ToString(
                "MMM yyyy",
                CultureInfo.InvariantCulture);
        }

        if (periodFrom.HasValue &&
            periodTo.HasValue)
        {
            return
                $"{periodFrom.Value:MMM yyyy} – " +
                $"{periodTo.Value:MMM yyyy}";
        }

        return (periodFrom ?? periodTo)!.Value
            .ToString(
                "MMM yyyy",
                CultureInfo.InvariantCulture);
    }

    private static DateTime? FinancialYearStartDate(
        string financialYear)
    {
        if (financialYear.Length < 4 ||
            !int.TryParse(
                financialYear[..4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var year))
        {
            return null;
        }

        return new DateTime(
            year,
            4,
            1);
    }

    private static DateTime? FinancialYearEndDate(
        string financialYear) =>
        FinancialYearStartDate(
            financialYear)?
            .AddYears(1)
            .AddDays(-1);

    private static void ReplaceRows<T>(
        ObservableCollection<T> destination,
        IEnumerable<T> rows)
    {
        if (destination is BulkObservableCollection<T> bulk)
        {
            bulk.ReplaceRange(rows);
            return;
        }

        destination.Clear();
        foreach (var row in rows) destination.Add(row);
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanImportSales));
        OnPropertyChanged(nameof(CanReimportSales));
        OnPropertyChanged(nameof(CanEditSales));
        OnPropertyChanged(nameof(IsSalesReadOnly));
        OnPropertyChanged(nameof(CanClearSales));
        OnPropertyChanged(nameof(SalesPermissionSummary));
        BrowseEReturnsRootCommand
            .RaiseCanExecuteChanged();

        CancelCommand
            .RaiseCanExecuteChanged();

        SaveChangesCommand
            .RaiseCanExecuteChanged();

        RefreshCommand
            .RaiseCanExecuteChanged();

        ReimportCommand.RaiseCanExecuteChanged();
        ManualImportCommand.RaiseCanExecuteChanged();
        RemapSalesRegisterCommand.RaiseCanExecuteChanged();
        ClearModuleCommand.RaiseCanExecuteChanged();
        ItemNameMappingCommand.RaiseCanExecuteChanged();
        UnitMappingCommand.RaiseCanExecuteChanged();
        QuantityConversionCommand.RaiseCanExecuteChanged();
        ApplyB2BManualNameCommand.RaiseCanExecuteChanged();
    }

    private void NotifyStatusVisibility()
    {
        OnPropertyChanged(
            nameof(HasWarning));

        OnPropertyChanged(
            nameof(HasError));
    }
}
