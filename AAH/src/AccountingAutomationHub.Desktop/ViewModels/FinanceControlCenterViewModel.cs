using System.Linq;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Diagnostics;
using System.Windows;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Desktop.Views;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Finance;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Finance;
using AccountingAutomationHub.Infrastructure.Governance;
using Microsoft.Win32;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class FinanceControlCenterViewModel : PageViewModel, IDisposable
{
    private readonly ActiveClientContext _clientContext;
    private readonly SqliteFinanceControlRepository _repository;
    private readonly ApplicationBackupService _backupService;
    private readonly BackgroundAutomationService _automationService;
    private readonly AccountingLiveChangeHub _liveChangeHub;
    private readonly SqliteGovernanceRepository _governanceRepository;
    private readonly ServerSecurityRepository _serverSecurityRepository;
    private readonly ImportUndoService _undoService;
    private readonly VerifiedBackupService _verifiedBackupService;
    private readonly ApplicationPreferenceService _settings;
    private bool _disposed;
    private bool _refreshing;
    private readonly PersistentProjectionCache<FinancePreparedSnapshot> _preparedFinance;
    private readonly WorkspaceSourceChangeMonitor _financeSourceMonitor;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long ClientId, string FinancialYear, DateTime Through)> _preparedScopes = new(StringComparer.OrdinalIgnoreCase);
    private bool _presentationActive;
    private int _backgroundPreparationStarted;
    private string _statusText = "Open a client to use Finance Center.";
    private DateTime _closeThroughDate = DateTime.Today;
    private string _searchText = string.Empty;
    private string _lockModule = "All";
    private DateTime _lockDate = DateTime.Today;
    private string _lockReason = "Month-end close";
    private AccountingPeriodLock? _selectedPeriodLock;
    private FinanceAgingRow? _selectedAging;
    private decimal _settlementAmount;
    private DateTime _settlementDate = DateTime.Today;
    private string _settlementReference = string.Empty;
    private FixedAssetRecord _editingAsset = new();
    private FixedAssetRecord? _selectedAsset;
    private BankRuleRecord _editingBankRule = new();
    private BankRuleRecord? _selectedBankRule;
    private FinanceBudgetRecord _editingBudget = new();
    private FinanceBudgetRecord? _selectedBudget;
    private RecurringCashFlowTemplate _editingRecurring = new() { StartDate = DateTime.Today };
    private RecurringCashFlowTemplate? _selectedRecurring;
    private FinanceApprovalItem _editingApproval = new() { Status = "Pending" };
    private FinanceApprovalItem? _selectedApproval;
    private InventoryPolicyRecord _editingInventoryPolicy = new();
    private InventoryPolicyRecord? _selectedInventoryPolicy;
    private ImportRunRecord? _selectedImportRun;
    private ImportJobRecord? _selectedImportJob;
    private DocumentVaultRecord? _selectedVaultDocument;
    private EntityVersionRecord? _selectedVersion;
    private ApplicationUserRecord _editingApplicationUser = new();
    private ApplicationUserRecord? _selectedApplicationUser;
    private VerifiedBackupRecord? _selectedVerifiedBackup;
    private LanDeviceRecord? _selectedLanDevice;
    private string _selectedApplicationUserAccessText = "Select an application user to review Client/FY access.";
    private string _undoReason = "Undo incorrect import batch";

    public FinanceControlCenterViewModel(
        ActiveClientContext clientContext,
        SqliteFinanceControlRepository repository,
        BackgroundAutomationService automationService,
        AccountingLiveChangeHub liveChangeHub,
        ApplicationPreferenceService settings)
        : base("Finance Center", "Close, control, forecast, search, audit and accounting intelligence")
    {
        _clientContext = clientContext;
        _repository = repository;
        _automationService = automationService;
        _liveChangeHub = liveChangeHub;
        _settings = settings;
        _backupService = new ApplicationBackupService();
        _repository.Initialize();
        _governanceRepository = new SqliteGovernanceRepository();
        _governanceRepository.Initialize();
        _serverSecurityRepository = new ServerSecurityRepository();
        _serverSecurityRepository.Initialize();
        _undoService = new ImportUndoService(_governanceRepository);
        _verifiedBackupService = new VerifiedBackupService(_governanceRepository);
        _verifiedBackupService.Start();

        RefreshCommand = new RelayCommand(RefreshAll, HasClient);
        SearchCommand = new RelayCommand(RunSearch, () => HasClient() && SearchText.Trim().Length >= 2);
        ClearSearchCommand = new RelayCommand(() => { SearchText = string.Empty; SearchResults.Clear(); });
        SavePeriodLockCommand = new RelayCommand(SavePeriodLock, HasClient);
        UnlockPeriodCommand = new RelayCommand(UnlockSelectedPeriod, () => HasClient() && SelectedPeriodLock is not null);
        SaveSettlementCommand = new RelayCommand(SaveSettlement, () => HasClient() && SelectedAging is not null && SettlementAmount > 0);

        NewAssetCommand = new RelayCommand(NewAsset, HasClient);
        EditAssetCommand = new RelayCommand(EditSelectedAsset, () => SelectedAsset is not null);
        SaveAssetCommand = new RelayCommand(SaveAsset, HasClient);
        DeleteAssetCommand = new RelayCommand(DeleteAsset, () => HasClient() && SelectedAsset is not null);

        NewBankRuleCommand = new RelayCommand(NewBankRule, HasClient);
        EditBankRuleCommand = new RelayCommand(EditSelectedBankRule, () => SelectedBankRule is not null);
        PreviewBankRuleCommand = new RelayCommand(PreviewBankRule, HasClient);
        SaveBankRuleCommand = new RelayCommand(SaveBankRule, HasClient);
        DeleteBankRuleCommand = new RelayCommand(DeleteBankRule, () => HasClient() && SelectedBankRule is not null);
        ApplyBankRulesCommand = new RelayCommand(ApplyBankRules, HasClient);

        NewBudgetCommand = new RelayCommand(NewBudget, HasClient);
        EditBudgetCommand = new RelayCommand(EditSelectedBudget, () => SelectedBudget is not null);
        SaveBudgetCommand = new RelayCommand(SaveBudget, HasClient);
        DeleteBudgetCommand = new RelayCommand(DeleteBudget, () => HasClient() && SelectedBudget is not null);

        NewRecurringCommand = new RelayCommand(NewRecurring, HasClient);
        EditRecurringCommand = new RelayCommand(EditSelectedRecurring, () => SelectedRecurring is not null);
        SaveRecurringCommand = new RelayCommand(SaveRecurring, HasClient);
        DeleteRecurringCommand = new RelayCommand(DeleteRecurring, () => HasClient() && SelectedRecurring is not null);

        NewApprovalCommand = new RelayCommand(NewApproval, HasClient);
        EditApprovalCommand = new RelayCommand(EditSelectedApproval, () => SelectedApproval is not null);
        SaveApprovalCommand = new RelayCommand(SaveApproval, HasClient);
        ApproveCommand = new RelayCommand(() => DecideApproval("Approved"), () => HasClient() && SelectedApproval is not null);
        RejectCommand = new RelayCommand(() => DecideApproval("Rejected"), () => HasClient() && SelectedApproval is not null);
        DeleteApprovalCommand = new RelayCommand(DeleteApproval, () => HasClient() && SelectedApproval is not null);

        NewInventoryPolicyCommand = new RelayCommand(NewInventoryPolicy, HasClient);
        EditInventoryPolicyCommand = new RelayCommand(EditSelectedInventoryPolicy, () => SelectedInventoryPolicy is not null);
        SaveInventoryPolicyCommand = new RelayCommand(SaveInventoryPolicy, HasClient);
        DeleteInventoryPolicyCommand = new RelayCommand(DeleteInventoryPolicy, () => HasClient() && SelectedInventoryPolicy is not null);

        CreateBackupCommand = new RelayCommand(CreateBackup);
        RestoreBackupCommand = new RelayCommand(StageRestore);
        RecoveryScanCommand = new RelayCommand(RecoveryScan);
        ExportControlPackCommand = new RelayCommand(ExportControlPack, HasClient);

        PreviewUndoCommand = new RelayCommand(PreviewSelectedImportRun, () => HasClient() && SelectedImportRun is not null && SelectedImportRun.IsUndoable);
        UndoImportRunCommand = new RelayCommand(UndoSelectedImportRun, () => HasClient() && SelectedImportRun is not null && SelectedImportRun.IsUndoable);
        OpenVaultDocumentCommand = new RelayCommand(OpenSelectedVaultDocument, () => SelectedVaultDocument?.IsAvailable == true);
        NewApplicationUserCommand = new RelayCommand(NewApplicationUser);
        EditApplicationUserCommand = new RelayCommand(EditSelectedApplicationUser, () => SelectedApplicationUser is not null);
        SaveApplicationUserCommand = new RelayCommand(SaveApplicationUser);
        ConfigureNetworkMfaCommand = new RelayCommand(ConfigureNetworkMfa, () => SelectedApplicationUser is not null);
        AllowSelectedUserCurrentClientCommand = new RelayCommand(AllowSelectedUserCurrentClient, () => HasClient() && SelectedApplicationUser is not null);
        ClearSelectedUserClientAccessCommand = new RelayCommand(ClearSelectedUserClientAccess, () => SelectedApplicationUser is not null);
        ToggleLanDeviceActiveCommand = new RelayCommand(ToggleLanDeviceActive, () => SelectedLanDevice is not null);
        CreateVerifiedBackupCommand = new RelayCommand(CreateVerifiedBackup);
        OpenVerifiedBackupFolderCommand = new RelayCommand(OpenVerifiedBackupFolder);

        LockModuleOptions = new ObservableCollection<string>(new[] { "All", "Banking", "Cash Entry", "Purchase", "Sales", "Journal" });
        DepreciationMethods = new ObservableCollection<string>(new[] { "SLM", "WDV" });
        ApprovalStatuses = new ObservableCollection<string>(new[] { "Pending", "Approved", "Rejected" });
        CashDirections = new ObservableCollection<string>(new[] { "Inflow", "Outflow" });
        ApplicationRoleOptions = new ObservableCollection<string>(GovernanceRoles.All);

        _preparedFinance = new PersistentProjectionCache<FinancePreparedSnapshot>(
            System.IO.Path.Combine(AccountingAutomationHub.Infrastructure.Persistence.ApplicationDataPaths.RootFolder,
                "Cache", "FinanceProjectionsV1"), WorkspaceCacheRevisionStore.AccountingSourceStamp, workerCount: 1, maxResidentEntries: 3);
        _preparedFinance.Ready += OnPreparedFinanceReady;
        _preparedFinance.Failed += OnPreparedFinanceFailed;
        _financeSourceMonitor = new WorkspaceSourceChangeMonitor(
            WorkspaceCacheRevisionStore.AccountingSourceStamp, RevalidateAllPreparedScopes);
        _clientContext.PropertyChanged += OnClientContextChanged;
        _liveChangeHub.Changed += OnLiveChange;
    }

    public ObservableCollection<FinanceKpiRow> Kpis { get; } = [];
    public ObservableCollection<MonthEndCloseCheck> CloseChecks { get; } = [];
    public ObservableCollection<FinanceIntegrityCheckRow> IntegrityChecks { get; } = [];
    public ObservableCollection<AccountingPeriodLock> PeriodLocks { get; } = [];
    public ObservableCollection<FinanceAuditEvent> AuditTrail { get; } = [];
    public ObservableCollection<FinanceCashFlowRow> CashFlow { get; } = [];
    public ObservableCollection<FinanceAgingRow> Aging { get; } = [];
    public ObservableCollection<FixedAssetRecord> FixedAssets { get; } = [];
    public ObservableCollection<BankRuleRecord> BankRules { get; } = [];
    public ObservableCollection<BankRulePreviewRow> BankRulePreview { get; } = [];
    public ObservableCollection<FinanceBudgetRecord> Budgets { get; } = [];
    public ObservableCollection<RecurringCashFlowTemplate> RecurringItems { get; } = [];
    public ObservableCollection<FinanceApprovalItem> Approvals { get; } = [];
    public ObservableCollection<InventoryPolicyRecord> InventoryPolicies { get; } = [];
    public ObservableCollection<InventoryIntelligenceRow> InventoryIntelligence { get; } = [];
    public ObservableCollection<DocumentInboxRow> DocumentInbox { get; } = [];
    public ObservableCollection<FinanceSearchResult> SearchResults { get; } = [];
    public ObservableCollection<ImportRunRecord> ImportRuns { get; } = [];
    public ObservableCollection<ImportJobRecord> ImportJobs { get; } = [];
    public ObservableCollection<DocumentVaultRecord> VaultDocuments { get; } = [];
    public ObservableCollection<EntityVersionRecord> VersionHistory { get; } = [];
    public ObservableCollection<ApplicationUserRecord> ApplicationUsers { get; } = [];
    public ObservableCollection<LanDeviceRecord> LanDevices { get; } = [];
    public ObservableCollection<VerifiedBackupRecord> VerifiedBackups { get; } = [];
    public ObservableCollection<RecurringTransactionProfile> TransactionAutomationProfiles { get; } = [];
    public ObservableCollection<AdvancedReconciliationPreview> AdvancedReconciliations { get; } = [];
    public ObservableCollection<string> LockModuleOptions { get; }
    public ObservableCollection<string> DepreciationMethods { get; }
    public ObservableCollection<string> ApprovalStatuses { get; }
    public ObservableCollection<string> CashDirections { get; }
    public ObservableCollection<string> ApplicationRoleOptions { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SearchCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand SavePeriodLockCommand { get; }
    public RelayCommand UnlockPeriodCommand { get; }
    public RelayCommand SaveSettlementCommand { get; }
    public RelayCommand NewAssetCommand { get; }
    public RelayCommand EditAssetCommand { get; }
    public RelayCommand SaveAssetCommand { get; }
    public RelayCommand DeleteAssetCommand { get; }
    public RelayCommand NewBankRuleCommand { get; }
    public RelayCommand EditBankRuleCommand { get; }
    public RelayCommand PreviewBankRuleCommand { get; }
    public RelayCommand SaveBankRuleCommand { get; }
    public RelayCommand DeleteBankRuleCommand { get; }
    public RelayCommand ApplyBankRulesCommand { get; }
    public RelayCommand NewBudgetCommand { get; }
    public RelayCommand EditBudgetCommand { get; }
    public RelayCommand SaveBudgetCommand { get; }
    public RelayCommand DeleteBudgetCommand { get; }
    public RelayCommand NewRecurringCommand { get; }
    public RelayCommand EditRecurringCommand { get; }
    public RelayCommand SaveRecurringCommand { get; }
    public RelayCommand DeleteRecurringCommand { get; }
    public RelayCommand NewApprovalCommand { get; }
    public RelayCommand EditApprovalCommand { get; }
    public RelayCommand SaveApprovalCommand { get; }
    public RelayCommand ApproveCommand { get; }
    public RelayCommand RejectCommand { get; }
    public RelayCommand DeleteApprovalCommand { get; }
    public RelayCommand NewInventoryPolicyCommand { get; }
    public RelayCommand EditInventoryPolicyCommand { get; }
    public RelayCommand SaveInventoryPolicyCommand { get; }
    public RelayCommand DeleteInventoryPolicyCommand { get; }
    public RelayCommand CreateBackupCommand { get; }
    public RelayCommand RestoreBackupCommand { get; }
    public RelayCommand RecoveryScanCommand { get; }
    public RelayCommand ExportControlPackCommand { get; }
    public RelayCommand PreviewUndoCommand { get; }
    public RelayCommand UndoImportRunCommand { get; }
    public RelayCommand OpenVaultDocumentCommand { get; }
    public RelayCommand NewApplicationUserCommand { get; }
    public RelayCommand EditApplicationUserCommand { get; }
    public RelayCommand SaveApplicationUserCommand { get; }
    public RelayCommand ConfigureNetworkMfaCommand { get; }
    public RelayCommand AllowSelectedUserCurrentClientCommand { get; }
    public RelayCommand ClearSelectedUserClientAccessCommand { get; }
    public RelayCommand ToggleLanDeviceActiveCommand { get; }
    public RelayCommand CreateVerifiedBackupCommand { get; }
    public RelayCommand OpenVerifiedBackupFolderCommand { get; }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public DateTime CloseThroughDate { get => _closeThroughDate; set { if (SetProperty(ref _closeThroughDate, value) && _presentationActive) RefreshAll(force: false); } }
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) SearchCommand.RaiseCanExecuteChanged(); } }
    public string LockModule { get => _lockModule; set => SetProperty(ref _lockModule, value); }
    public DateTime LockDate { get => _lockDate; set => SetProperty(ref _lockDate, value); }
    public string LockReason { get => _lockReason; set => SetProperty(ref _lockReason, value); }
    public AccountingPeriodLock? SelectedPeriodLock { get => _selectedPeriodLock; set { if (SetProperty(ref _selectedPeriodLock, value)) UnlockPeriodCommand.RaiseCanExecuteChanged(); } }
    public FinanceAgingRow? SelectedAging { get => _selectedAging; set { if (SetProperty(ref _selectedAging, value)) { SettlementAmount = value?.OutstandingAmount ?? 0m; SaveSettlementCommand.RaiseCanExecuteChanged(); } } }
    public decimal SettlementAmount { get => _settlementAmount; set { if (SetProperty(ref _settlementAmount, value)) SaveSettlementCommand.RaiseCanExecuteChanged(); } }
    public DateTime SettlementDate { get => _settlementDate; set => SetProperty(ref _settlementDate, value); }
    public string SettlementReference { get => _settlementReference; set => SetProperty(ref _settlementReference, value); }
    public FixedAssetRecord EditingAsset { get => _editingAsset; set => SetProperty(ref _editingAsset, value); }
    public FixedAssetRecord? SelectedAsset { get => _selectedAsset; set { if (SetProperty(ref _selectedAsset, value)) { EditAssetCommand.RaiseCanExecuteChanged(); DeleteAssetCommand.RaiseCanExecuteChanged(); } } }
    public BankRuleRecord EditingBankRule { get => _editingBankRule; set => SetProperty(ref _editingBankRule, value); }
    public BankRuleRecord? SelectedBankRule { get => _selectedBankRule; set { if (SetProperty(ref _selectedBankRule, value)) { EditBankRuleCommand.RaiseCanExecuteChanged(); DeleteBankRuleCommand.RaiseCanExecuteChanged(); } } }
    public FinanceBudgetRecord EditingBudget { get => _editingBudget; set => SetProperty(ref _editingBudget, value); }
    public FinanceBudgetRecord? SelectedBudget { get => _selectedBudget; set { if (SetProperty(ref _selectedBudget, value)) { EditBudgetCommand.RaiseCanExecuteChanged(); DeleteBudgetCommand.RaiseCanExecuteChanged(); } } }
    public RecurringCashFlowTemplate EditingRecurring { get => _editingRecurring; set => SetProperty(ref _editingRecurring, value); }
    public RecurringCashFlowTemplate? SelectedRecurring { get => _selectedRecurring; set { if (SetProperty(ref _selectedRecurring, value)) { EditRecurringCommand.RaiseCanExecuteChanged(); DeleteRecurringCommand.RaiseCanExecuteChanged(); } } }
    public FinanceApprovalItem EditingApproval { get => _editingApproval; set => SetProperty(ref _editingApproval, value); }
    public FinanceApprovalItem? SelectedApproval { get => _selectedApproval; set { if (SetProperty(ref _selectedApproval, value)) { EditApprovalCommand.RaiseCanExecuteChanged(); ApproveCommand.RaiseCanExecuteChanged(); RejectCommand.RaiseCanExecuteChanged(); DeleteApprovalCommand.RaiseCanExecuteChanged(); } } }
    public InventoryPolicyRecord EditingInventoryPolicy { get => _editingInventoryPolicy; set => SetProperty(ref _editingInventoryPolicy, value); }
    public InventoryPolicyRecord? SelectedInventoryPolicy { get => _selectedInventoryPolicy; set { if (SetProperty(ref _selectedInventoryPolicy, value)) { EditInventoryPolicyCommand.RaiseCanExecuteChanged(); DeleteInventoryPolicyCommand.RaiseCanExecuteChanged(); } } }
    public ImportRunRecord? SelectedImportRun { get => _selectedImportRun; set { if (SetProperty(ref _selectedImportRun, value)) { PreviewUndoCommand.RaiseCanExecuteChanged(); UndoImportRunCommand.RaiseCanExecuteChanged(); } } }
    public ImportJobRecord? SelectedImportJob { get => _selectedImportJob; set => SetProperty(ref _selectedImportJob, value); }
    public DocumentVaultRecord? SelectedVaultDocument { get => _selectedVaultDocument; set { if (SetProperty(ref _selectedVaultDocument, value)) OpenVaultDocumentCommand.RaiseCanExecuteChanged(); } }
    public EntityVersionRecord? SelectedVersion { get => _selectedVersion; set => SetProperty(ref _selectedVersion, value); }
    public ApplicationUserRecord EditingApplicationUser { get => _editingApplicationUser; set => SetProperty(ref _editingApplicationUser, value); }
    public ApplicationUserRecord? SelectedApplicationUser
    {
        get => _selectedApplicationUser;
        set
        {
            if (!SetProperty(ref _selectedApplicationUser, value)) return;
            EditApplicationUserCommand.RaiseCanExecuteChanged();
            ConfigureNetworkMfaCommand.RaiseCanExecuteChanged();
            AllowSelectedUserCurrentClientCommand.RaiseCanExecuteChanged();
            ClearSelectedUserClientAccessCommand.RaiseCanExecuteChanged();
            UpdateSelectedApplicationUserAccessText();
        }
    }
    public VerifiedBackupRecord? SelectedVerifiedBackup { get => _selectedVerifiedBackup; set => SetProperty(ref _selectedVerifiedBackup, value); }
    public LanDeviceRecord? SelectedLanDevice
    {
        get => _selectedLanDevice;
        set
        {
            if (!SetProperty(ref _selectedLanDevice, value)) return;
            ToggleLanDeviceActiveCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(ToggleLanDeviceActiveText));
        }
    }
    public string ToggleLanDeviceActiveText => SelectedLanDevice?.IsActive == true ? "Revoke Device" : "Re-enrollment Required";
    public string UndoReason { get => _undoReason; set => SetProperty(ref _undoReason, value); }
    public string CurrentApplicationUser => $"{_governanceRepository.GetCurrentUser().DisplayName} · {_governanceRepository.GetCurrentUser().RoleName}";
    public string SelectedApplicationUserAccessText { get => _selectedApplicationUserAccessText; private set => SetProperty(ref _selectedApplicationUserAccessText, value); }

    private bool HasClient() => _clientContext.CurrentClient is not null;

    private static string FinanceCacheKey(long id, string fy, DateTime through) =>
        $"{id}\u001f{fy.Trim().ToUpperInvariant()}\u001f{through:yyyyMMdd}";

    public void StartBackgroundPreparation(IEnumerable<AccountingAutomationHub.Domain.Clients.ClientProfile> clients)
    {
        if (_disposed || Interlocked.Exchange(ref _backgroundPreparationStarted, 1) != 0) return;
        // R44: Finance is a page projection, not the Client/FY accounting snapshot authority.
        // Do not build its large audit/vault/import-history payload for every client at startup.
        // The active Client/FY is prepared on demand by RefreshAll/PrepareForDisplay.
        var current = _clientContext.CurrentClient;
        if (current is not null && !current.IsArchived && current.Id > 0)
            RegisterFinance(current.Id, current.FinancialYear, DateTime.Today);
    }

    private void RegisterFinance(long id, string fy, DateTime through, bool invalidate = false, bool priority = false)
    {
        var date = through.Date;
        var key = FinanceCacheKey(id, fy, date);
        _preparedScopes[key] = (id, fy, date);
        _preparedFinance.Register(key, () => BuildFinancePreparedSnapshot(id, fy, date), invalidate, priority);
    }

    private void RevalidateAllPreparedScopes()
    {
        if (_disposed) return;
        foreach (var scope in _preparedScopes.Values)
            RegisterFinance(scope.ClientId, scope.FinancialYear, scope.Through, invalidate: true);
    }

    private void InvalidateFinanceClient(long id, string fy)
    {
        if (_disposed) return;
        foreach (var scope in _preparedScopes.Values.Where(scope => scope.ClientId == id &&
                     scope.FinancialYear.Equals(fy, StringComparison.OrdinalIgnoreCase)))
            RegisterFinance(scope.ClientId, scope.FinancialYear, scope.Through, invalidate: true, priority: true);
    }

    private FinancePreparedSnapshot BuildFinancePreparedSnapshot(long id, string fy, DateTime through) => new()
    {
        ClientId = id,
        FinancialYear = fy,
        CloseThroughDate = through,
        Kpis = _repository.GetKpis(id, fy).ToArray(),
        CloseChecks = _repository.GetCloseChecks(id, fy, through).ToArray(),
        IntegrityChecks = _repository.GetIntegrityChecks(id, fy).ToArray(),
        PeriodLocks = _repository.GetPeriodLocks(id, fy).ToArray(),
        AuditTrail = _repository.GetAudit(id, fy, 1000).ToArray(),
        CashFlow = _repository.GetCashFlow(id, fy, 6).ToArray(),
        Aging = _repository.GetAging(id, fy).ToArray(),
        FixedAssets = _repository.GetFixedAssets(id, fy).ToArray(),
        BankRules = _repository.GetBankRules(id, fy).ToArray(),
        Budgets = _repository.GetBudgets(id, fy).ToArray(),
        RecurringItems = _repository.GetRecurringTemplates(id, fy).ToArray(),
        Approvals = _repository.GetApprovals(id, fy).ToArray(),
        InventoryPolicies = _repository.GetInventoryPolicies(id, fy).ToArray(),
        InventoryIntelligence = _repository.GetInventoryIntelligence(id, fy).ToArray(),
        DocumentInbox = _repository.GetDocumentInbox(id, fy, 1000).ToArray(),
        ImportRuns = _governanceRepository.GetRuns(id, fy, 500).ToArray(),
        ImportJobs = _governanceRepository.GetJobs(id, fy, 5000).ToArray(),
        VaultDocuments = _governanceRepository.GetDocuments(id, fy, 5000).ToArray(),
        VersionHistory = _governanceRepository.GetVersions(id, fy, 5000).ToArray(),
        TransactionAutomationProfiles = _governanceRepository.GetRecurringProfiles(id, fy, "Cash").Concat(_governanceRepository.GetRecurringProfiles(id, fy, "Journal")).OrderBy(x => x.NextRunDate).ThenBy(x => x.Module).ToArray(),
        AdvancedReconciliations = _governanceRepository.GetAdvancedReconciliations(id, fy, 500).ToArray(),
    };

    private void OnPreparedFinanceReady(string key, long revision)
    {
        if (_disposed || !_preparedFinance.IsCurrent(key, revision)) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || !_presentationActive || !_preparedFinance.IsCurrent(key, revision)) return;
            var client = _clientContext.CurrentClient;
            if (client is null || !key.Equals(FinanceCacheKey(client.Id, client.FinancialYear, CloseThroughDate), StringComparison.OrdinalIgnoreCase)) return;
            if (_preparedFinance.TryGet(key, out var snapshot)) ApplyFinancePreparedSnapshot(snapshot);
        }));
    }

    private void OnPreparedFinanceFailed(string key, string message)
    {
        if (_disposed || !_presentationActive) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            var client = _clientContext.CurrentClient;
            if (client is not null && key.Equals(FinanceCacheKey(client.Id, client.FinancialYear, CloseThroughDate), StringComparison.OrdinalIgnoreCase))
                StatusText = "Finance preparation failed; last saved view retained. " + message;
        }));
    }

    public void SetPresentationActive(bool active)
    {
        _presentationActive = active;
        if (active) RefreshAll(force: false);
    }

    public void PrepareForDisplay() => SetPresentationActive(true);
    public void CheckpointPresentationClose()
    {
        var client = _clientContext.CurrentClient;
        if (client is null || _disposed) return;
        // Re-registering without invalidation is cheap when current and forces a rebuild only
        // when a committed/external source change made the persisted Finance projection stale.
        RegisterFinance(client.Id, client.FinancialYear, CloseThroughDate, invalidate: false, priority: true);
    }


    // Keep the original parameterless command contract. Optional parameters do not
    // make a one-argument method compatible with RelayCommand's Action delegate.
    private void RefreshAll() => RefreshAll(force: true);

    private void RefreshAll(bool force)
    {
        if (_disposed) return;
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            if (_presentationActive) ClearAll();
            StatusText = "Open a client to use Finance Center.";
            RaiseCommandStates();
            return;
        }
        var key = FinanceCacheKey(client.Id, client.FinancialYear, CloseThroughDate);
        if (_presentationActive && _preparedFinance.TryGet(key, out var snapshot))
            ApplyFinancePreparedSnapshot(snapshot);
        RegisterFinance(client.Id, client.FinancialYear, CloseThroughDate, invalidate: force, priority: true);
        if (!_preparedFinance.IsAuthoritative(key))
            StatusText = "Finance Center is preparing the latest committed data in the background.";
    }

    private void ApplyFinancePreparedSnapshot(FinancePreparedSnapshot snapshot)
    {
        var client = _clientContext.CurrentClient;
        if (_disposed || !_presentationActive || _refreshing || client is null || client.Id != snapshot.ClientId ||
            !client.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase) ||
            CloseThroughDate.Date != snapshot.CloseThroughDate.Date) return;
        _refreshing = true;
        try
        {
            Replace(Kpis, snapshot.Kpis);
            Replace(CloseChecks, snapshot.CloseChecks);
            Replace(IntegrityChecks, snapshot.IntegrityChecks);
            Replace(PeriodLocks, snapshot.PeriodLocks);
            Replace(AuditTrail, snapshot.AuditTrail);
            Replace(CashFlow, snapshot.CashFlow);
            Replace(Aging, snapshot.Aging);
            Replace(FixedAssets, snapshot.FixedAssets);
            Replace(BankRules, snapshot.BankRules);
            Replace(Budgets, snapshot.Budgets);
            Replace(RecurringItems, snapshot.RecurringItems);
            Replace(Approvals, snapshot.Approvals);
            Replace(InventoryPolicies, snapshot.InventoryPolicies);
            Replace(InventoryIntelligence, snapshot.InventoryIntelligence);
            Replace(DocumentInbox, snapshot.DocumentInbox);
            Replace(ImportRuns, snapshot.ImportRuns);
            Replace(ImportJobs, snapshot.ImportJobs);
            Replace(VaultDocuments, snapshot.VaultDocuments);
            Replace(VersionHistory, snapshot.VersionHistory);
            Replace(TransactionAutomationProfiles, snapshot.TransactionAutomationProfiles);
            Replace(AdvancedReconciliations, snapshot.AdvancedReconciliations);
            // Security/identity controls are read only for the active authenticated user.
            // Their device token hashes and privileged state are never persisted in this cache.
            Replace(ApplicationUsers, _governanceRepository.GetUsers());
            Replace(LanDevices, _governanceRepository.GetLanDevices());
            Replace(VerifiedBackups, _governanceRepository.GetBackups(250));
            OnPropertyChanged(nameof(CurrentApplicationUser));
            UpdateSelectedApplicationUserAccessText();
            StatusText = $"Finance Center ready for {client.ClientName} • FY {client.FinancialYear}.";
        }
        catch (Exception ex) { StatusText = "Finance presentation failed: " + ex.Message; }
        finally { _refreshing = false; RaiseCommandStates(); }
    }

    private void RunSearch()
    {
        var client = _clientContext.CurrentClient; if (client is null) return;
        try { Replace(SearchResults, _repository.Search(client.Id, client.FinancialYear, SearchText)); StatusText = $"Search returned {SearchResults.Count:N0} result(s)."; }
        catch (Exception ex) { StatusText = $"Search failed: {ex.Message}"; }
    }

    private void SavePeriodLock()
    {
        var client = _clientContext.CurrentClient; if (client is null) return;
        if (string.IsNullOrWhiteSpace(LockReason)) { StatusText = "Enter a reason before locking a period."; return; }
        var item = new AccountingPeriodLock { ClientId=client.Id,FinancialYear=client.FinancialYear,Module=LockModule,LockDate=LockDate.Date,Reason=AccountingText.CleanWhitespace(LockReason),IsActive=true };
        try
        {
            _repository.SavePeriodLock(item);
            _repository.RecordAudit(client.Id,client.FinancialYear,"Finance Center","Period Lock",LockModule,"Lock","",$"Through {LockDate:dd-MMM-yyyy} | {item.Reason}");
            StatusText = $"{LockModule} locked through {LockDate:dd-MMM-yyyy}.";
            RefreshAll();
        }
        catch(Exception ex){StatusText=$"Period lock failed: {ex.Message}";}
    }

    private void UnlockSelectedPeriod()
    {
        var client=_clientContext.CurrentClient;var item=SelectedPeriodLock;if(client is null||item is null)return;
        if (!_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.UnlockPeriod))
        {
            StatusText = "Your role does not have permission to unlock accounting periods.";
            return;
        }
        if(MessageBox.Show($"Unlock {item.Module} through {item.LockDate:dd-MMM-yyyy}?\n\nA reason will be retained in the audit trail.","Unlock Accounting Period",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        try{var reason=string.IsNullOrWhiteSpace(LockReason)?"Manual unlock":AccountingText.CleanWhitespace(LockReason);_repository.UnlockPeriod(client.Id,client.FinancialYear,item.Module,reason);_repository.RecordAudit(client.Id,client.FinancialYear,"Finance Center","Period Lock",item.Module,"Unlock",$"Through {item.LockDate:dd-MMM-yyyy}",reason);StatusText=$"{item.Module} unlocked.";RefreshAll();}catch(Exception ex){StatusText=$"Unlock failed: {ex.Message}";}
    }

    private void SaveSettlement()
    {
        var client=_clientContext.CurrentClient;var row=SelectedAging;if(client is null||row is null)return;
        if(SettlementAmount<=0||SettlementAmount>row.OutstandingAmount+0.005m){StatusText="Settlement amount must be positive and cannot exceed the outstanding amount.";return;}
        try
        {
            _repository.SaveSettlement(new FinanceSettlement{ClientId=client.Id,FinancialYear=client.FinancialYear,Side=row.Side,Party=row.Party,DocumentNumber=row.DocumentNumber,DocumentDate=row.DocumentDate,SettledAmount=SettlementAmount,SettlementDate=SettlementDate.Date,Reference=AccountingText.CleanWhitespace(SettlementReference)});
            _repository.RecordAudit(client.Id,client.FinancialYear,"Finance Center","Settlement",$"{row.Side}:{row.Party}:{row.DocumentNumber}","Settle","",SettlementAmount.ToString("0.00",CultureInfo.InvariantCulture));
            StatusText=$"Settlement recorded for {AccountingText.DisplayName(row.Party)}.";SettlementReference=string.Empty;RefreshAll();
        }
        catch(Exception ex){StatusText=$"Settlement failed: {ex.Message}";}
    }

    private void NewAsset(){var c=_clientContext.CurrentClient;if(c is null)return;EditingAsset=new FixedAssetRecord{ClientId=c.Id,FinancialYear=c.FinancialYear,AssetGroup="Fixed Assets",PurchaseDate=DateTime.Today,PutToUseDate=DateTime.Today,DepreciationMethod="SLM"};}
    private void EditSelectedAsset(){if(SelectedAsset is null)return;EditingAsset=CloneAsset(SelectedAsset);}
    private void SaveAsset(){var c=_clientContext.CurrentClient;if(c is null)return;try{EditingAsset.ClientId=c.Id;EditingAsset.FinancialYear=c.FinancialYear;EditingAsset.AssetName=AccountingText.DisplayName(EditingAsset.AssetName);EditingAsset.AssetGroup=AccountingText.DisplayName(EditingAsset.AssetGroup);EditingAsset.Location=AccountingText.DisplayName(EditingAsset.Location);_repository.SaveFixedAsset(EditingAsset);_repository.RecordAudit(c.Id,c.FinancialYear,"Finance Center","Fixed Asset",EditingAsset.AssetCode.Length>0?EditingAsset.AssetCode:EditingAsset.AssetName,"Save","",EditingAsset.AssetName);StatusText=$"Fixed asset '{EditingAsset.AssetName}' saved.";RefreshAll();NewAsset();}catch(Exception ex){StatusText=$"Fixed asset save failed: {ex.Message}";}}
    private void DeleteAsset(){var c=_clientContext.CurrentClient;var row=SelectedAsset;if(c is null||row is null)return;if(MessageBox.Show($"Delete fixed asset '{row.AssetName}' from the register?","Delete Fixed Asset",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;try{_repository.DeleteFixedAsset(row.Id,c.Id,c.FinancialYear);_repository.RecordAudit(c.Id,c.FinancialYear,"Finance Center","Fixed Asset",row.AssetCode.Length>0?row.AssetCode:row.AssetName,"Delete",row.AssetName,"");RefreshAll();}catch(Exception ex){StatusText=$"Delete failed: {ex.Message}";}}

    private void NewBankRule(){var c=_clientContext.CurrentClient;if(c is null)return;EditingBankRule=new BankRuleRecord{ClientId=c.Id,FinancialYear=c.FinancialYear,Priority=100,IsActive=true};BankRulePreview.Clear();}
    private void EditSelectedBankRule(){if(SelectedBankRule is null)return;EditingBankRule=CloneRule(SelectedBankRule);BankRulePreview.Clear();}
    private void PreviewBankRule(){var c=_clientContext.CurrentClient;if(c is null)return;try{EditingBankRule.ClientId=c.Id;EditingBankRule.FinancialYear=c.FinancialYear;EditingBankRule.LedgerName=AccountingText.DisplayName(EditingBankRule.LedgerName);Replace(BankRulePreview,_repository.PreviewBankRule(EditingBankRule));StatusText=$"Rule preview: {BankRulePreview.Count:N0} transaction(s) match. No accounting data was changed.";}catch(Exception ex){StatusText=$"Rule preview failed: {ex.Message}";}}
    private void SaveBankRule(){var c=_clientContext.CurrentClient;if(c is null)return;try{EditingBankRule.ClientId=c.Id;EditingBankRule.FinancialYear=c.FinancialYear;EditingBankRule.RuleName=AccountingText.DisplayName(EditingBankRule.RuleName);EditingBankRule.LedgerName=AccountingText.DisplayName(EditingBankRule.LedgerName);EditingBankRule.LedgerGroup=AccountingText.DisplayName(EditingBankRule.LedgerGroup);_repository.SaveBankRule(EditingBankRule);_repository.RecordAudit(c.Id,c.FinancialYear,"Finance Center","Bank Rule",EditingBankRule.RuleName,"Save","",EditingBankRule.LedgerName);StatusText=$"Bank rule '{EditingBankRule.RuleName}' saved.";RefreshAll();}catch(Exception ex){StatusText=$"Bank rule save failed: {ex.Message}";}}
    private void DeleteBankRule(){var c=_clientContext.CurrentClient;var row=SelectedBankRule;if(c is null||row is null)return;if(MessageBox.Show($"Delete bank rule '{row.RuleName}'?","Delete Bank Rule",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;try{_repository.DeleteBankRule(row.Id,c.Id,c.FinancialYear);_repository.RecordAudit(c.Id,c.FinancialYear,"Finance Center","Bank Rule",row.RuleName,"Delete",row.LedgerName,"");RefreshAll();}catch(Exception ex){StatusText=$"Delete failed: {ex.Message}";}}
    private void ApplyBankRules(){var c=_clientContext.CurrentClient;if(c is null)return;if(MessageBox.Show("Apply active bank rules to currently unmapped Bank transactions?\n\nExisting mapped ledgers will not be overwritten.","Apply Bank Rules",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;try{var count=_repository.ApplyBankRules(c.Id,c.FinancialYear,true);_repository.RecordAudit(c.Id,c.FinancialYear,"Finance Center","Bank Rules","Batch","Apply","",$"{count} transaction(s)");_liveChangeHub.Publish(c.Id,c.FinancialYear,AccountingLiveArea.Banking,"Finance Center bank rules applied");StatusText=$"Bank rules applied to {count:N0} unmapped transaction(s).";RefreshAll();}catch(Exception ex){StatusText=$"Applying bank rules failed: {ex.Message}";}}

    private void NewBudget(){var c=_clientContext.CurrentClient;if(c is null)return;EditingBudget=new FinanceBudgetRecord{ClientId=c.Id,FinancialYear=c.FinancialYear,Period=DateTime.Today.ToString("yyyy-MM",CultureInfo.InvariantCulture),Category="Sales"};}
    private void EditSelectedBudget(){if(SelectedBudget is null)return;EditingBudget=new FinanceBudgetRecord{Id=SelectedBudget.Id,ClientId=SelectedBudget.ClientId,FinancialYear=SelectedBudget.FinancialYear,Period=SelectedBudget.Period,Category=SelectedBudget.Category,BudgetAmount=SelectedBudget.BudgetAmount,ActualAmount=SelectedBudget.ActualAmount,UpdatedUtc=SelectedBudget.UpdatedUtc};}
    private void SaveBudget(){var c=_clientContext.CurrentClient;if(c is null)return;try{EditingBudget.ClientId=c.Id;EditingBudget.FinancialYear=c.FinancialYear;EditingBudget.Category=AccountingText.DisplayName(EditingBudget.Category);_repository.SaveBudget(EditingBudget);_repository.RecordAudit(c.Id,c.FinancialYear,"Finance Center","Budget",$"{EditingBudget.Period}:{EditingBudget.Category}","Save","",EditingBudget.BudgetAmount.ToString("0.00",CultureInfo.InvariantCulture));StatusText="Budget saved.";RefreshAll();}catch(Exception ex){StatusText=$"Budget save failed: {ex.Message}";}}
    private void DeleteBudget(){var c=_clientContext.CurrentClient;var row=SelectedBudget;if(c is null||row is null)return;try{_repository.DeleteBudget(row.Id,c.Id,c.FinancialYear);_repository.RecordAudit(c.Id,c.FinancialYear,"Finance Center","Budget",$"{row.Period}:{row.Category}","Delete",row.BudgetAmount.ToString(CultureInfo.InvariantCulture),"");RefreshAll();}catch(Exception ex){StatusText=$"Delete failed: {ex.Message}";}}

    private void NewRecurring(){var c=_clientContext.CurrentClient;if(c is null)return;EditingRecurring=new RecurringCashFlowTemplate{ClientId=c.Id,FinancialYear=c.FinancialYear,Direction="Outflow",StartDate=DateTime.Today,DayOfMonth=Math.Min(DateTime.Today.Day,28),IsActive=true};}
    private void EditSelectedRecurring(){if(SelectedRecurring is null)return;var r=SelectedRecurring;EditingRecurring=new RecurringCashFlowTemplate{Id=r.Id,ClientId=r.ClientId,FinancialYear=r.FinancialYear,Name=r.Name,Direction=r.Direction,Amount=r.Amount,DayOfMonth=r.DayOfMonth,StartDate=r.StartDate,EndDate=r.EndDate,LedgerName=r.LedgerName,IsActive=r.IsActive,UpdatedUtc=r.UpdatedUtc};}
    private void SaveRecurring(){var c=_clientContext.CurrentClient;if(c is null)return;try{EditingRecurring.ClientId=c.Id;EditingRecurring.FinancialYear=c.FinancialYear;EditingRecurring.Name=AccountingText.DisplayName(EditingRecurring.Name);EditingRecurring.LedgerName=AccountingText.DisplayName(EditingRecurring.LedgerName);_repository.SaveRecurringTemplate(EditingRecurring);_repository.RecordAudit(c.Id,c.FinancialYear,"Finance Center","Recurring Cash Flow",EditingRecurring.Name,"Save","",$"{EditingRecurring.Direction} {EditingRecurring.Amount:0.00}");StatusText="Recurring cash-flow item saved.";RefreshAll();}catch(Exception ex){StatusText=$"Recurring item save failed: {ex.Message}";}}
    private void DeleteRecurring(){var c=_clientContext.CurrentClient;var row=SelectedRecurring;if(c is null||row is null)return;try{_repository.DeleteRecurringTemplate(row.Id,c.Id,c.FinancialYear);_repository.RecordAudit(c.Id,c.FinancialYear,"Finance Center","Recurring Cash Flow",row.Name,"Delete",row.Amount.ToString(CultureInfo.InvariantCulture),"");RefreshAll();}catch(Exception ex){StatusText=$"Delete failed: {ex.Message}";}}

    private void NewApproval(){var c=_clientContext.CurrentClient;if(c is null)return;EditingApproval=new FinanceApprovalItem{ClientId=c.Id,FinancialYear=c.FinancialYear,Status="Pending",CreatedUtc=DateTime.UtcNow,UpdatedUtc=DateTime.UtcNow};}
    private void EditSelectedApproval(){if(SelectedApproval is null)return;var r=SelectedApproval;EditingApproval=new FinanceApprovalItem{Id=r.Id,ClientId=r.ClientId,FinancialYear=r.FinancialYear,Module=r.Module,ActionType=r.ActionType,Reference=r.Reference,Amount=r.Amount,RequestedBy=r.RequestedBy,Status=r.Status,DecisionNote=r.DecisionNote,CreatedUtc=r.CreatedUtc,UpdatedUtc=r.UpdatedUtc};}
    private void SaveApproval(){var c=_clientContext.CurrentClient;if(c is null)return;try{EditingApproval.ClientId=c.Id;EditingApproval.FinancialYear=c.FinancialYear;EditingApproval.Module=AccountingText.DisplayName(EditingApproval.Module);EditingApproval.ActionType=AccountingText.DisplayName(EditingApproval.ActionType);EditingApproval.RequestedBy=AccountingText.DisplayName(EditingApproval.RequestedBy);_repository.SaveApproval(EditingApproval);_repository.RecordAudit(c.Id,c.FinancialYear,"Finance Center","Approval",EditingApproval.Reference,"Save","",EditingApproval.Status);StatusText="Approval item saved.";RefreshAll();}catch(Exception ex){StatusText=$"Approval save failed: {ex.Message}";}}
    private void DecideApproval(string status){var c=_clientContext.CurrentClient;var row=SelectedApproval;if(c is null||row is null)return;if(!_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.Approve)){StatusText="Your role does not have permission to approve or reject accounting actions.";return;}try{row.Status=status;_repository.SaveApproval(row);_repository.RecordAudit(c.Id,c.FinancialYear,"Finance Center","Approval",row.Reference,status,"Pending",row.DecisionNote);StatusText=$"Approval marked {status}.";RefreshAll();}catch(Exception ex){StatusText=$"Approval update failed: {ex.Message}";}}
    private void DeleteApproval(){var c=_clientContext.CurrentClient;var row=SelectedApproval;if(c is null||row is null)return;try{_repository.DeleteApproval(row.Id,c.Id,c.FinancialYear);_repository.RecordAudit(c.Id,c.FinancialYear,"Finance Center","Approval",row.Reference,"Delete",row.Status,"");RefreshAll();}catch(Exception ex){StatusText=$"Delete failed: {ex.Message}";}}

    private void NewInventoryPolicy(){var c=_clientContext.CurrentClient;if(c is null)return;EditingInventoryPolicy=new InventoryPolicyRecord{ClientId=c.Id,FinancialYear=c.FinancialYear,ExpiryWarningDays=60};}
    private void EditSelectedInventoryPolicy(){if(SelectedInventoryPolicy is null)return;var r=SelectedInventoryPolicy;EditingInventoryPolicy=new InventoryPolicyRecord{Id=r.Id,ClientId=r.ClientId,FinancialYear=r.FinancialYear,StockItemName=r.StockItemName,OpeningQuantity=r.OpeningQuantity,ReorderLevel=r.ReorderLevel,MinimumStock=r.MinimumStock,MaximumStock=r.MaximumStock,ExpiryWarningDays=r.ExpiryWarningDays,UpdatedUtc=r.UpdatedUtc};}
    private void SaveInventoryPolicy(){var c=_clientContext.CurrentClient;if(c is null)return;try{EditingInventoryPolicy.ClientId=c.Id;EditingInventoryPolicy.FinancialYear=c.FinancialYear;EditingInventoryPolicy.StockItemName=AccountingText.DisplayName(EditingInventoryPolicy.StockItemName);_repository.SaveInventoryPolicy(EditingInventoryPolicy);_repository.RecordAudit(c.Id,c.FinancialYear,"Finance Center","Inventory Policy",EditingInventoryPolicy.StockItemName,"Save","",$"Reorder {EditingInventoryPolicy.ReorderLevel:0.##}");StatusText="Inventory policy saved.";RefreshAll();}catch(Exception ex){StatusText=$"Inventory policy save failed: {ex.Message}";}}
    private void DeleteInventoryPolicy(){var c=_clientContext.CurrentClient;var row=SelectedInventoryPolicy;if(c is null||row is null)return;try{_repository.DeleteInventoryPolicy(row.Id,c.Id,c.FinancialYear);_repository.RecordAudit(c.Id,c.FinancialYear,"Finance Center","Inventory Policy",row.StockItemName,"Delete",row.ReorderLevel.ToString(CultureInfo.InvariantCulture),"");RefreshAll();}catch(Exception ex){StatusText=$"Delete failed: {ex.Message}";}}

    private void CreateBackup()
    {
        try
        {
            var dialog=new SaveFileDialog{Title="Create Accounting Automation Hub Backup",Filter="Accounting Automation Hub backup (*.aahbackup)|*.aahbackup|ZIP archive (*.zip)|*.zip",DefaultExt="aahbackup",AddExtension=true,FileName=$"AccountingAutomationHub-{DateTime.Now:yyyyMMdd-HHmm}.aahbackup"};
            if(dialog.ShowDialog()!=true)return;var path=_backupService.CreateBackup(dialog.FileName);StatusText=$"Backup created: {path}";
        }
        catch(Exception ex){StatusText=$"Backup failed: {ex.Message}";}
    }

    private void StageRestore()
    {
        if (!_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.RestoreBackup))
        {
            StatusText = "Your role does not have permission to restore backups.";
            return;
        }
        try
        {
            var dialog=new OpenFileDialog{Title="Restore Accounting Automation Hub Backup",Filter="Accounting Automation Hub backup (*.aahbackup;*.zip)|*.aahbackup;*.zip|All files (*.*)|*.*",CheckFileExists=true};
            if(dialog.ShowDialog()!=true)return;
            if(MessageBox.Show("Restore this backup on the next application start?\n\nThe current database and PDF learning registry will first be copied to a rollback folder.","Stage Backup Restore",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            _backupService.StageRestore(dialog.FileName);StatusText="Restore staged. Exit Accounting Automation Hub completely and start it again to apply the backup safely.";
        }
        catch(Exception ex){StatusText=$"Restore staging failed: {ex.Message}";}
    }

    private void ExportControlPack()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export Finance Control Pack",
                Filter = "ZIP archive (*.zip)|*.zip",
                DefaultExt = "zip",
                AddExtension = true,
                FileName = $"{SafeFileName(client.ClientName)}-{client.FinancialYear}-Finance-Control-Pack-{DateTime.Now:yyyyMMdd}.zip"
            };
            if (dialog.ShowDialog() != true) return;
            using var archive = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create);
            WriteCsv(archive,"Month-End-Close.csv",new[]{"Area","Check","Status","Count","Detail"},CloseChecks.Select(x=>new[]{x.Area,x.Check,x.Status,x.Count.ToString(CultureInfo.InvariantCulture),x.Detail}));
            WriteCsv(archive,"Data-Integrity.csv",new[]{"Check","Status","Detail"},IntegrityChecks.Select(x=>new[]{x.Check,x.Status,x.Detail}));
            WriteCsv(archive,"Audit-Trail.csv",new[]{"ChangedUtc","Module","EntityType","EntityKey","Operation","OldValue","NewValue","Source"},AuditTrail.Select(x=>new[]{x.ChangedUtc.ToString("O"),x.Module,x.EntityType,x.EntityKey,x.Operation,x.OldValue,x.NewValue,x.Source}));
            WriteCsv(archive,"Cash-Flow.csv",new[]{"Period","ActualInflow","ActualOutflow","PlannedInflow","PlannedOutflow","Net","Closing"},CashFlow.Select(x=>new[]{x.Period,F(x.ActualInflow),F(x.ActualOutflow),F(x.PlannedInflow),F(x.PlannedOutflow),F(x.Net),F(x.Closing)}));
            WriteCsv(archive,"AR-AP-Ageing.csv",new[]{"Side","Party","DocumentNumber","DocumentDate","OriginalAmount","SettledAmount","OutstandingAmount","AgeDays","Bucket","Status"},Aging.Select(x=>new[]{x.Side,x.Party,x.DocumentNumber,x.DocumentDate?.ToString("yyyy-MM-dd")??string.Empty,F(x.OriginalAmount),F(x.SettledAmount),F(x.OutstandingAmount),x.AgeDays.ToString(CultureInfo.InvariantCulture),x.Bucket,x.Status}));
            WriteCsv(archive,"Fixed-Assets.csv",new[]{"AssetCode","AssetName","AssetGroup","PurchaseDate","PurchaseCost","Method","Rate","BookValue","Location","SerialNumber"},FixedAssets.Select(x=>new[]{x.AssetCode,x.AssetName,x.AssetGroup,x.PurchaseDate?.ToString("yyyy-MM-dd")??string.Empty,F(x.PurchaseCost),x.DepreciationMethod,F(x.DepreciationRate),F(x.CurrentBookValue),x.Location,x.SerialNumber}));
            WriteCsv(archive,"Budgets.csv",new[]{"Period","Category","Budget","Actual","Variance"},Budgets.Select(x=>new[]{x.Period,x.Category,F(x.BudgetAmount),F(x.ActualAmount),F(x.Variance)}));
            WriteCsv(archive,"Inventory-Intelligence.csv",new[]{"StockItem","Unit","Opening","SalesQty","EstimatedAvailable","ReorderLevel","NearestExpiry","Alert"},InventoryIntelligence.Select(x=>new[]{x.StockItemName,x.Unit,F(x.OpeningQuantity),F(x.SalesQuantity),F(x.EstimatedAvailable),F(x.ReorderLevel),x.NearestExpiry?.ToString("yyyy-MM-dd")??string.Empty,x.Alert}));
            WriteCsv(archive,"Document-Inbox.csv",new[]{"ProcessedUtc","FileName","FileType","Status","Detail","FilePath"},DocumentInbox.Select(x=>new[]{x.ProcessedUtc.ToString("O"),x.FileName,x.FileType,x.Status,x.Detail,x.FilePath}));
            _repository.RecordAudit(client.Id,client.FinancialYear,"Finance Center","Control Pack",Path.GetFileName(dialog.FileName),"Export","",dialog.FileName);
            StatusText = $"Finance Control Pack exported: {dialog.FileName}";
        }
        catch(Exception ex){StatusText=$"Control Pack export failed: {ex.Message}";}
    }

    private static void WriteCsv(ZipArchive archive,string name,IReadOnlyList<string> headers,IEnumerable<string[]> rows)
    {
        var entry=archive.CreateEntry(name,CompressionLevel.Optimal);
        using var stream=entry.Open();using var writer=new StreamWriter(stream,new UTF8Encoding(true));
        writer.WriteLine(string.Join(',',headers.Select(Csv)));
        foreach(var row in rows)writer.WriteLine(string.Join(',',row.Select(Csv)));
    }
    private static string Csv(string? value){var text=value??string.Empty;return "\""+text.Replace("\"","\"\"")+"\"";}
    private static string F(decimal value)=>value.ToString("0.00",CultureInfo.InvariantCulture);
    private static string SafeFileName(string value)=>string.Concat((value??string.Empty).Select(ch=>Path.GetInvalidFileNameChars().Contains(ch)?'_':ch)).Trim();

    private void PreviewSelectedImportRun()
    {
        var run = SelectedImportRun;
        if (run is null) return;
        try
        {
            var preview = _undoService.Preview(run.RunId);
            MessageBox.Show(
                $"Run: {run.RunId}\n\nBank statements: {preview.BankStatements:N0}\nBank transactions: {preview.BankTransactions:N0}\nPurchase rows: {preview.PurchaseRecords:N0}\nSales rows: {preview.SalesRecords:N0}\nCash entries: {preview.CashEntries:N0}\nJournal vouchers: {preview.JournalVouchers:N0}\n\nTotal attributable records: {preview.TotalRecords:N0}\n\n{preview.Warning}",
                "Preview Undo Import",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            StatusText = $"Undo preview found {preview.TotalRecords:N0} attributable accounting record(s).";
        }
        catch (Exception exception) { StatusText = $"Undo preview failed: {exception.Message}"; }
    }

    private void UndoSelectedImportRun()
    {
        var run = SelectedImportRun;
        if (run is null) return;
        if (!_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.UndoImport))
        {
            StatusText = "The current application user does not have Undo Import permission.";
            return;
        }
        var preview = _undoService.Preview(run.RunId);
        if (preview.TotalRecords == 0) { StatusText = "No attributable accounting records are available to undo for this run."; return; }
        var reason = string.IsNullOrWhiteSpace(UndoReason) ? "Manual import rollback" : UndoReason.Trim();
        if (MessageBox.Show(
                $"Undo import run {run.RunId[..Math.Min(8, run.RunId.Length)].ToUpperInvariant()}?\n\n{preview.TotalRecords:N0} attributable accounting record(s) will be removed. Masters, Settings, mappings and external Python are not touched.\n\nReason: {reason}",
                "Undo Import Run",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var result = _undoService.Undo(run.RunId, reason);
            StatusText = $"Import run rolled back. {result.TotalRecords:N0} attributable record(s) removed.";
            if (result.BankStatements > 0 || result.BankTransactions > 0 || result.CashEntries > 0)
                _liveChangeHub.Publish(run.ClientId, run.FinancialYear, AccountingLiveArea.Banking, "Import run rolled back");
            if (result.SalesRecords > 0)
                _liveChangeHub.Publish(run.ClientId, run.FinancialYear, AccountingLiveArea.Sales, "Import run rolled back");
            if (result.PurchaseRecords > 0)
                _liveChangeHub.Publish(run.ClientId, run.FinancialYear, AccountingLiveArea.Purchase, "Import run rolled back");
            if (result.JournalVouchers > 0)
                _liveChangeHub.Publish(run.ClientId, run.FinancialYear, AccountingLiveArea.Journal, "Import run rolled back");
            RefreshAll();
        }
        catch (Exception exception) { StatusText = $"Undo failed: {exception.Message}"; }
    }

    private void OpenSelectedVaultDocument()
    {
        var document = SelectedVaultDocument;
        if (document is null) return;
        var path = File.Exists(document.VaultPath) ? document.VaultPath : document.OriginalPath;
        if (!File.Exists(path)) { StatusText = "The selected source document is no longer available at its recorded local path."; return; }
        try
        {
            // Security contract: imported/source documents are evidence. AAH never shell-executes
            // them from the Document Vault because a malicious document could invoke an external
            // handler. Select the file in Explorer instead; the user remains in control of opening it.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            StatusText = $"Selected source document in File Explorer: {document.FileName}. AAH did not execute the document.";
        }
        catch (Exception exception) { StatusText = $"Could not select source document: {exception.Message}"; }
    }

    private void NewApplicationUser()
    {
        EditingApplicationUser = new ApplicationUserRecord
        {
            RoleName = GovernanceRoles.User,
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
    }


    private void ConfigureNetworkMfa()
    {
        var row = SelectedApplicationUser;
        if (row is null) return;
        if (!_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.ManageUsers))
        {
            StatusText = "Only a user with Manage Users permission can create or reset an AAH user login.";
            return;
        }
        try
        {
            var dialog = new NetworkCredentialSetupWindow(row.WindowsUserName, _settings)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (dialog.ShowDialog() == true)
                StatusText = $"AAH User ID/password configured for {row.WindowsUserName}. Existing network sessions were revoked; existing sessions were revoked. Routine registered-device login remains password-based; an Administrator can separately require Registration OTP from the Main Server Control Panel.";
        }
        catch (Exception exception) { StatusText = $"Could not configure network MFA: {exception.Message}"; }
    }

    private void EditSelectedApplicationUser()
    {
        var row = SelectedApplicationUser;
        if (row is null) return;
        EditingApplicationUser = new ApplicationUserRecord
        {
            Id = row.Id,
            WindowsUserName = row.WindowsUserName,
            DisplayName = row.DisplayName,
            RoleName = row.RoleName,
            IsActive = row.IsActive,
            CreatedUtc = row.CreatedUtc,
            UpdatedUtc = row.UpdatedUtc,
            LastSeenUtc = row.LastSeenUtc
        };
    }

    private void SaveApplicationUser()
    {
        if (!_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.ManageUsers))
        {
            StatusText = "Only a user with Manage Users permission can change application access.";
            return;
        }
        try
        {
            _governanceRepository.SaveUser(EditingApplicationUser);
            StatusText = $"Application user '{EditingApplicationUser.WindowsUserName}' saved as {EditingApplicationUser.RoleName}.";
            RefreshAll();
        }
        catch (Exception exception) { StatusText = $"User save failed: {exception.Message}"; }
    }


    private void UpdateSelectedApplicationUserAccessText()
    {
        var row = SelectedApplicationUser;
        if (row is null)
        {
            SelectedApplicationUserAccessText = "Select an application user to review Client/FY access.";
            return;
        }

        if (row.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase))
        {
            SelectedApplicationUserAccessText = "Administrator has full Client/FY access by design.";
            return;
        }

        var scopes = _governanceRepository.GetClientAccess(row.WindowsUserName);
        if (scopes.Count == 0)
        {
            SelectedApplicationUserAccessText = "Local desktop uses the legacy unrestricted scope, but Remote access is fail-closed: no remote Client/FY is allowed until you explicitly add one here.";
            return;
        }

        var allowed = scopes.Where(x => x.IsAllowed).ToArray();
        SelectedApplicationUserAccessText = allowed.Length == 0
            ? "Explicit scope exists but no Client/FY is allowed."
            : $"Explicit Client/FY scope: {string.Join("; ", allowed.Select(x => $"Client {x.ClientId} · FY {(string.IsNullOrWhiteSpace(x.FinancialYear) ? "All" : x.FinancialYear)}"))}";
    }

    private void AllowSelectedUserCurrentClient()
    {
        var row = SelectedApplicationUser;
        var client = _clientContext.CurrentClient;
        if (row is null || client is null) return;
        if (!_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.ManageUsers))
        {
            StatusText = "Only a user with Manage Users permission can change Client/FY access.";
            return;
        }
        if (row.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "Administrator already has full Client/FY access.";
            return;
        }

        try
        {
            _governanceRepository.AllowClientAccess(row.WindowsUserName, client.Id, client.FinancialYear);
            UpdateSelectedApplicationUserAccessText();
            StatusText = $"Allowed {row.DisplayName} to access {client.ClientName} · FY {client.FinancialYear}. Existing explicit scopes remain unchanged.";
        }
        catch (Exception exception) { StatusText = $"Client/FY access update failed: {exception.Message}"; }
    }

    private void ClearSelectedUserClientAccess()
    {
        var row = SelectedApplicationUser;
        if (row is null) return;
        if (!_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.ManageUsers))
        {
            StatusText = "Only a user with Manage Users permission can change Client/FY access.";
            return;
        }
        if (MessageBox.Show(
                $"Clear all explicit Client/FY assignments for '{row.DisplayName}'?\n\nLocal desktop returns to its legacy unrestricted scope, but Remote access becomes NONE until a Client/FY is explicitly assigned again.",
                "Clear Client/FY Restrictions",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            _governanceRepository.ClearClientAccess(row.WindowsUserName);
            UpdateSelectedApplicationUserAccessText();
            StatusText = $"Cleared explicit Client/FY assignments for {row.DisplayName}. Remote access is now blocked until a Client/FY is explicitly assigned again.";
        }
        catch (Exception exception) { StatusText = $"Client/FY access reset failed: {exception.Message}"; }
    }

    private void ToggleLanDeviceActive()
    {
        var device = SelectedLanDevice;
        if (device is null) return;
        if (!_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.ManageDevices) &&
            !_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.ManageUsers))
        {
            StatusText = "Only a user with Manage Devices or Manage Users permission can revoke enrolled devices.";
            return;
        }
        if (!device.IsActive)
        {
            StatusText = $"Device '{device.DeviceName}' is inactive/expired/revoked. Create a new enrollment code and re-enroll it; old device tokens are never re-enabled.";
            return;
        }
        try
        {
            _serverSecurityRepository.RevokeDevice(device.Id);
            StatusText = $"Revoked device '{device.DeviceName}'. Existing sessions and linked-folder authorization were revoked; re-enrollment is required before it can return.";
            RefreshAll();
        }
        catch (Exception exception) { StatusText = $"Device revocation failed: {exception.Message}"; }
    }

    private void CreateVerifiedBackup()
    {
        try
        {
            var backup = _verifiedBackupService.CreateVerifiedBackup();
            StatusText = backup.IsVerified
                ? $"Verified accounting-data backup created: {Path.GetFileName(backup.BackupPath)}"
                : $"Backup created but integrity verification reported: {backup.IntegrityResult}";
            RefreshAll();
        }
        catch (Exception exception) { StatusText = $"Verified backup failed: {exception.Message}"; }
    }

    private void OpenVerifiedBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(AccountingAutomationHub.Infrastructure.Persistence.ApplicationDataPaths.VerifiedBackupFolder);
            Process.Start(new ProcessStartInfo(AccountingAutomationHub.Infrastructure.Persistence.ApplicationDataPaths.VerifiedBackupFolder) { UseShellExecute = true });
        }
        catch (Exception exception) { StatusText = $"Could not open backup folder: {exception.Message}"; }
    }

    private void RecoveryScan()
    {
        try { _automationService.ForceScanAllFolders(); StatusText="Auto Import recovery scan requested for all registered folders."; }
        catch(Exception ex){StatusText=$"Recovery scan failed: {ex.Message}";}
    }

    private void OnClientContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveClientContext.CurrentClient)) return;
        if (_presentationActive) ClearAll();
        StatusText = _clientContext.CurrentClient is null ? "Open a client to use Finance Center." : "Finance Center is preparing in the background.";
        RaiseCommandStates();
        if (_presentationActive) RefreshAll(force: false);
    }

    private void OnLiveChange(object? sender, AccountingLiveChange e)
    {
        InvalidateFinanceClient(e.ClientId, e.FinancialYear);
    }

    private void DispatchRefresh()
    {
        // A data change is recorded even when Finance Center is not visible.
        // The background repository projection owns its replacement.
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        InvalidateFinanceClient(client.Id, client.FinancialYear);
    }

    private void ClearAll(){Kpis.Clear();CloseChecks.Clear();IntegrityChecks.Clear();PeriodLocks.Clear();AuditTrail.Clear();CashFlow.Clear();Aging.Clear();FixedAssets.Clear();BankRules.Clear();BankRulePreview.Clear();Budgets.Clear();RecurringItems.Clear();Approvals.Clear();InventoryPolicies.Clear();InventoryIntelligence.Clear();DocumentInbox.Clear();SearchResults.Clear();ImportRuns.Clear();ImportJobs.Clear();VaultDocuments.Clear();VersionHistory.Clear();ApplicationUsers.Clear();LanDevices.Clear();VerifiedBackups.Clear();TransactionAutomationProfiles.Clear();AdvancedReconciliations.Clear();}
    private static void Replace<T>(ObservableCollection<T> target,IEnumerable<T> rows){target.Clear();foreach(var row in rows)target.Add(row);}

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();SearchCommand.RaiseCanExecuteChanged();SavePeriodLockCommand.RaiseCanExecuteChanged();UnlockPeriodCommand.RaiseCanExecuteChanged();SaveSettlementCommand.RaiseCanExecuteChanged();
        NewAssetCommand.RaiseCanExecuteChanged();SaveAssetCommand.RaiseCanExecuteChanged();DeleteAssetCommand.RaiseCanExecuteChanged();NewBankRuleCommand.RaiseCanExecuteChanged();PreviewBankRuleCommand.RaiseCanExecuteChanged();SaveBankRuleCommand.RaiseCanExecuteChanged();DeleteBankRuleCommand.RaiseCanExecuteChanged();ApplyBankRulesCommand.RaiseCanExecuteChanged();
        NewBudgetCommand.RaiseCanExecuteChanged();SaveBudgetCommand.RaiseCanExecuteChanged();DeleteBudgetCommand.RaiseCanExecuteChanged();NewRecurringCommand.RaiseCanExecuteChanged();SaveRecurringCommand.RaiseCanExecuteChanged();DeleteRecurringCommand.RaiseCanExecuteChanged();NewApprovalCommand.RaiseCanExecuteChanged();SaveApprovalCommand.RaiseCanExecuteChanged();ApproveCommand.RaiseCanExecuteChanged();RejectCommand.RaiseCanExecuteChanged();DeleteApprovalCommand.RaiseCanExecuteChanged();NewInventoryPolicyCommand.RaiseCanExecuteChanged();SaveInventoryPolicyCommand.RaiseCanExecuteChanged();DeleteInventoryPolicyCommand.RaiseCanExecuteChanged();
        PreviewUndoCommand.RaiseCanExecuteChanged();UndoImportRunCommand.RaiseCanExecuteChanged();OpenVaultDocumentCommand.RaiseCanExecuteChanged();EditApplicationUserCommand.RaiseCanExecuteChanged();ConfigureNetworkMfaCommand.RaiseCanExecuteChanged();AllowSelectedUserCurrentClientCommand.RaiseCanExecuteChanged();ClearSelectedUserClientAccessCommand.RaiseCanExecuteChanged();ToggleLanDeviceActiveCommand.RaiseCanExecuteChanged();
    }

    private static FixedAssetRecord CloneAsset(FixedAssetRecord r)=>new(){Id=r.Id,ClientId=r.ClientId,FinancialYear=r.FinancialYear,AssetCode=r.AssetCode,AssetName=r.AssetName,AssetGroup=r.AssetGroup,PurchaseDate=r.PurchaseDate,PutToUseDate=r.PutToUseDate,PurchaseCost=r.PurchaseCost,ResidualValue=r.ResidualValue,DepreciationMethod=r.DepreciationMethod,DepreciationRate=r.DepreciationRate,OpeningAccumulatedDepreciation=r.OpeningAccumulatedDepreciation,DisposalDate=r.DisposalDate,DisposalValue=r.DisposalValue,Location=r.Location,SerialNumber=r.SerialNumber,Notes=r.Notes,UpdatedUtc=r.UpdatedUtc,CurrentBookValue=r.CurrentBookValue};
    private static BankRuleRecord CloneRule(BankRuleRecord r)=>new(){Id=r.Id,ClientId=r.ClientId,FinancialYear=r.FinancialYear,Priority=r.Priority,RuleName=r.RuleName,BankContains=r.BankContains,Mode=r.Mode,NarrationContains=r.NarrationContains,NarrationStartsWith=r.NarrationStartsWith,ReferenceContains=r.ReferenceContains,NarrationRegex=r.NarrationRegex,MinAmount=r.MinAmount,MaxAmount=r.MaxAmount,LedgerName=r.LedgerName,LedgerGroup=r.LedgerGroup,IsActive=r.IsActive,AppliedCount=r.AppliedCount,UpdatedUtc=r.UpdatedUtc};

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clientContext.PropertyChanged -= OnClientContextChanged;
        _liveChangeHub.Changed -= OnLiveChange;
        _financeSourceMonitor.Dispose();
        _preparedFinance.Ready -= OnPreparedFinanceReady;
        _preparedFinance.Failed -= OnPreparedFinanceFailed;
        _preparedFinance.Dispose();
        _verifiedBackupService.Dispose();
    }
}
