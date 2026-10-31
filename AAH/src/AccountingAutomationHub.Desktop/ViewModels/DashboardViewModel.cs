using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Application.Clients;
using AccountingAutomationHub.Application.Gst;
using AccountingAutomationHub.Application.Imports;
using AccountingAutomationHub.Application.Journals;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.Models.Tally;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Gst;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Domain.Imports;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Gst;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Persistence;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Tally;
using Microsoft.Win32;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed record DashboardGstinRegistration(string Gstin, string Role);



public sealed class DashboardViewModel :
    PageViewModel,
    IDisposable
{
    private static readonly HashSet<string> LocalTallyReports =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Company Overview",
            "Master Statistics",
            "Voucher Statistics",
            "Duplicate Control"
        };

    private readonly ActiveClientContext _clientContext;
    private readonly IImportActivityRepository _activityRepository;
    private readonly ImportActivityContext _activityContext;
    private readonly TallyLiveConnectionCoordinator _tallyCoordinator;
    private readonly AccountingRefreshCoordinator _accountingRefreshCoordinator;
    private readonly IBankingRepository _bankingRepository;
    private readonly IPurchaseRecordRepository _purchaseRepository;
    private readonly ISalesRecordRepository _salesRepository;
    private readonly IJournalRepository _journalRepository;
    private readonly IGstRepository _gstRepository;
    private readonly IMasterRepository _masterRepository;
    private readonly IStockMasterRepository _stockMasterRepository;
    private readonly IActionRequiredRepository _actionRequiredRepository;
    private readonly SqliteTallyRepository _tallyRepository;
    private readonly TallyExportSelectionContext _tallyExportSelectionContext;
    private readonly BackgroundAutomationService _automationService;
    private readonly IClientPreferenceRepository _clientPreferences;
    private readonly ClientDiagnosticExportService _diagnosticExportService;
    private readonly ClientWorkspaceStateCache _workspaceStateCache;
    private readonly AccountingLiveChangeHub _liveChangeHub;
    private readonly ApplicationPreferenceService _applicationPreferences;
    private readonly SqliteGovernanceRepository _governanceRepository;
    private readonly SemaphoreSlim _reviewSyncGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _automaticReviewSyncRequested = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _reviewWorkbookSyncFingerprints = new(StringComparer.OrdinalIgnoreCase);

    private string _displayName = "No client selected";
    private string _clientCode = "—";
    private string _clientName = "—";
    private string _tradeName = "—";
    private string _legalName = "—";
    private string _gstin = "—";
    private string _pan = "—";
    private string _state = "—";
    private string _financialYear = "—";
    private string _contactText = "No contact details";
    private string _addressText = "No address recorded";
    private string _lastActivityText = "No client activity recorded";
    private string _workspaceSummary = "Open a client from the Client Directory.";
    private string _autoImportFolderStatus = "Use Auto Import From Folder to force a manual re-scan of this client's linked Automation Folder.";
    private string _autoImportSummary = "No Auto Import activity for this client yet.";
    private string _autoImportModules = "Modules: —";
    private string _autoImportHealthState = "Neutral";
    private int _autoImportErrorCount;
    private double _autoImportProgressPercent;
    private bool _isAutoImportActive;
    private int _autoImportPendingFiles;
    private int _autoImportTransactionCount;
    private int _autoImportSuccessfulFiles;
    private int _autoImportActiveFiles;
    private string _diagnosticExportStatus = "Generate Excel / CSV auto-saves into this client’s linked folder. Saving an edited review workbook applies only changed rows automatically.";
    private int _availableDataModules;
    private int _totalRecords;
    private bool _hasAnyData;

    private string _tallyConnectionStatus = "Tally offline";
    private string _tallyLinkStatus = "No Tally company linked.";
    private string _tallyCompanyName = "—";
    private string _tallyCompanyGstin = "—";
    private string _tallyCompanyFinancialYear = "—";
    private string _tallyCompanyState = "—";
    private string _tallyCompanyBooksPeriod = "—";
    private string _tallyCompanyNumber = "—";
    private string _tallyIdentityMatchText = "Select an open Tally company from the top dropdown.";
    private string _tallyLastSyncText = "Never synchronized";
    private string _tallyLastError = string.Empty;
    private string _tallyActionStatus = "Waiting for Tally.";
    private string _tallyLiveXmlStatus = "Live Tally XML waiting for accounting data.";
    private string _selectedTallyReport = "Balance Sheet";
    private string _tallyReportStatus = "Link and open a Tally company to view reports.";
    private string _tallyStatisticsStatus = "Tally Statistics has not been refreshed.";
    private string _tallyExportStatus = "No Tally export has been run.";
    private string _tallyStatusState = "Neutral";
    private string _tallyExportHealthState = "Neutral";
    // FIXED28 Dashboard Tally Auto Sync is layered on the existing serialized
    // ExportSelectedAsync authority. It never creates a second Tally writer.
    private string _tallyAutoSyncMode = "Off";
    private string _tallyAutoSyncActiveMode = "Selected";
    private string _tallyAutoSyncStatus = "Automatic Tally Sync is off.";
    // R24 Fix7: Auto Import is owned by the existing Settings page only.
    // Dashboard stages only Auto Open/Auto Close; Save never changes Auto Import or company-path authority.
    private readonly SqliteClientPreferenceRepository _tallyAutoImportPreferences = new();
    private bool _tallyAutoImportEnabled = true;
    private bool _tallyAutoOpen;
    private bool _tallyAutoClose;
    private string _tallyExactCompanyFolder = string.Empty;
    private string _tallyAutoLifecycleStatus = "Select a linked Client/FY to configure Tally Auto Open / Auto Close.";
    private bool _isTallyAutoSyncSettingsOpen;
    private bool _autoSyncBank = true;
    private bool _autoSyncBankImportSuspense;
    private bool _autoSyncPurchase = true;
    private bool _autoSyncSales = true;
    private bool _autoSyncCash = true;
    private bool _autoSyncJournal = true;
    private bool _autoSyncRecurring = true;
    private bool _autoSyncMasterChanges = true;
    private bool _autoSyncCreateMasters = true;
    private bool _autoSyncCreateVouchers = true;
    private bool _autoSyncAlterMasters = true;
    private bool _autoSyncAlterVouchers = true;
    private bool _autoSyncDelete;
    private bool _autoSyncRecreate;
    private bool _loadingTallyAutoLifecycle;
    private int _autoTallySyncPendingMask;
    // A committed local source change is distinct from a startup/backlog scan.
    // Existing imported source rows may need ALTER even when ReadyCount is zero.
    private int _autoTallySyncChangedMask;
    // Fix28: an explicit user selection is distinct from client-open/cache repaint.
    // This is the only source-free way to request a review of an existing backlog.
    private int _autoTallySyncExplicitSelectionMask;
    private readonly Dictionary<string, string> _autoTallyKnownLocalRevisions = new(StringComparer.OrdinalIgnoreCase);
    private int _autoTallyRecreateContinuation;
    private int _autoTallySyncScheduleQueued;
    private int _autoTallySyncScheduleVersion;
    private int _autoTallySyncInFlight;
    private DateTime _lastAutoTallySyncAttemptUtc = DateTime.MinValue;
    private int _tallyExportTotalTransactions;
    private int _tallyExportSelectedTransactions;
    private bool _isTallyReportBusy;
    private bool _isTallyStatisticsBusy;

    private string _bankExportPeriodMode = "Month";
    private string _purchaseExportPeriodMode = "Month";
    private string _salesExportPeriodMode = "Month";
    private string _cashExportPeriodMode = "Month";
    private string _journalExportPeriodMode = "Month";
    private TallyExportMonthOption? _selectedBankExportMonth;
    private TallyExportMonthOption? _selectedPurchaseExportMonth;
    private TallyExportMonthOption? _selectedSalesExportMonth;
    private DateTime? _bankExportFromDate;
    private DateTime? _bankExportToDate;
    private DateTime? _purchaseExportFromDate;
    private DateTime? _purchaseExportToDate;
    private DateTime? _salesExportFromDate;
    private DateTime? _salesExportToDate;
    private DateTime? _cashExportFromDate;
    private DateTime? _cashExportToDate;
    private DateTime? _journalExportFromDate;
    private DateTime? _journalExportToDate;
    private DateTime? _bankExportMinDate;
    private DateTime? _bankExportMaxDate;
    private DateTime? _purchaseExportMinDate;
    private DateTime? _purchaseExportMaxDate;
    private DateTime? _salesExportMinDate;
    private DateTime? _salesExportMaxDate;
    private DateTime? _cashExportMinDate;
    private DateTime? _cashExportMaxDate;
    private DateTime? _journalExportMinDate;
    private DateTime? _journalExportMaxDate;
    private bool _includeBankStatement;
    private bool _includePurchases;
    private bool _includeSales;
    private bool _includeCashEntries;
    private bool _includeJournals;
    private bool _selectAllExportMonths = true;
    private bool _updatingSelectAllExportMonths;

    private string _lastBankImportStatus = "No Bank Statement date-range import has been run.";
    private bool _hasPendingTallySelection;
    private bool _hasConfirmedTallyLink;
    private bool _canConfirmTallyLink;
    private bool _isPrivateBranchDashboard;

    // Original dashboard Tally link/export applies to the current Client/FY,
    // including separate private branch accounts. No parent-link inheritance.
    public bool ShowTallyPanel => true;
    public bool IsBranchTallyReference => false;

    public void SetPrivateBranchDashboard(bool isPrivate)
    {
        if (_isPrivateBranchDashboard == isPrivate) return;
        _isPrivateBranchDashboard = isPrivate;
        OnPropertyChanged(nameof(ShowTallyPanel));
        OnPropertyChanged(nameof(IsBranchTallyReference));
        RefreshTallySection();
        RefreshTallyCommandStates();
    }
    private bool _canCancelTallySelection;
    private bool _canUnlinkTallyLink;
    private bool _canImportBankRange;
    private bool _unlinkConfirmationPending;
    private long _dateClientId;
    private string _dateFinancialYear = string.Empty;
    private readonly Dictionary<string, MonthRangeGestureState> _monthRangeGestures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingTallyPreferenceWrite> _pendingTallyPreferenceWrites =
        new(StringComparer.OrdinalIgnoreCase);
    private int _tallyPreferenceFlushScheduled;
    private DateTime _tallyExportInteractionHoldUntilUtc;
    private bool _loadingExportPreferences;
    private int _autoImportProgressRefreshVersion;
    private int _autoImportPanelRefreshVersion;
    private bool _autoImportProgressObservedActive;
    private string _immediateDashboardClientKey = string.Empty;
    private bool _disposed;

    public DashboardViewModel(
        ActiveClientContext clientContext,
        IImportActivityRepository activityRepository,
        ImportActivityContext activityContext,
        TallyLiveConnectionCoordinator tallyCoordinator,
        AccountingRefreshCoordinator accountingRefreshCoordinator,
        IBankingRepository bankingRepository,
        IPurchaseRecordRepository purchaseRepository,
        ISalesRecordRepository salesRepository,
        IJournalRepository journalRepository,
        IGstRepository gstRepository,
        IMasterRepository masterRepository,
        IStockMasterRepository stockMasterRepository,
        IActionRequiredRepository actionRequiredRepository,
        SqliteTallyRepository tallyRepository,
        TallyExportSelectionContext tallyExportSelectionContext,
        BackgroundAutomationService automationService,
        IClientPreferenceRepository clientPreferences,
        ClientWorkspaceStateCache workspaceStateCache,
        AccountingLiveChangeHub liveChangeHub,
        ApplicationPreferenceService applicationPreferences,
        BankLedgerMappingService ledgerMappingService,
        SqliteGovernanceRepository governanceRepository,
        bool selectAllExportMonthsDefault = true)
        : base(
            "Dashboard",
            "Detailed selected-client workspace overview")
    {
        _clientContext = clientContext;
        _tallyAutoImportPreferences.Initialize();
        _activityRepository = activityRepository;
        _activityContext = activityContext;
        _tallyCoordinator = tallyCoordinator;
        _accountingRefreshCoordinator = accountingRefreshCoordinator;
        _bankingRepository = bankingRepository;
        _purchaseRepository = purchaseRepository;
        _salesRepository = salesRepository;
        _journalRepository = journalRepository;
        _gstRepository = gstRepository;
        _masterRepository = masterRepository;
        _stockMasterRepository = stockMasterRepository;
        _actionRequiredRepository = actionRequiredRepository;
        _tallyRepository = tallyRepository;
        _tallyExportSelectionContext = tallyExportSelectionContext;
        _automationService = automationService;
        _clientPreferences = clientPreferences;
        _workspaceStateCache = workspaceStateCache;
        _liveChangeHub = liveChangeHub;
        _applicationPreferences = applicationPreferences;
        _governanceRepository = governanceRepository;
        _diagnosticExportService = new ClientDiagnosticExportService(
            bankingRepository, purchaseRepository, salesRepository, journalRepository, masterRepository, stockMasterRepository,
            ledgerMappingService, automationService, tallyRepository);
        _selectAllExportMonths = selectAllExportMonthsDefault;

        BankCard = new DashboardImportCard("Bank Statement", "Banking", "B");
        PurchaseCard = new DashboardImportCard("Purchase", "Purchase", "P");
        SalesCard = new DashboardImportCard("Sales", "Sales", "S");

        TallyReportOptions =
        [
            "Company Overview",
            "Master Statistics",
            "Voucher Statistics",
            "Duplicate Control"
        ];
        foreach (var report in TallyReportCatalog.DisplayNames.Where(report =>
                     !report.Equals("Statistics", StringComparison.OrdinalIgnoreCase)))
        {
            TallyReportOptions.Add(report);
        }

        RefreshCommand = new RelayCommand(Refresh);
        SaveTallyAutoLifecycleCommand = new RelayCommand(() => { _ = SaveTallyAutoLifecycle(); });
        ClearAllDataCommand = new RelayCommand(ClearAllData, () => _clientContext.CurrentClient is not null);
        AutoImportFromFolderCommand = new RelayCommand(ManualAutoImportFromFolder, () => _clientContext.CurrentClient is not null);
        ExportDiagnosticExcelCommand = new RelayCommand(ExportDiagnosticExcel, () => _clientContext.CurrentClient is not null);
        ExportDiagnosticCsvCommand = new RelayCommand(ExportDiagnosticCsv, () => _clientContext.CurrentClient is not null);
        OpenLinkedFolderCommand = new RelayCommand(OpenLinkedFolder, CanOpenLinkedFolder);
        ImportReviewExcelCommand = new RelayCommand(ImportReviewExcel, () => _clientContext.CurrentClient is not null);
        ConfirmTallyLinkCommand = new AsyncRelayCommand(
            ConfirmTallyLinkAsync,
            () => CanConfirmTallyLink);
        CancelTallySelectionCommand = new RelayCommand(
            CancelTallySelection,
            () => CanCancelTallySelection);
        RequestUnlinkTallyCommand = new RelayCommand(
            RequestUnlinkTally,
            () => CanUnlinkTallyLink && !UnlinkConfirmationPending);
        ConfirmUnlinkTallyCommand = new RelayCommand(
            ConfirmUnlinkTally,
            () => UnlinkConfirmationPending && !_tallyCoordinator.IsBusy);
        CancelUnlinkTallyCommand = new RelayCommand(
            CancelUnlinkTally,
            () => UnlinkConfirmationPending);
        RefreshTallyCommand = new AsyncRelayCommand(
            RefreshTallyAsync,
            () => !_tallyCoordinator.IsBusy);
        RefreshSelectedTallyReportCommand = new AsyncRelayCommand(
            RefreshSelectedTallyReportAsync,
            CanRefreshLiveTallyReport);
        RefreshTallyStatisticsCommand = new AsyncRelayCommand(
            RefreshTallyStatisticsAsync,
            CanRefreshTallyStatistics);
        ExportSelectedToTallyCommand = new AsyncRelayCommand(
            ExportSelectedToTallyAsync,
            CanExportSelectedToTally);
        OpenTallyDiagnosticFolderCommand = new RelayCommand(
            OpenTallyDiagnosticFolder,
            () => !string.IsNullOrWhiteSpace(_tallyCoordinator.LastDiagnosticReportPath) &&
                  (File.Exists(_tallyCoordinator.LastDiagnosticReportPath) ||
                   Directory.Exists(_tallyCoordinator.LastDiagnosticReportPath)));

        // Retained for compatibility with the earlier Bank-only command. The
        // visible dashboard now uses the unified Tally Export card.
        ImportBankRangeCommand = new AsyncRelayCommand(
            ImportBankRangeAsync,
            () => CanImportBankRange);

        _clientContext.PropertyChanged += OnClientContextChanged;
        _activityContext.Changed += OnImportActivityChanged;
        _tallyCoordinator.Changed += OnTallyCoordinatorChanged;
        _tallyCoordinator.AttachOutboundAutomaticWorkProvider(_ =>
            Volatile.Read(ref _autoTallySyncPendingMask) != 0 ||
            Volatile.Read(ref _autoTallySyncInFlight) != 0);
        _automationService.ProgressChanged += OnAutomationProgressChanged;
        _automationService.ClientReviewWorkbookChanged += OnClientReviewWorkbookChanged;
        _workspaceStateCache.SnapshotReady += OnWorkspaceSnapshotReady;
        _liveChangeHub.Changed += OnAccountingLiveChanged;
        TallyMetrics.CollectionChanged += OnTallyDisplayCollectionChanged;
        TallyReportRows.CollectionChanged += OnTallyDisplayCollectionChanged;
        TallyStatisticsMetrics.CollectionChanged += OnTallyDisplayCollectionChanged;
        BankTallyExportMonths.CollectionChanged += OnTallyExportCollectionChanged;
        PurchaseTallyExportMonths.CollectionChanged += OnTallyExportCollectionChanged;
        SalesTallyExportMonths.CollectionChanged += OnTallyExportCollectionChanged;
        CashTallyExportMonths.CollectionChanged += OnTallyExportCollectionChanged;
        JournalTallyExportMonths.CollectionChanged += OnTallyExportCollectionChanged;

        Refresh();
    }

    public DashboardImportCard BankCard { get; }
    public DashboardImportCard PurchaseCard { get; }
    public DashboardImportCard SalesCard { get; }

    public ObservableCollection<DashboardRecentImport> RecentImports { get; } = [];
    public ObservableCollection<DashboardAutoImportFileRow> RecentAutoImportFiles { get; } = [];
    public ObservableCollection<DashboardAccountingMetric> AccountingSummaryMetrics { get; } = [];
    public ObservableCollection<DashboardAccountingMetric> AutoImportSummaryMetrics { get; } = [];
    public ObservableCollection<DashboardGstr3bMonthTile> Gstr3bMonthTiles { get; } = [];
    public ObservableCollection<string> TallyReportOptions { get; }
    public ObservableCollection<TallyDashboardMetric> TallyMetrics { get; } = [];
    public ObservableCollection<TallyReportRow> TallyReportRows { get; } = [];
    public ObservableCollection<TallyDashboardMetric> TallyStatisticsMetrics { get; } = [];
    public ObservableCollection<TallyExportMonthOption> BankTallyExportMonths { get; } = [];
    public ObservableCollection<TallyExportMonthOption> PurchaseTallyExportMonths { get; } = [];
    public ObservableCollection<TallyExportMonthOption> SalesTallyExportMonths { get; } = [];
    public ObservableCollection<TallyExportMonthOption> CashTallyExportMonths { get; } = [];
    public ObservableCollection<TallyExportMonthOption> JournalTallyExportMonths { get; } = [];
    public ObservableCollection<TallyExportModuleStatus> TallyExportModuleStatuses { get; } = [];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveTallyAutoLifecycleCommand { get; }
    public RelayCommand ClearAllDataCommand { get; }
    public RelayCommand AutoImportFromFolderCommand { get; }
    public RelayCommand ExportDiagnosticExcelCommand { get; }
    public RelayCommand ExportDiagnosticCsvCommand { get; }
    public RelayCommand OpenLinkedFolderCommand { get; }
    public RelayCommand ImportReviewExcelCommand { get; }
    public AsyncRelayCommand ConfirmTallyLinkCommand { get; }
    public RelayCommand CancelTallySelectionCommand { get; }
    public RelayCommand RequestUnlinkTallyCommand { get; }
    public RelayCommand ConfirmUnlinkTallyCommand { get; }
    public RelayCommand CancelUnlinkTallyCommand { get; }
    public AsyncRelayCommand RefreshTallyCommand { get; }
    public AsyncRelayCommand RefreshSelectedTallyReportCommand { get; }
    public AsyncRelayCommand RefreshTallyStatisticsCommand { get; }
    public AsyncRelayCommand ExportSelectedToTallyCommand { get; }
    public RelayCommand OpenTallyDiagnosticFolderCommand { get; }
    public AsyncRelayCommand ImportBankRangeCommand { get; }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string ClientCode
    {
        get => _clientCode;
        private set => SetProperty(ref _clientCode, value);
    }

    public string ClientName
    {
        get => _clientName;
        private set => SetProperty(ref _clientName, value);
    }

    public string TradeName
    {
        get => _tradeName;
        private set => SetProperty(ref _tradeName, value);
    }

    public string LegalName
    {
        get => _legalName;
        private set => SetProperty(ref _legalName, value);
    }

    public string Gstin
    {
        get => _gstin;
        private set => SetProperty(ref _gstin, value);
    }

    // Client Identity shows every registration, never just the primary GSTIN.
    // This reflects the active Client/FY's saved ownership list and deliberately
    // does not create a second GSTIN management or accounting import route.
    private IReadOnlyList<DashboardGstinRegistration> _clientGstinRegistrations = [];
    public IReadOnlyList<DashboardGstinRegistration> ClientGstinRegistrations
    {
        get => _clientGstinRegistrations;
        private set => SetProperty(ref _clientGstinRegistrations, value);
    }

    private IReadOnlyList<ClientBranchRecord> _clientBranches = [];
    public IReadOnlyList<ClientBranchRecord> ClientBranches
    {
        get => _clientBranches;
        private set
        {
            if (!SetProperty(ref _clientBranches, value)) return;
            OnPropertyChanged(nameof(HasBranches));
            OnPropertyChanged(nameof(BranchCount));
            OnPropertyChanged(nameof(ShowUnassignedPrimaryGstin));
        }
    }
    public bool HasBranches => ClientBranches.Count > 0;
    public int BranchCount => ClientBranches.Count;
    public bool ShowUnassignedPrimaryGstin =>
        HasBranches && Gstin.Length == 15 &&
        !ClientBranches.Any(x => x.Gstin.Equals(Gstin, StringComparison.OrdinalIgnoreCase));

    public string Pan
    {
        get => _pan;
        private set => SetProperty(ref _pan, value);
    }

    public string State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string FinancialYear
    {
        get => _financialYear;
        private set => SetProperty(ref _financialYear, value);
    }

    public long ActiveClientId => _clientContext.CurrentClient?.Id ?? 0;
    public string ActiveClientFinancialYear => _clientContext.CurrentClient?.FinancialYear ?? string.Empty;
    public bool CanUseUniversalDump => ActiveClientId > 0;

    public UniversalDumpImportResult StageUniversalDump(IEnumerable<string> sources)
    {
        var client = _clientContext.CurrentClient ?? throw new InvalidOperationException("Open a client before using Universal Dump.");
        return _automationService.StageUniversalDump(client.Id, client.FinancialYear, sources);
    }

    public string ContactText
    {
        get => _contactText;
        private set => SetProperty(ref _contactText, value);
    }

    public string AddressText
    {
        get => _addressText;
        private set => SetProperty(ref _addressText, value);
    }

    public string LastActivityText
    {
        get => _lastActivityText;
        private set => SetProperty(ref _lastActivityText, value);
    }

    public string WorkspaceSummary
    {
        get => _workspaceSummary;
        private set => SetProperty(ref _workspaceSummary, value);
    }

    public string AutoImportFolderStatus
    {
        get => _autoImportFolderStatus;
        private set => SetProperty(ref _autoImportFolderStatus, value);
    }

    public string AutoImportSummary
    {
        get => _autoImportSummary;
        private set => SetProperty(ref _autoImportSummary, value);
    }

    public string AutoImportModules
    {
        get => _autoImportModules;
        private set => SetProperty(ref _autoImportModules, value);
    }

    public string AutoImportHealthState
    {
        get => _autoImportHealthState;
        private set => SetProperty(ref _autoImportHealthState, value);
    }

    public int AutoImportErrorCount
    {
        get => _autoImportErrorCount;
        private set => SetProperty(ref _autoImportErrorCount, value);
    }

    public double AutoImportProgressPercent
    {
        get => _autoImportProgressPercent;
        private set => SetProperty(ref _autoImportProgressPercent, value);
    }

    public bool IsAutoImportActive
    {
        get => _isAutoImportActive;
        private set => SetProperty(ref _isAutoImportActive, value);
    }

    public int AutoImportPendingFiles
    {
        get => _autoImportPendingFiles;
        private set => SetProperty(ref _autoImportPendingFiles, value);
    }

    public int AutoImportTransactionCount
    {
        get => _autoImportTransactionCount;
        private set => SetProperty(ref _autoImportTransactionCount, value);
    }

    public int AutoImportSuccessfulFiles
    {
        get => _autoImportSuccessfulFiles;
        private set => SetProperty(ref _autoImportSuccessfulFiles, value);
    }

    public int AutoImportActiveFiles
    {
        get => _autoImportActiveFiles;
        private set => SetProperty(ref _autoImportActiveFiles, value);
    }

    public string DiagnosticExportStatus
    {
        get => _diagnosticExportStatus;
        private set => SetProperty(ref _diagnosticExportStatus, value);
    }

    public int AvailableDataModules
    {
        get => _availableDataModules;
        private set => SetProperty(ref _availableDataModules, value);
    }

    public int TotalRecords
    {
        get => _totalRecords;
        private set => SetProperty(ref _totalRecords, value);
    }

    public bool HasAnyData
    {
        get => _hasAnyData;
        private set => SetProperty(ref _hasAnyData, value);
    }

    public bool HasBankData => BankCard.IsImported;
    public bool HasPurchaseData => PurchaseCard.IsImported;
    public bool HasSalesData => SalesCard.IsImported;
    public bool HasRecentImports => RecentImports.Count > 0;
    public bool HasValidGstin => TallyCompanyInfo.NormalizeGstin(
        _clientContext.CurrentClient?.Gstin ?? string.Empty).Length == 15;
    public bool HasBankExportData => BankTallyExportMonths.Any(option => option.SourceCount > 0);
    public bool HasPurchaseExportData => PurchaseTallyExportMonths.Any(option => option.SourceCount > 0);
    public bool HasSalesExportData => SalesTallyExportMonths.Any(option => option.SourceCount > 0);
    public bool HasCashExportData => CashTallyExportMonths.Any(option => option.SourceCount > 0);
    public bool HasJournalExportData => JournalTallyExportMonths.Any(option => option.SourceCount > 0);
    public bool HasAnyTallyExportData => HasBankExportData || HasPurchaseExportData || HasSalesExportData ||
                                         HasCashExportData || HasJournalExportData;
    public bool HasTallyMetrics => TallyMetrics.Count > 0;
    public bool HasTallyReportRows => TallyReportRows.Count > 0;
    public bool HasTallyStatisticsMetrics => TallyStatisticsMetrics.Count > 0;

    public string TallyConnectionStatus
    {
        get => _tallyConnectionStatus;
        private set => SetProperty(ref _tallyConnectionStatus, value);
    }

    public string TallyLiveXmlStatus
    {
        get => _tallyLiveXmlStatus;
        private set => SetProperty(ref _tallyLiveXmlStatus, value);
    }

    public string TallyLinkStatus
    {
        get => _tallyLinkStatus;
        private set => SetProperty(ref _tallyLinkStatus, value);
    }

    public string TallyCompanyName
    {
        get => _tallyCompanyName;
        private set => SetProperty(ref _tallyCompanyName, value);
    }

    public string TallyCompanyGstin
    {
        get => _tallyCompanyGstin;
        private set => SetProperty(ref _tallyCompanyGstin, value);
    }

    public string TallyCompanyFinancialYear
    {
        get => _tallyCompanyFinancialYear;
        private set => SetProperty(ref _tallyCompanyFinancialYear, value);
    }

    public string TallyCompanyState
    {
        get => _tallyCompanyState;
        private set => SetProperty(ref _tallyCompanyState, value);
    }

    public string TallyCompanyBooksPeriod
    {
        get => _tallyCompanyBooksPeriod;
        private set => SetProperty(ref _tallyCompanyBooksPeriod, value);
    }

    public string TallyCompanyNumber
    {
        get => _tallyCompanyNumber;
        private set => SetProperty(ref _tallyCompanyNumber, value);
    }

    public string TallyIdentityMatchText
    {
        get => _tallyIdentityMatchText;
        private set => SetProperty(ref _tallyIdentityMatchText, value);
    }

    public string TallyLastSyncText
    {
        get => _tallyLastSyncText;
        private set => SetProperty(ref _tallyLastSyncText, value);
    }

    public string TallyLastError
    {
        get => _tallyLastError;
        private set
        {
            if (SetProperty(ref _tallyLastError, value))
            {
                OnPropertyChanged(nameof(HasTallyError));
            }
        }
    }

    public bool HasTallyError => TallyLastError.Length > 0;

    public string TallyActionStatus
    {
        get => _tallyActionStatus;
        private set => SetProperty(ref _tallyActionStatus, value);
    }

    public string SelectedTallyReport
    {
        get => _selectedTallyReport;
        set
        {
            if (!SetProperty(ref _selectedTallyReport, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsLocalTallyReport));
            OnPropertyChanged(nameof(IsLiveTallyReport));
            RefreshTallyMetrics();
            if (IsLiveTallyReport)
            {
                _ = RefreshSelectedTallyReportAsync();
            }
            else
            {
                TallyReportRows.Clear();
                TallyReportStatus = "Showing synchronized application statistics for the linked Tally company.";
            }
            RefreshTallyCommandStates();
        }
    }

    public bool IsLocalTallyReport => LocalTallyReports.Contains(SelectedTallyReport);
    public bool IsLiveTallyReport => !IsLocalTallyReport;

    public string TallyReportStatus
    {
        get => _tallyReportStatus;
        private set => SetProperty(ref _tallyReportStatus, value);
    }

    public bool IsErp9CoreLiveMode => _tallyCoordinator.IsErp9CompatibilityMode;
    public bool AreSupportingTallyReportsEnabled => true;

    public string TallyStatisticsStatus
    {
        get => _tallyStatisticsStatus;
        private set => SetProperty(ref _tallyStatisticsStatus, value);
    }

    public string TallyExportStatus
    {
        get => _tallyExportStatus;
        private set
        {
            if (SetProperty(ref _tallyExportStatus, value))
                RefreshTallyExportSummaryState();
        }
    }

    public string TallyStatusState
    {
        get => _tallyStatusState;
        private set => SetProperty(ref _tallyStatusState, value);
    }

    public int TallyReadyVoucherCount => _tallyCoordinator.LastLiveXmlReadyVouchers;
    public int TallyNewMasterCreateCount => _tallyCoordinator.LastLiveXmlNewMasters;

    public string TallyExportHealthState
    {
        get => _tallyExportHealthState;
        private set => SetProperty(ref _tallyExportHealthState, value);
    }

    public int TallyExportTotalTransactions
    {
        get => _tallyExportTotalTransactions;
        private set => SetProperty(ref _tallyExportTotalTransactions, value);
    }

    public string TallyAutoSyncMode
    {
        get => _tallyAutoSyncMode;
        set
        {
            var normalized = value.Equals("Full Auto", StringComparison.OrdinalIgnoreCase) ? "Full Auto" :
                value.Equals("Selected", StringComparison.OrdinalIgnoreCase) ? "Selected" : "Off";
            if (!SetProperty(ref _tallyAutoSyncMode, normalized)) return;
            OnPropertyChanged(nameof(IsTallyAutoSyncOff));
            OnPropertyChanged(nameof(IsTallyAutoSyncEnabled));
            OnPropertyChanged(nameof(IsTallySessionAutomationVisible));
            OnPropertyChanged(nameof(IsTallyAutoSyncSelected));
            OnPropertyChanged(nameof(IsTallyAutoSyncFull));
            PersistAutoSyncPreference("Mode", normalized);
            if (normalized.Equals("Off", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Exchange(ref _autoTallySyncPendingMask, 0);
                Interlocked.Exchange(ref _autoTallySyncChangedMask, 0);
                Interlocked.Exchange(ref _autoTallySyncExplicitSelectionMask, 0);
                Interlocked.Exchange(ref _autoTallyRecreateContinuation, 0);
                if (_clientContext.CurrentClient is { } offClient)
                    _tallyCoordinator.SetOutboundAutoSyncEnabled(offClient, false);
                IsTallyAutoSyncSettingsOpen = false;
                TallyAutoSyncStatus = "Automatic Tally Sync is off. Manual Tally Export remains available.";
            }
            else
            {
                _tallyAutoSyncActiveMode = normalized;
                PersistAutoSyncPreference("ActiveMode", normalized);
                if (_clientContext.CurrentClient is { } onClient)
                    _tallyCoordinator.SetOutboundAutoSyncEnabled(onClient, true);
                TallyAutoSyncStatus = $"Automatic Tally Sync {normalized} enabled. Selecting existing eligible entries using the visible module/period filters.";
                // An existing backlog is not a live-change event. Enabling outbound
                // Auto Sync must explicitly review it through the SAME export exit.
                SyncManualTileSelectionFromAutoSettings();
                QueueExistingEligibleTallyExport();
            }
        }
    }

    public bool IsTallyAutoSyncOff { get => TallyAutoSyncMode.Equals("Off", StringComparison.OrdinalIgnoreCase); set { if (value) TallyAutoSyncMode = "Off"; } }
    public bool IsTallyAutoSyncEnabled
    {
        get => !IsTallyAutoSyncOff;
        set
        {
            if (value == IsTallyAutoSyncEnabled) return;
            if (value)
                TallyAutoSyncMode = _tallyAutoSyncActiveMode.Equals("Full Auto", StringComparison.OrdinalIgnoreCase) ? "Full Auto" : "Selected";
            else
                TallyAutoSyncMode = "Off";
        }
    }
    public bool IsTallyAutoSyncSelected { get => TallyAutoSyncMode.Equals("Selected", StringComparison.OrdinalIgnoreCase); set { if (value) TallyAutoSyncMode = "Selected"; } }
    public bool IsTallyAutoSyncFull { get => TallyAutoSyncMode.Equals("Full Auto", StringComparison.OrdinalIgnoreCase); set { if (value) TallyAutoSyncMode = "Full Auto"; } }

    public string TallyAutoSyncStatus { get => _tallyAutoSyncStatus; private set => SetProperty(ref _tallyAutoSyncStatus, value); }
    public bool IsTallyAutoSyncSettingsOpen
    {
        get => _isTallyAutoSyncSettingsOpen;
        set => SetProperty(ref _isTallyAutoSyncSettingsOpen, value && IsTallyAutoSyncEnabled);
    }

    // Session automation belongs exclusively to outbound Automatic Tally Sync.
    // The independent Tally -> AAH 5-second import cannot authorize launches or closes.
    public bool IsTallySessionAutomationVisible => IsTallyAutoSyncEnabled;
    public bool TallyAutoImportEnabled
    {
        get => _tallyAutoImportEnabled;
        set { if (SetProperty(ref _tallyAutoImportEnabled, value)) OnPropertyChanged(nameof(IsTallySessionAutomationVisible)); }
    }
    public bool TallyAutoOpen
    {
        get => _tallyAutoOpen;
        set
        {
            var old = _tallyAutoOpen;
            if (!SetProperty(ref _tallyAutoOpen, value) || _loadingTallyAutoLifecycle) return;
            if (!SaveTallyAutoLifecycle()) SetProperty(ref _tallyAutoOpen, old);
        }
    }
    public bool TallyAutoClose
    {
        get => _tallyAutoClose;
        set
        {
            var old = _tallyAutoClose;
            if (!SetProperty(ref _tallyAutoClose, value) || _loadingTallyAutoLifecycle) return;
            if (!SaveTallyAutoLifecycle()) SetProperty(ref _tallyAutoClose, old);
        }
    }
    public string TallyExactCompanyFolder { get => _tallyExactCompanyFolder; set => SetProperty(ref _tallyExactCompanyFolder, value); }
    public string TallyAutoLifecycleStatus { get => _tallyAutoLifecycleStatus; private set => SetProperty(ref _tallyAutoLifecycleStatus, value); }

    private void LoadTallyAutoLifecycle(ClientProfile? client)
    {
        _loadingTallyAutoLifecycle = true;
        try
        {
        if (client is null)
        {
            TallyAutoImportEnabled = false;
            TallyAutoOpen = false;
            TallyAutoClose = false;
            TallyExactCompanyFolder = string.Empty;
            TallyAutoLifecycleStatus = "Open a Client/FY before configuring Auto Open or Auto Close.";
            return;
        }
        TallyAutoImportEnabled = AutoTallyImportPreference.IsEnabled(_tallyAutoImportPreferences, client);
        TallyAutoOpen = AutoTallyImportPreference.AutoOpen(_tallyAutoImportPreferences, client);
        TallyAutoClose = AutoTallyImportPreference.AutoClose(_tallyAutoImportPreferences, client);
        TallyExactCompanyFolder = AutoTallyImportPreference.ExactCompanyFolder(_tallyAutoImportPreferences, client);
        var link = _tallyRepository.GetLinkForClient(client.Id);
        TallyAutoLifecycleStatus = link is null ||
            (link.FinancialYear.Length > 0 && !link.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            ? "No confirmed Tally link for this Client/FY. Use the existing Link control first."
            : TallyExactCompanyFolder.Length == 0
                ? "Linked to " + link.CompanyName + ". The saved Tally link identifies the company but does not contain a verified disk location. Configure the exact folder once in Settings > Auto Tally Import; it will then be reused automatically."
                : "Linked to " + link.CompanyName + ". Reusing previously saved company folder: " + TallyExactCompanyFolder;
        }
        finally { _loadingTallyAutoLifecycle = false; }
    }

    private bool SaveTallyAutoLifecycle()
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            TallyAutoLifecycleStatus = "No active Client/FY; nothing saved.";
            return false;
        }
        try
        {
            var link = _tallyRepository.GetLinkForClient(client.Id);
            // Validate before any preference is written; no partial changes on failure.
            if (TallyAutoOpen)
            {
                if (link is null ||
                    (link.FinancialYear.Length > 0 && !link.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)) ||
                    string.IsNullOrWhiteSpace(link.CompanyKey))
                {
                    TallyAutoLifecycleStatus = "Auto Open not saved: confirm the correct Tally company using Link first.";
                    return false;
                }
                if (!IsTallyAutoSyncEnabled)
                {
                    TallyAutoLifecycleStatus = "Enable Automatic Tally Sync before Auto Open.";
                    return false;
                }
                var savedPath = AutoTallyImportPreference.ExactCompanyFolder(_tallyAutoImportPreferences, client);
                var savedKey = AutoTallyImportPreference.ExactCompanyKey(_tallyAutoImportPreferences, client);
                if (string.IsNullOrWhiteSpace(savedKey) ||
                    !savedKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase))
                {
                    TallyAutoLifecycleStatus = "The exact company folder is not bound to the current linked company. Revalidate it once in Settings > Auto Tally Import; no company will be guessed from its number.";
                    return false;
                }
                TallyPrivateCompanySessionService.VerifyCompanyFolder(savedPath, link.CompanyNumber);
            }
            // Existing Auto Import and exact-folder ownership are managed only
            // by the pre-existing Settings page. Never silently rewrite either here.
            AutoTallyImportPreference.SetAutoOpen(_tallyAutoImportPreferences, client.Id, client.FinancialYear, TallyAutoOpen);
            AutoTallyImportPreference.SetAutoClose(_tallyAutoImportPreferences, client.Id, client.FinancialYear, TallyAutoClose);
            TallyAutoLifecycleStatus = "Saved automatically for " + client.ClientName + " (" + client.FinancialYear + "). " +
                "These options apply only while Automatic Tally Sync is ON. Auto Open " + (TallyAutoOpen ? "ON" : "OFF") + ", Auto Close " + (TallyAutoClose ? "ON" : "OFF") +
                ". If Tally is already running with another company, a second session is prohibited.";
            return true;
        }
        catch (Exception ex)
        {
            TallyAutoLifecycleStatus = "Settings NOT saved: " + ex.Message;
            return false;
        }
    }

    public bool AutoSyncBank { get => _autoSyncBank; set { if (SetProperty(ref _autoSyncBank, value)) { PersistAutoSyncPreference("Bank", value); ApplyAutoSettingToManualTile("Bank", value); RefreshAutoSyncTileIndicators(); } } }
    public bool AutoSyncBankImportSuspense
    {
        get => _autoSyncBankImportSuspense;
        set
        {
            if (!SetProperty(ref _autoSyncBankImportSuspense, value)) return;
            PersistAutoSyncPreference("BankImportSuspense", value);
            if (value && IsTallyAutoSyncEnabled && AutoSyncModuleEnabled(AutoSyncBankMask))
            {
                // Enabling Suspense changes what the automatic Bank exporter may
                // write. This explicit user intent must survive even when the UI's
                // already-imported ReadyCount is zero.
                Interlocked.Or(ref _autoTallySyncChangedMask, AutoSyncBankMask);
                Interlocked.Or(ref _autoTallySyncPendingMask, AutoSyncBankMask);
                ScheduleTallyAutoSync();
            }
        }
    }
    public bool AutoSyncPurchase { get => _autoSyncPurchase; set { if (SetProperty(ref _autoSyncPurchase, value)) { PersistAutoSyncPreference("Purchase", value); ApplyAutoSettingToManualTile("Purchase", value); RefreshAutoSyncTileIndicators(); } } }
    public bool AutoSyncSales { get => _autoSyncSales; set { if (SetProperty(ref _autoSyncSales, value)) { PersistAutoSyncPreference("Sales", value); ApplyAutoSettingToManualTile("Sales", value); RefreshAutoSyncTileIndicators(); } } }
    public bool AutoSyncCash { get => _autoSyncCash; set { if (SetProperty(ref _autoSyncCash, value)) { PersistAutoSyncPreference("Cash", value); ApplyAutoSettingToManualTile("Cash", value); RefreshAutoSyncTileIndicators(); } } }
    public bool AutoSyncJournal { get => _autoSyncJournal; set { if (SetProperty(ref _autoSyncJournal, value)) { PersistAutoSyncPreference("Journal", value); ApplyAutoSettingToManualTile("Journal", value); RefreshAutoSyncTileIndicators(); } } }
    public bool AutoSyncRecurring { get => _autoSyncRecurring; set { if (SetProperty(ref _autoSyncRecurring, value)) PersistAutoSyncPreference("Recurring", value); } }
    public bool AutoSyncMasterChanges { get => _autoSyncMasterChanges; set { if (SetProperty(ref _autoSyncMasterChanges, value)) PersistAutoSyncPreference("Masters", value); } }
    public bool AutoSyncCreateMasters { get => _autoSyncCreateMasters; set { if (SetProperty(ref _autoSyncCreateMasters, value)) PersistAutoSyncPreference("CreateMasters", value); } }
    public bool AutoSyncCreateVouchers { get => _autoSyncCreateVouchers; set { if (SetProperty(ref _autoSyncCreateVouchers, value)) PersistAutoSyncPreference("CreateVouchers", value); } }
    public bool AutoSyncAlterMasters { get => _autoSyncAlterMasters; set { if (SetProperty(ref _autoSyncAlterMasters, value)) PersistAutoSyncPreference("AlterMasters", value); } }
    public bool AutoSyncAlterVouchers { get => _autoSyncAlterVouchers; set { if (SetProperty(ref _autoSyncAlterVouchers, value)) PersistAutoSyncPreference("AlterVouchers", value); } }
    public bool AutoSyncDelete { get => _autoSyncDelete; set { if (SetProperty(ref _autoSyncDelete, value)) PersistAutoSyncPreference("Delete", value); } }
    // Fix20: OFF by default; only confirmed DeletedInTally rows may be restored.
    // This is NOT Auto Delete and never broadens Manual Export permissions.
    public bool AutoSyncRecreate
    {
        get => _autoSyncRecreate;
        set
        {
            if (!SetProperty(ref _autoSyncRecreate, value)) return;
            PersistAutoSyncPreference("Recreate", value);
            if (value && IsTallyAutoSyncEnabled) QueueExistingEligibleTallyExport();
        }
    }

    public int TallyExportSelectedTransactions
    {
        get => _tallyExportSelectedTransactions;
        private set => SetProperty(ref _tallyExportSelectedTransactions, value);
    }

    public string TallyDiagnosticReportPath => _tallyCoordinator.LastDiagnosticReportPath;
    public bool HasTallyDiagnosticReport =>
        !string.IsNullOrWhiteSpace(TallyDiagnosticReportPath) &&
        (File.Exists(TallyDiagnosticReportPath) || Directory.Exists(TallyDiagnosticReportPath));

    public bool IsTallyReportBusy
    {
        get => _isTallyReportBusy;
        private set
        {
            if (SetProperty(ref _isTallyReportBusy, value))
            {
                RefreshTallyCommandStates();
            }
        }
    }

    public bool IsTallyStatisticsBusy
    {
        get => _isTallyStatisticsBusy;
        private set
        {
            if (SetProperty(ref _isTallyStatisticsBusy, value))
            {
                RefreshTallyCommandStates();
            }
        }
    }

    public string BankExportPeriodMode
    {
        get => _bankExportPeriodMode;
        set
        {
            if (SetProperty(ref _bankExportPeriodMode, value))
            {
                OnPropertyChanged(nameof(IsBankMonthExportMode));
                OnPropertyChanged(nameof(IsBankDateRangeExportMode));
                PersistTallyExportPreference("Bank", "Mode", value);
                PublishBankExportSelection();
                RefreshTallyCommandStates();
            }
        }
    }

    public bool IsBankMonthExportMode
    {
        get => BankExportPeriodMode.Equals("Month", StringComparison.OrdinalIgnoreCase);
        set { if (value) BankExportPeriodMode = "Month"; }
    }
    public bool IsBankDateRangeExportMode
    {
        get => !IsBankMonthExportMode;
        set { if (value) BankExportPeriodMode = "Date Range"; }
    }

    public string PurchaseExportPeriodMode
    {
        get => _purchaseExportPeriodMode;
        set
        {
            if (SetProperty(ref _purchaseExportPeriodMode, value))
            {
                OnPropertyChanged(nameof(IsPurchaseMonthExportMode));
                OnPropertyChanged(nameof(IsPurchaseDateRangeExportMode));
                PersistTallyExportPreference("Purchase", "Mode", value);
                RefreshTallyCommandStates();
            }
        }
    }

    public bool IsPurchaseMonthExportMode
    {
        get => PurchaseExportPeriodMode.Equals("Month", StringComparison.OrdinalIgnoreCase);
        set { if (value) PurchaseExportPeriodMode = "Month"; }
    }
    public bool IsPurchaseDateRangeExportMode
    {
        get => !IsPurchaseMonthExportMode;
        set { if (value) PurchaseExportPeriodMode = "Date Range"; }
    }

    public string SalesExportPeriodMode
    {
        get => _salesExportPeriodMode;
        set
        {
            if (SetProperty(ref _salesExportPeriodMode, value))
            {
                OnPropertyChanged(nameof(IsSalesMonthExportMode));
                OnPropertyChanged(nameof(IsSalesDateRangeExportMode));
                PersistTallyExportPreference("Sales", "Mode", value);
                RefreshTallyCommandStates();
            }
        }
    }

    public bool IsSalesMonthExportMode
    {
        get => SalesExportPeriodMode.Equals("Month", StringComparison.OrdinalIgnoreCase);
        set { if (value) SalesExportPeriodMode = "Month"; }
    }
    public bool IsSalesDateRangeExportMode
    {
        get => !IsSalesMonthExportMode;
        set { if (value) SalesExportPeriodMode = "Date Range"; }
    }

    public string CashExportPeriodMode
    {
        get => _cashExportPeriodMode;
        set
        {
            if (SetProperty(ref _cashExportPeriodMode, value))
            {
                OnPropertyChanged(nameof(IsCashMonthExportMode));
                OnPropertyChanged(nameof(IsCashDateRangeExportMode));
                PersistTallyExportPreference("Cash", "Mode", value);
                RefreshTallyCommandStates();
            }
        }
    }
    public bool IsCashMonthExportMode
    {
        get => CashExportPeriodMode.Equals("Month", StringComparison.OrdinalIgnoreCase);
        set { if (value) CashExportPeriodMode = "Month"; }
    }
    public bool IsCashDateRangeExportMode
    {
        get => !IsCashMonthExportMode;
        set { if (value) CashExportPeriodMode = "Date Range"; }
    }

    public string JournalExportPeriodMode
    {
        get => _journalExportPeriodMode;
        set
        {
            if (SetProperty(ref _journalExportPeriodMode, value))
            {
                OnPropertyChanged(nameof(IsJournalMonthExportMode));
                OnPropertyChanged(nameof(IsJournalDateRangeExportMode));
                PersistTallyExportPreference("Journal", "Mode", value);
                RefreshTallyCommandStates();
            }
        }
    }
    public bool IsJournalMonthExportMode
    {
        get => JournalExportPeriodMode.Equals("Month", StringComparison.OrdinalIgnoreCase);
        set { if (value) JournalExportPeriodMode = "Month"; }
    }
    public bool IsJournalDateRangeExportMode
    {
        get => !IsJournalMonthExportMode;
        set { if (value) JournalExportPeriodMode = "Date Range"; }
    }

    public void SetTallyExportPeriodMode(string module, string mode)
    {
        BeginTallyExportInteraction();
        var normalizedMode = mode.Equals("Date Range", StringComparison.OrdinalIgnoreCase)
            ? "Date Range"
            : "Month";
        switch ((module ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "BANK": BankExportPeriodMode = normalizedMode; break;
            case "PURCHASE": PurchaseExportPeriodMode = normalizedMode; break;
            case "SALES": SalesExportPeriodMode = normalizedMode; break;
            case "CASH": CashExportPeriodMode = normalizedMode; break;
            case "JOURNAL": JournalExportPeriodMode = normalizedMode; break;
        }
    }

    private void NotifyTallyExportPeriodModeBindings()
    {
        OnPropertyChanged(nameof(BankExportPeriodMode));
        OnPropertyChanged(nameof(IsBankMonthExportMode));
        OnPropertyChanged(nameof(IsBankDateRangeExportMode));
        OnPropertyChanged(nameof(PurchaseExportPeriodMode));
        OnPropertyChanged(nameof(IsPurchaseMonthExportMode));
        OnPropertyChanged(nameof(IsPurchaseDateRangeExportMode));
        OnPropertyChanged(nameof(SalesExportPeriodMode));
        OnPropertyChanged(nameof(IsSalesMonthExportMode));
        OnPropertyChanged(nameof(IsSalesDateRangeExportMode));
        OnPropertyChanged(nameof(CashExportPeriodMode));
        OnPropertyChanged(nameof(IsCashMonthExportMode));
        OnPropertyChanged(nameof(IsCashDateRangeExportMode));
        OnPropertyChanged(nameof(JournalExportPeriodMode));
        OnPropertyChanged(nameof(IsJournalMonthExportMode));
        OnPropertyChanged(nameof(IsJournalDateRangeExportMode));
    }

    public TallyExportMonthOption? SelectedBankExportMonth
    {
        get => _selectedBankExportMonth;
        set
        {
            if (SetProperty(ref _selectedBankExportMonth, value))
            {
                PublishBankExportSelection();
                RefreshTallyCommandStates();
            }
        }
    }

    public TallyExportMonthOption? SelectedPurchaseExportMonth
    {
        get => _selectedPurchaseExportMonth;
        set
        {
            if (SetProperty(ref _selectedPurchaseExportMonth, value))
            {
                RefreshTallyCommandStates();
            }
        }
    }

    public TallyExportMonthOption? SelectedSalesExportMonth
    {
        get => _selectedSalesExportMonth;
        set
        {
            if (SetProperty(ref _selectedSalesExportMonth, value))
            {
                RefreshTallyCommandStates();
            }
        }
    }

    public DateTime? BankExportFromDate
    {
        get => _bankExportFromDate;
        set
        {
            if (SetProperty(ref _bankExportFromDate, ClampExportDate(value, BankExportMinDate, BankExportMaxDate)))
            {
                OnPropertyChanged(nameof(BankTallyFromDate));
                PersistTallyExportPreference("Bank", "From", _bankExportFromDate);
                PublishBankExportSelection();
                RefreshTallyCommandStates();
            }
        }
    }

    public DateTime? BankExportToDate
    {
        get => _bankExportToDate;
        set
        {
            if (SetProperty(ref _bankExportToDate, ClampExportDate(value, BankExportMinDate, BankExportMaxDate)))
            {
                OnPropertyChanged(nameof(BankTallyToDate));
                PersistTallyExportPreference("Bank", "To", _bankExportToDate);
                PublishBankExportSelection();
                RefreshTallyCommandStates();
            }
        }
    }

    public DateTime? PurchaseExportFromDate
    {
        get => _purchaseExportFromDate;
        set
        {
            if (SetProperty(ref _purchaseExportFromDate, ClampExportDate(value, PurchaseExportMinDate, PurchaseExportMaxDate)))
            {
                PersistTallyExportPreference("Purchase", "From", _purchaseExportFromDate);
                RefreshTallyCommandStates();
            }
        }
    }

    public DateTime? PurchaseExportToDate
    {
        get => _purchaseExportToDate;
        set
        {
            if (SetProperty(ref _purchaseExportToDate, ClampExportDate(value, PurchaseExportMinDate, PurchaseExportMaxDate)))
            {
                PersistTallyExportPreference("Purchase", "To", _purchaseExportToDate);
                RefreshTallyCommandStates();
            }
        }
    }

    public DateTime? SalesExportFromDate
    {
        get => _salesExportFromDate;
        set
        {
            if (SetProperty(ref _salesExportFromDate, ClampExportDate(value, SalesExportMinDate, SalesExportMaxDate)))
            {
                PersistTallyExportPreference("Sales", "From", _salesExportFromDate);
                RefreshTallyCommandStates();
            }
        }
    }

    public DateTime? SalesExportToDate
    {
        get => _salesExportToDate;
        set
        {
            if (SetProperty(ref _salesExportToDate, ClampExportDate(value, SalesExportMinDate, SalesExportMaxDate)))
            {
                PersistTallyExportPreference("Sales", "To", _salesExportToDate);
                RefreshTallyCommandStates();
            }
        }
    }

    public DateTime? CashExportFromDate
    {
        get => _cashExportFromDate;
        set { if (SetProperty(ref _cashExportFromDate, ClampExportDate(value, CashExportMinDate, CashExportMaxDate))) { PersistTallyExportPreference("Cash", "From", _cashExportFromDate); RefreshTallyCommandStates(); } }
    }
    public DateTime? CashExportToDate
    {
        get => _cashExportToDate;
        set { if (SetProperty(ref _cashExportToDate, ClampExportDate(value, CashExportMinDate, CashExportMaxDate))) { PersistTallyExportPreference("Cash", "To", _cashExportToDate); RefreshTallyCommandStates(); } }
    }
    public DateTime? JournalExportFromDate
    {
        get => _journalExportFromDate;
        set { if (SetProperty(ref _journalExportFromDate, ClampExportDate(value, JournalExportMinDate, JournalExportMaxDate))) { PersistTallyExportPreference("Journal", "From", _journalExportFromDate); RefreshTallyCommandStates(); } }
    }
    public DateTime? JournalExportToDate
    {
        get => _journalExportToDate;
        set { if (SetProperty(ref _journalExportToDate, ClampExportDate(value, JournalExportMinDate, JournalExportMaxDate))) { PersistTallyExportPreference("Journal", "To", _journalExportToDate); RefreshTallyCommandStates(); } }
    }

    public DateTime? BankExportMinDate
    {
        get => _bankExportMinDate;
        private set => SetProperty(ref _bankExportMinDate, value);
    }
    public DateTime? BankExportMaxDate
    {
        get => _bankExportMaxDate;
        private set => SetProperty(ref _bankExportMaxDate, value);
    }
    public DateTime? PurchaseExportMinDate
    {
        get => _purchaseExportMinDate;
        private set => SetProperty(ref _purchaseExportMinDate, value);
    }
    public DateTime? PurchaseExportMaxDate
    {
        get => _purchaseExportMaxDate;
        private set => SetProperty(ref _purchaseExportMaxDate, value);
    }
    public DateTime? SalesExportMinDate
    {
        get => _salesExportMinDate;
        private set => SetProperty(ref _salesExportMinDate, value);
    }
    public DateTime? SalesExportMaxDate
    {
        get => _salesExportMaxDate;
        private set => SetProperty(ref _salesExportMaxDate, value);
    }
    public DateTime? CashExportMinDate
    {
        get => _cashExportMinDate;
        private set => SetProperty(ref _cashExportMinDate, value);
    }
    public DateTime? CashExportMaxDate
    {
        get => _cashExportMaxDate;
        private set => SetProperty(ref _cashExportMaxDate, value);
    }
    public DateTime? JournalExportMinDate
    {
        get => _journalExportMinDate;
        private set => SetProperty(ref _journalExportMinDate, value);
    }
    public DateTime? JournalExportMaxDate
    {
        get => _journalExportMaxDate;
        private set => SetProperty(ref _journalExportMaxDate, value);
    }

    public bool SelectAllExportMonths
    {
        get => _selectAllExportMonths;
        set
        {
            if (!SetProperty(ref _selectAllExportMonths, value) || _updatingSelectAllExportMonths) return;
            ApplySelectAllExportMonths(value);
        }
    }

    public bool IncludeBankStatement
    {
        get => _includeBankStatement;
        set
        {
            if (SetProperty(ref _includeBankStatement, value))
            {
                PersistTallyExportPreference("Bank", "Include", value);
                ApplyManualTileToAutoSetting("Bank", value);
                RefreshAutoSyncTileIndicators();
                RefreshTallyCommandStates();
            }
        }
    }

    public bool IncludePurchases
    {
        get => _includePurchases;
        set
        {
            if (SetProperty(ref _includePurchases, value))
            {
                PersistTallyExportPreference("Purchase", "Include", value);
                ApplyManualTileToAutoSetting("Purchase", value);
                RefreshAutoSyncTileIndicators();
                RefreshTallyCommandStates();
            }
        }
    }

    public bool IncludeSales
    {
        get => _includeSales;
        set
        {
            if (SetProperty(ref _includeSales, value))
            {
                PersistTallyExportPreference("Sales", "Include", value);
                ApplyManualTileToAutoSetting("Sales", value);
                RefreshAutoSyncTileIndicators();
                RefreshTallyCommandStates();
            }
        }
    }

    public bool IncludeCashEntries
    {
        get => _includeCashEntries;
        set { if (SetProperty(ref _includeCashEntries, value)) { PersistTallyExportPreference("Cash", "Include", value); ApplyManualTileToAutoSetting("Cash", value); RefreshAutoSyncTileIndicators(); RefreshTallyCommandStates(); } }
    }
    public bool IncludeJournals
    {
        get => _includeJournals;
        set { if (SetProperty(ref _includeJournals, value)) { PersistTallyExportPreference("Journal", "Include", value); ApplyManualTileToAutoSetting("Journal", value); RefreshAutoSyncTileIndicators(); RefreshTallyCommandStates(); } }
    }

    // Compatibility aliases retained for the earlier Bank-only command.
    public DateTime? BankTallyFromDate
    {
        get => BankExportFromDate;
        set => BankExportFromDate = value;
    }

    public DateTime? BankTallyToDate
    {
        get => BankExportToDate;
        set => BankExportToDate = value;
    }

    public string LastBankImportStatus
    {
        get => _lastBankImportStatus;
        private set => SetProperty(ref _lastBankImportStatus, value);
    }

    public bool HasPendingTallySelection
    {
        get => _hasPendingTallySelection;
        private set => SetProperty(ref _hasPendingTallySelection, value);
    }

    public bool HasConfirmedTallyLink
    {
        get => _hasConfirmedTallyLink;
        private set => SetProperty(ref _hasConfirmedTallyLink, value);
    }

    public bool CanConfirmTallyLink
    {
        get => _canConfirmTallyLink;
        private set => SetProperty(ref _canConfirmTallyLink, value);
    }

    public bool CanCancelTallySelection
    {
        get => _canCancelTallySelection;
        private set => SetProperty(ref _canCancelTallySelection, value);
    }

    public bool CanUnlinkTallyLink
    {
        get => _canUnlinkTallyLink;
        private set => SetProperty(ref _canUnlinkTallyLink, value);
    }

    public bool CanImportBankRange
    {
        get => _canImportBankRange;
        private set => SetProperty(ref _canImportBankRange, value);
    }

    public bool UnlinkConfirmationPending
    {
        get => _unlinkConfirmationPending;
        private set
        {
            if (SetProperty(ref _unlinkConfirmationPending, value))
            {
                RefreshTallyCommandStates();
            }
        }
    }

    public bool IsTallyBusy => _tallyCoordinator.IsBusy;

    private void Refresh()
    {
        var client = _clientContext.CurrentClient;
        LoadTallyAutoLifecycle(client);
        if (client is null)
        {
            ApplyEmptyClient();
            RefreshTallySection();
            return;
        }

        // Paint a complete zero/default Dashboard immediately. Detailed counts are
        // allowed to arrive later from the prepared workspace/cache. The UI must never
        // wait for SQLite/history/balance calculations before showing the cards.
        ApplyImmediateDashboardState(client);
        ApplyClientIdentity(client);
        ApplyFastModuleCards(client);

        AvailableDataModules = new[]
        {
            BankCard.IsImported,
            PurchaseCard.IsImported,
            SalesCard.IsImported
        }.Count(value => value);

        LoadAccountingOverview(client);

        HasAnyData = TotalRecords > 0 || AvailableDataModules > 0;
        WorkspaceSummary = HasAnyData
            ? $"{TotalRecords:N0} accounting record(s) available for FY {client.FinancialYear}."
            : "No accounting data has been imported for this client yet.";

        QueueAutoImportPanelRefresh(client);
        QueueTallyExportPeriodRefresh(client);
        RefreshTallySection();

        OnPropertyChanged(nameof(HasBankData));
        OnPropertyChanged(nameof(HasPurchaseData));
        OnPropertyChanged(nameof(HasSalesData));
    }

    public void RefreshAccountingOverviewAfterExternalChange()
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            ApplyEmptyClient();
            return;
        }

        // Client-context changes deliberately avoid a duplicate full Dashboard load.
        // This display refresh therefore owns the identity bind as well as the cached
        // accounting overview, preventing a newly opened workspace from retaining the
        // previous "No client selected" header while its data is already visible.
        ApplyImmediateDashboardState(client);
        ApplyClientIdentity(client);
        ApplyFastModuleCards(client);
        AvailableDataModules = new[]
        {
            BankCard.IsImported,
            PurchaseCard.IsImported,
            SalesCard.IsImported
        }.Count(value => value);

        LoadAccountingOverview(client);
        HasAnyData = TotalRecords > 0 || AvailableDataModules > 0;
        WorkspaceSummary = HasAnyData
            ? $"{TotalRecords:N0} accounting record(s) available for FY {client.FinancialYear}."
            : "No accounting data has been imported for this client yet.";
        QueueAutoImportPanelRefresh(client);
        QueueTallyExportPeriodRefresh(client, force: true);
        OnPropertyChanged(nameof(HasBankData));
        OnPropertyChanged(nameof(HasPurchaseData));
        OnPropertyChanged(nameof(HasSalesData));
    }

    private void ApplyFastModuleCards(ClientProfile client)
    {
        if (!_workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var cached)) return;

        var bankMetric = cached.DashboardAccountingMetrics.FirstOrDefault(item => item.Label.Equals("Bank Statement", StringComparison.OrdinalIgnoreCase));
        var purchaseMetric = cached.DashboardAccountingMetrics.FirstOrDefault(item => item.Label.Equals("Purchase", StringComparison.OrdinalIgnoreCase));
        var salesMetric = cached.DashboardAccountingMetrics.FirstOrDefault(item => item.Label.Equals("Sales", StringComparison.OrdinalIgnoreCase));
        if (bankMetric is not null) ApplyFastCard(BankCard, bankMetric.Value, cached.BankAccounts.Count);
        if (purchaseMetric is not null) ApplyFastCard(PurchaseCard, purchaseMetric.Value, 0);
        if (salesMetric is not null) ApplyFastCard(SalesCard, salesMetric.Value, 0);
    }

    private static void ApplyFastCard(DashboardImportCard card, int recordCount, int bankCount)
    {
        card.IsImported = recordCount > 0;
        card.StatusText = recordCount > 0 ? "Available" : "Not available";
        card.PeriodText = string.Empty;
        card.FilingStatus = string.Empty;
        card.CountText = card.ImportType == "Bank Statement"
            ? $"{bankCount:N0} bank(s) • {recordCount:N0} transaction(s)"
            : $"{recordCount:N0} record(s)";
        card.LastImportedText = string.Empty;
        card.Details = string.Empty;
    }

    private void OnAccountingLiveChanged(object? sender, AccountingLiveChange change)
    {
        var client = _clientContext.CurrentClient;
        if (client is null ||
            client.Id != change.ClientId ||
            !client.FinancialYear.Equals(change.FinancialYear, StringComparison.OrdinalIgnoreCase))
            return;

        // Completion 61: MainWindow owns module-scoped cache invalidation. Dashboard must
        // not throw away the whole Client/FY cache again when the same live event arrives.

        // Fix38: Dashboard never performs repository reads on live accounting events.
        // MainWindow invalidates/refreshes only the affected workspace-cache modules; the
        // next prepared snapshot updates the visible Dashboard from published output.

        QueueTallyAutoSync(change);
    }

    private void OnWorkspaceSnapshotReady(ClientWorkspaceSnapshot snapshot)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != snapshot.ClientId ||
            !client.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            var current = _clientContext.CurrentClient;
            if (current is null || current.Id != snapshot.ClientId ||
                !current.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;
            // Fix38: the prepared snapshot is authoritative for all Dashboard accounting cards.
            // Rebind from published output only; Dashboard performs no module repository rebuild.
            ApplyFastModuleCards(current);
            // Fix28: a committed edit may have arrived while its module was
            // invalidated; the pending request is retried when the prepared
            // snapshot becomes current. A snapshot alone never creates work.
            if (Volatile.Read(ref _autoTallySyncPendingMask) != 0 &&
                Volatile.Read(ref _autoTallySyncInFlight) == 0)
                ScheduleTallyAutoSync();
            AvailableDataModules = new[]
            {
                BankCard.IsImported,
                PurchaseCard.IsImported,
                SalesCard.IsImported
            }.Count(value => value);
            LoadAccountingOverview(current);
            TotalRecords = AccountingSummaryMetrics
                .Where(item => !item.Label.Equals("Stock Master", StringComparison.OrdinalIgnoreCase))
                .Sum(item => item.Value);
            HasAnyData = TotalRecords > 0 || AvailableDataModules > 0;
            WorkspaceSummary = HasAnyData
                ? $"{TotalRecords:N0} accounting record(s) available for FY {current.FinancialYear}."
                : "No accounting data has been imported for this client yet.";
            QueueAutoImportPanelRefresh(current);

            // Month controls are still initialized only once to avoid replacing controls
            // while the user is clicking a Tally export month/date picker.
            if (!IsTallyExportPeriodMatrixInitialized(current))
                InitializeTallyExportPeriods(current);
            else if (IsTallyAutoSyncEnabled)
            {
                RefreshTallyExportSummaryState();
                // Fix27: preparing or repainting a client's cached snapshot is NOT a
                // committed accounting edit. It must not open Tally on client entry.
                // A real source event already set pending bits even if it reached us
                // before this paint; let that saved change run without new permission.
                if (Volatile.Read(ref _autoTallySyncPendingMask) != 0)
                    ScheduleTallyAutoSync();
            }
        }));
    }

    private void QueueTallyExportPeriodRefresh(ClientProfile client, bool force = false)
    {
        if (!force && IsTallyExportPeriodMatrixInitialized(client)) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            InitializeTallyExportPeriods(client, force);
            return;
        }

        // The period matrix is useful for Tally export, but it must never delay
        // opening the client dashboard. More importantly, a stale queued refresh must
        // never clear/recreate month/date controls during a user's click.
        dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                var current = _clientContext.CurrentClient;
                if (current is null ||
                    current.Id != client.Id ||
                    !current.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
                    return;
                if (!force && IsTallyExportPeriodMatrixInitialized(current)) return;
                if (!force && IsTallyExportInteractionActive && IsTallyExportPeriodMatrixInitialized(current)) return;
                InitializeTallyExportPeriods(current, force);
            }));
    }

    private void LoadAccountingOverview(ClientProfile client)
    {
        // Fix38: Dashboard is a consumer only. Accounting counts, review/action states,
        // mapping progress and stock totals are prepared by ClientWorkspaceStateCache.
        // Client selection never starts SQLite/module refreshes or recalculates accounting
        // populations on the WPF thread. If a snapshot is not ready yet, the zero/default
        // cards already painted by ApplyImmediateDashboardState remain visible.
        AccountingSummaryMetrics.Clear();
        Gstr3bMonthTiles.Clear();

        if (!_workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var cached))
        {
            foreach (var metric in CreateZeroAccountingMetrics()) AccountingSummaryMetrics.Add(metric);
            TotalRecords = 0;
            return;
        }

        foreach (var metric in cached.DashboardAccountingMetrics)
            AccountingSummaryMetrics.Add(metric);
        TotalRecords = cached.DashboardTotalRecords;

        // GST display is derived only from already-prepared GST cache data. No repository,
        // file-system, Auto Import or Tally request is made here.
        PopulateGstr3bTilesFromPreparedSnapshot(client, cached);
    }

    private void PopulateGstr3bTilesFromPreparedSnapshot(ClientProfile client, ClientWorkspaceSnapshot cached)
    {
        if (!HasValidGstin) return;
        var (fyFrom, _) = TallyLiveConnectionCoordinator.FinancialYearDates(client.FinancialYear);
        var returns = cached.GstReturns;
        var reconciliation = cached.GstReconciliation;
        var filingFrequency = returns
            .Select(row => row.FilingFrequency?.Trim() ?? string.Empty)
            .FirstOrDefault(value => value.Contains("Quarter", StringComparison.OrdinalIgnoreCase)) is { Length: > 0 }
                ? "Quarterly"
                : returns.Any(row => (row.FilingFrequency ?? string.Empty).Contains("Month", StringComparison.OrdinalIgnoreCase))
                    ? "Monthly"
                    : (client.LatestGstr3bPeriod.Contains(" to ", StringComparison.OrdinalIgnoreCase) ? "Quarterly" : "Monthly");

        var reconciliationByPeriod = reconciliation
            .GroupBy(row => NormalizeReportingPeriodKey(row.PeriodKey, filingFrequency), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(row => ReconciliationStatusSeverity(row.Status)).ThenByDescending(row => row.RefreshedUtc).First(),
                StringComparer.OrdinalIgnoreCase);
        var returnPeriods = returns
            .Select(row => NormalizeReportingPeriodKey(row.PeriodKey, filingFrequency))
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (filingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase))
        {
            for (var quarterIndex = 0; quarterIndex < 4; quarterIndex++)
            {
                var quarterFrom = fyFrom.AddMonths(quarterIndex * 3);
                var quarterTo = quarterFrom.AddMonths(3).AddDays(-1);
                var reportingKey = GstJournalGenerationService.PeriodKey(quarterFrom, filingFrequency);
                var status = BuildGstr3bMonthStatus(reportingKey, filingFrequency, reconciliationByPeriod, returnPeriods);
                Gstr3bMonthTiles.Add(new DashboardGstr3bMonthTile
                {
                    PeriodKey = reportingKey, Month = $"{quarterFrom:MMM}–{quarterTo:MMM}",
                    MonthLabel = $"{quarterFrom:MMMM}–{quarterTo:MMMM yyyy}", StatusText = status.Text,
                    StatusState = status.State, Detail = status.Detail, TargetPage = "GST"
                });
            }
            return;
        }

        for (var index = 0; index < 12; index++)
        {
            var month = fyFrom.AddMonths(index);
            var reportingKey = GstJournalGenerationService.PeriodKey(month, filingFrequency);
            var status = BuildGstr3bMonthStatus(reportingKey, filingFrequency, reconciliationByPeriod, returnPeriods);
            Gstr3bMonthTiles.Add(new DashboardGstr3bMonthTile
            {
                PeriodKey = reportingKey, Month = month.ToString("MMM", CultureInfo.InvariantCulture),
                MonthLabel = month.ToString("MMMM yyyy", CultureInfo.InvariantCulture), StatusText = status.Text,
                StatusState = status.State, Detail = status.Detail, TargetPage = "GST"
            });
        }
    }

    private static bool IsInside(DateTime date, DateTime from, DateTime to) =>
        date.Date >= from.Date && date.Date <= to.Date;

    private static int CountPurchaseVouchers(
        IEnumerable<PurchaseImportRecord> records,
        DateTime from,
        DateTime to) =>
        records
            .Where(row =>
                row.GstDate is DateTime date &&
                row.DocumentDate.HasValue &&
                row.DocumentNumber.Trim().Length > 0 &&
                row.SupplierName.Trim().Length > 0 &&
                IsInside(date, from, to))
            .Select(row => string.Join(
                "|",
                NormalizeTransactionKey(row.Destination),
                NormalizeTransactionKey(row.DocumentType),
                NormalizeTransactionKey(row.DocumentNumber),
                row.GstDate!.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                NormalizeTransactionKey(row.SupplierGstin),
                NormalizeTransactionKey(row.SupplierName)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static int CountSalesVouchers(
        IEnumerable<SalesImportRecord> records,
        DateTime from,
        DateTime to) =>
        records
            .Where(row =>
                (row.GstDate ?? row.DocumentDate) is DateTime date &&
                IsInside(date, from, to))
            .Select(row => string.Join(
                "|",
                NormalizeTransactionKey(row.Destination),
                NormalizeTransactionKey(row.DocumentType),
                NormalizeTransactionKey(row.DocumentNumber),
                (row.GstDate ?? row.DocumentDate)!.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                NormalizeTransactionKey(row.CustomerGstin),
                NormalizeTransactionKey(row.CustomerName)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static string NormalizeTransactionKey(string? value) =>
        string.Join(
            " ",
            (value ?? string.Empty)
                .Trim()
                .ToUpperInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeReportingPeriodKey(string periodKey, string filingFrequency)
    {
        var key = (periodKey ?? string.Empty).Trim();
        if (key.Length == 6 &&
            int.TryParse(key[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) &&
            int.TryParse(key[4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var month) &&
            month is >= 1 and <= 12)
        {
            return GstJournalGenerationService.PeriodKey(new DateTime(year, month, 1), filingFrequency);
        }
        return key;
    }

    private static int ReconciliationStatusSeverity(string? status)
    {
        var value = (status ?? string.Empty).Trim();
        if (value.Length == 0) return 1;
        if (value.Equals("Matched", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Balanced", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("reconciled", StringComparison.OrdinalIgnoreCase) &&
            !value.Contains("not", StringComparison.OrdinalIgnoreCase) &&
            !value.Contains("unreconciled", StringComparison.OrdinalIgnoreCase))
            return 0;
        return 2;
    }

    private static (string Text, string State, string Detail) BuildGstr3bMonthStatus(
        string reportingKey,
        string filingFrequency,
        IReadOnlyDictionary<string, GstReconciliationRow> reconciliation,
        IReadOnlySet<string> returnPeriods)
    {
        var quarterly = filingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase);
        var periodDetail = quarterly
            ? $"Quarterly GSTR-3B period {reportingKey}"
            : $"Monthly GSTR-3B period {reportingKey}";
        if (reconciliation.TryGetValue(reportingKey, out var row))
        {
            var status = row.Status.Trim();
            if (status.Equals("Matched", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Balanced", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("reconciled", StringComparison.OrdinalIgnoreCase) &&
                !status.Contains("not", StringComparison.OrdinalIgnoreCase) &&
                !status.Contains("unreconciled", StringComparison.OrdinalIgnoreCase))
            {
                return ("Matched", "Green", periodDetail + " • matched");
            }

            var display = status.Length == 0 ? "Review" : status;
            return (display, "Red", periodDetail + " • requires review");
        }

        if (returnPeriods.Contains(reportingKey))
        {
            return ("Review", "Red", periodDetail + " • GSTR-3B imported but not matched");
        }

        return ("Not available", "Grey", periodDetail + " • no GSTR-3B data");
    }

    public void BeginTallyExportInteraction()
    {
        // Protect active mouse/calendar interaction from a stale ContextIdle/cache
        // refresh that would otherwise replace the bound controls underneath it.
        _tallyExportInteractionHoldUntilUtc = DateTime.UtcNow.AddSeconds(2);
    }

    private bool IsTallyExportInteractionActive => DateTime.UtcNow < _tallyExportInteractionHoldUntilUtc;

    private bool IsTallyExportPeriodMatrixInitialized(ClientProfile client) =>
        _dateClientId == client.Id &&
        _dateFinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase) &&
        BankTallyExportMonths.Count == 12 &&
        PurchaseTallyExportMonths.Count == 12 &&
        SalesTallyExportMonths.Count == 12 &&
        CashTallyExportMonths.Count == 12 &&
        JournalTallyExportMonths.Count == 12;

    private void InitializeTallyExportPeriods(ClientProfile client, bool force = false)
    {
        if (!force && IsTallyExportInteractionActive && IsTallyExportPeriodMatrixInitialized(client))
            return;

        // Do not mark a Client/FY as initialized until its prepared workspace is
        // actually available. The old order set the token before a cache miss and
        // therefore skipped loading the saved Tally selections on the retry.
        if (!_workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var cached))
        {
            _workspaceStateCache.QueueRefresh(client);
            return;
        }

        var isNewClient = _dateClientId != client.Id ||
                          !_dateFinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase);
        var selectedBankKeys = BankTallyExportMonths.Where(item => item.IsSelected).Select(item => item.PeriodKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedPurchaseKeys = PurchaseTallyExportMonths.Where(item => item.IsSelected).Select(item => item.PeriodKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedSalesKeys = SalesTallyExportMonths.Where(item => item.IsSelected).Select(item => item.PeriodKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedCashKeys = CashTallyExportMonths.Where(item => item.IsSelected).Select(item => item.PeriodKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedJournalKeys = JournalTallyExportMonths.Where(item => item.IsSelected).Select(item => item.PeriodKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bankSelectionSaved = !isNewClient;
        var purchaseSelectionSaved = !isNewClient;
        var salesSelectionSaved = !isNewClient;
        var cashSelectionSaved = !isNewClient;
        var journalSelectionSaved = !isNewClient;
        if (isNewClient)
        {
            LoadTallyExportPreferences(client);
            selectedBankKeys = ReadSavedTallyPeriods(client, "Bank", out bankSelectionSaved);
            selectedPurchaseKeys = ReadSavedTallyPeriods(client, "Purchase", out purchaseSelectionSaved);
            selectedSalesKeys = ReadSavedTallyPeriods(client, "Sales", out salesSelectionSaved);
            selectedCashKeys = ReadSavedTallyPeriods(client, "Cash", out cashSelectionSaved);
            selectedJournalKeys = ReadSavedTallyPeriods(client, "Journal", out journalSelectionSaved);
            _dateClientId = client.Id;
            _dateFinancialYear = client.FinancialYear;
        }

        BankTallyExportMonths.Clear(); PurchaseTallyExportMonths.Clear(); SalesTallyExportMonths.Clear();
        CashTallyExportMonths.Clear(); JournalTallyExportMonths.Clear();
        var (fyFrom, fyTo) = TallyLiveConnectionCoordinator.FinancialYearDates(client.FinancialYear);
        IReadOnlyList<BankTransactionRecord> bankTransactions = cached.BankTransactions
            .Where(row => !row.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase)).ToArray();
        IReadOnlyList<PurchaseImportRecord> purchases = cached.Purchases;
        IReadOnlyList<SalesImportRecord> sales = cached.Sales;
        IReadOnlyList<CashEntryRecord> cashEntries = cached.CashEntries;
        IReadOnlyList<JournalVoucher> journals = cached.Journals;

        var link = _tallyCoordinator.GetCurrentClientLink();
        IReadOnlySet<string> importedBank = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> importedPurchases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> importedSales = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> importedCash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> importedJournals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (link is not null)
        {
            try
            {
                importedBank = _tallyRepository.GetImportedSourceKeys(client.Id, client.FinancialYear, link.CompanyKey, "Bank Voucher");
                importedPurchases = _tallyRepository.GetImportedSourceKeys(client.Id, client.FinancialYear, link.CompanyKey, "Purchase Voucher");
                importedSales = _tallyRepository.GetImportedSourceKeys(client.Id, client.FinancialYear, link.CompanyKey, "Sales Voucher");
                importedCash = _tallyRepository.GetImportedSourceKeys(client.Id, client.FinancialYear, link.CompanyKey, "Cash Voucher");
                importedJournals = _tallyRepository.GetImportedSourceKeys(client.Id, client.FinancialYear, link.CompanyKey, "Manual Journal")
                    .Concat(_tallyRepository.GetImportedSourceKeys(client.Id, client.FinancialYear, link.CompanyKey, "GST Journal"))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        for (var index = 0; index < 12; index++)
        {
            var from = fyFrom.AddMonths(index); var to = from.AddMonths(1).AddDays(-1);
            var bankKeys = bankTransactions.Where(row => IsInside(row.TransactionDate, from, to))
                .Select(row => TallyVoucherFingerprint.BuildSourceKey(client.Id, client.FinancialYear, row.TransactionId, row.Id))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var bank = BuildTallyExportMonthOption(index, from, to, bankKeys, importedBank);
            var purchase = BuildTallyExportMonthOption(index, from, to, BuildPurchaseSourceKeys(client, purchases, from, to), importedPurchases);
            var sale = BuildTallyExportMonthOption(index, from, to, BuildSalesSourceKeys(client, sales, from, to), importedSales);
            var cash = BuildTallyExportMonthOption(index, from, to, BuildCashSourceKeys(client, cashEntries, from, to), importedCash);
            var journal = BuildTallyExportMonthOption(index, from, to, BuildJournalSourceKeys(client, journals, from, to), importedJournals);
            bank.IsSelected = bankSelectionSaved ? selectedBankKeys.Contains(bank.PeriodKey) : _selectAllExportMonths && bank.SourceCount > 0;
            purchase.IsSelected = purchaseSelectionSaved ? selectedPurchaseKeys.Contains(purchase.PeriodKey) : _selectAllExportMonths && purchase.SourceCount > 0;
            sale.IsSelected = salesSelectionSaved ? selectedSalesKeys.Contains(sale.PeriodKey) : _selectAllExportMonths && sale.SourceCount > 0;
            cash.IsSelected = cashSelectionSaved ? selectedCashKeys.Contains(cash.PeriodKey) : _selectAllExportMonths && cash.SourceCount > 0;
            journal.IsSelected = journalSelectionSaved ? selectedJournalKeys.Contains(journal.PeriodKey) : _selectAllExportMonths && journal.SourceCount > 0;
            BankTallyExportMonths.Add(bank); PurchaseTallyExportMonths.Add(purchase); SalesTallyExportMonths.Add(sale);
            CashTallyExportMonths.Add(cash); JournalTallyExportMonths.Add(journal);
        }

        _loadingExportPreferences = true;
        try
        {
        ApplySourceDateRange(
            bankTransactions.Select(row => row.TransactionDate), fyFrom, fyTo,
            BankExportFromDate, BankExportToDate,
            (min, max) => { BankExportMinDate = min; BankExportMaxDate = max; },
            (from, to) => { BankExportFromDate = from; BankExportToDate = to; });
        ApplySourceDateRange(
            purchases.Where(row => row.GstDate.HasValue).Select(row => row.GstDate!.Value), fyFrom, fyTo,
            PurchaseExportFromDate, PurchaseExportToDate,
            (min, max) => { PurchaseExportMinDate = min; PurchaseExportMaxDate = max; },
            (from, to) => { PurchaseExportFromDate = from; PurchaseExportToDate = to; });
        ApplySourceDateRange(
            sales.Select(row => row.GstDate ?? row.DocumentDate).Where(date => date.HasValue).Select(date => date!.Value), fyFrom, fyTo,
            SalesExportFromDate, SalesExportToDate,
            (min, max) => { SalesExportMinDate = min; SalesExportMaxDate = max; },
            (from, to) => { SalesExportFromDate = from; SalesExportToDate = to; });
        ApplySourceDateRange(
            cashEntries.Select(row => row.EntryDate), fyFrom, fyTo,
            CashExportFromDate, CashExportToDate,
            (min, max) => { CashExportMinDate = min; CashExportMaxDate = max; },
            (from, to) => { CashExportFromDate = from; CashExportToDate = to; });
        ApplySourceDateRange(
            journals.Select(row => row.VoucherDate), fyFrom, fyTo,
            JournalExportFromDate, JournalExportToDate,
            (min, max) => { JournalExportMinDate = min; JournalExportMaxDate = max; },
            (from, to) => { JournalExportFromDate = from; JournalExportToDate = to; });
        }
        finally
        {
            _loadingExportPreferences = false;
        }

        if (isNewClient)
        {
            _loadingExportPreferences = true;
            try
            {
                _includeBankStatement = ReadSavedTallyBool(client, "Bank", "Include", HasBankExportData);
                _includePurchases = ReadSavedTallyBool(client, "Purchase", "Include", HasPurchaseExportData);
                _includeSales = ReadSavedTallyBool(client, "Sales", "Include", HasSalesExportData);
                _includeCashEntries = ReadSavedTallyBool(client, "Cash", "Include", HasCashExportData);
                _includeJournals = ReadSavedTallyBool(client, "Journal", "Include", HasJournalExportData);
                OnPropertyChanged(nameof(IncludeBankStatement));
                OnPropertyChanged(nameof(IncludePurchases));
                OnPropertyChanged(nameof(IncludeSales));
                OnPropertyChanged(nameof(IncludeCashEntries));
                OnPropertyChanged(nameof(IncludeJournals));
            }
            finally { _loadingExportPreferences = false; }
            _monthRangeGestures.Clear();
        }

        // Lack of current source data must not silently turn OFF a user's durable
        // Auto Sync module setting: new eligible work may arrive in that module.
        // While OFF, retain the original Manual Export availability behavior.
        var automaticSelectionLoad = IsTallyAutoSyncEnabled;
        if (automaticSelectionLoad) _loadingExportPreferences = true;
        try
        {
            if (!HasBankExportData) IncludeBankStatement = false;
            if (!HasPurchaseExportData) IncludePurchases = false;
            if (!HasSalesExportData) IncludeSales = false;
            if (!HasCashExportData) IncludeCashEntries = false;
            if (!HasJournalExportData) IncludeJournals = false;
        }
        finally
        {
            if (automaticSelectionLoad) _loadingExportPreferences = false;
        }
        NotifyTallyExportAvailability(); UpdateSelectAllExportMonthsState(); PublishBankExportSelection(); RefreshTallyCommandStates();
        if (IsTallyAutoSyncEnabled)
        {
            if (isNewClient) SyncManualTileSelectionFromAutoSettings();
            // Fix27: opening/rebinding Client+FY is read-only. A loaded selection
            // must never be mistaken for a new XML-ready voucher. Explicit module
            // selection edits and committed source events retain their own queue.
            // Work already queued during the matrix load is scheduled below.
        }
        // A committed edit can arrive before this matrix/cache is initialized.
        // Once selection is populated, wake the saved mask without another change.
        if (IsTallyAutoSyncEnabled && Volatile.Read(ref _autoTallySyncPendingMask) != 0)
            ScheduleTallyAutoSync();
    }

    private static DateTime? ClampExportDate(DateTime? value, DateTime? min, DateTime? max)
    {
        if (!value.HasValue) return null;
        var date = value.Value.Date;
        if (min.HasValue && date < min.Value.Date) date = min.Value.Date;
        if (max.HasValue && date > max.Value.Date) date = max.Value.Date;
        return date;
    }

    private static void ApplySourceDateRange(
        IEnumerable<DateTime> sourceDates,
        DateTime fyFrom,
        DateTime fyTo,
        DateTime? currentFrom,
        DateTime? currentTo,
        Action<DateTime, DateTime> limitsSetter,
        Action<DateTime, DateTime> selectionSetter)
    {
        var dates = sourceDates
            .Select(date => date.Date)
            .Where(date => date >= fyFrom.Date && date <= fyTo.Date)
            .ToArray();

        // A user's saved date range belongs to the Client/FY, not to today's current
        // source-data min/max. New imports can expand/contract the source dates without
        // overwriting the dates the user previously entered.
        limitsSetter(fyFrom.Date, fyTo.Date);
        var defaultFrom = dates.Length == 0 ? fyFrom.Date : dates.Min();
        var defaultTo = dates.Length == 0 ? fyTo.Date : dates.Max();
        var from = currentFrom?.Date ?? defaultFrom;
        var to = currentTo?.Date ?? defaultTo;

        if (from < fyFrom.Date || from > fyTo.Date) from = fyFrom.Date;
        if (to < fyFrom.Date || to > fyTo.Date) to = fyTo.Date;
        if (from > to)
        {
            from = currentFrom.HasValue ? from : defaultFrom;
            to = currentTo.HasValue ? to : defaultTo;
            if (from > to) { from = fyFrom.Date; to = fyTo.Date; }
        }
        selectionSetter(from, to);
    }

    private static TallyExportMonthOption BuildTallyExportMonthOption(
        int monthIndex,
        DateTime from,
        DateTime to,
        IReadOnlyCollection<string> sourceKeys,
        IReadOnlySet<string> importedKeys)
    {
        var imported = sourceKeys.Count(key => importedKeys.Contains(key));
        var state = sourceKeys.Count == 0
            ? "Grey"
            : imported >= sourceKeys.Count
                ? "Green"
                : "Red";
        var status = state switch
        {
            "Green" => $"Exported {imported:N0} of {sourceKeys.Count:N0}",
            "Red" => imported > 0
                ? $"Pending {sourceKeys.Count - imported:N0} of {sourceKeys.Count:N0}"
                : $"Pending {sourceKeys.Count:N0}",
            _ => "No data"
        };
        return new TallyExportMonthOption
        {
            PeriodKey = from.ToString("yyyyMM", CultureInfo.InvariantCulture),
            MonthIndex = monthIndex,
            ShortcutText = $"F{monthIndex + 1}",
            Label = from.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            ShortLabel = from.ToString("MMM", CultureInfo.InvariantCulture),
            FromDate = from,
            ToDate = to,
            SourceCount = sourceKeys.Count,
            ImportedCount = imported,
            StatusState = state,
            StatusText = status
        };
    }

    private static IReadOnlyList<string> BuildPurchaseSourceKeys(
        ClientProfile client,
        IEnumerable<PurchaseImportRecord> records,
        DateTime from,
        DateTime to) =>
        records
            .Where(row =>
                row.GstDate is DateTime date &&
                row.DocumentDate.HasValue &&
                row.DocumentNumber.Trim().Length > 0 &&
                row.SupplierName.Trim().Length > 0 &&
                IsInside(date, from, to))
            .GroupBy(row => new
            {
                Destination = NormalizePurchaseDestination(row.Destination, row.DocumentType),
                row.DocumentNumber,
                Date = row.GstDate!.Value.Date,
                PartyGstin = NormalizeVoucherGstin(row.SupplierGstin)
            })
            .Select(group => BuildDocumentSourceKey(
                "PURCHASE",
                client,
                group.Key.Destination,
                group.Key.DocumentNumber,
                group.Key.PartyGstin,
                group.Key.Date))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> BuildSalesSourceKeys(
        ClientProfile client,
        IEnumerable<SalesImportRecord> records,
        DateTime from,
        DateTime to) =>
        records
            .Where(row =>
                (row.GstDate ?? row.DocumentDate) is DateTime date &&
                row.DocumentDate.HasValue &&
                row.DocumentNumber.Trim().Length > 0 &&
                row.CustomerName.Trim().Length > 0 &&
                IsInside(date, from, to))
            .GroupBy(row => new
            {
                Destination = NormalizeSalesDestination(row.Destination, row.DocumentType),
                row.DocumentNumber,
                Date = (row.GstDate ?? row.DocumentDate)!.Value.Date,
                PartyGstin = NormalizeVoucherGstin(row.CustomerGstin)
            })
            .Select(group => BuildDocumentSourceKey(
                "SALES",
                client,
                group.Key.Destination,
                group.Key.DocumentNumber,
                group.Key.PartyGstin,
                group.Key.Date))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> BuildCashSourceKeys(
        ClientProfile client,
        IEnumerable<CashEntryRecord> records,
        DateTime from,
        DateTime to) =>
        records
            .Where(row => IsInside(row.EntryDate, from, to) &&
                          row.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
            .Select(row =>
            {
                var identity = row.EntryId.Trim().Length > 0
                    ? row.EntryId.Trim()
                    : row.Id.ToString(CultureInfo.InvariantCulture);
                return $"AAH|CASH|{client.Id}|{client.FinancialYear}|{identity}|{row.EntryDate:yyyyMMdd}";
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> BuildJournalSourceKeys(
        ClientProfile client,
        IEnumerable<JournalVoucher> vouchers,
        DateTime from,
        DateTime to) =>
        vouchers
            .Where(voucher => IsInside(voucher.VoucherDate, from, to))
            .Select(voucher =>
            {
                if (voucher.GenerationMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
                    return $"AAH|GSTJ|{client.Id}|{client.FinancialYear}|{voucher.PeriodKey}";

                var identity = voucher.VoucherNumber.Trim().Length > 0
                    ? voucher.VoucherNumber.Trim()
                    : voucher.PersistentId.ToString(CultureInfo.InvariantCulture);
                return $"AAH|JOURNAL|{client.Id}|{client.FinancialYear}|{identity}|{voucher.VoucherDate:yyyyMMdd}";
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizePurchaseDestination(string destination, string documentType) =>
        destination.Contains("Debit", StringComparison.OrdinalIgnoreCase) ||
        documentType.Contains("Debit", StringComparison.OrdinalIgnoreCase) ||
        documentType.Contains("Credit Note", StringComparison.OrdinalIgnoreCase)
            ? "Debit Note"
            : "Purchase";

    private static string NormalizeSalesDestination(string destination, string documentType) =>
        destination.Contains("Credit", StringComparison.OrdinalIgnoreCase) ||
        documentType.Contains("Credit", StringComparison.OrdinalIgnoreCase)
            ? "Credit Note"
            : "Sales";

    private static string BuildDocumentSourceKey(
        string module,
        ClientProfile client,
        string voucherType,
        string documentNumber,
        string partyGstin,
        DateTime date) =>
        $"AAH|{module}|{client.Id}|{client.FinancialYear}|{voucherType}|{date:yyyyMMdd}|{partyGstin.Trim()}|{documentNumber.Trim()}";

    private static string NormalizeVoucherGstin(string value)
    {
        var normalized = TallyCompanyInfo.NormalizeGstin(value);
        return normalized.Length == 15 ? normalized : string.Empty;
    }

    private void PublishBankExportSelection()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        if (!TryResolveModuleRanges(BankExportPeriodMode, BankTallyExportMonths, BankExportFromDate, BankExportToDate,
                "Bank Statement", out var ranges, out _))
        {
            _tallyExportSelectionContext.SetBankRanges(client.Id, client.FinancialYear, [], BankExportPeriodMode, string.Empty);
            return;
        }
        var selectedLabels = BankTallyExportMonths.Where(item => item.IsSelected).Select(item => item.Label).ToArray();
        var description = BankExportPeriodMode.Equals("Month", StringComparison.OrdinalIgnoreCase)
            ? selectedLabels.Length > 0 ? string.Join(", ", selectedLabels) : "All available months"
            : $"{ranges[0].FromDate:dd.MM.yyyy} to {ranges[0].ToDate:dd.MM.yyyy}";
        _tallyExportSelectionContext.SetBankRanges(client.Id, client.FinancialYear, ranges, BankExportPeriodMode, description);
    }

    public void SetExportMonthSelection(string module, int monthIndex, bool selected)
    {
        var options = GetExportOptions(module);
        if (monthIndex < 0 || monthIndex >= options.Count) return;
        ClearPendingMonthRangeGestures(module);
        options[monthIndex].IsSelected = selected;
        SynchronizeModuleIncludeWithMonthSelection(module);
        UpdateSelectAllExportMonthsState();
        PersistTallyMonthSelections(module);
        if (module.Equals("Bank", StringComparison.OrdinalIgnoreCase)) PublishBankExportSelection();
        RefreshTallyCommandStates();
    }

    public void ToggleExportMonthShortcut(int monthIndex, string? moduleScope, bool shift)
    {
        if (monthIndex < 0 || monthIndex > 11) return;
        var scope = string.IsNullOrWhiteSpace(moduleScope) ? "All" : moduleScope.Trim();
        var modules = ResolveShortcutModules(scope)
            .Where(IsExportModuleAvailable)
            .ToArray();
        if (modules.Length == 0) return;
        foreach (var module in modules) SetModuleMonthMode(module);

        if (!shift)
        {
            _monthRangeGestures.Remove(scope);
            ToggleMonthRange(modules, monthIndex, monthIndex);
            foreach (var module in modules) SynchronizeModuleIncludeWithMonthSelection(module);
            UpdateSelectAllExportMonthsState();
            foreach (var module in modules) PersistTallyMonthSelections(module);
            PublishBankExportSelection();
            RefreshTallyCommandStates();
            return;
        }

        if (!_monthRangeGestures.TryGetValue(scope, out var gesture))
        {
            var snapshots = modules.ToDictionary(
                module => module,
                module => GetExportOptions(module).Select(option => option.IsSelected).ToArray(),
                StringComparer.OrdinalIgnoreCase);
            _monthRangeGestures[scope] = new MonthRangeGestureState(monthIndex, snapshots);
            ToggleMonthRange(modules, 0, monthIndex);
        }
        else
        {
            RestoreMonthRangeSnapshots(gesture);
            ToggleMonthRange(modules, Math.Min(gesture.AnchorMonthIndex, monthIndex),
                Math.Max(gesture.AnchorMonthIndex, monthIndex));
            _monthRangeGestures.Remove(scope);
        }

        foreach (var module in modules) SynchronizeModuleIncludeWithMonthSelection(module);
        UpdateSelectAllExportMonthsState();
        foreach (var module in modules) PersistTallyMonthSelections(module);
        PublishBankExportSelection();
        RefreshTallyCommandStates();
    }

    private string[] ResolveShortcutModules(string scope) =>
        scope.Equals("All", StringComparison.OrdinalIgnoreCase)
            ? ["Bank", "Purchase", "Sales", "Cash", "Journal"]
            : [scope];

    private void SetModuleMonthMode(string module)
    {
        if (module.Equals("Bank", StringComparison.OrdinalIgnoreCase))
            BankExportPeriodMode = "Month";
        else if (module.Equals("Purchase", StringComparison.OrdinalIgnoreCase))
            PurchaseExportPeriodMode = "Month";
        else if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase))
            SalesExportPeriodMode = "Month";
        else if (module.Equals("Cash", StringComparison.OrdinalIgnoreCase))
            CashExportPeriodMode = "Month";
        else if (module.Equals("Journal", StringComparison.OrdinalIgnoreCase))
            JournalExportPeriodMode = "Month";
    }

    private void ToggleMonthRange(IEnumerable<string> modules, int from, int to)
    {
        foreach (var module in modules)
        {
            var options = GetExportOptions(module);
            if (options.Count == 0) continue;
            var indexes = Enumerable.Range(from, to - from + 1)
                .Where(index => index >= 0 && index < options.Count)
                .ToArray();
            if (indexes.Length == 0) continue;
            var select = indexes.Any(index => !options[index].IsSelected);
            foreach (var index in indexes) options[index].IsSelected = select;
        }
    }

    private void RestoreMonthRangeSnapshots(MonthRangeGestureState gesture)
    {
        foreach (var (module, selectedStates) in gesture.SelectedStatesByModule)
        {
            var options = GetExportOptions(module);
            for (var index = 0; index < options.Count && index < selectedStates.Length; index++)
                options[index].IsSelected = selectedStates[index];
        }
    }

    private void ClearPendingMonthRangeGestures(string module)
    {
        _monthRangeGestures.Remove(module);
        _monthRangeGestures.Remove("All");
    }

    private void SynchronizeModuleIncludeWithMonthSelection(string module)
    {
        // Month selection controls the period only. An enabled module with zero
        // selected months intentionally means "all available months". The module
        // checkbox is therefore the only control that can explicitly exclude it.
        var hasSelection = GetExportOptions(module).Any(option => option.IsSelected);
        if (!hasSelection) return;
        if (module.Equals("Bank", StringComparison.OrdinalIgnoreCase))
            IncludeBankStatement = true;
        else if (module.Equals("Purchase", StringComparison.OrdinalIgnoreCase))
            IncludePurchases = true;
        else if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase))
            IncludeSales = true;
        else if (module.Equals("Cash", StringComparison.OrdinalIgnoreCase))
            IncludeCashEntries = true;
        else if (module.Equals("Journal", StringComparison.OrdinalIgnoreCase))
            IncludeJournals = true;
    }

    public void SetAllExportMonthsForModule(string module, bool selected)
    {
        BeginTallyExportInteraction();
        var normalized = (module ?? string.Empty).Trim();
        var options = GetExportOptions(normalized);
        foreach (var option in options)
            option.IsSelected = selected && option.SourceCount > 0;
        ClearPendingMonthRangeGestures(normalized);
        if (selected && IsExportModuleAvailable(normalized))
            SynchronizeModuleIncludeWithMonthSelection(normalized);
        PersistTallyMonthSelections(normalized);
        if (normalized.Equals("Bank", StringComparison.OrdinalIgnoreCase))
            PublishBankExportSelection();
        UpdateSelectAllExportMonthsState();
        RefreshTallyCommandStates();
    }

    private void ApplySelectAllExportMonths(bool selected)
    {
        foreach (var module in new[] { "Bank", "Purchase", "Sales", "Cash", "Journal" })
        {
            foreach (var option in GetExportOptions(module))
                option.IsSelected = selected && option.SourceCount > 0;
            if (selected && IsExportModuleAvailable(module))
                SynchronizeModuleIncludeWithMonthSelection(module);
        }
        _monthRangeGestures.Clear();
        foreach (var module in new[] { "Bank", "Purchase", "Sales", "Cash", "Journal" }) PersistTallyMonthSelections(module);
        PublishBankExportSelection();
        RefreshTallyCommandStates();
    }

    private void UpdateSelectAllExportMonthsState()
    {
        var available = new[] { BankTallyExportMonths, PurchaseTallyExportMonths, SalesTallyExportMonths, CashTallyExportMonths, JournalTallyExportMonths }
            .SelectMany(options => options)
            .Where(option => option.SourceCount > 0)
            .ToArray();
        var isAll = available.Length > 0 && available.All(option => option.IsSelected);
        if (_selectAllExportMonths == isAll) return;
        _updatingSelectAllExportMonths = true;
        try
        {
            _selectAllExportMonths = isAll;
            OnPropertyChanged(nameof(SelectAllExportMonths));
        }
        finally
        {
            _updatingSelectAllExportMonths = false;
        }
    }

    public void ToggleExportMonth(string module, int monthIndex) =>
        SetExportMonthSelection(module, monthIndex, !GetExportOptions(module)[monthIndex].IsSelected);

    private ObservableCollection<TallyExportMonthOption> GetExportOptions(string module) =>
        module.Equals("Bank", StringComparison.OrdinalIgnoreCase) ? BankTallyExportMonths :
        module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? PurchaseTallyExportMonths :
        module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? SalesTallyExportMonths :
        module.Equals("Cash", StringComparison.OrdinalIgnoreCase) ? CashTallyExportMonths : JournalTallyExportMonths;

    private bool IsExportModuleAvailable(string module) =>
        module.Equals("Bank", StringComparison.OrdinalIgnoreCase) ? HasBankExportData :
        module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? HasPurchaseExportData :
        module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? HasSalesExportData :
        module.Equals("Cash", StringComparison.OrdinalIgnoreCase) ? HasCashExportData : HasJournalExportData;

    private void RefreshTallyReportOptionsForRuntime()
    {
        // Fix123: ERP 9 keeps its normal supported live-report surface. The safe ERP 9
        // catalog is narrower than Prime, but it is never hidden/disabled merely to
        // avoid the five-second background pulse holding AAH.
        var liveReports = _tallyCoordinator.IsErp9CompatibilityMode
            ? TallyReportCatalog.Erp9DisplayNames
            : TallyReportCatalog.DisplayNames;
        var desired = LocalTallyReports
            .Concat(liveReports.Where(report => !report.Equals("Statistics", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (TallyReportOptions.SequenceEqual(desired, StringComparer.OrdinalIgnoreCase))
            return;

        var current = SelectedTallyReport;
        TallyReportOptions.Clear();
        foreach (var report in desired)
            TallyReportOptions.Add(report);

        if (!TallyReportOptions.Contains(current, StringComparer.OrdinalIgnoreCase))
            SelectedTallyReport = "Company Overview";
    }

    private void RefreshTallySection()
    {
        RefreshTallyReportOptionsForRuntime();
        OnPropertyChanged(nameof(IsErp9CoreLiveMode));
        OnPropertyChanged(nameof(AreSupportingTallyReportsEnabled));
        var client = _clientContext.CurrentClient;
        var selected = _tallyCoordinator.SelectedCompany;
        // No parent-link borrowing: the coordinator reads the active ClientId.
        var link = _tallyCoordinator.GetCurrentClientLink();
        var pending = client is not null && _tallyCoordinator.IsCurrentSelectionPending();
        var openLinkedCompany = link is null
            ? null
            : _tallyCoordinator.OpenCompanies.FirstOrDefault(company =>
                company.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase));

        TallyConnectionStatus = _tallyCoordinator.ConnectionStatus;
        TallyActionStatus = _tallyCoordinator.LastOperationStatus;
        TallyLiveXmlStatus = _tallyCoordinator.LiveXmlStatus;
        OnPropertyChanged(nameof(TallyReadyVoucherCount));
        OnPropertyChanged(nameof(TallyNewMasterCreateCount));
        HasPendingTallySelection = pending;
        HasConfirmedTallyLink = link is not null;

        if (client is null)
        {
            ApplyEmptyTallySection();
            return;
        }

        TallyClientLink? metricLink = link;
        var displayCompany = pending ? selected : openLinkedCompany ?? selected;
        var confirmedLinkIsLive = IsConfirmedTallyLinkLive(link, openLinkedCompany);

        if (pending && selected is not null)
        {
            ApplyCompanyIdentity(selected);
            TallyLinkStatus = BuildPendingLinkStatus(client, link, selected);
            TallyIdentityMatchText = TallyLiveConnectionCoordinator.IdentityMatchText(client, selected);
        }
        else if (link is not null)
        {
            ApplyLinkIdentity(link, openLinkedCompany);
            TallyLinkStatus = confirmedLinkIsLive
                ? "Confirmed link • live connection active"
                : $"Confirmed link • {link.CompanyName} is not currently open in Tally";
            TallyIdentityMatchText = "This Tally company is exclusively linked to the active Client/FY.";
        }
        else if (selected is not null)
        {
            ApplyCompanyIdentity(selected);
            TallyLinkStatus = "Selected in the top dropdown • confirmation required";
            TallyIdentityMatchText = TallyLiveConnectionCoordinator.IdentityMatchText(client, selected);
        }
        else
        {
            ApplyNoCompanyIdentity();
            TallyLinkStatus = "No Tally company linked.";
            TallyIdentityMatchText =
                "Select an open Tally company from the top dropdown. Confirmation will remain inside this dashboard.";
        }

        TallyLastSyncText = link is null || link.LastMasterSyncUtc == DateTime.MinValue
            ? "Never synchronized"
            : $"Masters {AppTime.ToLocal(link.LastMasterSyncUtc):dd-MM-yyyy HH:mm} • " +
              $"Reports {FormatLocalDate(link.LastReportRefreshUtc)}";
        TallyLastError = link?.LastError?.Trim() ?? string.Empty;

        var selectedLinkedElsewhere = selected is not null &&
                                      selected.IsLinked &&
                                      selected.LinkedClientId != client.Id;
        var clientHasDifferentLink = selected is not null &&
                                     link is not null &&
                                     !link.CompanyKey.Equals(selected.CompanyKey, StringComparison.OrdinalIgnoreCase);
        var identityAllowed = selected is not null &&
                              TallyLiveConnectionCoordinator.CanConfirmIdentity(client, selected);

        CanConfirmTallyLink = pending &&
                              selected is not null &&
                              identityAllowed &&
                              !selectedLinkedElsewhere &&
                              !clientHasDifferentLink &&
                              !_tallyCoordinator.IsBusy;
        CanCancelTallySelection = pending && !_tallyCoordinator.IsBusy;
        CanUnlinkTallyLink = link is not null && !_tallyCoordinator.IsBusy;
        CanImportBankRange = confirmedLinkIsLive &&
                             BankTallyFromDate is not null &&
                             BankTallyToDate is not null &&
                             BankTallyFromDate.Value.Date <= BankTallyToDate.Value.Date &&
                             !_tallyCoordinator.IsBusy;

        if (metricLink is null && displayCompany is not null)
        {
            metricLink = LinkPreview(displayCompany);
        }

        RefreshTallyMetrics(metricLink, displayCompany ?? openLinkedCompany);
        RefreshTallyCommandStates();
        OnPropertyChanged(nameof(IsTallyBusy));
    }

    private static string BuildPendingLinkStatus(
        ClientProfile client,
        TallyClientLink? existingLink,
        TallyCompanyInfo selected)
    {
        if (selected.IsLinked && selected.LinkedClientId != client.Id)
        {
            var linkedClient = string.Join(
                " - ",
                new[] { selected.LinkedClientCode, selected.LinkedClientName }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            return linkedClient.Length > 0
                ? $"Cannot confirm • already linked to {linkedClient}"
                : "Cannot confirm • already linked to another client";
        }

        if (existingLink is not null &&
            !existingLink.CompanyKey.Equals(selected.CompanyKey, StringComparison.OrdinalIgnoreCase))
        {
            return $"Cannot confirm • unlink {existingLink.CompanyName} from this client first";
        }

        return "Pending confirmation in this dashboard";
    }

    private void ApplyCompanyIdentity(TallyCompanyInfo company)
    {
        TallyCompanyName = EmptyToDash(company.Name);
        TallyCompanyGstin = EmptyToDash(company.Gstin);
        TallyCompanyFinancialYear = EmptyToDash(company.FinancialYear);
        TallyCompanyState = EmptyToDash(company.StateName);
        TallyCompanyNumber = EmptyToDash(company.CompanyNumber);
        TallyCompanyBooksPeriod = BuildBooksPeriod(company.BooksFrom, company.StartingFrom);
    }

    private void ApplyLinkIdentity(TallyClientLink link, TallyCompanyInfo? openCompany)
    {
        TallyCompanyName = EmptyToDash(openCompany?.Name ?? link.CompanyName);
        TallyCompanyGstin = EmptyToDash(openCompany?.Gstin ?? link.Gstin);
        TallyCompanyFinancialYear = EmptyToDash(openCompany?.FinancialYear ?? link.FinancialYear);
        TallyCompanyState = EmptyToDash(openCompany?.StateName ?? link.StateName);
        TallyCompanyNumber = EmptyToDash(openCompany?.CompanyNumber ?? link.CompanyNumber);
        TallyCompanyBooksPeriod = BuildBooksPeriod(
            openCompany?.BooksFrom ?? link.BooksFrom,
            openCompany?.StartingFrom ?? link.StartingFrom);
    }

    private void ApplyNoCompanyIdentity()
    {
        TallyCompanyName = "—";
        TallyCompanyGstin = "—";
        TallyCompanyFinancialYear = "—";
        TallyCompanyState = "—";
        TallyCompanyBooksPeriod = "—";
        TallyCompanyNumber = "—";
        TallyLastSyncText = "Never synchronized";
        TallyLastError = string.Empty;
        TallyMetrics.Clear();
        TallyReportRows.Clear();
        TallyStatisticsMetrics.Clear();
    }

    private void ApplyEmptyTallySection()
    {
        HasPendingTallySelection = false;
        HasConfirmedTallyLink = false;
        CanConfirmTallyLink = false;
        CanCancelTallySelection = false;
        CanUnlinkTallyLink = false;
        CanImportBankRange = false;
        UnlinkConfirmationPending = false;
        TallyLinkStatus = "Open a client to link a Tally company.";
        TallyIdentityMatchText =
            "The global dropdown can show open Tally companies, but linking requires an active client.";
        TallyReportStatus = "Open a client and link Tally to view reports.";
        TallyStatisticsStatus = "Open a client and link Tally to view Statistics.";
        TallyExportStatus = "Open a client and link Tally before exporting vouchers.";
        TallyLiveXmlStatus = _tallyCoordinator.LiveXmlStatus;
        ApplyNoCompanyIdentity();
        _dateClientId = 0;
        _dateFinancialYear = string.Empty;
        BankTallyExportMonths.Clear();
        PurchaseTallyExportMonths.Clear();
        SalesTallyExportMonths.Clear();
        CashTallyExportMonths.Clear();
        JournalTallyExportMonths.Clear();
        _selectedBankExportMonth = null;
        _selectedPurchaseExportMonth = null;
        _selectedSalesExportMonth = null;
        _bankExportFromDate = null;
        _bankExportToDate = null;
        _purchaseExportFromDate = null;
        _purchaseExportToDate = null;
        _salesExportFromDate = null;
        _salesExportToDate = null;
        _cashExportFromDate = null;
        _cashExportToDate = null;
        _journalExportFromDate = null;
        _journalExportToDate = null;
        _includeBankStatement = false;
        _includePurchases = false;
        _includeSales = false;
        _includeCashEntries = false;
        _includeJournals = false;
        _monthRangeGestures.Clear();
        NotifyTallyExportAvailability();
        OnPropertyChanged(nameof(SelectedBankExportMonth));
        OnPropertyChanged(nameof(SelectedPurchaseExportMonth));
        OnPropertyChanged(nameof(SelectedSalesExportMonth));
        OnPropertyChanged(nameof(BankExportFromDate));
        OnPropertyChanged(nameof(BankExportToDate));
        OnPropertyChanged(nameof(PurchaseExportFromDate));
        OnPropertyChanged(nameof(PurchaseExportToDate));
        OnPropertyChanged(nameof(SalesExportFromDate));
        OnPropertyChanged(nameof(SalesExportToDate));
        OnPropertyChanged(nameof(CashExportFromDate));
        OnPropertyChanged(nameof(CashExportToDate));
        OnPropertyChanged(nameof(JournalExportFromDate));
        OnPropertyChanged(nameof(JournalExportToDate));
        OnPropertyChanged(nameof(BankTallyFromDate));
        OnPropertyChanged(nameof(BankTallyToDate));
        OnPropertyChanged(nameof(IncludeBankStatement));
        OnPropertyChanged(nameof(IncludePurchases));
        OnPropertyChanged(nameof(IncludeSales));
        OnPropertyChanged(nameof(IncludeCashEntries));
        OnPropertyChanged(nameof(IncludeJournals));
        RefreshTallyCommandStates();
        OnPropertyChanged(nameof(IsTallyBusy));
    }

    private void RefreshTallyMetrics()
    {
        var selected = _tallyCoordinator.SelectedCompany;
        var link = _tallyCoordinator.GetCurrentClientLink();
        var openLinkedCompany = link is null
            ? null
            : _tallyCoordinator.OpenCompanies.FirstOrDefault(company =>
                company.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase));
        RefreshTallyMetrics(
            link ?? (selected is null ? null : LinkPreview(selected)),
            selected ?? openLinkedCompany);
    }

    private void RefreshTallyMetrics(TallyClientLink? link, TallyCompanyInfo? liveCompany)
    {
        TallyMetrics.Clear();
        if (!IsLocalTallyReport)
        {
            return;
        }

        if (link is null)
        {
            TallyMetrics.Add(new TallyDashboardMetric(
                "Status",
                "Not linked",
                "Choose a company in the top dropdown and confirm it here."));
            return;
        }

        switch (SelectedTallyReport)
        {
            case "Master Statistics":
                AddMetric("Total masters", link.MasterCount);
                AddMetric("Groups", liveCompany?.GroupCount ?? link.GroupCount);
                AddMetric("Ledgers", liveCompany?.LedgerCount ?? link.LedgerCount);
                AddMetric("Voucher types", liveCompany?.VoucherTypeCount ?? link.VoucherTypeCount);
                AddMetric("Stock items", liveCompany?.StockItemCount ?? link.StockItemCount);
                AddMetric("Units", liveCompany?.UnitCount ?? link.UnitCount);
                AddMetric("Godowns", liveCompany?.GodownCount ?? link.GodownCount);
                AddMetric("Duplicate masters", link.DuplicateMasterCount);
                AddMetric("Master conflicts", link.ConflictingMasterCount);
                break;

            case "Voucher Statistics":
                AddMetric("Indexed vouchers", liveCompany?.VoucherCount ?? link.VoucherCount);
                AddMetric("Duplicate references", link.DuplicateVoucherCount);
                AddMetric("Conflicting references", link.ConflictingVoucherCount);
                TallyMetrics.Add(new TallyDashboardMetric(
                    "Last report refresh",
                    FormatLocalDate(link.LastReportRefreshUtc),
                    link.LastSyncStatus));
                break;

            case "Duplicate Control":
                AddMetric("Duplicate masters", link.DuplicateMasterCount,
                    "Repeated master type and name combinations in the latest Tally response.");
                AddMetric("Master conflicts", link.ConflictingMasterCount,
                    "Same master identity returned with different parent or company details.");
                AddMetric("Duplicate vouchers", link.DuplicateVoucherCount,
                    "Repeated voucher identity/reference in the linked Tally company.");
                AddMetric("Voucher conflicts", link.ConflictingVoucherCount,
                    "Same voucher identity/reference with different content.");
                break;

            default:
                TallyMetrics.Add(new TallyDashboardMetric(
                    "Company",
                    EmptyToDash(link.CompanyName),
                    $"Company No. {EmptyToDash(link.CompanyNumber)}"));
                TallyMetrics.Add(new TallyDashboardMetric(
                    "GSTIN",
                    EmptyToDash(link.Gstin),
                    $"State: {EmptyToDash(link.StateName)}"));
                TallyMetrics.Add(new TallyDashboardMetric(
                    "Financial year",
                    EmptyToDash(link.FinancialYear),
                    BuildBooksPeriod(link.BooksFrom, link.StartingFrom)));
                TallyMetrics.Add(new TallyDashboardMetric(
                    "Connection",
                    liveCompany is null ? "Not currently open" : "Live",
                    _tallyCoordinator.ConnectionStatus));
                TallyMetrics.Add(new TallyDashboardMetric(
                    "Last synchronization",
                    FormatLocalDate(link.LastMasterSyncUtc),
                    link.LastSyncStatus));
                break;
        }
    }

    private void AddMetric(string label, int value, string detail = "") =>
        TallyMetrics.Add(new TallyDashboardMetric(label, value.ToString("N0"), detail));

    private bool CanRefreshLiveTallyReport() =>
        IsLiveTallyReport &&
        HasConfirmedTallyLink &&
        !_tallyCoordinator.IsBusy &&
        !IsTallyReportBusy;

    private bool CanRefreshTallyStatistics() =>
        HasConfirmedTallyLink &&
        !_tallyCoordinator.IsBusy &&
        !IsTallyStatisticsBusy;

    private bool CanExportSelectedToTally()
    {
        if (!_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.TallyExport))
        {
            return false;
        }
        if (!HasConfirmedTallyLink || _tallyCoordinator.IsBusy)
        {
            return false;
        }
        if (!IncludeBankStatement && !IncludePurchases && !IncludeSales &&
            !IncludeCashEntries && !IncludeJournals)
        {
            return false;
        }

        if (IncludeBankStatement && !TryResolveModuleRanges(
                BankExportPeriodMode,
                BankTallyExportMonths,
                BankExportFromDate,
                BankExportToDate,
                "Bank Statement",
                out _,
                out _))
        {
            return false;
        }
        if (IncludePurchases && !TryResolveModuleRanges(
                PurchaseExportPeriodMode,
                PurchaseTallyExportMonths,
                PurchaseExportFromDate,
                PurchaseExportToDate,
                "Purchase",
                out _,
                out _))
        {
            return false;
        }
        if (IncludeSales && !TryResolveModuleRanges(
                SalesExportPeriodMode,
                SalesTallyExportMonths,
                SalesExportFromDate,
                SalesExportToDate,
                "Sales",
                out _,
                out _))
        {
            return false;
        }
        if (IncludeCashEntries && !TryResolveModuleRanges(
                CashExportPeriodMode,
                CashTallyExportMonths,
                CashExportFromDate,
                CashExportToDate,
                "Cash Entry",
                out _,
                out _))
        {
            return false;
        }
        if (IncludeJournals && !TryResolveModuleRanges(
                JournalExportPeriodMode,
                JournalTallyExportMonths,
                JournalExportFromDate,
                JournalExportToDate,
                "Journal",
                out _,
                out _))
        {
            return false;
        }
        return true;
    }

    private async Task ConfirmTallyLinkAsync()
    {
        try
        {
            TallyActionStatus = "Confirming the selected Tally company link…";
            UnlinkConfirmationPending = false;
            await _tallyCoordinator.ConfirmSelectedCompanyAsync();
            TallyActionStatus = _tallyCoordinator.LastOperationStatus;
            await RefreshTallyReportsAfterConnectionAsync();
        }
        catch (Exception exception)
        {
            TallyActionStatus = $"Tally link was not confirmed: {exception.Message}";
        }
        finally
        {
            RefreshTallySection();
        }
    }

    private void CancelTallySelection()
    {
        _tallyCoordinator.CancelPendingSelection();
        UnlinkConfirmationPending = false;
        RefreshTallySection();
    }

    private void RequestUnlinkTally()
    {
        UnlinkConfirmationPending = true;
        TallyActionStatus =
            "Confirm unlink below. Existing masters, accounting data, import history and logs will be retained.";
    }

    private void ConfirmUnlinkTally()
    {
        _tallyCoordinator.UnlinkCurrentClient();
        UnlinkConfirmationPending = false;
        TallyReportRows.Clear();
        TallyStatisticsMetrics.Clear();
        TallyReportStatus = "Tally company unlinked.";
        TallyStatisticsStatus = "Tally company unlinked.";
        RefreshTallySection();
    }

    private void CancelUnlinkTally()
    {
        UnlinkConfirmationPending = false;
        TallyActionStatus = _tallyCoordinator.LastOperationStatus;
    }

    private async Task RefreshTallyAsync()
    {
        try
        {
            TallyActionStatus = "Refreshing Tally connection and rebuilding the full active-FY authority…";
            await _tallyCoordinator.RefreshNowAsync(forceLinkedSynchronization: true);
            TallyActionStatus = _tallyCoordinator.LastOperationStatus;
        }
        catch (Exception exception)
        {
            TallyActionStatus = $"Tally refresh could not complete: {exception.Message}";
        }
        finally
        {
            RefreshTallySection();
        }
    }

    private Task RefreshTallyReportsAfterConnectionAsync()
    {
        // Fix37: connection/link/export/background synchronization never requests
        // Statistics or supporting reports automatically. Reports are explicit/lazy
        // only through the report controls below.
        TallyStatisticsStatus = "Tally Statistics is on-demand. Use Refresh Statistics to request it.";
        TallyReportStatus = IsLiveTallyReport
            ? "Live Tally report is on-demand. Use Refresh Report to request it."
            : TallyReportStatus;
        return Task.CompletedTask;
    }

    private async Task RefreshSelectedTallyReportAsync()
    {
        if (IsTallyReportBusy)
        {
            return;
        }
        if (!IsLiveTallyReport)
        {
            RefreshTallyMetrics();
            return;
        }

        var client = _clientContext.CurrentClient;
        if (client is null || _tallyCoordinator.GetCurrentClientLink() is null)
        {
            TallyReportStatus = "Open a client and confirm a Tally company link first.";
            return;
        }

        IsTallyReportBusy = true;
        TallyReportStatus = $"Reading {SelectedTallyReport} from Tally…";
        try
        {
            var (from, to) = TallyLiveConnectionCoordinator.FinancialYearDates(client.FinancialYear);
            var snapshot = await _tallyCoordinator.GetReportAsync(SelectedTallyReport, from, to);
            TallyReportRows.Clear();
            foreach (var row in snapshot.Rows)
            {
                TallyReportRows.Add(row);
            }
            TallyReportStatus = snapshot.IsAvailable
                ? $"{snapshot.Status} Period {from:dd.MM.yyyy} to {to:dd.MM.yyyy}."
                : $"{snapshot.Status} {snapshot.Error}".Trim();
        }
        catch (Exception exception)
        {
            TallyReportRows.Clear();
            TallyReportStatus = $"{SelectedTallyReport} could not be read: {exception.Message}";
        }
        finally
        {
            IsTallyReportBusy = false;
        }
    }

    private async Task RefreshTallyStatisticsAsync()
    {
        if (IsTallyStatisticsBusy)
        {
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null || _tallyCoordinator.GetCurrentClientLink() is null)
        {
            TallyStatisticsStatus = "Open a client and confirm a Tally company link first.";
            return;
        }

        IsTallyStatisticsBusy = true;
        TallyStatisticsStatus = "Reading Statistics from Tally…";
        try
        {
            var (from, to) = TallyLiveConnectionCoordinator.FinancialYearDates(client.FinancialYear);
            var snapshot = await _tallyCoordinator.GetReportAsync("Statistics", from, to);
            TallyStatisticsMetrics.Clear();
            foreach (var row in snapshot.Rows.Take(30))
            {
                TallyStatisticsMetrics.Add(new TallyDashboardMetric(
                    EmptyToDefault(row.Particulars, "Statistic"),
                    FormatReportRowValue(row),
                    row.Detail));
            }
            if (TallyStatisticsMetrics.Count == 0 && snapshot.IsAvailable)
            {
                TallyStatisticsMetrics.Add(new TallyDashboardMetric(
                    "Statistics response",
                    $"{snapshot.ResponseCharacters:N0} characters",
                    "Tally returned the Statistics report, but no standard display rows were present."));
            }
            TallyStatisticsStatus = snapshot.IsAvailable
                ? snapshot.Status
                : $"{snapshot.Status} {snapshot.Error}".Trim();
        }
        catch (Exception exception)
        {
            TallyStatisticsMetrics.Clear();
            TallyStatisticsStatus = $"Statistics could not be read: {exception.Message}";
        }
        finally
        {
            IsTallyStatisticsBusy = false;
        }
    }

    private static string FormatReportRowValue(TallyReportRow row)
    {
        if (row.Debit != 0m || row.Credit != 0m)
        {
            return $"Dr {row.Debit:N2} • Cr {row.Credit:N2}";
        }
        if (row.Amount != 0m)
        {
            return row.Amount.ToString("N2", CultureInfo.InvariantCulture);
        }
        return "Available";
    }

    private void ClearAllData()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var progress = _automationService.GetClientProgress(client.Id, client.FinancialYear);
        if (progress.IsActive)
        {
            System.Windows.MessageBox.Show(
                "Auto Import is currently active for this Client/FY. Wait for the current file batch to finish before clearing accounting data.",
                "Clear All Data",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var message = $"Clear ALL accounting/module data for {client.ClientName} • FY {client.FinancialYear}?\n\n" +
                      "This removes imported Banking, Purchase, Sales, Journal, GST, Masters/Stock, Cash, mappings, reconciliation, import history and generated accounting state for this Client/FY.\n\n" +
                      "Standard Tally Groups, Cash and Profit & Loss A/c will be restored. Client profile, Settings, Automation Folder link, Tally company link and source documents are preserved. This action cannot be undone.";
        var answer = System.Windows.MessageBox.Show(
            message,
            "Clear All Data",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            var result = new ClientYearDataResetService().Clear(client.Id, client.FinancialYear);
            _workspaceStateCache.Invalidate(client.Id, client.FinancialYear);
            foreach (var area in new[]
                     {
                         AccountingLiveArea.Banking, AccountingLiveArea.Cash, AccountingLiveArea.Purchase, AccountingLiveArea.Sales,
                         AccountingLiveArea.Journal, AccountingLiveArea.Gst, AccountingLiveArea.Masters, AccountingLiveArea.StockMasters
                     })
                _liveChangeHub.Publish(client.Id, client.FinancialYear, area, "Dashboard Clear All Data");

            Refresh();
            DiagnosticExportStatus = $"Cleared {result.DeletedRows:N0} accounting/module row(s) for {client.ClientName} • FY {client.FinancialYear}. Restored {result.StandardMastersRestored:N0} standard Groups/Ledgers. Client/Settings/folder/Tally-link authorities were preserved.";
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(exception.Message, "Clear All Data", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private bool CanOpenLinkedFolder() =>
        _clientContext.CurrentClient is not null;

    private void OpenLinkedFolder()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var folder = _automationService.GetFolders()
            .Where(item => item.ClientId == client.Id &&
                           item.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => Directory.Exists(item.FolderPath))
            .ThenBy(item => item.FolderPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (folder is not null && Directory.Exists(folder.FolderPath))
        {
            _automationService.OpenFolder(folder);
            DiagnosticExportStatus = $"Opened linked folder: {folder.FolderPath}";
            return;
        }

        if (folder is null)
        {
            var linkNow = System.Windows.MessageBox.Show(
                $"No Automation Folder is linked to {client.ClientName} • FY {client.FinancialYear}.\n\nLink a folder now?",
                "Link Automation Folder",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (linkNow != System.Windows.MessageBoxResult.Yes)
            {
                DiagnosticExportStatus = "No linked folder. Use Open / Link Folder whenever you are ready to link one.";
                return;
            }
        }

        var dialog = new OpenFolderDialog
        {
            Title = folder is null
                ? $"Link Automation Folder for {client.ClientName} • FY {client.FinancialYear}"
                : $"Locate moved Automation Folder for {client.ClientName} • FY {client.FinancialYear}",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            DiagnosticExportStatus = folder is null
                ? "Folder linking was cancelled."
                : "Moved-folder location was not selected; the previous link was left unchanged.";
            return;
        }

        try
        {
            var linked = _automationService.LinkOrRecoverFolderToExistingClient(dialog.FolderName, client.Id);
            _automationService.OpenFolder(linked);
            DiagnosticExportStatus = folder is null
                ? $"Linked and opened folder: {linked.FolderPath}"
                : $"Recovered moved linked folder and opened it: {linked.FolderPath}";
            OpenLinkedFolderCommand.RaiseCanExecuteChanged();
        }
        catch (Exception exception)
        {
            DiagnosticExportStatus = $"The folder could not be linked: {exception.Message}";
            System.Windows.MessageBox.Show(
                exception.Message,
                "Link Automation Folder",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private void OpenTallyDiagnosticFolder()
    {
        var path = _tallyCoordinator.LastDiagnosticReportPath;
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path))) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            TallyExportStatus = $"Could not open the Tally diagnostics: {exception.Message}";
        }
    }

    // Fix17: Auto Sync adds automatic selection to existing manual Export Selected.
    // Both settings and visible tile checkboxes share one module selection only
    // while outbound automatic sync is enabled. Inbound Auto Tally Import is NOT
    // an outbound write permission, and its five-second heartbeat is unchanged.
    private void SyncManualTileSelectionFromAutoSettings()
    {
        if (_loadingExportPreferences || !IsTallyAutoSyncEnabled || _clientContext.CurrentClient is null) return;
        // Fix30: restoring saved automatic selection on Client/FY open must be
        // presentation-only. The Include* property setters normally announce an
        // explicit USER selection via ApplyManualTileToAutoSetting -> queue a Tally
        // export. Suppress those writes while copying saved choices to the tile;
        // explicit toggle/period changes and committed accounting edits still queue.
        var wasLoading = _loadingExportPreferences;
        _loadingExportPreferences = true;
        try
        {
            IncludeBankStatement = AutoSyncBank;
            IncludePurchases = AutoSyncPurchase;
            IncludeSales = AutoSyncSales;
            IncludeCashEntries = AutoSyncCash;
            IncludeJournals = AutoSyncJournal;
        }
        finally { _loadingExportPreferences = wasLoading; }
        RefreshAutoSyncTileIndicators();
    }

    private void ApplyAutoSettingToManualTile(string module, bool enabled)
    {
        if (_loadingExportPreferences || !IsTallyAutoSyncEnabled) return;
        switch (module)
        {
            case "Bank": IncludeBankStatement = enabled; break;
            case "Purchase": IncludePurchases = enabled; break;
            case "Sales": IncludeSales = enabled; break;
            case "Cash": IncludeCashEntries = enabled; break;
            case "Journal": IncludeJournals = enabled; break;
        }
        QueueExistingEligibleTallyExport();
    }

    private void ApplyManualTileToAutoSetting(string module, bool enabled)
    {
        if (_loadingExportPreferences || !IsTallyAutoSyncEnabled) return;
        switch (module)
        {
            case "Bank": AutoSyncBank = enabled; break;
            case "Purchase": AutoSyncPurchase = enabled; break;
            case "Sales": AutoSyncSales = enabled; break;
            case "Cash": AutoSyncCash = enabled; break;
            case "Journal": AutoSyncJournal = enabled; break;
        }
        QueueExistingEligibleTallyExport();
    }

    private void RefreshAutoSyncTileIndicators() => RefreshTallyExportSummaryState();

    // Fix26: this signature is a LOCAL, read-only wake-up gate, NOT another
    // duplicate authority or export writer. The common exporter remains the only
    // source of truth for real Tally CREATE/ALTER/RECREATE classification.
    // IMPORTANT: never include imported status, Tally MASTERID/ALTERID, or cache
    // refresh timestamps: those can change after a successful write without any
    // change in the user's desired voucher.
    private string? GetAutomaticSourceRevision(
        ClientProfile client, int moduleBits,
        IReadOnlyList<TallyVoucherExportRange> bankRanges,
        IReadOnlyList<TallyVoucherExportRange> purchaseRanges,
        IReadOnlyList<TallyVoucherExportRange> salesRanges,
        IReadOnlyList<TallyVoucherExportRange> cashRanges,
        IReadOnlyList<TallyVoucherExportRange> journalRanges,
        out bool confirmedDeletedRecreateReady)
    {
        confirmedDeletedRecreateReady = false;
        // Fix28: the fast snapshot is a PAINT cache, not committed change evidence.
        // On a changed source its old data remains visible while module workers
        // reload SQLite. Comparing that old snapshot would consume a fresh edit,
        // or later mistake its delayed publication for another new ALTER.
        // Only selected source/mapping streams need to be authoritative; this
        // does not delay unrelated modules, change the fast-paint flow, or query Tally.
        var required = new HashSet<ClientWorkspaceModule>();
        if ((moduleBits & AutoSyncBankMask) != 0)
        {
            required.Add(ClientWorkspaceModule.Banking);
            required.Add(ClientWorkspaceModule.LedgerMapping);
            required.Add(ClientWorkspaceModule.Masters);
        }
        if ((moduleBits & AutoSyncPurchaseMask) != 0)
        {
            required.Add(ClientWorkspaceModule.Purchase);
            required.Add(ClientWorkspaceModule.Stock);
            required.Add(ClientWorkspaceModule.Masters);
        }
        if ((moduleBits & AutoSyncSalesMask) != 0)
        {
            required.Add(ClientWorkspaceModule.Sales);
            required.Add(ClientWorkspaceModule.Stock);
            required.Add(ClientWorkspaceModule.Masters);
        }
        if ((moduleBits & AutoSyncCashMask) != 0)
        {
            required.Add(ClientWorkspaceModule.Cash);
            required.Add(ClientWorkspaceModule.Masters);
        }
        if ((moduleBits & AutoSyncJournalMask) != 0)
        {
            required.Add(ClientWorkspaceModule.Journal);
            required.Add(ClientWorkspaceModule.Masters);
        }
        if (required.Any(module => !_workspaceStateCache.IsModuleAuthoritativeReady(client.Id, client.FinancialYear, module)) ||
            !_workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var snapshot))
            return null; // Queue remains pending; do not open or interrogate Tally.
        static bool Selected(DateTime? date, IReadOnlyList<TallyVoucherExportRange> ranges) =>
            date.HasValue && ranges.Any(range => range.Contains(date.Value));
        var work = new StringBuilder();
        void Add(string label, object values)
        {
            work.Append(label).Append(':').Append(JsonSerializer.Serialize(values)).Append('\n');
        }
        Add("Selection", new
        {
            client.Id, client.FinancialYear, moduleBits,
            Bank = bankRanges.Select(r => new { r.FromDate, r.ToDate }).ToArray(),
            Purchase = purchaseRanges.Select(r => new { r.FromDate, r.ToDate }).ToArray(),
            Sales = salesRanges.Select(r => new { r.FromDate, r.ToDate }).ToArray(),
            Cash = cashRanges.Select(r => new { r.FromDate, r.ToDate }).ToArray(),
            Journal = journalRanges.Select(r => new { r.FromDate, r.ToDate }).ToArray(),
            AutoSyncBankImportSuspense, AutoSyncRecreate
        });
        if ((moduleBits & AutoSyncBankMask) != 0)
        {
            Add("Bank", snapshot.BankTransactions.Where(row =>
                !row.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase) &&
                Selected(row.TransactionDate, bankRanges)).OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.TransactionId, row.TransactionDate,
                    row.BankAccountId, row.Debit, row.Credit, row.Mode, row.LedgerName,
                    row.ReferenceNumber, row.Narration }).ToArray());
            Add("BankAccounts", snapshot.BankAccounts.OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.BankName, row.AccountNumber,
                    row.LedgerName, row.AccountType }).ToArray());
            Add("BankMappings", snapshot.BankLedgerMappings.OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.LedgerName, row.MasterGroup,
                    row.BankFilter, row.SummaryName, row.SourceLedgerName,
                    row.VoucherType, row.NarrationPattern }).ToArray());
            Add("BankTransfers", snapshot.BankTransfers.OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.SourceTransactionId, row.TargetTransactionId,
                    row.Amount, row.MatchId, row.ChargeAmount }).ToArray());
        }
        if ((moduleBits & AutoSyncPurchaseMask) != 0)
            Add("Purchase", snapshot.Purchases.Where(row => Selected(row.GstDate ?? row.DocumentDate, purchaseRanges))
                .OrderBy(row => row.PersistentId).Select(row => new
                { row.PersistentId, row.Destination, row.SupplierName, row.SupplierGstin,
                  row.DocumentType, row.DocumentNumber, row.DocumentDate, row.GstDate,
                  row.DocumentValue, row.TaxableValue, row.Igst, row.Cgst, row.Sgst, row.Cess,
                  row.RoundOff, row.PurchaseLedger, row.IgstLedger, row.CgstLedger,
                  row.SgstLedger, row.GstApplicability, row.Narration, row.ItemName,
                  row.ItemPurchaseLedger, row.ItemAmount, row.Unit, row.BilledQuantity,
                  row.HsnCode, row.ActualQuantity, row.ItemRate, row.AccountingAmount,
                  row.Godown, row.BatchName, row.DiscountAmount }).ToArray());
        if ((moduleBits & AutoSyncSalesMask) != 0)
            Add("Sales", snapshot.Sales.Where(row => Selected(row.GstDate ?? row.DocumentDate, salesRanges))
                .OrderBy(row => row.PersistentId).Select(row => new
                { row.PersistentId, row.Destination, row.CustomerName, row.CustomerGstin,
                  row.DocumentType, row.DocumentNumber, row.DocumentDate, row.GstDate,
                  row.DocumentValue, row.TaxableValue, row.Igst, row.Cgst, row.Sgst, row.Cess,
                  row.RoundOff, row.SalesLedger, row.IgstLedger, row.CgstLedger,
                  row.SgstLedger, row.Narration, row.ItemName, row.ItemSalesLedger,
                  row.ItemAmount, row.Unit, row.BilledQuantity, row.HsnCode,
                  row.ActualQuantity, row.ItemRate, row.AccountingAmount,
                  row.Godown, row.BatchName, row.DiscountAmount }).ToArray());
        if ((moduleBits & AutoSyncCashMask) != 0)
            Add("Cash", snapshot.CashEntries.Where(row => Selected(row.EntryDate, cashRanges))
                .OrderBy(row => row.Id).Select(row => new
                { row.Id, row.EntryId, row.EntryDate, row.EntryLedger, row.CommonLedger,
                  row.MappedMasterLedger, row.VoucherType, row.ReferenceNumber,
                  row.Debit, row.Credit, row.Narration }).ToArray());
        if ((moduleBits & AutoSyncJournalMask) != 0)
            Add("Journal", snapshot.Journals.Where(row => Selected(row.VoucherDate, journalRanges))
                .OrderBy(row => row.PersistentId).Select(row => new
                { row.PersistentId, row.VoucherDate, row.VoucherNumber, row.VoucherType,
                  row.Narration, row.ReferenceNumber,
                  row.OutputIgst, row.OutputCgst, row.OutputSgst, row.InputIgst,
                  row.InputCgst, row.InputSgst,
                  Lines = row.Lines.OrderBy(line => line.LineNumber)
                      .Select(line => new { line.PersistentId, line.LineNumber,
                          line.LedgerName, line.Amount, line.DrCr,
                          line.TaxHead, line.LineRole }).ToArray() }).ToArray());
        if (AutoSyncRecreate && _tallyCoordinator.GetCurrentClientLink() is { } recoveryLink &&
            !string.IsNullOrWhiteSpace(recoveryLink.CompanyKey))
        {
            var recoveries = _tallyRepository.GetVoucherRecoveryCandidates(
                client.Id, client.FinancialYear, recoveryLink.CompanyKey)
                .Where(candidate => candidate.State.Equals("DeletedInTally", StringComparison.OrdinalIgnoreCase) &&
                    candidate.PreviousMasterId.Length > 0 && candidate.SourceAnchor.Length > 0 &&
                    candidate.VerifiedVersion.Length > 0 &&
                    (candidate.EntityType switch
                    {
                        "Bank Voucher" => Selected(candidate.VoucherDate, bankRanges),
                        "Purchase Voucher" => Selected(candidate.VoucherDate, purchaseRanges),
                        "Sales Voucher" => Selected(candidate.VoucherDate, salesRanges),
                        "Cash Voucher" => Selected(candidate.VoucherDate, cashRanges),
                        "Manual Journal" or "GST Journal" => Selected(candidate.VoucherDate, journalRanges),
                        _ => false
                    }))
                .OrderBy(candidate => candidate.EntityType)
                .ThenBy(candidate => candidate.SourceAnchor)
                .Select(candidate => new { candidate.EntityType, candidate.SourceAnchor,
                    candidate.PreviousMasterId, candidate.VerifiedVersion }).ToArray();
            confirmedDeletedRecreateReady = recoveries.Length > 0;
            Add("ConfirmedDeletedOnly", recoveries);
        }
        // Local edits to Ledger/Group authority can require a same-MASTERID ALTER
        // even when there is no new voucher. Tally-owned identity/checkpoint data
        // is deliberately excluded, otherwise every heartbeat could relaunch Tally.
        if (AutoSyncMasterChanges)
            Add("Masters", snapshot.Masters.Where(row => row.IsUserEdited && !row.IsArchived)
                .OrderBy(row => row.PersistentId).Select(row => new
                { row.PersistentId, row.Name, row.Parent, row.Type,
                  row.OpeningBalance, row.BaseUnit }).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(work.ToString())));
    }

    // The per-Client/FY/company selection SCOPE is distinct from the desired source
    // revision. Changing selected months alone must not be classified as an ALTER of
    // previously imported vouchers. Each scope remembers its own last comparison.
    private string AutoSyncRevisionPreferenceKey(
        ClientProfile client, string companyKey, int moduleBits,
        IReadOnlyList<TallyVoucherExportRange> bankRanges,
        IReadOnlyList<TallyVoucherExportRange> purchaseRanges,
        IReadOnlyList<TallyVoucherExportRange> salesRanges,
        IReadOnlyList<TallyVoucherExportRange> cashRanges,
        IReadOnlyList<TallyVoucherExportRange> journalRanges)
    {
        static string Dates(IReadOnlyList<TallyVoucherExportRange> ranges) =>
            string.Join(",", ranges.Select(range =>
                $"{range.FromDate:yyyyMMdd}-{range.ToDate:yyyyMMdd}"));
        var scope = string.Join("|", companyKey.Trim(), client.FinancialYear.Trim(),
            moduleBits, Dates(bankRanges), Dates(purchaseRanges), Dates(salesRanges),
            Dates(cashRanges), Dates(journalRanges), AutoSyncBankImportSuspense,
            AutoSyncRecreate, AutoSyncMasterChanges);
        return $"TallyExport.{client.FinancialYear.Trim()}.AutoSync.LastClassifiedRevision." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..24];
    }

    private void QueueExistingEligibleTallyExport()
    {
        if (_disposed || !IsTallyAutoSyncEnabled || _loadingExportPreferences ||
            _clientContext.CurrentClient is not { } client ||
            !IsTallyExportPeriodMatrixInitialized(client)) return;
        var mask = 0;
        if (AutoSyncBank && IncludeBankStatement) mask |= AutoSyncBankMask;
        if (AutoSyncPurchase && IncludePurchases) mask |= AutoSyncPurchaseMask;
        if (AutoSyncSales && IncludeSales) mask |= AutoSyncSalesMask;
        if (AutoSyncCash && IncludeCashEntries) mask |= AutoSyncCashMask;
        if (AutoSyncJournal && IncludeJournals) mask |= AutoSyncJournalMask;
        if (AutoSyncMasterChanges && mask != 0) mask |= AutoSyncMastersMask;
        if (mask == 0) return;
        Interlocked.Or(ref _autoTallySyncPendingMask, mask);
        Interlocked.Or(ref _autoTallySyncExplicitSelectionMask, mask);
        ScheduleTallyAutoSync();
    }

    // Auto-only: 'Clear' means no automatic export. Manual Export deliberately
    // keeps the existing zero-selected-month => all available fallback.
    private static bool TryResolveAutomaticModuleRanges(
        string mode, IEnumerable<TallyExportMonthOption> months,
        DateTime? from, DateTime? to, string module,
        out IReadOnlyList<TallyVoucherExportRange> ranges, out string error)
    {
        if (mode.Equals("Month", StringComparison.OrdinalIgnoreCase) && !months.Any(month => month.IsSelected))
        {
            ranges = [];
            error = $"Auto Sync: no {module} months are selected. Manual Export remains unchanged.";
            return false;
        }
        return TryResolveModuleRanges(mode, months, from, to, module, out ranges, out error);
    }

    private const int AutoSyncBankMask = 1;
    private const int AutoSyncPurchaseMask = 2;
    private const int AutoSyncSalesMask = 4;
    private const int AutoSyncCashMask = 8;
    private const int AutoSyncJournalMask = 16;
    private const int AutoSyncMastersMask = 32;

    private void QueueTallyAutoSync(AccountingLiveChange change)
    {
        if (_disposed || IsTallyAutoSyncOff) return;
        // Tally-originated baseline/delta publications must never echo back as writes.
        if (change.Reason.Contains("Tally", StringComparison.OrdinalIgnoreCase) ||
            change.Reason.Contains("ALTERID", StringComparison.OrdinalIgnoreCase)) return;

        // FIXED31 Banking Auto Import may trigger the Tally queue immediately. The Bank
        // exporter remains the per-row accounting barrier: mapped rows require Final Ledger;
        // when Import Suspense is enabled, only Auto Import intake may stage unmapped rows
        // against the dedicated Suspense Ledger. Manual Import intake remains unchanged and
        // still waits for a later Banking accounting/mapping change.
        if (change.Area == AccountingLiveArea.Banking && IsBankingIntakeOnlyAutoSyncChange(change.Reason) &&
            !IsBankingAutoImportCommitAutoSyncChange(change.Reason)) return;

        var mask = change.Area switch
        {
            AccountingLiveArea.Banking => AutoSyncBankMask,
            AccountingLiveArea.Purchase => AutoSyncPurchaseMask,
            AccountingLiveArea.Sales => AutoSyncSalesMask,
            AccountingLiveArea.Cash => AutoSyncCashMask,
            AccountingLiveArea.Journal or AccountingLiveArea.Gst => AutoSyncJournalMask,
            AccountingLiveArea.Masters or AccountingLiveArea.StockMasters => AutoSyncMastersMask,
            _ => 0
        };
        if (mask == 0) return;
        Interlocked.Or(ref _autoTallySyncChangedMask, mask);
        Interlocked.Or(ref _autoTallySyncPendingMask, mask);
        // Diagnostics record the source of an automatic request, not a timer
        // heartbeat or a post-import import-status/cache presentation change.
        AutomationDiagnosticLog.Write("TALLY-AUTO-SOURCE-SIGNAL",
            $"Committed {change.Area}: {change.Reason}; only the current selected scope may run.",
            _clientContext.CurrentClient?.ClientCode);
        ScheduleTallyAutoSync();
    }


    private static bool IsBankingIntakeOnlyAutoSyncChange(string? reason)
    {
        var text = (reason ?? string.Empty).Trim();
        if (text.Length == 0) return false;
        return text.Contains("committed Banking", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Bank Statement deleted", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Bank balance evidence", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Import run rolled back", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBankingAutoImportCommitAutoSyncChange(string? reason)
    {
        var text = (reason ?? string.Empty).Trim();
        return text.Contains("Auto Import committed Banking", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleTallyAutoSync()
    {
        if (_disposed || IsTallyAutoSyncOff || Volatile.Read(ref _autoTallySyncPendingMask) == 0) return;
        // One non-sliding dispatch for a burst of committed source changes. A newer
        // modification cannot repeatedly restart a 1.5-second debounce timer.
        // The existing in-flight export gate still serializes every pass.
        if (Interlocked.CompareExchange(ref _autoTallySyncScheduleQueued, 1, 0) != 0) return;
        var version = Volatile.Read(ref _autoTallySyncScheduleVersion);
        _ = Task.Run(async () =>
        {
            var queuedOnDispatcher = false;
            try
            {
                // Let the committing source event return; do not wait for a batch
                // size, a repeat-launch interval, or additional vouchers.
                await Task.Delay(100).ConfigureAwait(false);
                if (_disposed || version != Volatile.Read(ref _autoTallySyncScheduleVersion)) return;
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is null) return;
                _ = dispatcher.BeginInvoke(new Action(() =>
                {
                    // Release scheduling ownership on the UI thread BEFORE
                    // starting a pass: a concurrent event can queue the next one.
                    Interlocked.Exchange(ref _autoTallySyncScheduleQueued, 0);
                    if (!_disposed && !IsTallyAutoSyncOff &&
                        Volatile.Read(ref _autoTallySyncPendingMask) != 0)
                        _ = RunTallyAutoSyncAsync();
                }));
                queuedOnDispatcher = true;
            }
            catch { }
            finally
            {
                if (!queuedOnDispatcher) Interlocked.Exchange(ref _autoTallySyncScheduleQueued, 0);
            }
        });
    }

    private bool AutoSyncModuleEnabled(int mask) => IsTallyAutoSyncEnabled && (mask switch
    {
        AutoSyncBankMask => AutoSyncBank && IncludeBankStatement,
        AutoSyncPurchaseMask => AutoSyncPurchase && IncludePurchases,
        AutoSyncSalesMask => AutoSyncSales && IncludeSales,
        AutoSyncCashMask => AutoSyncCash && IncludeCashEntries,
        AutoSyncJournalMask => AutoSyncJournal && IncludeJournals,
        AutoSyncMastersMask => AutoSyncMasterChanges,
        _ => false
    });

    private async Task RunTallyAutoSyncAsync()
    {
        if (_disposed || IsTallyAutoSyncOff || Interlocked.CompareExchange(ref _autoTallySyncInFlight, 1, 0) != 0) return;
        var claimedPending = 0;
        var claimedChanged = 0;
        var claimedExplicit = 0;
        var enteredTallyExit = false;
        var completed = false;
        try
        {
            var client = _clientContext.CurrentClient;
            if (client is null) return;
            claimedPending = Interlocked.Exchange(ref _autoTallySyncPendingMask, 0);
            claimedChanged = Interlocked.Exchange(ref _autoTallySyncChangedMask, 0);
            claimedExplicit = Interlocked.Exchange(ref _autoTallySyncExplicitSelectionMask, 0);
            var pending = claimedPending;
            if (pending == 0) return;
            if (!IsTallyExportPeriodMatrixInitialized(client))
            {
                // Preserve the committed change until the prepared workspace has
                // supplied the actual visible module/month/date selection.
                QueueTallyExportPeriodRefresh(client);
                TallyAutoSyncStatus = "Auto Sync queued; waiting for selected period data to finish preparing.";
                return;
            }

            if (!_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.TallyExport))
            {
                TallyAutoSyncStatus = "Auto Sync waiting: this role does not have Tally Export permission.";
                return;
            }

            // The established export pipeline can classify mixed CREATE/ALTER work safely,
            // but it does not expose a dry-run action filter. If the user restricts one of
            // those action classes, fail closed rather than performing an unapproved write.
            if (!AutoSyncCreateMasters || !AutoSyncCreateVouchers || !AutoSyncAlterMasters || !AutoSyncAlterVouchers)
            {
                TallyAutoSyncStatus = "Auto Sync waiting: CREATE/ALTER action restrictions are enabled. Manual Export remains available for selective review.";
                return;
            }
            if (AutoSyncDelete)
            {
                // Deletion is intentionally never inferred by the automatic voucher path.
                // The switch is persisted separately for future explicit delete workflows.
                TallyAutoSyncStatus = "Auto Delete is enabled in settings, but automatic deletion remains fail-closed; CREATE/ALTER sync continues only.";
            }

            if (_tallyCoordinator.IsBusy)
            {
                TallyAutoSyncStatus = "Auto Sync waiting: Tally is busy. The queued change will retry on the next Tally-ready signal.";
                return;
            }

            var effective = pending;
            if (!AutoSyncModuleEnabled(AutoSyncBankMask)) effective &= ~AutoSyncBankMask;
            if (!AutoSyncModuleEnabled(AutoSyncPurchaseMask)) effective &= ~AutoSyncPurchaseMask;
            if (!AutoSyncModuleEnabled(AutoSyncSalesMask)) effective &= ~AutoSyncSalesMask;
            if (!AutoSyncModuleEnabled(AutoSyncCashMask)) effective &= ~AutoSyncCashMask;
            if (!AutoSyncModuleEnabled(AutoSyncJournalMask)) effective &= ~AutoSyncJournalMask;
            if (!AutoSyncModuleEnabled(AutoSyncMastersMask)) effective &= ~AutoSyncMastersMask;

            // A Master edit is synchronized through the established master-first stages of
            // enabled voucher modules; unreferenced Master-only changes remain queued until
            // an eligible dependent module is available, avoiding unsafe name-only ALTERs.
            if (effective == 0)
            {
                completed = true;
                TallyAutoSyncStatus = "Auto Sync ignored this change because its module is not selected.";
                return;
            }

            // Always classify the SAME selected Auto Import scope. The pending bit
            // selects the wake-up, not a different source identity per event.
            if (AutoSyncModuleEnabled(AutoSyncBankMask)) effective |= AutoSyncBankMask;
            if (AutoSyncModuleEnabled(AutoSyncPurchaseMask)) effective |= AutoSyncPurchaseMask;
            if (AutoSyncModuleEnabled(AutoSyncSalesMask)) effective |= AutoSyncSalesMask;
            if (AutoSyncModuleEnabled(AutoSyncCashMask)) effective |= AutoSyncCashMask;
            if (AutoSyncModuleEnabled(AutoSyncJournalMask)) effective |= AutoSyncJournalMask;

            if ((effective & AutoSyncMastersMask) != 0)
            {
                if (AutoSyncModuleEnabled(AutoSyncBankMask)) effective |= AutoSyncBankMask;
                if (AutoSyncModuleEnabled(AutoSyncPurchaseMask)) effective |= AutoSyncPurchaseMask;
                if (AutoSyncModuleEnabled(AutoSyncSalesMask)) effective |= AutoSyncSalesMask;
                if (AutoSyncModuleEnabled(AutoSyncCashMask)) effective |= AutoSyncCashMask;
                if (AutoSyncModuleEnabled(AutoSyncJournalMask)) effective |= AutoSyncJournalMask;
            }

            var moduleBits = effective & (AutoSyncBankMask | AutoSyncPurchaseMask | AutoSyncSalesMask | AutoSyncCashMask | AutoSyncJournalMask);
            if (moduleBits == 0)
            {
                TallyAutoSyncStatus = "Auto Sync queued a Master change; it will run through the next enabled dependent module so MASTERID-first safety is preserved.";
                return;
            }

            IReadOnlyList<TallyVoucherExportRange> bankRanges = [], purchaseRanges = [], salesRanges = [], cashRanges = [], journalRanges = [];
            if ((moduleBits & AutoSyncBankMask) != 0 &&
                !TryResolveAutomaticModuleRanges(BankExportPeriodMode, BankTallyExportMonths, BankExportFromDate, BankExportToDate, "Bank Statement", out bankRanges, out _))
                moduleBits &= ~AutoSyncBankMask;
            if ((moduleBits & AutoSyncPurchaseMask) != 0 &&
                !TryResolveAutomaticModuleRanges(PurchaseExportPeriodMode, PurchaseTallyExportMonths, PurchaseExportFromDate, PurchaseExportToDate, "Purchase", out purchaseRanges, out _))
                moduleBits &= ~AutoSyncPurchaseMask;
            if ((moduleBits & AutoSyncSalesMask) != 0 &&
                !TryResolveAutomaticModuleRanges(SalesExportPeriodMode, SalesTallyExportMonths, SalesExportFromDate, SalesExportToDate, "Sales", out salesRanges, out _))
                moduleBits &= ~AutoSyncSalesMask;
            if ((moduleBits & AutoSyncCashMask) != 0 &&
                !TryResolveAutomaticModuleRanges(CashExportPeriodMode, CashTallyExportMonths, CashExportFromDate, CashExportToDate, "Cash", out cashRanges, out _))
                moduleBits &= ~AutoSyncCashMask;
            if ((moduleBits & AutoSyncJournalMask) != 0 &&
                !TryResolveAutomaticModuleRanges(JournalExportPeriodMode, JournalTallyExportMonths, JournalExportFromDate, JournalExportToDate, "Journal", out journalRanges, out _))
                moduleBits &= ~AutoSyncJournalMask;
            if (moduleBits == 0)
            {
                completed = true;
                TallyAutoSyncStatus = "Auto Sync waiting: no selected modules have eligible selected months/date ranges.";
                return;
            }
            // Fix26: do the cheap LOCAL duplicate/revision check BEFORE waking Tally.
            // A repeated event, imported-status refresh or stale ready counter must
            // not relaunch Tally for an already-classified, unchanged source set.
            var revision = GetAutomaticSourceRevision(client, moduleBits,
                bankRanges, purchaseRanges, salesRanges, cashRanges, journalRanges,
                out var confirmedDeletedRecreateReady);
            if (revision is null)
            {
                TallyAutoSyncStatus = "Auto Sync waiting for current source/mapping cache; no Tally request issued.";
                return;
            }
            var exportCompanyKey = _tallyCoordinator.GetCurrentClientLink()?.CompanyKey ?? string.Empty;
            if (exportCompanyKey.Length == 0)
            {
                TallyAutoSyncStatus = "Auto Sync waiting: exact linked company is not confirmed.";
                return;
            }
            var revisionKey = AutoSyncRevisionPreferenceKey(client, exportCompanyKey, moduleBits,
                bankRanges, purchaseRanges, salesRanges, cashRanges, journalRanges);
            if (!_autoTallyKnownLocalRevisions.TryGetValue(revisionKey, out var previousRevision))
            {
                previousRevision = _clientPreferences.Get(client.Id, revisionKey).Trim();
                _autoTallyKnownLocalRevisions[revisionKey] = previousRevision;
            }
            var freshRevision = !revision.Equals(previousRevision, StringComparison.Ordinal);
            // Do not consume continuation while Tally is still offline or busy.
            // It belongs to the next completed common-export batch, not a wake-up.
            var recreateFollowUp = AutoSyncRecreate &&
                Volatile.Read(ref _autoTallyRecreateContinuation) != 0;
            // Fix28: a delayed cache publication, client paint or Tally readback
            // cannot authorize another export even if its derived digest differs.
            // A classified scope is only re-examined after a genuine committed
            // local change, a user selection edit or an explicit restore batch.
            // The manual Export Selected command is independent of this gate.
            if (previousRevision.Length > 0 && freshRevision && claimedChanged == 0 &&
                claimedExplicit == 0 && !recreateFollowUp)
            {
                completed = true;
                _tallyCoordinator.SetOutboundAutoExportReady(client, false);
                TallyAutoSyncStatus = "Auto Sync idle: cache-only revision changed without committed work; no Tally request.";
                AutomationDiagnosticLog.Write("TALLY-AUTO-CACHE-ECHO",
                    $"Ignored cache-only revision for {client.FinancialYear} scope {revisionKey}. " +
                    $"Saved={previousRevision[..Math.Min(12, previousRevision.Length)]}; " +
                    $"cache={revision[..Math.Min(12, revision.Length)]}. No manual or automatic import issued.",
                    client.ClientCode);
                return;
            }
            if (!freshRevision && !recreateFollowUp)
            {
                completed = true; // consume duplicate queue signal; do not loop on it.
                _tallyCoordinator.SetOutboundAutoExportReady(client, false);
                TallyAutoSyncStatus = "Auto Sync idle: previously classified source revision unchanged; no duplicate export or Tally launch.";
                AutomationDiagnosticLog.Write("TALLY-AUTO-NOOP",
                    "Selected local revision unchanged; blocked duplicate Auto Open/ExportSelectedAsync.", client.ClientCode);
                return;
            }
            // An initial app load with all rows already verified imported is NOT
            // evidence of a new ALTER. Capture its baseline without a Tally request.
            // A later actual local edit changes the fingerprint and can wake ALTER.
            if (previousRevision.Length == 0 && claimedChanged == 0 && !recreateFollowUp && !AutoSyncRecreate &&
                !confirmedDeletedRecreateReady &&
                TallyExportModuleStatuses.Where(item => item.IsAutoImportEnabled &&
                    (item.Module == "Bank" || item.Module == "Purchase" || item.Module == "Sales" ||
                     item.Module == "Cash" || item.Module == "Journal")).All(item => item.ReadyCount == 0))
            {
                completed = true;
                _autoTallyKnownLocalRevisions[revisionKey] = revision;
                QueueTallyPreferenceWrite(client.Id, revisionKey, revision);
                _tallyCoordinator.SetOutboundAutoExportReady(client, false);
                TallyAutoSyncStatus = "Auto Sync idle: all selected entries are already imported; no Tally launch.";
                return;
            }
            // Auto Open is NOT a Manual Export step. For Accounting modules, wait
            // for the existing completed XML-ready signal. Banking is different:
            // its final XML requires live MASTERID authority, so a complete local
            // Bank source plan is the pre-open readiness signal, never final XML.
            // Fresh Tally authority and the common exporter still gate any write.
            if (!TallyPrivateCompanySessionService.AnyTallyRunning())
            {
                // Do not open for a startup scan that contains only already-imported
                // unchanged rows. But a COMMITTED source change may need ALTER even
                // if its saved import key makes the UI's ReadyCount show zero.
                // Only the shared exporter makes the final CREATE/ALTER decision.
                // A genuine revision discovered AFTER its earlier event was handled
                // (e.g. a delayed workspace snapshot) still wakes the verified exporter.
                // A newly selected scope with no source events/Ready rows instead
                // establishes its idle baseline above and does not launch Tally.
                var knownScopeChanged = freshRevision && previousRevision.Length > 0;
                bool LocalWorkReady(string name, int bit) => (moduleBits & bit) != 0 &&
                    TallyExportModuleStatuses.Any(item =>
                        item.Module.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                        item.IsAutoImportEnabled && item.SelectedCount > 0 &&
                        (item.ReadyCount > 0 || (claimedChanged & bit) != 0 || knownScopeChanged));
                var bankingPlanReady = LocalWorkReady("Bank", AutoSyncBankMask);
                var cashPlanReady = LocalWorkReady("Cash", AutoSyncCashMask);
                var accountingXmlReady = (moduleBits &
                    (AutoSyncPurchaseMask | AutoSyncSalesMask | AutoSyncJournalMask)) != 0 &&
                    _tallyCoordinator.PreparedAccountingXmlReadyCount > 0 &&
                    TallyExportModuleStatuses.Any(item => item.IsAutoImportEnabled &&
                        item.SelectedCount > 0 &&
                        (item.Module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ||
                         item.Module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ||
                         item.Module.Equals("Journal", StringComparison.OrdinalIgnoreCase)));
                // Accounting ALTER requests may already be marked Imported locally;
                // their new committed version still needs the normal live-Tally
                // MASTERID barrier before final XML serialization.
                var changedAccountingPlanReady =
                    LocalWorkReady("Purchase", AutoSyncPurchaseMask) ||
                    LocalWorkReady("Sales", AutoSyncSalesMask) ||
                    LocalWorkReady("Journal", AutoSyncJournalMask);
                // The user may turn Auto Recreate ON after a voucher was confirmed
                // deleted in Tally. That saved deletion is already work-ready even
                // though its source is marked Imported and has ReadyCount == 0.
                // These read-only candidates grant only Auto Open permission; the
                // unchanged common exporter still re-proves deletion/MASTERID.
                var deletedRecreateReady = confirmedDeletedRecreateReady;
                if (!bankingPlanReady && !cashPlanReady && !accountingXmlReady &&
                    !changedAccountingPlanReady && !deletedRecreateReady)
                {
                    TallyAutoSyncStatus = "Auto Sync waiting for eligible XML or a committed CREATE/ALTER/RECREATE plan in the selected periods.";
                    return;
                }
                if (TallyAutoOpen)
                {
                    _tallyCoordinator.SetOutboundAutoExportReady(client, true, revision);
                    TallyAutoSyncStatus = bankingPlanReady
                        ? "Bank export plan prepared. Auto Open uses the linked Tally executable; Bank XML awaits fresh MASTERID authority."
                        : cashPlanReady && !accountingXmlReady
                            ? "Cash export plan prepared. Final Cash XML awaits fresh Tally authority."
                            : "Accounting export XML ready. Auto Open will use the verified linked Tally executable.";
                }
                else
                    TallyAutoSyncStatus = "Export work prepared. Auto Open is OFF; waiting for the linked Tally company to be opened manually.";
                return;
            }
            // Fix24: Process.Start / AnyTallyRunning proves only that a Tally process
            // exists. The company can take several seconds longer to load (diagnostic:
            // Auto Open 19:11:35, premature export 19:11:37, live TRAIL 19:11:40).
            // Do not enter the common exporter until the NORMAL five-second discovery
            // confirms this client's exact linked company. The pending mask is restored
            // in finally, and OnTallyCoordinatorChanged wakes the same queued work
            // when the existing connection loop confirms that company. No new XML,
            // Tally query or after-export operation belongs to this handoff.
            var exportLink = _tallyCoordinator.GetCurrentClientLink();
            var verifiedLiveCompany = exportLink is not null &&
                _tallyCoordinator.IsConnected &&
                _tallyCoordinator.OpenCompanies.Any(company =>
                    company.CompanyKey.Equals(exportLink.CompanyKey, StringComparison.OrdinalIgnoreCase) &&
                    company.Name.Equals(exportLink.CompanyName, StringComparison.OrdinalIgnoreCase));
            if (!verifiedLiveCompany)
            {
                TallyAutoSyncStatus =
                    "Auto Sync waiting for the confirmed linked Tally company to finish opening; queued export preserved.";
                return;
            }

            _lastAutoTallySyncAttemptUtc = DateTime.UtcNow;
            TallyAutoSyncStatus = $"Auto Sync checking verified Tally authority for {client.ClientName} · FY {client.FinancialYear}…";
            AutomationDiagnosticLog.Write("TALLY-AUTO-EXPORT",
                $"Entered existing manual MASTERID-first exit once; source={revision[..Math.Min(12, revision.Length)]}; " +
                $"committedBits={claimedChanged}; selectionBits={claimedExplicit}; continuation={recreateFollowUp}. " +
                "Await live authority and IMPORTRESULT.", client.ClientCode);
            enteredTallyExit = true;
            // Fix27: automatic imports are background work. The Win32 focus guard
            // applies to AAH-owned automatic sessions only, never a user Tally.
            var result = await TallyPrivateCompanySessionService.Shared.WithoutAutomaticForegroundAsync(
                client, () => _tallyCoordinator.ExportSelectedAsync(new TallyVoucherExportRequest
            {
                BankStatementRanges = (moduleBits & AutoSyncBankMask) != 0 ? bankRanges : [],
                PurchaseRanges = (moduleBits & AutoSyncPurchaseMask) != 0 ? purchaseRanges : [],
                SalesRanges = (moduleBits & AutoSyncSalesMask) != 0 ? salesRanges : [],
                CashEntryRanges = (moduleBits & AutoSyncCashMask) != 0 ? cashRanges : [],
                JournalRanges = (moduleBits & AutoSyncJournalMask) != 0 ? journalRanges : [],
                AllowInteractiveRecoveryPrompt = false,
                IncludeRecurringCash = IsTallyAutoSyncFull || AutoSyncRecurring,
                AllowBankSuspenseForUnmapped = AutoSyncBankImportSuspense,
                AutoRecreateConfirmedTallyDeletions = AutoSyncRecreate,
                GenerateDiagnosticPack = true,
                GenerateDiagnosticPackOnlyWhenMaterial = true
            }));

            Interlocked.Exchange(ref _autoTallyRecreateContinuation, 0);
            // Classification finished through the ONE existing exit. A second wake-up
            // with identical local source must never enter Tally again, even when
            // some rows were duplicates, blocked or already imported. Genuine edits
            // produce a new revision and are eligible for another checked pass.
            // Fix27: the exporter can commit benign local normalization in addition
            // to Tally IMPORTRESULT. Capture the post-write desired revision (unless
            // a real user/import event is queued) so reconciliation publications
            // cannot cause a second automatic Tally request for the same batch.
            // A genuine concurrent source edit leaves pending bits and runs next.
            if (!result.AutoRecreateBatchLimitReached &&
                Volatile.Read(ref _autoTallySyncPendingMask) == 0 &&
                Volatile.Read(ref _autoTallySyncChangedMask) == 0)
            {
                var after = GetAutomaticSourceRevision(client, moduleBits,
                    bankRanges, purchaseRanges, salesRanges, cashRanges, journalRanges,
                    out _);
                if (after is not null) revision = after;
            }
            _autoTallyKnownLocalRevisions[revisionKey] = revision;
            QueueTallyPreferenceWrite(client.Id, revisionKey, revision);
            completed = true;
            _tallyCoordinator.SetOutboundAutoExportReady(client, false);
            TallyAutoSyncStatus = $"Auto Sync: {result.Status}";
            AutomationDiagnosticLog.Write("TALLY-AUTO-EXPORT",
                $"Shared export completed: selected={result.Selected}; ready={result.Ready}; imported={result.Imported}; " +
                $"verifiedPending={result.UnverifiedAfterImport}; conflicts={result.ConflictsBlocked}; " +
                $"TallyErrors={result.TallyErrors}; liveReadErrors={result.LiveReadErrors}; " +
                $"diagnostics={_tallyCoordinator.LastDiagnosticReportPath}", client.ClientCode);
            TallyExportStatus = result.Status;
            // Adaptive unattended restore batches, only while the verified import
            // confirmed some new vouchers and the user still has Auto Recreate ON.
            if (result.AutoRecreateBatchLimitReached && AutoSyncRecreate && IsTallyAutoSyncEnabled)
            {
                Interlocked.Exchange(ref _autoTallyRecreateContinuation, 1);
                QueueExistingEligibleTallyExport();
            }
            OnPropertyChanged(nameof(TallyDiagnosticReportPath));
            OnPropertyChanged(nameof(HasTallyDiagnosticReport));
            OpenTallyDiagnosticFolderCommand.RaiseCanExecuteChanged();
        }
        catch (InvalidOperationException exception)
        {
            // If the common writer was entered, Tally may have accepted XML before
            // a response/readback failed. Never automatically replay that unknown
            // outcome; manual Export/Refresh still uses its original authority.
            if (enteredTallyExit) completed = true;
            TallyAutoSyncStatus = enteredTallyExit
                ? $"Auto Sync verification required (no blind retry): {exception.Message}"
                : $"Auto Sync waiting: {exception.Message}";
            AutomationDiagnosticLog.Write("TALLY-AUTO-EXPORT-WAIT", exception.ToString(), _clientContext.CurrentClient?.ClientCode);
        }
        catch (Exception exception)
        {
            if (enteredTallyExit) completed = true;
            TallyAutoSyncStatus = enteredTallyExit
                ? $"Auto Sync stopped after uncertain Tally outcome; verify before another write: {exception.Message}"
                : $"Auto Sync action required: {exception.Message}";
            AutomationDiagnosticLog.Write("TALLY-AUTO-EXPORT-ERROR", exception.ToString(), _clientContext.CurrentClient?.ClientCode);
        }
        finally
        {
            if (!completed && claimedPending != 0)
            {
                Interlocked.Or(ref _autoTallySyncPendingMask, claimedPending);
                if (claimedChanged != 0) Interlocked.Or(ref _autoTallySyncChangedMask, claimedChanged);
                if (claimedExplicit != 0) Interlocked.Or(ref _autoTallySyncExplicitSelectionMask, claimedExplicit);
            }
            Interlocked.Exchange(ref _autoTallySyncInFlight, 0);
            RefreshTallySection();
            if (!_disposed && !IsTallyAutoSyncOff && completed && Volatile.Read(ref _autoTallySyncPendingMask) != 0)
                ScheduleTallyAutoSync();
        }
    }

    private async Task ExportSelectedToTallyAsync()
    {
        if (!_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.TallyExport))
        {
            TallyExportStatus = "Your role does not have permission to export accounting vouchers to Tally.";
            return;
        }
        if (!IncludeBankStatement && !IncludePurchases && !IncludeSales &&
            !IncludeCashEntries && !IncludeJournals)
        { TallyExportStatus = "Select at least one available accounting module."; return; }

        IReadOnlyList<TallyVoucherExportRange> bankRanges = [];
        IReadOnlyList<TallyVoucherExportRange> purchaseRanges = [];
        IReadOnlyList<TallyVoucherExportRange> salesRanges = [];
        IReadOnlyList<TallyVoucherExportRange> cashRanges = [];
        IReadOnlyList<TallyVoucherExportRange> journalRanges = [];
        if (IncludeBankStatement && !TryResolveModuleRanges(BankExportPeriodMode, BankTallyExportMonths, BankExportFromDate, BankExportToDate, "Bank Statement", out bankRanges, out var bankError))
        { TallyExportStatus = bankError; return; }
        if (IncludePurchases && !TryResolveModuleRanges(PurchaseExportPeriodMode, PurchaseTallyExportMonths, PurchaseExportFromDate, PurchaseExportToDate, "Purchase", out purchaseRanges, out var purchaseError))
        { TallyExportStatus = purchaseError; return; }
        if (IncludeSales && !TryResolveModuleRanges(SalesExportPeriodMode, SalesTallyExportMonths, SalesExportFromDate, SalesExportToDate, "Sales", out salesRanges, out var salesError))
        { TallyExportStatus = salesError; return; }
        if (IncludeCashEntries && !TryResolveModuleRanges(CashExportPeriodMode, CashTallyExportMonths, CashExportFromDate, CashExportToDate, "Cash Entry", out cashRanges, out var cashError))
        { TallyExportStatus = cashError; return; }
        if (IncludeJournals && !TryResolveModuleRanges(JournalExportPeriodMode, JournalTallyExportMonths, JournalExportFromDate, JournalExportToDate, "Journal", out journalRanges, out var journalError))
        { TallyExportStatus = journalError; return; }

        try
        {
            TallyExportStatus = $"Preparing Bank {bankRanges.Count}; Purchase {purchaseRanges.Count}; Sales {salesRanges.Count}; Cash {cashRanges.Count}; Journal {journalRanges.Count} selected period(s).";
            var result = await _tallyCoordinator.ExportSelectedAsync(new TallyVoucherExportRequest
            {
                BankStatementRanges = bankRanges, PurchaseRanges = purchaseRanges, SalesRanges = salesRanges,
                CashEntryRanges = cashRanges, JournalRanges = journalRanges,
                // Manual Bank export: mapped Final Ledger wins; blank Final Ledger uses the existing Suspense counterpart.
                // This is independent of the automatic-only Import Suspense preference.
                AllowBankSuspenseForUnmapped = true
            });
            TallyExportStatus = result.Status;
            OnPropertyChanged(nameof(TallyDiagnosticReportPath));
            OnPropertyChanged(nameof(HasTallyDiagnosticReport));
            OpenTallyDiagnosticFolderCommand.RaiseCanExecuteChanged();
            if (bankRanges.Count > 0) LastBankImportStatus = result.Status;
            var client = _clientContext.CurrentClient!;
            LoadAccountingOverview(client); InitializeTallyExportPeriods(client);
            TallyReportStatus = "Export completed. Live reports remain on-demand; no supporting report was requested automatically.";
        }
        catch (Exception exception) { TallyExportStatus = $"Tally export could not complete: {exception.Message}"; }
        finally { RefreshTallySection(); }
    }

    private static bool TryResolveModuleRanges(
        string mode,
        IEnumerable<TallyExportMonthOption> monthOptions,
        DateTime? fromDate,
        DateTime? toDate,
        string moduleName,
        out IReadOnlyList<TallyVoucherExportRange> ranges,
        out string error)
    {
        if (mode.Equals("Month", StringComparison.OrdinalIgnoreCase))
        {
            var options = monthOptions.ToArray();
            var selected = options.Where(option => option.IsSelected).ToArray();
            // By design, zero selected months means all available months for an
            // enabled module. This protects the default full-year export flow.
            var effective = selected.Length > 0
                ? selected
                : options.Where(option => option.SourceCount > 0).ToArray();
            ranges = effective
                .OrderBy(option => option.FromDate)
                .Select(option => new TallyVoucherExportRange { FromDate = option.FromDate.Date, ToDate = option.ToDate.Date })
                .ToArray();
            if (ranges.Count == 0) { error = $"No {moduleName} source data is available in this financial year."; return false; }
            error = string.Empty; return true;
        }
        if (fromDate is null || toDate is null) { ranges = []; error = $"Select both {moduleName} From Date and To Date."; return false; }
        if (fromDate.Value.Date > toDate.Value.Date) { ranges = []; error = $"{moduleName} From Date cannot be after To Date."; return false; }
        ranges = [new TallyVoucherExportRange { FromDate = fromDate.Value.Date, ToDate = toDate.Value.Date }];
        error = string.Empty; return true;
    }

    private static IReadOnlyList<TallyVoucherExportRange> CoalesceContiguousTallyRanges(
        IEnumerable<TallyVoucherExportRange> ranges)
    {
        var ordered = ranges.OrderBy(range => range.FromDate).ThenBy(range => range.ToDate).ToArray();
        if (ordered.Length <= 1) return ordered;
        var result = new List<TallyVoucherExportRange>();
        var from = ordered[0].FromDate.Date;
        var to = ordered[0].ToDate.Date;
        for (var i = 1; i < ordered.Length; i++)
        {
            var next = ordered[i];
            if (next.FromDate.Date <= to.AddDays(1))
            {
                if (next.ToDate.Date > to) to = next.ToDate.Date;
                continue;
            }
            result.Add(new TallyVoucherExportRange { FromDate = from, ToDate = to });
            from = next.FromDate.Date;
            to = next.ToDate.Date;
        }
        result.Add(new TallyVoucherExportRange { FromDate = from, ToDate = to });
        return result;
    }

    private async Task ImportBankRangeAsync()
    {
        if (!TryResolveModuleRanges(BankExportPeriodMode, BankTallyExportMonths, BankExportFromDate, BankExportToDate,
                "Bank Statement", out var ranges, out var error))
        { LastBankImportStatus = error; return; }
        try
        {
            LastBankImportStatus = $"Preparing Bank Statement vouchers for {ranges.Count:N0} selected period(s)…";
            var statuses = new List<string>();
            foreach (var range in CoalesceContiguousTallyRanges(ranges))
            {
                var result = await _tallyCoordinator.ImportBankRangeAsync(range.FromDate, range.ToDate);
                statuses.Add(result.Status);
            }
            LastBankImportStatus = string.Join(" ", statuses);
            InitializeTallyExportPeriods(_clientContext.CurrentClient!);
        }
        catch (Exception exception) { LastBankImportStatus = $"Bank Statement Tally import could not complete: {exception.Message}"; }
        finally { RefreshTallySection(); }
    }

    private void RefreshTallyCommandStates()
    {
        ConfirmTallyLinkCommand.RaiseCanExecuteChanged();
        CancelTallySelectionCommand.RaiseCanExecuteChanged();
        RequestUnlinkTallyCommand.RaiseCanExecuteChanged();
        ConfirmUnlinkTallyCommand.RaiseCanExecuteChanged();
        CancelUnlinkTallyCommand.RaiseCanExecuteChanged();
        RefreshTallyCommand.RaiseCanExecuteChanged();
        RefreshSelectedTallyReportCommand.RaiseCanExecuteChanged();
        RefreshTallyStatisticsCommand.RaiseCanExecuteChanged();
        ExportSelectedToTallyCommand.RaiseCanExecuteChanged();
        OpenTallyDiagnosticFolderCommand.RaiseCanExecuteChanged();
        OpenLinkedFolderCommand.RaiseCanExecuteChanged();
        ImportBankRangeCommand.RaiseCanExecuteChanged();
        RefreshTallyExportSummaryState();
    }

    private bool IsConfirmedTallyLinkLive(TallyClientLink? link, TallyCompanyInfo? openLinkedCompany)
    {
        if (link is null || !_tallyCoordinator.IsConnected)
            return false;

        // ERP 9 background discovery is deliberately suppressed to prevent HTTP/XML
        // hold. In ERP 9 mode, a running Tally process plus the persisted exclusive
        // client/company link is therefore the live-link authority. TallyPrime keeps
        // the stronger live OpenCompanies membership requirement.
        return _tallyCoordinator.IsErp9CompatibilityMode || openLinkedCompany is not null;
    }

    private void RefreshTallyExportSummaryState()
    {
        var client = _clientContext.CurrentClient;
        TallyExportModuleStatuses.Clear();

        if (client is null)
        {
            TallyExportTotalTransactions = 0;
            TallyExportSelectedTransactions = 0;
            TallyStatusState = "Neutral";
            TallyExportHealthState = "Neutral";
            return;
        }

        var link = _tallyCoordinator.GetCurrentClientLink();
        var openLinkedCompany = link is null
            ? null
            : _tallyCoordinator.OpenCompanies.FirstOrDefault(company =>
                company.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase));

        // Connection/link health must come from the coordinator's authoritative state,
        // never from words inside the user-facing status text. ERP 9 intentionally uses
        // "live" wording and may retain a confirmed persisted link without background
        // company polling, while Prime still requires the linked company to be present
        // in the live OpenCompanies collection.
        var connectionLooksOnline = _tallyCoordinator.IsConnected;
        var confirmedLinkIsLive = IsConfirmedTallyLinkLive(link, openLinkedCompany);
        TallyStatusState = !connectionLooksOnline
            ? "Red"
            : confirmedLinkIsLive
                ? "Green"
                : "Neutral";

        if (!IsTallyExportPeriodMatrixInitialized(client) ||
            !_workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var cached))
        {
            TallyExportTotalTransactions = 0;
            TallyExportSelectedTransactions = 0;
            TallyExportHealthState = confirmedLinkIsLive ? "Green" : "Neutral";
            return;
        }

        var modules = new[] { "Bank", "Purchase", "Sales", "Cash", "Journal" };
        var total = 0;
        var selectedTotal = 0;

        foreach (var module in modules)
        {
            var allKeys = BuildModuleSourceKeys(client, cached, module, null, null);
            total += allKeys.Count;

            if (!IsModuleIncluded(module) || allKeys.Count == 0)
            {
                TallyExportModuleStatuses.Add(new TallyExportModuleStatus
                {
                    Module = ModuleDisplayName(module),
                    IsAutoImportEnabled = false, // never mark an empty/unselected module AI
                    TotalCount = allKeys.Count,
                    SelectedCount = 0,
                    ReadyCount = 0,
                    ImportedCount = 0,
                    State = "Neutral",
                    Detail = allKeys.Count == 0 ? "No source transactions" : "Module not selected"
                });
                continue;
            }

            var selectedKeys = BuildSelectedModuleSourceKeys(client, cached, module);
            selectedTotal += selectedKeys.Count;
            var imported = link is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : ReadImportedSourceKeys(client, link.CompanyKey, module);
            var importedCount = selectedKeys.Count(key => imported.Contains(key));
            // Dashboard readiness is module-scoped and must cover every selected export
            // path, including the strict ERP 9 Bank writer. The previous global live-XML
            // count represented only the accounting XML pipeline and therefore looked
            // lower whenever Bank vouchers were selected. Here, Ready means a locally
            // selected voucher that is not already recorded as imported for this linked
            // Company/FY. Exact live-Tally duplicate/conflict checks still run immediately
            // before send and remain the final authority.
            var readyCount = Math.Max(0, selectedKeys.Count - importedCount);
            var exportHasFailure = HasTallyError ||
                TallyExportStatus.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                TallyExportStatus.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                TallyExportStatus.Contains("unverified", StringComparison.OrdinalIgnoreCase) ||
                TallyExportStatus.Contains("incomplete", StringComparison.OrdinalIgnoreCase);
            var state = selectedKeys.Count == 0
                ? "Neutral"
                : importedCount >= selectedKeys.Count
                    ? "Green"
                    : importedCount > 0 || exportHasFailure
                        ? "Red"
                        : "Neutral";
            var detail = selectedKeys.Count == 0
                ? "No transactions in selected period"
                : $"Selected {selectedKeys.Count:N0} • Ready {readyCount:N0} • Imported {importedCount:N0}. " +
                  "Ready is the current local not-yet-imported count; live Tally duplicate/conflict preflight remains authoritative at export.";

            TallyExportModuleStatuses.Add(new TallyExportModuleStatus
            {
                Module = ModuleDisplayName(module),
                IsAutoImportEnabled = selectedKeys.Count > 0 && IsAutoBadgeEnabled(module),
                TotalCount = allKeys.Count,
                SelectedCount = selectedKeys.Count,
                ReadyCount = readyCount,
                ImportedCount = importedCount,
                State = state,
                Detail = detail
            });
        }

        // Masters belong in the same per-type status strip as Bank/Purchase/Sales/
        // Cash/Journal, not in a disconnected metric below the selectors. The value
        // remains CREATE-only from the background accounting XML; existing Tally
        // Masters are excluded by the XML builder.
        var newMastersToCreate = _tallyCoordinator.LastLiveXmlNewMasters;
        TallyExportModuleStatuses.Add(new TallyExportModuleStatus
        {
            Module = "Master",
            IsAutoImportEnabled = newMastersToCreate > 0 && IsTallyAutoSyncEnabled && AutoSyncMasterChanges &&
                (AutoSyncModuleEnabled(AutoSyncBankMask) || AutoSyncModuleEnabled(AutoSyncPurchaseMask) ||
                 AutoSyncModuleEnabled(AutoSyncSalesMask) || AutoSyncModuleEnabled(AutoSyncCashMask) ||
                 AutoSyncModuleEnabled(AutoSyncJournalMask)),
            TotalCount = newMastersToCreate,
            SelectedCount = newMastersToCreate,
            ReadyCount = 0,
            ImportedCount = 0,
            IsMasterStatus = true,
            State = "Neutral",
            Detail = newMastersToCreate > 0
                ? $"{newMastersToCreate:N0} new CREATE-only Master(s) are required by the current background accounting XML; existing Tally Masters are excluded."
                : "No new accounting Master CREATE is currently required by the background XML."
        });

        TallyExportTotalTransactions = total;
        TallyExportSelectedTransactions = selectedTotal;

        var ready = confirmedLinkIsLive && selectedTotal > 0 && !HasTallyError;
        TallyExportHealthState = ready ? "Green" : selectedTotal > 0 || HasAnyTallyExportData ? "Red" : "Neutral";
    }

    private bool IsAutoBadgeEnabled(string module) => IsTallyAutoSyncEnabled &&
        (module.Equals("Bank", StringComparison.OrdinalIgnoreCase) ? AutoSyncBank && IncludeBankStatement :
         module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? AutoSyncPurchase && IncludePurchases :
         module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? AutoSyncSales && IncludeSales :
         module.Equals("Cash", StringComparison.OrdinalIgnoreCase) ? AutoSyncCash && IncludeCashEntries :
         AutoSyncJournal && IncludeJournals);

    private bool IsModuleIncluded(string module) =>
        module.Equals("Bank", StringComparison.OrdinalIgnoreCase) ? IncludeBankStatement :
        module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? IncludePurchases :
        module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? IncludeSales :
        module.Equals("Cash", StringComparison.OrdinalIgnoreCase) ? IncludeCashEntries :
        IncludeJournals;

    private static string ModuleDisplayName(string module) =>
        module.Equals("Bank", StringComparison.OrdinalIgnoreCase) ? "Bank" :
        module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? "Purchase" :
        module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? "Sales" :
        module.Equals("Cash", StringComparison.OrdinalIgnoreCase) ? "Cash" : "Journal";

    private IReadOnlySet<string> ReadImportedSourceKeys(ClientProfile client, string companyKey, string module)
    {
        try
        {
            return module.Equals("Bank", StringComparison.OrdinalIgnoreCase)
                ? _tallyRepository.GetImportedSourceKeys(client.Id, client.FinancialYear, companyKey, "Bank Voucher")
                : module.Equals("Purchase", StringComparison.OrdinalIgnoreCase)
                    ? _tallyRepository.GetImportedSourceKeys(client.Id, client.FinancialYear, companyKey, "Purchase Voucher")
                : module.Equals("Sales", StringComparison.OrdinalIgnoreCase)
                    ? _tallyRepository.GetImportedSourceKeys(client.Id, client.FinancialYear, companyKey, "Sales Voucher")
                : module.Equals("Cash", StringComparison.OrdinalIgnoreCase)
                    ? _tallyRepository.GetImportedSourceKeys(client.Id, client.FinancialYear, companyKey, "Cash Voucher")
                : _tallyRepository.GetImportedSourceKeys(client.Id, client.FinancialYear, companyKey, "Manual Journal")
                    .Concat(_tallyRepository.GetImportedSourceKeys(client.Id, client.FinancialYear, companyKey, "GST Journal"))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private IReadOnlyList<string> BuildSelectedModuleSourceKeys(
        ClientProfile client,
        ClientWorkspaceSnapshot cached,
        string module)
    {
        var mode = module.Equals("Bank", StringComparison.OrdinalIgnoreCase) ? BankExportPeriodMode :
            module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? PurchaseExportPeriodMode :
            module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? SalesExportPeriodMode :
            module.Equals("Cash", StringComparison.OrdinalIgnoreCase) ? CashExportPeriodMode : JournalExportPeriodMode;

        if (mode.Equals("Month", StringComparison.OrdinalIgnoreCase))
        {
            var selected = GetExportOptions(module).Where(option => option.IsSelected && option.SourceCount > 0).ToArray();
            var effective = selected.Length > 0
                ? selected
                : GetExportOptions(module).Where(option => option.SourceCount > 0).ToArray();
            return effective
                .SelectMany(option => BuildModuleSourceKeys(client, cached, module, option.FromDate, option.ToDate))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var from = module.Equals("Bank", StringComparison.OrdinalIgnoreCase) ? BankExportFromDate :
            module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? PurchaseExportFromDate :
            module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? SalesExportFromDate :
            module.Equals("Cash", StringComparison.OrdinalIgnoreCase) ? CashExportFromDate : JournalExportFromDate;
        var to = module.Equals("Bank", StringComparison.OrdinalIgnoreCase) ? BankExportToDate :
            module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? PurchaseExportToDate :
            module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? SalesExportToDate :
            module.Equals("Cash", StringComparison.OrdinalIgnoreCase) ? CashExportToDate : JournalExportToDate;
        if (!from.HasValue || !to.HasValue || from.Value.Date > to.Value.Date) return [];
        return BuildModuleSourceKeys(client, cached, module, from.Value.Date, to.Value.Date);
    }

    private static IReadOnlyList<string> BuildModuleSourceKeys(
        ClientProfile client,
        ClientWorkspaceSnapshot cached,
        string module,
        DateTime? from,
        DateTime? to)
    {
        var (fyFrom, fyTo) = TallyLiveConnectionCoordinator.FinancialYearDates(client.FinancialYear);
        var start = (from ?? fyFrom).Date;
        var end = (to ?? fyTo).Date;

        if (module.Equals("Bank", StringComparison.OrdinalIgnoreCase))
        {
            return cached.BankTransactions
                .Where(row => !row.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase) &&
                              IsInside(row.TransactionDate, start, end))
                .Select(row => TallyVoucherFingerprint.BuildSourceKey(client.Id, client.FinancialYear, row.TransactionId, row.Id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (module.Equals("Purchase", StringComparison.OrdinalIgnoreCase))
            return BuildPurchaseSourceKeys(client, cached.Purchases, start, end);
        if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase))
            return BuildSalesSourceKeys(client, cached.Sales, start, end);
        if (module.Equals("Cash", StringComparison.OrdinalIgnoreCase))
            return BuildCashSourceKeys(client, cached.CashEntries, start, end);
        return BuildJournalSourceKeys(client, cached.Journals, start, end);
    }

    private void ApplyClientIdentity(ClientProfile client)
    {
        DisplayName = _clientContext.DisplayName;
        ClientCode = EmptyToDash(client.ClientCode);
        ClientName = EmptyToDash(client.ClientName);
        TradeName = EmptyToDash(client.TradeName);
        LegalName = EmptyToDash(client.LegalName);
        Gstin = EmptyToDash(client.Gstin);
        ClientGstinRegistrations = BuildClientGstinRegistrations(client);
        // Display-only branches. The workspace remains the same Client/FY and
        // all existing bank, GST and Tally import routes remain unchanged.
        ClientBranches = ClientBranchCatalog.Resolve(client,
            _applicationPreferences.Get(ClientBranchCatalog.Key(client.Id)));
        OnPropertyChanged(nameof(HasValidGstin));
        Pan = EmptyToDash(client.Pan);
        State = EmptyToDash(client.State);
        FinancialYear = EmptyToDash(client.FinancialYear);
        ContactText = BuildContactText(client);
        AddressText = EmptyToDefault(client.Address, "No address recorded");
    }

    private static IReadOnlyList<DashboardGstinRegistration> BuildClientGstinRegistrations(ClientProfile client)
    {
        var registrations = new List<DashboardGstinRegistration>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primary = (client.Gstin ?? string.Empty).Trim().ToUpperInvariant();
        if (primary.Length == 15 && seen.Add(primary))
            registrations.Add(new DashboardGstinRegistration(primary, "Primary"));
        foreach (var value in client.AdditionalGstins ?? [])
        {
            var additional = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (additional.Length == 15 && seen.Add(additional))
                registrations.Add(new DashboardGstinRegistration(additional, "Additional"));
        }
        return registrations;
    }

    private void ApplyImmediateDashboardState(ClientProfile client, bool force = false)
    {
        var key = $"{client.Id}\u001f{client.FinancialYear.Trim().ToUpperInvariant()}";
        if (!force && _immediateDashboardClientKey.Equals(key, StringComparison.Ordinal) &&
            AccountingSummaryMetrics.Count > 0 && AutoImportSummaryMetrics.Count > 0)
            return;

        _immediateDashboardClientKey = key;
        AccountingSummaryMetrics.Clear();
        foreach (var metric in CreateZeroAccountingMetrics()) AccountingSummaryMetrics.Add(metric);

        AutoImportSummaryMetrics.Clear();
        foreach (var module in new[] { "Banking", "Purchase", "Sales" })
        {
            AutoImportSummaryMetrics.Add(new DashboardAccountingMetric
            {
                Label = module,
                Value = 0,
                Detail = "0 automatic transactions",
                TargetPage = module.Equals("Banking", StringComparison.OrdinalIgnoreCase) ? "Banking" : module,
                StatusState = "Grey"
            });
        }

        AutoImportSummary = "0 current accounting transactions created by Auto Import.";
        AutoImportModules = "Modules: —";
        AutoImportHealthState = "Neutral";
        AutoImportErrorCount = 0;
        AutoImportProgressPercent = 0;
        AutoImportPendingFiles = 0;
        AutoImportActiveFiles = 0;
        AutoImportSuccessfulFiles = 0;
        AutoImportTransactionCount = 0;
        IsAutoImportActive = false;
        TotalRecords = 0;
        AvailableDataModules = 0;
        HasAnyData = false;
        WorkspaceSummary = $"Accounting activity ready • FY {client.FinancialYear} • values updating in background.";
    }

    private static IReadOnlyList<DashboardAccountingMetric> CreateZeroAccountingMetrics() =>
    [
        new DashboardAccountingMetric { Label = "Bank Statement", Value = 0, Detail = "0 bank(s) • 0 transaction(s)", TargetPage = "Banking", IsBankStatementMetric = true, StatusState = "Grey" },
        new DashboardAccountingMetric { Label = "Purchase", Value = 0, Detail = "0 creditor(s) • 0 voucher(s)", TargetPage = "Purchase", StatusState = "Grey" },
        new DashboardAccountingMetric { Label = "Sales", Value = 0, Detail = "0 debtor(s) • 0 voucher(s)", TargetPage = "Sales", StatusState = "Grey" },
        new DashboardAccountingMetric { Label = "Journal", Value = 0, Detail = "Manual 0 • Automatic 0 • 0 review", TargetPage = "Journal", StatusState = "Grey" },
        new DashboardAccountingMetric { Label = "Cash Entries", Value = 0, Detail = "0 cash-entry row(s)", TargetPage = "Cash Entry", StatusState = "Grey" },
        new DashboardAccountingMetric { Label = "Stock Master", Value = 0, Detail = "0 stock item(s)", TargetPage = "Stock Master", StatusState = "Grey" }
    ];

    private void QueueAutoImportPanelRefresh(ClientProfile client, int delayMilliseconds = 160)
    {
        var version = Interlocked.Increment(ref _autoImportPanelRefreshVersion);
        var clientId = client.Id;
        var financialYear = client.FinancialYear;
        _ = Task.Run(async () =>
        {
            await Task.Delay(Math.Max(0, delayMilliseconds)).ConfigureAwait(false);
            if (version != Volatile.Read(ref _autoImportPanelRefreshVersion)) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            await dispatcher.InvokeAsync(() =>
            {
                if (version != Volatile.Read(ref _autoImportPanelRefreshVersion)) return;
                var current = _clientContext.CurrentClient;
                if (current is null || current.Id != clientId ||
                    !current.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)) return;
                RefreshAutoImportPanel();
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        });
    }

    private void QueueAutomaticReviewSyncDeferred(ClientProfile client)
    {
        var clientId = client.Id;
        var financialYear = client.FinancialYear;
        _ = Task.Run(async () =>
        {
            await Task.Delay(250).ConfigureAwait(false);
            var current = _clientContext.CurrentClient;
            if (current is null || current.Id != clientId ||
                !current.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)) return;
            QueueAutomaticReviewSync(client);
        });
    }

    private void RefreshAutoImportPanel()
    {
        var client = _clientContext.CurrentClient;
        RecentAutoImportFiles.Clear();
        AutoImportSummaryMetrics.Clear();
        if (client is null)
        {
            AutoImportSummary = "Open a client to view Auto Import activity.";
            AutoImportModules = "Modules: —";
            AutoImportHealthState = "Neutral";
            AutoImportErrorCount = 0;
            AutoImportProgressPercent = 0;
            AutoImportPendingFiles = 0;
            AutoImportTransactionCount = 0;
            AutoImportSuccessfulFiles = 0;
            AutoImportActiveFiles = 0;
            IsAutoImportActive = false;
            return;
        }

        // Fix38: Auto Import performs and prepares this output in its background service.
        // Dashboard never scans history, source folders, Bank/Purchase/Sales repositories,
        // or reconstructs duplicate/coverage state when a client is opened.
        var output = _automationService.GetDashboardOutput(client.Id, client.FinancialYear);
        foreach (var metric in output.Metrics)
        {
            AutoImportSummaryMetrics.Add(new DashboardAccountingMetric
            {
                Label = metric.Label, Value = metric.Value, Detail = metric.Detail,
                TargetPage = metric.Label.Equals("Banking", StringComparison.OrdinalIgnoreCase) ? "Banking" : metric.Label,
                StatusState = metric.Value > 0 ? "Green" : "Grey"
            });
        }
        foreach (var module in new[] { "Banking", "Purchase", "Sales" })
        {
            if (AutoImportSummaryMetrics.Any(metric => metric.Label.Equals(module, StringComparison.OrdinalIgnoreCase))) continue;
            AutoImportSummaryMetrics.Add(new DashboardAccountingMetric
            {
                Label = module, Value = 0, Detail = "0 automatic transactions",
                TargetPage = module, StatusState = "Grey"
            });
        }

        AutoImportSuccessfulFiles = output.SuccessfulFiles;
        AutoImportTransactionCount = output.TransactionCount;
        AutoImportErrorCount = output.ErrorCount;
        var modules = AutoImportSummaryMetrics.Where(metric => metric.Value > 0).Select(metric => metric.Label).ToArray();
        AutoImportModules = modules.Length == 0 ? "Modules: —" : "Modules: " + string.Join(" • ", modules);

        var progress = _automationService.GetClientProgress(client.Id, client.FinancialYear);
        AutoImportProgressPercent = progress.Percent;
        AutoImportPendingFiles = progress.Pending;
        AutoImportActiveFiles = progress.Active;
        IsAutoImportActive = progress.IsActive;
        _autoImportProgressObservedActive = progress.IsActive;
        AutoImportHealthState = progress.IsActive ? "Neutral" : AutoImportErrorCount > 0 ? "Red" : AutoImportSuccessfulFiles > 0 ? "Green" : "Neutral";
        var currentStage = progress.CurrentStage.Length > 0 ? progress.CurrentStage : "Processing";
        var currentFile = progress.CurrentFileName.Length > 0 ? $" • {progress.CurrentFileName}" : string.Empty;
        AutoImportSummary = progress.IsActive
            ? $"{currentStage}{currentFile} • active {progress.Active:N0} • queued {progress.Queued:N0} • completed {progress.Completed:N0} of {progress.Total:N0}."
            : AutoImportErrorCount > 0
                ? $"{AutoImportTransactionCount:N0} current accounting transaction(s) • {AutoImportErrorCount:N0} current import issue(s)."
                : AutoImportTransactionCount == 0
                    ? "No current accounting transactions created by Auto Import."
                    : $"{AutoImportTransactionCount:N0} current accounting transaction(s) created by Auto Import.";
    }


    private static bool IsAutomationActionCoveredBySuccessfulSource(
        string? sourceKey,
        string? detail,
        IReadOnlySet<string> successfulHashes,
        IReadOnlySet<string> successfulPaths)
    {
        var key = (sourceKey ?? string.Empty).Trim();
        const string prefix = "AUTOMATION:";
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            key = key[prefix.Length..].Trim();
            if (key.EndsWith(":post", StringComparison.OrdinalIgnoreCase))
                key = key[..^5].Trim();
            if (key.Length > 0 && successfulHashes.Contains(key)) return true;
        }

        // A user can correct/replace a source file at the SAME linked path, producing
        // a new hash.  The old hash action then becomes stale once the latest state for
        // that source path imported successfully.  Compare the action's Source: line as
        // well so old failures do not keep an otherwise complete Auto Import card red.
        foreach (var line in (detail ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Source:", StringComparison.OrdinalIgnoreCase)) continue;
            var path = NormalizeAutoImportSource(trimmed["Source:".Length..].Trim());
            return path.Length > 0 && successfulPaths.Contains(path);
        }
        return false;
    }

    private IReadOnlyList<AccountingAutomationHub.Domain.Automation.AutomationFileHistoryRecord> RecoverBankAutoImportSources(
        ClientProfile client,
        IReadOnlyList<AccountingAutomationHub.Domain.Automation.AutomationFileHistoryRecord> history)
    {
        if (!_workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var cached))
            return [];

        var linkedFolders = _automationService.GetFolders()
            .Where(folder => folder.ClientId == client.Id &&
                             folder.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            .Select(folder => NormalizeAutoImportSource(folder.FolderPath))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var historicalBankHashes = history
            .Where(row => AutoImportModule(row.FileType).Equals("Banking", StringComparison.OrdinalIgnoreCase))
            .Select(row => (row.FileHash ?? string.Empty).Trim())
            .Where(hash => hash.Length > 0 && !hash.Contains(':'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<AccountingAutomationHub.Domain.Automation.AutomationFileHistoryRecord>();
        foreach (var statement in cached.BankStatements)
        {
            var statementHash = (statement.FileHash ?? string.Empty).Trim();
            var statementPath = NormalizeAutoImportSource(statement.SourcePath);
            var linkedFolderEvidence = linkedFolders.Any(folder => IsPathInsideFolder(statementPath, folder));
            var historyEvidence = statementHash.Length > 0 && historicalBankHashes.Contains(statementHash);
            if (!linkedFolderEvidence && !historyEvidence) continue;

            result.Add(new AccountingAutomationHub.Domain.Automation.AutomationFileHistoryRecord
            {
                PersistentId = -Math.Max(1L, statement.Id),
                FolderId = string.Empty,
                ClientId = client.Id,
                FinancialYear = client.FinancialYear,
                FilePath = statement.SourcePath,
                FileHash = statementHash,
                FileType = "Bank",
                Status = "Imported",
                Detail = "Recovered from current Bank Statement accounting source evidence.",
                RecordCount = Math.Max(0, statement.TransactionCount),
                ProcessedUtc = statement.ImportedUtc
            });
        }

        return result;
    }

    private static bool IsPathInsideFolder(string normalizedPath, string normalizedFolder)
    {
        if (normalizedPath.Length == 0 || normalizedFolder.Length == 0) return false;
        if (normalizedPath.Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase)) return true;
        var separator = Path.DirectorySeparatorChar.ToString();
        var prefix = normalizedFolder.EndsWith(separator, StringComparison.Ordinal)
            ? normalizedFolder
            : normalizedFolder + separator;
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private AutoImportTransactionCoverage GetCurrentAutomaticTransactionCount(
        ClientProfile client,
        string module,
        IReadOnlyList<AccountingAutomationHub.Domain.Automation.AutomationFileHistoryRecord> sources)
    {
        if (sources.Count == 0) return default;
        // The dashboard reports the records that currently exist in accounting
        // storage, never historical FileHistory.RecordCount values.
        if (!_workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var cached))
            return default;

        var hashes = sources.Select(item => item.FileHash.Trim())
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = sources.Select(item => NormalizeAutoImportSource(item.FilePath))
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = paths.Select(Path.GetFileName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (module.Equals("Banking", StringComparison.OrdinalIgnoreCase))
        {
            var statementIds = cached.BankStatements
                .Where(statement => hashes.Contains(statement.FileHash?.Trim() ?? string.Empty) ||
                                    paths.Contains(NormalizeAutoImportSource(statement.SourcePath)) ||
                                    names.Contains(statement.SourceFile?.Trim() ?? string.Empty))
                .Select(statement => statement.Id)
                .ToHashSet();
            var linkedRows = cached.BankTransactions
                .Where(transaction => statementIds.Contains(transaction.StatementId))
                .Where(transaction => !transaction.RecordStatus.Equals("Generated Transfer Charge", StringComparison.OrdinalIgnoreCase))
                .Where(transaction => !transaction.RecordStatus.StartsWith("Duplicate", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            // Accounting Activity intentionally shows only active Bank Statement rows.
            // Auto Import, however, imported both sides of an inter-bank transfer. Once
            // reconciliation archives the counterpart, that imported row must remain in
            // the Auto Import comparison basis instead of making the panel look incomplete.
            var active = linkedRows.Count(transaction =>
                !transaction.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase));
            var interbankMatched = linkedRows.Count(transaction =>
                transaction.RecordStatus.Equals("Archived Interbank Counterpart", StringComparison.OrdinalIgnoreCase));
            var tallyReconciled = linkedRows.Count(transaction =>
                transaction.RecordStatus.Equals("Archived Tally Reconciled", StringComparison.OrdinalIgnoreCase));
            return new AutoImportTransactionCoverage(active + interbankMatched + tallyReconciled, active, interbankMatched, tallyReconciled);
        }

        if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase))
        {
            var count = cached.Sales.Count(row =>
                hashes.Contains(row.SourceHash?.Trim() ?? string.Empty) ||
                paths.Contains(NormalizeAutoImportSource(row.SourcePath)) ||
                names.Contains(row.SourceFile?.Trim() ?? string.Empty));
            return new AutoImportTransactionCoverage(count, count, 0, 0);
        }

        if (module.Equals("Purchase", StringComparison.OrdinalIgnoreCase))
        {
            var count = cached.Purchases.Count(row =>
                paths.Contains(NormalizeAutoImportSource(row.Source2APath)) ||
                paths.Contains(NormalizeAutoImportSource(row.Source2BPath)) ||
                names.Contains(row.Source2AFile?.Trim() ?? string.Empty) ||
                names.Contains(row.Source2BFile?.Trim() ?? string.Empty));
            return new AutoImportTransactionCoverage(count, count, 0, 0);
        }

        return default;
    }

    private readonly record struct AutoImportTransactionCoverage(int Total, int Active, int InterbankMatched, int TallyReconciled);

    private static string NormalizeAutoImportSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); }
        catch { return path.Trim().ToUpperInvariant(); }
    }

    private static string AutoImportModule(string fileType)
    {
        var value = fileType.Trim();
        if (value.Equals("Bank", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Bank PDF", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Bank OCR", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Bank OCR Page", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("MT940", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("CAMT.053", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("OFX", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("QIF", StringComparison.OrdinalIgnoreCase)) return "Banking";
        if (value.Equals("Purchase", StringComparison.OrdinalIgnoreCase)) return "Purchase";
        if (value.Equals("Sales", StringComparison.OrdinalIgnoreCase)) return "Sales";
        return string.Empty;
    }

    private static int AutoImportModuleOrder(string module) => module switch
    {
        "Banking" => 0,
        "Purchase" => 1,
        "Sales" => 2,
        _ => 9
    };

    private void OnAutomationProgressChanged(object? sender, EventArgs e)
    {
        var version = Interlocked.Increment(ref _autoImportProgressRefreshVersion);
        _ = Task.Run(async () =>
        {
            await Task.Delay(140).ConfigureAwait(false);
            if (version != Volatile.Read(ref _autoImportProgressRefreshVersion)) return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            await dispatcher.InvokeAsync(() =>
            {
                if (version != Volatile.Read(ref _autoImportProgressRefreshVersion)) return;
                RefreshAutoImportProgressOnly();
            }, System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    private void RefreshAutoImportProgressOnly()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var progress = _automationService.GetClientProgress(client.Id, client.FinancialYear);
        if (progress.IsActive) _autoImportProgressObservedActive = true;

        AutoImportProgressPercent = progress.Percent;
        AutoImportPendingFiles = progress.Pending;
        AutoImportActiveFiles = progress.Active;
        IsAutoImportActive = progress.IsActive;

        if (progress.IsActive)
        {
            AutoImportHealthState = "Neutral";
            var currentStage = progress.CurrentStage.Length > 0
                ? progress.CurrentStage
                : "Processing";
            var currentFile = progress.CurrentFileName.Length > 0
                ? $" • {progress.CurrentFileName}"
                : string.Empty;
            AutoImportSummary =
                $"{currentStage}{currentFile} • active {progress.Active:N0} • queued {progress.Queued:N0} • completed {progress.Completed:N0} of {progress.Total:N0}.";
            return;
        }

        // History/accounting coverage is the expensive half of the panel. Rebuild it once
        // when a real active batch becomes idle, not on every queue/stage/history event.
        if (_autoImportProgressObservedActive)
        {
            _autoImportProgressObservedActive = false;
            QueueAutoImportPanelRefresh(client, 80);
        }
    }

    private void ExportDiagnosticExcel()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        try
        {
            var overwrite = _applicationPreferences.GetBool(ApplicationSettingKeys.ReviewExportOverwriteExisting);
            var path = _diagnosticExportService.ExportExcelAuto(client, overwrite);
            DiagnosticExportStatus = $"Client review Excel auto-saved: {path}";
        }
        catch (Exception exception)
        {
            DiagnosticExportStatus = $"Excel client review export failed: {exception.Message}";
        }
    }

    private void ExportDiagnosticCsv()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        try
        {
            var overwrite = _applicationPreferences.GetBool(ApplicationSettingKeys.ReviewExportOverwriteExisting);
            var path = _diagnosticExportService.ExportCsvAuto(client, overwrite);
            DiagnosticExportStatus = $"Bank Statement CSV auto-saved: {path}";
        }
        catch (Exception exception)
        {
            DiagnosticExportStatus = $"CSV client review export failed: {exception.Message}";
        }
    }

    private void QueueAutomaticReviewSync(ClientProfile client)
    {
        string? path;
        try { path = _diagnosticExportService.FindLatestReviewWorkbook(client); }
        catch { return; }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var fingerprint = ReviewWorkbookFingerprint(path);
        if (fingerprint.Length == 0) return;
        if (_reviewWorkbookSyncFingerprints.TryGetValue(path, out var previous) &&
            previous.Equals(fingerprint, StringComparison.Ordinal))
            return;

        var key = $"{client.Id}\u001f{client.FinancialYear.Trim().ToUpperInvariant()}";
        if (!_automaticReviewSyncRequested.TryAdd(key, 0)) return;
        _ = AutoSyncReviewWorkbookAsync(client, path, key, fingerprint);
    }

    private async void OnClientReviewWorkbookChanged(object? sender, ClientReviewWorkbookChangedEventArgs e)
    {
        var client = _clientContext.CurrentClient;
        if (client is null ||
            client.Id != e.ClientId ||
            !client.FinancialYear.Equals(e.FinancialYear, StringComparison.OrdinalIgnoreCase))
            return;

        var fingerprint = ReviewWorkbookFingerprint(e.WorkbookPath);
        if (fingerprint.Length == 0) return;
        if (_reviewWorkbookSyncFingerprints.TryGetValue(e.WorkbookPath, out var previous) &&
            previous.Equals(fingerprint, StringComparison.Ordinal))
            return;

        await AutoSyncReviewWorkbookAsync(client, e.WorkbookPath, null, fingerprint);
    }

    private async Task AutoSyncReviewWorkbookAsync(
        ClientProfile client,
        string workbookPath,
        string? requestKey,
        string fingerprint)
    {
        await _reviewSyncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await Task.Run(
                () => _diagnosticExportService.ImportExcelChanges(client, workbookPath)).ConfigureAwait(false);

            // A clean pass (including zero changed rows) establishes that this exact
            // workbook version is synchronized. Do not repeatedly reopen the same xlsx
            // on every Dashboard refresh/client reopen.
            if (result.ErrorRows == 0 && fingerprint.Length > 0)
                _reviewWorkbookSyncFingerprints[workbookPath] = fingerprint;

            if (result.ChangedRows <= 0 && result.ErrorRows <= 0) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            await dispatcher.InvokeAsync(() =>
            {
                DiagnosticExportStatus = result.WorkbookPath.Length > 0
                    ? $"{result.Message} {result.WorkbookPath}"
                    : result.Message;
                if (result.ChangedRows <= 0) return;
                _workspaceStateCache.Invalidate(client.Id, client.FinancialYear);
                _workspaceStateCache.QueueRefresh(client);
                RefreshAccountingOverviewAfterExternalChange();
            });
        }
        catch (Exception exception)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null)
                await dispatcher.InvokeAsync(() => DiagnosticExportStatus = $"Automatic review sync failed: {exception.Message}");
        }
        finally
        {
            _reviewSyncGate.Release();
            if (!string.IsNullOrWhiteSpace(requestKey))
                _automaticReviewSyncRequested.TryRemove(requestKey, out _);
        }
    }

    private static string ReviewWorkbookFingerprint(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void ImportReviewExcel()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var result = _diagnosticExportService.ImportLatestExcelChanges(client);
        DiagnosticExportStatus = result.WorkbookPath.Length > 0
            ? $"{result.Message} {result.WorkbookPath}"
            : result.Message;
        if (result.ChangedRows <= 0) return;

        // Database writes above are authoritative; refresh the session cache and the
        // currently visible client summary immediately after the edited workbook is applied.
        _workspaceStateCache.Invalidate(client.Id, client.FinancialYear);
        _workspaceStateCache.QueueRefresh(client);
        RefreshAccountingOverviewAfterExternalChange();
    }

    private void LoadTallyExportPreferences(ClientProfile client)
    {
        _loadingExportPreferences = true;
        try
        {
            LoadTallyAutoSyncPreferences(client);
            _bankExportPeriodMode = ReadMode(client, "Bank", _bankExportPeriodMode);
            _purchaseExportPeriodMode = ReadMode(client, "Purchase", _purchaseExportPeriodMode);
            _salesExportPeriodMode = ReadMode(client, "Sales", _salesExportPeriodMode);
            _cashExportPeriodMode = ReadMode(client, "Cash", _cashExportPeriodMode);
            _journalExportPeriodMode = ReadMode(client, "Journal", _journalExportPeriodMode);
            _bankExportFromDate = ReadDate(client, "Bank", "From");
            _bankExportToDate = ReadDate(client, "Bank", "To");
            _purchaseExportFromDate = ReadDate(client, "Purchase", "From");
            _purchaseExportToDate = ReadDate(client, "Purchase", "To");
            _salesExportFromDate = ReadDate(client, "Sales", "From");
            _salesExportToDate = ReadDate(client, "Sales", "To");
            _cashExportFromDate = ReadDate(client, "Cash", "From");
            _cashExportToDate = ReadDate(client, "Cash", "To");
            _journalExportFromDate = ReadDate(client, "Journal", "From");
            _journalExportToDate = ReadDate(client, "Journal", "To");
            // These backing fields are loaded directly to avoid re-persisting the
            // stored preference. The RadioButtons bind to the derived mode booleans,
            // so notify those explicitly as well; otherwise the visual checked state
            // can disagree with the backing mode and the first click appears ignored.
            NotifyTallyExportPeriodModeBindings();
            OnPropertyChanged(nameof(BankExportFromDate)); OnPropertyChanged(nameof(BankExportToDate));
            OnPropertyChanged(nameof(PurchaseExportFromDate)); OnPropertyChanged(nameof(PurchaseExportToDate));
            OnPropertyChanged(nameof(SalesExportFromDate)); OnPropertyChanged(nameof(SalesExportToDate));
            OnPropertyChanged(nameof(CashExportFromDate)); OnPropertyChanged(nameof(CashExportToDate));
            OnPropertyChanged(nameof(JournalExportFromDate)); OnPropertyChanged(nameof(JournalExportToDate));
            OnPropertyChanged(nameof(BankTallyFromDate)); OnPropertyChanged(nameof(BankTallyToDate));
        }
        finally { _loadingExportPreferences = false; }
    }

    private void LoadTallyAutoSyncPreferences(ClientProfile client)
    {
        var mode = _clientPreferences.Get(client.Id, TallyPreferenceKey(client.FinancialYear, "AutoSync", "Mode")).Trim();
        _tallyAutoSyncMode = mode.Equals("Full Auto", StringComparison.OrdinalIgnoreCase) ? "Full Auto" :
            mode.Equals("Selected", StringComparison.OrdinalIgnoreCase) ? "Selected" : "Off";
        var activeMode = _clientPreferences.Get(client.Id, TallyPreferenceKey(client.FinancialYear, "AutoSync", "ActiveMode")).Trim();
        _tallyAutoSyncActiveMode = _tallyAutoSyncMode.Equals("Full Auto", StringComparison.OrdinalIgnoreCase) ||
                                   _tallyAutoSyncMode.Equals("Selected", StringComparison.OrdinalIgnoreCase)
            ? _tallyAutoSyncMode
            : activeMode.Equals("Full Auto", StringComparison.OrdinalIgnoreCase) ? "Full Auto" : "Selected";
        _autoSyncBank = ReadSavedTallyBool(client, "AutoSync", "Bank", true);
        _autoSyncBankImportSuspense = ReadSavedTallyBool(client, "AutoSync", "BankImportSuspense", false);
        _autoSyncPurchase = ReadSavedTallyBool(client, "AutoSync", "Purchase", true);
        _autoSyncSales = ReadSavedTallyBool(client, "AutoSync", "Sales", true);
        _autoSyncCash = ReadSavedTallyBool(client, "AutoSync", "Cash", true);
        _autoSyncJournal = ReadSavedTallyBool(client, "AutoSync", "Journal", true);
        _autoSyncRecurring = ReadSavedTallyBool(client, "AutoSync", "Recurring", true);
        _autoSyncMasterChanges = ReadSavedTallyBool(client, "AutoSync", "Masters", true);
        _autoSyncCreateMasters = ReadSavedTallyBool(client, "AutoSync", "CreateMasters", true);
        _autoSyncCreateVouchers = ReadSavedTallyBool(client, "AutoSync", "CreateVouchers", true);
        _autoSyncAlterMasters = ReadSavedTallyBool(client, "AutoSync", "AlterMasters", true);
        _autoSyncAlterVouchers = ReadSavedTallyBool(client, "AutoSync", "AlterVouchers", true);
        _autoSyncDelete = ReadSavedTallyBool(client, "AutoSync", "Delete", false);
        _autoSyncRecreate = ReadSavedTallyBool(client, "AutoSync", "Recreate", false);
        _tallyCoordinator.SetOutboundAutoSyncEnabled(client, !_tallyAutoSyncMode.Equals("Off", StringComparison.OrdinalIgnoreCase));
        Interlocked.Exchange(ref _autoTallySyncPendingMask, 0);
        Interlocked.Exchange(ref _autoTallySyncChangedMask, 0);
        Interlocked.Exchange(ref _autoTallySyncExplicitSelectionMask, 0);
        TallyAutoSyncStatus = _tallyAutoSyncMode.Equals("Off", StringComparison.OrdinalIgnoreCase)
            ? "Automatic Tally Sync is off. Manual Tally Export remains available."
            : $"Automatic Tally Sync {_tallyAutoSyncMode} enabled. Waiting for an eligible AAH accounting change.";
        foreach (var property in new[]
                 {
                     nameof(TallyAutoSyncMode), nameof(IsTallyAutoSyncOff), nameof(IsTallyAutoSyncEnabled), nameof(IsTallyAutoSyncSelected), nameof(IsTallyAutoSyncFull),
                     nameof(AutoSyncBank), nameof(AutoSyncBankImportSuspense), nameof(AutoSyncPurchase), nameof(AutoSyncSales), nameof(AutoSyncCash), nameof(AutoSyncJournal), nameof(AutoSyncRecurring),
                     nameof(AutoSyncMasterChanges), nameof(AutoSyncCreateMasters), nameof(AutoSyncCreateVouchers), nameof(AutoSyncAlterMasters),
                     nameof(AutoSyncAlterVouchers), nameof(AutoSyncDelete), nameof(AutoSyncRecreate)
                 })
            OnPropertyChanged(property);
    }

    private void PersistAutoSyncPreference(string setting, object value)
    {
        if (_loadingExportPreferences) return;
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        QueueTallyPreferenceWrite(
            client.Id,
            TallyPreferenceKey(client.FinancialYear, "AutoSync", setting),
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private string ReadMode(ClientProfile client, string module, string fallback)
    {
        var value = _clientPreferences.Get(client.Id, TallyPreferenceKey(client.FinancialYear, module, "Mode"));
        return value.Equals("Date Range", StringComparison.OrdinalIgnoreCase) ? "Date Range" : value.Equals("Month", StringComparison.OrdinalIgnoreCase) ? "Month" : fallback;
    }

    private DateTime? ReadDate(ClientProfile client, string module, string side)
    {
        var value = _clientPreferences.Get(client.Id, TallyPreferenceKey(client.FinancialYear, module, side));
        return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.Date : null;
    }

    private HashSet<string> ReadSavedTallyPeriods(ClientProfile client, string module, out bool hasSavedValue)
    {
        var raw = _clientPreferences.Get(client.Id, TallyPreferenceKey(client.FinancialYear, module, "Months")).Trim();
        hasSavedValue = raw.Length > 0;
        if (!hasSavedValue || raw.Equals("__NONE__", StringComparison.OrdinalIgnoreCase))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private bool ReadSavedTallyBool(ClientProfile client, string module, string setting, bool fallback)
    {
        var raw = _clientPreferences.Get(client.Id, TallyPreferenceKey(client.FinancialYear, module, setting)).Trim();
        return bool.TryParse(raw, out var value) ? value : fallback;
    }

    private void PersistTallyMonthSelections(string module)
    {
        if (_loadingExportPreferences) return;
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var selected = GetExportOptions(module)
            .Where(option => option.IsSelected)
            .Select(option => option.PeriodKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        QueueTallyPreferenceWrite(
            client.Id,
            TallyPreferenceKey(client.FinancialYear, module, "Months"),
            selected.Length == 0 ? "__NONE__" : string.Join(',', selected));
        QueueExistingEligibleTallyExport();
    }

    private void PersistTallyExportPreference(string module, string setting, object? value)
    {
        if (_loadingExportPreferences) return;
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var text = value is DateTime date
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        QueueTallyPreferenceWrite(client.Id, TallyPreferenceKey(client.FinancialYear, module, setting), text);
        if (setting is "Mode" or "From" or "To") QueueExistingEligibleTallyExport();
    }

    private void QueueTallyPreferenceWrite(long clientId, string preferenceKey, string preferenceValue)
    {
        var compositeKey = $"{clientId}\u001F{preferenceKey}";
        _pendingTallyPreferenceWrites[compositeKey] =
            new PendingTallyPreferenceWrite(clientId, preferenceKey, preferenceValue);
        ScheduleTallyPreferenceFlush();
    }

    private void ScheduleTallyPreferenceFlush()
    {
        if (Interlocked.CompareExchange(ref _tallyPreferenceFlushScheduled, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                while (!_pendingTallyPreferenceWrites.IsEmpty)
                {
                    foreach (var pair in _pendingTallyPreferenceWrites.ToArray())
                    {
                        if (!_pendingTallyPreferenceWrites.TryRemove(pair.Key, out var pending)) continue;
                        try
                        {
                            _clientPreferences.Set(pending.ClientId, pending.PreferenceKey, pending.PreferenceValue);
                        }
                        catch
                        {
                            // Preferences must never block or undo a UI selection. The
                            // current in-memory value remains authoritative for this session.
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _tallyPreferenceFlushScheduled, 0);
                if (!_pendingTallyPreferenceWrites.IsEmpty) ScheduleTallyPreferenceFlush();
            }
        });
    }

    private static string TallyPreferenceKey(string financialYear, string module, string setting) =>
        $"TallyExport.{financialYear.Trim()}.{module}.{setting}";

    private void ManualAutoImportFromFolder()
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            AutoImportFolderStatus = "Open a client first.";
            return;
        }

        var result = _automationService.ManualImportClientFolders(client.Id, client.FinancialYear);
        AutoImportFolderStatus = result.Message;
    }

    private void ApplyEmptyClient()
    {
        // Force Tally export date/mode preferences to be re-read when this client
        // is opened again instead of inheriting the previous in-memory date token.
        _dateClientId = 0;
        _dateFinancialYear = string.Empty;
        DisplayName = "No client selected";
        ClientCode = "—";
        ClientName = "—";
        TradeName = "—";
        LegalName = "—";
        Gstin = "—";
        ClientGstinRegistrations = [];
        ClientBranches = [];
        OnPropertyChanged(nameof(HasValidGstin));
        Pan = "—";
        State = "—";
        FinancialYear = "—";
        ContactText = "No contact details";
        AddressText = "No address recorded";
        LastActivityText = "No client activity recorded";
        WorkspaceSummary = "Open a client from the Client Directory.";
        AutoImportFolderStatus = "Open a client to use Auto Import From Folder.";
        RefreshAutoImportPanel();
        AvailableDataModules = 0;
        TotalRecords = 0;
        HasAnyData = false;
        RecentImports.Clear();
        AccountingSummaryMetrics.Clear();
        AutoImportSummaryMetrics.Clear();
        Gstr3bMonthTiles.Clear();
        ApplyEmptyCard(BankCard);
        ApplyEmptyCard(PurchaseCard);
        ApplyEmptyCard(SalesCard);
        OnPropertyChanged(nameof(HasBankData));
        OnPropertyChanged(nameof(HasPurchaseData));
        OnPropertyChanged(nameof(HasSalesData));
    }

    private static void ApplySnapshot(DashboardImportCard card, ImportModuleSnapshot snapshot)
    {
        card.IsImported = snapshot.IsImported;
        card.StatusText = snapshot.Status;
        card.PeriodText = snapshot.PeriodLabel;
        card.FilingStatus = snapshot.FilingStatus;
        card.CountText = card.ImportType == "Bank Statement"
            ? $"{snapshot.BankCount:N0} bank(s)"
            : $"{snapshot.RecordCount:N0} record(s)";
        card.LastImportedText = snapshot.LastImportedUtc is null
            ? "Never"
            : AppTime.ToLocal(snapshot.LastImportedUtc.Value).ToString("dd-MM-yyyy HH:mm");
        card.Details = snapshot.Details;
    }

    private static void ApplyEmptyCard(DashboardImportCard card)
    {
        card.IsImported = false;
        card.StatusText = "Not available";
        card.PeriodText = "No period available";
        card.FilingStatus = "Not checked";
        card.CountText = "0 records";
        card.LastImportedText = "Never";
        card.Details = string.Empty;
    }

    private static string BuildContactText(ClientProfile client)
    {
        var parts = new[] { client.Phone, client.Email }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        return parts.Count == 0 ? "No contact details" : string.Join(" • ", parts);
    }

    private static string EmptyToDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static string EmptyToDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string BuildBooksPeriod(string? booksFrom, string? startingFrom)
    {
        static string FormatTallyBookDate(string? value)
        {
            var parsed = TallyCompanyInfo.ParseTallyDate(value ?? string.Empty);
            return parsed is null
                ? EmptyToDash(value)
                : parsed.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        }

        var books = FormatTallyBookDate(booksFrom);
        var starts = FormatTallyBookDate(startingFrom);
        return books == "—" && starts == "—" ? "—" : $"Books: {books} • Starts: {starts}";
    }

    private static string FormatLocalDate(DateTime value) =>
        value == DateTime.MinValue ? "Never" : AppTime.ToLocal(value).ToString("dd-MM-yyyy HH:mm");

    private static TallyClientLink LinkPreview(TallyCompanyInfo company) =>
        new()
        {
            CompanyKey = company.CompanyKey,
            CompanyName = company.Name,
            CompanyNumber = company.CompanyNumber,
            Gstin = company.Gstin,
            StateName = company.StateName,
            BooksFrom = company.BooksFrom,
            StartingFrom = company.StartingFrom,
            FinancialYear = company.FinancialYear,
            GroupCount = company.GroupCount,
            LedgerCount = company.LedgerCount,
            VoucherTypeCount = company.VoucherTypeCount,
            StockItemCount = company.StockItemCount,
            UnitCount = company.UnitCount,
            GodownCount = company.GodownCount,
            VoucherCount = company.VoucherCount,
            LastSeenUtc = company.LastSeenUtc
        };

    private void OnClientContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ActiveClientContext.CurrentClient) or nameof(ActiveClientContext.DisplayNameMode)))
            return;

        UnlinkConfirmationPending = false;
        AutoImportFolderStatus = _clientContext.CurrentClient is null
            ? "Open a client to use Auto Import From Folder."
            : "Use Auto Import From Folder to force a manual re-scan of this client's linked Automation Folder.";
        AutoImportFromFolderCommand.RaiseCanExecuteChanged();
        ClearAllDataCommand.RaiseCanExecuteChanged();
        ExportDiagnosticExcelCommand.RaiseCanExecuteChanged();
        ExportDiagnosticCsvCommand.RaiseCanExecuteChanged();
        ImportReviewExcelCommand.RaiseCanExecuteChanged();

        if (e.PropertyName == nameof(ActiveClientContext.CurrentClient))
            LoadTallyAutoLifecycle(_clientContext.CurrentClient);

        if (e.PropertyName == nameof(ActiveClientContext.CurrentClient) && _clientContext.CurrentClient is { } openedClient)
        {
            // This runs before shell navigation. Populate both Dashboard panels with
            // explicit zero/default rows now so opening a large client never shows a
            // blank Accounting Activity/Auto Import area while cache numbers arrive.
            ApplyImmediateDashboardState(openedClient, force: true);
            QueueAutoImportPanelRefresh(openedClient);
        }

        // Client opening is followed by shell navigation to Dashboard, where the
        // dashboard is loaded once. Avoid doing the same full accounting read twice.
        // Display-name changes can refresh immediately because they are user-visible.
        if (e.PropertyName == nameof(ActiveClientContext.DisplayNameMode))
            Refresh();
    }

    private void OnImportActivityChanged(object? sender, EventArgs e)
    {
        // Import Activity is persisted already. The visible Dashboard is refreshed by
        // the shell's detailed Auto Import event or the next Dashboard navigation;
        // do not reload every accounting table while another page is active.
    }

    private void OnTallyCoordinatorChanged(object? sender, EventArgs e)
    {
        RefreshTallySection();
        // Fix37: coordinator changes update connection/authority UI only. They must
        // never trigger hidden Statistics/supporting-report traffic.
        if (!IsTallyAutoSyncOff && Volatile.Read(ref _autoTallySyncPendingMask) != 0 &&
            !_tallyCoordinator.IsBusy)
            ScheduleTallyAutoSync();
    }

    private void OnTallyDisplayCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasTallyMetrics));
        OnPropertyChanged(nameof(HasTallyReportRows));
        OnPropertyChanged(nameof(HasTallyStatisticsMetrics));
    }

    private void OnTallyExportCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        NotifyTallyExportAvailability();

    private void NotifyTallyExportAvailability()
    {
        OnPropertyChanged(nameof(HasBankExportData));
        OnPropertyChanged(nameof(HasPurchaseExportData));
        OnPropertyChanged(nameof(HasSalesExportData));
        OnPropertyChanged(nameof(HasCashExportData));
        OnPropertyChanged(nameof(HasJournalExportData));
        OnPropertyChanged(nameof(HasAnyTallyExportData));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Interlocked.Increment(ref _autoTallySyncScheduleVersion);
        Interlocked.Exchange(ref _autoTallySyncPendingMask, 0);
        _clientContext.PropertyChanged -= OnClientContextChanged;
        _activityContext.Changed -= OnImportActivityChanged;
        _tallyCoordinator.Changed -= OnTallyCoordinatorChanged;
        _automationService.ProgressChanged -= OnAutomationProgressChanged;
        _automationService.ClientReviewWorkbookChanged -= OnClientReviewWorkbookChanged;
        _workspaceStateCache.SnapshotReady -= OnWorkspaceSnapshotReady;
        _liveChangeHub.Changed -= OnAccountingLiveChanged;
        TallyMetrics.CollectionChanged -= OnTallyDisplayCollectionChanged;
        TallyReportRows.CollectionChanged -= OnTallyDisplayCollectionChanged;
        TallyStatisticsMetrics.CollectionChanged -= OnTallyDisplayCollectionChanged;
        BankTallyExportMonths.CollectionChanged -= OnTallyExportCollectionChanged;
        PurchaseTallyExportMonths.CollectionChanged -= OnTallyExportCollectionChanged;
        SalesTallyExportMonths.CollectionChanged -= OnTallyExportCollectionChanged;
        CashTallyExportMonths.CollectionChanged -= OnTallyExportCollectionChanged;
        JournalTallyExportMonths.CollectionChanged -= OnTallyExportCollectionChanged;
    }
    private sealed record PendingTallyPreferenceWrite(
        long ClientId,
        string PreferenceKey,
        string PreferenceValue);

    private sealed record MonthRangeGestureState(
        int AnchorMonthIndex,
        IReadOnlyDictionary<string, bool[]> SelectedStatesByModule);

}
