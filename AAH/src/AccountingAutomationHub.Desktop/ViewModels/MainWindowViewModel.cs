using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Application.Imports;
using AccountingAutomationHub.Application.Settings;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Desktop.Views;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Imports;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Accounting;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Imports;
using AccountingAutomationHub.Infrastructure.Gst;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Finance;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Persistence;
using AccountingAutomationHub.Infrastructure.Sales;
using AccountingAutomationHub.Infrastructure.Settings;
using AccountingAutomationHub.Infrastructure.Tally;
using AccountingAutomationHub.Infrastructure.Tax;
using AccountingAutomationHub.Domain.Tally;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class TopClientWorkspaceChoice
{
    public string IdentityKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public IReadOnlyList<ClientProfile> Profiles { get; init; } = Array.Empty<ClientProfile>();
}

public sealed class TopFinancialYearChoice
{
    public long ClientId { get; init; }
    public string FinancialYear { get; init; } = string.Empty;
    public string DisplayText => FinancialYear;
}

public sealed class MainWindowViewModel :
    ViewModelBase,
    IDisposable
{
    private const string DisplayNameSettingKey =
        "DashboardClientNameMode";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo[]> GlobalSearchScalarProperties = new();

    private readonly ActiveClientContext _clientContext;
    private readonly SqliteClientRepository _clientRepository;
    private readonly SqliteClientPreferenceRepository _clientPreferenceRepository;
    private readonly SqliteBankingRepository _bankingRepository;
    private readonly GstinProfileLookupService _gstinProfileLookupService;
    private readonly EReturnsRootContext _eReturnsRootContext;
    private readonly ClientProfileSyncContext _profileSyncContext;
    private readonly ImportActivityContext _importActivityContext;
    private readonly SqliteImportActivityRepository _importActivityRepository;
    private readonly SqlitePurchaseRecordRepository _purchaseRecordRepository;
    private readonly SqliteSalesRecordRepository _salesRecordRepository;
    private readonly SqliteMasterRepository _masterRepository;
    private readonly SqliteStockMasterRepository _stockMasterRepository;
    private readonly SqliteActionRequiredRepository _actionRequiredRepository;
    private readonly SqliteSalesMappingRepository _salesMappingRepository;
    private readonly SqlitePurchaseMappingRepository _purchaseMappingRepository;
    private readonly SqliteJournalRepository _journalRepository;
    private readonly SqliteGstRepository _gstRepository;
    private readonly AccountingRefreshCoordinator _refreshCoordinator;
    private readonly AccountingLiveChangeHub _liveChangeHub;
    private readonly SqliteTallyRepository _tallyRepository;
    private readonly TallyLiveConnectionCoordinator _tallyCoordinator;
    private readonly TallyApplicationLaunchService _tallyApplicationLaunchService = new();
    private readonly ApplicationPreferenceService _applicationPreferences;
    private readonly BackgroundAutomationService _automationService;
    private readonly SecureLanServerService? _secureLanServerService;
    private readonly ClientWorkspaceStateCache _workspaceStateCache;
    private bool _accountingPresentationActive = true;
    private bool _financialTaxPresentationActive;
    private int _deferredWorkspacePreparationStarted;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _startupStockVerified = new(StringComparer.OrdinalIgnoreCase);
    private readonly TransactionAutomationService _transactionAutomationService;
    private readonly LedgerTotalService _ledgerTotalService;
    private readonly StockMasterLiveProjectionService _stockMasterLiveProjectionService;
    private readonly SqliteGovernanceRepository _governanceRepository;
    private bool _lastBankingTallyConnectionState;
    private int _workspaceNavigationRefreshVersion;
    private readonly Dictionary<string, long> _presentationWarmGenerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PageViewModel, string> _preparedPagePresentations = new();

    private readonly ClientManagementViewModel _clientsViewModel;
    private readonly DashboardViewModel _dashboardViewModel;
    private readonly BankingViewModel _bankingViewModel;
    private readonly PurchaseImportViewModel _purchaseViewModel;
    private readonly SalesImportViewModel _salesViewModel;
    private readonly MasterCentreViewModel _masterViewModel;
    private readonly StockMasterViewModel _stockMasterViewModel;
    private readonly FixedAssetsViewModel _fixedAssetsViewModel;
    private readonly JournalViewModel _journalViewModel;
    private readonly GstReconciliationViewModel _gstViewModel;
    private readonly PortalLoginsViewModel _portalLoginsViewModel;
    private readonly TaxCreditReconciliationViewModel _taxCreditViewModel;
    private readonly TallyClientViewModel _tallyClientViewModel;
    private readonly MobileLinkViewModel _mobileLinkViewModel;
    private readonly AccountingStatementViewModel _balanceSheetViewModel;
    private readonly AccountingStatementViewModel _profitAndLossViewModel;
    private readonly FinancialStatementsViewModel _financialStatementsViewModel;
    private readonly IncomeTaxViewModel _incomeTaxViewModel;
    private readonly NavigationItem _dashboardNavigationItem;
    private readonly NavigationItem _bankingNavigationItem;
    private readonly NavigationItem _cashEntryNavigationItem;
    private readonly NavigationItem _purchaseNavigationItem;
    private readonly NavigationItem _salesNavigationItem;
    private readonly NavigationItem _ledgerMasterNavigationItem;
    private readonly NavigationItem _stockMasterNavigationItem;
    private readonly NavigationItem _fixedAssetsNavigationItem;
    private readonly NavigationItem _journalNavigationItem;
    private readonly NavigationItem _balanceSheetNavigationItem;
    private readonly NavigationItem _profitAndLossNavigationItem;
    private readonly NavigationItem _financialStatementsNavigationItem;
    private readonly NavigationItem _incomeTaxNavigationItem;
    private bool _showBalanceSheet;
    private bool _showProfitAndLoss;
    private string _financialStatementToggleScope = string.Empty;
    private readonly NavigationItem _gstNavigationItem;
    private readonly NavigationItem _portalLoginsNavigationItem;
    private readonly NavigationItem _taxCreditNavigationItem;
    private readonly NavigationItem _tallyNavigationItem;
    private readonly NavigationItem _mobileLinkNavigationItem;
    private readonly NavigationItem _nilReturnsNavigationItem;
    private readonly FinanceControlCenterViewModel _financeControlCenterViewModel;
    private readonly NavigationItem _financeNavigationItem;
    private readonly ActionCenterViewModel _actionCenterViewModel;
    private readonly NavigationItem _actionCenterNavigationItem;
    private SettingsViewModel? _settingsViewModel;
    private readonly NavigationItem _settingsNavigationItem;
    private readonly TeamViewModel _teamViewModel;
    private readonly WorkViewModel _workViewModel;
    private readonly NotificationCenterViewModel _notificationCenterViewModel;
    private readonly RemoteAssistViewModel? _remoteAssistViewModel;
    private AdministrationViewModel? _administrationViewModel;
    private readonly NavigationItem _teamNavigationItem;
    private readonly NavigationItem _workNavigationItem;
    private readonly NavigationItem _notificationNavigationItem;
    private readonly NavigationItem? _remoteAssistNavigationItem;
    private readonly NavigationItem? _administrationNavigationItem;

    private CancellationTokenSource? _profileSyncCancellation;
    private bool _changingWorkspace;
    private int _navigationPrepareGeneration;
    private int _visiblePresentationGeneration;
    private int _safePresentationWarmGeneration;
    private NavigationItem? _selectedNavigationItem;
    private PageViewModel? _currentPage;
    private string _applicationState =
        "Client Directory ready.";
    private TopClientWorkspaceChoice? _selectedTopClientChoice;
    private TopFinancialYearChoice? _selectedTopFinancialYearChoice;
    private bool _refreshingTopWorkspaceSelectors;
    private BranchAccountingWorkspace? _selectedBranchWorkspace;
    private bool _refreshingBranchWorkspaces;
    private long _privateBranchParentClientId;
    private bool _isPrivateBranchActive;
    private readonly SqliteInternalBranchAccountRepository _privateBranchAccounts = new();
    private string _branchWorkspaceProblem = string.Empty;

    public MainWindowViewModel()
        : this(null, null, null, null)
    {
    }

    public BackgroundAutomationService AutomationService => _automationService;

    // Fix95: Accounting and Audit are separate workspaces with separate client bases. Explicit Audit links may read Accounting/Tally evidence without merging client ownership.
    // Audit consumes these existing repository/service instances; it never starts a second
    // Tally connection, Auto Import watcher, GST source watcher, or accounting database.
    public event EventHandler? SharedAuthorityChanged;
    // Fix92: Audit consumes the same already-synchronized Tally Voucher authority.
    // This event is notification-only; Audit never opens its own Tally connection.
    public event EventHandler? SharedTallyAuthorityChanged;
    public ActiveClientContext SharedClientContext => _clientContext;
    public SqliteBankingRepository SharedBankingRepository => _bankingRepository;
    public SqlitePurchaseRecordRepository SharedPurchaseRepository => _purchaseRecordRepository;
    public SqliteSalesRecordRepository SharedSalesRepository => _salesRecordRepository;
    public SqliteJournalRepository SharedJournalRepository => _journalRepository;
    public SqliteGstRepository SharedGstRepository => _gstRepository;
    public SqliteMasterRepository SharedMasterRepository => _masterRepository;
    public SqliteTallyRepository SharedTallyRepository => _tallyRepository;
    public ApplicationPreferenceService SharedPreferences => _applicationPreferences;
    public SettingsViewModel SharedSettingsViewModel => GetSettingsViewModel();
    public FinancialStatementsViewModel SharedFinancialStatementsViewModel => _financialStatementsViewModel;

    public System.Windows.Media.ImageSource? WorkspaceMobileQrImage => _clientsViewModel.HomeMobileQrImage;
    public string WorkspaceMobileQrText => _clientsViewModel.HomeMobileQrText;
    public string WorkspaceMobileCode => _clientsViewModel.HomeMobileCode;
    public string WorkspaceMobileUserCaption => _clientsViewModel.HomeMobileUserCaption;
    public AsyncRelayCommand RefreshWorkspaceMobileQrCommand { get; }

    public MainWindowViewModel(
        IApplicationPreferenceRepository? preferenceRepository,
        ApplicationPreferenceService? applicationPreferences,
        BackgroundAutomationService? automationService,
        SecureLanServerService? secureLanServerService)
    {
        _clientRepository =
            new SqliteClientRepository();
        _clientRepository.Initialize();

        var applicationPreferenceRepository =
            preferenceRepository ?? new SqliteApplicationPreferenceRepository();
        applicationPreferenceRepository.Initialize();
        _applicationPreferences =
            applicationPreferences ?? new ApplicationPreferenceService(applicationPreferenceRepository);
        _automationService =
            automationService ?? new BackgroundAutomationService(_applicationPreferences);
        _secureLanServerService = secureLanServerService;

        _clientContext =
            new ActiveClientContext();
        _clientContext.SetDisplayNameMode(
            _applicationPreferences.Get(
                DisplayNameSettingKey));
        _clientContext.PropertyChanged +=
            OnClientContextPropertyChanged;

        var purchaseImportService =
            new PurchaseImportService();
        var purchaseManualImportService =
            new PurchaseManualImportService();
        var purchaseAutoImportService =
            new PurchaseAutoImportService();

        _purchaseRecordRepository =
            new SqlitePurchaseRecordRepository();
        _purchaseRecordRepository.Initialize();

        _clientPreferenceRepository =
            new SqliteClientPreferenceRepository();
        _clientPreferenceRepository.Initialize();
        var clientPreferenceRepository = _clientPreferenceRepository;

        _importActivityRepository =
            new SqliteImportActivityRepository();
        _importActivityRepository.Initialize();

        _importActivityContext =
            new ImportActivityContext();
        _importActivityContext.Changed +=
            OnImportActivityChanged;

        _eReturnsRootContext =
            new EReturnsRootContext(
                applicationPreferenceRepository);

        _gstinProfileLookupService =
            new GstinProfileLookupService();
        _gstinProfileLookupService.Initialize();

        _profileSyncContext =
            new ClientProfileSyncContext();

        var salesAutoImportService =
            new SalesAutoImportService();

        _salesRecordRepository =
            new SqliteSalesRecordRepository();
        _salesRecordRepository.Initialize();

        _masterRepository = new SqliteMasterRepository();
        _masterRepository.Initialize();

        _stockMasterRepository = new SqliteStockMasterRepository();
        _stockMasterRepository.Initialize();

        _actionRequiredRepository = new SqliteActionRequiredRepository();
        _actionRequiredRepository.Initialize();

        _salesMappingRepository = new SqliteSalesMappingRepository();
        _salesMappingRepository.Initialize();

        _purchaseMappingRepository = new SqlitePurchaseMappingRepository();
        _purchaseMappingRepository.Initialize();

        _journalRepository = new SqliteJournalRepository();
        _journalRepository.Initialize();

        _gstRepository = new SqliteGstRepository();
        _gstRepository.Initialize();

        _bankingRepository = new SqliteBankingRepository();
        _bankingRepository.Initialize();

        // Schema/index creation is complete. Ask SQLite to refresh planner
        // statistics only when useful; this is fast/no-op in the common case.
        SqliteDatabaseOptimizer.OptimizeOnce();

        _governanceRepository = new SqliteGovernanceRepository();
        _governanceRepository.Initialize();
        var governanceRepository = _governanceRepository;
        _transactionAutomationService = new TransactionAutomationService(
            _bankingRepository,
            _masterRepository,
            _journalRepository,
            governanceRepository);
        // Fix137 R23: recurring Cash/Journal is now explicit direct generation from its
        // independent panel. Preserve legacy profile rows for audit/backward compatibility,
        // but never let the retired hourly scheduler silently create duplicate entries.
        // Direct generation has no background recurring timer.

        // Keep one mapping service instance so startup cache repair, Banking and the
        // review-workbook sync all use the same parser/service implementation.
        var bankLedgerMappingService = new BankLedgerMappingService(_bankingRepository, _masterRepository, governanceRepository);

        var journalGenerationService =
            new GstJournalGenerationService(
                _journalRepository,
                _purchaseRecordRepository,
                _salesRecordRepository);

        var masterSynchronizationService =
            new MasterSynchronizationService(
                _masterRepository,
                _purchaseRecordRepository,
                _salesRecordRepository,
                _journalRepository);

        var reconciliationService =
            new GstReconciliationService(
                _gstRepository,
                _journalRepository,
                _purchaseRecordRepository);

        _refreshCoordinator =
            new AccountingRefreshCoordinator(
                masterSynchronizationService,
                journalGenerationService,
                reconciliationService,
                _gstRepository,
                _journalRepository);

        _liveChangeHub = new AccountingLiveChangeHub();
        _liveChangeHub.Changed += (_, change) => _secureLanServerService?.NotifyLocalDataChanged(change.ClientId, change.FinancialYear, $"{change.Area}: {change.Reason}");

        _ledgerTotalService = new LedgerTotalService(
            _bankingRepository,
            _purchaseRecordRepository,
            _salesRecordRepository,
            _journalRepository,
            _masterRepository,
            clientPreferenceRepository);

        _workspaceStateCache = new ClientWorkspaceStateCache(
            _bankingRepository,
            _purchaseRecordRepository,
            _salesRecordRepository,
            _journalRepository,
            _gstRepository,
            _masterRepository,
            _stockMasterRepository,
            _actionRequiredRepository,
            bankLedgerMappingService,
            _ledgerTotalService,
            _clientRepository);

        _purchaseViewModel =
            new PurchaseImportViewModel(
                purchaseImportService,
                purchaseManualImportService,
                purchaseAutoImportService,
                _purchaseRecordRepository,
                _purchaseMappingRepository,
                clientPreferenceRepository,
                applicationPreferenceRepository,
                _eReturnsRootContext,
                _clientContext,
                _importActivityRepository,
                _importActivityContext);
        var purchaseStockNormalizationService =
            new PurchaseStockNormalizationService(
                _stockMasterRepository,
                _actionRequiredRepository);
        _purchaseViewModel.AttachParityServices(
            _masterRepository, _refreshCoordinator, _ledgerTotalService, _liveChangeHub,
            _stockMasterRepository, purchaseStockNormalizationService, _automationService);

        _balanceSheetViewModel = new AccountingStatementViewModel(
            profitAndLoss: false,
            _clientContext,
            _masterRepository,
            _ledgerTotalService,
            _bankingRepository,
            _journalRepository,
            _workspaceStateCache,
            _liveChangeHub);
        _profitAndLossViewModel = new AccountingStatementViewModel(
            profitAndLoss: true,
            _clientContext,
            _masterRepository,
            _ledgerTotalService,
            _bankingRepository,
            _journalRepository,
            _workspaceStateCache,
            _liveChangeHub);
        _financialStatementsViewModel = new FinancialStatementsViewModel(
            _clientContext,
            _masterRepository,
            _stockMasterRepository,
            _ledgerTotalService,
            _applicationPreferences,
            _workspaceStateCache);
        _incomeTaxViewModel = new IncomeTaxViewModel(_clientContext, _applicationPreferences);

        _tallyRepository = new SqliteTallyRepository();
        _tallyRepository.Initialize();

        var tallyHttpService = new TallyHttpService(
            _applicationPreferences.Get(ApplicationSettingKeys.TallyHost),
            _applicationPreferences.GetInt(ApplicationSettingKeys.TallyPort, 9000));
        var tallyMasterImportService = new TallyMasterImportService();
        var tallyLiveMasterSyncService =
            new TallyLiveMasterSynchronizationService(
                _masterRepository,
                _stockMasterRepository,
                tallyMasterImportService);
        var tallyBankImportService =
            new TallyBankVoucherImportService(
                _bankingRepository,
                _masterRepository,
                _tallyRepository,
                tallyHttpService,
                tallyLiveMasterSyncService,
                clientPreferenceRepository);
        var tallyAccountingExportService =
            new TallyAccountingVoucherExportService(
                _bankingRepository,
                _purchaseRecordRepository,
                _salesRecordRepository,
                _journalRepository,
                _masterRepository,
                _stockMasterRepository,
                _tallyRepository,
                tallyHttpService,
                tallyLiveMasterSyncService);
        var bankTransferReconciliationService =
            new BankTransferReconciliationService(_bankingRepository, _masterRepository);
        var tallyBankStatementReconciliationService =
            new TallyBankStatementReconciliationService(
                _bankingRepository,
                _tallyRepository,
                tallyHttpService,
                _masterRepository,
                bankTransferReconciliationService,
                tallyHostEnabled: !NetworkClientRuntime.IsNetworkClient);
        _automationService.AttachTallyBankStatementReconciliationService(tallyBankStatementReconciliationService);
        _automationService.AttachAccountingRefreshCoordinator(_refreshCoordinator);

        _tallyCoordinator =
            new TallyLiveConnectionCoordinator(
                _clientContext,
                _clientRepository,
                _tallyRepository,
                _stockMasterRepository,
                tallyHttpService,
                tallyLiveMasterSyncService,
                tallyBankImportService,
                tallyAccountingExportService,
                tallyBankStatementReconciliationService,
                _refreshCoordinator,
                _automationService,
                _liveChangeHub);
        _automationService.AttachLocalPreparationReadyProvider(() => _workspaceStateCache.IsStartupPreparationComplete);
        // FIXED41 R5: restore the proven Tally-last startup boundary without reintroducing the
        // old runtime interlock. The coordinator releases only after workspace startup scheduling,
        // central source discovery and startup recovery have completed once. Its own one-time latch
        // then stays open permanently; normal Auto Import/Bank queues can refill without blocking
        // later five-second ALTERID synchronization.
        _tallyCoordinator.AttachLocalPreparationReadyProvider(() =>
            _workspaceStateCache.IsStartupPreparationComplete && _automationService.IsInitialLocalPreparationComplete);
        _tallyCoordinator.Changed += OnTallyCoordinatorChanged;
        _financialStatementsViewModel.AttachTallyCoordinator(_tallyCoordinator);
        _automationService.AttachTallyConnectionStateProvider(() => _tallyCoordinator.IsConnected);

        var salesStockNormalizationService =
            new SalesStockNormalizationService(
                _stockMasterRepository,
                _actionRequiredRepository);

        _stockMasterLiveProjectionService = new StockMasterLiveProjectionService(
            _purchaseRecordRepository,
            _salesRecordRepository,
            purchaseStockNormalizationService,
            salesStockNormalizationService,
            _liveChangeHub);

        _workspaceStateCache.AttachLiveChangeHub(_liveChangeHub);
        _liveChangeHub.Changed += OnAccountingLiveCacheChanged;

        _salesViewModel =
            new SalesImportViewModel(
                salesAutoImportService,
                new SalesManualImportService(),
                _salesRecordRepository,
                _salesMappingRepository,
                _masterRepository,
                salesStockNormalizationService,
                _stockMasterRepository,
                _actionRequiredRepository,
                _eReturnsRootContext,
                _clientContext,
                _importActivityRepository,
                _importActivityContext,
                _refreshCoordinator,
                _liveChangeHub,
                _applicationPreferences,
                _automationService);

        _masterViewModel =
            new MasterCentreViewModel(
                _masterRepository,
                tallyMasterImportService,
                _refreshCoordinator,
                _clientContext,
                _liveChangeHub,
                governanceRepository,
                _tallyCoordinator,
                new PreviousFinancialYearMasterImportService(_clientRepository, _masterRepository, _stockMasterRepository),
                _automationService);

        _stockMasterViewModel =
            new StockMasterViewModel(
                _stockMasterRepository,
                tallyMasterImportService,
                _clientContext,
                _liveChangeHub,
                _tallyCoordinator,
                _automationService);

        _journalViewModel =
            new JournalViewModel(
                _journalRepository,
                _masterRepository,
                _ledgerTotalService,
                _gstRepository,
                _refreshCoordinator,
                _liveChangeHub,
                new ManualJournalBulkImportService(),
                _clientContext,
                _transactionAutomationService,
                _automationService);

        var gstr3bImportService = new Gstr3bPdfImportService(_gstRepository);
        var gstLedgerImportService = new GstLedgerImportService(_gstRepository);
        _gstViewModel =
            new GstReconciliationViewModel(
                _gstRepository,
                _journalRepository,
                gstr3bImportService,
                gstLedgerImportService,
                new GstPortalAutoImportService(
                    _gstRepository,
                    gstr3bImportService,
                    gstLedgerImportService),
                _refreshCoordinator,
                reconciliationService,
                _eReturnsRootContext,
                _clientContext,
                _liveChangeHub,
                _automationService);

        var taxCreditRepository = new SqliteTaxCreditRepository();
        taxCreditRepository.Initialize();
        _taxCreditViewModel = new TaxCreditReconciliationViewModel(
            _clientContext,
            taxCreditRepository,
            _salesRecordRepository,
            _journalRepository,
            _masterRepository,
            _automationService);

        var tallyExportSelectionContext = new TallyExportSelectionContext();

        _bankingViewModel =
            new BankingViewModel(
                _bankingRepository,
                new BankStatementImportService(_bankingRepository, _masterRepository),
                new BankReconciliationService(_bankingRepository),
                bankTransferReconciliationService,
                bankLedgerMappingService,
                new CashEntryService(_bankingRepository, _masterRepository),
                new BankingWorkbookReportService(_bankingRepository, _masterRepository),
                _masterRepository,
                _ledgerTotalService,
                new AdvancedClosingCashRepairService(_ledgerTotalService, _bankingRepository, _journalRepository, _masterRepository, clientPreferenceRepository),
                _clientContext,
                _importActivityRepository,
                _importActivityContext,
                tallyExportSelectionContext,
                new PdfBankStatementPipelineService(_applicationPreferences),
                _liveChangeHub,
                _workspaceStateCache,
                clientPreferenceRepository,
                _applicationPreferences.GetBool(ApplicationSettingKeys.BankingShowMappedDefault),
                _automationService,
                _transactionAutomationService,
                tallyBankStatementReconciliationService,
                () => _tallyCoordinator.IsConnected,
                () =>
                {
                    var currentClient = _clientContext.CurrentClient;
                    return currentClient is not null && _tallyRepository.GetLinkForClient(currentClient.Id) is not null;
                });

        _clientsViewModel =
            new ClientManagementViewModel(
                _clientRepository,
                _gstinProfileLookupService,
                _eReturnsRootContext,
                _clientContext,
                _profileSyncContext,
                _importActivityRepository,
                applicationPreferenceRepository,
                governanceRepository,
                _masterRepository,
                _secureLanServerService,
                _automationService);
        RefreshWorkspaceMobileQrCommand = new AsyncRelayCommand(() => _clientsViewModel.RefreshWorkspaceMobileQrAsync());
        _clientsViewModel.WorkspaceOpenRequested +=
            OnClientWorkspaceOpenRequested;

        _clientsViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClientManagementViewModel.HomeMobileQrImage)) OnPropertyChanged(nameof(WorkspaceMobileQrImage));
            if (e.PropertyName == nameof(ClientManagementViewModel.HomeMobileQrText)) OnPropertyChanged(nameof(WorkspaceMobileQrText));
            if (e.PropertyName == nameof(ClientManagementViewModel.HomeMobileCode)) OnPropertyChanged(nameof(WorkspaceMobileCode));
            if (e.PropertyName == nameof(ClientManagementViewModel.HomeMobileUserCaption)) OnPropertyChanged(nameof(WorkspaceMobileUserCaption));
        };

        _portalLoginsViewModel = new PortalLoginsViewModel(
            _clientContext,
            clientPreferenceRepository,
            _automationService);

        _dashboardViewModel =
            new DashboardViewModel(
                _clientContext,
                _importActivityRepository,
                _importActivityContext,
                _tallyCoordinator,
                _refreshCoordinator,
                _bankingRepository,
                _purchaseRecordRepository,
                _salesRecordRepository,
                _journalRepository,
                _gstRepository,
                _masterRepository,
                _stockMasterRepository,
                _actionRequiredRepository,
                _tallyRepository,
                tallyExportSelectionContext,
                _automationService,
                clientPreferenceRepository,
                _workspaceStateCache,
                _liveChangeHub,
                _applicationPreferences,
                bankLedgerMappingService,
                governanceRepository,
                _applicationPreferences.GetBool(ApplicationSettingKeys.TallySelectAllDefault));

        _dashboardNavigationItem =
            new NavigationItem(
                "Dashboard",
                "⌂",
                "Detailed selected-client overview",
                () => _dashboardViewModel);

        var cashEntryViewModel =
            new CashEntryViewModel(
                _bankingViewModel);

        _bankingNavigationItem =
            new NavigationItem(
                "Banking",
                "B",
                "Bank Statements, balances, transfers, mapping and reports",
                () => _bankingViewModel);

        _cashEntryNavigationItem =
            new NavigationItem(
                "Cash Entry",
                "C",
                "Cash Payment, Cash Receipt, Journal and mapping",
                () => cashEntryViewModel);

        _purchaseNavigationItem =
            new NavigationItem(
                "Purchase",
                "P",
                "Purchase, RCM and Debit Note records",
                () => _purchaseViewModel);

        _salesNavigationItem =
            new NavigationItem(
                "Sales",
                "S",
                "Sales and Credit Note records",
                () => _salesViewModel);

        _ledgerMasterNavigationItem =
            new NavigationItem(
                "Ledger Master",
                "L",
                "Tally Groups, Ledgers, Voucher Types and accounting masters",
                () => _masterViewModel);

        _stockMasterNavigationItem =
            new NavigationItem(
                "Stock Master",
                "M",
                "Tally Stock Items, Groups, Units, Godowns and conversion mappings",
                () => _stockMasterViewModel);

        _journalNavigationItem =
            new NavigationItem(
                "Journal",
                "J",
                "Generated and manual GST Journal vouchers",
                () => _journalViewModel);

        _balanceSheetNavigationItem =
            new NavigationItem(
                "Balance Sheet",
                "BS",
                "Tally opening balances plus AAH imported debit/credit movement",
                () => _balanceSheetViewModel);

        _profitAndLossNavigationItem =
            new NavigationItem(
                "P&L",
                "PL",
                "Tally opening balances plus AAH imported debit/credit movement",
                () => _profitAndLossViewModel);

        _financialStatementsNavigationItem =
            new NavigationItem(
                "Financial Statements",
                "FS",
                "Basic Info, FY/PY TB, Stock, Debtors, Creditors, Notes and complete exports",
                () => _financialStatementsViewModel,
                showToggle: true,
                toggleChanged: enabled =>
                {
                    if (_clientContext.CurrentClient is not null && _financialStatementsViewModel.FinancialStatementsEnabled != enabled)
                        _financialStatementsViewModel.FinancialStatementsEnabled = enabled;
                });
        _financialStatementsViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FinancialStatementsViewModel.FinancialStatementsEnabled))
                _financialStatementsNavigationItem.SetToggleValue(_financialStatementsViewModel.FinancialStatementsEnabled, notifyAction: false);
        };

        _incomeTaxNavigationItem =
            new NavigationItem(
                "Income Tax",
                "IT",
                "Form 3CB preparation and future Income Tax reports",
                () => _incomeTaxViewModel);

        _gstNavigationItem =
            new NavigationItem(
                "GST",
                "G",
                "GSTR-3B and portal ledger reconciliation",
                () => { _gstViewModel.SelectedTabIndex = 0; return _gstViewModel; });

        _portalLoginsNavigationItem =
            new NavigationItem(
                "Portal Logins",
                "↗",
                "GST and Income Tax credentials, assisted Chrome login and login status",
                () => { _portalLoginsViewModel.PrepareForDisplay(); return _portalLoginsViewModel; });

        _taxCreditNavigationItem =
            new NavigationItem(
                "26AS",
                "26",
                "Form 26AS tax-credit import and Books reconciliation",
                () => _taxCreditViewModel);

        _tallyClientViewModel = new TallyClientViewModel(
            _clientContext, _tallyRepository, _tallyCoordinator, _governanceRepository);
        _tallyNavigationItem =
            new NavigationItem(
                "Tally",
                "TY",
                "Client-specific Tally connection, authority and deleted-voucher re-import",
                () => { _tallyClientViewModel.RefreshLocal(); return _tallyClientViewModel; });

        _mobileLinkViewModel = new MobileLinkViewModel(_clientContext, _secureLanServerService);
        _mobileLinkNavigationItem =
            new NavigationItem(
                "Mobile Link",
                "ML",
                "Client/FY-specific phone link, live status and invoice mapping queue",
                () => { _mobileLinkViewModel.PrepareForDisplay(); return _mobileLinkViewModel; });

        _nilReturnsNavigationItem =
            new NavigationItem(
                "Nil Returns",
                "NR",
                "Verified GSTR-3B Nil/Nil-Sales periods used by GST Journal creation",
                () => { _gstViewModel.SelectedTabIndex = 1; return _gstViewModel; });

        var financeRepository = new SqliteFinanceControlRepository();
        financeRepository.Initialize();
        var fixedAssetDiscoveryService = new FixedAssetDiscoveryService(_masterRepository, _tallyRepository);
        _fixedAssetsViewModel = new FixedAssetsViewModel(_clientContext, financeRepository, fixedAssetDiscoveryService, _liveChangeHub);
        _fixedAssetsNavigationItem =
            new NavigationItem(
                "Fixed Assets",
                "FA",
                "Fixed asset register, depreciation and disposal details",
                () => _fixedAssetsViewModel);
        _financeControlCenterViewModel = new FinanceControlCenterViewModel(
            _clientContext,
            financeRepository,
            _automationService,
            _liveChangeHub,
            _applicationPreferences);
        _financeNavigationItem =
            new NavigationItem(
                "Finance Center",
                "F",
                "Close, search, audit, cash flow, ageing, rules, budgets and controls",
                () => _financeControlCenterViewModel);

        _actionCenterViewModel =
            new ActionCenterViewModel(
                _clientContext,
                _actionRequiredRepository,
                _clientRepository,
                _automationService);
        _actionCenterViewModel.OpenActionRequested += (_, action) => NavigateFromActionCenter(action);
        _actionCenterViewModel.ActionAvailabilityChanged +=
            OnActionAvailabilityChanged;

        _actionCenterNavigationItem =
            new NavigationItem(
                "Action Center",
                "!",
                "Shows only files/imports that failed or were blocked before successful import",
                () => _actionCenterViewModel);

        // Settings performs folder, notification, holiday and LAN diagnostics reads.
        // Do not do that work while the shell is being composed; create the page on first use.
        _settingsNavigationItem =
            new NavigationItem(
                "Settings",
                "⚙",
                "Application, automation folders, PDF, Tally, Security & LAN, shortcut and appearance settings",
                GetSettingsViewModel);

        _teamViewModel = new TeamViewModel(_clientContext);
        _workViewModel = new WorkViewModel(_clientContext);
        _notificationCenterViewModel = new NotificationCenterViewModel();
        _teamNavigationItem = new NavigationItem("Team", "T", "Private/Global/Client chat, presence and shared files", () => _teamViewModel);
        _workNavigationItem = new NavigationItem("Work", "W", "Tasks, Queries, Access Requests and Approvals", () => _workViewModel);
        _notificationNavigationItem = new NavigationItem("Notifications", "N", "Security, messages, remote assist, tasks, imports, updates and system history", () => _notificationCenterViewModel);
        if (NetworkClientRuntime.IsNetworkClient)
        {
            _remoteAssistViewModel = new RemoteAssistViewModel();
            _remoteAssistNavigationItem = new NavigationItem("Remote Assist", "R", "Review, accept, decline or end Main Server Remote Assist requests", () => _remoteAssistViewModel);
        }
        var canAdminister = !NetworkClientRuntime.IsNetworkClient && (
            governanceRepository.GetCurrentUser().RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase) ||
            governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.ManageServer) ||
            governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.ManageUsers) ||
            governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.ManageDevices) ||
            governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.ManageClientAccess) ||
            governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.ViewAudit));
        if (canAdminister)
        {
            // Administration performs multiple security/audit reads and starts live timers.
            // Keep it completely lazy so it cannot delay normal accounting startup.
            _administrationNavigationItem = new NavigationItem(
                "Administration",
                "A",
                "Users, devices, access, security, remote AAH, deployment and audit",
                GetAdministrationViewModel);
        }

        _automationService.ActionRequiredChanged +=
            OnActionAvailabilityChanged;
        _automationService.ClientRegistryChanged +=
            OnAutomationClientRegistryChanged;

        NavigationItems = [];
        TopClientChoices = [];
        TopFinancialYearChoices = [];
        CloseWorkspaceCommand = new RelayCommand(CloseWorkspace);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        RefreshTallyCommand = new AsyncRelayCommand(
            () => _tallyCoordinator.RefreshNowAsync(forceLinkedSynchronization: true),
            () => !_isPrivateBranchActive && !_tallyCoordinator.IsBusy);
        ConfirmTopTallyLinkCommand = new AsyncRelayCommand(
            ConfirmTopTallyLinkAsync,
            () => !_isPrivateBranchActive && _clientContext.CurrentClient is not null && SelectedTallyCompany is not null && !_tallyCoordinator.IsBusy);
        OpenLinkedTallyCommand = new AsyncRelayCommand(
            OpenLinkedTallyAsync,
            CanOpenLinkedTally);
        ShowLinkedTallyInfoCommand = new RelayCommand(ShowLinkedTallyInfo);

        // Startup is intentionally neutral: always open the Client Directory instead
        // of silently reopening the client from the previous session. This avoids
        // posting/importing into a workspace merely because it was open yesterday.
        CloseWorkspaceCore(clearRepository: true);
        RefreshTopWorkspaceSelectors();
        ApplicationState = "Client Directory ready. Preparing client caches in the background…";

        _taxCreditViewModel.AttachWorkspaceCache(_workspaceStateCache);
        _workspaceStateCache.ModuleReady += OnWorkspaceModuleReady;
        _workspaceStateCache.ModuleAuthoritativeReady += OnWorkspaceModuleAuthoritativeReady;
        _workspaceStateCache.ModuleFailed += OnWorkspaceModuleFailed;
        // Fix130: do not start multi-client SQLite/cache preparation synchronously inside
        // the Accounting shell constructor. App starts it immediately after the persistent
        // application host / Workspace Selector paints, before any Client/FY must be opened.
        // The bounded cache service then prepares every non-archived client in Last-Opened order.

        // LAN clients must never probe/control local Tally. They consume only the filtered Main Server
        // snapshot and server-approved collaboration/update APIs. Tally live connectivity remains
        // Main Server/local-desktop authority even if a copied preference says Auto Connect.
        if (!NetworkClientRuntime.IsNetworkClient && _applicationPreferences.GetBool(ApplicationSettingKeys.TallyAutoConnect))
            _tallyCoordinator.Start();
    }

    /// <summary>
    /// Fix130: start bounded global Client/FY preparation after the application host has painted.
    /// No Client/FY selection is required. Safe to call repeatedly; the first call owns startup scheduling.
    /// </summary>
    public void StartDeferredWorkspacePreparation()
    {
        if (Interlocked.Exchange(ref _deferredWorkspacePreparationStarted, 1) != 0) return;
        // Client enumeration and all snapshot reads belong off the WPF dispatcher.
        _ = Task.Run(() =>
        {
            try
            {
                var clients = _clientRepository.GetAll(includeArchived: false).ToArray();
                _workspaceStateCache.StartPreload(clients);
                _financeControlCenterViewModel.StartBackgroundPreparation(clients);
            }
            catch (Exception exception) { PipelineRuntimeDiagnostics.NavigationFailed("Global startup cache", exception); }
        });
    }

    /// <summary>
    /// Accounting and Audit share repositories/background authority, but only the visible shell
    /// should hydrate WPF collections/navigation. Hidden Accounting remains current through the
    /// workspace cache and paints the latest prepared snapshot when reactivated.
    /// </summary>
    public void SetAccountingPresentationActive(bool active)
    {
        if (_accountingPresentationActive == active) return;
        _accountingPresentationActive = active;
        if (!active)
        {
            var closingClient = _clientContext.CurrentClient;
            if (closingClient is not null)
                _workspaceStateCache.CheckpointPresentationClose(
                    closingClient.Id, closingClient.FinancialYear, _workspaceStateCache.RegisteredModules);
            _financeControlCenterViewModel.CheckpointPresentationClose();
            _financeControlCenterViewModel.SetPresentationActive(false);
            Interlocked.Increment(ref _workspaceNavigationRefreshVersion);
            return;
        }

        var client = _clientContext.CurrentClient;
        if (client is not null && _workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var snapshot))
            QueueWarmCurrentWorkspaceAfterPaint(snapshot);
        QueueWorkspaceNavigationRefresh();
    }

    public ObservableCollection<NavigationItem> NavigationItems { get; }

    public RelayCommand CloseWorkspaceCommand { get; }

    public RelayCommand OpenSettingsCommand { get; }

    public AsyncRelayCommand RefreshTallyCommand { get; }
    public AsyncRelayCommand ConfirmTopTallyLinkCommand { get; }
    public AsyncRelayCommand OpenLinkedTallyCommand { get; }
    public RelayCommand ShowLinkedTallyInfoCommand { get; }

    public ObservableCollection<TopClientWorkspaceChoice> TopClientChoices { get; }
    public ObservableCollection<TopFinancialYearChoice> TopFinancialYearChoices { get; }

    public TopClientWorkspaceChoice? SelectedTopClientChoice
    {
        get => _selectedTopClientChoice;
        set
        {
            if (ReferenceEquals(_selectedTopClientChoice, value))
                return;
            _selectedTopClientChoice = value;
            OnPropertyChanged();
            if (_refreshingTopWorkspaceSelectors)
                return;

            RebuildTopFinancialYearChoices(value, _clientContext.CurrentClient?.FinancialYear);
            if (_selectedTopFinancialYearChoice is not null)
                SwitchTopWorkspace(_selectedTopFinancialYearChoice.ClientId);
        }
    }

    public TopFinancialYearChoice? SelectedTopFinancialYearChoice
    {
        get => _selectedTopFinancialYearChoice;
        set
        {
            if (ReferenceEquals(_selectedTopFinancialYearChoice, value))
                return;
            _selectedTopFinancialYearChoice = value;
            OnPropertyChanged();
            if (!_refreshingTopWorkspaceSelectors && value is not null)
                SwitchTopWorkspace(value.ClientId);
        }
    }

    public ObservableCollection<TallyCompanyInfo> TallyCompanies =>
        _tallyCoordinator.OpenCompanies;

    public TallyCompanyInfo? SelectedTallyCompany
    {
        get => _tallyCoordinator.SelectedCompany;
        set
        {
            if (ReferenceEquals(_tallyCoordinator.SelectedCompany, value))
            {
                return;
            }

            _tallyCoordinator.SelectedCompany = value;
            OnPropertyChanged();
            ConfirmTopTallyLinkCommand.RaiseCanExecuteChanged();
            OpenLinkedTallyCommand.RaiseCanExecuteChanged();
        }
    }

    public string TallyConnectionStatus =>
        _tallyCoordinator.ConnectionStatus;

    public bool IsTallyConnected =>
        _tallyCoordinator.IsConnected;

    public bool IsTallyBusy =>
        _tallyCoordinator.IsBusy;

    public NavigationItem? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (HasBranchWorkspaces && value is not null && !NavigationItems.Contains(value))
                return;
            if (!SetProperty(
                    ref _selectedNavigationItem,
                    value) ||
                value is null)
            {
                return;
            }

            var nextPage = value.PageFactory();
            if (ReferenceEquals(CurrentPage, nextPage))
            {
                // Sidebar/workspace rebuilds can temporarily clear and re-select the
                // same NavigationItem.  That is not a real navigation and must not
                // reload Journal/GST/Purchase or start another accounting refresh.
                return;
            }

            var previousPage = CurrentPage;
            CurrentPage = nextPage;
            SchedulePageTransition(previousPage, nextPage);
        }
    }

    public PageViewModel? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(
            ref _currentPage,
            value);
    }

    public bool HasActiveWorkspace =>
        _clientContext.CurrentClient is not null;

    // Branch scopes must never reuse legacy ClientId/FY repositories under a
    // different visual label. Only the unbranched NORMAL scope can post there.
    public ObservableCollection<BranchAccountingWorkspace> BranchWorkspaces { get; } = [];
    public bool HasBranchWorkspaces => BranchWorkspaces.Count > 1;
    public BranchAccountingWorkspace? SelectedBranchWorkspace
    {
        get => _selectedBranchWorkspace;
        set
        {
            if (!BranchWorkspaces.Contains(value) || ReferenceEquals(_selectedBranchWorkspace, value)) return;
            if (value is null || _clientContext.CurrentClient is null) return;
            if (_refreshingBranchWorkspaces) return;
            try
            {
                var client = _clientContext.CurrentClient;
                var parent = _privateBranchParentClientId > 0
                    ? _clientRepository.GetById(_privateBranchParentClientId)
                    : client;
                if (parent is null || parent.IsArchived ||
                    !_governanceRepository.HasClientAccess(Environment.UserName, parent.Id, parent.FinancialYear))
                    throw new InvalidOperationException("The parent Client is not authorised for this user.");
                if (value.IsConsolidated)
                {
                    if (client.Id != parent.Id)
                    {
                        _clientRepository.SetCurrent(parent.Id);
                        _clientContext.SetCurrent(_clientRepository.GetCurrent());
                    }
                    else
                    {
                        _selectedBranchWorkspace = value;
                        OnPropertyChanged();
                        RebuildWorkspaceNavigation("Dashboard");
                    }
                    return;
                }
                if (value.ScopeId == "REVIEW")
                    throw new InvalidOperationException("Branch ownership must be corrected before opening its books.");
                var savedBranches = ClientBranchCatalog.Resolve(parent,
                    _applicationPreferences.Get(ClientBranchCatalog.Key(parent.Id)));
                var selectedBranch = savedBranches.SingleOrDefault(b =>
                    b.BranchId.Equals(value.ScopeId, StringComparison.OrdinalIgnoreCase));
                if (selectedBranch is null)
                    throw new InvalidOperationException("The selected branch no longer belongs to this parent Client.");
                var privateClient = _privateBranchAccounts.GetOrCreate(
                    parent.Id, selectedBranch.BranchId, selectedBranch.Name, selectedBranch.Gstin);
                // The existing Purchase/Sales/Banking/Journal repositories are
                // keyed by ClientId + FY. A private saved ClientId isolates the
                // branch without duplicating the Client Directory entry.
                _clientRepository.SetCurrent(privateClient.Id);
                _clientContext.SetCurrent(_clientRepository.GetCurrent());
            }
            catch (Exception ex)
            {
                _branchWorkspaceProblem = ex.Message;
                ApplicationState = "Branch scope not changed: " + ex.Message;
                OnPropertyChanged(nameof(SelectedBranchWorkspace));
            }
        }
    }

    private void RefreshBranchWorkspaces()
    {
        _refreshingBranchWorkspaces = true;
        try
        {
            BranchWorkspaces.Clear();
            _selectedBranchWorkspace = null;
            _branchWorkspaceProblem = string.Empty;
            _privateBranchParentClientId = 0;
            _isPrivateBranchActive = false;
            var current = _clientContext.CurrentClient;
            if (current is not null && !NetworkClientRuntime.IsNetworkClient)
            {
                var owner = _privateBranchAccounts.FindByChild(current.Id);
                _isPrivateBranchActive = owner is not null;
                var parent = owner is null ? current : _clientRepository.GetById(owner.ParentClientId);
                if (parent is null)
                    throw new InvalidOperationException("The private branch parent was not found.");
                _privateBranchParentClientId = parent.Id;
                var separateBooksEnabled = BranchAccountingModePreference.IsEnabled(
                    _applicationPreferences.Get(BranchAccountingModePreference.Key(parent.Id)));
                var branches = separateBooksEnabled
                    ? ClientBranchCatalog.Resolve(parent,
                        _applicationPreferences.Get(ClientBranchCatalog.Key(parent.Id)))
                    : Array.Empty<ClientBranchRecord>();
                try
                {
                    if (!separateBooksEnabled &&
                        (_isPrivateBranchActive || _privateBranchAccounts.FindByParent(parent.Id).Count > 0))
                        throw new InvalidOperationException(
                            "Private branch accounting already exists but the separate-books setting is disabled. Accounting remains locked pending reconciliation.");
                    foreach (var scope in BranchAccountingWorkspaceCatalog.Build(parent, branches))
                        BranchWorkspaces.Add(scope);
                    if (owner is not null && !BranchWorkspaces.Any(x => x.ScopeId.Equals(owner.BranchId, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Private branch no longer belongs to the registered parent.");
                    _selectedBranchWorkspace = owner is null
                        ? BranchWorkspaces.FirstOrDefault()
                        : BranchWorkspaces.First(x => x.ScopeId.Equals(owner.BranchId, StringComparison.OrdinalIgnoreCase));
                }
                catch (InvalidOperationException ex)
                {
                    _branchWorkspaceProblem = ex.Message;
                    _isPrivateBranchActive = false;
                    BranchWorkspaces.Clear();
                    BranchWorkspaces.Add(new BranchAccountingWorkspace(
                        parent.Id, parent.FinancialYear, "CONSOLIDATED",
                        "Consolidated — review required", "", "", true,
                        ["Dashboard", "Tally Import"], false));
                    BranchWorkspaces.Add(new BranchAccountingWorkspace(
                        parent.Id, parent.FinancialYear, "REVIEW",
                        "Branch configuration blocked", "", "", false, ["Branch Overview"], false));
                    _selectedBranchWorkspace = BranchWorkspaces[0];
                }
            }
            OnPropertyChanged(nameof(SelectedBranchWorkspace));
            OnPropertyChanged(nameof(HasBranchWorkspaces));
        }
        finally { _refreshingBranchWorkspaces = false; }
    }

    public double SidebarWidth => NetworkClientRuntime.IsNetworkClient ? 200d : 260d;

    public string CurrentClient =>
        string.IsNullOrWhiteSpace(_clientContext.CurrentClient?.ClientName)
            ? _clientContext.CurrentClient?.ClientCode ?? string.Empty
            : _clientContext.CurrentClient.ClientName.Trim();

    public long CurrentClientId => _clientContext.CurrentClient?.Id ?? 0;
    public string CurrentFinancialYear => _clientContext.CurrentClient?.FinancialYear ?? string.Empty;


    // Fix137 R46 R9: Ctrl+G and Dashboard All Search share one permission-aware set of
    // destinations. Hidden accounting-activity pages are included because NavigateTo already
    // knows how to open them without making them permanent sidebar items.
    public IReadOnlyList<string> GetGoToDestinations()
    {
        if (HasBranchWorkspaces)
            return NavigationItems.Select(item => item.Title).ToArray();
        var result = NavigationItems
            .Select(item => item.Title)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        static void AddIfMissing(List<string> values, string title)
        {
            if (!values.Contains(title, StringComparer.OrdinalIgnoreCase)) values.Add(title);
        }

        AddIfMissing(result, "Clients");
        AddIfMissing(result, "Settings");
        if (HasActiveWorkspace)
        {
            var client = _clientContext.CurrentClient;
            if (CanViewModule(GovernancePermissions.BankView)) AddIfMissing(result, "Banking");
            if (CanViewModule(GovernancePermissions.ReadData)) AddIfMissing(result, "Cash Entry");
            if (CanViewModule(GovernancePermissions.PurchaseView)) AddIfMissing(result, "Purchase");
            if (CanViewModule(GovernancePermissions.SalesView)) AddIfMissing(result, "Sales");
            if (CanViewModule(GovernancePermissions.StockView)) AddIfMissing(result, "Stock Master");
            if (!NetworkClientRuntime.IsNetworkClient && CanViewModule(GovernancePermissions.ReadData)) AddIfMissing(result, "Fixed Assets");
            if (CanViewModule(GovernancePermissions.JournalView)) AddIfMissing(result, "Journal");
            if (CanViewModule(GovernancePermissions.ReadData))
            {
                AddIfMissing(result, "Balance Sheet");
                AddIfMissing(result, "P&L");
            }
            if (client is not null && HasValidGstin(client) && CanViewModule(GovernancePermissions.GstView))
                AddIfMissing(result, "GST");
        }

        result.Sort(StringComparer.CurrentCultureIgnoreCase);
        return result;
    }

    // Dashboard All Search works only from the already prepared current Client/FY snapshot.
    // It does not issue a new SQLite scan per keypress and therefore cannot recreate the old
    // background-refresh/scroll lag. As each module cache becomes ready it appears in search.
    public IReadOnlyList<GlobalSearchResult> SearchWorkspace(string? query, int limit = 100)
    {
        var search = (query ?? string.Empty).Trim();
        var results = new List<GlobalSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string category, string module, string primary, string secondary, string searchable)
        {
            primary = (primary ?? string.Empty).Trim();
            secondary = (secondary ?? string.Empty).Trim();
            searchable = string.Join(" | ", new[] { category, module, primary, secondary, searchable }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var score = GlobalSearchScore(search, primary, searchable);
            if (score < 0) return;
            var signature = $"{module}\u001f{category}\u001f{primary}\u001f{secondary}";
            if (!seen.Add(signature)) return;
            results.Add(new GlobalSearchResult
            {
                Category = category,
                ModuleTitle = module,
                PrimaryText = primary.Length == 0 ? module : primary,
                SecondaryText = secondary,
                SearchText = search,
                Score = score
            });
        }

        foreach (var destination in GetGoToDestinations())
            Add("Module", destination, destination, "Open module/page", destination);

        var client = _clientContext.CurrentClient;
        if (client is null || search.Length == 0)
            return results.OrderByDescending(item => item.Score)
                .ThenBy(item => item.PrimaryText, StringComparer.CurrentCultureIgnoreCase)
                .Take(Math.Max(1, limit)).ToArray();

        Add("Client", "Dashboard", client.ClientName.Length == 0 ? client.ClientCode : client.ClientName,
            $"{client.ClientCode} · {client.Gstin} · FY {client.FinancialYear}", GlobalSearchRecordText(client));

        if (!_workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var snapshot))
            return results.OrderByDescending(item => item.Score)
                .ThenBy(item => item.PrimaryText, StringComparer.CurrentCultureIgnoreCase)
                .Take(Math.Max(1, limit)).ToArray();

        foreach (var record in snapshot.Masters)
            Add(record.Type.Length == 0 ? "Master" : record.Type, "Ledger Master", record.Name,
                $"Parent: {record.Parent} · Tally ID: {record.TallyMasterId}", GlobalSearchRecordText(record));

        foreach (var record in snapshot.StockItems)
            Add("Stock Item", "Stock Master", record.Name,
                $"Group: {record.StockGroup} · Unit: {record.BaseUnit} · HSN/SAC: {record.HsnSac}", GlobalSearchRecordText(record));

        foreach (var record in snapshot.StockUnits)
            Add("Stock Unit", "Stock Master", record.Name,
                $"UQC: {record.ReportingUqc} · Decimals: {record.DecimalPlaces}", GlobalSearchRecordText(record));

        foreach (var record in snapshot.StockSimpleMasters)
            Add(record.Type.Length == 0 ? "Stock Master" : record.Type, "Stock Master", record.Name,
                $"Parent: {record.Parent}", GlobalSearchRecordText(record));

        foreach (var record in snapshot.StockItemAliases)
            Add("Stock Item Alias", "Stock Master", record.ImportedAlias,
                $"→ {record.StockItemName} · {record.Source}", GlobalSearchRecordText(record));

        foreach (var record in snapshot.StockUnitMappings)
            Add("Stock Unit Mapping", "Stock Master", record.StockItemName,
                $"{record.ImportedUnit} → {record.FinalUnit}", GlobalSearchRecordText(record));

        foreach (var record in snapshot.QuantityConversions)
            Add("Quantity Conversion", "Stock Master", record.StockItemName,
                $"{record.ImportedUnit} → {record.FinalUnit} × {record.Factor:0.########}", GlobalSearchRecordText(record));

        foreach (var record in snapshot.StockItemUnitProfiles)
            Add("Stock Item Unit Profile", "Stock Master", record.StockItemName,
                $"{record.UnitCode} · Factor {record.FactorToBase:0.########} · {record.Source}", GlobalSearchRecordText(record));

        foreach (var record in snapshot.Purchases)
        {
            var number = string.IsNullOrWhiteSpace(record.DocumentNumber) ? record.SupplierName : record.DocumentNumber;
            var date = record.DocumentDate?.ToString("dd-MM-yyyy") ?? string.Empty;
            Add("Purchase Invoice / Voucher", "Purchase", number,
                $"{record.SupplierName} · {date} · {record.DocumentValue:0.00} · {record.ItemName}", GlobalSearchRecordText(record));
        }

        foreach (var record in snapshot.Sales)
        {
            var number = string.IsNullOrWhiteSpace(record.DocumentNumber) ? record.CustomerName : record.DocumentNumber;
            var date = record.DocumentDate?.ToString("dd-MM-yyyy") ?? string.Empty;
            Add("Sales Invoice / Voucher", "Sales", number,
                $"{record.CustomerName} · {date} · {record.DocumentValue:0.00} · {record.ItemName}", GlobalSearchRecordText(record));
        }

        foreach (var record in snapshot.Journals)
        {
            var lineText = string.Join(" | ", record.Lines.Select(line => GlobalSearchRecordText(line)));
            Add("Journal Voucher", "Journal",
                string.IsNullOrWhiteSpace(record.VoucherNumber) ? record.ReferenceNumber : record.VoucherNumber,
                $"{record.VoucherType} · {record.VoucherDate:dd-MM-yyyy} · {record.Narration}",
                GlobalSearchRecordText(record) + " | " + lineText);
        }

        foreach (var record in snapshot.JournalLedgerMappings)
            Add("Journal Ledger Mapping", "Journal", record.LedgerName,
                $"{record.Status} · Group {record.MasterGroup} · {record.Source}",
                GlobalSearchRecordText(record));

        if (snapshot.JournalSettings is not null)
            Add("Journal Settings", "Journal", "Journal generation settings", string.Empty,
                GlobalSearchRecordText(snapshot.JournalSettings));

        foreach (var record in snapshot.BankAccounts)
            Add("Bank Account", "Banking", record.AuthorityName,
                $"{record.BankName} · {record.AccountNumber} · {record.IfscCode}", GlobalSearchRecordText(record));

        foreach (var record in snapshot.BankTransactions)
            Add("Bank Voucher / Transaction", "Banking",
                string.IsNullOrWhiteSpace(record.ReferenceNumber) ? record.Narration : record.ReferenceNumber,
                $"{record.TransactionDate:dd-MM-yyyy} · Dr {record.Debit:0.00} · Cr {record.Credit:0.00} · {record.LedgerName}",
                GlobalSearchRecordText(record));

        foreach (var record in snapshot.BankStatements)
            Add("Bank Statement", "Banking",
                string.IsNullOrWhiteSpace(record.SourceFile) ? $"Statement #{record.Id}" : record.SourceFile,
                $"{record.PeriodFrom:dd-MM-yyyy} to {record.PeriodTo:dd-MM-yyyy} · {record.ReconciliationStatus} · Closing {record.ClosingBalance:0.00}",
                GlobalSearchRecordText(record));

        foreach (var record in snapshot.BankReconciliation)
            Add("Bank Reconciliation", "Banking",
                string.IsNullOrWhiteSpace(record.AccountName) ? record.PeriodLabel : record.AccountName,
                $"{record.PeriodLabel} · {record.Status} · Difference {record.Difference:0.00}",
                GlobalSearchRecordText(record));

        foreach (var record in snapshot.BankTransfers)
            Add("Inter-Bank Transfer", "Banking",
                string.IsNullOrWhiteSpace(record.ReferenceNumber) ? record.MatchId : record.ReferenceNumber,
                $"{record.MatchDate:dd-MM-yyyy} · {record.Amount:0.00} · {record.Status} · {record.MatchOrigin}",
                GlobalSearchRecordText(record));

        foreach (var record in snapshot.BankLedgerMappings)
            Add("Bank Ledger Mapping", "Banking",
                string.IsNullOrWhiteSpace(record.SummaryName) ? record.SourceLedgerName : record.SummaryName,
                $"{record.Status} · Source {record.SourceLedgerName} · Final {record.LedgerName} · Group {record.MasterGroup}",
                GlobalSearchRecordText(record));

        foreach (var record in snapshot.CashEntries)
            Add("Cash Voucher", "Cash Entry",
                string.IsNullOrWhiteSpace(record.ReferenceNumber) ? record.EntryLedger : record.ReferenceNumber,
                $"{record.EntryDate:dd-MM-yyyy} · Dr {record.Debit:0.00} · Cr {record.Credit:0.00} · {record.Narration}",
                GlobalSearchRecordText(record));

        foreach (var record in snapshot.GstReturns)
            Add("GST Return", "GST", record.PeriodLabel.Length == 0 ? record.PeriodKey : record.PeriodLabel,
                $"ARN {record.Arn} · {record.Gstin} · {(record.IsNilReturn ? "Nil Return" : "Filed Return")}",
                GlobalSearchRecordText(record));

        foreach (var record in snapshot.GstReconciliation)
            Add("GST Reconciliation", "GST", record.PeriodLabel.Length == 0 ? record.PeriodKey : record.PeriodLabel,
                $"{record.Status} · {record.ReviewNotes}", GlobalSearchRecordText(record));

        foreach (var record in snapshot.GstLedgerPeriods)
            Add("GST Ledger Period", "GST", record.PeriodLabel.Length == 0 ? record.PeriodKey : record.PeriodLabel,
                $"{record.LedgerType} · Closing {record.ClosingTotal:0.00} · {record.SourcePath}",
                GlobalSearchRecordText(record));

        foreach (var record in snapshot.GstNilSalesOptions)
            Add("GST Nil / Missing Sales", "Nil Returns", record.PeriodLabel.Length == 0 ? record.PeriodKey : record.PeriodLabel,
                $"{record.Status} · Input GST {record.InputGst:0.00} · {record.Source}",
                GlobalSearchRecordText(record));

        if (snapshot.GstJournalSettings is not null)
            Add("GST Journal Settings", "GST", "GST Journal generation settings", string.Empty,
                GlobalSearchRecordText(snapshot.GstJournalSettings));

        foreach (var record in snapshot.TaxCreditRows)
            Add("26AS / Tax Credit", "26AS",
                string.IsNullOrWhiteSpace(record.DeductorName) ? record.DeductorTan : record.DeductorName,
                $"TAN {record.DeductorTan} · Section {record.Section} · {record.TransactionDate:dd-MM-yyyy} · TDS {record.TaxDeducted:0.00}",
                GlobalSearchRecordText(record));

        foreach (var record in snapshot.TaxCreditReconciliationRows)
            Add("26AS Reconciliation", "26AS",
                string.IsNullOrWhiteSpace(record.DeductorName) ? record.DeductorTan : record.DeductorName,
                $"{record.Status} · {record.BooksReference} · {record.StatementAmount:0.00}", GlobalSearchRecordText(record));

        if (snapshot.TaxCreditImport is not null)
            Add("26AS Statement", "26AS",
                string.IsNullOrWhiteSpace(snapshot.TaxCreditImport.AssesseeName) ? snapshot.TaxCreditImport.Pan : snapshot.TaxCreditImport.AssesseeName,
                $"{snapshot.TaxCreditImport.StatementType} · AY {snapshot.TaxCreditImport.AssessmentYear} · PAN {snapshot.TaxCreditImport.Pan}",
                GlobalSearchRecordText(snapshot.TaxCreditImport));

        foreach (var record in snapshot.TaxCreditLedgerMasters)
            Add("26AS Ledger Master", "Ledger Master", record.Name,
                $"Parent: {record.Parent} · Tally ID: {record.TallyMasterId}", GlobalSearchRecordText(record));

        foreach (var record in snapshot.LedgerTotals)
            Add("Statement Ledger", "Balance Sheet", record.LedgerName,
                $"{record.ParentGroup} · Closing {record.ClosingBalance:0.00} · Movements {record.MovementCount}", GlobalSearchRecordText(record));

        foreach (var record in snapshot.StatementMasters)
            Add("Statement Master", "Ledger Master", record.Name,
                $"Parent: {record.Parent} · Tally ID: {record.TallyMasterId}", GlobalSearchRecordText(record));

        foreach (var record in snapshot.DashboardAccountingMetrics)
            Add("Dashboard Metric", string.IsNullOrWhiteSpace(record.TargetPage) ? "Dashboard" : record.TargetPage,
                record.Label, record.Detail, GlobalSearchRecordText(record));

        foreach (var record in snapshot.OpenActions)
            Add("Action Required", "Action Center", record.Title,
                $"{record.Module} · {record.ActionType} · {record.Detail}", GlobalSearchRecordText(record));

        return results.OrderByDescending(item => item.Score)
            .ThenBy(item => item.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.PrimaryText, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(1, limit)).ToArray();
    }

    private static int GlobalSearchScore(string query, string primary, string searchable)
    {
        if (query.Length == 0) return 10;
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!tokens.All(token => searchable.Contains(token, StringComparison.OrdinalIgnoreCase))) return -1;
        if (primary.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (primary.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 900;
        if (primary.Contains(query, StringComparison.OrdinalIgnoreCase)) return 800;
        if (searchable.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 700;
        if (searchable.Contains(query, StringComparison.OrdinalIgnoreCase)) return 600 + Math.Min(tokens.Length, 20);
        return 500;
    }

    private static string GlobalSearchRecordText(object? record)
    {
        if (record is null) return string.Empty;
        var properties = GlobalSearchScalarProperties.GetOrAdd(record.GetType(), static type =>
            type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0 &&
                                   !property.Name.Equals("RawText", StringComparison.OrdinalIgnoreCase) &&
                                   IsGlobalSearchScalar(property.PropertyType))
                .ToArray());
        var values = new List<string>(properties.Length);
        foreach (var property in properties)
        {
            try
            {
                var value = property.GetValue(record);
                if (value is null) continue;
                var text = value switch
                {
                    DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                    DateTimeOffset date => date.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture),
                    IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    _ => value.ToString() ?? string.Empty
                };
                if (text.Length > 0) values.Add(text);
            }
            catch
            {
                // Search is an optional read-only projection; one calculated property must
                // never break Dashboard navigation or the accounting cache.
            }
        }
        return string.Join(" | ", values);
    }

    private static bool IsGlobalSearchScalar(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;
        return actual == typeof(string) || actual == typeof(decimal) || actual == typeof(DateTime) ||
               actual == typeof(DateTimeOffset) || actual == typeof(Guid) || actual.IsPrimitive || actual.IsEnum;
    }

    public string CurrentClientDetail =>
        _clientContext.DetailText;

    public string WorkspaceButtonText =>
        HasActiveWorkspace
            ? $"←  {CurrentClient}"
            : "Client Directory";

    public string ApplicationState
    {
        get => _applicationState;
        private set => SetProperty(
            ref _applicationState,
            value);
    }

    /// <summary>Dashboard-only GSTIN panel uses the existing Client Management
    /// repository/registration save rules without opening the full client editor.</summary>
    public (string Primary, string Additional)? GetDashboardGstinSnapshot()
    {
        var current = _clientContext.CurrentClient;
        if (current is null) return null;
        var fresh = _clientRepository.GetById(current.Id);
        if (fresh is null || !fresh.FinancialYear.Equals(current.FinancialYear, StringComparison.OrdinalIgnoreCase))
            return null;
        return (fresh.Gstin, string.Join(Environment.NewLine, fresh.AdditionalGstins ?? []));
    }

    public bool SaveDashboardGstinRegistrations(
        long clientId, string financialYear, string primary, string additional,
        string originalPrimary, string originalAdditional, out string status) =>
        _clientsViewModel.SaveDashboardGstinRegistrations(
            clientId, financialYear, primary, additional,
            originalPrimary, originalAdditional, out status);

    public bool NavigateTo(
        string title)
    {
        if (title.Equals(
                "Clients",
                StringComparison.OrdinalIgnoreCase))
        {
            CloseWorkspace();
            return true;
        }

        if (title.Equals("Settings", StringComparison.OrdinalIgnoreCase))
        {
            OpenSettings();
            return true;
        }

        var item = NavigationItems.FirstOrDefault(candidate =>
            candidate.Title.Equals(
                title,
                StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            return false;
        }

        SelectedNavigationItem = item;
        return true;
    }

    public bool NavigateAnywhere(string title) =>
        NavigateTo(title) || NavigateFromAccountingActivity(title);

    public bool OpenLinkedClientWorkspace(long clientId, string moduleTitle, out string error)
    {
        error = string.Empty;
        var client = _clientRepository.GetById(clientId);
        if (client is null || client.IsArchived)
        {
            error = "The linked Accounting client is no longer available.";
            return false;
        }
        if (NetworkClientRuntime.IsNetworkClient && NetworkClientRuntime.AccountingAccessSuspended)
        {
            error = string.IsNullOrWhiteSpace(NetworkClientRuntime.AccountingAccessSuspensionReason)
                ? "Main Server policy currently suspends accounting access on this device."
                : NetworkClientRuntime.AccountingAccessSuspensionReason;
            return false;
        }
        if (!_governanceRepository.HasClientAccess(Environment.UserName, client.Id, client.FinancialYear))
        {
            error = "Your application user does not have access to the linked Accounting Client / Financial Year.";
            return false;
        }

        // R4 R7: navigation targets an existing linked Client/FY only. No ClientProfile is
        // created or cloned and Audit never becomes Accounting identity authority.
        _clientRepository.SetCurrent(client.Id);
        var current = _clientRepository.GetCurrent() ?? client;
        _clientContext.SetCurrent(current);
        RebuildWorkspaceNavigation(_dashboardNavigationItem.Title);

        var target = string.IsNullOrWhiteSpace(moduleTitle) ? "Dashboard" : moduleTitle.Trim();
        if (NavigateAnywhere(target)) return true;

        error = $"The linked Accounting client opened, but module '{target}' is not currently available.";
        return false;
    }

    public void OpenOppositeAccountingStatement(bool fromBalanceSheet)
    {
        _ = NavigateFromAccountingActivity(fromBalanceSheet ? "P&L" : "Balance Sheet");
    }

    public void OpenAccountingReports(string initialReport = "Trial Balance")
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var window = new AccountingReportsWindow(
            client,
            _ledgerTotalService,
            _bankingRepository,
            _purchaseRecordRepository,
            _salesRecordRepository,
            _journalRepository,
            _masterRepository,
            _stockMasterRepository,
            _tallyCoordinator,
            initialReport)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        window.Show();
    }

    public void OpenAdvancedBalanceSheetClosingEntry(IReadOnlyCollection<string>? scopedLedgerNames = null)
    {
        if (!DesktopPermissionService.CanMutateAccounting(_governanceRepository, GovernancePermissions.EditAccounting))
        {
            MessageBox.Show(DesktopPermissionService.DeniedMessage(_governanceRepository, GovernancePermissions.EditAccounting, "create Balance Sheet closure entries"), "Permission Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var client = _clientContext.CurrentClient;
        if (client is null) return;

        // Fix27: a Balance Sheet selection is a Closure scope, not merely a
        // preselection hint.  One selected/open Ledger means the Closure panel shows
        // only that Ledger; Ctrl/Shift/range multi-selection means it shows only that
        // selected set.  With no Balance Sheet selection the full Balance Sheet ledger
        // set remains available.
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (scopedLedgerNames is not null)
        {
            foreach (var name in scopedLedgerNames)
            {
                var normalized = name?.Trim() ?? string.Empty;
                if (normalized.Length > 0) requested.Add(normalized);
            }
        }

        var eligible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (requested.Count > 0)
        {
            foreach (var name in requested) eligible.Add(name);
        }
        else
        {
            foreach (var row in _balanceSheetViewModel.LeftRows)
            {
                if (string.Equals(row.RowType, "Ledger", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(row.Label))
                    eligible.Add(row.Label.Trim());
            }
            foreach (var row in _balanceSheetViewModel.RightRows)
            {
                if (string.Equals(row.RowType, "Ledger", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(row.Label))
                    eligible.Add(row.Label.Trim());
            }
            foreach (var row in _balanceSheetViewModel.ScheduleRows)
            {
                if (string.Equals(row.RowType, "Ledger", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(row.Label))
                    eligible.Add(row.Label.Trim());
            }
            foreach (var row in _balanceSheetViewModel.DrilldownRows)
            {
                if (string.Equals(row.RowType, "Ledger", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(row.Label))
                    eligible.Add(row.Label.Trim());
            }
        }

        var scoped = eligible.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var preselected = requested.Count > 0
            ? requested.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()
            : null;

        var window = new AdvancedClosingEntryWindow(
            client,
            _ledgerTotalService,
            _bankingRepository,
            _journalRepository,
            _masterRepository,
            _clientPreferenceRepository,
            _liveChangeHub,
            () =>
            {
                // Fix27 live authority: closure creation changes accounting immediately.
                // Invalidate the ledger book and the prepared Statement snapshot, then
                // rebuild the currently visible Balance Sheet/P&L from live totals so an
                // open ledger account shows its new closing balance without manual Refresh.
                _ledgerTotalService.Invalidate(client.Id, client.FinancialYear);
                _workspaceStateCache.Invalidate(client.Id, client.FinancialYear, ClientWorkspaceModule.Statements);
                _balanceSheetViewModel.RefreshFromLiveLedgerTotals();
                _profitAndLossViewModel.RefreshFromLiveLedgerTotals();
                _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Statements);
            },
            scoped,
            preselected)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        window.ShowDialog();
    }

    public void CreateBalanceSheetClosingEntry(AccountingStatementLine line)
    {
        if (!DesktopPermissionService.CanMutateAccounting(_governanceRepository, GovernancePermissions.EditAccounting))
        {
            MessageBox.Show(DesktopPermissionService.DeniedMessage(_governanceRepository, GovernancePermissions.EditAccounting, "create a Balance Sheet closing entry"), "Permission Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null || line.RowType != "Ledger" || string.IsNullOrWhiteSpace(line.Label)) return;
        _ledgerTotalService.Invalidate(client.Id, client.FinancialYear);
        var snapshot = _ledgerTotalService.Get(client.Id, client.FinancialYear, line.Label);
        var balance = snapshot?.ClosingBalance ?? line.ClosingBalance;
        if (Math.Abs(balance) < 0.005m)
        {
            MessageBox.Show($"'{line.Label}' is already closed at zero.", "Closing Entry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var masters = _masterRepository.Get(client.Id, client.FinancialYear, includeArchived: false)
            .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !item.IsArchived && !string.IsNullOrWhiteSpace(item.Name))
            .ToArray();
        var allLedgers = masters.Select(item => item.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var cashLedgers = masters.Where(item => item.Parent.Equals("Cash-in-Hand", StringComparison.OrdinalIgnoreCase) || item.Parent.Equals("Cash-in-hand", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var lastDate = _ledgerTotalService.GetLastMovementDate(client.Id, client.FinancialYear, line.Label) ?? FinancialYearStart(client.FinancialYear);
        var dialog = new BalanceClosingEntryWindow(line.Label, balance, lastDate.AddDays(1), cashLedgers, allLedgers) { Owner = System.Windows.Application.Current?.MainWindow };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var amount = Math.Round(Math.Abs(balance), 2, MidpointRounding.AwayFromZero);
            if (dialog.EntryMode.Equals("Cash", StringComparison.OrdinalIgnoreCase))
            {
                var cashService = new CashEntryService(_bankingRepository, _masterRepository);
                var settings = cashService.EnsureSettings(client.Id, client.FinancialYear);
                var entry = new CashEntryRecord
                {
                    ClientId = client.Id, FinancialYear = client.FinancialYear, EntryDate = dialog.EntryDate, EntryLedger = line.Label,
                    CommonLedger = dialog.CashLedger, MappedMasterLedger = line.Label, Narration = $"Closing entry for {line.Label}",
                    Debit = balance < 0m ? amount : 0m, Credit = balance > 0m ? amount : 0m,
                    SingleAmount = amount, SingleDrCr = balance < 0m ? "Dr" : "Cr", WorkflowStatus = "Ready", Status = "Ready",
                    EntryId = $"BS-CLOSE-{DateTime.Now:yyyyMMddHHmmssfff}", IsUserEdited = true, UpdatedUtc = DateTime.UtcNow
                };
                var saved = cashService.ValidateAndSave(entry, settings);
                if (!saved.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(saved.Status);
                _ledgerTotalService.Invalidate(client.Id, client.FinancialYear);
                _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Banking, $"Balance Sheet closing entry for {line.Label}");
                NavigateFromAccountingActivity("Cash Entry");
            }
            else
            {
                var date = dialog.EntryDate;
                var periodKey = GstJournalGenerationService.PeriodKey(date, "Monthly");
                var selectedSide = balance > 0m ? "Cr" : "Dr";
                var oppositeSide = balance > 0m ? "Dr" : "Cr";
                var voucher = new JournalVoucher
                {
                    ClientId = client.Id, FinancialYear = client.FinancialYear, PeriodKey = periodKey, PeriodLabel = GstJournalGenerationService.PeriodLabel(periodKey, "Monthly"),
                    VoucherDate = date, VoucherNumber = $"CLOSE-{date:yyyyMMdd}-{DateTime.Now:HHmmss}", VoucherType = "Journal",
                    Narration = $"Closing entry for {line.Label}", GenerationMode = "Manual", WorkflowStatus = "Ready", IsUserEdited = true, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
                    Lines =
                    [
                        new JournalLine { LineNumber = 1, LedgerName = line.Label, Amount = amount, DrCr = selectedSide, LineRole = "Manual Note" },
                        new JournalLine { LineNumber = 2, LedgerName = dialog.OppositeLedger, Amount = amount, DrCr = oppositeSide, LineRole = "Manual Note" }
                    ]
                };
                _journalRepository.SaveVoucher(voucher);
                _ledgerTotalService.Invalidate(client.Id, client.FinancialYear);
                _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Journal, $"Balance Sheet closing entry for {line.Label}");
                NavigateFromAccountingActivity("Journal");
            }
            _balanceSheetViewModel.Refresh();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Closing entry was not created.\n\n{exception.Message}", "Closing Entry", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static DateTime FinancialYearStart(string financialYear)
    {
        var first = financialYear.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return int.TryParse(first, out var year) ? new DateTime(year, 4, 1) : DateTime.Today;
    }

    public bool NavigateFromAccountingActivity(string title)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || (HasBranchWorkspaces && !_isPrivateBranchActive)) return false;
        if (_isPrivateBranchActive && title.Equals("Tally", StringComparison.OrdinalIgnoreCase))
            return false; // branch Tally posting not yet verified

        if (title.Equals("GST", StringComparison.OrdinalIgnoreCase) && (!HasValidGstin(client) || !CanViewModule(GovernancePermissions.GstView)))
            return false;
        if (title.Equals("Banking", StringComparison.OrdinalIgnoreCase) && !CanViewModule(GovernancePermissions.BankView)) return false;
        if (title.Equals("Purchase", StringComparison.OrdinalIgnoreCase) && !CanViewModule(GovernancePermissions.PurchaseView)) return false;
        if (title.Equals("Sales", StringComparison.OrdinalIgnoreCase) && !CanViewModule(GovernancePermissions.SalesView)) return false;
        if (title.Equals("Cash Entry", StringComparison.OrdinalIgnoreCase) && !CanViewModule(GovernancePermissions.ReadData)) return false;
        if (title.Equals("Stock Master", StringComparison.OrdinalIgnoreCase) && !CanViewModule(GovernancePermissions.StockView)) return false;
        if (title.Equals("Fixed Assets", StringComparison.OrdinalIgnoreCase) && (NetworkClientRuntime.IsNetworkClient || !CanViewModule(GovernancePermissions.ReadData))) return false;
        if (title.Equals("Journal", StringComparison.OrdinalIgnoreCase) && !CanViewModule(GovernancePermissions.JournalView)) return false;
        if ((title.Equals("Balance Sheet", StringComparison.OrdinalIgnoreCase) || title.Equals("P&L", StringComparison.OrdinalIgnoreCase) || title.Equals("Profit & Loss", StringComparison.OrdinalIgnoreCase)) && !CanViewModule(GovernancePermissions.ReadData)) return false;

        var listed = NavigationItems.FirstOrDefault(candidate =>
            candidate.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
        if (listed is not null)
        {
            SelectedNavigationItem = listed;
            return true;
        }

        var hidden = title.Equals("Banking", StringComparison.OrdinalIgnoreCase)
            ? _bankingNavigationItem
            : title.Equals("GST", StringComparison.OrdinalIgnoreCase)
                ? _gstNavigationItem
                : title.Equals("Purchase", StringComparison.OrdinalIgnoreCase)
                    ? _purchaseNavigationItem
            : title.Equals("Sales", StringComparison.OrdinalIgnoreCase)
                ? _salesNavigationItem
                : title.Equals("Cash Entry", StringComparison.OrdinalIgnoreCase)
                    ? _cashEntryNavigationItem
                    : title.Equals("Stock Master", StringComparison.OrdinalIgnoreCase)
                        ? _stockMasterNavigationItem
                        : title.Equals("Fixed Assets", StringComparison.OrdinalIgnoreCase)
                            ? _fixedAssetsNavigationItem
                        : title.Equals("Journal", StringComparison.OrdinalIgnoreCase)
                            ? _journalNavigationItem
                            : title.Equals("Balance Sheet", StringComparison.OrdinalIgnoreCase)
                                ? _balanceSheetNavigationItem
                                : title.Equals("P&L", StringComparison.OrdinalIgnoreCase) || title.Equals("Profit & Loss", StringComparison.OrdinalIgnoreCase)
                                    ? _profitAndLossNavigationItem
                                    : null;
        if (hidden is null) return false;

        _selectedNavigationItem = null;
        OnPropertyChanged(nameof(SelectedNavigationItem));
        var nextPage = hidden.PageFactory();
        if (!ReferenceEquals(CurrentPage, nextPage))
        {
            var previousPage = CurrentPage;
            CurrentPage = nextPage;
            SchedulePageTransition(previousPage, nextPage);
        }
        ApplicationState = CurrentPage is AccountingStatementViewModel
            ? $"{hidden.Title} generated on demand."
            : $"{hidden.Title} manual import opened from Accounting Activity.";
        return true;
    }

    public bool IsFinancialStatementEnabled(string title) =>
        title.Equals("Balance Sheet", StringComparison.OrdinalIgnoreCase)
            ? _showBalanceSheet
            : title.Equals("P&L", StringComparison.OrdinalIgnoreCase) || title.Equals("Profit & Loss", StringComparison.OrdinalIgnoreCase)
                ? _showProfitAndLoss
                : false;

    public bool SetFinancialStatementEnabled(string title, bool enabled)
    {
        if (_clientContext.CurrentClient is null) return false;
        if (title.Equals("Balance Sheet", StringComparison.OrdinalIgnoreCase))
            _showBalanceSheet = enabled;
        else if (title.Equals("P&L", StringComparison.OrdinalIgnoreCase) || title.Equals("Profit & Loss", StringComparison.OrdinalIgnoreCase))
            _showProfitAndLoss = enabled;
        else
            return false;

        var currentIsDisabledStatement = !enabled &&
            ((ReferenceEquals(CurrentPage, _balanceSheetViewModel) && title.Equals("Balance Sheet", StringComparison.OrdinalIgnoreCase)) ||
             (ReferenceEquals(CurrentPage, _profitAndLossViewModel) && !title.Equals("Balance Sheet", StringComparison.OrdinalIgnoreCase)));
        RebuildWorkspaceNavigation(currentIsDisabledStatement ? _dashboardNavigationItem.Title : SelectedNavigationItem?.Title ?? _dashboardNavigationItem.Title);
        ApplicationState = enabled
            ? $"{title} enabled. It will generate only when opened."
            : $"{title} hidden. No background statement generation is running.";
        return true;
    }

    public bool OpenActionCenter(string? module = null)
    {
        if (NetworkClientRuntime.IsNetworkClient && !NetworkClientRuntime.HasPermission(GovernancePermissions.ViewAudit)) return false;
        if (!HasActiveWorkspace) return false;
        _actionCenterViewModel.SelectModule(module);
        if (!NavigationItems.Contains(_actionCenterNavigationItem))
            NavigationItems.Add(_actionCenterNavigationItem);
        SelectedNavigationItem = _actionCenterNavigationItem;
        return true;
    }

    private void NavigateFromActionCenter(ActionRequiredItem action)
    {
        var client = _clientRepository.GetById(action.ClientId);
        if (client is null)
        {
            ApplicationState = $"Action Required belongs to client #{action.ClientId}, but that client is no longer available.";
            return;
        }

        var current = _clientContext.CurrentClient;
        if (current?.Id != client.Id)
        {
            _clientRepository.SetCurrent(client.Id);
            _clientContext.SetCurrent(client);
        }

        var module = action.Module.Equals("Automation", StringComparison.OrdinalIgnoreCase)
            ? "Settings"
            : action.Module;
        if (module.Equals("Settings", StringComparison.OrdinalIgnoreCase))
        {
            NavigateTo("Settings");
            return;
        }

        if (!NavigateFromAccountingActivity(module))
            ApplicationState = $"Opened {client.ClientName} • FY {client.FinancialYear}, but module '{module}' is not currently available.";
    }

    private SettingsViewModel GetSettingsViewModel()
    {
        if (_settingsViewModel is not null) return _settingsViewModel;
        _secureLanServerService?.EnsureInitializedForUi();
        return _settingsViewModel = new SettingsViewModel(
            _applicationPreferences,
            _automationService,
            _clientContext,
            _secureLanServerService);
    }

    private AdministrationViewModel GetAdministrationViewModel()
    {
        if (_administrationViewModel is not null) return _administrationViewModel;
        _secureLanServerService?.EnsureInitializedForUi();
        return _administrationViewModel = new AdministrationViewModel(
            _applicationPreferences,
            _secureLanServerService);
    }

    private void OpenSettings()
    {
        ReleasePageForNavigation(CurrentPage);
        _selectedNavigationItem = null;
        OnPropertyChanged(nameof(SelectedNavigationItem));
        CurrentPage = GetSettingsViewModel();
        ApplicationState = "Application Settings opened.";
    }

    public void ApplyNetworkLiveStatusRefresh()
    {
        if(!NetworkClientRuntime.IsNetworkClient)return;
        _settingsViewModel?.RefreshNetworkIdentityPresentation();
        _remoteAssistViewModel?.Refresh();
        if(CurrentPage is NotificationCenterViewModel notificationCenter)notificationCenter.Refresh();
        NotifyWorkspaceChanged();
    }

    public void ApplyNetworkIdentityRefresh()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        _settingsViewModel?.RefreshNetworkIdentityPresentation();
        if (_clientContext.CurrentClient is not null)
            RebuildWorkspaceNavigation(SelectedNavigationItem?.Title);
        else
            NotifyWorkspaceChanged();
    }

    public bool TryRemoteAssistNavigate(string pageTitle, out string detail)
    {
        detail = string.Empty;
        if (!NetworkClientRuntime.IsNetworkClient) { detail = "Remote Assist navigation is available only in the LAN/remote client."; return false; }
        if (string.IsNullOrWhiteSpace(pageTitle)) { detail = "A page name is required."; return false; }
        var target = NavigationItems.FirstOrDefault(x => x.Title.Equals(pageTitle.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            detail = $"Page '{pageTitle}' is not available to this user's current permissions/client workspace.";
            return false;
        }
        SelectedNavigationItem = target;
        detail = $"Navigated AAH to {target.Title}.";
        return true;
    }

    public void ApplyNetworkAccessSuspension(string reason)
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        NetworkClientRuntime.AccountingAccessSuspended = true;
        NetworkClientRuntime.AccountingAccessSuspensionReason = reason;
        CloseWorkspaceCore(clearRepository: true);
        ApplicationState = $"LAN accounting access suspended by Main Server policy · {reason}. Team, Work, Notifications, Remote Assist and Settings remain available.";
        NotifyWorkspaceChanged();
    }

    public void ClearNetworkAccessSuspension()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        NetworkClientRuntime.AccountingAccessSuspended = false;
        NetworkClientRuntime.AccountingAccessSuspensionReason = string.Empty;
        ApplicationState = "Main Server policy allows accounting access. Refresh Server Data if Client Directory needs the latest assignment.";
        NotifyWorkspaceChanged();
    }

    public void ApplyNetworkLiveSnapshotRefresh()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        NetworkLocalPreferenceService.ApplyTo(_applicationPreferences);
        AppearanceService.Apply(_applicationPreferences);
        _settingsViewModel?.RefreshNetworkIdentityPresentation();
        _profileSyncCancellation?.Cancel();

        var previousClientId=_clientContext.CurrentClient?.Id??0;
        var previousFinancialYear=_clientContext.CurrentClient?.FinancialYear??string.Empty;
        var previousPageTitle=CurrentPage?.Title??string.Empty;
        var previousNavigationTitle=SelectedNavigationItem?.Title;
        var currentClients=_clientRepository.GetAll(includeArchived:false);
        _workspaceStateCache.ResetForNetworkSnapshot(currentClients);

        if(previousClientId>0)
        {
            var refreshed=currentClients.FirstOrDefault(x=>x.Id==previousClientId&&x.FinancialYear.Equals(previousFinancialYear,StringComparison.OrdinalIgnoreCase));
            if(refreshed is null)
            {
                CloseWorkspaceCore(clearRepository:true);
                _clientsViewModel.RefreshFromExternalChange();
                ApplicationState="Main Server live sync removed or revoked the previously open Client/FY. Client Directory is current.";
                return;
            }

            _clientContext.SetCurrent(refreshed);

            if(!string.IsNullOrWhiteSpace(previousNavigationTitle))
                RebuildWorkspaceNavigation(previousNavigationTitle);
            else if(!string.IsNullOrWhiteSpace(previousPageTitle)&&!NavigateFromAccountingActivity(previousPageTitle))
                PreparePageForDisplay(CurrentPage);
            else
                PreparePageForDisplay(CurrentPage);

            _clientsViewModel.RefreshFromExternalChange();
            _actionCenterViewModel.Refresh();
            ApplicationState=$"Live Main Server data synchronized · {refreshed.ClientCode} · FY {refreshed.FinancialYear}.";
            NotifyWorkspaceChanged();
            return;
        }

        _clientsViewModel.RefreshFromExternalChange();
        PreparePageForDisplay(CurrentPage);
        ApplicationState=$"Live Main Server data synchronized · {currentClients.Count:N0} authorised client(s).";
        NotifyWorkspaceChanged();
    }

    public void ApplyNetworkSnapshotRefresh()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        NetworkLocalPreferenceService.ApplyTo(_applicationPreferences);
        AppearanceService.Apply(_applicationPreferences);
        _settingsViewModel?.RefreshNetworkIdentityPresentation();
        _profileSyncCancellation?.Cancel();
        ReleasePageForNavigation(CurrentPage);
        _clientRepository.ClearCurrent();
        _clientContext.SetCurrent(null);
        _selectedNavigationItem = null;
        OnPropertyChanged(nameof(SelectedNavigationItem));
        NavigationItems.Clear();
        AppendGlobalNavigation(NavigationItems);
        CurrentPage = _clientsViewModel;
        _clientsViewModel.ReloadCommand.Execute(null);
        var currentClients = _clientRepository.GetAll(includeArchived: false);
        _workspaceStateCache.ResetForNetworkSnapshot(currentClients);
        ApplicationState = $"LAN data refreshed from Main Server · {currentClients.Count:N0} authorised client(s) · {NetworkClientRuntime.RoleName}.";
        NotifyWorkspaceChanged();
    }

    public bool FinancialTaxPresentationActive => _financialTaxPresentationActive;

    public void SetFinancialTaxPresentationActive(bool active)
    {
        if (_financialTaxPresentationActive == active) return;
        _financialTaxPresentationActive = active;
        OnPropertyChanged(nameof(FinancialTaxPresentationActive));
        if (_clientContext.CurrentClient is not null)
            RebuildWorkspaceNavigation(active
                ? (ReleaseFeatureGates.FinancialStatements ? "Financial Statements" : "Income Tax")
                : null);
    }

    public void CloseWorkspace() =>
        CloseWorkspaceCore(clearRepository: true);

    private void CloseWorkspaceCore(
        bool clearRepository)
    {
        // The left WORKSPACE / Client Directory control is also the canonical way
        // out of the Client Groups overlay when no client workspace is open.
        _clientsViewModel.CloseGroupManager();
        if (_changingWorkspace)
        {
            return;
        }

        _changingWorkspace = true;

        try
        {
            _profileSyncCancellation?.Cancel();

            // SQLite has already received edits as they are made.  On workspace close
            // refresh the in-memory snapshot from SQLite so reopening the client is an
            // immediate cache bind with the latest committed values.
            var closingClient = _clientContext.CurrentClient;
            if (closingClient is not null) _workspaceStateCache.QueueRefresh(closingClient);

            if (clearRepository)
            {
                _clientRepository.ClearCurrent();
            }

            ReleasePageForNavigation(CurrentPage);
            _clientContext.SetCurrent(null);

            _selectedNavigationItem = null;
            OnPropertyChanged(nameof(SelectedNavigationItem));
            NavigationItems.Clear();
            AppendGlobalNavigation(NavigationItems);
            CurrentPage = _clientsViewModel;
            ApplicationState =
                "Client Directory ready. Team, Work, Notifications, Remote Assist and authorised Administration remain available without opening a client.";
            NotifyWorkspaceChanged();
        }
        finally
        {
            _changingWorkspace = false;
        }
    }

    private void RebuildWorkspaceNavigation(
        string? preferredTitle = null)
    {
        var client =
            _clientContext.CurrentClient;

        if (client is null)
        {
            CloseWorkspaceCore(clearRepository: false);
            return;
        }

        if (HasBranchWorkspaces && !_isPrivateBranchActive)
        {
            var scope = SelectedBranchWorkspace ?? BranchWorkspaces[0];
            var note = _branchWorkspaceProblem.Length > 0
                ? "Branch identity review required: " + _branchWorkspaceProblem
                : scope.IsConsolidated
                    ? "Consolidated reporting is for the saved branch scopes only. Live combined balances and Tally import remain blocked until every member ledger is scope-verified."
                    : "Dedicated branch records and modules require verified scope-aware repositories. Existing parent Client/FY rows cannot be presented as this branch's books.";
            var view = new BranchScopePreviewViewModel(
                "Dashboard", "Account scope / import verification", scope.DisplayName,
                scope.ScopeId, scope.FinancialYear, note);
            var choices = new List<NavigationItem>
            {
                new("Dashboard", "⌂", "Source-verified scope overview", () => view)
            };
            if (scope.IsConsolidated)
            {
                var tallyView = new BranchScopePreviewViewModel(
                    "Tally Import", "Consolidated Tally verification", scope.DisplayName,
                    scope.ScopeId, scope.FinancialYear,
                    "Consolidated Tally import is pending source-backed branch ledgers, duplicate-safe DR/CR validation and existing-company read-back. No unverified Tally company or voucher will be created.");
                choices.Add(new NavigationItem(
                    "Tally Import", "T", "Blocked until verified branch reconciliation", () => tallyView));
            }
            _selectedNavigationItem = null;
            OnPropertyChanged(nameof(SelectedNavigationItem));
            NavigationItems.Clear();
            foreach (var item in choices) NavigationItems.Add(item);
            SelectedNavigationItem = choices.FirstOrDefault(x =>
                x.Title.Equals(preferredTitle, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
            ApplicationState = scope.DisplayName +
                " · branch-scoped accounting pending verified storage/importer routing; parent records preserved.";
            NotifyWorkspaceChanged();
            return;
        }

        _financialStatementsNavigationItem.SetToggleValue(_financialStatementsViewModel.IsEnabledFor(client), notifyAction: false);

        var statementScope = $"{client.Id}|{client.FinancialYear.Trim().ToUpperInvariant()}";
        if (!_financialStatementToggleScope.Equals(statementScope, StringComparison.OrdinalIgnoreCase))
        {
            _financialStatementToggleScope = statementScope;
            _showBalanceSheet = false;
            _showProfitAndLoss = false;
        }

        var hiddenActivityPageTitle = SelectedNavigationItem is null && CurrentPage is PurchaseImportViewModel
            ? "Purchase"
            : SelectedNavigationItem is null && CurrentPage is SalesImportViewModel
                ? "Sales"
                : SelectedNavigationItem is null && CurrentPage is CashEntryViewModel
                    ? "Cash Entry"
                    : SelectedNavigationItem is null && CurrentPage is StockMasterViewModel
                        ? "Stock Master"
                        : SelectedNavigationItem is null && CurrentPage is FixedAssetsViewModel
                            ? "Fixed Assets"
                        : SelectedNavigationItem is null && CurrentPage is JournalViewModel
                            ? "Journal"
                            : SelectedNavigationItem is null && ReferenceEquals(CurrentPage, _balanceSheetViewModel)
                                ? "Balance Sheet"
                                : SelectedNavigationItem is null && ReferenceEquals(CurrentPage, _profitAndLossViewModel)
                                    ? "P&L"
                                    : SelectedNavigationItem is null && CurrentPage is TallyClientViewModel
                                        ? "Tally"
                                    : SelectedNavigationItem is null && CurrentPage is MobileLinkViewModel
                                        ? "Mobile Link"
                                    : SelectedNavigationItem is null && CurrentPage is IncomeTaxViewModel
                                        ? "Income Tax"
                                    : SelectedNavigationItem is null && CurrentPage is PortalLoginsViewModel
                                        ? "Portal Logins"
                                    : SelectedNavigationItem is null && CurrentPage is SettingsViewModel
                                        ? "Settings"
                                        : SelectedNavigationItem is null && CurrentPage is TeamViewModel
                                            ? "Team"
                                            : SelectedNavigationItem is null && CurrentPage is WorkViewModel
                                                ? "Work"
                                                : SelectedNavigationItem is null && CurrentPage is NotificationCenterViewModel
                                                    ? "Notifications"
                                                    : SelectedNavigationItem is null && CurrentPage is RemoteAssistViewModel
                                                        ? "Remote Assist"
                                                        : SelectedNavigationItem is null && CurrentPage is AdministrationViewModel
                                                            ? "Administration"
                                                            : string.Empty;

        var previousTitle =
            preferredTitle
            ?? SelectedNavigationItem?.Title
            ?? (hiddenActivityPageTitle.Length > 0 ? hiddenActivityPageTitle : _dashboardNavigationItem.Title);

        var canReadNetworkData = !NetworkClientRuntime.IsNetworkClient || NetworkClientRuntime.HasPermission(GovernancePermissions.ReadData);
        var available = new List<NavigationItem>();
        if (canReadNetworkData)
        {
            if (_isPrivateBranchActive)
            {
                var owner = SelectedBranchWorkspace;
                var branchDashboard = new BranchScopePreviewViewModel(
                    "Dashboard", "Separate branch accounting overview",
                    owner?.DisplayName ?? client.ClientName,
                    owner?.ScopeId ?? "REVIEW", client.FinancialYear,
                    "Branch records are held under a separate, durable ClientId. Existing Sales, Purchase, Bank, Cash, Journal and other accounting modules use that ID. Existing parent records were not moved. Tally export is blocked until verified mapping to the existing Tally company is complete.");
                available.Add(new NavigationItem("Dashboard", "⌂",
                    "Private branch accounting scope", () => branchDashboard));
            }
            else available.Add(_dashboardNavigationItem);
        }

        var hasValidGstin = HasValidGstin(client);
        if (!canReadNetworkData)
        {
            _selectedNavigationItem = null;
            OnPropertyChanged(nameof(SelectedNavigationItem));
            NavigationItems.Clear();
            AppendGlobalNavigation(NavigationItems);
            CurrentPage = _clientsViewModel;
            ApplicationState = $"{client.ClientCode} selected · {NetworkClientRuntime.RoleName}. This AAH user may upload/import for authorised clients but does not have Read Data permission.";
            NotifyWorkspaceChanged();
            return;
        }

        if (_financialTaxPresentationActive)
        {
            // R52 FIXED22: Financial Statements & Income Tax is a separate domain surface over
            // the SAME Accounting Client/FY authority. Financial Statements is the existing shared
            // ViewModel; there is no duplicate mapping/data store. Income Tax starts with Form 3CB.
            if (CanViewModule(GovernancePermissions.ReadData))
            {
                if (ReleaseFeatureGates.FinancialStatements)
                    available.Add(_financialStatementsNavigationItem);
                available.Add(_incomeTaxNavigationItem);
            }
            AppendGlobalNavigation(available);
            _selectedNavigationItem = null;
            OnPropertyChanged(nameof(SelectedNavigationItem));
            NavigationItems.Clear();
            foreach (var item in available) NavigationItems.Add(item);
            var financialTaxTarget = previousTitle.Equals("Income Tax", StringComparison.OrdinalIgnoreCase)
                ? available.FirstOrDefault(item => ReferenceEquals(item, _incomeTaxNavigationItem))
                : available.FirstOrDefault(item => ReferenceEquals(item, _financialStatementsNavigationItem));
            if (!ReleaseFeatureGates.FinancialStatements)
                financialTaxTarget = available.FirstOrDefault(item => ReferenceEquals(item, _incomeTaxNavigationItem));
            financialTaxTarget ??= available.FirstOrDefault();
            if (financialTaxTarget is not null) SelectedNavigationItem = financialTaxTarget;
            NotifyWorkspaceChanged();
            return;
        }

        // Fix129: restore the original data-driven sidebar contract. Purchase/Sales
        // stay hidden until durable rows exist for this Client/FY. Manual Import is still
        // reachable from Accounting Activity through NavigateFromAccountingActivity.
        // R9 FIXED16: accounting refresh invalidates the prepared Sales/Purchase cache
        // before the replacement snapshot is ready. Never interpret that short cache gap
        // as "module has no data" while the user is already on that page; doing so removed
        // the active navigation item and fell back to Dashboard immediately after XML
        // autosave. Hold the active module in navigation until its refreshed snapshot lands.
        var holdActivePurchasePage = CurrentPage is PurchaseImportViewModel ||
                                     ReferenceEquals(SelectedNavigationItem, _purchaseNavigationItem);
        var holdActiveSalesPage = CurrentPage is SalesImportViewModel ||
                                  ReferenceEquals(SelectedNavigationItem, _salesNavigationItem);

        if (CanViewModule(GovernancePermissions.PurchaseView) &&
            (HasPurchaseData(client) || holdActivePurchasePage || _isPrivateBranchActive))
        {
            available.Add(_purchaseNavigationItem);
        }

        if (CanViewModule(GovernancePermissions.SalesView) &&
            (HasSalesData(client) || holdActiveSalesPage || _isPrivateBranchActive))
        {
            available.Add(_salesNavigationItem);
        }

        // Navigation follows effective module-view permissions. A fixed Read Only role
        // sees the accounting centres it may inspect; a Custom role sees only the
        // modules explicitly granted to it. Mutation remains separately permissioned.
        if (CanViewModule(GovernancePermissions.BankView)) available.Add(_bankingNavigationItem);
        if (CanViewModule(GovernancePermissions.ReadData) && (HasCashData(client) || _isPrivateBranchActive)) available.Add(_cashEntryNavigationItem);
        if (CanViewModule(GovernancePermissions.MasterView)) available.Add(_ledgerMasterNavigationItem);
        if (CanViewModule(GovernancePermissions.StockView) && (HasStockData(client) || _isPrivateBranchActive)) available.Add(_stockMasterNavigationItem);
        if (!NetworkClientRuntime.IsNetworkClient && CanViewModule(GovernancePermissions.ReadData)) available.Add(_fixedAssetsNavigationItem);
        if (CanViewModule(GovernancePermissions.JournalView) && (HasJournalData(client) || _isPrivateBranchActive)) available.Add(_journalNavigationItem);
        if (_showBalanceSheet && CanViewModule(GovernancePermissions.ReadData)) available.Add(_balanceSheetNavigationItem);
        if (_showProfitAndLoss && CanViewModule(GovernancePermissions.ReadData)) available.Add(_profitAndLossNavigationItem);
        if (ReleaseFeatureGates.FinancialStatements && CanViewModule(GovernancePermissions.ReadData))
            available.Add(_financialStatementsNavigationItem);
        if (hasValidGstin && CanViewModule(GovernancePermissions.GstView))
        {
            available.Add(_gstNavigationItem);
        }
        if (!NetworkClientRuntime.IsNetworkClient && CanViewModule(GovernancePermissions.ReadData)) available.Add(_portalLoginsNavigationItem);
        if (CanViewModule(GovernancePermissions.ReadData)) available.Add(_taxCreditNavigationItem);
        if (!_isPrivateBranchActive && !NetworkClientRuntime.IsNetworkClient && CanViewModule(GovernancePermissions.TallyView)) available.Add(_tallyNavigationItem);
        // R42 Mobile Link is a Client/FY module on the Main Server Windows app. Android
        // authentication and access checks remain enforced by SecureLanServerService.
        if (!NetworkClientRuntime.IsNetworkClient && _secureLanServerService is not null && CanViewModule(GovernancePermissions.ReadData)) available.Add(_mobileLinkNavigationItem);
        if (!NetworkClientRuntime.IsNetworkClient && CanViewModule(GovernancePermissions.BankView)) available.Add(_financeNavigationItem);
        // Completion 59: Action Center is client-specific and appears only while a real
        // unresolved blocking item exists. Historical/resolved rows are self-healed by the
        // ActionCenterViewModel and must not keep the sidebar entry visible forever.
        // Action Center is hydrated by its independent cache stream. Never perform a
        // synchronous SQLite refresh while rebuilding shell navigation; the Actions module
        // will warm this ViewModel and request one coalesced navigation refresh when ready.
        if (!_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, ClientWorkspaceModule.Actions))
            _workspaceStateCache.EnsureModulePrepared(client.Id, client.FinancialYear, ClientWorkspaceModule.Actions);
        if (_actionCenterViewModel.OpenBlockingCount > 0 &&
            (!NetworkClientRuntime.IsNetworkClient ||
             NetworkClientRuntime.HasPermission(GovernancePermissions.ViewAudit) ||
             NetworkClientRuntime.HasPermission(GovernancePermissions.Import) ||
             NetworkClientRuntime.HasPermission(GovernancePermissions.UploadFiles)))
            available.Add(_actionCenterNavigationItem);
        AppendGlobalNavigation(available);

        _selectedNavigationItem = null;
        OnPropertyChanged(nameof(SelectedNavigationItem));
        NavigationItems.Clear();

        foreach (var item in available)
        {
            NavigationItems.Add(item);
        }

        var target =
            available.FirstOrDefault(item =>
                item.Title.Equals(
                    previousTitle,
                    StringComparison.OrdinalIgnoreCase));

        if (target is not null)
        {
            SelectedNavigationItem = target;
        }
        else if (hiddenActivityPageTitle.Length > 0 &&
                 hiddenActivityPageTitle.Equals(previousTitle, StringComparison.OrdinalIgnoreCase))
        {
            _selectedNavigationItem = null;
            OnPropertyChanged(nameof(SelectedNavigationItem));
            // Keep the explicitly opened manual-import page active without adding
            // a permanent sidebar entry.
        }
        else
        {
            SelectedNavigationItem = available.FirstOrDefault(x =>
                x.Title.Equals("Dashboard", StringComparison.OrdinalIgnoreCase)) ?? _dashboardNavigationItem;
        }

        ApplicationState = BuildWorkspaceStatus(
            client,
            available);
        NotifyWorkspaceChanged();
    }

    private bool CanViewModule(string permission) =>
        DesktopPermissionService.HasPermission(_governanceRepository, permission);

    private void AppendGlobalNavigation(ICollection<NavigationItem> target)
    {
        // Team, Work and Notifications are authenticated collaboration surfaces, not accounting
        // mutation permissions. Every signed-in AAH user can open them; the Main Server/API still
        // enforces Send/Share/Manage/Approve capabilities on each action. This keeps a Read Only or
        // Import Only AAH user from losing the collaboration tabs merely because accounting access
        // is narrower than another role.
        if (NetworkClientRuntime.IsNetworkClient)
        {
            if (!target.Contains(_teamNavigationItem)) target.Add(_teamNavigationItem);
            if (!target.Contains(_workNavigationItem)) target.Add(_workNavigationItem);
            if (!target.Contains(_notificationNavigationItem)) target.Add(_notificationNavigationItem);
            if (_remoteAssistNavigationItem is not null && !target.Contains(_remoteAssistNavigationItem)) target.Add(_remoteAssistNavigationItem);
        }
        else
        {
            if ((CanViewModule(GovernancePermissions.MessagesSend) || CanViewModule(GovernancePermissions.FilesShare)) && !target.Contains(_teamNavigationItem)) target.Add(_teamNavigationItem);
            if ((CanViewModule(GovernancePermissions.ReadData) || CanViewModule(GovernancePermissions.TasksManage) || CanViewModule(GovernancePermissions.ApprovalsManage)) && !target.Contains(_workNavigationItem)) target.Add(_workNavigationItem);
            if ((CanViewModule(GovernancePermissions.ReadData) || CanViewModule(GovernancePermissions.ViewAudit)) && !target.Contains(_notificationNavigationItem)) target.Add(_notificationNavigationItem);
        }
        if (_administrationNavigationItem is not null && !target.Contains(_administrationNavigationItem)) target.Add(_administrationNavigationItem);
    }

    private static bool HasValidGstin(ClientProfile client) =>
        TallyCompanyInfo.NormalizeGstin(client.Gstin).Length == 15;

    private bool HasPurchaseData(ClientProfile client)
    {
        if (_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, ClientWorkspaceModule.Purchase) &&
            _workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var cached))
            return cached.Purchases.Count > 0;
        _workspaceStateCache.EnsureModulePrepared(client.Id, client.FinancialYear, ClientWorkspaceModule.Purchase);
        return false;
    }

    private bool HasSalesData(ClientProfile client)
    {
        if (_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, ClientWorkspaceModule.Sales) &&
            _workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var cached))
            return cached.Sales.Count > 0;
        _workspaceStateCache.EnsureModulePrepared(client.Id, client.FinancialYear, ClientWorkspaceModule.Sales);
        return false;
    }

    private static string BuildWorkspaceStatus(
        ClientProfile client,
        IReadOnlyCollection<NavigationItem> available)
    {
        var displayName = string.IsNullOrWhiteSpace(client.ClientName)
            ? client.ClientCode
            : client.ClientName.Trim();
        var fy = client.FinancialYear?.Trim() ?? string.Empty;
        return fy.Length > 0
            ? $"{displayName} • FY {fy}"
            : displayName;
    }

    private bool HasOpenImportFailures()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return false;
        if (_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, ClientWorkspaceModule.Actions) &&
            _workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var cached))
            return cached.OpenActions.Any(ActionRequiredPolicy.IsImportBlocking);
        _workspaceStateCache.EnsureModulePrepared(client.Id, client.FinancialYear, ClientWorkspaceModule.Actions);
        return false;
    }

    private bool HasCashData(ClientProfile client)
    {
        if (_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, ClientWorkspaceModule.Cash) &&
            _workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var cached)) return cached.CashEntries.Count > 0;
        _workspaceStateCache.EnsureModulePrepared(client.Id, client.FinancialYear, ClientWorkspaceModule.Cash);
        return false;
    }

    private bool HasStockData(ClientProfile client)
    {
        if (_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, ClientWorkspaceModule.Stock) &&
            _workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var cached)) return cached.StockItems.Count > 0;
        _workspaceStateCache.EnsureModulePrepared(client.Id, client.FinancialYear, ClientWorkspaceModule.Stock);
        return false;
    }

    private bool HasJournalData(ClientProfile client)
    {
        if (_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, ClientWorkspaceModule.Journal) &&
            _workspaceStateCache.TryPeekReadOnly(client.Id, client.FinancialYear, out var cached)) return cached.Journals.Count > 0;
        _workspaceStateCache.EnsureModulePrepared(client.Id, client.FinancialYear, ClientWorkspaceModule.Journal);
        return false;
    }

    private void OnClientWorkspaceOpenRequested(
        object? sender,
        EventArgs e)
    {
        if (_clientContext.CurrentClient is null)
        {
            return;
        }

        // A client selected from Client Directory must immediately become a real
        // workspace: populate its module navigation and move to Dashboard. This
        // defensive path prevents the contradictory state where the footer says
        // a client is active while the shell still says "Client Directory ready".
        RebuildWorkspaceNavigation(_dashboardNavigationItem.Title);
    }

    private void OnActionAvailabilityChanged(
        object? sender,
        EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => OnActionAvailabilityChanged(sender, e)));
            return;
        }

        _actionCenterViewModel.Refresh();

        if (HasActiveWorkspace)
        {
            RebuildWorkspaceNavigation(SelectedNavigationItem?.Title);
            return;
        }

        // Action Required is client-specific; no workspace means no global Action Center.
        if (NavigationItems.Contains(_actionCenterNavigationItem))
            NavigationItems.Remove(_actionCenterNavigationItem);
    }

    private void NotifyWorkspaceChanged()
    {
        OnPropertyChanged(nameof(HasActiveWorkspace));
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(CurrentClient));
        OnPropertyChanged(nameof(CurrentFinancialYear));
        OnPropertyChanged(nameof(CurrentClientDetail));
        OnPropertyChanged(nameof(WorkspaceButtonText));
        SharedAuthorityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnClientContextPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveClientContext.CurrentClient))
            RefreshBranchWorkspaces();
        NotifyWorkspaceChanged();
        ConfirmTopTallyLinkCommand.RaiseCanExecuteChanged();
        OpenLinkedTallyCommand.RaiseCanExecuteChanged();
        RefreshTallyCommand.RaiseCanExecuteChanged();

        if (e.PropertyName !=
            nameof(ActiveClientContext.CurrentClient))
        {
            return;
        }

        RefreshTopWorkspaceSelectors();

        if (_changingWorkspace)
        {
            return;
        }

        if (_clientContext.CurrentClient is null)
        {
            CloseWorkspaceCore(clearRepository: false);
            return;
        }

        var openedClient = _clientContext.CurrentClient;
        lock (_presentationWarmGenerations) _presentationWarmGenerations.Clear();
        _preparedPagePresentations.Clear();

        // Fix38: opening a Client is a cache-consumer operation, never the trigger for
        // normal accounting preparation. The shell paints immediately from the latest
        // startup-prepared/frozen snapshot. Timestamp/reactivation work runs after paint;
        // only a six-month frozen or still-missing stream is rescanned in background.
        RebuildWorkspaceNavigation(_dashboardNavigationItem.Title);
        if (HasBranchWorkspaces && !_isPrivateBranchActive) return; // never warm/display parent books as a branch.
        if (_workspaceStateCache.TryPeekReadOnly(openedClient.Id, openedClient.FinancialYear, out var openingSnapshot))
        {
            QueueWarmCurrentWorkspaceAfterPaint(openingSnapshot);
            QueueAllSafeModulePresentations(openingSnapshot);
        }
        _ = Task.Run(() => _workspaceStateCache.RecordClientOpenedAndEnsurePrepared(openedClient));

        // Fix38: do not start GSTIN/e-returns profile discovery from client-open. Auto Import's
        // registered-client GST bootstrap owns that work in the background; opening a client
        // remains snapshot -> paint -> timestamp only.

        // Do not regenerate Masters/Journal/GSTR-3B merely because the user opened
        // an unchanged client. That pipeline is triggered by real import/activity
        // changes. Running it on every workspace open competed with the cache reads
        // and made a 1,000-row client feel much larger than it is.
    }

    private void OnImportActivityChanged(
        object? sender,
        EventArgs e)
    {
        if (!HasActiveWorkspace)
        {
            return;
        }

        // ImportActivity is dashboard/audit metadata, not an accounting source event.
        // Purchase/Sales/Journal/GST modules request AccountingRefreshCoordinator
        // themselves when persisted accounting data changes. Running that pipeline
        // here caused a second full refresh after those modules completed and even
        // regenerated Journal/GST after Bank-only imports. Keep this event lightweight.
        // Accounting cache refresh is driven by the narrow AccountingLiveChange event,
        // not by generic import-activity/audit metadata.
        if (CurrentPage is DashboardViewModel)
        {
            _dashboardViewModel.RefreshAccountingOverviewAfterExternalChange();
        }
    }

    private void QueuePriorityWorkspaceRefreshAfterPaint(ClientProfile client)
    {
        // Fix38 retained compatibility hook. Client-open no longer calls this method: startup
        // preparation owns normal cache generation and RecordClientOpenedAndEnsurePrepared
        // promotes only missing/reactivated streams after the screen has already painted.
    }

    private async Task SynchronizeCurrentClientProfileAsync()
    {
        _profileSyncCancellation?.Cancel();
        _profileSyncCancellation?.Dispose();

        var cancellationSource =
            new CancellationTokenSource();
        _profileSyncCancellation =
            cancellationSource;
        var cancellationToken =
            cancellationSource.Token;

        var client =
            _clientContext.CurrentClient;

        if (client is null ||
            client.Gstin.Length != 15)
        {
            _profileSyncCancellation = null;
            cancellationSource.Dispose();
            return;
        }

        ApplicationState =
            "Checking GSTR-3B client profile…";

        try
        {
            var profile =
                await _gstinProfileLookupService.LookupAsync(
                    client.Gstin,
                    _eReturnsRootContext.RootPath,
                    cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var saved =
                _clientRepository.ApplyImportedGstProfile(
                    client.Id,
                    profile);

            if (_clientContext.CurrentClient?.Id == saved.Id &&
                HasProfileDifference(client, saved))
            {
                _clientContext.SetCurrent(saved);
            }

            var sourceName =
                profile.SourcePath.Length > 0
                    ? Path.GetFileName(profile.SourcePath)
                      ?? profile.SourcePath
                    : string.Empty;

            var status =
                profile.HasGstr3bMetadata
                    ? $"GSTR-3B profile updated from {sourceName}."
                    : profile.WasFound
                        ? $"GST client profile updated from {sourceName}."
                        : "No new Legal or Trade Name was found; PAN and State remain derived from GSTIN.";

            _profileSyncContext.Publish(
                saved.Id,
                status);
            ApplicationState = status;
        }
        catch (OperationCanceledException)
        {
            // A different workspace replaced this check.
        }
        catch (Exception exception)
        {
            var status =
                $"Client profile check could not complete: {exception.Message}";

            _profileSyncContext.Publish(
                client.Id,
                status);
            ApplicationState = status;
        }
        finally
        {
            if (ReferenceEquals(
                    _profileSyncCancellation,
                    cancellationSource))
            {
                _profileSyncCancellation = null;
                cancellationSource.Dispose();
            }
        }
    }

    private void RefreshTopWorkspaceSelectors()
    {
        var current = _clientContext.CurrentClient;
        var currentIdentity = current is null ? string.Empty : BuildTopClientIdentity(current);
        var priorGuard = _refreshingTopWorkspaceSelectors;
        _refreshingTopWorkspaceSelectors = true;
        try
        {
            var accessible = _clientRepository.GetAll(includeArchived: false)
                .Where(client => _governanceRepository.HasClientAccess(Environment.UserName, client.Id, client.FinancialYear))
                .ToArray();

            var choices = accessible
                .GroupBy(BuildTopClientIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var ordered = group
                        .OrderByDescending(profile => FinancialYearSortKey(profile.FinancialYear))
                        .ThenByDescending(profile => profile.UpdatedUtc)
                        .ToArray();
                    var displayProfile = current is not null && group.Any(profile => profile.Id == current.Id)
                        ? group.First(profile => profile.Id == current.Id)
                        : ordered.First();
                    return new TopClientWorkspaceChoice
                    {
                        IdentityKey = group.Key,
                        DisplayName = TopClientDisplayName(displayProfile),
                        Profiles = ordered
                    };
                })
                .OrderBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            TopClientChoices.Clear();
            foreach (var choice in choices)
                TopClientChoices.Add(choice);

            _selectedTopClientChoice = currentIdentity.Length == 0
                ? null
                : TopClientChoices.FirstOrDefault(choice => choice.IdentityKey.Equals(currentIdentity, StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged(nameof(SelectedTopClientChoice));

            RebuildTopFinancialYearChoices(_selectedTopClientChoice, current?.FinancialYear);
        }
        catch (Exception exception)
        {
            PipelineRuntimeDiagnostics.NavigationFailed("Top Client/FY selector refresh", exception);
        }
        finally
        {
            _refreshingTopWorkspaceSelectors = priorGuard;
        }
    }

    private void RebuildTopFinancialYearChoices(TopClientWorkspaceChoice? clientChoice, string? preferredFinancialYear)
    {
        var priorGuard = _refreshingTopWorkspaceSelectors;
        _refreshingTopWorkspaceSelectors = true;
        try
        {
            TopFinancialYearChoices.Clear();
            if (clientChoice is null)
            {
                _selectedTopFinancialYearChoice = null;
                OnPropertyChanged(nameof(SelectedTopFinancialYearChoice));
                return;
            }

            foreach (var profile in clientChoice.Profiles
                         .OrderByDescending(profile => FinancialYearSortKey(profile.FinancialYear))
                         .ThenByDescending(profile => profile.UpdatedUtc))
            {
                TopFinancialYearChoices.Add(new TopFinancialYearChoice
                {
                    ClientId = profile.Id,
                    FinancialYear = profile.FinancialYear.Trim()
                });
            }

            _selectedTopFinancialYearChoice = TopFinancialYearChoices.FirstOrDefault(choice =>
                preferredFinancialYear is not null &&
                choice.FinancialYear.Equals(preferredFinancialYear.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? TopFinancialYearChoices.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedTopFinancialYearChoice));
        }
        finally
        {
            _refreshingTopWorkspaceSelectors = priorGuard;
        }
    }

    private void SwitchTopWorkspace(long clientId)
    {
        if (clientId <= 0 || _clientContext.CurrentClient?.Id == clientId)
            return;

        try
        {
            var selected = _clientRepository.GetById(clientId)
                ?? throw new InvalidOperationException("The selected Client/FY is no longer available.");
            if (selected.IsArchived)
                throw new InvalidOperationException("The selected Client/FY is archived.");
            if (NetworkClientRuntime.IsNetworkClient && NetworkClientRuntime.AccountingAccessSuspended)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(NetworkClientRuntime.AccountingAccessSuspensionReason)
                    ? "Main Server policy currently suspends accounting access on this device."
                    : NetworkClientRuntime.AccountingAccessSuspensionReason);
            if (!_governanceRepository.HasClientAccess(Environment.UserName, selected.Id, selected.FinancialYear))
                throw new InvalidOperationException("Your application user does not have access to this Client / Financial Year.");

            // This uses the same authority transition as Client Directory. The existing
            // ActiveClientContext handler remains the single cache-first navigation path:
            // prepared snapshot -> immediate paint -> background freshness/Tally work.
            _clientRepository.SetCurrent(selected.Id);
            var current = _clientRepository.GetCurrent()
                ?? throw new InvalidOperationException("AAH could not activate the selected Client/FY.");
            _clientContext.SetCurrent(current);
            ApplicationState = $"{TopClientDisplayName(current)} • FY {current.FinancialYear}";
        }
        catch (Exception exception)
        {
            RefreshTopWorkspaceSelectors();
            ApplicationState = "Client/FY was not changed: " + exception.Message;
            MessageBox.Show(exception.Message, "Change Client / FY", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string BuildTopClientIdentity(ClientProfile profile)
    {
        var gstin = new string((profile.Gstin ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (gstin.Length == 15)
            return "GSTIN|" + gstin;

        var pan = new string((profile.Pan ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (pan.Length == 10)
            return "PAN|" + pan;

        var legal = NormalizeTopClientToken(profile.LegalName);
        if (legal.Length > 0)
            return "LEGAL|" + legal;

        return "NAME|" + NormalizeTopClientToken(profile.ClientName) + "|STATE|" + NormalizeTopClientToken(profile.State);
    }

    private static string NormalizeTopClientToken(string? value) =>
        new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static string TopClientDisplayName(ClientProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.ClientName)) return profile.ClientName.Trim();
        if (!string.IsNullOrWhiteSpace(profile.TradeName)) return profile.TradeName.Trim();
        if (!string.IsNullOrWhiteSpace(profile.LegalName)) return profile.LegalName.Trim();
        return profile.ClientCode.Trim();
    }

    private static int FinancialYearSortKey(string? financialYear)
    {
        var digits = new string((financialYear ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length >= 4 && int.TryParse(digits[..4], out var year) ? year : 0;
    }

    private bool CanOpenLinkedTally() =>
        !_isPrivateBranchActive && !NetworkClientRuntime.IsNetworkClient &&
        _clientContext.CurrentClient is not null;

    private async Task OpenLinkedTallyAsync()
    {
        if (_isPrivateBranchActive)
        {
            ApplicationState = "Use the verified parent Tally company. Private branch linking is disabled until ledger read-back is complete.";
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            ApplicationState = "Open a Client/FY before opening linked Tally.";
            return;
        }

        var link = _tallyRepository.GetLinkForClient(client.Id);
        if (link is null)
        {
            var message = "This Client/FY does not have a confirmed Tally link yet. Open Tally, select the exact company in the top Tally dropdown and press Link first.";
            ApplicationState = message;
            MessageBox.Show(message, "Open Linked Tally", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = await _tallyApplicationLaunchService.OpenLinkedCompanyAsync(client, link, _tallyCoordinator);
        ApplicationState = result.Message;
        if (!result.Success)
            MessageBox.Show(result.Message, "Open Linked Tally", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowLinkedTallyInfo()
    {
        var client = _clientContext.CurrentClient;
        var link = client is null ? null : _tallyRepository.GetLinkForClient(client.Id);
        var saved = client is null ? null : _tallyApplicationLaunchService.GetSavedLaunchProfile(client.Id);
        var linkedText = link is null
            ? "No confirmed Tally company is linked to the active Client/FY."
            : $"Linked company: {link.CompanyName}\nCompany No.: {(string.IsNullOrWhiteSpace(link.CompanyNumber) ? "Not available" : link.CompanyNumber)}";
        var executableText = saved is not null && !string.IsNullOrWhiteSpace(saved.ExecutablePath)
            ? $"\nTally program: {saved.ExecutablePath}"
            : string.Empty;

        var message =
            "OPEN LINKED TALLY\n\n" +
            linkedText + executableText + "\n\n" +
            "• The red T button is only a launcher. It does not export, create, alter or delete any Tally voucher.\n" +
            "• If the linked company is already open, AAH brings the running Tally window to the front.\n" +
            "• Otherwise AAH uses a private Tally.ini with the exact company folder registered in Settings, then starts /NOINILOAD /LOAD:<Company Number>.\n" +
            "• Client and FY are separate authorities. Changing the top Client/FY selector changes which saved Tally link this button uses.\n" +
            "• AAH does not edit the installed Tally.ini. The exact data root comes only from the verified Client/FY company folder registered in Auto Tally Import settings.\n" +
            "• If Tally was moved or upgraded and AAH cannot locate it, open Tally once, select the exact company and press Link again.\n" +
            "• Manual Export and Automatic Tally Sync remain separate and keep the existing MASTERID/duplicate/version checks.";

        MessageBox.Show(message, "Tally Launcher Information", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task ConfirmTopTallyLinkAsync()
    {
        if (_isPrivateBranchActive)
        {
            ApplicationState = "Branch Tally linking is disabled until the saved parent company and branch ledgers pass verified read-back.";
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            ApplicationState = "Open an Accounting client before linking a Tally company.";
            return;
        }
        if (SelectedTallyCompany is null)
        {
            ApplicationState = "Select an open Tally company before pressing Link.";
            return;
        }

        try
        {
            var link = await _tallyCoordinator.ConfirmSelectedCompanyAsync();
            ApplicationState = $"Tally linked to {link.CompanyName} for {client.ClientCode}. Shared Audit ownership has been synchronized where an explicit Audit link exists.";
            _bankingViewModel.Refresh();
        }
        catch (Exception exception)
        {
            ApplicationState = "Tally link was not changed: " + exception.Message;
            MessageBox.Show(exception.Message, "Link Tally Company", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnTallyCoordinatorChanged(
        object? sender,
        EventArgs e)
    {
        OnPropertyChanged(nameof(TallyCompanies));
        OnPropertyChanged(nameof(SelectedTallyCompany));
        OnPropertyChanged(nameof(TallyConnectionStatus));
        OnPropertyChanged(nameof(IsTallyConnected));
        OnPropertyChanged(nameof(IsTallyBusy));
        RefreshTallyCommand.RaiseCanExecuteChanged();
        ConfirmTopTallyLinkCommand.RaiseCanExecuteChanged();
        OpenLinkedTallyCommand.RaiseCanExecuteChanged();

        // The coordinator raises Changed for status/master refreshes too. Re-run the
        // Bank/Tally reconciliation only on the actual disconnected -> connected edge;
        // Banking workspace open and explicit Refresh cover already-connected Tally.
        var tallyConnectedNow = IsTallyConnected;
        if (tallyConnectedNow && !_lastBankingTallyConnectionState)
            _bankingViewModel.NotifyTallyConnectionChanged(true);
        _lastBankingTallyConnectionState = tallyConnectedNow;

        // Fix92: the coordinator has already merged the current baseline/ALTERID delta
        // into the shared SQLite authority before this notification. Audit therefore
        // refreshes from local cached authority only and never adds a second Tally read.
        SharedTallyAuthorityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BridgeCommittedImportToCache(AutomationAccountingDataChangedEventArgs change)
    {
        // Background imports are global. No selected-client check belongs in the
        // authority notification path; the visible shell remains selected-client-only.
        var module = (change.Module ?? string.Empty).Trim();
        var area = module.Equals("Banking", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Banking
            : module.Equals("Cash", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Cash
            : module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Purchase
            : module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Sales
            : module.Equals("Journal", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Journal
            : module.Equals("GST", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Gst
            : module.Equals("Masters", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Masters
            : module.Equals("Stock", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.StockMasters
            : module.Equals("26AS", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.TaxCredit
            : module.Equals("Actions", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Actions
            : (AccountingLiveArea?)null;
        if (area.HasValue)
            _liveChangeHub.Publish(change.ClientId, change.FinancialYear, area.Value, $"{change.ImportMethod} committed {module}");
        _workspaceStateCache.QueueModuleRefresh(change.ClientId, change.FinancialYear, ClientWorkspaceModule.Actions);
    }

    public void NotifyExternalDataChanged(AutomationAccountingDataChangedEventArgs change)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != change.ClientId ||
            !client.FinancialYear.Equals(change.FinancialYear, StringComparison.OrdinalIgnoreCase))
        {
            BridgeCommittedImportToCache(change);
            return;
        }

        ApplicationState = $"{change.ImportMethod} updated {change.Module}: {change.RecordCount:N0} record(s).";
        PipelineRuntimeDiagnostics.LiveNotification(change.ClientId, change.FinancialYear, change.Module);
        // Fix91 Audit projection refreshes for every committed module, including domains such
        // as 26AS/Cash that are not represented by the historical AccountingLiveArea enum.
        SharedAuthorityChanged?.Invoke(this, EventArgs.Empty);

        // First update only the visible module from the authoritative repository.
        // The full workspace snapshot is queued afterwards.  This removes the old
        // import -> navigation rebuild -> cache snapshot -> navigation rebuild chain
        // that made imported rows appear several beats after the import completed.

        // Refresh only the currently visible module when its actual source changed.
        // RebuildWorkspaceNavigation itself no longer reloads the same page merely
        // because a cache snapshot temporarily cleared/reselected the sidebar item.
        var module = change.Module.Trim();

        // Auto Import commits originate in BackgroundAutomationService, outside the normal
        // page ViewModels that publish AccountingLiveChangeHub notifications. Bridge the
        // detailed committed event into the same live-change stream so Dashboard cards update
        // immediately from authoritative repositories instead of waiting for SnapshotReady.
        var liveArea = module.Equals("Banking", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Banking
            : module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Purchase
            : module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Sales
            : module.Equals("Journal", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Journal
            : module.Equals("GST", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Gst
            : module.Equals("Cash", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Cash
            : module.Equals("Masters", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Masters
            : module.Equals("Stock", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.StockMasters
            : module.Equals("26AS", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.TaxCredit
            : module.Equals("Actions", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Actions
            : (AccountingLiveArea?)null;
        if (liveArea is not null)
        {
            _liveChangeHub.Publish(change.ClientId, change.FinancialYear, liveArea.Value, $"{change.ImportMethod} committed {module}");

            // Fix127: every successful intake method uses the same post-import cache path.
            // The live-change handler invalidates the affected dependency streams; this
            // promotion makes the directly committed module authoritative immediately and
            // writes its persisted prepared cache without waiting behind startup/background work.
            var committedModule = module.Equals("Banking", StringComparison.OrdinalIgnoreCase) ? ClientWorkspaceModule.Banking
                : module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? ClientWorkspaceModule.Purchase
                : module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? ClientWorkspaceModule.Sales
                : module.Equals("Journal", StringComparison.OrdinalIgnoreCase) ? ClientWorkspaceModule.Journal
                : module.Equals("GST", StringComparison.OrdinalIgnoreCase) ? ClientWorkspaceModule.Gst
                : module.Equals("Cash", StringComparison.OrdinalIgnoreCase) ? ClientWorkspaceModule.Cash
                : module.Equals("Masters", StringComparison.OrdinalIgnoreCase) ? ClientWorkspaceModule.Masters
                : module.Equals("Stock", StringComparison.OrdinalIgnoreCase) ? ClientWorkspaceModule.Stock
                : (ClientWorkspaceModule?)null;
            if (committedModule.HasValue)
                _workspaceStateCache.QueuePriorityModuleRefresh(change.ClientId, change.FinancialYear, committedModule.Value);
        }

        // 26AS is a first-class prepared stream. Its page consumes the published cache. 

        // Completion 61: do not perform a second synchronous repository refresh in the
        // visible ViewModel. The live-change event above invalidates and queues only the
        // affected module streams; each module publishes its replacement independently.

        // One coalesced background snapshot updates counts/navigation after the visible
        // page has already populated. Action Required remains lightweight and immediate.
        _workspaceStateCache.QueueModuleRefresh(change.ClientId, change.FinancialYear, ClientWorkspaceModule.Actions);
        PipelineRuntimeDiagnostics.WriteReportIfDue();
    }

    private void OnWorkspaceModuleAuthoritativeReady(ClientWorkspaceModuleUpdate update)
    {
        if (update.Module != ClientWorkspaceModule.Stock || NetworkClientRuntime.IsNetworkClient ||
            !update.IsAuthoritative || !_workspaceStateCache.IsUpdateCurrent(update)) return;
        var scope = $"{update.ClientId}|{update.FinancialYear.Trim().ToUpperInvariant()}";
        if (!_startupStockVerified.TryAdd(scope, 0)) return;
        // A one-time startup verification is owned by the shared Stock authority, not
        // by Stock Master navigation. It is independent of which client/page is open.
        try
        {
            _stockMasterLiveProjectionService.Queue(update.ClientId, update.FinancialYear, string.Empty,
                "Startup imported Unit authority verification");
        }
        catch (Exception exception)
        {
            _startupStockVerified.TryRemove(scope, out _);
            PipelineRuntimeDiagnostics.NavigationFailed("Startup Stock authority", exception);
        }
    }

    private void OnWorkspaceModuleReady(ClientWorkspaceModuleUpdate update)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            var client = _clientContext.CurrentClient;
            if (client is null || client.Id != update.ClientId ||
                !client.FinancialYear.Equals(update.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;
            if (!_accountingPresentationActive || !_workspaceStateCache.IsUpdateCurrent(update)) return;

            // A background completion must not mutate a hidden WPF DataGrid or replay
            // an older event snapshot over a newer committed cache generation.
            try
            {
                InvalidatePreparedPresentations(update.Module);
                var page = CurrentPage;
                if (page is not null && VisibleModulesForPage(page).Contains(update.Module))
                    QueueAtomicVisiblePageRefresh(page, client);

                // R8 Repair 12: a module-ready event invalidates only presentations that
                // depend on that module, so warm only those pages. Repair 11 queued the full
                // ten-page sweep for every module completion, which repeatedly rebuilt hidden
                // WPF collections and stalled the dispatcher even though navigation ViewModels
                // were already cached.
                QueueHiddenModulePresentationWarm(update.Snapshot, update);
                QueueWorkspaceNavigationRefresh();
            }
            catch (Exception exception)
            {
                PipelineRuntimeDiagnostics.NavigationFailed($"Cache/{update.Module}", exception);
                ApplicationState = $"{update.Module} presentation failed; saved data is unchanged. See navigation diagnostics.";
            }
        }), DispatcherPriority.Background);
    }

    private void QueueAllSafeModulePresentations(ClientWorkspaceSnapshot snapshot)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        var generation = Interlocked.Increment(ref _safePresentationWarmGeneration);
        var clientId = snapshot.ClientId;
        var financialYear = snapshot.FinancialYear;

        // R8 Repair 11: coalesce bursts of module-ready events, then warm
        // one complete page presentation per idle slice. This reconnects the dormant fast-presentation
        // path without bringing back the old all-at-once dispatcher stall.
        _ = Task.Run(async () =>
        {
            await Task.Delay(60).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _safePresentationWarmGeneration)) return;
            QueueSafePresentationWarmSlice(clientId, financialYear, generation, 0);
        });
    }

    private void QueueSafePresentationWarmSlice(long clientId, string financialYear, int generation, int slice)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        _ = dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var client = _clientContext.CurrentClient;
            if (generation != Volatile.Read(ref _safePresentationWarmGeneration) ||
                client is null || client.Id != clientId ||
                !client.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase) ||
                !_accountingPresentationActive)
                return;

            try
            {
                switch (slice)
                {
                    case 0:
                        WarmHiddenMasterPresentation(client);
                        break;
                    case 1:
                        WarmHiddenPurchasePresentation(client);
                        break;
                    case 2:
                        WarmHiddenSalesPresentation(client);
                        break;
                    case 3:
                        WarmHiddenJournalPresentation(client);
                        break;
                    case 4:
                        WarmHiddenGstPresentation(client);
                        break;
                    case 5:
                        WarmHiddenStockPresentation(client);
                        break;
                    case 6:
                        WarmHiddenTaxCreditPresentation(client);
                        break;
                    case 7:
                        WarmHiddenStatementPresentations(client);
                        break;
                    case 8:
                        WarmHiddenActionPresentation(client);
                        break;
                    case 9:
                        WarmHiddenLedgerMappingPresentation(client);
                        break;
                }
            }
            catch (Exception exception)
            {
                PipelineRuntimeDiagnostics.NavigationFailed($"Idle presentation warm/{slice}", exception);
            }

            if (slice < 9 && generation == Volatile.Read(ref _safePresentationWarmGeneration))
                QueueSafePresentationWarmSlice(clientId, financialYear, generation, slice + 1);
        }));
    }

    private bool TryGetReadyPresentation(
        ClientProfile client,
        IReadOnlyList<ClientWorkspaceModule> modules,
        out ClientWorkspaceSnapshot snapshot)
    {
        if (modules.Any(module => !_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, module)))
        {
            snapshot = default!;
            return false;
        }

        return _workspaceStateCache.TryGetModulesForPresentation(
            client.Id, client.FinancialYear, modules, out snapshot);
    }

    private void WarmHiddenMasterPresentation(ClientProfile client)
    {
        if (ReferenceEquals(CurrentPage, _masterViewModel) || IsPagePresentationPrepared(_masterViewModel, client)) return;
        ClientWorkspaceModule[] modules = [ClientWorkspaceModule.Masters];
        if (TryGetReadyPresentation(client, modules, out var snapshot))
        {
            _masterViewModel.WarmFromPreparedSnapshot(snapshot);
            MarkPagePresentationPrepared(_masterViewModel, snapshot);
        }
    }

    private void WarmHiddenPurchasePresentation(ClientProfile client)
    {
        if (ReferenceEquals(CurrentPage, _purchaseViewModel) || IsPagePresentationPrepared(_purchaseViewModel, client)) return;
        ClientWorkspaceModule[] modules = [ClientWorkspaceModule.Purchase, ClientWorkspaceModule.Masters];
        if (!TryGetReadyPresentation(client, modules, out var snapshot)) return;
        _purchaseViewModel.WarmFromPreparedSnapshot(snapshot);
        _purchaseViewModel.WarmMasterDependenciesFromPreparedSnapshot(snapshot);
        MarkPagePresentationPrepared(_purchaseViewModel, snapshot);
    }

    private void WarmHiddenSalesPresentation(ClientProfile client)
    {
        if (ReferenceEquals(CurrentPage, _salesViewModel) || IsPagePresentationPrepared(_salesViewModel, client)) return;
        ClientWorkspaceModule[] modules = [ClientWorkspaceModule.Sales];
        if (TryGetReadyPresentation(client, modules, out var snapshot))
        {
            _salesViewModel.WarmFromPreparedSnapshot(snapshot);
            MarkPagePresentationPrepared(_salesViewModel, snapshot);
        }
    }

    private void WarmHiddenJournalPresentation(ClientProfile client)
    {
        if (ReferenceEquals(CurrentPage, _journalViewModel) || IsPagePresentationPrepared(_journalViewModel, client)) return;
        ClientWorkspaceModule[] modules =
            [ClientWorkspaceModule.Journal, ClientWorkspaceModule.Gst, ClientWorkspaceModule.Masters];
        if (!TryGetReadyPresentation(client, modules, out var snapshot)) return;
        _journalViewModel.WarmFromPreparedSnapshot(snapshot);
        _journalViewModel.WarmGstDependenciesFromPreparedSnapshot(snapshot);
        _journalViewModel.WarmMasterDependenciesFromPreparedSnapshot(snapshot);
        MarkPagePresentationPrepared(_journalViewModel, snapshot);
    }

    private void WarmHiddenGstPresentation(ClientProfile client)
    {
        if (ReferenceEquals(CurrentPage, _gstViewModel) || IsPagePresentationPrepared(_gstViewModel, client)) return;
        ClientWorkspaceModule[] modules = [ClientWorkspaceModule.Gst];
        if (TryGetReadyPresentation(client, modules, out var snapshot))
        {
            _gstViewModel.WarmFromPreparedSnapshot(snapshot);
            MarkPagePresentationPrepared(_gstViewModel, snapshot);
        }
    }

    private void WarmHiddenStockPresentation(ClientProfile client)
    {
        if (ReferenceEquals(CurrentPage, _stockMasterViewModel) || IsPagePresentationPrepared(_stockMasterViewModel, client)) return;
        ClientWorkspaceModule[] modules = [ClientWorkspaceModule.Stock];
        if (TryGetReadyPresentation(client, modules, out var snapshot))
        {
            _stockMasterViewModel.WarmFromPreparedSnapshot(snapshot);
            MarkPagePresentationPrepared(_stockMasterViewModel, snapshot);
        }
    }

    private void WarmHiddenTaxCreditPresentation(ClientProfile client)
    {
        if (ReferenceEquals(CurrentPage, _taxCreditViewModel) || IsPagePresentationPrepared(_taxCreditViewModel, client)) return;
        ClientWorkspaceModule[] modules = [ClientWorkspaceModule.TaxCredit, ClientWorkspaceModule.Masters];
        if (TryGetReadyPresentation(client, modules, out var snapshot))
        {
            _taxCreditViewModel.WarmFromPreparedSnapshot(snapshot);
            MarkPagePresentationPrepared(_taxCreditViewModel, snapshot);
        }
    }

    private void WarmHiddenStatementPresentations(ClientProfile client)
    {
        if (CurrentPage is AccountingStatementViewModel ||
            (IsPagePresentationPrepared(_balanceSheetViewModel, client) &&
             IsPagePresentationPrepared(_profitAndLossViewModel, client))) return;
        ClientWorkspaceModule[] modules = [ClientWorkspaceModule.Statements];
        if (!TryGetReadyPresentation(client, modules, out var snapshot)) return;
        _balanceSheetViewModel.WarmFromPreparedSnapshot(snapshot);
        _profitAndLossViewModel.WarmFromPreparedSnapshot(snapshot);
        MarkPagePresentationPrepared(_balanceSheetViewModel, snapshot);
        MarkPagePresentationPrepared(_profitAndLossViewModel, snapshot);
    }

    private void WarmHiddenActionPresentation(ClientProfile client)
    {
        if (ReferenceEquals(CurrentPage, _actionCenterViewModel) || IsPagePresentationPrepared(_actionCenterViewModel, client)) return;
        ClientWorkspaceModule[] modules = [ClientWorkspaceModule.Actions];
        if (TryGetReadyPresentation(client, modules, out var snapshot))
        {
            _actionCenterViewModel.WarmFromPreparedSnapshot(snapshot);
            MarkPagePresentationPrepared(_actionCenterViewModel, snapshot);
        }
    }

    private void WarmHiddenLedgerMappingPresentation(ClientProfile client)
    {
        // Banking and Cash retain their existing visible-page lifecycle because their
        // full presentation can queue reconciliation/repair work. Only the persisted
        // Ledger Mapping projection is safe to prebuild while hidden.
        if (CurrentPage is BankingViewModel or CashEntryViewModel) return;
        ClientWorkspaceModule[] modules = [ClientWorkspaceModule.LedgerMapping];
        if (TryGetReadyPresentation(client, modules, out var snapshot))
            _bankingViewModel.WarmFromPreparedSnapshot(snapshot, ClientWorkspaceModule.LedgerMapping);
    }

    private static string PresentationScope(ClientWorkspaceSnapshot snapshot) =>
        $"{snapshot.ClientId}|{snapshot.FinancialYear.Trim().ToUpperInvariant()}";

    private void MarkPagePresentationPrepared(PageViewModel page, ClientWorkspaceSnapshot snapshot) =>
        _preparedPagePresentations[page] = PresentationScope(snapshot);

    private bool IsPagePresentationPrepared(PageViewModel page, ClientProfile client)
    {
        var modules = VisibleModulesForPage(page);
        if (modules.Count == 0 ||
            modules.Any(module => !_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, module)))
            return false;
        return _preparedPagePresentations.TryGetValue(page, out var scope) &&
               scope.Equals($"{client.Id}|{client.FinancialYear.Trim().ToUpperInvariant()}", StringComparison.Ordinal);
    }

    private void InvalidatePreparedPresentations(ClientWorkspaceModule module)
    {
        foreach (var page in _preparedPagePresentations.Keys
                     .Where(page => VisibleModulesForPage(page).Contains(module))
                     .ToArray())
            _preparedPagePresentations.Remove(page);
    }

    private void QueueHiddenModulePresentationWarm(ClientWorkspaceSnapshot snapshot, ClientWorkspaceModuleUpdate update)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        var key = $"{snapshot.ClientId}|{snapshot.FinancialYear.Trim().ToUpperInvariant()}|{update.Module}";
        lock (_presentationWarmGenerations)
        {
            if (_presentationWarmGenerations.TryGetValue(key, out var prior) && prior >= update.Generation) return;
            _presentationWarmGenerations[key] = update.Generation;
        }

        _ = dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var client = _clientContext.CurrentClient;
            if (client is null || client.Id != snapshot.ClientId ||
                !client.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase) ||
                !_accountingPresentationActive || !_workspaceStateCache.IsUpdateCurrent(update)) return;
            try
            {
                // R8 Repair 12: warm only page presentations whose dependency set contains
                // the completed module. Each helper first proves all of its dependencies are
                // ready and skips an already-current presentation. This prevents one module
                // completion from replaying the entire hidden WPF presentation sweep.
                switch (update.Module)
                {
                    case ClientWorkspaceModule.LedgerMapping:
                        WarmHiddenLedgerMappingPresentation(client);
                        break;
                    case ClientWorkspaceModule.Purchase:
                        WarmHiddenPurchasePresentation(client);
                        break;
                    case ClientWorkspaceModule.Sales:
                        WarmHiddenSalesPresentation(client);
                        break;
                    case ClientWorkspaceModule.Journal:
                        WarmHiddenJournalPresentation(client);
                        break;
                    case ClientWorkspaceModule.Gst:
                        WarmHiddenGstPresentation(client);
                        WarmHiddenJournalPresentation(client);
                        break;
                    case ClientWorkspaceModule.Masters:
                        WarmHiddenMasterPresentation(client);
                        WarmHiddenPurchasePresentation(client);
                        WarmHiddenJournalPresentation(client);
                        WarmHiddenTaxCreditPresentation(client);
                        break;
                    case ClientWorkspaceModule.Stock:
                        WarmHiddenStockPresentation(client);
                        break;
                    case ClientWorkspaceModule.TaxCredit:
                        WarmHiddenTaxCreditPresentation(client);
                        break;
                    case ClientWorkspaceModule.Statements:
                        WarmHiddenStatementPresentations(client);
                        break;
                    case ClientWorkspaceModule.Actions:
                        WarmHiddenActionPresentation(client);
                        break;
                }
            }
            catch (Exception exception)
            {
                PipelineRuntimeDiagnostics.NavigationFailed($"Background presentation warm/{update.Module}", exception);
            }
        }));
    }

    private void OnWorkspaceModuleFailed(ClientWorkspaceModuleFailure failure)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            var client = _clientContext.CurrentClient;
            if (client is null || client.Id != failure.ClientId ||
                !client.FinancialYear.Equals(failure.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;

            ApplicationState = $"{failure.Module} prepared-cache read failed (attempt {failure.Attempt}): {failure.Error}";
            if (_accountingPresentationActive && failure.Module == ClientWorkspaceModule.Sales && CurrentPage is SalesImportViewModel)
                _salesViewModel.NotifyPreparedCacheFailure(failure.Error, failure.Attempt);
        }), DispatcherPriority.Background);
    }

    private void QueueWarmCurrentWorkspaceAfterPaint(ClientWorkspaceSnapshot snapshot)
    {
        if (!_accountingPresentationActive) return;
        // R8 Repair 11: the visible page still paints first from the latest prepared
        // snapshot. Safe hidden presentations are queued separately at ApplicationIdle,
        // one complete page dependency-set at a time, so navigation does not become
        // the first place where names/counts/rows are constructed.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            try
            {
                var page = CurrentPage;
                IReadOnlyList<ClientWorkspaceModule> modules = page is null ? Array.Empty<ClientWorkspaceModule>() : VisibleModulesForPage(page);
                if (page is not null && modules.Count > 0 &&
                    _workspaceStateCache.TryGetModulesForPresentation(snapshot.ClientId, snapshot.FinancialYear, modules, out var latest))
                    WarmVisiblePageFromSnapshot(page, latest);
            }
            catch (Exception exception) { PipelineRuntimeDiagnostics.NavigationFailed("Workspace snapshot presentation", exception); }
            return;
        }
        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var current = _clientContext.CurrentClient;
            if (current is null || current.Id != snapshot.ClientId ||
                !current.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;
            if (!_accountingPresentationActive) return;
            try
            {
                var page = CurrentPage;
                IReadOnlyList<ClientWorkspaceModule> modules = page is null ? Array.Empty<ClientWorkspaceModule>() : VisibleModulesForPage(page);
                if (page is not null && modules.Count > 0 &&
                    _workspaceStateCache.TryGetModulesForPresentation(current.Id, current.FinancialYear, modules, out var latest))
                    WarmVisiblePageFromSnapshot(page, latest);
            }
            catch (Exception exception) { PipelineRuntimeDiagnostics.NavigationFailed("Workspace snapshot presentation", exception); }
        }));
    }

    private void WarmCurrentWorkspaceFromSnapshot(ClientWorkspaceSnapshot snapshot)
    {
        if (CurrentPage is not null) WarmVisiblePageFromSnapshot(CurrentPage, snapshot);
    }

    private void WarmVisibleWorkspaceModule(ClientWorkspaceSnapshot snapshot, ClientWorkspaceModule module)
    {
        var page = CurrentPage;
        if (page is null) return;
        switch (page)
        {
            case DashboardViewModel:
                // Dashboard reads published/cache outputs; it does not hydrate hidden modules.
                _dashboardViewModel.RefreshAccountingOverviewAfterExternalChange();
                return;
            case BankingViewModel when module is ClientWorkspaceModule.Banking or ClientWorkspaceModule.LedgerMapping or ClientWorkspaceModule.Masters:
            case CashEntryViewModel when module is ClientWorkspaceModule.Cash or ClientWorkspaceModule.Masters:
                _bankingViewModel.WarmFromPreparedSnapshot(snapshot, module);
                return;
            case PurchaseImportViewModel when module == ClientWorkspaceModule.Purchase:
                _purchaseViewModel.WarmFromPreparedSnapshot(snapshot);
                return;
            case PurchaseImportViewModel when module == ClientWorkspaceModule.Masters:
                _purchaseViewModel.WarmMasterDependenciesFromPreparedSnapshot(snapshot);
                return;
            case SalesImportViewModel when module == ClientWorkspaceModule.Sales:
                _salesViewModel.WarmFromPreparedSnapshot(snapshot);
                return;
            case JournalViewModel when module == ClientWorkspaceModule.Journal:
                _journalViewModel.WarmFromPreparedSnapshot(snapshot);
                return;
            case JournalViewModel when module == ClientWorkspaceModule.Gst:
                _journalViewModel.WarmGstDependenciesFromPreparedSnapshot(snapshot);
                return;
            case JournalViewModel when module == ClientWorkspaceModule.Masters:
                _journalViewModel.WarmMasterDependenciesFromPreparedSnapshot(snapshot);
                return;
            case GstReconciliationViewModel when module == ClientWorkspaceModule.Gst:
                _gstViewModel.WarmFromPreparedSnapshot(snapshot);
                return;
            case MasterCentreViewModel when module == ClientWorkspaceModule.Masters:
                _masterViewModel.WarmFromPreparedSnapshot(snapshot);
                return;
            case StockMasterViewModel when module == ClientWorkspaceModule.Stock:
                _stockMasterViewModel.WarmFromPreparedSnapshot(snapshot);
                return;
            case TaxCreditReconciliationViewModel when module == ClientWorkspaceModule.TaxCredit:
                _taxCreditViewModel.WarmFromPreparedSnapshot(snapshot);
                return;
            case AccountingStatementViewModel when module == ClientWorkspaceModule.Statements:
                if (ReferenceEquals(page, _balanceSheetViewModel)) _balanceSheetViewModel.WarmFromPreparedSnapshot(snapshot);
                else if (ReferenceEquals(page, _profitAndLossViewModel)) _profitAndLossViewModel.WarmFromPreparedSnapshot(snapshot);
                return;
            case ActionCenterViewModel when module == ClientWorkspaceModule.Actions:
                _actionCenterViewModel.WarmFromPreparedSnapshot(snapshot);
                return;
        }
    }

    private void WarmVisiblePageFromSnapshot(PageViewModel page, ClientWorkspaceSnapshot snapshot)
    {
        // R8 Repair 8 presentation transaction: a visible page is hydrated from one
        // projected Client/FY snapshot. Do not paint Purchase first and Masters later,
        // or Journal first and GST/Masters later. All visible streams are applied in the
        // same dispatcher turn so tab headers, names, counts and rows move together.
        foreach (var module in VisibleModulesForPage(page))
            WarmVisibleWorkspaceModule(snapshot, module);
        MarkPagePresentationPrepared(page, snapshot);
    }

    private void QueueAtomicVisiblePageRefresh(PageViewModel page, ClientProfile client)
    {
        var modules = VisibleModulesForPage(page);
        if (modules.Count == 0) return;

        // Do not replace a complete on-screen snapshot with a partially refreshed one.
        // Invalidation clears module readiness, so the last committed presentation remains
        // visible until every stream required by this page is ready again.
        if (modules.Any(module => !_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, module)))
            return;

        var generation = Interlocked.Increment(ref _visiblePresentationGeneration);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            if (_workspaceStateCache.TryGetModulesForPresentation(client.Id, client.FinancialYear, modules, out var immediate))
                WarmVisiblePageFromSnapshot(page, immediate);
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(45).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _visiblePresentationGeneration)) return;
            _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var current = _clientContext.CurrentClient;
                if (generation != Volatile.Read(ref _visiblePresentationGeneration) ||
                    !ReferenceEquals(CurrentPage, page) ||
                    current is null || current.Id != client.Id ||
                    !current.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase) ||
                    modules.Any(module => !_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, module)))
                    return;

                if (_workspaceStateCache.TryGetModulesForPresentation(client.Id, client.FinancialYear, modules, out var latest))
                    WarmVisiblePageFromSnapshot(page, latest);
            }));
        });
    }

    private static IReadOnlyList<ClientWorkspaceModule> VisibleModulesForPage(PageViewModel page) => page switch
    {
        BankingViewModel => [ClientWorkspaceModule.Banking, ClientWorkspaceModule.LedgerMapping, ClientWorkspaceModule.Masters],
        CashEntryViewModel => [ClientWorkspaceModule.Cash, ClientWorkspaceModule.Masters],
        PurchaseImportViewModel => [ClientWorkspaceModule.Purchase, ClientWorkspaceModule.Masters],
        SalesImportViewModel => [ClientWorkspaceModule.Sales],
        JournalViewModel => [ClientWorkspaceModule.Journal, ClientWorkspaceModule.Gst, ClientWorkspaceModule.Masters],
        GstReconciliationViewModel => [ClientWorkspaceModule.Gst],
        MasterCentreViewModel => [ClientWorkspaceModule.Masters],
        StockMasterViewModel => [ClientWorkspaceModule.Stock],
        TaxCreditReconciliationViewModel => [ClientWorkspaceModule.TaxCredit, ClientWorkspaceModule.Masters],
        AccountingStatementViewModel => [ClientWorkspaceModule.Statements],
        ActionCenterViewModel => [ClientWorkspaceModule.Actions],
        _ => Array.Empty<ClientWorkspaceModule>()
    };

    private static bool SnapshotHasModuleData(ClientWorkspaceSnapshot snapshot, ClientWorkspaceModule module) => module switch
    {
        ClientWorkspaceModule.Banking => snapshot.BankAccounts.Count > 0 || snapshot.BankTransactions.Count > 0,
        ClientWorkspaceModule.LedgerMapping => snapshot.BankLedgerMappings.Count > 0,
        ClientWorkspaceModule.Cash => snapshot.CashEntries.Count > 0,
        ClientWorkspaceModule.Purchase => snapshot.Purchases.Count > 0,
        ClientWorkspaceModule.Sales => snapshot.Sales.Count > 0,
        ClientWorkspaceModule.Journal => snapshot.Journals.Count > 0,
        ClientWorkspaceModule.Gst => snapshot.GstReturns.Count > 0 || snapshot.GstLedgerPeriods.Count > 0 || snapshot.GstReconciliation.Count > 0,
        ClientWorkspaceModule.Masters => snapshot.Masters.Count > 0,
        ClientWorkspaceModule.Stock => snapshot.StockItems.Count > 0 || snapshot.StockUnits.Count > 0,
        ClientWorkspaceModule.Statements => snapshot.StatementMasters.Count > 0 || snapshot.LedgerTotals.Count > 0,
        ClientWorkspaceModule.Actions => snapshot.OpenActions.Count > 0,
        ClientWorkspaceModule.TaxCredit => snapshot.TaxCreditRows.Count > 0,
        _ => false
    };

    private void WarmCurrentWorkspaceModule(ClientWorkspaceSnapshot snapshot, ClientWorkspaceModule module)
    {
        switch (module)
        {
            case ClientWorkspaceModule.Banking:
            case ClientWorkspaceModule.LedgerMapping:
            case ClientWorkspaceModule.Cash:
                _bankingViewModel.WarmFromPreparedSnapshot(snapshot, module);
                break;
            case ClientWorkspaceModule.Purchase:
                _purchaseViewModel.WarmFromPreparedSnapshot(snapshot);
                break;
            case ClientWorkspaceModule.Sales:
                _salesViewModel.WarmFromPreparedSnapshot(snapshot);
                break;
            case ClientWorkspaceModule.Journal:
                _journalViewModel.WarmFromPreparedSnapshot(snapshot);
                break;
            case ClientWorkspaceModule.Gst:
                _gstViewModel.WarmFromPreparedSnapshot(snapshot);
                _journalViewModel.WarmGstDependenciesFromPreparedSnapshot(snapshot);
                break;
            case ClientWorkspaceModule.Masters:
                _masterViewModel.WarmFromPreparedSnapshot(snapshot);
                _bankingViewModel.WarmFromPreparedSnapshot(snapshot, module);
                _journalViewModel.WarmMasterDependenciesFromPreparedSnapshot(snapshot);
                _purchaseViewModel.WarmMasterDependenciesFromPreparedSnapshot(snapshot);
                break;
            case ClientWorkspaceModule.Stock:
                _stockMasterViewModel.WarmFromPreparedSnapshot(snapshot);
                break;
            case ClientWorkspaceModule.Statements:
                _balanceSheetViewModel.WarmFromPreparedSnapshot(snapshot);
                _profitAndLossViewModel.WarmFromPreparedSnapshot(snapshot);
                break;
            case ClientWorkspaceModule.Actions:
                _actionCenterViewModel.WarmFromPreparedSnapshot(snapshot);
                break;
        }
    }

    private void QueueWorkspaceNavigationRefresh()
    {
        if (!_accountingPresentationActive) return;
        var version = Interlocked.Increment(ref _workspaceNavigationRefreshVersion);
        _ = Task.Run(async () =>
        {
            await Task.Delay(140).ConfigureAwait(false);
            if (version != Volatile.Read(ref _workspaceNavigationRefreshVersion)) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || !_accountingPresentationActive) return;
            await dispatcher.InvokeAsync(() =>
            {
                if (!_accountingPresentationActive) return;
                try { RebuildWorkspaceNavigation(SelectedNavigationItem?.Title); }
                catch (Exception exception)
                {
                    PipelineRuntimeDiagnostics.NavigationFailed("Sidebar refresh", exception);
                    ApplicationState = "Navigation refresh failed; saved data is unchanged. See navigation diagnostics.";
                }
            }, DispatcherPriority.Background);
        });
    }

    private void OnAccountingLiveCacheChanged(object? sender, AccountingLiveChange change)
    {
        // Fix118: Stock Master edits are live accounting authority. Re-normalize already
        // imported Purchase/Sales inventory rows from their original values; never require
        // the user to reimport a workbook just to apply an Item/Unit/Profile correction.
        if (change.Area == AccountingLiveArea.StockMasters &&
            !change.Reason.StartsWith("Live Stock Master projection completed", StringComparison.OrdinalIgnoreCase))
        {
            var client = _clientContext.CurrentClient;
            var company = client is not null && client.Id == change.ClientId
                ? (string.IsNullOrWhiteSpace(client.LegalName) ? client.ClientName : client.LegalName)
                : string.Empty;
            _stockMasterLiveProjectionService.Queue(change.ClientId, change.FinancialYear, company, change.Reason);
        }

        // The Audit window is a projection over the same committed accounting authority.
        // Notify it immediately; the Audit ViewModel coalesces/dispatches refresh work.
        SharedAuthorityChanged?.Invoke(this, EventArgs.Empty);

        // ClientWorkspaceStateCache subscribes directly to the committed-change hub.
        // This ViewModel only handles presentation and Stock reprojection; it must not
        // duplicate invalidation or make background authority depend on the open page.

    }


    public void NotifyAutomationConfigurationChanged()
    {
        ApplicationState = "Automation Folder link updated.";
        _clientsViewModel.RefreshFromExternalChange();
        if (HasActiveWorkspace)
            RebuildWorkspaceNavigation(SelectedNavigationItem?.Title);
    }

    private void OnAutomationClientRegistryChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess())
        {
            _clientsViewModel.RefreshFromExternalChange();
            RefreshTopWorkspaceSelectors();
            return;
        }

        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            _clientsViewModel.RefreshFromExternalChange();
            RefreshTopWorkspaceSelectors();
        }));
    }

    private void ReleasePageForNavigation(PageViewModel? page)
    {
        var client = _clientContext.CurrentClient;
        if (page is not null && client is not null)
            _workspaceStateCache.CheckpointPresentationClose(client.Id, client.FinancialYear, VisibleModulesForPage(page));

        switch (page)
        {
            case FinanceControlCenterViewModel finance:
                finance.CheckpointPresentationClose();
                finance.SetPresentationActive(false);
                break;
            case PurchaseImportViewModel purchase:
                purchase.ReleaseForNavigation();
                break;
            case JournalViewModel journal:
                journal.ReleaseForNavigation();
                break;
            case GstReconciliationViewModel gst:
                gst.ReleaseForNavigation();
                break;
        }
    }

    private void SchedulePageTransition(PageViewModel? previousPage, PageViewModel? page)
    {
        if (page is null) return;
        var generation = Interlocked.Increment(ref _navigationPrepareGeneration);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ReleasePageForNavigation(previousPage);
            PreparePageForDisplay(page);
            return;
        }

        // R8 Repair 12: when the page-level presentation is already current, there is
        // nothing left to coalesce. Reuse it immediately instead of imposing the 90 ms
        // settle delay on every normal module click.
        var currentClient = _clientContext.CurrentClient;
        if (currentClient is not null && IsPagePresentationPrepared(page, currentClient))
        {
            ReleasePageForNavigation(previousPage);
            PreparePageForDisplay(page);
            return;
        }

        // R8 R6 navigation safety bar: CurrentPage changes immediately so WPF paints the
        // requested module at once, but prepared-snapshot hydration is coalesced for a very
        // short settle window. Rapid module clicks therefore prepare only the page the user
        // actually stops on; no feature, cache authority or refresh path is removed.
        _ = Task.Run(async () =>
        {
            await Task.Delay(90).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _navigationPrepareGeneration)) return;
            _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (generation != Volatile.Read(ref _navigationPrepareGeneration) ||
                    !ReferenceEquals(CurrentPage, page)) return;
                try
                {
                    if (!ReferenceEquals(previousPage, CurrentPage)) ReleasePageForNavigation(previousPage);
                    PreparePageForDisplay(page);
                }
                catch (Exception exception)
                {
                    PipelineRuntimeDiagnostics.NavigationFailed(page.GetType().Name, exception);
                    ApplicationState = $"Could not prepare {page.Title}: {exception.Message}";
                }
            }));
        });
    }

    private void PreparePageForDisplay(PageViewModel? page)
    {
        var client = _clientContext.CurrentClient;
        IReadOnlyList<ClientWorkspaceModule> visibleModules = page is null ? Array.Empty<ClientWorkspaceModule>() : VisibleModulesForPage(page);
        if (page is not null && client is not null && visibleModules.Count > 0 &&
            !IsPagePresentationPrepared(page, client) &&
            visibleModules.All(module => _workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, module)) &&
            _workspaceStateCache.TryGetModulesForPresentation(client.Id, client.FinancialYear, visibleModules, out var preparedSnapshot))
            WarmVisiblePageFromSnapshot(page, preparedSnapshot);

        if (page is not null && client is not null)
        {
            foreach (var module in VisibleModulesForPage(page))
                if (!_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, module))
                    _workspaceStateCache.EnsureModulePrepared(client.Id, client.FinancialYear, module);
        }

        switch (page)
        {
            case DashboardViewModel:
                _dashboardViewModel.RefreshAccountingOverviewAfterExternalChange();
                break;
            case PurchaseImportViewModel:
                _purchaseViewModel.PrepareForDisplay();
                break;
            case SalesImportViewModel:
                _salesViewModel.PrepareForDisplay();
                break;
            case BankingViewModel:
                _bankingViewModel.PrepareForDisplay();
                break;
            case CashEntryViewModel:
                _bankingViewModel.PrepareForDisplay();
                break;
            case MasterCentreViewModel:
                _masterViewModel.PrepareForDisplay();
                break;
            case StockMasterViewModel:
                _stockMasterViewModel.PrepareForDisplay();
                break;
            case FixedAssetsViewModel:
                _fixedAssetsViewModel.PrepareForDisplay();
                break;
            case JournalViewModel:
                _journalViewModel.PrepareForDisplay();
                break;
            case AccountingStatementViewModel accountingStatementViewModel:
                accountingStatementViewModel.PrepareForDisplay();
                break;
            case FinancialStatementsViewModel financialStatementsViewModel:
                financialStatementsViewModel.PrepareForDisplay();
                break;
            case GstReconciliationViewModel gstReconciliationViewModel:
                gstReconciliationViewModel.PrepareForDisplay();
                break;
            case TaxCreditReconciliationViewModel taxCreditReconciliationViewModel:
                taxCreditReconciliationViewModel.PrepareForDisplay();
                break;
            case FinanceControlCenterViewModel:
                _financeControlCenterViewModel.PrepareForDisplay();
                break;
            case MobileLinkViewModel mobileLinkViewModel:
                mobileLinkViewModel.PrepareForDisplay();
                break;
            case ActionCenterViewModel:
            {
                // Fix32/Fix38: normal Action Center navigation is cache-first. Reuse the
                // already-resolved current client; do not shadow it or reload merely because
                // the page became visible.
                if (client is null)
                {
                    _actionCenterViewModel.Refresh();
                    break;
                }
                if (_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, ClientWorkspaceModule.Actions) &&
                    _workspaceStateCache.TryGetModulesForPresentation(client.Id, client.FinancialYear, new[] { ClientWorkspaceModule.Actions }, out var snapshot))
                    _actionCenterViewModel.WarmFromPreparedSnapshot(snapshot);
                else
                    _workspaceStateCache.EnsureModulePrepared(client.Id, client.FinancialYear, ClientWorkspaceModule.Actions);
                break;
            }
            case NotificationCenterViewModel notificationCenterViewModel:
                notificationCenterViewModel.Refresh();
                break;
            case RemoteAssistViewModel remoteAssistViewModel:
                remoteAssistViewModel.Refresh();
                break;
        }
    }

    private int _shutdownStarted;

    private static void TryShutdown(Action action)
    {
        try { action(); }
        catch (Exception exception) { App.WriteShutdownDiagnostic("Workspace cleanup error: " + exception); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;
        // Stop Tally polling and cancel its HTTP client before draining caches or
        // disposing presentations. A failure in another service must not skip this.
        _tallyCoordinator.Changed -= OnTallyCoordinatorChanged;
        TryShutdown(() => _tallyClientViewModel.Dispose());
        TryShutdown(() => _mobileLinkViewModel.Dispose());
        App.WriteShutdownDiagnostic("Stopping Tally connection timers and HTTP requests.");
        TryShutdown(() => _tallyCoordinator.Dispose());
        App.WriteShutdownDiagnostic("Tally coordinator disposal completed.");
        TryShutdown(() => _profileSyncCancellation?.Cancel());
        TryShutdown(() => _profileSyncCancellation?.Dispose());
        TryShutdown(() => _clientsViewModel.StopMobileAccessRefresh());
        _clientsViewModel.WorkspaceOpenRequested -= OnClientWorkspaceOpenRequested;
        _actionCenterViewModel.ActionAvailabilityChanged -= OnActionAvailabilityChanged;
        _automationService.ActionRequiredChanged -= OnActionAvailabilityChanged;
        _automationService.ClientRegistryChanged -= OnAutomationClientRegistryChanged;
        _workspaceStateCache.ModuleReady -= OnWorkspaceModuleReady;
        _workspaceStateCache.ModuleAuthoritativeReady -= OnWorkspaceModuleAuthoritativeReady;
        _workspaceStateCache.ModuleFailed -= OnWorkspaceModuleFailed;
        TryShutdown(() => _stockMasterLiveProjectionService.Dispose());
        _liveChangeHub.Changed -= OnAccountingLiveCacheChanged;
        TryShutdown(() => _dashboardViewModel.Dispose());
        TryShutdown(() => _financeControlCenterViewModel.Dispose());
        TryShutdown(() => _settingsViewModel?.Dispose());
        TryShutdown(() => _remoteAssistViewModel?.Dispose());
        App.WriteShutdownDiagnostic("Draining prepared workspace cache.");
        TryShutdown(() => _workspaceStateCache.Dispose());
        App.WriteShutdownDiagnostic("Workspace cache disposal completed.");
    }

    private static bool HasProfileDifference(
        ClientProfile before,
        ClientProfile after) =>
        !before.LegalName.Equals(after.LegalName, StringComparison.Ordinal) ||
        !before.TradeName.Equals(after.TradeName, StringComparison.Ordinal) ||
        !before.Pan.Equals(after.Pan, StringComparison.Ordinal) ||
        !before.State.Equals(after.State, StringComparison.Ordinal) ||
        !before.GstProfileSourceType.Equals(after.GstProfileSourceType, StringComparison.Ordinal) ||
        !before.GstProfileSourcePath.Equals(after.GstProfileSourcePath, StringComparison.Ordinal) ||
        !before.LatestGstr3bPeriod.Equals(after.LatestGstr3bPeriod, StringComparison.Ordinal) ||
        !before.LatestGstr3bPeriodEndKey.Equals(after.LatestGstr3bPeriodEndKey, StringComparison.Ordinal) ||
        !before.LatestGstr3bArn.Equals(after.LatestGstr3bArn, StringComparison.Ordinal) ||
        !before.LatestGstr3bArnDate.Equals(after.LatestGstr3bArnDate, StringComparison.Ordinal);
}
