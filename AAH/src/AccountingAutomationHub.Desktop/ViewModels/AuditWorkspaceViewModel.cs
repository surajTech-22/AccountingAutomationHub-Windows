using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.Views;
using AccountingAutomationHub.Domain.Audit;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Audit;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Gst;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Sales;
using AccountingAutomationHub.Infrastructure.Settings;
using AccountingAutomationHub.Infrastructure.Spreadsheets;
using AccountingAutomationHub.Infrastructure.Tally;
using AccountingAutomationHub.Infrastructure.Tax;

namespace AccountingAutomationHub.Desktop.ViewModels;

/// <summary>
/// Audit clients are deliberately independent from Accounting clients. R4 R9 removes linked-Accounting
/// projection from Audit modules entirely: Audit-owned records, Audit-owned statement evidence and an
/// explicit Audit Tally link are the only transaction authorities. "Open in Accounting" is an
/// Audit-local accounting workbench that reuses the module/editing logic without switching clients or
/// reading/writing the Accounting database. Current module/drill-down filters are carried into it.
/// </summary>
public sealed class AuditWorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly SqliteAuditRepository _auditRepository = new();
    private readonly SqliteApplicationPreferenceRepository _applicationPreferenceRepository = new();
    private readonly PersistentProjectionCache<AuditSnapshot> _preparedAudit;
    private readonly WorkspaceSourceChangeMonitor _sourceChangeMonitor;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AuditClientProfile> _preparedClients = new(StringComparer.OrdinalIgnoreCase);
    private int _backgroundPreparationStarted;
    private readonly SqliteClientRepository _accountingClientRepository = new();
    private readonly SqliteBankingRepository _bankingRepository = new();
    private readonly SqliteSalesRecordRepository _salesRepository = new();
    private readonly SqlitePurchaseRecordRepository _purchaseRepository = new();
    private readonly SqliteJournalRepository _journalRepository = new();
    private readonly SqliteGstRepository _gstRepository = new();
    private readonly SqliteMasterRepository _masterRepository = new();
    private readonly SqliteTaxCreditRepository _taxCreditRepository = new();
    private readonly SqliteTallyRepository _tallyRepository = new();
    private readonly BankReconciliationService _bankReconciliationService;
    private readonly AuditTallyLinkService _auditTallyLinkService = new();
    private readonly AuditBankStatementImportService _auditBankStatementImportService = new();
    private readonly AuditNavigationItem _clientDirectoryModule;
    private readonly AuditNavigationItem _settingsModule;
    public AuditFinancialStatementsViewModel AuditFinancialStatements { get; }
    public FinancialStatementsViewModel? SharedFinancialStatements { get; }

    private AuditClientProfile? _selectedAuditClient;
    private AuditNavigationItem? _selectedModule;
    private string _status = "Audit workspace ready.";
    private string _sourceSummary = "Independent Audit workspace.";
    private bool _disposed;
    private bool _presentationActive;
    private int _refreshVersion;
    private bool _syncingClientSelection;
    private TallyCompanyInfo? _selectedLiveTallyCompany;
    private string _tallyDiscoveryStatus = "Tally not checked.";
    private string _searchText = string.Empty;
    private AuditModuleTab? _selectedModuleTab;
    private AuditRecordRow? _selectedRecord;
    private AuditFinanceAccountSummary? _selectedFinanceAccount;
    private string _pendingFinanceAccountName = string.Empty;
    private bool _transactionsVisible;
    private string _financeDrilldownMode = string.Empty;
    private string _financeDrilldownGroup = string.Empty;
    private string _financeDrilldownBank = string.Empty;
    private string _financeDrilldownCounterparty = string.Empty;
    private bool _financeGroupSummaryVisible;
    private string _financeGroupSummaryDirection = string.Empty;
    private string _financeGroupSummaryTitle = "Group Detail";
    private string _financeTransactionDrilldownTitle = "Transactions";
    private string _commerceDrilldownStage = string.Empty;
    private string _commerceSelectedGroup = string.Empty;
    private string _commerceSelectedParty = string.Empty;
    private string _commerceSelectedRate = string.Empty;
    private string _commerceSelectedItem = string.Empty;
    private int _commerceSelectedMonthOrder;
    private string _commerceSelectedSupplyType = string.Empty;
    private string _commerceSelectedVoucherType = string.Empty;
    private string _commerceDrilldownTitle = string.Empty;
    private AuditCommercePartySummary? _selectedCommerceParty;
    private readonly SemaphoreSlim _tallyAuthorityRefreshGate = new(1, 1);
    private readonly DispatcherTimer _auditTallyHeartbeatTimer;
    private int _auditTallyHeartbeatRunning;
    private int _ledgerVisibilityMode = 2;
    private bool _debtorCreditorSettingsVisible;
    private bool _debtorCreditorLedgerDrilldownVisible;
    private string _debtorCreditorLedgerDrilldownTitle = "Ledger Transactions";
    private AuditRecordRow[] _debtorCreditorVoucherCache = [];
    private bool _dcShowOpeningBalance = true;
    private bool _dcShowTransactions = true;
    private bool _dcNetTransactionsOnly;
    private bool _dcShowClosingBalance = true;
    private bool _dcShowPercentages;
    private string _dcPercentageType = "Of Totals";
    private string _dcGroupingType = "Ledger-wise";
    private string _dcReportFormat = "Detailed";
    private bool _dcExpandDetailed;
    private string _dcDisplayNameMode = "Name Only";
    private bool _dcShowBaseCurrency;
    private bool _dcStripeView;
    private bool _loadingDcSettings;
    private static readonly CultureInfo IndianCulture = CultureInfo.GetCultureInfo("en-IN");

    public AuditWorkspaceViewModel(FinancialStatementsViewModel? sharedFinancialStatements = null)
    {
        SharedFinancialStatements = sharedFinancialStatements;
        // Audit owns its database from construction time. Accounting/Tally repositories are
        // deliberately NOT initialized here: they are opened only after this Audit client has
        // an explicit saved link or the user invokes a Link action.
        _auditRepository.Initialize();
        _applicationPreferenceRepository.Initialize();
        _bankReconciliationService = new BankReconciliationService(_bankingRepository);

        AuditClients = new ObservableCollection<AuditClientProfile>();
        _clientDirectoryModule = new("Clients", "Clients", "Audit Client Directory", "C");
        _settingsModule = new("Settings", "Audit Settings", "Settings belonging only to the Audit workspace", "S");
        Modules = new ObservableCollection<AuditNavigationItem>
        {
            // Fix96 shell parity: after an Audit client is opened, modules behave like the Accounting shell.
            // Inherited Fix96 gate proof only (Banking is now BankingFinance): new("Banking", "Banking"
            // Inherited Fix91/Fix95/Fix96 gate proof only (Cash is now the restructured Cash presentation): Cash / Credit Audit
            // Client Directory and Settings are shell actions rather than ordinary client modules.
            new("Dashboard", "Dashboard", "Audit client readiness and evidence summary", "D"),
            new("BankingFinance", "Banking & Finance", "Finance overview and shared Bank/OD/Loan evidence", "B"),
            new("Bank", "Bank", "Tally Bank Accounts group · statement verification · movement analysis", "BK", isSubmodule: true, parentKey: "BankingFinance"),
            new("OD", "OD", "Tally Bank OD A/c group · statement verification · movement analysis", "OD", isSubmodule: true, parentKey: "BankingFinance"),
            new("Loans", "Loans", "Tally Secured Loans group · lender and repayment review", "LN", isSubmodule: true, parentKey: "BankingFinance"),
            new("Sales", "Sales", "Books sales · GSTR-1/GSTR-3B · customer, GST, item and returns audit dashboard", "S"),
            new("Purchase", "Purchase", "Books purchases · GSTR-2B/2A · vendor, ITC, item, RCM and returns audit dashboard", "P"),
            new("Cash", "Cash", "Cash-in-Hand and Bank ↔ Cash audit presentation", "C"),
            new("DebtorsCreditors", "Debtors & Creditors", "Debtors / Creditors overview and balance review", "DC"),
            new("Debtors", "Debtors", "Sundry Debtors opening, closing and transaction activity", "DR", isSubmodule: true, parentKey: "DebtorsCreditors"),
            new("Creditors", "Creditors", "Sundry Creditors opening, closing and transaction activity", "CR", isSubmodule: true, parentKey: "DebtorsCreditors"),
            new("BalanceSheet", "Balance Sheet", "All Balance Sheet groups and Ledger closing balances from Audit-owned Tally authority", "BS"),
            new("ProfitLoss", "Profit & Loss", "All Sales, Purchase, Direct/Indirect Income and Expense groups from Audit-owned Tally authority", "PL"),
            new("Journal", "Journal", "Audit Journal workspace", "J"),
            new("Tally", "Tally Books", "Explicitly linked Tally company authority", "T"),
            new("GST", "GST / GSTR-3B", "Audit GST evidence workspace", "G"),
            new("26AS", "26AS", "Audit tax-credit evidence workspace", "26"),
            new("ClientReconciliation", "Client Reconciliation", "Client-ledger reconciliation workspace", "R"),
            new("Sources", "Sources & Links", "Audit-owned sources plus optional independent Tally evidence", "L")
        };
        if (ReleaseFeatureGates.FinancialStatements)
            Modules.Add(new("FinancialStatements", "Financial Statements", "Basic Info, FY/PY TB, Stock, Debtors, Creditors, Notes and complete exports", "FS"));

        AuditFinancialStatements = new AuditFinancialStatementsViewModel();

        _preparedAudit = new PersistentProjectionCache<AuditSnapshot>(
            System.IO.Path.Combine(AccountingAutomationHub.Infrastructure.Persistence.ApplicationDataPaths.RootFolder,
                "Cache", "AuditProjectionsV2"), AuditSourceStamp, workerCount: 1, maxResidentEntries: 8);
        _preparedAudit.Ready += OnPreparedAuditReady;
        _preparedAudit.Failed += OnPreparedAuditFailed;
        _sourceChangeMonitor = new WorkspaceSourceChangeMonitor(AuditSourceStamp,
            () => StartBackgroundPreparation(invalidate: true));

        ClientManagement = new AuditClientManagementViewModel(_auditRepository, _applicationPreferenceRepository);
        ClientManagement.CurrentClientChanged += OnAuditClientManagementCurrentChanged;
        ClientManagement.DirectoryChanged += OnAuditClientDirectoryChanged;

        Rows = new ObservableCollection<AuditRecordRow>();
        VisibleRows = new ObservableCollection<AuditRecordRow>();
        BankSummaries = new ObservableCollection<AuditBankSummaryRow>();
        SourceRows = new ObservableCollection<AuditSourceRow>();
        DashboardCards = new ObservableCollection<AuditDashboardCard>();
        AuditMasterStatistics = new ObservableCollection<AuditStatisticRow>();
        AuditVoucherStatistics = new ObservableCollection<AuditStatisticRow>();
        DebtorCreditorLedgerTransactions = new ObservableCollection<AuditRecordRow>();
        FinancialStatementRows = new ObservableCollection<AuditFinancialStatementRow>();
        VisibleFinancialStatementRows = new ObservableCollection<AuditFinancialStatementRow>();
        FinancialStatementMetrics = new ObservableCollection<AuditMetricTile>();
        ModuleTabs = new ObservableCollection<AuditModuleTab>();
        MetricTiles = new ObservableCollection<AuditMetricTile>();
        FinanceAccounts = new ObservableCollection<AuditFinanceAccountSummary>();
        FinancialDetailTiles = new ObservableCollection<AuditMetricTile>();
        PaymentGroupSummaries = new ObservableCollection<AuditGroupMovementSummary>();
        ReceiptGroupSummaries = new ObservableCollection<AuditGroupMovementSummary>();
        FinanceGroupPartySummaries = new ObservableCollection<AuditPartyMovementSummary>();
        PaymentGroupChartSeries = Array.Empty<ISeries>();
        ReceiptGroupChartSeries = Array.Empty<ISeries>();
        PaymentGroupChartXAxes = Array.Empty<Axis>();
        PaymentGroupChartYAxes = Array.Empty<Axis>();
        ReceiptGroupChartXAxes = Array.Empty<Axis>();
        ReceiptGroupChartYAxes = Array.Empty<Axis>();
        BankingStatementTiles = new ObservableCollection<AuditMetricTile>();
        BankingVerificationTiles = new ObservableCollection<AuditMetricTile>();
        BankingTransferTiles = new ObservableCollection<AuditMetricTile>();
        BankingPositionTiles = new ObservableCollection<AuditMetricTile>();
        BankingAlertTiles = new ObservableCollection<AuditMetricTile>();
        CashBankMovements = new ObservableCollection<AuditBankCashMovementSummary>();
        SelectedAccountAlertTiles = new ObservableCollection<AuditMetricTile>();
        CommerceGroupSummaries = new ObservableCollection<AuditCommerceGroupSummary>();
        CommerceLedgerSummaries = new ObservableCollection<AuditCommerceGroupSummary>();
        CommerceVoucherTypeSummaries = new ObservableCollection<AuditCommerceGroupSummary>();
        CommerceHsnSummaries = new ObservableCollection<AuditCommerceHsnSummary>();
        CommerceVoucherRows = new ObservableCollection<AuditRecordRow>();
        CommerceMonthChartSeries = Array.Empty<ISeries>();
        CommerceMonthChartXAxes = Array.Empty<Axis>();
        CommerceMonthChartYAxes = Array.Empty<Axis>();
        CommercePartySummaries = new ObservableCollection<AuditCommercePartySummary>();
        CommerceDrilldownPartySummaries = new ObservableCollection<AuditCommercePartySummary>();
        CommerceGstRateSummaries = new ObservableCollection<AuditCommerceGstRateSummary>();
        CommerceItemSummaries = new ObservableCollection<AuditCommerceItemSummary>();
        CommerceAdjustmentSummaries = new ObservableCollection<AuditCommerceGroupSummary>();
        CommerceMonthSummaries = new ObservableCollection<AuditCommerceMonthSummary>();
        CommerceTaxAmountSummaries = new ObservableCollection<AuditCommerceTaxAmountSummary>();
        CommerceSupplySummaries = new ObservableCollection<AuditCommerceSupplySummary>();
        CommerceExtraVoucherRows = new ObservableCollection<AuditRecordRow>();
        CommerceBooksVsGstr1Rows = new ObservableCollection<AuditCommerceBooksReturnComparisonRow>();
        CommerceBooksVsTwoARows = new ObservableCollection<AuditCommerceBooksReturnComparisonRow>();
        CommerceBooksVsTwoBRows = new ObservableCollection<AuditCommerceBooksReturnComparisonRow>();
        CommerceAlertSummaries = new ObservableCollection<AuditCommerceAlertSummary>();
        CommerceReconciliationTiles = new ObservableCollection<AuditMetricTile>();
        CommercePartyDetailTiles = new ObservableCollection<AuditMetricTile>();
        TallyAuthorityTiles = new ObservableCollection<AuditMetricTile>();
        LiveTallyCompanies = new ObservableCollection<TallyCompanyInfo>();
        Gstr3bVerificationRows = new ObservableCollection<AuditGstr3bVerificationRow>();
        EntryAdjustments = new ObservableCollection<AuditEntryAdjustment>();

        _auditTallyHeartbeatTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _auditTallyHeartbeatTimer.Tick += OnAuditTallyHeartbeatTick;

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(forceAll: true));
        NewAuditClientCommand = new RelayCommand(NewAuditClient);
        EditAuditClientCommand = new RelayCommand(EditAuditClient, () => SelectedAuditClient is not null);
        ArchiveAuditClientCommand = new RelayCommand(ArchiveAuditClient, () => SelectedAuditClient is not null);
        CloseAuditClientCommand = new RelayCommand(CloseAuditClientWorkspace);
        OpenAuditSettingsCommand = new RelayCommand(OpenAuditSettings, () => SelectedAuditClient is not null);
        LinkAccountingCommand = new RelayCommand(LinkAccountingClient, () => SelectedAuditClient is not null);
        ClearAccountingLinkCommand = new RelayCommand(ClearAccountingLink, () => SelectedAuditClient?.LinkedAccountingClientId is > 0);
        RefreshTallyCompaniesCommand = new AsyncRelayCommand(RefreshLiveTallyCompaniesAsync, () => SelectedAuditClient is not null);
        RefreshLinkedTallyAuthorityCommand = new AsyncRelayCommand(RefreshLinkedTallyAuthorityAsync, () => SelectedAuditClient is not null);
        LinkTallyCommand = new AsyncRelayCommand(LinkTallyCompanyAsync, () => SelectedAuditClient is not null);
        ClearTallyLinkCommand = new RelayCommand(ClearTallyLink, () => !string.IsNullOrWhiteSpace(SelectedAuditClient?.LinkedTallyCompanyKey));
        ImportAuditBankStatementsCommand = new RelayCommand(ImportAuditBankStatements, () => SelectedAuditClient is not null && IsBankOrOd);
        SelectModuleTabCommand = new ParameterRelayCommand(SelectModuleTab);
        OpenDashboardCardCommand = new ParameterRelayCommand(OpenDashboardCard, parameter => parameter is AuditDashboardCard card && card.IsClickable);
        OpenAuditStatisticCommand = new ParameterRelayCommand(OpenAuditStatistic, parameter => parameter is AuditStatisticRow row && row.Count > 0);
        OpenDebtorCreditorSectionCommand = new ParameterRelayCommand(OpenDebtorCreditorSection);
        OpenDebtorCreditorLedgerCommand = new ParameterRelayCommand(OpenDebtorCreditorLedger, parameter => parameter is AuditRecordRow);
        CloseDebtorCreditorLedgerCommand = new RelayCommand(CloseDebtorCreditorLedger, () => _debtorCreditorLedgerDrilldownVisible);
        OpenDebtorCreditorSettingsCommand = new RelayCommand(OpenDebtorCreditorSettings, () => IsDebtors || IsCreditors);
        CloseDebtorCreditorSettingsCommand = new RelayCommand(CloseDebtorCreditorSettings, () => _debtorCreditorSettingsVisible);
        ShowDebtorCreditorInfoCommand = new RelayCommand(ShowDebtorCreditorInfo, () => IsDebtors || IsCreditors);
        ToggleLedgerVisibilityCommand = new RelayCommand(ToggleLedgerVisibility);
        ClearModuleFiltersCommand = new RelayCommand(ClearModuleFilters);
        ShowTransactionsCommand = new RelayCommand(ShowTransactions, () => IsFinanceAccountDashboardModule && SelectedFinanceAccount is not null);
        CloseTransactionsCommand = new RelayCommand(CloseTransactions, () => TransactionsVisible);
        OpenFinanceAccountCommand = new ParameterRelayCommand(OpenFinanceAccountFromParent);
        OpenAllPaymentsCommand = new RelayCommand(OpenAllPaymentTransactions, () => IsFinanceAccountDashboardModule && SelectedFinanceAccount is not null);
        OpenAllReceiptsCommand = new RelayCommand(OpenAllReceiptTransactions, () => IsFinanceAccountDashboardModule && SelectedFinanceAccount is not null);
        OpenPaymentGroupCommand = new ParameterRelayCommand(OpenPaymentGroupTransactions, parameter => parameter is AuditGroupMovementSummary);
        OpenReceiptGroupCommand = new ParameterRelayCommand(OpenReceiptGroupTransactions, parameter => parameter is AuditGroupMovementSummary);
        OpenPartyTransactionsCommand = new ParameterRelayCommand(OpenPartyTransactions, parameter => parameter is AuditPartyMovementSummary);
        CloseGroupSummaryCommand = new RelayCommand(CloseGroupSummary, () => FinanceGroupSummaryVisible);
        OpenCashMovementCommand = new ParameterRelayCommand(OpenCashMovementTransactions);
        OpenCommerceGroupCommand = new ParameterRelayCommand(OpenCommerceGroup, parameter => parameter is AuditCommerceGroupSummary);
        OpenCommerceVoucherTypeCommand = new ParameterRelayCommand(OpenCommerceVoucherType, parameter => parameter is AuditCommerceGroupSummary);
        OpenCommercePartyCommand = new ParameterRelayCommand(OpenCommerceParty, parameter => parameter is AuditCommercePartySummary);
        OpenCommerceRateCommand = new ParameterRelayCommand(OpenCommerceRate, parameter => parameter is AuditCommerceGstRateSummary);
        OpenCommerceItemCommand = new ParameterRelayCommand(OpenCommerceItem, parameter => parameter is AuditCommerceItemSummary);
        OpenCommerceMonthCommand = new ParameterRelayCommand(OpenCommerceMonth, parameter => parameter is AuditCommerceMonthSummary);
        OpenCommerceSupplyCommand = new ParameterRelayCommand(OpenCommerceSupply, parameter => parameter is AuditCommerceSupplySummary);
        ShowCommercePartyTransactionsCommand = new RelayCommand(ShowCommercePartyTransactions, () => IsCommercePartyDetailVisible && SelectedCommerceParty is not null);
        CloseCommerceDrilldownCommand = new RelayCommand(CloseCommerceDrilldown, () => IsCommerceDrilldownActive);
        OpenRecordLookupCommand = new RelayCommand(OpenRecordLookup, () => SelectedRecord is not null);
        // R4 R9: Accounting workbench is Audit-local. It never selects/reads an Accounting client.
        // The current module plus every active drill-down filter is projected into the workbench.
        ModifyEntryCommand = new RelayCommand(OpenAuditAccountingWorkbench, CanOpenAuditAccountingWorkbench);
        SwitchToAccountingCommand = new RelayCommand(() =>
        {
            // Fix95: cross-workspace navigation always returns through the selector so
            // Audit never silently hands its client context to Accounting or vice versa.
            if (System.Windows.Application.Current is App app) app.LogoutWorkspace();
        });
        LogoutCommand = new RelayCommand(() =>
        {
            if (System.Windows.Application.Current is App app) app.LogoutWorkspace();
        });

        // Match Accounting's safe Client Directory startup: Audit never silently reopens
        // the last Audit client. The directory is visible first and the user explicitly opens
        // a client/FY before audit modules hydrate that client's data.
        LoadAuditClients();
        _selectedModule = _clientDirectoryModule;
        RaiseModuleVisibility();
    }

    public ObservableCollection<AuditClientProfile> AuditClients { get; }
    public AuditClientManagementViewModel ClientManagement { get; }
    public ObservableCollection<AuditNavigationItem> Modules { get; }
    public ObservableCollection<AuditRecordRow> Rows { get; }
    public ObservableCollection<AuditRecordRow> VisibleRows { get; }
    public ObservableCollection<AuditBankSummaryRow> BankSummaries { get; }
    public ObservableCollection<AuditSourceRow> SourceRows { get; }
    public ObservableCollection<AuditDashboardCard> DashboardCards { get; }
    public ObservableCollection<AuditStatisticRow> AuditMasterStatistics { get; }
    public ObservableCollection<AuditStatisticRow> AuditVoucherStatistics { get; }
    public ObservableCollection<AuditRecordRow> DebtorCreditorLedgerTransactions { get; }
    public ObservableCollection<AuditFinancialStatementRow> FinancialStatementRows { get; }
    public ObservableCollection<AuditFinancialStatementRow> VisibleFinancialStatementRows { get; }
    public ObservableCollection<AuditMetricTile> FinancialStatementMetrics { get; }
    public ObservableCollection<AuditModuleTab> ModuleTabs { get; }
    public ObservableCollection<AuditMetricTile> MetricTiles { get; }
    public ObservableCollection<AuditFinanceAccountSummary> FinanceAccounts { get; }
    public ObservableCollection<AuditMetricTile> FinancialDetailTiles { get; }
    public ObservableCollection<AuditGroupMovementSummary> PaymentGroupSummaries { get; }
    public ObservableCollection<AuditGroupMovementSummary> ReceiptGroupSummaries { get; }
    public ObservableCollection<AuditPartyMovementSummary> FinanceGroupPartySummaries { get; }
    public ISeries[] PaymentGroupChartSeries { get; private set; }
    public ISeries[] ReceiptGroupChartSeries { get; private set; }
    public Axis[] PaymentGroupChartXAxes { get; private set; }
    public Axis[] PaymentGroupChartYAxes { get; private set; }
    public Axis[] ReceiptGroupChartXAxes { get; private set; }
    public Axis[] ReceiptGroupChartYAxes { get; private set; }
    public ObservableCollection<AuditMetricTile> BankingStatementTiles { get; }
    public ObservableCollection<AuditMetricTile> BankingVerificationTiles { get; }
    public ObservableCollection<AuditMetricTile> BankingTransferTiles { get; }
    public ObservableCollection<AuditMetricTile> BankingPositionTiles { get; }
    public ObservableCollection<AuditMetricTile> BankingAlertTiles { get; }
    public ObservableCollection<AuditBankCashMovementSummary> CashBankMovements { get; }
    public ObservableCollection<AuditMetricTile> SelectedAccountAlertTiles { get; }
    public ObservableCollection<AuditCommerceGroupSummary> CommerceGroupSummaries { get; }
    public ObservableCollection<AuditCommerceGroupSummary> CommerceLedgerSummaries { get; }
    public ObservableCollection<AuditCommerceGroupSummary> CommerceVoucherTypeSummaries { get; }
    public ObservableCollection<AuditCommerceHsnSummary> CommerceHsnSummaries { get; }
    public ObservableCollection<AuditRecordRow> CommerceVoucherRows { get; }
    public ISeries[] CommerceMonthChartSeries { get; private set; }
    public Axis[] CommerceMonthChartXAxes { get; private set; }
    public Axis[] CommerceMonthChartYAxes { get; private set; }
    public ObservableCollection<AuditCommercePartySummary> CommercePartySummaries { get; }
    public ObservableCollection<AuditCommercePartySummary> CommerceDrilldownPartySummaries { get; }
    public ObservableCollection<AuditCommerceGstRateSummary> CommerceGstRateSummaries { get; }
    public ObservableCollection<AuditCommerceItemSummary> CommerceItemSummaries { get; }
    public ObservableCollection<AuditCommerceGroupSummary> CommerceAdjustmentSummaries { get; }
    public ObservableCollection<AuditCommerceMonthSummary> CommerceMonthSummaries { get; }
    public ObservableCollection<AuditCommerceTaxAmountSummary> CommerceTaxAmountSummaries { get; }
    public ObservableCollection<AuditCommerceSupplySummary> CommerceSupplySummaries { get; }
    public ObservableCollection<AuditRecordRow> CommerceExtraVoucherRows { get; }
    public ObservableCollection<AuditCommerceBooksReturnComparisonRow> CommerceBooksVsGstr1Rows { get; }
    public ObservableCollection<AuditCommerceBooksReturnComparisonRow> CommerceBooksVsTwoARows { get; }
    public ObservableCollection<AuditCommerceBooksReturnComparisonRow> CommerceBooksVsTwoBRows { get; }
    public ObservableCollection<AuditCommerceAlertSummary> CommerceAlertSummaries { get; }
    public ObservableCollection<AuditMetricTile> CommerceReconciliationTiles { get; }
    public ObservableCollection<AuditMetricTile> CommercePartyDetailTiles { get; }
    public ObservableCollection<AuditMetricTile> TallyAuthorityTiles { get; }
    public ObservableCollection<TallyCompanyInfo> LiveTallyCompanies { get; }
    public ObservableCollection<AuditGstr3bVerificationRow> Gstr3bVerificationRows { get; }
    public ObservableCollection<AuditEntryAdjustment> EntryAdjustments { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand NewAuditClientCommand { get; }
    public RelayCommand EditAuditClientCommand { get; }
    public RelayCommand ArchiveAuditClientCommand { get; }
    public RelayCommand CloseAuditClientCommand { get; }
    public RelayCommand OpenAuditSettingsCommand { get; }
    public RelayCommand LinkAccountingCommand { get; }
    public RelayCommand ClearAccountingLinkCommand { get; }
    public AsyncRelayCommand RefreshTallyCompaniesCommand { get; }
    public AsyncRelayCommand RefreshLinkedTallyAuthorityCommand { get; }
    public AsyncRelayCommand LinkTallyCommand { get; }
    public RelayCommand ClearTallyLinkCommand { get; }
    public RelayCommand ImportAuditBankStatementsCommand { get; }
    public ParameterRelayCommand SelectModuleTabCommand { get; }
    public ParameterRelayCommand OpenDashboardCardCommand { get; }
    public ParameterRelayCommand OpenAuditStatisticCommand { get; }
    public ParameterRelayCommand OpenDebtorCreditorLedgerCommand { get; }
    public RelayCommand CloseDebtorCreditorLedgerCommand { get; }
    public RelayCommand OpenDebtorCreditorSettingsCommand { get; }
    public RelayCommand CloseDebtorCreditorSettingsCommand { get; }
    public RelayCommand ShowDebtorCreditorInfoCommand { get; }
    public ParameterRelayCommand OpenDebtorCreditorSectionCommand { get; }
    public RelayCommand ToggleLedgerVisibilityCommand { get; }
    public RelayCommand ClearModuleFiltersCommand { get; }
    public RelayCommand ShowTransactionsCommand { get; }
    public RelayCommand CloseTransactionsCommand { get; }
    public ParameterRelayCommand OpenFinanceAccountCommand { get; }
    public RelayCommand OpenAllPaymentsCommand { get; }
    public RelayCommand OpenAllReceiptsCommand { get; }
    public ParameterRelayCommand OpenPaymentGroupCommand { get; }
    public ParameterRelayCommand OpenReceiptGroupCommand { get; }
    public ParameterRelayCommand OpenPartyTransactionsCommand { get; }
    public RelayCommand CloseGroupSummaryCommand { get; }
    public ParameterRelayCommand OpenCashMovementCommand { get; }
    public ParameterRelayCommand OpenCommerceGroupCommand { get; }
    public ParameterRelayCommand OpenCommerceVoucherTypeCommand { get; }
    public ParameterRelayCommand OpenCommercePartyCommand { get; }
    public ParameterRelayCommand OpenCommerceRateCommand { get; }
    public ParameterRelayCommand OpenCommerceItemCommand { get; }
    public ParameterRelayCommand OpenCommerceMonthCommand { get; }
    public ParameterRelayCommand OpenCommerceSupplyCommand { get; }
    public RelayCommand ShowCommercePartyTransactionsCommand { get; }
    public RelayCommand CloseCommerceDrilldownCommand { get; }
    public RelayCommand OpenRecordLookupCommand { get; }
    public RelayCommand ModifyEntryCommand { get; }
    public RelayCommand SwitchToAccountingCommand { get; }
    public RelayCommand LogoutCommand { get; }

    public AuditClientProfile? SelectedAuditClient
    {
        get => _selectedAuditClient;
        set
        {
            if (!SetProperty(ref _selectedAuditClient, value)) return;
            if (value is not null) _auditRepository.SetCurrentClient(value.Id);
            if (!_syncingClientSelection && value is not null)
            {
                _syncingClientSelection = true;
                try { ClientManagement.Reload(value.Id); } finally { _syncingClientSelection = false; }
            }
            OnPropertyChanged(nameof(CurrentClient));
            OnPropertyChanged(nameof(CurrentFinancialYear));
            OnPropertyChanged(nameof(CurrentClientDetail));
            OnPropertyChanged(nameof(HasActiveWorkspace));
            OnPropertyChanged(nameof(AccountingLinkText));
            OnPropertyChanged(nameof(TallyLinkText));
            OnPropertyChanged(nameof(TallyConnectionStatus));
            OnPropertyChanged(nameof(SelectedLiveTallyCompany));
            OnPropertyChanged(nameof(WorkspaceButtonText));
            OnPropertyChanged(nameof(CurrentPageTitle));
            OnPropertyChanged(nameof(CurrentPageSubtitle));
            RefreshLinkCommandStates();
            LoadDebtorCreditorSettings();
            _ = RefreshSelectedClientAuthorityAsync();
        }
    }

    private async Task RefreshSelectedClientAuthorityAsync()
    {
        await RefreshAsync(forceAll: false).ConfigureAwait(false);
    }

    public AuditNavigationItem? SelectedModule
    {
        get => _selectedModule;
        set
        {
            if (value is null || !SetProperty(ref _selectedModule, value)) return;
            OnPropertyChanged(nameof(SelectedClientModule));
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            _selectedModuleTab = null;
            if (value.Key is "Debtors" or "Creditors") _ledgerVisibilityMode = 2;
            _transactionsVisible = false;
            _financeGroupSummaryVisible = false;
            _debtorCreditorSettingsVisible = false;
            _debtorCreditorLedgerDrilldownVisible = false;
            DebtorCreditorLedgerTransactions.Clear();
            FinanceGroupPartySummaries.Clear();
            _selectedFinanceAccount = null;
            ResetCommerceDrilldown();
            CommerceDrilldownPartySummaries.Clear();
            CommercePartyDetailTiles.Clear();
            _selectedCommerceParty = null;
            OnPropertyChanged(nameof(SelectedCommerceParty));
            OnPropertyChanged(nameof(TransactionsVisible));
            OnPropertyChanged(nameof(FinanceGroupSummaryVisible));
            OnPropertyChanged(nameof(IsFinanceDashboardVisible));
            OnPropertyChanged(nameof(IsFinanceGroupSummaryVisible));
            OnPropertyChanged(nameof(IsFinanceTransactionsVisible));
            OnPropertyChanged(nameof(IsTransactionToolsVisible));
            SelectedRecord = null;
            RaiseModuleVisibility();
            ImportAuditBankStatementsCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CurrentPageTitle));
            OnPropertyChanged(nameof(CurrentPageSubtitle));
            if (value.Key == "FinancialStatements")
            {
                if (!ReleaseFeatureGates.FinancialStatements)
                {
                    Status = ReleaseFeatureGates.FinancialStatementsDisabledReason;
                    return;
                }
                if (SelectedAuditClient?.LinkedAccountingClientId is long sharedAccountingClientId && sharedAccountingClientId > 0)
                {
                    // R52 FIXED22: keep the Audit shell visible while binding its Financial
                    // Statements page to the SAME Accounting Client/FY FinancialStatementsViewModel.
                    _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (System.Windows.Application.Current is App app &&
                            !app.PrepareSharedFinancialStatementsForAudit(sharedAccountingClientId, out var error) &&
                            !string.IsNullOrWhiteSpace(error))
                            Status = "Shared Financial Statements could not be opened: " + error;
                    }));
                }
                else
                {
                    Status = "Link this Audit client to its Accounting Client/FY to use the shared Financial Statements authority.";
                }
            }
            _ = RefreshAsync(forceAll: false);
        }
    }

    // The sidebar contains only active-client modules. Client Directory and Audit Settings
    // are shell pages, so keep their selection out of the ListBox binding.
    public AuditNavigationItem? SelectedClientModule
    {
        get => SelectedModule is not null && Modules.Contains(SelectedModule) ? SelectedModule : null;
        set
        {
            if (value is not null) SelectedModule = value;
        }
    }

    public string CurrentClient => SelectedAuditClient?.ClientName ?? "No Audit client selected";
    public string CurrentFinancialYear => SelectedAuditClient?.FinancialYear ?? string.Empty;
    public string CurrentClientDetail => SelectedAuditClient is null
        ? "Create or select an Audit client. Accounting clients do not appear here automatically."
        : $"{SelectedAuditClient.ClientCode} · FY {SelectedAuditClient.FinancialYear}" +
          (SelectedAuditClient.Gstin.Length > 0 ? $" · GSTIN {SelectedAuditClient.Gstin}" : string.Empty);
    public bool HasActiveWorkspace => SelectedAuditClient is not null;

    public string AccountingLinkText => "Not used by Audit accounting workbench";

    public string TallyLinkText => string.IsNullOrWhiteSpace(SelectedAuditClient?.LinkedTallyCompanyKey)
        ? "Not linked"
        : SelectedAuditClient!.LinkedTallyCompanyName.Length > 0
            ? SelectedAuditClient.LinkedTallyCompanyName
            : SelectedAuditClient.LinkedTallyCompanyKey;

    public string TallyConnectionStatus => string.IsNullOrWhiteSpace(SelectedAuditClient?.LinkedTallyCompanyKey)
        ? TallyDiscoveryStatus
        : $"Linked · {TallyLinkText}";

    public TallyCompanyInfo? SelectedLiveTallyCompany
    {
        get => _selectedLiveTallyCompany;
        set => SetProperty(ref _selectedLiveTallyCompany, value);
    }

    public string TallyDiscoveryStatus
    {
        get => _tallyDiscoveryStatus;
        private set
        {
            if (!SetProperty(ref _tallyDiscoveryStatus, value)) return;
            OnPropertyChanged(nameof(TallyConnectionStatus));
        }
    }

    public string WorkspaceButtonText => HasActiveWorkspace ? $"←  {CurrentClient}" : "Client Directory";
    public string CurrentPageTitle => SelectedModule?.Title ?? "Clients";
    public string CurrentPageSubtitle => SelectedModule?.Description ?? "Audit Client Directory";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string SourceSummary
    {
        get => _sourceSummary;
        private set => SetProperty(ref _sourceSummary, value);
    }

    public bool IsDashboard => SelectedModule?.Key == "Dashboard";
    public bool IsClients => SelectedModule?.Key == "Clients";
    public bool IsBankingFinance => SelectedModule?.Key is "BankingFinance" or "Bank" or "OD" or "Loans";
    public bool IsBankingFinanceParent => SelectedModule?.Key == "BankingFinance";
    public bool IsBankOdLoanModule => SelectedModule?.Key is "Bank" or "OD" or "Loans";
    public bool IsBankOrOdAccountModule => SelectedModule?.Key is "Bank" or "OD";
    public bool IsBank => SelectedModule?.Key == "Bank";
    public bool IsOD => SelectedModule?.Key == "OD";
    public bool IsLoans => SelectedModule?.Key == "Loans";
    public bool IsCashMovementPanelVisible => IsBankOrOdAccountModule || IsCash;
    public bool IsFinanceAccountDashboardModule => SelectedModule?.Key is "Bank" or "OD" or "Loans" or "Cash";
    public bool IsFinanceDashboardVisible => IsFinanceAccountDashboardModule && !TransactionsVisible && !FinanceGroupSummaryVisible;
    public bool IsFinanceGroupSummaryVisible => IsFinanceAccountDashboardModule && FinanceGroupSummaryVisible && !TransactionsVisible;
    public bool IsFinanceTransactionsVisible => IsFinanceAccountDashboardModule && TransactionsVisible;
    public bool ShowModuleMetricStrip => !IsFinanceAccountDashboardModule;
    public bool IsBankingOverviewTab => IsBankingFinanceParent && (SelectedModuleTab?.Key is null or "Overview");
    public bool IsBankingAccountsTab => IsBankingFinanceParent && SelectedModuleTab?.Key == "Accounts";
    public bool IsBankingStatementStatusTab => IsBankingFinanceParent && SelectedModuleTab?.Key == "StatementStatus";
    public bool IsBankingBooksVsStatementTab => IsBankingFinanceParent && SelectedModuleTab?.Key == "BooksVsStatement";
    public bool IsBankingTransfersTab => IsBankingFinanceParent && SelectedModuleTab?.Key == "Transfers";
    public bool IsBankingPositionTab => IsBankingFinanceParent && SelectedModuleTab?.Key == "Position";
    public bool IsBankingAlertsTab => IsBankingFinanceParent && SelectedModuleTab?.Key == "Alerts";
    public bool IsDebtorsCreditorsParent => SelectedModule?.Key == "DebtorsCreditors";
    public bool IsDebtors => SelectedModule?.Key == "Debtors";
    public bool IsCreditors => SelectedModule?.Key == "Creditors";
    public bool IsDebtorCreditorModule => IsDebtorsCreditorsParent || IsDebtors || IsCreditors;
    public bool IsStandardStructuredRowsVisible => IsDebtors || IsCreditors;
    public bool IsDebtorCreditorLedgerListVisible => IsStandardStructuredRowsVisible && !_debtorCreditorSettingsVisible && !_debtorCreditorLedgerDrilldownVisible;
    public bool IsDebtorCreditorSettingsVisible => IsStandardStructuredRowsVisible && _debtorCreditorSettingsVisible;
    public bool IsDebtorCreditorLedgerDrilldownVisible => IsStandardStructuredRowsVisible && _debtorCreditorLedgerDrilldownVisible;
    public string DebtorCreditorLedgerDrilldownTitle => _debtorCreditorLedgerDrilldownTitle;
    public IReadOnlyList<string> DebtorCreditorPercentageTypes { get; } = ["Of Totals", "Of Debit", "Of Credit", "Of Closing Balance"];
    public IReadOnlyList<string> DebtorCreditorGroupingTypes { get; } = ["Ledger-wise", "Group-wise"];
    public IReadOnlyList<string> DebtorCreditorReportFormats { get; } = ["Condensed", "Detailed"];
    public IReadOnlyList<string> DebtorCreditorDisplayNameModes { get; } = ["Name Only", "Name + GSTIN"];
    public bool DebtorCreditorShowOpeningBalance { get => _dcShowOpeningBalance; set { if (SetProperty(ref _dcShowOpeningBalance, value)) SaveDebtorCreditorSetting("ShowOpeningBalance", value); } }
    public bool DebtorCreditorShowTransactions { get => _dcShowTransactions; set { if (SetProperty(ref _dcShowTransactions, value)) { SaveDebtorCreditorSetting("ShowTransactions", value); OnPropertyChanged(nameof(DebtorCreditorCanNetTransactions)); } } }
    public bool DebtorCreditorNetTransactionsOnly { get => _dcNetTransactionsOnly; set { if (SetProperty(ref _dcNetTransactionsOnly, value)) SaveDebtorCreditorSetting("NetTransactionsOnly", value); } }
    public bool DebtorCreditorCanNetTransactions => DebtorCreditorShowTransactions;
    public bool DebtorCreditorShowClosingBalance { get => _dcShowClosingBalance; set { if (SetProperty(ref _dcShowClosingBalance, value)) SaveDebtorCreditorSetting("ShowClosingBalance", value); } }
    public bool DebtorCreditorShowPercentages { get => _dcShowPercentages; set { if (SetProperty(ref _dcShowPercentages, value)) { SaveDebtorCreditorSetting("ShowPercentages", value); OnPropertyChanged(nameof(DebtorCreditorCanChoosePercentageType)); ApplyDebtorCreditorPercentages(); } } }
    public bool DebtorCreditorCanChoosePercentageType => DebtorCreditorShowPercentages;
    public string DebtorCreditorPercentageType { get => _dcPercentageType; set { var v=(value??"Of Totals").Trim(); if (SetProperty(ref _dcPercentageType, v)) { SaveDebtorCreditorSetting("PercentageType", v); ApplyDebtorCreditorPercentages(); } } }
    public string DebtorCreditorGroupingType { get => _dcGroupingType; set { var v=(value??"Ledger-wise").Trim(); if (SetProperty(ref _dcGroupingType, v)) SaveDebtorCreditorSetting("GroupingType", v); } }
    public string DebtorCreditorReportFormat { get => _dcReportFormat; set { var v=(value??"Detailed").Trim(); if (SetProperty(ref _dcReportFormat, v)) { SaveDebtorCreditorSetting("ReportFormat", v); OnPropertyChanged(nameof(DebtorCreditorCanExpandDetailed)); } } }
    public bool DebtorCreditorCanExpandDetailed => DebtorCreditorReportFormat.Equals("Detailed", StringComparison.OrdinalIgnoreCase);
    public bool DebtorCreditorExpandDetailed { get => _dcExpandDetailed; set { if (SetProperty(ref _dcExpandDetailed, value)) SaveDebtorCreditorSetting("ExpandDetailed", value); } }
    public string DebtorCreditorDisplayNameMode { get => _dcDisplayNameMode; set { var v=(value??"Name Only").Trim(); if (SetProperty(ref _dcDisplayNameMode, v)) { SaveDebtorCreditorSetting("DisplayNameMode", v); ApplyDebtorCreditorDisplayNames(); } } }
    public bool DebtorCreditorShowBaseCurrency { get => _dcShowBaseCurrency; set { if (SetProperty(ref _dcShowBaseCurrency, value)) SaveDebtorCreditorSetting("ShowBaseCurrency", value); } }
    public bool DebtorCreditorStripeView { get => _dcStripeView; set { if (SetProperty(ref _dcStripeView, value)) SaveDebtorCreditorSetting("StripeView", value); } }
    public bool IsTransactionToolsVisible => IsStandardStructuredRowsVisible || IsFinanceTransactionsVisible || IsCommerceTransactionsVisible;
    public bool IsAccountingWorkbenchAvailable => SelectedAuditClient is not null && SelectedModule?.Key is
        "BankingFinance" or "Bank" or "OD" or "Loans" or "Sales" or "Purchase" or "Cash" or "Debtors" or "Creditors" or "Journal" or "Tally" or "GST" or "26AS";
    public bool IsBankOrOd => SelectedModule?.Key is "BankingFinance" or "Bank" or "OD";
    public bool IsSales => SelectedModule?.Key == "Sales";
    public bool IsPurchase => SelectedModule?.Key == "Purchase";
    public bool IsCommerceModule => IsSales || IsPurchase;
    public bool IsCommerceDrilldownActive => IsCommerceModule && !string.IsNullOrWhiteSpace(_commerceDrilldownStage);
    public bool IsCommerceDashboardVisible => IsCommerceModule && !IsCommerceDrilldownActive;
    public bool IsCommerceGroupSummaryVisible => IsCommerceModule && _commerceDrilldownStage.Equals("GroupParties", StringComparison.OrdinalIgnoreCase);
    public bool IsCommercePartyDetailVisible => IsCommerceModule && _commerceDrilldownStage.Equals("PartyDetail", StringComparison.OrdinalIgnoreCase);
    public bool IsCommerceTransactionsVisible => IsCommerceModule && _commerceDrilldownStage.Equals("Transactions", StringComparison.OrdinalIgnoreCase);
    public string CommerceDrilldownTitle => _commerceDrilldownTitle;
    public string CommercePartyNoun => IsSales ? "Customer" : "Vendor";
    public string CommerceGroupNoun => IsSales ? "Sales Group / Ledger" : "Purchase / Expense Group";
    public string CommerceLedgerOverviewTitle => IsSales ? "Sales Ledgers" : "Purchase / Expense Ledgers";
    public string CommerceTaxTableTitle => IsSales ? "Output Tax Amount" : "Input Tax Amount";
    public bool IsCommerceOverviewTab => IsCommerceModule && (SelectedModuleTab?.Key is null or "Overview");
    public bool IsCommerceGroupsTab => IsCommerceModule && SelectedModuleTab?.Key is "SalesGroups" or "PurchaseGroups";
    public bool IsCommerceVoucherTypesTab => IsCommerceModule && SelectedModuleTab?.Key == "VoucherTypes";
    public bool IsCommerceGstTab => IsCommerceModule && SelectedModuleTab?.Key is "GSTAnalysis" or "GstItc";
    public bool IsCommerceItemsTab => IsCommerceModule && SelectedModuleTab?.Key == "Items";
    public bool IsCommerceHsnTab => IsCommerceModule && SelectedModuleTab?.Key == "HSN";
    public bool IsCommerceAdjustmentsTab => IsCommerceModule && SelectedModuleTab?.Key is "Returns" or "Notes";
    public bool IsCommerceBooksVsGstr1Tab => IsSales && SelectedModuleTab?.Key == "BooksVsGstr1";
    public bool IsCommerceBooksVs2ATab => IsPurchase && SelectedModuleTab?.Key == "BooksVs2A";
    public bool IsCommerceBooksVs2BTab => IsPurchase && SelectedModuleTab?.Key == "BooksVs2B";
    // Retained compatibility name for inherited Fix107 presentation logic.
    public bool IsCommerceBooksVsTaxTab => IsCommerceBooksVsGstr1Tab || IsCommerceBooksVs2ATab || IsCommerceBooksVs2BTab;
    public bool IsCommerceRcmTab => IsPurchase && SelectedModuleTab?.Key == "RCM";
    public bool IsCommerceVoucherRegisterTab => IsCommerceModule && SelectedModuleTab?.Key == "VoucherRegister";
    public bool IsCommerceExtraTab => IsCommerceModule && SelectedModuleTab?.Key == "ExtraVouchers";
    public bool IsCommerceAlertsTab => IsCommerceModule && SelectedModuleTab?.Key == "Alerts";
    public bool IsCash => SelectedModule?.Key == "Cash";
    public bool IsDebtorsCreditors => IsDebtorCreditorModule;
    public bool IsBalanceSheet => SelectedModule?.Key == "BalanceSheet";
    public bool IsProfitLoss => SelectedModule?.Key == "ProfitLoss";
    public bool IsFinancialStatements => SelectedModule?.Key == "FinancialStatements";
    public bool IsFinancialStatementModule => IsBalanceSheet || IsProfitLoss || IsFinancialStatements;
    public bool IsLedgerVisibilityFilterVisible => IsDebtors || IsCreditors || IsFinancialStatementModule;
    public string LedgerVisibilityFilterText => _ledgerVisibilityMode switch
    {
        1 => "Filter: Hide 0 Balance",
        2 => "Filter: Hide 0 Transactions",
        _ => "Filter: Show All"
    };
    public string LedgerVisibilityFilterHint => _ledgerVisibilityMode switch
    {
        1 => "Effective zero-balance Ledgers are hidden: explicit Closing 0.00, or missing Closing with Opening 0.00 and no FY activity. Click for zero-transaction mode.",
        2 => "Ledgers with no Audit-owned FY voucher activity are hidden. Click to show all.",
        _ => "All Ledgers are visible. Click to hide zero closing balances."
    };
    public string DebtorCountText => Rows.Count(row => row.Category.Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase)).ToString("N0", IndianCulture);
    public string CreditorCountText => Rows.Count(row => row.Category.Equals("Sundry Creditors", StringComparison.OrdinalIgnoreCase)).ToString("N0", IndianCulture);
    public string DebtorClosingText => FormatMoney(Rows.Where(row => row.Category.Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase)).Sum(row => Math.Max(0m, row.ClosingBalance ?? 0m)));
    public string CreditorClosingText => FormatMoney(Rows.Where(row => row.Category.Equals("Sundry Creditors", StringComparison.OrdinalIgnoreCase)).Sum(row => Math.Max(0m, -(row.ClosingBalance ?? 0m))));
    public bool IsStructuredAuditModule => SelectedModule?.Key is "BankingFinance" or "Bank" or "OD" or "Loans" or "Sales" or "Purchase" or "Cash" or "DebtorsCreditors" or "Debtors" or "Creditors";
    public bool IsGstModule => SelectedModule?.Key == "GST";
    public bool IsSources => SelectedModule?.Key == "Sources";
    public bool IsSettings => SelectedModule?.Key == "Settings";
    public bool IsClientReconciliation => SelectedModule?.Key == "ClientReconciliation";
    public bool IsGenericDataModule => SelectedModule is not null &&
        SelectedModule.Key is "Journal" or "Tally" or "GST" or "26AS";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
            ApplyRowFilter();
        }
    }

    public AuditModuleTab? SelectedModuleTab
    {
        get => _selectedModuleTab;
        private set
        {
            if (!SetProperty(ref _selectedModuleTab, value)) return;
            foreach (var tab in ModuleTabs) tab.IsSelected = ReferenceEquals(tab, value);
            RaiseBankingTabVisibility();
            RaiseCommerceTabVisibility();
            ApplyRowFilter();
        }
    }

    public AuditRecordRow? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (!SetProperty(ref _selectedRecord, value)) return;
            OpenRecordLookupCommand.RaiseCanExecuteChanged();
            ModifyEntryCommand.RaiseCanExecuteChanged();
        }
    }

    public AuditFinanceAccountSummary? SelectedFinanceAccount
    {
        get => _selectedFinanceAccount;
        set
        {
            if (!SetProperty(ref _selectedFinanceAccount, value)) return;
            foreach (var account in FinanceAccounts) account.IsSelected = value is not null && account.Account.Equals(value.Account, StringComparison.OrdinalIgnoreCase);
            _transactionsVisible = false;
            _financeGroupSummaryVisible = false;
            FinanceGroupPartySummaries.Clear();
            ResetFinanceDrilldown();
            OnPropertyChanged(nameof(TransactionsVisible));
            OnPropertyChanged(nameof(FinanceGroupSummaryVisible));
            OnPropertyChanged(nameof(IsFinanceDashboardVisible));
            OnPropertyChanged(nameof(IsFinanceGroupSummaryVisible));
            OnPropertyChanged(nameof(IsFinanceTransactionsVisible));
            OnPropertyChanged(nameof(IsTransactionToolsVisible));
            RebuildSelectedFinanceAccountPresentation();
            ShowTransactionsCommand.RaiseCanExecuteChanged();
            OpenAllPaymentsCommand.RaiseCanExecuteChanged();
            OpenAllReceiptsCommand.RaiseCanExecuteChanged();
            CloseTransactionsCommand.RaiseCanExecuteChanged();
            CloseGroupSummaryCommand.RaiseCanExecuteChanged();
            ApplyRowFilter();
        }
    }

    public AuditCommercePartySummary? SelectedCommerceParty
    {
        get => _selectedCommerceParty;
        private set
        {
            if (!SetProperty(ref _selectedCommerceParty, value)) return;
            ShowCommercePartyTransactionsCommand.RaiseCanExecuteChanged();
        }
    }

    public bool TransactionsVisible => _transactionsVisible;
    public bool FinanceGroupSummaryVisible => _financeGroupSummaryVisible;
    public string FinanceGroupSummaryTitle => _financeGroupSummaryTitle;
    public string FinanceGroupSummaryDirection => _financeGroupSummaryDirection;
    public string FinanceTransactionDrilldownTitle => _financeTransactionDrilldownTitle;
    public string FinanceTransactionsCloseText => _financeGroupSummaryVisible ? "Back to Group Summary" : "Close Transactions";

    private static string AuditSourceStamp() =>
        WorkspaceCacheRevisionStore.AuditSourceStamp();

    private static string AuditCacheKey(AuditClientProfile client, string module) =>
        $"{client.Id}\u001f{client.FinancialYear.Trim().ToUpperInvariant()}\u001f{module}";

    private static readonly string[] PreparedAuditModules =
    [
        "Dashboard", "BankingFinance", "Bank", "OD", "Loans", "Sales", "Purchase",
        "Cash", "DebtorsCreditors", "Debtors", "Creditors", "BalanceSheet", "ProfitLoss", "Journal", "Tally", "GST", "26AS",
        "ClientReconciliation", "Sources"
    ];

    public void StartBackgroundPreparation(bool invalidate = false)
    {
        if (_disposed) return;
        if (!invalidate && Interlocked.Exchange(ref _backgroundPreparationStarted, 1) != 0) return;
        // R44: Audit projections are independent presentation data, not the Accounting
        // Client/FY snapshot synchronization authority. Do not prepare 15 modules for every
        // Audit client while the Accounting workspace is open. The selected Audit client/module
        // is promoted on RefreshAsync; source changes only refresh that selected scope.
        var client = SelectedAuditClient?.Clone();
        if (client is null) return;
        var module = SelectedModule?.Key ?? "Dashboard";
        RegisterAuditProjection(client, module, invalidate, priority: false);
    }

    private void RegisterAuditProjection(AuditClientProfile client, string module, bool invalidate = false, bool priority = false)
    {
        var captured = client.Clone();
        var key = AuditCacheKey(captured, module);
        _preparedClients[key] = captured;
        _preparedAudit.Register(key, () => BuildSnapshot(module, captured), invalidate, priority);
    }

    private void InvalidateAuditClient(AuditClientProfile? client)
    {
        if (client is null || _disposed) return;
        foreach (var module in PreparedAuditModules)
            RegisterAuditProjection(client, module, invalidate: true, priority: true);
    }

    private void OnPreparedAuditReady(string key, long revision)
    {
        if (_disposed || !_preparedAudit.IsCurrent(key, revision)) return;
        _ = DispatchAsync(() =>
        {
            if (_disposed || !_presentationActive || !_preparedAudit.IsCurrent(key, revision)) return;
            var current = SelectedAuditClient;
            if (current is null || !key.Equals(AuditCacheKey(current, SelectedModule?.Key ?? "Dashboard"), StringComparison.OrdinalIgnoreCase)) return;
            if (_preparedAudit.TryGet(key, out var snapshot)) ApplySnapshot(snapshot);
        });
    }

    private void OnPreparedAuditFailed(string key, string error)
    {
        if (_disposed) return;
        _ = DispatchAsync(() =>
        {
            if (_disposed || !_presentationActive) return;
            var client = SelectedAuditClient;
            if (client is not null && key.Equals(AuditCacheKey(client, SelectedModule?.Key ?? "Dashboard"), StringComparison.OrdinalIgnoreCase))
                Status = "Audit preparation failed; last saved view retained. " + error;
        });
    }

    public async Task PrepareForDisplayAsync()
    {
        _presentationActive = true;
        StartBackgroundPreparation();
        await RefreshAsync(forceAll: false).ConfigureAwait(false);
        await DispatchAsync(() =>
        {
            if (!_disposed && _presentationActive && !_auditTallyHeartbeatTimer.IsEnabled)
                _auditTallyHeartbeatTimer.Start();
        }).ConfigureAwait(false);
    }

    private async Task EnsureExistingTallyAuthorityAsync(bool forceLiveRefresh)
    {
        var client = SelectedAuditClient?.Clone();
        if (client is null || string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)) return;

        await _tallyAuthorityRefreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureLinkedTallyEvidenceInitialized();
            var (from, to) = AdvancedClosingEntryPlanner.FinancialYearRange(client.FinancialYear);
            var cachedLedgers = _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
            var cachedGroups = _auditRepository.GetTallyGroupParents(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
            var cachedVouchers = _tallyRepository.GetVoucherIndexRange(client.LinkedTallyCompanyKey, client.FinancialYear, from, to);
            var classifiedMasters = CountClassifiableTallyMasters(cachedLedgers, cachedGroups);
            var ledgerParentsForBalance = cachedLedgers
                .Where(item => !string.IsNullOrWhiteSpace(item.DisplayName))
                .GroupBy(item => item.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Parent.Trim(), StringComparer.OrdinalIgnoreCase);
            var needsFinanceBalanceAuthority = cachedLedgers.Any(ledger =>
                (ClassifyLedgerByGroup(ledger.DisplayName, ledgerParentsForBalance, cachedGroups) is "Bank" or "OD" or "Loans" or "Cash") &&
                (!ledger.OpeningBalance.HasValue || !ledger.ClosingBalance.HasValue));
            var needsMasters = cachedLedgers.Count == 0 || cachedGroups.Count == 0 || classifiedMasters == 0 || needsFinanceBalanceAuthority;
            var needsVouchers = cachedVouchers.Count == 0;

            if (!forceLiveRefresh && !needsMasters && !needsVouchers)
            {
                IReadOnlyList<TallyCompanyInfo> liveCompanies = Array.Empty<TallyCompanyInfo>();
                try { liveCompanies = await _auditTallyLinkService.DiscoverOpenCompaniesAsync().ConfigureAwait(false); } catch { }
                var live = FindLinkedLiveCompany(client, liveCompanies);
                await DispatchAsync(() =>
                {
                    if (liveCompanies.Count > 0) LiveTallyCompanies.ReplaceWith(liveCompanies);
                    if (live is not null) SelectedLiveTallyCompany = live;
                    TallyDiscoveryStatus = $"Linked · {FormatCount(cachedVouchers.Count)} FY vouchers · {FormatCount(classifiedMasters)} classified masters";
                    Status = $"Tally Audit authority ready · {FormatCount(cachedVouchers.Count)} FY voucher(s) · {FormatCount(classifiedMasters)} Bank/OD/Loan/Cash/party Master(s).";
                }).ConfigureAwait(false);
                return;
            }

            var companies = await _auditTallyLinkService.DiscoverOpenCompaniesAsync().ConfigureAwait(false);
            var company = FindLinkedLiveCompany(client, companies);
            await DispatchAsync(() =>
            {
                LiveTallyCompanies.ReplaceWith(companies);
                SelectedLiveTallyCompany = company ?? companies.FirstOrDefault();
            }).ConfigureAwait(false);
            if (company is null)
            {
                await DispatchAsync(() =>
                {
                    TallyDiscoveryStatus = "Linked · Tally company not currently open";
                    Status = $"Tally remains linked, but the linked company is not open. Cached authority: {FormatCount(cachedVouchers.Count)} voucher(s), {FormatCount(classifiedMasters)} classified Master(s).";
                }).ConfigureAwait(false);
                return;
            }

            // CompanyKey includes FY/company identity and can legitimately change after an older
            // link/discovery implementation. Rebind by the saved exact company name when the
            // live company is clearly the same company, then hydrate under the current key.
            if (!company.CompanyKey.Equals(client.LinkedTallyCompanyKey, StringComparison.OrdinalIgnoreCase))
            {
                var updated = client.Clone();
                updated.LinkedTallyCompanyKey = company.CompanyKey;
                updated.LinkedTallyCompanyName = company.Name;
                var saved = _auditRepository.SaveClient(updated);
                client = saved.Clone();
                await DispatchAsync(() => LoadAuditClients(saved.Id)).ConfigureAwait(false);
                needsMasters = true;
                needsVouchers = true;
            }

            await DispatchAsync(() => TallyDiscoveryStatus = forceLiveRefresh ? "Refreshing linked Tally authority…" : "Repairing linked Tally authority…").ConfigureAwait(false);
            if (forceLiveRefresh || needsVouchers)
            {
                await _auditTallyLinkService.RefreshVoucherAuthorityAsync(client.Id, company, client.FinancialYear).ConfigureAwait(false);
            }
            else if (needsMasters)
            {
                await _auditTallyLinkService.RefreshMasterAuthorityAsync(client.Id, company, client.FinancialYear).ConfigureAwait(false);
            }

            var ledgers = _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, company.CompanyKey);
            var groups = _auditRepository.GetTallyGroupParents(client.Id, client.FinancialYear, company.CompanyKey);
            var vouchers = _tallyRepository.GetVoucherIndexRange(company.CompanyKey, client.FinancialYear, from, to);
            var classified = CountClassifiableTallyMasters(ledgers, groups);
            await DispatchAsync(() =>
            {
                TallyDiscoveryStatus = $"Linked · {FormatCount(vouchers.Count)} FY vouchers · {FormatCount(classified)} classified masters";
                Status = $"Tally refreshed · {FormatCount(vouchers.Count)} FY voucher(s), {FormatCount(ledgers.Count)} Ledger Master(s), {FormatCount(classified)} classified into Audit modules.";
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await DispatchAsync(() =>
            {
                TallyDiscoveryStatus = "Linked · authority refresh needs attention";
                Status = $"Tally remains linked; Audit authority refresh failed: {exception.Message}";
            }).ConfigureAwait(false);
        }
        finally
        {
            _tallyAuthorityRefreshGate.Release();
        }
    }

    private static TallyCompanyInfo? FindLinkedLiveCompany(AuditClientProfile client, IReadOnlyList<TallyCompanyInfo> companies)
    {
        var exactKey = companies.FirstOrDefault(item => item.CompanyKey.Equals(client.LinkedTallyCompanyKey, StringComparison.OrdinalIgnoreCase));
        if (exactKey is not null) return exactKey;
        var savedName = (client.LinkedTallyCompanyName ?? string.Empty).Trim();
        if (savedName.Length == 0) return null;
        var byNameAndFy = companies.FirstOrDefault(item =>
            item.Name.Trim().Equals(savedName, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(item.FinancialYear) || item.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)));
        return byNameAndFy ?? companies.FirstOrDefault(item => item.Name.Trim().Equals(savedName, StringComparison.OrdinalIgnoreCase));
    }

    private static int CountClassifiableTallyMasters(
        IReadOnlyList<AuditTallyLedgerAuthority> ledgers,
        IReadOnlyDictionary<string, string> groupParents)
    {
        if (ledgers.Count == 0) return 0;
        var ledgerParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ledger in ledgers)
        {
            if (!string.IsNullOrWhiteSpace(ledger.DisplayName)) ledgerParents[ledger.DisplayName.Trim()] = (ledger.Parent ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(ledger.ExactName)) ledgerParents[ledger.ExactName.Trim()] = (ledger.Parent ?? string.Empty).Trim();
        }
        return ledgers.Count(ledger =>
            ClassifyLedgerByGroup(ledger.DisplayName, ledgerParents, groupParents) is "Bank" or "OD" or "Loans" or "Cash" or "Sundry Debtors" or "Sundry Creditors");
    }

    public void PauseForBackground()
    {
        var client = SelectedAuditClient?.Clone();
        if (client is not null)
        {
            // The window is only a subscriber. On close, ensure any stale Audit projection
            // for this Client/FY is promoted; current projections do not perform another read.
            foreach (var module in PreparedAuditModules)
                RegisterAuditProjection(client, module, invalidate: false, priority: true);
        }
        _presentationActive = false;
        _auditTallyHeartbeatTimer.Stop();
        Interlocked.Increment(ref _refreshVersion);
    }

    public async Task RefreshAsync(bool forceAll = false)
    {
        if (_disposed || (!_presentationActive && !forceAll)) return;
        var version = Interlocked.Increment(ref _refreshVersion);
        var module = SelectedModule?.Key ?? "Dashboard";
        var client = SelectedAuditClient?.Clone();

        if (client is null)
        {
            await DispatchAsync(() =>
            {
                if (version != Volatile.Read(ref _refreshVersion)) return;
                Rows.Clear();
                VisibleRows.Clear();
                BankSummaries.Clear();
                ModuleTabs.Clear();
                MetricTiles.Clear();
                SelectedRecord = null;
                DashboardCards.ReplaceWith(new[]
                {
                    new AuditDashboardCard("Audit Clients", AuditClients.Count, "Independent Audit client directory"),
                    new AuditDashboardCard("Selected Client", 0, "Create or select an Audit client to begin"),
                    new AuditDashboardCard("Accounting Workbench", 0, "No Audit client selected"),
                    new AuditDashboardCard("Tally Link", 0, "Link a Tally company explicitly when required")
                });
                SourceRows.ReplaceWith(BuildSources(null));
                SourceSummary = "No Audit client selected · Accounting and Audit remain isolated.";
                Status = "Create or select an Audit client.";
                UpdateModuleCounts(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            }).ConfigureAwait(false);
            return;
        }

        try
        {
            var key = AuditCacheKey(client, module);
            if (_preparedAudit.TryGet(key, out var snapshot))
            {
                await DispatchAsync(() =>
                {
                    if (_disposed || !_presentationActive || version != Volatile.Read(ref _refreshVersion)) return;
                    var current = SelectedAuditClient;
                    if (current is null || !key.Equals(AuditCacheKey(current, SelectedModule?.Key ?? "Dashboard"), StringComparison.OrdinalIgnoreCase)) return;
                    ApplySnapshot(snapshot);
                }).ConfigureAwait(false);
            }
            // A manual refresh invalidates the selected stream; ordinary navigation only
            // promotes missing work. Neither path performs SQLite reads on the UI thread.
            RegisterAuditProjection(client, module, invalidate: forceAll, priority: true);
        }
        catch (Exception ex)
        {
            await DispatchAsync(() => Status = $"Audit refresh could not complete: {ex.Message}").ConfigureAwait(false);
        }
    }

    private AuditSnapshot BuildSnapshot(string module, AuditClientProfile client)
    {
        var requestedModule = module;
        var dataModule = module is "Bank" or "OD" or "Loans"
            ? "BankingFinance"
            : module is "Debtors" or "Creditors"
                ? "DebtorsCreditors"
                : module;
        var snapshot = new AuditSnapshot { Module = requestedModule };
        var counts = _auditRepository.GetModuleCounts(client.Id, client.FinancialYear)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var auditStoredCount = counts.Values.Sum();

        var storageModules = dataModule switch
        {
            "BankingFinance" => new[] { "BankingFinance", "Banking", "OD", "Loans" },
            "DebtorsCreditors" => new[] { "DebtorsCreditors", "Debtors", "Creditors" },
            _ => new[] { module }
        };
        var storedRecords = dataModule is "Dashboard" or "Clients" or "Sources" or "Settings" or "ClientReconciliation" or "BalanceSheet" or "ProfitLoss"
            ? Array.Empty<AuditStoredRecord>()
            : storageModules
                .SelectMany(key => _auditRepository.GetRecords(client.Id, client.FinancialYear, key))
                .ToArray();
        var local = storedRecords
            .Where(row => !row.OriginKind.Equals("AccountingWorkbenchOverride", StringComparison.OrdinalIgnoreCase) &&
                          !row.OriginKind.Equals("AccountingWorkbenchDelete", StringComparison.OrdinalIgnoreCase))
            .Select(ToAuditRow)
            .ToArray();
        snapshot.Rows = local;

        if (dataModule == "BankingFinance")
            snapshot.Rows = snapshot.Rows.Concat(BuildAuditOwnedBankStatementRows(client)).ToArray();

        if (!string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey) &&
            dataModule is "BankingFinance" or "Sales" or "Purchase" or "Cash" or "DebtorsCreditors" or "Journal" or "Tally")
        {
            snapshot.Rows = snapshot.Rows.Concat(BuildExplicitTallyRows(client, dataModule)).ToArray();
        }

        if (storedRecords.Length > 0)
            snapshot.Rows = ApplyAccountingWorkbenchOverlays(storedRecords, snapshot.Rows);

        if (requestedModule.Equals("Debtors", StringComparison.OrdinalIgnoreCase))
            snapshot.Rows = snapshot.Rows.Where(row => row.Category.Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase)).ToArray();
        else if (requestedModule.Equals("Creditors", StringComparison.OrdinalIgnoreCase))
            snapshot.Rows = snapshot.Rows.Where(row => row.Category.Equals("Sundry Creditors", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (requestedModule is "Debtors" or "Creditors")
            snapshot.DebtorCreditorVoucherRows = BuildDebtorCreditorVoucherCache(client, snapshot.Rows.Select(row => row.PartyOrLedger));

        if (dataModule == "BankingFinance")
        {
            ApplyAuditInterBankStatementConfirmation(snapshot.Rows);
            var auditOwned = BuildAuditOwnedBankSummaries(client, snapshot.Rows);
            snapshot.BankSummaries = MergeBankSummaries(auditOwned, snapshot.BankSummaries);
        }

        if (dataModule is "Sales" or "Purchase")
            EnrichCommercePartyGstinFromMasterAuthority(client, snapshot.Rows);

        ApplyFix97StatutoryRulePack(dataModule, snapshot.Rows);

        if (requestedModule is "Bank" or "OD" or "Loans")
        {
            snapshot.Rows = snapshot.Rows
                .Where(row => row.Category.Equals(requestedModule, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            snapshot.BankSummaries = snapshot.BankSummaries
                .Where(summary => BankSummaryBelongsToCategory(summary, requestedModule, client))
                .ToArray();
        }

        snapshot.Rows = snapshot.Rows
            .OrderBy(row => row.Date ?? DateTime.MinValue)
            .ThenBy(row => row.Reference, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.AuthorityId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.PartyOrLedger, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        snapshot.Sources = BuildSources(client);
        snapshot.ModuleCounts = counts;
        EnrichModuleCountsFromTallyAuthority(client, snapshot.ModuleCounts);
        // Fix99 compatibility proof only: BuildDashboardCards(client, snapshot.ModuleCounts)
        if (dataModule == "Dashboard")
        {
            snapshot.DashboardCards = BuildDashboardCards(client, snapshot.ModuleCounts, auditStoredCount);
            var statistics = BuildAuditStatistics(client, auditStoredCount);
            snapshot.MasterStatistics = statistics.Masters;
            snapshot.VoucherStatistics = statistics.Vouchers;
        }
        if (dataModule is "BalanceSheet" or "ProfitLoss")
            snapshot.FinancialStatementRows = BuildFinancialStatementRows(client, dataModule);

        // Sidebar counts represent the actual active presentation whenever that module has
        // been hydrated. Older AuditRecords keys remain supported without forcing migration.
        foreach (var key in new[] { "BankingFinance", "Sales", "Purchase", "Cash", "DebtorsCreditors", "Debtors", "Creditors", "Journal", "Tally", "GST", "26AS" })
        {
            var storedFallback = key switch
            {
                "BankingFinance" => counts.GetValueOrDefault("Banking") + counts.GetValueOrDefault("OD") + counts.GetValueOrDefault("Loans"),
                "DebtorsCreditors" => counts.GetValueOrDefault("Debtors") + counts.GetValueOrDefault("Creditors"),
                "Debtors" => counts.GetValueOrDefault("Debtors"),
                "Creditors" => counts.GetValueOrDefault("Creditors"),
                _ => counts.GetValueOrDefault(key)
            };
            var count = key == requestedModule ? snapshot.Rows.Length : storedFallback;
            if (count > 0) snapshot.ModuleCounts[key] = count;
        }
        return snapshot;
    }

    private static AuditRecordRow[] ApplyAccountingWorkbenchOverlays(
        IReadOnlyCollection<AuditStoredRecord> storedRecords,
        IReadOnlyCollection<AuditRecordRow> sourceRows)
    {
        var overlays = storedRecords
            .Where(row => (row.OriginKind.Equals("AccountingWorkbenchOverride", StringComparison.OrdinalIgnoreCase) ||
                           row.OriginKind.Equals("AccountingWorkbenchDelete", StringComparison.OrdinalIgnoreCase)) &&
                          !string.IsNullOrWhiteSpace(row.OriginKey))
            .GroupBy(row => row.OriginKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Id).Last(), StringComparer.OrdinalIgnoreCase);
        if (overlays.Count == 0) return sourceRows.ToArray();

        var result = new List<AuditRecordRow>(sourceRows.Count + overlays.Count);
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourceRows)
        {
            // Every source-backed Audit row may receive a separate workbench override/tombstone.
            // The source row itself remains immutable; only AccountingWorkbenchCreate rows are
            // direct workbench-owned entries and normally have no overlay identity targeting them.
            var key = AccountingWorkbenchSourceIdentity(source);
            if (key.Length > 0 && overlays.TryGetValue(key, out var overlay))
            {
                matched.Add(key);
                if (overlay.OriginKind.Equals("AccountingWorkbenchDelete", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(ToAuditRow(overlay));
                continue;
            }
            result.Add(source);
        }

        // If source evidence is temporarily unavailable, retain the last independent Audit
        // override rather than silently dropping the user's Audit working entry.
        foreach (var pair in overlays)
        {
            if (matched.Contains(pair.Key) || pair.Value.OriginKind.Equals("AccountingWorkbenchDelete", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(ToAuditRow(pair.Value));
        }
        return result.ToArray();
    }

    public static string AccountingWorkbenchSourceIdentity(AuditRecordRow row)
    {
        if (row is null) return string.Empty;
        if (!string.IsNullOrWhiteSpace(row.TallyMasterId)) return $"TALLY:{row.TallyMasterId.Trim()}";
        if (!string.IsNullOrWhiteSpace(row.AuthorityId)) return $"{row.Source.Trim()}:{row.AuthorityId.Trim()}";
        var date = row.Date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty;
        var reference = (row.Reference ?? string.Empty).Trim();
        var party = (row.PartyOrLedger ?? string.Empty).Trim();
        var amount = row.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var source = (row.Source ?? string.Empty).Trim();
        if (date.Length == 0 && reference.Length == 0 && party.Length == 0) return string.Empty;
        return $"{source}:{date}:{reference}:{party}:{amount}";
    }

    private void EnsureLinkedAccountingEvidenceInitialized()
    {
        _accountingClientRepository.Initialize();
        _bankingRepository.Initialize();
        _salesRepository.Initialize();
        _purchaseRepository.Initialize();
        _journalRepository.Initialize();
        _gstRepository.Initialize();
        _masterRepository.Initialize();
        _taxCreditRepository.Initialize();
    }

    private void EnsureLinkedTallyEvidenceInitialized()
    {
        _tallyRepository.Initialize();
    }

    private void EnrichModuleCountsFromTallyAuthority(AuditClientProfile client, IDictionary<string, int> counts)
    {
        if (string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)) return;
        var ledgers = _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
        var groups = _auditRepository.GetTallyGroupParents(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
        if (ledgers.Count == 0) return;
        var ledgerParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ledger in ledgers)
        {
            if (!string.IsNullOrWhiteSpace(ledger.DisplayName)) ledgerParents[ledger.DisplayName.Trim()] = (ledger.Parent ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(ledger.ExactName)) ledgerParents[ledger.ExactName.Trim()] = (ledger.Parent ?? string.Empty).Trim();
        }
        var classified = ledgers
            .Select(ledger => ClassifyLedgerByGroup(ledger.DisplayName, ledgerParents, groups))
            .Where(category => category.Length > 0)
            .GroupBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        counts["Bank"] = classified.GetValueOrDefault("Bank");
        counts["OD"] = classified.GetValueOrDefault("OD");
        counts["Loans"] = classified.GetValueOrDefault("Loans");
        counts["BankingFinance"] = counts["Bank"] + counts["OD"] + counts["Loans"];
        counts["Debtors"] = classified.GetValueOrDefault("Sundry Debtors");
        counts["Creditors"] = classified.GetValueOrDefault("Sundry Creditors");
        counts["DebtorsCreditors"] = counts["Debtors"] + counts["Creditors"];
        var statementRows = BuildFinancialStatementRows(client, "All");
        counts["BalanceSheet"] = statementRows.Count(row => row.Statement.Equals("Balance Sheet", StringComparison.OrdinalIgnoreCase) && !row.IsGroupSummary);
        counts["ProfitLoss"] = statementRows.Count(row => row.Statement.Equals("Profit & Loss", StringComparison.OrdinalIgnoreCase) && !row.IsGroupSummary);

        EnsureLinkedTallyEvidenceInitialized();
        var (from, to) = AdvancedClosingEntryPlanner.FinancialYearRange(client.FinancialYear);
        var vouchers = _tallyRepository.GetVoucherIndexRange(client.LinkedTallyCompanyKey, client.FinancialYear, from, to);
        // Fix100 common authority count: Banking & Finance represents the same projected finance movement rows
        // used by the parent module, while Bank/OD/Loans continue to show classified account-master counts.
        counts["BankingFinance"] = vouchers.Sum(voucher =>
        {
            var ledgerNames = SplitLedgerNames(voucher.DebitLedgers)
                .Concat(SplitLedgerNames(voucher.CreditLedgers))
                .Append((voucher.PartyLedger ?? string.Empty).Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return ledgerNames.Count(name =>
            {
                var category = ClassifyLedgerByGroup(name, ledgerParents, groups);
                return category is "Bank" or "OD" or "Loans";
            });
        });
        counts["Tally"] = vouchers.Count;
        counts["Sales"] = vouchers.Count(voucher =>
        {
            var type = (voucher.VoucherType ?? string.Empty).Trim();
            return type.Equals("Sales", StringComparison.OrdinalIgnoreCase) || type.Contains("Credit Note", StringComparison.OrdinalIgnoreCase);
        });
        counts["Purchase"] = vouchers.Count(voucher =>
        {
            var type = (voucher.VoucherType ?? string.Empty).Trim();
            return type.Equals("Purchase", StringComparison.OrdinalIgnoreCase) || type.Contains("Debit Note", StringComparison.OrdinalIgnoreCase);
        });
        counts["Journal"] = vouchers.Count(voucher =>
        {
            var type = (voucher.VoucherType ?? string.Empty).Trim();
            return type.Contains("Journal", StringComparison.OrdinalIgnoreCase) || type.Contains("Memorandum", StringComparison.OrdinalIgnoreCase);
        });
        counts["Cash"] = vouchers.Count(voucher => SplitLedgerNames(voucher.DebitLedgers)
            .Concat(SplitLedgerNames(voucher.CreditLedgers))
            .Append((voucher.PartyLedger ?? string.Empty).Trim())
            .Any(name => name.Length > 0 && ClassifyLedgerByGroup(name, ledgerParents, groups).Equals("Cash", StringComparison.OrdinalIgnoreCase)));
    }

    private void EnrichModuleCountsFromAccountingAuthority(AuditClientProfile client, IDictionary<string, int> counts)
    {
        if (client.LinkedAccountingClientId is not > 0) return;
        EnsureLinkedAccountingEvidenceInitialized();
        var clientId = client.LinkedAccountingClientId.Value;
        var fy = client.FinancialYear;
        counts["Sales"] = Math.Max(GetMutableCount(counts, "Sales"), _salesRepository.GetRecords(clientId, fy).Count);
        counts["Purchase"] = Math.Max(GetMutableCount(counts, "Purchase"), _purchaseRepository.GetRecords(clientId, fy).Count);
        counts["Cash"] = Math.Max(GetMutableCount(counts, "Cash"), _bankingRepository.GetCashEntries(clientId, fy).Count);
        counts["Journal"] = Math.Max(GetMutableCount(counts, "Journal"), _journalRepository.GetVouchers(clientId, fy).Count);
        counts["GST"] = Math.Max(GetMutableCount(counts, "GST"), _gstRepository.GetReturns(clientId, fy).Count);
        counts["26AS"] = Math.Max(GetMutableCount(counts, "26AS"), _taxCreditRepository.GetCurrentRows(clientId, fy).Count);
        counts["BankingFinance"] = Math.Max(GetMutableCount(counts, "BankingFinance"), _bankingRepository.GetTransactions(clientId, fy, includeArchived: false).Count);

        var masters = _masterRepository.Get(clientId, fy, includeArchived: false);
        var ledgerParents = masters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && master.Name.Trim().Length > 0)
            .GroupBy(master => master.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Parent.Trim(), StringComparer.OrdinalIgnoreCase);
        var groupParents = masters
            .Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && master.Name.Trim().Length > 0)
            .GroupBy(master => master.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Parent.Trim(), StringComparer.OrdinalIgnoreCase);
        var classified = ledgerParents.Keys
            .Select(name => ClassifyLedgerByGroup(name, ledgerParents, groupParents))
            .Where(value => value.Length > 0)
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        counts["Bank"] = Math.Max(GetMutableCount(counts, "Bank"), classified.GetValueOrDefault("Bank"));
        counts["OD"] = Math.Max(GetMutableCount(counts, "OD"), classified.GetValueOrDefault("OD"));
        counts["Loans"] = Math.Max(GetMutableCount(counts, "Loans"), classified.GetValueOrDefault("Loans"));
        counts["Debtors"] = Math.Max(GetMutableCount(counts, "Debtors"), classified.GetValueOrDefault("Sundry Debtors"));
        counts["Creditors"] = Math.Max(GetMutableCount(counts, "Creditors"), classified.GetValueOrDefault("Sundry Creditors"));
        counts["DebtorsCreditors"] = Math.Max(GetMutableCount(counts, "DebtorsCreditors"), counts["Debtors"] + counts["Creditors"]);
    }

    // Fix106 R1: IDictionary does not bind reliably to IReadOnlyDictionary.GetValueOrDefault in
    // this target/runtime combination. Use the interface's own TryGetValue contract so the
    // blank-module count enrichment remains type-stable and allocation-free.
    private static int GetMutableCount(IDictionary<string, int> counts, string key)
        => counts.TryGetValue(key, out var value) ? value : 0;

    private bool BankSummaryBelongsToCategory(AuditBankSummaryRow summary, string category, AuditClientProfile client)
    {
        if (string.IsNullOrWhiteSpace(summary.Account) || string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)) return false;
        var ledgers = _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
        var groups = _auditRepository.GetTallyGroupParents(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
        var ledgerParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ledger in ledgers)
        {
            if (!string.IsNullOrWhiteSpace(ledger.DisplayName)) ledgerParents[ledger.DisplayName.Trim()] = (ledger.Parent ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(ledger.ExactName)) ledgerParents[ledger.ExactName.Trim()] = (ledger.Parent ?? string.Empty).Trim();
        }
        return ClassifyLedgerByGroup(summary.Account, ledgerParents, groups).Equals(category, StringComparison.OrdinalIgnoreCase);
    }

    private AuditRecordRow[] BuildLinkedAccountingRows(AuditClientProfile auditClient, string module)
    {
        if (auditClient.LinkedAccountingClientId is not > 0) return Array.Empty<AuditRecordRow>();
        EnsureLinkedAccountingEvidenceInitialized();
        var clientId = auditClient.LinkedAccountingClientId.Value;
        var fy = auditClient.FinancialYear;

        var masters = module is "BankingFinance" or "DebtorsCreditors" or "Sales" or "Purchase"
            ? _masterRepository.Get(clientId, fy, includeArchived: false)
            : Array.Empty<AccountingAutomationHub.Domain.Masters.MasterRecord>();
        var ledgerParents = masters
            .Where(x => x.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && x.Name.Trim().Length > 0)
            .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Parent.Trim(), StringComparer.OrdinalIgnoreCase);
        var groupParents = masters
            .Where(x => x.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && x.Name.Trim().Length > 0)
            .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Parent.Trim(), StringComparer.OrdinalIgnoreCase);

        return module switch
        {
            "BankingFinance" => _bankingRepository.GetTransactions(clientId, fy, includeArchived: false)
                .Where(x => !x.RecordStatus.Equals("Deleted", StringComparison.OrdinalIgnoreCase))
                .Select(x =>
                {
                    var accountName = x.BankAccountName.Trim().Length > 0 ? x.BankAccountName.Trim() : x.LedgerName.Trim();
                    var category = ClassifyLedgerByGroup(accountName, ledgerParents, groupParents);
                    if (category is not "Bank" and not "OD" and not "Loans") category = "Bank";
                    var transactionClass = x.MatchId.Trim().Length > 0 ? "Contra / Inter-bank" : "Bank Statement";
                    return new AuditRecordRow
                    {
                        SourceRecordId = x.Id,
                        SourceModule = "BankingFinance",
                        Date = x.TransactionDate,
                        Reference = x.ReferenceNumber,
                        PartyOrLedger = accountName,
                        Counterparty = x.LedgerName,
                        Category = category,
                        TransactionClass = transactionClass,
                        Details = x.Narration,
                        OriginalNarration = x.Narration,
                        Debit = x.Debit,
                        Credit = x.Credit,
                        Amount = x.Amount,
                        Status = x.MatchId.Trim().Length > 0 ? "Statement pair linked" : x.MappingStatus,
                        EvidenceStatus = x.RunningBalance.HasValue
                            ? $"Statement balance {FormatMoney(x.RunningBalance.Value)}"
                            : "Bank statement evidence",
                        SourceEvidence = BuildBankSourceEvidence(x.SourceFile, x.SourceSheet, x.SourceRow),
                        Source = "Linked Accounting · Bank Statement",
                        IsException = IsIssueText(x.MappingStatus)
                    };
                }).ToArray(),

            "Sales" => _salesRepository.GetRecords(clientId, fy)
                .Select(x => new AuditRecordRow
                {
                    SourceRecordId = x.PersistentId,
                    SourceModule = "Sales",
                    Date = x.DocumentDate,
                    Reference = x.DocumentNumber,
                    PartyOrLedger = x.CustomerName.Length > 0 ? x.CustomerName : x.CustomerGstin,
                    CounterpartyGroup = x.CustomerGstin.Length > 0 ? "Sundry Debtors · B2B" : "Sundry Debtors · B2C",
                    Gstin = x.CustomerGstin,
                    VoucherType = x.DocumentType,
                    Category = SalesCategory(x.DocumentType),
                    TransactionClass = "Sales Source",
                    BusinessLedger = FirstCapturedLedger(x.ItemSalesLedger, x.SalesLedger),
                    BusinessGroup = ResolveLedgerGroup(FirstCapturedLedger(x.ItemSalesLedger, x.SalesLedger), ledgerParents, groupParents),
                    TaxLedgers = string.Join(" | ", new[] { x.CgstLedger, x.SgstLedger, x.IgstLedger }.Where(value => !string.IsNullOrWhiteSpace(value))),
                    HsnCode = x.HsnCode,
                    RoundOff = x.RoundOff,
                    DiscountPercent = x.DiscountPercent,
                    DiscountAmount = x.DiscountAmount,
                    OrderNumber = x.BuyerOrderNumber.Length > 0 ? x.BuyerOrderNumber : x.OrderNumber,
                    TrackingNumber = x.TrackingNumber,
                    Godown = x.Godown,
                    BatchName = x.BatchName,
                    CostCentre = x.CostCentre,
                    SourceSection = x.SourceSection,
                    IsGstr1Evidence = x.SourceSection.Trim().Length > 0 || x.SourceOrigin.Contains("GSTR", StringComparison.OrdinalIgnoreCase) || x.SourceFile.Contains("GSTR", StringComparison.OrdinalIgnoreCase),
                    TaxEvidenceSource = x.SourceSection.Trim().Length > 0 ? "GSTR-1 / Accounting source" : "Accounting Sales source",
                    PlaceOfSupply = x.PlaceOfSupply,
                    ReturnPeriod = x.ReturnPeriod,
                    Irn = x.Irn,
                    EWayBillNumber = x.EWayBillNumber,
                    Details = $"Taxable {FormatMoney(x.TaxableValue)} · GST {FormatMoney(x.Igst + x.Cgst + x.Sgst + x.Cess)}",
                    OriginalNarration = x.Narration,
                    Debit = Math.Abs(x.DocumentValue),
                    Amount = Math.Abs(x.DocumentValue),
                    TaxableValue = Math.Abs(x.TaxableValue),
                    Igst = Math.Abs(x.Igst),
                    Cgst = Math.Abs(x.Cgst),
                    Sgst = Math.Abs(x.Sgst),
                    Cess = Math.Abs(x.Cess),
                    GstRate = x.Rate,
                    GstRateKnown = true,
                    ItemName = x.ItemName,
                    Quantity = Math.Abs(x.BilledQuantity != 0m ? x.BilledQuantity : x.ActualQuantity),
                    Unit = x.Unit,
                    ItemRate = Math.Abs(x.ItemRate),
                    ReconciliationStatus = x.Destination,
                    Status = x.Destination,
                    EvidenceStatus = "Accounting Sales / GSTR-1 source",
                    SourceEvidence = x.SourceFile.Length > 0 ? x.SourceFile : x.SourceOrigin,
                    Source = "Linked Accounting · Sales",
                    IsException = IsIssueText(x.Destination)
                }).ToArray(),

            "Purchase" => _purchaseRepository.GetRecords(clientId, fy)
                .Select(x => new AuditRecordRow
                {
                    SourceRecordId = x.PersistentId,
                    SourceModule = "Purchase",
                    Date = x.DocumentDate,
                    Reference = x.DocumentNumber,
                    PartyOrLedger = x.SupplierName.Length > 0 ? x.SupplierName : x.SupplierGstin,
                    CounterpartyGroup = "Sundry Creditors",
                    Gstin = x.SupplierGstin,
                    VoucherType = x.DocumentType,
                    Category = PurchaseCategory(x.DocumentType),
                    TransactionClass = "Purchase Source",
                    BusinessLedger = FirstCapturedLedger(x.ItemPurchaseLedger, x.PurchaseLedger),
                    BusinessGroup = ResolveLedgerGroup(FirstCapturedLedger(x.ItemPurchaseLedger, x.PurchaseLedger), ledgerParents, groupParents),
                    TaxLedgers = string.Join(" | ", new[] { x.CgstLedger, x.SgstLedger, x.IgstLedger }.Where(value => !string.IsNullOrWhiteSpace(value))),
                    HsnCode = x.HsnCode,
                    RoundOff = x.RoundOff,
                    DiscountPercent = x.DiscountPercent,
                    DiscountAmount = x.DiscountAmount,
                    OrderNumber = x.OrderNumber,
                    TrackingNumber = x.TrackingNumber,
                    Godown = x.Godown,
                    BatchName = x.BatchName,
                    CostCentre = x.CostCentre,
                    SourceSection = x.SourceSection,
                    HasTwoAEvidence = x.Source2AFile.Trim().Length > 0 || x.TwoAReturnPeriod.Trim().Length > 0,
                    HasTwoBEvidence = x.Source2BFile.Trim().Length > 0 || x.TwoBReturnPeriod.Trim().Length > 0 || x.TwoBInvoiceValue.HasValue || x.TwoBTaxableValue.HasValue,
                    TwoBInvoiceValue = x.TwoBInvoiceValue.HasValue ? Math.Abs(x.TwoBInvoiceValue.Value) : null,
                    TwoBTaxableValue = x.TwoBTaxableValue.HasValue ? Math.Abs(x.TwoBTaxableValue.Value) : null,
                    TwoBIgst = x.TwoBIgst.HasValue ? Math.Abs(x.TwoBIgst.Value) : null,
                    TwoBCgst = x.TwoBCgst.HasValue ? Math.Abs(x.TwoBCgst.Value) : null,
                    TwoBSgst = x.TwoBSgst.HasValue ? Math.Abs(x.TwoBSgst.Value) : null,
                    TaxEvidenceSource = x.Source2BFile.Trim().Length > 0 ? "GSTR-2B / Accounting source" : x.Source2AFile.Trim().Length > 0 ? "GSTR-2A / Accounting source" : "Accounting Purchase source",
                    PlaceOfSupply = x.PlaceOfSupply,
                    ReturnPeriod = x.TwoBReturnPeriod.Length > 0 ? x.TwoBReturnPeriod : x.TwoAReturnPeriod,
                    InvoiceType = x.InvoiceType,
                    TwoBReason = x.TwoBReason,
                    DifferenceDetail = x.DifferenceDetails,
                    Details = $"Taxable {FormatMoney(x.TaxableValue)} · GST {FormatMoney(x.Igst + x.Cgst + x.Sgst + x.Cess)}",
                    OriginalNarration = x.Narration,
                    Debit = Math.Abs(x.DocumentValue),
                    Amount = Math.Abs(x.DocumentValue),
                    TaxableValue = Math.Abs(x.TaxableValue),
                    Igst = Math.Abs(x.Igst),
                    Cgst = Math.Abs(x.Cgst),
                    Sgst = Math.Abs(x.Sgst),
                    Cess = Math.Abs(x.Cess),
                    GstRate = x.Rate,
                    GstRateKnown = x.RateKnown,
                    ItemName = x.ItemName,
                    Quantity = Math.Abs(x.BilledQuantity != 0m ? x.BilledQuantity : x.ActualQuantity),
                    Unit = x.Unit,
                    ItemRate = Math.Abs(x.ItemRate),
                    ReconciliationStatus = x.ReconciliationStatus.Length > 0 ? x.ReconciliationStatus : x.Destination,
                    ItcStatus = x.ItcAvailability,
                    IsRcm = x.IsRcmRow || x.ReverseCharge.Equals("Y", StringComparison.OrdinalIgnoreCase) || x.ReverseCharge.Equals("Yes", StringComparison.OrdinalIgnoreCase),
                    Status = x.ReconciliationStatus.Length > 0 ? x.ReconciliationStatus : x.Destination,
                    EvidenceStatus = "Accounting Purchase / GSTR-2B source",
                    SourceEvidence = string.Join(" | ", new[] { x.Source2BFile, x.Source2AFile }.Where(value => !string.IsNullOrWhiteSpace(value))),
                    Source = "Linked Accounting · Purchase",
                    IsException = IsIssueText(x.ReconciliationStatus) || IsIssueText(x.Destination) || IsIssueText(x.TwoBReason)
                }).ToArray(),

            "Cash" => _bankingRepository.GetCashEntries(clientId, fy)
                .Select(x => new AuditRecordRow
                {
                    Date = x.EntryDate,
                    Reference = x.ReferenceNumber,
                    PartyOrLedger = x.EntryLedger,
                    VoucherType = x.VoucherType,
                    Category = "Cash",
                    TransactionClass = CashTransactionClass(x.VoucherType, x.Narration),
                    Details = x.Narration,
                    OriginalNarration = x.Narration,
                    Debit = x.Debit,
                    Credit = x.Credit,
                    Amount = x.Amount,
                    Status = $"{x.WorkflowStatus} · {x.Status}",
                    EvidenceStatus = "Accounting Cash source",
                    Source = "Linked Accounting · Cash",
                    IsException = IsIssueText(x.WorkflowStatus) || IsIssueText(x.Status)
                }).ToArray(),

            "DebtorsCreditors" => masters
                .Where(x => x.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
                .Select(x => new { Master = x, Category = ClassifyLedgerByGroup(x.Name, ledgerParents, groupParents) })
                .Where(x => x.Category is "Sundry Debtors" or "Sundry Creditors")
                .Select(x => new AuditRecordRow
                {
                    PartyOrLedger = x.Master.Name,
                    Category = x.Category,
                    TransactionClass = "Master",
                    AuthorityId = x.Master.TallyMasterId.Trim().Length > 0 ? $"MID {x.Master.TallyMasterId.Trim()}" : x.Master.AahMasterId,
                    Details = $"Parent: {x.Master.Parent}",
                    Amount = Math.Abs(x.Master.OpeningBalance ?? 0m),
                    Debit = x.Master.OpeningBalance > 0 ? Math.Abs(x.Master.OpeningBalance.Value) : 0m,
                    Credit = x.Master.OpeningBalance < 0 ? Math.Abs(x.Master.OpeningBalance.Value) : 0m,
                    Status = x.Master.Parent,
                    EvidenceStatus = "Accounting Master authority",
                    Source = "Linked Accounting · Masters"
                }).ToArray(),

            "Journal" => _journalRepository.GetVouchers(clientId, fy)
                .Select(x => new AuditRecordRow
                {
                    SourceRecordId = x.PersistentId,
                    SourceModule = "Journal",
                    Date = x.VoucherDate,
                    Reference = x.VoucherNumber.Length > 0 ? x.VoucherNumber : x.ReferenceNumber,
                    PartyOrLedger = x.Lines.FirstOrDefault()?.LedgerName ?? string.Empty,
                    VoucherType = x.VoucherType,
                    Category = "Journal",
                    TransactionClass = x.VoucherType,
                    Details = x.Narration,
                    OriginalNarration = x.Narration,
                    Debit = x.Lines.Where(line => line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase)).Sum(line => Math.Abs(line.Amount)),
                    Credit = x.Lines.Where(line => line.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase)).Sum(line => Math.Abs(line.Amount)),
                    Amount = Math.Max(
                        x.Lines.Where(line => line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase)).Sum(line => Math.Abs(line.Amount)),
                        x.Lines.Where(line => line.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase)).Sum(line => Math.Abs(line.Amount))),
                    Status = x.WorkflowStatus,
                    EvidenceStatus = "Accounting Journal source",
                    Source = "Linked Accounting · Journal",
                    IsException = IsIssueText(x.WorkflowStatus)
                }).ToArray(),

            "GST" => _gstRepository.GetReturns(clientId, fy)
                .Select(x => new AuditRecordRow
                {
                    Date = x.ArnDate,
                    Reference = x.Arn,
                    PartyOrLedger = x.Gstin,
                    Category = "GSTR-3B",
                    TransactionClass = x.PeriodLabel,
                    Details = $"{x.PeriodLabel} · Outward {FormatMoney(x.OutwardTaxable)} · Net ITC {FormatMoney(x.NetItc)} · Tax payable {FormatMoney(x.TaxPayableTotal)}",
                    Debit = x.NetItc,
                    Credit = x.TaxPayableTotal,
                    Amount = Math.Max(Math.Abs(x.NetItc), Math.Abs(x.TaxPayableTotal)),
                    Status = x.IsNilReturn ? "Nil Return" : x.PaymentStatus,
                    EvidenceStatus = "Accounting GST source",
                    Source = "Linked Accounting · GSTR-3B",
                    IsException = IsIssueText(x.PaymentStatus)
                }).ToArray(),

            "26AS" => _taxCreditRepository.GetCurrentRows(clientId, fy)
                .Select(x => new AuditRecordRow
                {
                    Date = x.TransactionDate,
                    Reference = x.CertificateNumber.Length > 0 ? x.CertificateNumber : x.AcknowledgementNumber,
                    PartyOrLedger = x.DeductorName.Length > 0 ? x.DeductorName : x.DeductorTan,
                    Category = x.Part,
                    TransactionClass = x.Section,
                    Details = $"{x.Part} · {x.Section} · {x.TaxKind} · Gross {FormatMoney(x.AmountPaid)}",
                    Debit = x.TaxDeducted,
                    Amount = x.TaxDeducted,
                    Status = x.BookingStatus,
                    EvidenceStatus = "Accounting 26AS source",
                    Source = "Linked Accounting · 26AS",
                    IsException = IsIssueText(x.BookingStatus)
                }).ToArray(),
            _ => Array.Empty<AuditRecordRow>()
        };
    }

    private static string BuildBankSourceEvidence(string sourceFile, string sourceSheet, int sourceRow)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(sourceFile)) parts.Add(Path.GetFileName(sourceFile));
        if (!string.IsNullOrWhiteSpace(sourceSheet)) parts.Add(sourceSheet.Trim());
        if (sourceRow > 0) parts.Add($"row {FormatCount(sourceRow)}");
        return string.Join(" · ", parts);
    }

    private AuditRecordRow[] BuildAuditOwnedBankStatementRows(AuditClientProfile client)
    {
        var statements = _auditRepository.GetBankStatements(client.Id, client.FinancialYear)
            .ToDictionary(item => item.Id);
        return _auditRepository.GetBankStatementTransactions(client.Id, client.FinancialYear)
            .Select(row =>
            {
                statements.TryGetValue(row.StatementId, out var statement);
                return new AuditRecordRow
                {
                    Date = row.TransactionDate,
                    Reference = row.Reference,
                    PartyOrLedger = row.AccountName,
                    Category = row.AccountCategory,
                    TransactionClass = "Bank Statement",
                    Details = row.Narration,
                    OriginalNarration = row.Narration,
                    Debit = row.Debit,
                    Credit = row.Credit,
                    Amount = row.Amount,
                    Status = statement?.ParseStatus ?? "Audit statement evidence",
                    EvidenceStatus = row.RunningBalance.HasValue ? $"Running balance {FormatMoney(row.RunningBalance.Value)}" : "Statement movement evidence",
                    SourceEvidence = statement is null
                        ? row.SourceLocation
                        : $"{statement.SourceFile} · {statement.SourceSheet} · {row.SourceLocation}",
                    Source = "Audit-owned Bank Statement",
                    IsException = statement is not null && statement.ParseStatus.Contains("review", StringComparison.OrdinalIgnoreCase)
                };
            })
            .ToArray();
    }

    private static void ApplyAuditInterBankStatementConfirmation(AuditRecordRow[] rows)
    {
        var statementRows = rows
            .Where(row => row.Source.Contains("Bank Statement", StringComparison.OrdinalIgnoreCase))
            .Where(row => row.Category is "Bank" or "OD")
            .Where(row => row.Date.HasValue && row.Amount > 0m)
            .ToArray();
        if (statementRows.Length == 0) return;

        var booksContra = rows
            .Where(row => row.Source.Equals("Explicit Audit Tally Link", StringComparison.OrdinalIgnoreCase))
            .Where(row => row.TransactionClass.Contains("Contra", StringComparison.OrdinalIgnoreCase) || row.TransactionClass.Contains("Inter-bank", StringComparison.OrdinalIgnoreCase))
            .Where(row => row.Category is "Bank" or "OD")
            .ToArray();
        foreach (var group in booksContra.GroupBy(row =>
                     row.AuthorityId.Length > 0 ? row.AuthorityId : $"{row.Date:yyyyMMdd}|{row.Reference}|{row.Amount:0.00}",
                     StringComparer.OrdinalIgnoreCase))
        {
            var sides = group.GroupBy(row => row.PartyOrLedger, StringComparer.OrdinalIgnoreCase).Select(item => item.First()).ToArray();
            if (sides.Length < 2) continue;
            var found = new Dictionary<string, AuditRecordRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var side in sides)
            {
                var candidate = statementRows
                    .Where(statement => statement.PartyOrLedger.Equals(side.PartyOrLedger, StringComparison.OrdinalIgnoreCase))
                    .Where(statement => Math.Abs(statement.Amount - side.Amount) <= 0.01m)
                    .Where(statement => !side.Date.HasValue || !statement.Date.HasValue || Math.Abs((statement.Date.Value.Date - side.Date.Value.Date).TotalDays) <= 5)
                    .Where(statement => side.Debit > 0m ? statement.Credit > 0m : side.Credit > 0m ? statement.Debit > 0m : true)
                    .Select(statement => new { Row = statement, Score = InterBankEvidenceScore(side, statement) })
                    .Where(item => item.Score >= 2)
                    .OrderByDescending(item => item.Score)
                    .ThenBy(item => side.Date.HasValue && item.Row.Date.HasValue ? Math.Abs((item.Row.Date.Value.Date - side.Date.Value.Date).TotalDays) : 99d)
                    .Select(item => item.Row)
                    .FirstOrDefault();
                if (candidate is not null) found[side.PartyOrLedger] = candidate;
            }

            foreach (var side in sides)
            {
                if (found.Count >= 2)
                {
                    side.Status = "Statement confirmed inter-bank";
                    side.EvidenceStatus = "Both Bank/OD statements found · amount/direction/date/original narration-reference evidence confirmed";
                    side.IsException = false;
                }
                else if (found.ContainsKey(side.PartyOrLedger))
                {
                    side.Status = "One statement found · waiting opposite bank";
                    side.EvidenceStatus = "This bank statement evidence matched; opposite Bank/OD statement is still required";
                }
                else
                {
                    side.Status = "Pending statement confirmation";
                    side.EvidenceStatus = "Books pair identified · wait for both bank statements";
                }
            }
        }
    }

    private static int InterBankEvidenceScore(AuditRecordRow books, AuditRecordRow statement)
    {
        var score = 1; // amount/direction/date already passed the candidate filters.
        var bookRef = NormalizeEvidenceText(books.Reference);
        var statementRef = NormalizeEvidenceText(statement.Reference);
        if (bookRef.Length >= 4 && statementRef.Length >= 4 && (bookRef.Contains(statementRef, StringComparison.OrdinalIgnoreCase) || statementRef.Contains(bookRef, StringComparison.OrdinalIgnoreCase))) score += 4;
        var booksNarration = NormalizeEvidenceText(books.OriginalNarration.Length > 0 ? books.OriginalNarration : books.Details);
        var statementNarration = NormalizeEvidenceText(statement.OriginalNarration.Length > 0 ? statement.OriginalNarration : statement.Details);
        if (booksNarration.Length > 0 && statementNarration.Length > 0)
        {
            var bookTokens = booksNarration.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length >= 4).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var statementTokens = statementNarration.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length >= 4).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var overlap = bookTokens.Count == 0 ? 0 : bookTokens.Count(statementTokens.Contains);
            if (overlap >= 2) score += 3;
            else if (overlap == 1) score += 1;
        }
        return score;
    }

    private static string NormalizeEvidenceText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.ToUpperInvariant().Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private AuditBankSummaryRow[] BuildAuditOwnedBankSummaries(AuditClientProfile client, AuditRecordRow[] allRows)
    {
        var statements = _auditRepository.GetBankStatements(client.Id, client.FinancialYear);
        if (statements.Count == 0) return Array.Empty<AuditBankSummaryRow>();
        var books = allRows
            .Where(row => row.Source.Equals("Explicit Audit Tally Link", StringComparison.OrdinalIgnoreCase))
            .Where(row => row.Category is "Bank" or "OD" or "Loans")
            .ToArray();
        return statements.Select(statement =>
        {
            var accountBooks = books.Where(row => row.PartyOrLedger.Equals(statement.AccountName, StringComparison.OrdinalIgnoreCase)).ToArray();
            var booksDebit = accountBooks.Sum(row => row.Debit);
            var booksCredit = accountBooks.Sum(row => row.Credit);
            decimal? expected = statement.OpeningBalance.HasValue
                ? Math.Round(statement.OpeningBalance.Value + statement.TotalCredit - statement.TotalDebit, 2, MidpointRounding.AwayFromZero)
                : null;
            decimal? sourceDifference = statement.ClosingBalance.HasValue && expected.HasValue
                ? Math.Round(statement.ClosingBalance.Value - expected.Value, 2, MidpointRounding.AwayFromZero)
                : null;
            var movementDifference = Math.Round((statement.TotalCredit - statement.TotalDebit) - (booksDebit - booksCredit), 2, MidpointRounding.AwayFromZero);
            var sourceVerified = sourceDifference.HasValue && Math.Abs(sourceDifference.Value) <= 0.01m;
            var movementVerified = Math.Abs(movementDifference) <= 0.01m;
            return new AuditBankSummaryRow
            {
                Account = statement.AccountName,
                Period = statement.PeriodFrom.HasValue && statement.PeriodTo.HasValue ? $"{statement.PeriodFrom:dd-MM-yyyy} to {statement.PeriodTo:dd-MM-yyyy}" : statement.SourceFile,
                Opening = statement.OpeningBalance?.ToString("N2", IndianCulture) ?? string.Empty,
                StatementDebit = statement.TotalDebit,
                StatementCredit = statement.TotalCredit,
                BooksDebit = booksDebit,
                BooksCredit = booksCredit,
                ExpectedClosing = expected?.ToString("N2", IndianCulture) ?? string.Empty,
                StatementClosing = statement.ClosingBalance?.ToString("N2", IndianCulture) ?? string.Empty,
                Difference = sourceDifference,
                DebitCreditStatus = movementVerified ? "Debit/Credit verified" : $"Books movement difference {FormatMoney(movementDifference)}",
                Status = sourceVerified && movementVerified ? "Reconciled" : sourceVerified ? "Source balance verified · Books review" : statement.ParseStatus
            };
        }).ToArray();
    }

    private static AuditBankSummaryRow[] MergeBankSummaries(AuditBankSummaryRow[] auditOwned, AuditBankSummaryRow[] linkedAccounting)
    {
        if (auditOwned.Length == 0) return linkedAccounting;
        if (linkedAccounting.Length == 0) return auditOwned;
        var keys = auditOwned.Select(row => $"{row.Account}|{row.Period}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        return auditOwned.Concat(linkedAccounting.Where(row => !keys.Contains($"{row.Account}|{row.Period}"))).ToArray();
    }

    private void ImportAuditBankStatements()
    {
        var client = SelectedAuditClient;
        if (client is null) return;
        if (string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey))
        {
            System.Windows.MessageBox.Show("Link the Audit client to Tally first so statement evidence can be assigned to an actual Bank Accounts / Bank OD ledger.", "Audit Bank Statement", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var ledgerAuthority = _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
        var groupParents = _auditRepository.GetTallyGroupParents(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var ledgerParents = ledgerAuthority
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayName))
            .GroupBy(item => item.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Parent.Trim(), StringComparer.OrdinalIgnoreCase);
        var choices = ledgerAuthority
            .Select(item => new
            {
                Ledger = item,
                Category = ClassifyLedgerByGroup(item.DisplayName, ledgerParents, groupParents)
            })
            .Where(item => item.Category is "Bank" or "OD")
            .Select(item => new AuditBankAccountChoice
            {
                Name = item.Ledger.DisplayName.Trim(),
                Category = item.Category,
                MasterId = item.Ledger.MasterId.Trim()
            })
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (choices.Length == 0)
        {
            System.Windows.MessageBox.Show("No Bank Accounts / Bank OD ledgers are present in the Audit-owned Tally Master authority. Refresh/re-link Tally and try again.", "Audit Bank Statement", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Audit Bank / OD Statement Evidence",
            Multiselect = true,
            Filter = "Bank statements|*.xlsx;*.xlsm;*.xls;*.csv;*.txt;*.tsv;*.pdf|Excel workbooks|*.xlsx;*.xlsm;*.xls|CSV / text|*.csv;*.txt;*.tsv|PDF statements|*.pdf|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        var imported = 0;
        var skipped = 0;
        var issues = new List<string>();
        foreach (var file in dialog.FileNames)
        {
            try
            {
                IReadOnlyList<AuditBankStatementImportPreview>? previews = null;
                Exception? lastParseError = null;
                var extension = Path.GetExtension(file);
                if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    previews = _auditBankStatementImportService.Parse(file, client.FinancialYear);
                }
                else
                {
                    var candidates = LocalPasswordCandidateDiscovery.Discover(file)
                        .Prepend(string.Empty)
                        .Distinct(StringComparer.Ordinal)
                        .Take(12)
                        .ToArray();
                    foreach (var candidate in candidates)
                    {
                        try
                        {
                            previews = _auditBankStatementImportService.Parse(file, client.FinancialYear, candidate, recoveryMode: false);
                            lastParseError = null;
                            break;
                        }
                        catch (Exception ex)
                        {
                            lastParseError = ex;
                        }
                    }
                    if (previews is null)
                    {
                        foreach (var candidate in candidates)
                        {
                            try
                            {
                                previews = _auditBankStatementImportService.Parse(file, client.FinancialYear, candidate, recoveryMode: true);
                                lastParseError = null;
                                break;
                            }
                            catch (Exception ex)
                            {
                                lastParseError = ex;
                            }
                        }
                    }
                    if (previews is null)
                    {
                        var password = PasswordPromptService.Prompt(Path.GetFileName(file));
                        if (password is not null)
                        {
                            try
                            {
                                previews = _auditBankStatementImportService.Parse(file, client.FinancialYear, password, recoveryMode: true);
                                lastParseError = null;
                            }
                            catch (Exception ex)
                            {
                                lastParseError = ex;
                            }
                        }
                    }
                    if (previews is null && lastParseError is not null) throw lastParseError;
                }
                if (previews is null || previews.Count == 0)
                {
                    skipped++;
                    continue;
                }
                var hash = AuditBankStatementImportService.ComputeFileHash(file);
                foreach (var preview in previews)
                {
                    var picker = new AuditBankStatementAccountPickerWindow(preview, choices)
                    {
                        Owner = System.Windows.Application.Current?.MainWindow
                    };
                    if (picker.ShowDialog() != true || picker.SelectedChoice is null)
                    {
                        skipped++;
                        continue;
                    }
                    var choice = picker.SelectedChoice;
                    foreach (var row in preview.Transactions)
                    {
                        row.AuditClientId = client.Id;
                        row.FinancialYear = client.FinancialYear;
                        row.AccountName = choice.Name;
                        row.AccountCategory = choice.Category;
                    }
                    _auditRepository.SaveBankStatementEvidence(new AuditBankStatementEvidence
                    {
                        AuditClientId = client.Id,
                        FinancialYear = client.FinancialYear,
                        AccountName = choice.Name,
                        AccountCategory = choice.Category,
                        AccountIdentity = preview.DetectedAccountIdentity,
                        SourcePath = preview.SourcePath,
                        SourceFile = preview.SourceFile,
                        SourceSheet = preview.SourceSheet,
                        FileHash = hash,
                        PeriodFrom = preview.PeriodFrom,
                        PeriodTo = preview.PeriodTo,
                        OpeningBalance = preview.OpeningBalance,
                        ClosingBalance = preview.ClosingBalance,
                        TotalDebit = preview.TotalDebit,
                        TotalCredit = preview.TotalCredit,
                        TransactionCount = preview.Transactions.Count,
                        ParseStatus = preview.ParseStatus,
                        Notes = preview.Notes,
                        ImportedUtc = DateTime.UtcNow
                    }, preview.Transactions);
                    imported++;
                }
            }
            catch (Exception ex)
            {
                issues.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        _ = RefreshAsync(forceAll: true);
        var message = $"Audit statement sources imported: {FormatCount(imported)}; skipped/cancelled: {FormatCount(skipped)}.";
        if (issues.Count > 0) message += $"\n\nReview required:\n{string.Join("\n", issues.Take(8))}";
        System.Windows.MessageBox.Show(message, "Audit Bank Statement", MessageBoxButton.OK, issues.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private AuditBankSummaryRow[] BuildLinkedBankSummaries(long accountingClientId, string fy)
    {
        EnsureLinkedAccountingEvidenceInitialized();
        return _bankReconciliationService.Reconcile(accountingClientId, fy)
            .Select(x => new AuditBankSummaryRow
            {
                Account = x.AccountName,
                Period = x.PeriodLabel,
                Opening = x.OpeningBalance.HasValue ? x.OpeningBalance.Value.ToString("N2", IndianCulture) : string.Empty,
                StatementDebit = x.TotalDebit,
                StatementCredit = x.TotalCredit,
                BooksDebit = x.BooksDebitMovements,
                BooksCredit = x.BooksCreditMovements,
                ExpectedClosing = x.ExpectedClosingBalance.HasValue ? x.ExpectedClosingBalance.Value.ToString("N2", IndianCulture) : string.Empty,
                StatementClosing = x.StatementClosingBalance.HasValue ? x.StatementClosingBalance.Value.ToString("N2", IndianCulture) : string.Empty,
                Difference = x.Difference,
                DebitCreditStatus = x.OpeningBalance.HasValue && x.StatementClosingBalance.HasValue && x.Difference.HasValue && Math.Round(x.Difference.Value, 2) == 0m
                    ? "Debit/Credit verified"
                    : "Movement review",
                Status = x.Status
            }).ToArray();
    }

    // Inherited Fix92/Fix95 updater compatibility proof only: TallyBelongsToModule.
    // Active Fix97+ routing is intentionally handled by BuildExplicitTallyRows + ClassifyLedgerByGroup
    // so Bank/OD/Loans/Cash/Sales/Purchase/Journal remain driven by the independent Audit Tally authority.
    private AuditRecordRow[] BuildExplicitTallyRows(AuditClientProfile auditClient, string module)
    {
        var companyKey = auditClient.LinkedTallyCompanyKey.Trim();
        if (companyKey.Length == 0) return Array.Empty<AuditRecordRow>();
        EnsureLinkedTallyEvidenceInitialized();

        var ledgerAuthority = _auditRepository.GetTallyLedgerAuthority(auditClient.Id, auditClient.FinancialYear, companyKey);
        var groupParents = _auditRepository.GetTallyGroupParents(auditClient.Id, auditClient.FinancialYear, companyKey)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var ledgerParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ledger in ledgerAuthority)
        {
            if (ledger.DisplayName.Trim().Length > 0) ledgerParents[ledger.DisplayName.Trim()] = ledger.Parent.Trim();
            if (ledger.ExactName.Trim().Length > 0) ledgerParents[ledger.ExactName.Trim()] = ledger.Parent.Trim();
        }
        var ledgerGstins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ledger in ledgerAuthority)
        {
            var gstin = NormalizePartyGstin(ledger.Gstin);
            if (gstin.Length != 15) continue;
            if (ledger.DisplayName.Trim().Length > 0) ledgerGstins[ledger.DisplayName.Trim()] = gstin;
            if (ledger.ExactName.Trim().Length > 0) ledgerGstins[ledger.ExactName.Trim()] = gstin;
        }

        if (module.Equals("DebtorsCreditors", StringComparison.OrdinalIgnoreCase))
        {
            var voucherTypeCounts = BuildTallyLedgerVoucherTypeCounts(auditClient);
            var movementTotals = BuildTallyLedgerMovementTotals(auditClient);
            return ledgerAuthority
                .Select(ledger => new { Ledger = ledger, Category = ClassifyLedgerByGroup(ledger.DisplayName, ledgerParents, groupParents) })
                .Where(item => item.Category is "Sundry Debtors" or "Sundry Creditors")
                .Select(item =>
                {
                    var activity = ResolveLedgerVoucherTypeCounts(voucherTypeCounts, item.Ledger.DisplayName, item.Ledger.ExactName);
                    var movement = ResolveLedgerMovementTotals(movementTotals, item.Ledger.DisplayName, item.Ledger.ExactName);
                    return new AuditRecordRow
                    {
                        PartyOrLedger = item.Ledger.DisplayName,
                        Category = item.Category,
                        TransactionClass = "Tally Master",
                        AuthorityId = item.Ledger.MasterId.Trim().Length > 0 ? $"MID {item.Ledger.MasterId.Trim()}" : item.Ledger.Guid.Trim(),
                        Details = FormatLedgerVoucherTypeDetails(activity),
                        OpeningBalance = item.Ledger.OpeningBalance,
                        ClosingBalance = item.Ledger.ClosingBalance,
                        Debit = movement.Debit,
                        Credit = movement.Credit,
                        Amount = movement.Debit + movement.Credit,
                        BalanceNature = !item.Ledger.ClosingBalance.HasValue ? "N/A" : item.Ledger.ClosingBalance.Value > 0m ? "Dr" : item.Ledger.ClosingBalance.Value < 0m ? "Cr" : "Zero",
                        TransactionCount = activity.Values.Sum(),
                        Gstin = item.Ledger.Gstin,
                        Status = item.Ledger.Parent,
                        EvidenceStatus = item.Ledger.ClosingBalance.HasValue
                            ? "Live Tally Master authority · closing balance"
                            : "Live Tally Master authority · closing not supplied",
                        Source = "Explicit Audit Tally Link · Masters"
                    };
                })
                .OrderBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.PartyOrLedger, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var (from, to) = AdvancedClosingEntryPlanner.FinancialYearRange(auditClient.FinancialYear);
        var vouchers = _tallyRepository.GetVoucherIndexRange(companyKey, auditClient.FinancialYear, from, to);
        if (vouchers.Count == 0) return Array.Empty<AuditRecordRow>();

        var tallyXmlByMasterId = module is "Sales" or "Purchase" or "Journal" or "Tally"
            ? _tallyRepository.GetVoucherXmlAuthorityByMasterIds(companyKey, auditClient.FinancialYear, vouchers.Select(voucher => voucher.MasterId))
                .Where(snapshot => snapshot.MasterId.Trim().Length > 0)
                .GroupBy(snapshot => snapshot.MasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, TallyVoucherXmlSnapshot>(StringComparer.OrdinalIgnoreCase);

        var result = new List<AuditRecordRow>();
        foreach (var voucher in vouchers)
        {
            var debitLedgers = SplitLedgerNames(voucher.DebitLedgers);
            var creditLedgers = SplitLedgerNames(voucher.CreditLedgers);
            // Fix97 R3 inherited Fix92 R1 compatibility proof only: var party = voucher.PartyLedger?.Trim() ?? string.Empty;
            var partyLedger = voucher.PartyLedger?.Trim() ?? string.Empty;
            var ledgers = debitLedgers.Concat(creditLedgers)
                .Append(partyLedger)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var classified = ledgers
                .Select(name => new LedgerClass(name, ClassifyLedgerByGroup(name, ledgerParents, groupParents)))
                .ToArray();

            var financeLedgers = classified.Where(item => item.Category is "Bank" or "OD" or "Loans").ToArray();
            var cashLedgers = classified.Where(item => item.Category == "Cash").ToArray();
            var amount = Math.Abs(voucher.Amount);
            var reference = voucher.VoucherNumber.Trim();
            if (reference.Length == 0) reference = voucher.Reference.Trim();
            if (reference.Length == 0) reference = $"Tally MID {voucher.MasterId}";
            var masterId = voucher.MasterId?.Trim() ?? string.Empty;
            var voucherGuid = voucher.Guid?.Trim() ?? string.Empty;
            var authorityId = masterId.Length > 0 ? $"MID {masterId}" : voucherGuid;
            var alterStatus = voucher.AlterId.Trim().Length > 0 ? $"ALTERID {voucher.AlterId.Trim()}" : "Tally authority";
            var narration = voucher.Narration.Trim();

            if (module.Equals("BankingFinance", StringComparison.OrdinalIgnoreCase))
            {
                if (financeLedgers.Length == 0) continue;
                var contra = financeLedgers.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2;
                foreach (var finance in financeLedgers)
                {
                    var counterparties = ledgers.Where(name => !name.Equals(finance.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var isDebit = debitLedgers.Contains(finance.Name, StringComparer.OrdinalIgnoreCase);
                    var isCredit = creditLedgers.Contains(finance.Name, StringComparer.OrdinalIgnoreCase);
                    var counterpartyGroup = ResolveCounterpartyGroup(counterparties, ledgerParents, groupParents);
                    var movementType = FinanceMovementType(finance.Category, isDebit, isCredit, counterpartyGroup, cashLedgers.Length > 0, contra);
                    result.Add(new AuditRecordRow
                    {
                        Date = voucher.VoucherDate,
                        Reference = reference,
                        PartyOrLedger = finance.Name,
                        Counterparty = string.Join(" | ", counterparties),
                        VoucherType = (voucher.VoucherType ?? string.Empty).Trim(),
                        AuthorityId = authorityId,
                        TallyMasterId = voucher.MasterId?.Trim() ?? string.Empty,
                        TallyGuid = voucher.Guid?.Trim() ?? string.Empty,
                        SourceModule = module,
                        Category = finance.Category,
                        TransactionClass = contra ? "Contra / Inter-bank" : FinanceTransactionClass(voucher.VoucherType, cashLedgers.Length > 0),
                        CounterpartyGroup = counterpartyGroup,
                        PaymentReceiptType = movementType,
                        Details = narration,
                        OriginalNarration = narration,
                        Debit = isDebit ? amount : 0m,
                        Credit = isCredit ? amount : 0m,
                        Amount = amount,
                        Status = contra ? "Pending statement confirmation" : alterStatus,
                        EvidenceStatus = contra
                            ? "Books pair identified · wait for both bank statements"
                            : "Books authority · bank statement awaited",
                        Source = "Explicit Audit Tally Link",
                        IsException = false
                    });
                }
                continue;
            }

            if (module.Equals("Cash", StringComparison.OrdinalIgnoreCase))
            {
                if (cashLedgers.Length == 0) continue;
                foreach (var cash in cashLedgers)
                {
                    var counterparties = ledgers.Where(name => !name.Equals(cash.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var isDebit = debitLedgers.Contains(cash.Name, StringComparer.OrdinalIgnoreCase);
                    var isCredit = creditLedgers.Contains(cash.Name, StringComparer.OrdinalIgnoreCase);
                    var counterpartyGroup = ResolveCounterpartyGroup(counterparties, ledgerParents, groupParents);
                    var movementType = CashMovementType(isDebit, isCredit, counterpartyGroup, financeLedgers.Length > 0);
                    result.Add(new AuditRecordRow
                    {
                        Date = voucher.VoucherDate,
                        Reference = reference,
                        PartyOrLedger = cash.Name,
                        Counterparty = string.Join(" | ", counterparties),
                        VoucherType = (voucher.VoucherType ?? string.Empty).Trim(),
                        AuthorityId = authorityId,
                        TallyMasterId = voucher.MasterId?.Trim() ?? string.Empty,
                        TallyGuid = voucher.Guid?.Trim() ?? string.Empty,
                        SourceModule = module,
                        Category = "Cash",
                        TransactionClass = financeLedgers.Length > 0 ? "Bank ↔ Cash" : CashTransactionClass(voucher.VoucherType, narration),
                        CounterpartyGroup = counterpartyGroup,
                        PaymentReceiptType = movementType,
                        Details = narration,
                        OriginalNarration = narration,
                        Debit = isDebit ? amount : 0m,
                        Credit = isCredit ? amount : 0m,
                        Amount = amount,
                        Status = financeLedgers.Length > 0 ? "Contra evidence pending" : alterStatus,
                        EvidenceStatus = financeLedgers.Length > 0
                            ? "Books Bank ↔ Cash pair identified"
                            : "Live Tally cash authority",
                        Source = "Explicit Audit Tally Link"
                    });
                }
                continue;
            }

            var type = (voucher.VoucherType ?? string.Empty).Trim();
            if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase) &&
                !(type.Equals("Sales", StringComparison.OrdinalIgnoreCase) || type.Contains("Credit Note", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) &&
                !(type.Equals("Purchase", StringComparison.OrdinalIgnoreCase) || type.Contains("Debit Note", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (module.Equals("Journal", StringComparison.OrdinalIgnoreCase) &&
                !(type.Contains("Journal", StringComparison.OrdinalIgnoreCase) || type.Contains("Memorandum", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!module.Equals("Sales", StringComparison.OrdinalIgnoreCase) &&
                !module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) &&
                !module.Equals("Journal", StringComparison.OrdinalIgnoreCase) &&
                !module.Equals("Tally", StringComparison.OrdinalIgnoreCase))
                continue;

            var category = module switch
            {
                "Sales" => SalesCategory(type),
                "Purchase" => PurchaseCategory(type),
                "Journal" => "Journal",
                _ => type.Length > 0 ? type : "Tally Voucher"
            };
            var party = partyLedger.Length > 0 ? partyLedger : ledgers.FirstOrDefault() ?? string.Empty;
            var partyGroup = ResolveLedgerGroup(party, ledgerParents, groupParents);

            // Audit presentation is source-transparent: never suppress Credit/Debit Notes
            // because a ledger does not belong to a recognised commerce group. The voucher
            // exists in source authority, therefore Audit must show it exactly as captured.
            TallyVoucherXmlSnapshot? voucherXml = null;
            if (masterId.Length > 0)
                tallyXmlByMasterId.TryGetValue(masterId, out voucherXml);
            var taxEvidence = voucherXml is not null
                ? ExtractTallyCommerceTaxEvidence(voucherXml.OriginalXml, module, party, amount, ledgerParents, groupParents)
                : TallyCommerceTaxEvidence.Empty;
            var sourceLedgerLines = voucherXml is not null
                ? ExtractTallySourceLedgerLines(voucherXml.OriginalXml, ledgerParents, groupParents)
                : new List<AuditVoucherLedgerLineData>();
            if (sourceLedgerLines.Count == 0)
            {
                // Index fallback contains exact ledger identities/directions but not per-line values.
                // Keep amount zero rather than inventing an allocation.
                foreach (var name in debitLedgers.Where(name => name.Length > 0))
                    AddCapturedIndexLedgerLine(sourceLedgerLines, name, "Dr", ledgerParents, groupParents);
                foreach (var name in creditLedgers.Where(name => name.Length > 0))
                    AddCapturedIndexLedgerLine(sourceLedgerLines, name, "Cr", ledgerParents, groupParents);
            }
            var firstCapturedOther = sourceLedgerLines
                .FirstOrDefault(line => !line.Ledger.Equals(party, StringComparison.OrdinalIgnoreCase))?.Ledger
                ?? ledgers.FirstOrDefault(name => !name.Equals(party, StringComparison.OrdinalIgnoreCase))
                ?? string.Empty;
            var firstCapturedOtherGroup = ResolveLedgerGroup(firstCapturedOther, ledgerParents, groupParents);
            var voucherPartyGstin = voucherXml is not null ? ExtractTallyPartyGstin(voucherXml.OriginalXml) : string.Empty;
            var resolvedPartyGstin = voucherPartyGstin.Length == 15
                ? voucherPartyGstin
                : ledgerGstins.TryGetValue(party, out var partyMasterGstin) ? partyMasterGstin : string.Empty;
            result.Add(new AuditRecordRow
            {
                Date = voucher.VoucherDate,
                Reference = reference,
                PartyOrLedger = party,
                Gstin = resolvedPartyGstin,
                Counterparty = string.Join(" | ", ledgers.Where(name => !name.Equals(party, StringComparison.OrdinalIgnoreCase))),
                CounterpartyGroup = partyGroup,
                BusinessGroup = firstCapturedOtherGroup,
                BusinessLedger = firstCapturedOther,
                TaxLedgers = taxEvidence.TaxLedgers,
                TaxableValue = taxEvidence.TaxableValue,
                Cgst = taxEvidence.Cgst,
                Sgst = taxEvidence.Sgst,
                Igst = taxEvidence.Igst,
                Cess = taxEvidence.Cess,
                UnallocatedGst = taxEvidence.UnallocatedGst,
                GstRate = taxEvidence.GstRate,
                GstRateKnown = taxEvidence.GstRateKnown,
                TaxEvidenceSource = taxEvidence.Source,
                VoucherType = type,
                AuthorityId = authorityId,
                TallyMasterId = voucher.MasterId?.Trim() ?? string.Empty,
                TallyGuid = voucher.Guid?.Trim() ?? string.Empty,
                SourceModule = module,
                Category = category,
                TransactionClass = type,
                Details = narration,
                OriginalNarration = narration,
                Amount = amount,
                Status = alterStatus,
                EvidenceStatus = taxEvidence.HasTaxEvidence ? "Live Tally voucher + tax-ledger authority" : "Live Tally voucher authority",
                SourceEvidence = taxEvidence.Source,
                Source = "Explicit Audit Tally Link",
                AdvancedVoucher = new AuditVoucherAdvancedData
                {
                    AccountingLinesAuthoritative = false,
                    LedgerLines = sourceLedgerLines
                }
            });
        }
        return result.ToArray();
    }

    private static string ExtractTallyPartyGstin(string? originalXml)
    {
        if (string.IsNullOrWhiteSpace(originalXml)) return string.Empty;
        try
        {
            var document = XDocument.Parse(originalXml, LoadOptions.None);
            var preferred = document.Descendants()
                .Where(element =>
                {
                    var name = AuditNormalizeXmlName(element.Name.LocalName);
                    return name is "PARTYGSTIN" or "CONSIGNEEGSTIN" or "GSTIN";
                })
                .Select(element => NormalizePartyGstin(element.Value))
                .FirstOrDefault(value => value.Length == 15);
            return preferred ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private void EnrichCommercePartyGstinFromMasterAuthority(AuditClientProfile client, IReadOnlyCollection<AuditRecordRow> rows)
    {
        if (rows.Count == 0 || string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)) return;
        var gstinByLedger = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ledger in _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey))
        {
            var gstin = NormalizePartyGstin(ledger.Gstin);
            if (gstin.Length != 15) continue;
            if (!string.IsNullOrWhiteSpace(ledger.DisplayName)) gstinByLedger[ledger.DisplayName.Trim()] = gstin;
            if (!string.IsNullOrWhiteSpace(ledger.ExactName)) gstinByLedger[ledger.ExactName.Trim()] = gstin;
        }
        if (gstinByLedger.Count == 0) return;
        foreach (var row in rows)
        {
            if (NormalizePartyGstin(row.Gstin).Length == 15) continue;
            var party = (row.PartyOrLedger ?? string.Empty).Trim();
            if (party.Length > 0 && gstinByLedger.TryGetValue(party, out var masterGstin)) row.Gstin = masterGstin;
        }
    }

    private static string ClassifyLedgerByGroup(
        string ledger,
        IReadOnlyDictionary<string, string> ledgerParents,
        IReadOnlyDictionary<string, string> groupParents)
    {
        if (string.IsNullOrWhiteSpace(ledger) || !ledgerParents.TryGetValue(ledger.Trim(), out var current) || current.Length == 0)
            return string.Empty;
        for (var depth = 0; depth < 24 && current.Length > 0; depth++)
        {
            if (current.Equals("Bank Accounts", StringComparison.OrdinalIgnoreCase)) return "Bank";
            if (current.Equals("Bank OD A/c", StringComparison.OrdinalIgnoreCase) ||
                current.Equals("Bank OD Accounts", StringComparison.OrdinalIgnoreCase) ||
                current.Equals("Bank OCC A/c", StringComparison.OrdinalIgnoreCase)) return "OD";
            if (current.Equals("Secured Loans", StringComparison.OrdinalIgnoreCase)) return "Loans";
            if (current.Equals("Cash-in-Hand", StringComparison.OrdinalIgnoreCase) || current.Equals("Cash in Hand", StringComparison.OrdinalIgnoreCase)) return "Cash";
            if (current.Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase)) return "Sundry Debtors";
            if (current.Equals("Sundry Creditors", StringComparison.OrdinalIgnoreCase)) return "Sundry Creditors";
            if (!groupParents.TryGetValue(current, out var parent) || string.IsNullOrWhiteSpace(parent) || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent.Trim();
        }
        return string.Empty;
    }

    private static string ResolveCounterpartyGroup(
        IEnumerable<string> counterparties,
        IReadOnlyDictionary<string, string> ledgerParents,
        IReadOnlyDictionary<string, string> groupParents)
    {
        foreach (var counterparty in counterparties)
        {
            var group = ResolveLedgerGroup(counterparty, ledgerParents, groupParents);
            if (group.Length > 0) return group;
        }
        return "Other / Unclassified";
    }

    private static string ResolveLedgerGroup(
        string ledger,
        IReadOnlyDictionary<string, string> ledgerParents,
        IReadOnlyDictionary<string, string> groupParents)
    {
        if (string.IsNullOrWhiteSpace(ledger) || !ledgerParents.TryGetValue(ledger.Trim(), out var parent) || string.IsNullOrWhiteSpace(parent))
            return string.Empty;

        var recognised = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Bank Accounts", "Bank OD A/c", "Bank OD Accounts", "Bank OCC A/c", "Secured Loans", "Unsecured Loans",
            "Cash-in-Hand", "Cash in Hand", "Sundry Debtors", "Sundry Creditors", "Direct Expenses", "Indirect Expenses",
            "Direct Incomes", "Indirect Incomes", "Sales Accounts", "Purchase Accounts", "Duties & Taxes", "Capital Account",
            "Loans (Liability)", "Current Assets", "Current Liabilities", "Fixed Assets", "Investments", "Stock-in-Hand",
            "Deposits (Asset)", "Loans & Advances (Asset)", "Provisions", "Reserves & Surplus", "Suspense A/c"
        };

        var firstParent = parent.Trim();
        var current = firstParent;
        for (var depth = 0; depth < 24 && current.Length > 0; depth++)
        {
            if (recognised.Contains(current)) return current;
            if (!groupParents.TryGetValue(current, out var next) || string.IsNullOrWhiteSpace(next) || next.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
            current = next.Trim();
        }
        return firstParent;
    }

    private static string FirstCapturedLedger(params string[] values) =>
        values.Select(value => (value ?? string.Empty).Trim()).FirstOrDefault(value => value.Length > 0) ?? string.Empty;

    private static void AddCapturedIndexLedgerLine(
        List<AuditVoucherLedgerLineData> target,
        string ledger,
        string direction,
        IReadOnlyDictionary<string, string> ledgerParents,
        IReadOnlyDictionary<string, string> groupParents)
    {
        var name = (ledger ?? string.Empty).Trim();
        if (name.Length == 0 || target.Any(line => line.Ledger.Equals(name, StringComparison.OrdinalIgnoreCase) && line.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase))) return;
        target.Add(new AuditVoucherLedgerLineData
        {
            Ledger = name,
            Group = ResolveLedgerGroup(name, ledgerParents, groupParents),
            Direction = direction,
            Amount = 0m,
            Role = "Source"
        });
    }

    private static List<AuditVoucherLedgerLineData> ExtractTallySourceLedgerLines(
        string originalXml,
        IReadOnlyDictionary<string, string> ledgerParents,
        IReadOnlyDictionary<string, string> groupParents)
    {
        var result = new List<AuditVoucherLedgerLineData>();
        if (string.IsNullOrWhiteSpace(originalXml)) return result;
        try
        {
            var document = XDocument.Parse(originalXml, LoadOptions.None);
            var voucher = document.Descendants().FirstOrDefault(element => AuditNormalizeXmlName(element.Name.LocalName).Equals("VOUCHER", StringComparison.OrdinalIgnoreCase))
                          ?? document.Root;
            if (voucher is null) return result;

            foreach (var entry in voucher.Descendants().Where(element =>
                         element.Elements().Any(child => AuditNormalizeXmlName(child.Name.LocalName).Equals("LEDGERNAME", StringComparison.OrdinalIgnoreCase)) &&
                         element.Elements().Any(child => AuditNormalizeXmlName(child.Name.LocalName).Equals("AMOUNT", StringComparison.OrdinalIgnoreCase))))
            {
                var ledger = AuditXmlDirectChildText(entry, "LEDGERNAME").Trim();
                if (ledger.Length == 0) continue;
                var signedAmount = AuditParseDecimal(AuditXmlDirectChildText(entry, "AMOUNT"));
                var deemedPositive = AuditXmlDirectChildText(entry, "ISDEEMEDPOSITIVE").Trim();
                var direction = deemedPositive.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                    ? "Dr"
                    : deemedPositive.Equals("No", StringComparison.OrdinalIgnoreCase)
                        ? "Cr"
                        : signedAmount < 0m ? "Dr" : "Cr";
                var costCentre = entry.Descendants()
                    .FirstOrDefault(element => AuditNormalizeXmlName(element.Name.LocalName).Equals("COSTCENTRENAME", StringComparison.OrdinalIgnoreCase))?.Value?.Trim()
                    ?? string.Empty;
                result.Add(new AuditVoucherLedgerLineData
                {
                    Ledger = ledger,
                    Group = ResolveLedgerGroup(ledger, ledgerParents, groupParents),
                    Direction = direction,
                    Amount = Math.Abs(signedAmount),
                    CostCentre = costCentre,
                    Narration = AuditXmlDirectChildText(entry, "NARRATION"),
                    Role = "Source"
                });
            }
        }
        catch
        {
            // Source evidence remains fail-soft for presentation; index ledger identities are used by caller.
        }
        return result;
    }

    private static TallyCommerceTaxEvidence ExtractTallyCommerceTaxEvidence(
        string originalXml,
        string module,
        string party,
        decimal voucherAmount,
        IReadOnlyDictionary<string, string> ledgerParents,
        IReadOnlyDictionary<string, string> groupParents)
    {
        if (string.IsNullOrWhiteSpace(originalXml)) return TallyCommerceTaxEvidence.Empty;
        try
        {
            var document = XDocument.Parse(originalXml, LoadOptions.None);
            var voucher = document.Descendants().FirstOrDefault(element => AuditNormalizeXmlName(element.Name.LocalName).Equals("VOUCHER", StringComparison.OrdinalIgnoreCase))
                          ?? document.Root;
            if (voucher is null) return TallyCommerceTaxEvidence.Empty;

            decimal taxable = 0m, cgst = 0m, sgst = 0m, igst = 0m, cess = 0m, roundOff = 0m;
            var taxLedgers = new List<string>();
            var businessLedgers = new List<string>();
            foreach (var entry in voucher.Descendants().Where(element =>
                         element.Elements().Any(child => AuditNormalizeXmlName(child.Name.LocalName).Equals("LEDGERNAME", StringComparison.OrdinalIgnoreCase)) &&
                         element.Elements().Any(child => AuditNormalizeXmlName(child.Name.LocalName).Equals("AMOUNT", StringComparison.OrdinalIgnoreCase))))
            {
                var ledger = AuditXmlDirectChildText(entry, "LEDGERNAME").Trim();
                if (ledger.Length == 0 || ledger.Equals(party, StringComparison.OrdinalIgnoreCase)) continue;
                var amount = Math.Abs(AuditParseDecimal(AuditXmlDirectChildText(entry, "AMOUNT")));
                if (amount <= 0.0001m) continue;
                var group = ResolveLedgerGroup(ledger, ledgerParents, groupParents);
                var descriptor = (ledger + " " + group).ToUpperInvariant();
                if (descriptor.Contains("ROUND OFF") || descriptor.Contains("ROUNDOFF")) { roundOff += amount; continue; }
                if (descriptor.Contains("CGST") || descriptor.Contains("CENTRAL GST") || descriptor.Contains("CENTRAL GOODS")) { cgst += amount; taxLedgers.Add(ledger); continue; }
                if (descriptor.Contains("SGST") || descriptor.Contains("STATE GST") || descriptor.Contains("STATE GOODS")) { sgst += amount; taxLedgers.Add(ledger); continue; }
                if (descriptor.Contains("IGST") || descriptor.Contains("INTEGRATED GST") || descriptor.Contains("INTEGRATED GOODS")) { igst += amount; taxLedgers.Add(ledger); continue; }
                if (descriptor.Contains("CESS")) { cess += amount; taxLedgers.Add(ledger); continue; }

                var isBusiness = module.Equals("Sales", StringComparison.OrdinalIgnoreCase)
                    ? group is "Sales Accounts" or "Direct Incomes" or "Indirect Incomes"
                    : group is "Purchase Accounts" or "Direct Expenses" or "Indirect Expenses" or "Fixed Assets" or "Current Assets" or "Stock-in-Hand";
                if (isBusiness)
                {
                    taxable += amount;
                    if (!businessLedgers.Contains(ledger, StringComparer.OrdinalIgnoreCase)) businessLedgers.Add(ledger);
                }
            }

            var knownTax = cgst + sgst + igst + cess;
            var unallocated = 0m;
            if (taxable > 0m && voucherAmount > 0m)
            {
                var residual = voucherAmount - taxable - knownTax;
                if (Math.Abs(residual) <= roundOff + 0.05m) residual = 0m;
                else if (residual > 0m) unallocated = residual;
            }
            var gstForRate = cgst + sgst + igst + unallocated;
            var taxLedgerText = string.Join(" | ", taxLedgers.Distinct(StringComparer.OrdinalIgnoreCase));
            var explicitRateKnown = TryResolveTaxLedgerGstRate(taxLedgerText, out var explicitRate);
            var effectiveRate = taxable > 0m && gstForRate > 0m
                ? Math.Round(gstForRate * 100m / taxable, 4, MidpointRounding.AwayFromZero)
                : 0m;
            var normalizedEffectiveRate = 0m;
            var effectiveRateKnown = !explicitRateKnown && TryNormalizeStandardGstRate(effectiveRate, out normalizedEffectiveRate);
            var rateKnown = explicitRateKnown || effectiveRateKnown;
            var rate = explicitRateKnown ? explicitRate : effectiveRateKnown ? normalizedEffectiveRate : 0m;
            var hasTax = knownTax > 0m || unallocated > 0m;
            var source = hasTax
                ? explicitRateKnown
                    ? unallocated > 0m ? "Tally tax-ledger rate authority + residual GST" : "Tally tax-ledger rate authority"
                    : unallocated > 0m ? "Tally voucher tax ledgers + residual GST (rate unavailable)" : "Tally voucher tax ledger postings (rate unavailable)"
                : taxable > 0m ? "Tally voucher business-ledger postings" : string.Empty;
            return new TallyCommerceTaxEvidence
            {
                TaxableValue = taxable,
                Cgst = cgst,
                Sgst = sgst,
                Igst = igst,
                Cess = cess,
                UnallocatedGst = unallocated,
                GstRate = rate,
                GstRateKnown = rateKnown,
                TaxLedgers = taxLedgerText,
                BusinessLedger = string.Join(" | ", businessLedgers.Distinct(StringComparer.OrdinalIgnoreCase)),
                Source = source,
                HasTaxEvidence = hasTax
            };
        }
        catch
        {
            return TallyCommerceTaxEvidence.Empty;
        }
    }

    private static string AuditXmlDirectChildText(XElement element, string name)
    {
        var wanted = AuditNormalizeXmlName(name);
        var child = element.Elements().FirstOrDefault(candidate => AuditNormalizeXmlName(candidate.Name.LocalName).Equals(wanted, StringComparison.OrdinalIgnoreCase));
        return child?.Value?.Trim() ?? string.Empty;
    }

    private static string AuditNormalizeXmlName(string value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static decimal AuditParseDecimal(string? value)
    {
        var text = (value ?? string.Empty).Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        if (decimal.TryParse(text, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return 0m;
    }

    private sealed class TallyCommerceTaxEvidence
    {
        public static TallyCommerceTaxEvidence Empty { get; } = new();
        public decimal TaxableValue { get; init; }
        public decimal Cgst { get; init; }
        public decimal Sgst { get; init; }
        public decimal Igst { get; init; }
        public decimal Cess { get; init; }
        public decimal UnallocatedGst { get; init; }
        public decimal GstRate { get; init; }
        public bool GstRateKnown { get; init; }
        public string TaxLedgers { get; init; } = string.Empty;
        public string BusinessLedger { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public bool HasTaxEvidence { get; init; }
    }

    private static string ResolveAuditSourceBusinessLedger(
        string module,
        string itemLedger,
        string headerLedger,
        string party,
        string igstLedger,
        string cgstLedger,
        string sgstLedger,
        decimal rate,
        IReadOnlyDictionary<string, string> ledgerParents,
        IReadOnlyDictionary<string, string> groupParents)
    {
        var taxLedgers = new[] { igstLedger, cgstLedger, sgstLedger }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        foreach (var candidate in new[] { itemLedger, headerLedger })
        {
            var value = (candidate ?? string.Empty).Trim();
            if (value.Length == 0 || value.Equals(party, StringComparison.OrdinalIgnoreCase) || IsCommerceRoundOffLedger(value)) continue;
            if (taxLedgers.Any(tax => value.Equals(tax.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
            if (ResolveLedgerGroup(value, ledgerParents, groupParents).Equals("Duties & Taxes", StringComparison.OrdinalIgnoreCase)) continue;
            return value;
        }

        var rateText = rate.ToString("0.####", CultureInfo.InvariantCulture);
        return module.Equals("Sales", StringComparison.OrdinalIgnoreCase)
            ? rate > 0m ? $"Sales @ {rateText}%" : "Exempted Sales"
            : rate > 0m ? $"Purchase @ {rateText}%" : "Exempted Purchase";
    }

    private static string ResolveAuditSourceBusinessGroup(
        string module,
        string itemLedger,
        string headerLedger,
        string party,
        string igstLedger,
        string cgstLedger,
        string sgstLedger,
        decimal rate,
        IReadOnlyDictionary<string, string> ledgerParents,
        IReadOnlyDictionary<string, string> groupParents)
    {
        var ledger = ResolveAuditSourceBusinessLedger(module, itemLedger, headerLedger, party, igstLedger, cgstLedger, sgstLedger, rate, ledgerParents, groupParents);
        var resolved = ResolveLedgerGroup(ledger, ledgerParents, groupParents);
        return resolved.Length > 0
            ? resolved
            : module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? "Sales Accounts" : "Purchase Accounts";
    }

    private static string ResolveCommerceBusinessGroup(
        string module,
        IEnumerable<string> ledgers,
        string party,
        IReadOnlyDictionary<string, string> ledgerParents,
        IReadOnlyDictionary<string, string> groupParents)
    {
        var candidates = ledgers
            .Where(name => !name.Equals(party, StringComparison.OrdinalIgnoreCase))
            .Select(name => new { Name = name, Group = ResolveLedgerGroup(name, ledgerParents, groupParents) })
            .Where(item => !IsCommerceRoundOffLedger(item.Name))
            .ToArray();
        if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase))
        {
            var match = candidates.FirstOrDefault(item => item.Group.Equals("Sales Accounts", StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault(item => item.Group.Equals("Direct Incomes", StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault(item => item.Group.Equals("Indirect Incomes", StringComparison.OrdinalIgnoreCase));
            return match?.Group ?? "Sales Accounts";
        }
        if (module.Equals("Purchase", StringComparison.OrdinalIgnoreCase))
        {
            var match = candidates.FirstOrDefault(item => item.Group.Equals("Purchase Accounts", StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault(item => item.Group.Equals("Direct Expenses", StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault(item => item.Group.Equals("Indirect Expenses", StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault(item => item.Group.Equals("Fixed Assets", StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault(item => item.Group.Equals("Current Assets", StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault(item => item.Group.Equals("Stock-in-Hand", StringComparison.OrdinalIgnoreCase));
            return match?.Group ?? "Purchase Accounts";
        }
        return string.Empty;
    }

    private static string ResolveCommerceBusinessLedger(
        string module,
        IEnumerable<string> ledgers,
        string party,
        IReadOnlyDictionary<string, string> ledgerParents,
        IReadOnlyDictionary<string, string> groupParents)
    {
        var candidates = ledgers
            .Where(name => !name.Equals(party, StringComparison.OrdinalIgnoreCase))
            .Select(name => new { Name = name, Group = ResolveLedgerGroup(name, ledgerParents, groupParents) })
            .Where(item => !IsCommerceRoundOffLedger(item.Name))
            .ToArray();
        if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase))
            return candidates.FirstOrDefault(item => item.Group.Equals("Sales Accounts", StringComparison.OrdinalIgnoreCase))?.Name
                   ?? candidates.FirstOrDefault(item => item.Group.Equals("Direct Incomes", StringComparison.OrdinalIgnoreCase))?.Name
                   ?? candidates.FirstOrDefault(item => item.Group.Equals("Indirect Incomes", StringComparison.OrdinalIgnoreCase))?.Name
                   ?? string.Empty;
        if (module.Equals("Purchase", StringComparison.OrdinalIgnoreCase))
            return candidates.FirstOrDefault(item => item.Group.Equals("Purchase Accounts", StringComparison.OrdinalIgnoreCase))?.Name
                   ?? candidates.FirstOrDefault(item => item.Group.Equals("Direct Expenses", StringComparison.OrdinalIgnoreCase))?.Name
                   ?? candidates.FirstOrDefault(item => item.Group.Equals("Indirect Expenses", StringComparison.OrdinalIgnoreCase))?.Name
                   ?? candidates.FirstOrDefault(item => item.Group.Equals("Fixed Assets", StringComparison.OrdinalIgnoreCase))?.Name
                   ?? candidates.FirstOrDefault(item => item.Group.Equals("Current Assets", StringComparison.OrdinalIgnoreCase))?.Name
                   ?? candidates.FirstOrDefault(item => item.Group.Equals("Stock-in-Hand", StringComparison.OrdinalIgnoreCase))?.Name
                   ?? string.Empty;
        return string.Empty;
    }

    private static string FinanceMovementType(string financeCategory, bool isDebit, bool isCredit, string group, bool hasCash, bool contra)
    {
        if (financeCategory.Equals("Loans", StringComparison.OrdinalIgnoreCase))
            return isDebit ? "Loan Repayment" : isCredit ? "Loan Receipt" : "Loan Movement";
        if (hasCash) return isCredit ? "Cash Withdrawal" : isDebit ? "Cash Deposit" : "Bank ↔ Cash";
        if (contra) return isCredit ? "Inter-bank Payment" : isDebit ? "Inter-bank Receipt" : "Inter-bank / Contra";
        if (isCredit)
        {
            if (group.Equals("Sundry Creditors", StringComparison.OrdinalIgnoreCase)) return "Creditor Payment";
            if (group.Contains("Expense", StringComparison.OrdinalIgnoreCase)) return "Expense Payment";
            if (group.Contains("Loan", StringComparison.OrdinalIgnoreCase)) return "Loan Repayment";
            return "Payment";
        }
        if (isDebit)
        {
            if (group.Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase)) return "Debtor Receipt";
            if (group.Contains("Loan", StringComparison.OrdinalIgnoreCase)) return "Loan Receipt";
            return "Receipt";
        }
        return "Movement";
    }

    private static string CashMovementType(bool isDebit, bool isCredit, string group, bool hasFinance)
    {
        if (hasFinance) return isDebit ? "Cash Withdrawal from Bank" : isCredit ? "Cash Deposit to Bank" : "Bank ↔ Cash";
        if (isCredit)
        {
            if (group.Equals("Sundry Creditors", StringComparison.OrdinalIgnoreCase)) return "Creditor Cash Payment";
            if (group.Contains("Expense", StringComparison.OrdinalIgnoreCase)) return "Cash Expense Payment";
            return "Cash Payment";
        }
        if (isDebit)
        {
            if (group.Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase)) return "Debtor Cash Receipt";
            return "Cash Receipt";
        }
        return "Cash Movement";
    }

    private static string SalesCategory(string? voucherType)
    {
        var type = (voucherType ?? string.Empty).Trim();
        if (type.Contains("Credit Note", StringComparison.OrdinalIgnoreCase)) return "Credit Notes";
        if (type.Contains("Debit Note", StringComparison.OrdinalIgnoreCase)) return "Debit Notes / Other";
        return "Sales Invoices";
    }

    private static string PurchaseCategory(string? voucherType)
    {
        var type = (voucherType ?? string.Empty).Trim();
        if (type.Contains("Debit Note", StringComparison.OrdinalIgnoreCase)) return "Debit Notes";
        if (type.Contains("Credit Note", StringComparison.OrdinalIgnoreCase)) return "Credit Notes / Other";
        return "Purchase Invoices";
    }

    private static string CashTransactionClass(string? voucherType, string? narration)
    {
        var text = $"{voucherType} {narration}";
        if (text.Contains("receipt", StringComparison.OrdinalIgnoreCase)) return "Cash Receipts";
        if (text.Contains("payment", StringComparison.OrdinalIgnoreCase)) return "Cash Payments";
        return "Cash Entries";
    }

    private static string FinanceTransactionClass(string? voucherType, bool hasCash)
    {
        if (hasCash) return "Bank ↔ Cash";
        var type = (voucherType ?? string.Empty).Trim();
        if (type.Contains("Contra", StringComparison.OrdinalIgnoreCase)) return "Contra / Inter-bank";
        if (type.Contains("Receipt", StringComparison.OrdinalIgnoreCase)) return "Receipts";
        if (type.Contains("Payment", StringComparison.OrdinalIgnoreCase)) return "Payments";
        return type.Length > 0 ? type : "Bank Entries";
    }

    private static bool IsIssueText(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return false;
        return text.Contains("review", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("difference", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unmapped", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("required", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("pending", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("failed", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record LedgerClass(string Name, string Category);

    private AuditDashboardCard[] BuildDashboardCards(AuditClientProfile client, IReadOnlyDictionary<string, int> counts, int auditStoredCount)
    {
        var tallyLinked = string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey) ? 0 : 1;
        var cards = new List<AuditDashboardCard>
        {
            new("Audit Records", auditStoredCount, "Rows stored directly in the independent Audit workspace · open Sources & Links", "Sources"),
            new("Accounting Workbench", 1, "Uses Accounting-style module/edit logic on this Audit client's data only", "Sales"),
            new("Tally Link", tallyLinked, tallyLinked == 1 ? (client.LinkedTallyCompanyName.Length > 0 ? client.LinkedTallyCompanyName : client.LinkedTallyCompanyKey) : "No Tally company is linked", "Sources"),
            new("Audit Client", 1, $"{client.ClientCode} · FY {client.FinancialYear}", "Settings")
        };
        void AddModuleCard(string title, string key, string detail)
        {
            var count = counts.GetValueOrDefault(key);
            if (count > 0) cards.Add(new AuditDashboardCard(title, count, detail, key));
        }
        AddModuleCard("Sales", "Sales", "Sales audit dashboard · customers · GST · items · returns");
        AddModuleCard("Purchase", "Purchase", "Purchase audit dashboard · vendors · ITC · 2B · RCM");
        AddModuleCard("Banking & Finance", "BankingFinance", "Bank Accounts / Bank OD / Secured Loans");
        AddModuleCard("Cash", "Cash", "Cash-in-Hand movement dashboard");
        AddModuleCard("Debtors & Creditors", "DebtorsCreditors", "Opening + closing balance review for Sundry Debtors / Sundry Creditors");
        AddModuleCard("Balance Sheet", "BalanceSheet", "All asset, liability, capital and other Balance Sheet groups");
        AddModuleCard("Profit & Loss", "ProfitLoss", "All Sales, Purchase, Direct/Indirect Income and Expense groups");
        AddModuleCard("Journal", "Journal", "Audit Journal authority");
        AddModuleCard("GST / GSTR-3B", "GST", "GST evidence workspace");
        AddModuleCard("26AS", "26AS", "Tax-credit evidence workspace");
        AddModuleCard("Client Reconciliation", "ClientReconciliation", "Client-ledger reconciliation workspace");
        return cards.ToArray();
    }


    private (AuditStatisticRow[] Masters, AuditStatisticRow[] Vouchers) BuildAuditStatistics(
        AuditClientProfile client,
        int auditStoredCount)
    {
        if (string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey))
        {
            return (
                new[]
                {
                    new AuditStatisticRow("Audit Records", auditStoredCount, "Audit-owned stored rows"),
                    new AuditStatisticRow("Tally Masters", 0, "Link Tally to populate Master statistics")
                },
                new[] { new AuditStatisticRow("FY Vouchers", 0, "Link Tally to populate Voucher statistics") });
        }

        EnsureLinkedTallyEvidenceInitialized();
        var ledgers = _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
        var groups = _auditRepository.GetTallyGroupParents(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
        var statementRows = BuildFinancialStatementRows(client, "All");
        var (from, to) = AdvancedClosingEntryPlanner.FinancialYearRange(client.FinancialYear);
        var vouchers = _tallyRepository.GetVoucherIndexRange(client.LinkedTallyCompanyKey, client.FinancialYear, from, to);

        var debtorCount = ledgers.Count(ledger => ResolveGroupChain(ledger.Parent, groups).Any(group => group.Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase)));
        var creditorCount = ledgers.Count(ledger => ResolveGroupChain(ledger.Parent, groups).Any(group => group.Equals("Sundry Creditors", StringComparison.OrdinalIgnoreCase)));
        var masterStats = new List<AuditStatisticRow>
        {
            new("Groups", groups.Count, "Audit-owned Tally Group hierarchy"),
            new("Ledgers", ledgers.Count, "Audit-owned Tally Ledger authority"),
            new("Ledgers with MASTERID", ledgers.Count(x => !string.IsNullOrWhiteSpace(x.MasterId)), "Durable Tally identity"),
            new("Ledgers with Closing", ledgers.Count(x => x.ClosingBalance.HasValue), "FY closing balance authority"),
            new("Sundry Debtors", debtorCount, "Debtor ledgers"),
            new("Sundry Creditors", creditorCount, "Creditor ledgers"),
            new("Balance Sheet Ledgers", statementRows.Count(x => x.Statement == "Balance Sheet" && !x.IsGroupSummary), "All asset/liability/capital groups"),
            new("P&L Ledgers", statementRows.Count(x => x.Statement == "Profit & Loss" && !x.IsGroupSummary), "All income/expense groups"),
            new("Audit Records", auditStoredCount, "Independent Audit stored rows")
        };

        static bool TypeContains(TallyVoucherIndexRecord v, string text) =>
            (v.VoucherType ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase);
        var known = vouchers.Count(v => TypeContains(v, "Sales") || TypeContains(v, "Purchase") || TypeContains(v, "Debit Note") ||
                                      TypeContains(v, "Credit Note") || TypeContains(v, "Receipt") || TypeContains(v, "Payment") ||
                                      TypeContains(v, "Contra") || TypeContains(v, "Journal"));
        var voucherStats = new[]
        {
            new AuditStatisticRow("All FY Vouchers", vouchers.Count, $"{from:dd-MM-yyyy} to {to:dd-MM-yyyy}"),
            new AuditStatisticRow("Sales", vouchers.Count(v => TypeContains(v, "Sales") && !TypeContains(v, "Credit Note")), "Sales vouchers"),
            new AuditStatisticRow("Purchase", vouchers.Count(v => TypeContains(v, "Purchase") && !TypeContains(v, "Debit Note")), "Purchase vouchers"),
            new AuditStatisticRow("Debit Notes", vouchers.Count(v => TypeContains(v, "Debit Note")), "Purchase returns / Debit Notes"),
            new AuditStatisticRow("Credit Notes", vouchers.Count(v => TypeContains(v, "Credit Note")), "Sales returns / Credit Notes"),
            new AuditStatisticRow("Receipts", vouchers.Count(v => TypeContains(v, "Receipt")), "Receipt vouchers"),
            new AuditStatisticRow("Payments", vouchers.Count(v => TypeContains(v, "Payment")), "Payment vouchers"),
            new AuditStatisticRow("Contra", vouchers.Count(v => TypeContains(v, "Contra")), "Contra vouchers"),
            new AuditStatisticRow("Journal", vouchers.Count(v => TypeContains(v, "Journal")), "Journal vouchers"),
            new AuditStatisticRow("Other", Math.Max(0, vouchers.Count - known), "Other voucher types")
        };
        return (masterStats.ToArray(), voucherStats);
    }

    private AuditFinancialStatementRow[] BuildFinancialStatementRows(AuditClientProfile client, string statement)
    {
        if (string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)) return Array.Empty<AuditFinancialStatementRow>();
        var ledgers = _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
        var groupParents = _auditRepository.GetTallyGroupParents(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (ledgers.Count == 0) return Array.Empty<AuditFinancialStatementRow>();
        var transactionCounts = BuildTallyLedgerTransactionCounts(client);

        var rows = new List<AuditFinancialStatementRow>();
        foreach (var ledger in ledgers)
        {
            var parent = (ledger.Parent ?? string.Empty).Trim();
            var chain = ResolveGroupChain(parent, groupParents);
            var primary = ResolveStatementPrimaryGroup(chain);
            var isProfitLoss = ProfitLossPrimaryGroups.Contains(primary);
            var statementName = isProfitLoss ? "Profit & Loss" : "Balance Sheet";
            if (!statement.Equals("All", StringComparison.OrdinalIgnoreCase) && !statementName.Equals(statement, StringComparison.OrdinalIgnoreCase)) continue;
            var side = ResolveStatementSide(statementName, primary, ledger.ClosingBalance);
            rows.Add(new AuditFinancialStatementRow
            {
                Statement = statementName,
                Side = side,
                PrimaryGroup = primary.Length > 0 ? primary : (parent.Length > 0 ? parent : "Other / Unclassified"),
                LedgerGroup = parent,
                Ledger = ledger.DisplayName.Trim().Length > 0 ? ledger.DisplayName.Trim() : ledger.ExactName.Trim(),
                OpeningBalance = ledger.OpeningBalance,
                ClosingBalance = ledger.ClosingBalance,
                TransactionCount = ResolveLedgerTransactionCount(transactionCounts, ledger.DisplayName, ledger.ExactName),
                MasterId = ledger.MasterId,
                Gstin = ledger.Gstin,
                IsGroupSummary = false
            });
        }

        var summaries = rows
            .GroupBy(row => new { row.Statement, row.Side, row.PrimaryGroup })
            .Select(group => new AuditFinancialStatementRow
            {
                Statement = group.Key.Statement,
                Side = group.Key.Side,
                PrimaryGroup = group.Key.PrimaryGroup,
                LedgerGroup = group.Key.PrimaryGroup,
                Ledger = $"{group.Key.PrimaryGroup} TOTAL",
                OpeningBalance = group.Sum(row => row.OpeningBalance ?? 0m),
                ClosingBalance = group.Sum(row => row.ClosingBalance ?? 0m),
                TransactionCount = group.Sum(row => row.TransactionCount),
                LedgerCount = group.Count(),
                IsGroupSummary = true
            });
        rows.AddRange(summaries);
        return rows
            .OrderBy(row => row.Statement, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Side, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.PrimaryGroup, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(row => row.IsGroupSummary)
            .ThenBy(row => row.LedgerGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Ledger, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private Dictionary<string, int> BuildTallyLedgerTransactionCounts(AuditClientProfile client)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)) return result;
        EnsureLinkedTallyEvidenceInitialized();
        var (from, to) = AdvancedClosingEntryPlanner.FinancialYearRange(client.FinancialYear);
        var vouchers = _tallyRepository.GetVoucherIndexRange(client.LinkedTallyCompanyKey, client.FinancialYear, from, to);
        foreach (var voucher in vouchers)
        {
            var names = SplitLedgerNames(voucher.DebitLedgers)
                .Concat(SplitLedgerNames(voucher.CreditLedgers))
                .Append((voucher.PartyLedger ?? string.Empty).Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
                result[name] = result.GetValueOrDefault(name) + 1;
        }
        return result;
    }

    private Dictionary<string, Dictionary<string, int>> BuildTallyLedgerVoucherTypeCounts(AuditClientProfile client)
    {
        var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)) return result;
        EnsureLinkedTallyEvidenceInitialized();
        var (from, to) = AdvancedClosingEntryPlanner.FinancialYearRange(client.FinancialYear);
        var vouchers = _tallyRepository.GetVoucherIndexRange(client.LinkedTallyCompanyKey, client.FinancialYear, from, to);
        foreach (var voucher in vouchers)
        {
            var voucherType = (voucher.VoucherType ?? string.Empty).Trim();
            if (voucherType.Length == 0) voucherType = "Unclassified";
            var names = SplitLedgerNames(voucher.DebitLedgers)
                .Concat(SplitLedgerNames(voucher.CreditLedgers))
                .Append((voucher.PartyLedger ?? string.Empty).Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (!result.TryGetValue(name, out var typeCounts))
                {
                    typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    result[name] = typeCounts;
                }
                typeCounts[voucherType] = typeCounts.GetValueOrDefault(voucherType) + 1;
            }
        }
        return result;
    }

    private sealed class LedgerMovementTotals
    {
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
    }

    private Dictionary<string, LedgerMovementTotals> BuildTallyLedgerMovementTotals(AuditClientProfile client)
    {
        var result = new Dictionary<string, LedgerMovementTotals>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)) return result;
        EnsureLinkedTallyEvidenceInitialized();
        var (from, to) = AdvancedClosingEntryPlanner.FinancialYearRange(client.FinancialYear);
        var vouchers = _tallyRepository.GetVoucherIndexRange(client.LinkedTallyCompanyKey, client.FinancialYear, from, to);
        var xmlByMasterId = _tallyRepository.GetVoucherXmlAuthorityByMasterIds(client.LinkedTallyCompanyKey, client.FinancialYear, vouchers.Select(voucher => voucher.MasterId))
            .Where(snapshot => snapshot.MasterId.Trim().Length > 0)
            .GroupBy(snapshot => snapshot.MasterId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var emptyGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var voucher in vouchers)
        {
            if (voucher.LedgerPostings.Count > 0)
            {
                foreach (var posting in voucher.LedgerPostings)
                {
                    var ledger = (posting.LedgerName ?? string.Empty).Trim();
                    if (ledger.Length == 0) continue;
                    if (!result.TryGetValue(ledger, out var totals))
                    {
                        totals = new LedgerMovementTotals();
                        result[ledger] = totals;
                    }
                    if (posting.IsDebit) totals.Debit += posting.Amount;
                    else totals.Credit += posting.Amount;
                }
                continue;
            }

            var masterId = (voucher.MasterId ?? string.Empty).Trim();
            if (masterId.Length == 0 || !xmlByMasterId.TryGetValue(masterId, out var snapshot)) continue;
            foreach (var line in ExtractTallySourceLedgerLines(snapshot.OriginalXml, emptyGroups, emptyGroups))
            {
                var ledger = (line.Ledger ?? string.Empty).Trim();
                if (ledger.Length == 0) continue;
                if (!result.TryGetValue(ledger, out var totals))
                {
                    totals = new LedgerMovementTotals();
                    result[ledger] = totals;
                }
                if (line.Direction.Equals("Dr", StringComparison.OrdinalIgnoreCase)) totals.Debit += Math.Abs(line.Amount);
                else if (line.Direction.Equals("Cr", StringComparison.OrdinalIgnoreCase)) totals.Credit += Math.Abs(line.Amount);
            }
        }
        return result;
    }

    private static LedgerMovementTotals ResolveLedgerMovementTotals(
        IReadOnlyDictionary<string, LedgerMovementTotals> totals,
        string? displayName,
        string? exactName)
    {
        var display = (displayName ?? string.Empty).Trim();
        var exact = (exactName ?? string.Empty).Trim();
        if (display.Length > 0 && totals.TryGetValue(display, out var displayTotals)) return displayTotals;
        if (exact.Length > 0 && totals.TryGetValue(exact, out var exactTotals)) return exactTotals;
        return new LedgerMovementTotals();
    }

    private static IReadOnlyDictionary<string, int> ResolveLedgerVoucherTypeCounts(
        IReadOnlyDictionary<string, Dictionary<string, int>> counts,
        string? displayName,
        string? exactName)
    {
        var display = (displayName ?? string.Empty).Trim();
        var exact = (exactName ?? string.Empty).Trim();
        if (display.Length > 0 && counts.TryGetValue(display, out var displayCounts)) return displayCounts;
        if (exact.Length > 0 && counts.TryGetValue(exact, out var exactCounts)) return exactCounts;
        return EmptyVoucherTypeCounts;
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyVoucherTypeCounts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static string FormatLedgerVoucherTypeDetails(IReadOnlyDictionary<string, int> counts)
    {
        if (counts.Count == 0) return string.Empty;
        return string.Join(", ", counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}({pair.Value.ToString("N0", IndianCulture)})"));
    }

    private static int ResolveLedgerTransactionCount(IReadOnlyDictionary<string, int> counts, string? displayName, string? exactName)
    {
        var display = (displayName ?? string.Empty).Trim();
        var exact = (exactName ?? string.Empty).Trim();
        if (display.Length > 0 && counts.TryGetValue(display, out var displayCount)) return displayCount;
        if (exact.Length > 0 && counts.TryGetValue(exact, out var exactCount)) return exactCount;
        return 0;
    }

    private static readonly HashSet<string> ProfitLossPrimaryGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sales Accounts", "Purchase Accounts", "Direct Incomes", "Indirect Incomes", "Direct Expenses", "Indirect Expenses"
    };

    private static readonly HashSet<string> BalanceSheetAssetGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "Current Assets", "Fixed Assets", "Investments", "Misc. Expenses (ASSET)"
    };

    private static readonly HashSet<string> BalanceSheetLiabilityGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "Capital Account", "Loans (Liability)", "Current Liabilities", "Branch / Divisions", "Reserves & Surplus", "Provisions"
    };

    private static string[] ResolveGroupChain(string parent, IReadOnlyDictionary<string, string> groupParents)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = (parent ?? string.Empty).Trim();
        for (var depth = 0; depth < 32 && current.Length > 0 && seen.Add(current); depth++)
        {
            result.Add(current);
            if (!groupParents.TryGetValue(current, out var next) || string.IsNullOrWhiteSpace(next) || next.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
            current = next.Trim();
            if (current.Equals("Primary", StringComparison.OrdinalIgnoreCase) || current.Equals("$Primary", StringComparison.OrdinalIgnoreCase)) break;
        }
        return result.ToArray();
    }

    private static string ResolveStatementPrimaryGroup(IEnumerable<string> chain)
    {
        var values = chain.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        foreach (var value in values)
            if (ProfitLossPrimaryGroups.Contains(value)) return value;
        foreach (var value in values.Reverse())
            if (BalanceSheetAssetGroups.Contains(value) || BalanceSheetLiabilityGroups.Contains(value) || value.Equals("Suspense A/c", StringComparison.OrdinalIgnoreCase)) return value;
        return values.LastOrDefault() ?? string.Empty;
    }

    private static string ResolveStatementSide(string statement, string primaryGroup, decimal? closingBalance)
    {
        if (statement.Equals("Profit & Loss", StringComparison.OrdinalIgnoreCase))
            return primaryGroup is "Sales Accounts" or "Direct Incomes" or "Indirect Incomes" ? "Income" : "Expense";
        if (BalanceSheetAssetGroups.Contains(primaryGroup)) return "Assets";
        if (BalanceSheetLiabilityGroups.Contains(primaryGroup)) return "Liabilities";
        return (closingBalance ?? 0m) >= 0m ? "Assets" : "Liabilities";
    }

    private static AuditMetricTile[] BuildFinancialStatementMetrics(string module, IReadOnlyCollection<AuditFinancialStatementRow> rows)
    {
        if (module is not "BalanceSheet" and not "ProfitLoss") return Array.Empty<AuditMetricTile>();
        var ledgers = rows.Where(row => !row.IsGroupSummary).ToArray();
        if (module.Equals("BalanceSheet", StringComparison.OrdinalIgnoreCase))
        {
            var assets = Math.Abs(ledgers.Where(row => row.Side == "Assets").Sum(row => row.ClosingBalance ?? 0m));
            var liabilities = Math.Abs(ledgers.Where(row => row.Side == "Liabilities").Sum(row => row.ClosingBalance ?? 0m));
            return new[]
            {
                new AuditMetricTile("Groups", rows.Count(row => row.IsGroupSummary).ToString("N0", IndianCulture), "Balance Sheet primary groups"),
                new AuditMetricTile("Ledgers", ledgers.Length.ToString("N0", IndianCulture), "All Balance Sheet ledgers"),
                new AuditMetricTile("Assets · Dr", FormatMoney(assets), "Debit-nature closing balances"),
                new AuditMetricTile("Liabilities · Cr", FormatMoney(liabilities), "Credit-nature closing balances"),
                new AuditMetricTile("Closing Missing", ledgers.Count(row => !row.ClosingBalance.HasValue).ToString("N0", IndianCulture), "Ledgers without Tally closing authority")
            };
        }
        var income = Math.Abs(ledgers.Where(row => row.Side == "Income").Sum(row => row.ClosingBalance ?? 0m));
        var expense = Math.Abs(ledgers.Where(row => row.Side == "Expense").Sum(row => row.ClosingBalance ?? 0m));
        var result = income - expense;
        return new[]
        {
            new AuditMetricTile("P&L Groups", rows.Count(row => row.IsGroupSummary).ToString("N0", IndianCulture), "Sales/Purchase/Direct/Indirect groups"),
            new AuditMetricTile("Ledgers", ledgers.Length.ToString("N0", IndianCulture), "All P&L ledgers"),
            new AuditMetricTile("Income · Cr", FormatMoney(income), "Credit-nature income closing balances"),
            new AuditMetricTile("Expense · Dr", FormatMoney(expense), "Debit-nature expense closing balances"),
            new AuditMetricTile(result >= 0m ? "Net Credit" : "Net Debit", FormatMoney(Math.Abs(result)), "Income less Expense from closing ledger authority")
        };
    }

    private static bool IsEffectiveZeroLedgerBalance(decimal? openingBalance, decimal? closingBalance, int transactionCount)
    {
        if (closingBalance.HasValue) return closingBalance.Value == 0m;
        return transactionCount == 0 && openingBalance.HasValue && openingBalance.Value == 0m;
    }

    private void ApplyFinancialStatementFilter()
    {
        if (VisibleFinancialStatementRows is null || FinancialStatementRows is null) return;
        var ledgers = FinancialStatementRows
            .Where(row => !row.IsGroupSummary)
            .Where(row => _ledgerVisibilityMode switch
            {
                1 => !IsEffectiveZeroLedgerBalance(row.OpeningBalance, row.ClosingBalance, row.TransactionCount),
                2 => row.TransactionCount > 0,
                _ => true
            })
            .ToArray();

        var summaries = ledgers
            .GroupBy(row => new { row.Statement, row.Side, row.PrimaryGroup })
            .Select(group => new AuditFinancialStatementRow
            {
                Statement = group.Key.Statement,
                Side = group.Key.Side,
                PrimaryGroup = group.Key.PrimaryGroup,
                LedgerGroup = group.Key.PrimaryGroup,
                Ledger = $"{group.Key.PrimaryGroup} TOTAL",
                OpeningBalance = group.Sum(row => row.OpeningBalance ?? 0m),
                ClosingBalance = group.Sum(row => row.ClosingBalance ?? 0m),
                TransactionCount = group.Sum(row => row.TransactionCount),
                LedgerCount = group.Count(),
                IsGroupSummary = true
            });

        var visible = ledgers.Concat(summaries)
            .OrderBy(row => row.Statement, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Side, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.PrimaryGroup, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(row => row.IsGroupSummary)
            .ThenBy(row => row.LedgerGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Ledger, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        VisibleFinancialStatementRows.ReplaceWith(visible);
        FinancialStatementMetrics.ReplaceWith(BuildFinancialStatementMetrics(SelectedModule?.Key ?? string.Empty, visible));
    }

    private static string FormatSignedBalance(decimal? value)
    {
        if (!value.HasValue) return "Not available";
        if (value.Value > 0m) return $"{FormatMoney(Math.Abs(value.Value))} Dr";
        if (value.Value < 0m) return $"{FormatMoney(Math.Abs(value.Value))} Cr";
        return "0.00";
    }

    private AuditSourceRow[] BuildSources(AuditClientProfile? client)
    {
        if (client is null)
        {
            return new[]
            {
                new AuditSourceRow { Source = "Audit Client", Status = "Not selected", Detail = "Audit uses its own client directory." },
                new AuditSourceRow { Source = "Accounting", Status = "Isolated", Detail = "No Accounting client is shared automatically." },
                new AuditSourceRow { Source = "Tally", Status = "Isolated", Detail = "Tally transport is shared, but CompanyKey ownership is client-specific and cross-checked with Accounting." }
            };
        }

        return new[]
        {
            new AuditSourceRow
            {
                Source = "Audit Workspace",
                PathOrAuthority = $"Audit Client #{client.Id} · {client.ClientCode}",
                Status = "Independent",
                Detail = "Audit clients, records and current-client selection are stored separately from Accounting."
            },
            new AuditSourceRow
            {
                Source = "Accounting",
                PathOrAuthority = "Independent workbench only",
                Status = "Not a data source",
                Detail = "R4 R9: Accounting module UI/edit logic opens only the independent Audit workbench. LinkedAccountingClientId is never read as transaction authority and no Accounting client is selected or modified."
            },
            new AuditSourceRow
            {
                Source = "Tally Link",
                PathOrAuthority = client.LinkedTallyCompanyKey.Length > 0 ? (client.LinkedTallyCompanyName.Length > 0 ? client.LinkedTallyCompanyName : client.LinkedTallyCompanyKey) : "Not linked",
                Status = client.LinkedTallyCompanyKey.Length > 0 ? "Explicitly linked" : "Not linked",
                Detail = client.LinkedTallyCompanyKey.Length > 0
                    ? "This CompanyKey is reserved across Accounting + Audit. It may be shared only with this Audit client's explicitly linked Accounting client; unrelated clients are blocked."
                    : "Tally may be running with another company open, but Audit has no CompanyKey authority until this client is explicitly linked. Accounting/Audit sharing is allowed only through the matching client relationship."
            },
            new AuditSourceRow
            {
                Source = "Tally Master Groups",
                PathOrAuthority = client.LinkedTallyCompanyKey.Length > 0
                    ? $"{FormatCount(_auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey).Count)} Ledger authority row(s)"
                    : "Not linked",
                Status = client.LinkedTallyCompanyKey.Length > 0 && _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey).Count > 0
                    ? "Classified"
                    : "Awaiting Tally Master refresh",
                Detail = "Audit-owned Ledger → Group authority classifies Bank Accounts, Bank OD, Secured Loans, Cash-in-Hand, Sundry Debtors and Sundry Creditors."
            },
            new AuditSourceRow
            {
                Source = "Audit Imports",
                PathOrAuthority = $"{FormatCount(_auditRepository.GetBankStatements(client.Id, client.FinancialYear).Count)} Audit Bank/OD statement source(s)",
                Status = _auditRepository.GetBankStatements(client.Id, client.FinancialYear).Count > 0 ? "Active" : "Ready",
                Detail = "Excel/CSV/native-text PDF statement evidence is stored only in audit-hub.db and assigned to the selected Tally Bank/OD ledger."
            }
        };
    }

    private void RebuildModulePresentation(string module)
    {
        var previousKey = _selectedModuleTab?.Key;
        var tabs = BuildModuleTabs(module, Rows);
        ModuleTabs.ReplaceWith(tabs);
        MetricTiles.ReplaceWith(BuildMetricTiles(module, Rows, BankSummaries));
        var selected = previousKey is not null
            ? ModuleTabs.FirstOrDefault(tab => tab.Key.Equals(previousKey, StringComparison.OrdinalIgnoreCase))
            : null;
        selected ??= ModuleTabs.FirstOrDefault(tab => tab.Key.Equals("Dashboard", StringComparison.OrdinalIgnoreCase));
        selected ??= ModuleTabs.FirstOrDefault(tab => tab.Key.Equals("Overview", StringComparison.OrdinalIgnoreCase));
        selected ??= ModuleTabs.FirstOrDefault(tab => tab.Key.Equals("All", StringComparison.OrdinalIgnoreCase));
        selected ??= ModuleTabs.FirstOrDefault();
        _selectedModuleTab = selected;
        foreach (var tab in ModuleTabs) tab.IsSelected = ReferenceEquals(tab, selected);
        OnPropertyChanged(nameof(SelectedModuleTab));
        RaiseBankingTabVisibility();
        SelectedRecord = null;
        RebuildFinancePresentation(module);
        RebuildCommercePresentation(module);
        RebuildGstr3bVerification(module);
        RebuildEntryAdjustments(module);
        ApplyRowFilter();
    }

    private void RebuildGstr3bVerification(string module)
    {
        Gstr3bVerificationRows.Clear();
        OnPropertyChanged(nameof(IsGstModule));
        if (!module.Equals("GST", StringComparison.OrdinalIgnoreCase)) return;

        // R4 R9: never borrow another Accounting client's GST reconciliation. Audit GST
        // verification must come from Audit-owned evidence/returns. Until such rows are imported,
        // the verification grid stays empty rather than silently reading linked Accounting data.
    }

    private void RebuildEntryAdjustments(string module)
    {
        EntryAdjustments.Clear();
        var client = SelectedAuditClient;
        if (client is null || string.IsNullOrWhiteSpace(module)) return;
        foreach (var item in _auditRepository.GetEntryAdjustments(client.Id, client.FinancialYear, module))
            EntryAdjustments.Add(item);
    }

    private bool CanOpenAuditAccountingWorkbench() => IsAccountingWorkbenchAvailable;

    private void OpenAuditAccountingWorkbench()
    {
        var client = SelectedAuditClient;
        var module = SelectedModule?.Key ?? string.Empty;
        if (client is null || module.Length == 0 || !IsAccountingWorkbenchAvailable) return;

        try
        {
            var rows = CurrentAccountingWorkbenchRows();
            var context = CurrentAccountingWorkbenchFilterText();
            var ledgers = BuildAuditWorkbenchLedgerOptions(client, rows);
            var owner = FindAuditOwner();
            var window = new AuditAccountingWorkbenchWindow(
                _auditRepository, client.Clone(), module, NormalizeAccountingWorkbenchModule(module), context, rows, ledgers,
                () =>
                {
                    InvalidateCurrentAuditProjection();
                    _ = RefreshAsync(forceAll: true);
                })
            {
                Owner = owner
            };
            Status = $"Opened independent Accounting workbench · {NormalizeAccountingWorkbenchModule(module)} · {rows.Length:N0} filtered row(s).";
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            Status = $"Accounting workbench could not open: {exception.Message}";
            MessageBox.Show(FindAuditOwner(), Status, "Open in Accounting", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private AuditRecordRow[] CurrentAccountingWorkbenchRows()
    {
        var module = SelectedModule?.Key ?? string.Empty;
        IEnumerable<AuditRecordRow> rows = Rows;

        if (IsCommerceModule)
        {
            rows = GetPreferredCommerceRows(module);
            if (_commerceSelectedGroup.Length > 0 || _commerceSelectedRate.Length > 0 || _commerceSelectedItem.Length > 0 ||
                _commerceSelectedMonthOrder > 0 || _commerceSelectedSupplyType.Length > 0 || _commerceSelectedVoucherType.Length > 0)
                rows = rows.Where(CommerceRowMatchesCurrentDrilldown);
            if (_commerceSelectedParty.Length > 0)
                rows = rows.Where(row => row.PartyOrLedger.Equals(_commerceSelectedParty, StringComparison.OrdinalIgnoreCase));
        }
        else if (IsFinanceAccountDashboardModule)
        {
            if (SelectedFinanceAccount is not null)
                rows = rows.Where(row => row.PartyOrLedger.Equals(SelectedFinanceAccount.Account, StringComparison.OrdinalIgnoreCase));

            rows = _financeDrilldownMode switch
            {
                "Payments" => rows.Where(IsPaymentMovement),
                "Receipts" => rows.Where(IsReceiptMovement),
                "PaymentGroup" => rows.Where(row => IsPaymentMovement(row) && NormalizeFinanceGroup(row).Equals(_financeDrilldownGroup, StringComparison.OrdinalIgnoreCase)),
                "ReceiptGroup" => rows.Where(row => IsReceiptMovement(row) && NormalizeFinanceGroup(row).Equals(_financeDrilldownGroup, StringComparison.OrdinalIgnoreCase)),
                "PaymentGroupParty" => rows.Where(row => IsPaymentMovement(row) && NormalizeFinanceGroup(row).Equals(_financeDrilldownGroup, StringComparison.OrdinalIgnoreCase) && CounterpartyDrilldownKey(row).Equals(_financeDrilldownCounterparty, StringComparison.OrdinalIgnoreCase)),
                "ReceiptGroupParty" => rows.Where(row => IsReceiptMovement(row) && NormalizeFinanceGroup(row).Equals(_financeDrilldownGroup, StringComparison.OrdinalIgnoreCase) && CounterpartyDrilldownKey(row).Equals(_financeDrilldownCounterparty, StringComparison.OrdinalIgnoreCase)),
                "CashWithdrawal" => rows.Where(IsCashWithdrawalMovement),
                "CashDeposit" => rows.Where(IsCashDepositMovement),
                "CashBank" => rows.Where(row =>
                    (row.Counterparty.Split(" | ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                         .Any(value => value.Equals(_financeDrilldownBank, StringComparison.OrdinalIgnoreCase)) ||
                     row.PartyOrLedger.Equals(_financeDrilldownBank, StringComparison.OrdinalIgnoreCase)) &&
                    (IsCashWithdrawalMovement(row) || IsCashDepositMovement(row))),
                _ => rows
            };
        }
        else if (IsStandardStructuredRowsVisible || IsGenericDataModule)
        {
            rows = VisibleRows.Count > 0 ? VisibleRows : Rows;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
            rows = rows.Where(row => AccountingWorkbenchMatchesSearch(row, SearchText));

        return rows.Select(CloneAuditRow).ToArray();
    }

    private static string NormalizeFinanceGroup(AuditRecordRow row) =>
        string.IsNullOrWhiteSpace(row.CounterpartyGroup) ? "Other / Unclassified" : row.CounterpartyGroup.Trim();

    private static bool AccountingWorkbenchMatchesSearch(AuditRecordRow row, string searchText)
    {
        var tokens = (searchText ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 0)
            .ToArray();
        if (tokens.Length == 0) return true;
        var haystack = string.Join(" | ", new[]
        {
            row.DateText, row.Reference, row.VoucherType, row.AuthorityId, row.PartyOrLedger, row.Counterparty,
            row.CounterpartyGroup, row.BusinessGroup, row.BusinessLedger, row.TaxLedgers, row.Gstin, row.ItemName,
            row.HsnCode, row.Unit, row.Category, row.TransactionClass, row.Details, row.OriginalNarration, row.Status,
            row.ReconciliationStatus, row.ItcStatus, row.EvidenceStatus, row.SourceEvidence, row.Source,
            row.Debit.ToString("0.00"), row.Credit.ToString("0.00"), row.Amount.ToString("0.00"),
            row.TaxableValue.ToString("0.00"), row.GstAmount.ToString("0.00"), row.GstRate.ToString("0.####")
        });
        return tokens.All(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private string CurrentAccountingWorkbenchFilterText()
    {
        var parts = new List<string>();
        if (_commerceSelectedGroup.Length > 0) parts.Add($"Group: {_commerceSelectedGroup}");
        if (_commerceSelectedVoucherType.Length > 0) parts.Add($"Voucher Type: {_commerceSelectedVoucherType}");
        if (_commerceSelectedRate.Length > 0) parts.Add($"GST: {_commerceSelectedRate}");
        if (_commerceSelectedItem.Length > 0) parts.Add($"Item: {_commerceSelectedItem}");
        if (_commerceSelectedMonthOrder > 0) parts.Add(CommerceMonthSummaries.FirstOrDefault(row => row.MonthOrder == _commerceSelectedMonthOrder)?.Month ?? "Selected month");
        if (_commerceSelectedSupplyType.Length > 0) parts.Add($"Supply: {_commerceSelectedSupplyType}");
        if (_commerceSelectedParty.Length > 0) parts.Add($"Party: {_commerceSelectedParty}");
        if (SelectedFinanceAccount is not null) parts.Add($"Account: {SelectedFinanceAccount.Account}");
        if (_financeDrilldownGroup.Length > 0) parts.Add($"Group: {_financeDrilldownGroup}");
        if (_financeDrilldownCounterparty.Length > 0) parts.Add($"Party: {_financeDrilldownCounterparty}");
        if (_financeDrilldownBank.Length > 0) parts.Add($"Bank: {_financeDrilldownBank}");
        if (_financeDrilldownMode.Length > 0) parts.Add(_financeDrilldownMode);
        if (!string.IsNullOrWhiteSpace(SearchText)) parts.Add($"Search: {SearchText}");
        return parts.Count == 0 ? "Entire current Audit module" : string.Join(" · ", parts);
    }

    private static string NormalizeAccountingWorkbenchModule(string module) => module switch
    {
        "Bank" or "OD" or "Loans" or "BankingFinance" => "Banking",
        "DebtorsCreditors" => "Debtors & Creditors",
        _ => module
    };

    private string[] BuildAuditWorkbenchLedgerOptions(AuditClientProfile client, IEnumerable<AuditRecordRow> rows)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.PartyOrLedger)) values.Add(row.PartyOrLedger.Trim());
            if (!string.IsNullOrWhiteSpace(row.Counterparty)) values.Add(row.Counterparty.Trim());
            if (!string.IsNullOrWhiteSpace(row.BusinessLedger)) values.Add(row.BusinessLedger.Trim());
        }
        if (!string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey))
        {
            foreach (var ledger in _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey))
            {
                if (!string.IsNullOrWhiteSpace(ledger.DisplayName)) values.Add(ledger.DisplayName.Trim());
                if (!string.IsNullOrWhiteSpace(ledger.ExactName)) values.Add(ledger.ExactName.Trim());
            }
        }
        return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static AuditRecordRow CloneAuditRow(AuditRecordRow row) =>
        JsonSerializer.Deserialize<AuditRecordRow>(JsonSerializer.Serialize(row)) ?? new AuditRecordRow();

    private void InvalidateCurrentAuditProjection()
    {
        var client = SelectedAuditClient;
        var module = SelectedModule?.Key ?? string.Empty;
        if (client is null || module.Length == 0) return;
        RegisterAuditProjection(client.Clone(), module, invalidate: true, priority: true);
    }

    private async void ModifySelectedEntry()
    {
        var row = SelectedRecord;
        var client = SelectedAuditClient;
        var module = SelectedModule?.Key ?? string.Empty;
        if (row is null || client is null || module.Length == 0) return;

        var canApplyAah = client.LinkedAccountingClientId is > 0 && row.SourceRecordId > 0 &&
            row.SourceModule is "Sales" or "Purchase" or "Journal" or "BankingFinance";
        var canApplyTally = !string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey) && !string.IsNullOrWhiteSpace(row.TallyMasterId);
        var dialog = new AuditEntryModifyWindow(row, canApplyAah, canApplyTally)
        {
            Owner = System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
        };
        if (row.SourceModule == "BankingFinance" && !string.IsNullOrWhiteSpace(row.Counterparty))
            dialog.Draft.PartyOrLedger = row.Counterparty;
        if (dialog.ShowDialog() != true) return;

        var adjustment = new AuditEntryAdjustment
        {
            AuditClientId = client.Id,
            FinancialYear = client.FinancialYear,
            Module = module,
            SourceRecordId = row.SourceRecordId,
            Source = row.Source,
            SourceIdentity = row.AuthorityId.Length > 0 ? row.AuthorityId : $"{row.SourceModule}:{row.SourceRecordId}",
            TallyMasterId = row.TallyMasterId,
            TallyGuid = row.TallyGuid,
            Reason = dialog.Draft.Reason,
            Action = dialog.Draft.Action,
            Status = "Suggested",
            OriginalJson = JsonSerializer.Serialize(row),
            ProposedJson = JsonSerializer.Serialize(dialog.Draft)
        };
        _auditRepository.SaveEntryAdjustment(adjustment);

        try
        {
            if (!dialog.Draft.Action.Equals("Suggestion", StringComparison.OrdinalIgnoreCase))
            {
                if (canApplyAah)
                {
                    ApplyDraftToLinkedAccounting(client, row, dialog.Draft);
                    adjustment.Status = "Applied AAH";
                    _auditRepository.SaveEntryAdjustment(adjustment);
                }
                else if (!dialog.Draft.Action.Equals("ApplyAahTally", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("This Audit row has no exact linked Accounting record identity. It can be saved as an Audit Suggestion, but AAH will not be modified by fuzzy matching.");
                }
            }

            if (dialog.Draft.Action.Equals("ApplyAahTally", StringComparison.OrdinalIgnoreCase))
            {
                if (!canApplyTally)
                    throw new InvalidOperationException("Exact Tally Voucher MASTERID is not available for this row. The Tally alteration was not attempted.");
                if (!IsLedgerOnlyTallyChange(row, dialog.Draft))
                {
                    adjustment.Status = canApplyAah ? "Applied AAH / Pending Tally" : "Suggested / Pending Tally";
                    adjustment.Failure = "Automatic Tally ALTER is fail-closed for this edit. Only an exact ledger substitution with saved Voucher MASTERID is currently allowed; date/amount/tax/voucher-shape changes are not guessed into Tally XML.";
                    _auditRepository.SaveEntryAdjustment(adjustment);
                    throw new InvalidOperationException(adjustment.Failure);
                }

                adjustment.Status = "Pending Tally";
                _auditRepository.SaveEntryAdjustment(adjustment);
                var response = await _auditTallyLinkService.AlterExactVoucherLedgerAsync(client, row.TallyMasterId, row.PartyOrLedger, dialog.Draft.PartyOrLedger);
                if (response.Errors > 0 || response.Exceptions > 0 || !string.IsNullOrWhiteSpace(response.LineError) || response.Altered <= 0)
                    throw new InvalidOperationException($"Tally did not confirm the exact voucher alteration. Altered={response.Altered}; Ignored={response.Ignored}; Errors={response.Errors}; Exceptions={response.Exceptions}; {response.LineError}");
                adjustment.Status = "Verified";
                adjustment.Failure = string.Empty;
                _auditRepository.SaveEntryAdjustment(adjustment);
            }

            RebuildEntryAdjustments(module);
            StartBackgroundPreparation(invalidate: true);
            await RefreshAsync(forceAll: true);
            Status = dialog.Draft.Action.Equals("Suggestion", StringComparison.OrdinalIgnoreCase)
                ? "Audit suggestion saved. Original evidence remains unchanged."
                : $"Audit correction recorded: {adjustment.Status}.";
        }
        catch (Exception ex)
        {
            if (!adjustment.Status.Contains("Pending Tally", StringComparison.OrdinalIgnoreCase) &&
                !adjustment.Status.Equals("Applied AAH", StringComparison.OrdinalIgnoreCase))
                adjustment.Status = "Failed";
            adjustment.Failure = ex.Message;
            _auditRepository.SaveEntryAdjustment(adjustment);
            RebuildEntryAdjustments(module);
            System.Windows.MessageBox.Show(dialog, ex.Message, "Audit Entry Modify", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyDraftToLinkedAccounting(AuditClientProfile client, AuditRecordRow row, AuditEntryEditDraft draft)
    {
        if (client.LinkedAccountingClientId is not > 0) throw new InvalidOperationException("Linked Accounting client is required.");
        var accountingId = client.LinkedAccountingClientId.Value;
        const decimal tolerance = 0.005m;
        switch (row.SourceModule)
        {
            case "Sales":
            {
                var record = _salesRepository.GetRecords(accountingId, client.FinancialYear).FirstOrDefault(x => x.PersistentId == row.SourceRecordId)
                    ?? throw new InvalidOperationException("Exact linked Sales record no longer exists.");
                record.DocumentDate = draft.Date; record.DocumentNumber = draft.Reference; record.DocumentType = draft.VoucherType;
                record.CustomerName = draft.PartyOrLedger; record.DocumentValue = draft.Amount; record.TaxableValue = draft.TaxableValue;
                record.Rate = draft.GstRate; record.Igst = draft.Igst; record.Cgst = draft.Cgst; record.Sgst = draft.Sgst; record.Cess = draft.Cess;
                record.Narration = draft.Narration; record.CostCentre = draft.CostCentre; record.IsUserEdited = true;
                if (_salesRepository.UpdateRecords(accountingId, new[] { record }) != 1) throw new InvalidOperationException("Sales update did not affect the exact linked row.");
                break;
            }
            case "Purchase":
            {
                var record = _purchaseRepository.GetRecords(accountingId, client.FinancialYear).FirstOrDefault(x => x.PersistentId == row.SourceRecordId)
                    ?? throw new InvalidOperationException("Exact linked Purchase record no longer exists.");
                record.DocumentDate = draft.Date; record.DocumentNumber = draft.Reference; record.DocumentType = draft.VoucherType;
                record.SupplierName = draft.PartyOrLedger; record.DocumentValue = draft.Amount; record.TaxableValue = draft.TaxableValue;
                record.Rate = draft.GstRate; record.Igst = draft.Igst; record.Cgst = draft.Cgst; record.Sgst = draft.Sgst; record.Cess = draft.Cess;
                record.Narration = draft.Narration; record.CostCentre = draft.CostCentre; record.IsUserEdited = true;
                if (_purchaseRepository.UpdateRecords(accountingId, new[] { record }) != 1) throw new InvalidOperationException("Purchase update was not accepted for this exact row (manual/non-Auto-Import rows remain fail-closed). Save it as an Audit Suggestion instead.");
                break;
            }
            case "Journal":
            {
                var voucher = _journalRepository.GetVouchers(accountingId, client.FinancialYear).FirstOrDefault(x => x.PersistentId == row.SourceRecordId)
                    ?? throw new InvalidOperationException("Exact linked Journal voucher no longer exists.");
                if (Math.Abs(draft.Amount - row.Amount) > tolerance || Math.Abs(draft.TaxableValue-row.TaxableValue)>tolerance ||
                    Math.Abs(draft.Igst-row.Igst)>tolerance || Math.Abs(draft.Cgst-row.Cgst)>tolerance || Math.Abs(draft.Sgst-row.Sgst)>tolerance || Math.Abs(draft.Cess-row.Cess)>tolerance)
                    throw new InvalidOperationException("Journal amount/tax reshaping requires balanced line construction and is not applied from Audit automatically. Save the proposed change as an Audit Suggestion.");
                voucher.VoucherDate = draft.Date ?? voucher.VoucherDate; voucher.VoucherNumber = draft.Reference; voucher.ReferenceNumber = draft.Reference;
                voucher.VoucherType = draft.VoucherType; voucher.Narration = draft.Narration;
                if (!string.Equals(draft.PartyOrLedger, row.PartyOrLedger, StringComparison.OrdinalIgnoreCase) && voucher.Lines.Count > 0) voucher.Lines[0].LedgerName = draft.PartyOrLedger;
                _journalRepository.SaveVoucher(voucher);
                break;
            }
            case "BankingFinance":
            {
                var tx = _bankingRepository.GetTransactions(accountingId, client.FinancialYear).FirstOrDefault(x => x.Id == row.SourceRecordId)
                    ?? throw new InvalidOperationException("Exact linked Banking transaction no longer exists.");
                if (!string.Equals(draft.VoucherType, row.VoucherType, StringComparison.OrdinalIgnoreCase) ||
                    Math.Abs(draft.TaxableValue-row.TaxableValue)>tolerance || Math.Abs(draft.Igst-row.Igst)>tolerance || Math.Abs(draft.Cgst-row.Cgst)>tolerance || Math.Abs(draft.Sgst-row.Sgst)>tolerance || Math.Abs(draft.Cess-row.Cess)>tolerance)
                    throw new InvalidOperationException("Banking Audit apply supports source transaction date/reference/narration/Dr/Cr/ledger only; tax or voucher-shape changes remain suggestions.");
                tx.TransactionDate = draft.Date ?? tx.TransactionDate; tx.ReferenceNumber = draft.Reference; tx.Narration = draft.Narration;
                tx.Debit = Math.Abs(draft.Debit); tx.Credit = Math.Abs(draft.Credit); tx.LedgerName = draft.PartyOrLedger;
                _bankingRepository.SaveTransaction(tx);
                break;
            }
            default:
                throw new InvalidOperationException("This Audit source does not have an exact supported Accounting write-back route.");
        }

        if (row.SourceModule is "Sales" or "Purchase")
        {
            var journalGeneration = new GstJournalGenerationService(_journalRepository, _purchaseRepository, _salesRepository);
            journalGeneration.Generate(accountingId, client.FinancialYear);
            var gstReconciliation = new GstReconciliationService(_gstRepository, _journalRepository, _purchaseRepository);
            gstReconciliation.Reconcile(accountingId, client.FinancialYear);
        }
        else if (row.SourceModule == "Journal")
        {
            var gstReconciliation = new GstReconciliationService(_gstRepository, _journalRepository, _purchaseRepository);
            gstReconciliation.Reconcile(accountingId, client.FinancialYear);
        }
    }

    private static bool IsLedgerOnlyTallyChange(AuditRecordRow row, AuditEntryEditDraft draft)
    {
        const decimal t = 0.005m;
        return !string.Equals(row.PartyOrLedger, draft.PartyOrLedger, StringComparison.OrdinalIgnoreCase) &&
               Nullable.Equals(row.Date?.Date, draft.Date?.Date) && row.Reference == draft.Reference && row.VoucherType == draft.VoucherType &&
               Math.Abs(row.Debit-draft.Debit)<=t && Math.Abs(row.Credit-draft.Credit)<=t && Math.Abs(row.Amount-draft.Amount)<=t &&
               Math.Abs(row.TaxableValue-draft.TaxableValue)<=t && Math.Abs(row.GstRate-draft.GstRate)<=t && Math.Abs(row.Igst-draft.Igst)<=t &&
               Math.Abs(row.Cgst-draft.Cgst)<=t && Math.Abs(row.Sgst-draft.Sgst)<=t && Math.Abs(row.Cess-draft.Cess)<=t &&
               row.OriginalNarration == draft.Narration && row.CostCentre == draft.CostCentre;
    }

    private void RebuildCommercePresentation(string module)
    {
        CommerceGroupSummaries.Clear();
        CommerceLedgerSummaries.Clear();
        CommerceVoucherTypeSummaries.Clear();
        CommerceHsnSummaries.Clear();
        CommerceVoucherRows.Clear();
        CommerceMonthChartSeries = Array.Empty<ISeries>();
        CommerceMonthChartXAxes = Array.Empty<Axis>();
        CommerceMonthChartYAxes = Array.Empty<Axis>();
        OnPropertyChanged(nameof(CommerceMonthChartSeries));
        OnPropertyChanged(nameof(CommerceMonthChartXAxes));
        OnPropertyChanged(nameof(CommerceMonthChartYAxes));
        CommercePartySummaries.Clear();
        CommerceDrilldownPartySummaries.Clear();
        CommerceGstRateSummaries.Clear();
        CommerceItemSummaries.Clear();
        CommerceAdjustmentSummaries.Clear();
        CommerceMonthSummaries.Clear();
        CommerceTaxAmountSummaries.Clear();
        CommerceSupplySummaries.Clear();
        CommerceExtraVoucherRows.Clear();
        CommerceBooksVsGstr1Rows.Clear();
        CommerceBooksVsTwoARows.Clear();
        CommerceBooksVsTwoBRows.Clear();
        CommerceAlertSummaries.Clear();
        CommerceReconciliationTiles.Clear();
        CommercePartyDetailTiles.Clear();
        SelectedCommerceParty = null;
        ResetCommerceDrilldown();

        if (module is not ("Sales" or "Purchase")) return;

        var analysisRows = GetPreferredCommerceRows(module);
        var totalAmount = analysisRows.Sum(row => row.Amount);
        var client = SelectedAuditClient;
        var balanceLookup = new Dictionary<string, (decimal? Opening, decimal? Closing)>(StringComparer.OrdinalIgnoreCase);
        var ledgerParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (client is not null && !string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey))
        {
            foreach (var pair in _auditRepository.GetTallyGroupParents(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey))
                groupParents[pair.Key] = pair.Value;
            foreach (var ledger in _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey))
            {
                if (!string.IsNullOrWhiteSpace(ledger.DisplayName))
                {
                    var name = ledger.DisplayName.Trim();
                    ledgerParents[name] = ledger.Parent.Trim();
                    balanceLookup[name] = (ledger.OpeningBalance, ledger.ClosingBalance);
                }
                if (!string.IsNullOrWhiteSpace(ledger.ExactName))
                {
                    var name = ledger.ExactName.Trim();
                    ledgerParents[name] = ledger.Parent.Trim();
                    balanceLookup[name] = (ledger.OpeningBalance, ledger.ClosingBalance);
                }
            }
        }

        // Fix109: rate classification is authority-first. Explicit Tally tax-ledger rates
        // override odd effective percentages; unsupported effective percentages remain
        // diagnostic rather than becoming fake GST slabs.
        NormalizeCommerceRateAuthority(analysisRows);

        CommerceExtraVoucherRows.ReplaceWith(BuildCommerceExtraVoucherRows(module, ledgerParents, groupParents));
        SyncCommerceExtraVoucherTab();

        string BusinessGroup(AuditRecordRow row)
        {
            var raw = string.IsNullOrWhiteSpace(row.BusinessGroup) ? row.Category : row.BusinessGroup.Trim();
            var resolved = ResolveLedgerGroup(raw, ledgerParents, groupParents);
            return resolved.Length > 0 ? resolved : raw.Length > 0 ? raw : module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? "Sales Accounts" : "Purchase Accounts";
        }

        var groupRows = analysisRows
            .GroupBy(BusinessGroup, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AuditCommerceGroupSummary
            {
                Group = group.Key,
                Count = group.Count(),
                PartyCount = group.Select(row => row.PartyOrLedger).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                TaxableValue = group.Sum(row => row.TaxableValue),
                GstAmount = group.Sum(row => row.GstAmount),
                Amount = group.Sum(row => row.Amount),
                SharePercent = totalAmount <= 0m ? 0m : Math.Round(group.Sum(row => row.Amount) * 100m / totalAmount, 2),
                Type = module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? "Sales" : "Purchase / Expense"
            })
            .OrderByDescending(row => row.Amount)
            .ThenBy(row => row.Group, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CommerceGroupSummaries.ReplaceWith(groupRows);

        var ledgerTotal = analysisRows.Where(row => !IsCommerceAdjustment(module, row)).Sum(row => row.Amount);
        CommerceLedgerSummaries.ReplaceWith(analysisRows
            .GroupBy(row => string.IsNullOrWhiteSpace(row.BusinessLedger)
                ? (string.IsNullOrWhiteSpace(row.BusinessGroup) ? "Unclassified ledger" : row.BusinessGroup.Trim())
                : row.BusinessLedger.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var normal = group.Where(row => !IsCommerceAdjustment(module, row)).ToArray();
                var adjustmentsForLedger = group.Where(row => IsCommerceAdjustment(module, row)).ToArray();
                var gross = normal.Sum(row => row.Amount);
                var adjustments = adjustmentsForLedger.Sum(row => row.Amount);
                var net = gross - adjustments;
                var monthSummary = string.Join(" · ", group.Where(row => row.Date.HasValue)
                    .GroupBy(row => new { row.Date!.Value.Year, row.Date.Value.Month })
                    .OrderBy(month => month.Key.Year).ThenBy(month => month.Key.Month)
                    .Select(month =>
                    {
                        var monthGross = month.Where(row => !IsCommerceAdjustment(module, row)).Sum(row => row.Amount);
                        var monthAdjust = month.Where(row => IsCommerceAdjustment(module, row)).Sum(row => row.Amount);
                        return $"{new DateTime(month.Key.Year, month.Key.Month, 1):MMM}: {(monthGross - monthAdjust).ToString("N2", IndianCulture)}";
                    }));
                return new AuditCommerceGroupSummary
                {
                    Group = group.Key,
                    Count = group.Count(),
                    PartyCount = group.Select(row => row.PartyOrLedger).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    TaxableValue = normal.Sum(row => row.TaxableValue) - adjustmentsForLedger.Sum(row => row.TaxableValue),
                    GstAmount = normal.Sum(row => row.GstAmount) - adjustmentsForLedger.Sum(row => row.GstAmount),
                    Amount = gross,
                    AdjustmentAmount = adjustments,
                    NetAmount = net,
                    MonthSummary = monthSummary,
                    SharePercent = ledgerTotal <= 0m ? 0m : Math.Round(gross * 100m / ledgerTotal, 2),
                    Type = "Exact ledger"
                };
            })
            .OrderByDescending(row => row.NetAmount)
            .ThenBy(row => row.Group, StringComparer.OrdinalIgnoreCase)
            .ToArray());

        CommerceVoucherTypeSummaries.ReplaceWith(analysisRows
            .GroupBy(row => string.IsNullOrWhiteSpace(row.VoucherType) ? row.Category : row.VoucherType.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AuditCommerceGroupSummary
            {
                Group = group.Key.Length == 0 ? "Unclassified voucher" : group.Key,
                Count = group.Count(),
                PartyCount = group.Select(row => row.PartyOrLedger).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                TaxableValue = group.Sum(row => row.TaxableValue),
                GstAmount = group.Sum(row => row.GstAmount),
                Amount = group.Sum(row => row.Amount),
                Type = "Voucher type"
            })
            .OrderByDescending(row => row.Amount)
            .ToArray());

        CommerceVoucherRows.ReplaceWith(analysisRows.OrderBy(row => row.Date).ThenBy(row => row.Reference, StringComparer.OrdinalIgnoreCase).ToArray());

        foreach (var group in analysisRows
                     .Where(row => !string.IsNullOrWhiteSpace(row.PartyOrLedger))
                     .GroupBy(row => row.PartyOrLedger.Trim(), StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(group => group.Sum(row => row.Amount)))
        {
            balanceLookup.TryGetValue(group.Key, out var balances);
            CommercePartySummaries.Add(new AuditCommercePartySummary
            {
                Party = group.Key,
                Gstin = group.Select(row => row.Gstin).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
                Count = group.Count(),
                GrossAmount = group.Sum(row => row.Amount),
                AdjustmentAmount = group.Where(row => IsCommerceAdjustment(module, row)).Sum(row => row.Amount),
                TaxableValue = group.Sum(row => row.TaxableValue),
                GstAmount = group.Sum(row => row.GstAmount),
                OpeningBalance = balances.Opening,
                ClosingBalance = balances.Closing,
                AlertCount = group.Count(IsExceptionRow)
            });
        }

        var gstRows = analysisRows.Where(row => row.GstRateKnown).ToArray();
        var gstTotal = gstRows.Sum(row => row.Amount);
        CommerceGstRateSummaries.ReplaceWith(gstRows
            .GroupBy(row => row.GstRate)
            .Select(group => new AuditCommerceGstRateSummary
            {
                Rate = group.Key,
                RateLabel = group.Key.ToString("0.##", IndianCulture) + "%",
                Count = group.Count(),
                TaxableValue = group.Sum(row => row.TaxableValue),
                GstAmount = group.Sum(row => row.GstAmount),
                Amount = group.Sum(row => row.Amount),
                SharePercent = gstTotal <= 0m ? 0m : Math.Round(group.Sum(row => row.Amount) * 100m / gstTotal, 2)
            })
            .OrderBy(row => row.Rate)
            .ToArray());

        // Fix108: tax is a separate authority table. Do not mix component tax into the exact-ledger table.
        CommerceTaxAmountSummaries.ReplaceWith(analysisRows
            .Where(row => row.TaxableValue != 0m || row.GstAmount != 0m || row.GstRateKnown || row.TaxLedgers.Length > 0)
            .GroupBy(row => new
            {
                Basis = IsCommerceAdjustment(module, row)
                    ? (module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? "Credit Note / Sales Adjustment" : "Debit Note / Purchase Adjustment")
                    : module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? "Sales" : "Purchase",
                Rate = row.GstRateKnown ? row.GstRate : (decimal?)null
            })
            .Select(group => new AuditCommerceTaxAmountSummary
            {
                Basis = group.Key.Basis,
                Rate = group.Key.Rate,
                Count = group.Count(),
                TaxableValue = group.Sum(row => row.TaxableValue),
                Cgst = group.Sum(row => row.Cgst),
                Sgst = group.Sum(row => row.Sgst),
                Igst = group.Sum(row => row.Igst),
                Cess = group.Sum(row => row.Cess),
                UnallocatedGst = group.Sum(row => row.UnallocatedGst),
                TaxLedgers = string.Join(" | ", group.SelectMany(row => row.TaxLedgers.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.OrdinalIgnoreCase)),
                Source = string.Join(" + ", group.Select(row => row.TaxEvidenceSource).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            })
            .OrderBy(row => row.Basis, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Rate ?? decimal.MaxValue)
            .ToArray());

        var itemRows = analysisRows.Where(row => !string.IsNullOrWhiteSpace(row.ItemName)).ToArray();
        var itemTotal = itemRows.Sum(row => row.Amount);
        CommerceItemSummaries.ReplaceWith(itemRows
            .GroupBy(row => row.ItemName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var qty = group.Sum(row => row.Quantity);
                var amount = group.Sum(row => row.Amount);
                var knownRates = group.Where(row => row.GstRateKnown).Select(row => row.GstRate).Distinct().ToArray();
                return new AuditCommerceItemSummary
                {
                    Item = group.Key,
                    Unit = group.Select(row => row.Unit).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
                    Count = group.Count(),
                    PartyCount = group.Select(row => row.PartyOrLedger).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    Quantity = qty,
                    Amount = amount,
                    AverageRate = qty == 0m ? group.Where(row => row.ItemRate > 0m).Select(row => row.ItemRate).DefaultIfEmpty(0m).Average() : amount / qty,
                    GstRate = knownRates.Length == 1 ? knownRates[0] : null,
                    SharePercent = itemTotal <= 0m ? 0m : Math.Round(amount * 100m / itemTotal, 2)
                };
            })
            .OrderByDescending(row => row.Amount)
            .ToArray());

        CommerceHsnSummaries.ReplaceWith(analysisRows
            .Where(row => !string.IsNullOrWhiteSpace(row.HsnCode))
            .GroupBy(row => row.HsnCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AuditCommerceHsnSummary
            {
                Hsn = group.Key,
                Count = group.Count(),
                Quantity = group.Sum(row => row.Quantity),
                TaxableValue = group.Sum(row => row.TaxableValue),
                Cgst = group.Sum(row => row.Cgst),
                Sgst = group.Sum(row => row.Sgst),
                Igst = group.Sum(row => row.Igst),
                Cess = group.Sum(row => row.Cess),
                Amount = group.Sum(row => row.Amount)
            })
            .OrderByDescending(row => row.TaxableValue)
            .ThenBy(row => row.Hsn, StringComparer.OrdinalIgnoreCase)
            .ToArray());

        var adjustments = analysisRows.Where(row => IsCommerceAdjustment(module, row)).ToArray();
        var adjustmentTotal = adjustments.Sum(row => row.Amount);
        CommerceAdjustmentSummaries.ReplaceWith(adjustments
            .GroupBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AuditCommerceGroupSummary
            {
                Group = group.Key,
                Count = group.Count(),
                PartyCount = group.Select(row => row.PartyOrLedger).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                TaxableValue = group.Sum(row => row.TaxableValue),
                GstAmount = group.Sum(row => row.GstAmount),
                Amount = group.Sum(row => row.Amount),
                SharePercent = adjustmentTotal <= 0m ? 0m : Math.Round(group.Sum(row => row.Amount) * 100m / adjustmentTotal, 2),
                Type = group.Key
            })
            .OrderByDescending(row => row.Amount)
            .ToArray());

        if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase))
        {
            CommerceSupplySummaries.ReplaceWith(new[] { "B2B", "B2C" }
                .Select(supplyType =>
                {
                    var supplyRows = analysisRows.Where(row => CommerceSupplyType(row).Equals(supplyType, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var normal = supplyRows.Where(row => !IsCommerceAdjustment(module, row)).ToArray();
                    var notes = supplyRows.Where(row => IsCommerceAdjustment(module, row)).ToArray();
                    var gross = normal.Sum(row => row.Amount);
                    var adjustmentsAmount = notes.Sum(row => row.Amount);
                    return new AuditCommerceSupplySummary
                    {
                        SupplyType = supplyType,
                        Count = supplyRows.Length,
                        AdjustmentCount = notes.Length,
                        PartyCount = supplyRows.Select(row => row.PartyOrLedger).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                        GrossAmount = gross,
                        AdjustmentAmount = adjustmentsAmount,
                        NetAmount = gross - adjustmentsAmount,
                        TaxableValue = normal.Sum(row => row.TaxableValue) - notes.Sum(row => row.TaxableValue),
                        GstAmount = normal.Sum(row => row.GstAmount) - notes.Sum(row => row.GstAmount)
                    };
                })
                .Where(row => row.Count > 0)
                .ToArray());
        }

        var datedRows = analysisRows.Where(row => row.Date.HasValue).ToArray();
        if (datedRows.Length > 0)
        {
            var fyStartYear = datedRows
                .Select(row => row.Date!.Value.Month >= 4 ? row.Date.Value.Year : row.Date.Value.Year - 1)
                .GroupBy(year => year)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .First();
            var months = Enumerable.Range(0, 12).Select(offset => new DateTime(fyStartYear, 4, 1).AddMonths(offset)).ToArray();
            CommerceMonthSummaries.ReplaceWith(months.Select((monthDate, index) =>
            {
                var monthRows = datedRows.Where(row => row.Date!.Value.Year == monthDate.Year && row.Date.Value.Month == monthDate.Month).ToArray();
                var adjustment = monthRows.Where(row => IsCommerceAdjustment(module, row)).Sum(row => row.Amount);
                var gross = monthRows.Where(row => !IsCommerceAdjustment(module, row)).Sum(row => row.Amount);
                return new AuditCommerceMonthSummary
                {
                    MonthOrder = index + 1,
                    Month = monthDate.ToString("MMM yyyy", IndianCulture),
                    Count = monthRows.Length,
                    Gross = gross,
                    Adjustments = adjustment,
                    Net = gross - adjustment,
                    GstAmount = monthRows.Sum(row => row.GstAmount)
                };
            }).ToArray());
            CommerceMonthChartSeries = new ISeries[]
            {
                new ColumnSeries<double>
                {
                    Name = module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? "Net Sales" : "Net Purchases",
                    Values = CommerceMonthSummaries.Select(row => (double)row.Net).ToArray(),
                    MaxBarWidth = 46
                }
            };
            CommerceMonthChartXAxes = new[]
            {
                new Axis
                {
                    Labels = CommerceMonthSummaries.Select(row => row.Month.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]).ToArray(),
                    MinStep = 1,
                    ForceStepToMin = true
                }
            };
            CommerceMonthChartYAxes = new[] { new Axis { Labeler = value => FormatChartMoney(value) } };
            OnPropertyChanged(nameof(CommerceMonthChartSeries));
            OnPropertyChanged(nameof(CommerceMonthChartXAxes));
            OnPropertyChanged(nameof(CommerceMonthChartYAxes));
        }

        BuildCommerceBooksReturnComparisons(module);

        CommerceAlertSummaries.ReplaceWith(analysisRows
            .Where(IsExceptionRow)
            .GroupBy(row => CommerceAlertLabel(row), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AuditCommerceAlertSummary { Alert = group.Key, Count = group.Count(), Amount = group.Sum(row => row.Amount) })
            .OrderByDescending(row => row.Count)
            .ToArray());

        BuildCommerceReconciliationTiles(module, analysisRows);
        MetricTiles.ReplaceWith(BuildCommerceMetricTiles(module, analysisRows));
        RaiseCommerceTabVisibility();
    }

    private void SyncCommerceExtraVoucherTab()
    {
        if (!IsCommerceModule) return;
        var existing = ModuleTabs.FirstOrDefault(tab => tab.Key.Equals("ExtraVouchers", StringComparison.OrdinalIgnoreCase));
        if (CommerceExtraVoucherRows.Count > 0 && existing is null)
            ModuleTabs.Add(new AuditModuleTab("ExtraVouchers", "Extra / Other Vouchers", CommerceExtraVoucherRows.Count));
        else if (CommerceExtraVoucherRows.Count == 0 && existing is not null)
            ModuleTabs.Remove(existing);
        OnPropertyChanged(nameof(IsCommerceExtraTab));
    }

    private static bool IsPrimaryCommerceRow(string module, AuditRecordRow row)
    {
        var type = (row.VoucherType ?? string.Empty).Trim();
        if (type.Length > 0) return IsPrimaryCommerceVoucherType(module, type);

        // Imported detail rows from older Accounting snapshots can lack VoucherType.
        // Only those legacy blanks may fall back to the module/category classification;
        // a real Journal/Receipt/Payment/etc. can never enter normal Sales/Purchase totals.
        var category = (row.Category ?? string.Empty).Trim();
        return module.Equals("Sales", StringComparison.OrdinalIgnoreCase)
            ? category.Equals("Sales", StringComparison.OrdinalIgnoreCase) || category.Contains("Credit Note", StringComparison.OrdinalIgnoreCase)
            : module.Equals("Purchase", StringComparison.OrdinalIgnoreCase)
                ? category.Equals("Purchase", StringComparison.OrdinalIgnoreCase) || category.Contains("Debit Note", StringComparison.OrdinalIgnoreCase)
                : true;
    }

    private static bool IsPrimaryCommerceVoucherType(string module, string voucherType) =>
        module.Equals("Sales", StringComparison.OrdinalIgnoreCase)
            ? voucherType.Equals("Sales", StringComparison.OrdinalIgnoreCase) || voucherType.Contains("Credit Note", StringComparison.OrdinalIgnoreCase)
            : module.Equals("Purchase", StringComparison.OrdinalIgnoreCase)
                ? voucherType.Equals("Purchase", StringComparison.OrdinalIgnoreCase) || voucherType.Contains("Debit Note", StringComparison.OrdinalIgnoreCase)
                : true;

    private static bool IsCommerceBusinessGroup(string module, string group) =>
        module.Equals("Sales", StringComparison.OrdinalIgnoreCase)
            ? group is "Sales Accounts" or "Direct Incomes" or "Indirect Incomes"
            : group is "Purchase Accounts" or "Direct Expenses" or "Indirect Expenses" or "Fixed Assets" or "Current Assets" or "Stock-in-Hand";

    private static bool IsCommerceRoundOffLedger(string? ledger)
    {
        var normalized = new string((ledger ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        return normalized.Contains("ROUNDOFF", StringComparison.Ordinal) ||
               normalized.Contains("ROUNDINGOFF", StringComparison.Ordinal);
    }

    private static bool HasCommerceBusinessLedger(
        string module,
        IEnumerable<string> ledgers,
        string party,
        IReadOnlyDictionary<string, string> ledgerParents,
        IReadOnlyDictionary<string, string> groupParents) =>
        ledgers.Where(name => !name.Equals(party, StringComparison.OrdinalIgnoreCase))
            .Where(name => !IsCommerceRoundOffLedger(name))
            .Any(name => IsCommerceBusinessGroup(module, ResolveLedgerGroup(name, ledgerParents, groupParents)));

    private AuditRecordRow[] BuildCommerceExtraVoucherRows(
        string module,
        IReadOnlyDictionary<string, string> ledgerParents,
        IReadOnlyDictionary<string, string> groupParents)
    {
        var client = SelectedAuditClient;
        if (client is null || string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)) return Array.Empty<AuditRecordRow>();
        var (from, to) = AdvancedClosingEntryPlanner.FinancialYearRange(client.FinancialYear);
        return _tallyRepository.GetVoucherIndexRange(client.LinkedTallyCompanyKey, client.FinancialYear, from, to)
            .Select(voucher =>
            {
                var type = (voucher.VoucherType ?? string.Empty).Trim();
                var party = (voucher.PartyLedger ?? string.Empty).Trim();
                var ledgers = SplitLedgerNames(voucher.DebitLedgers)
                    .Concat(SplitLedgerNames(voucher.CreditLedgers))
                    .Append(party)
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var businessLedgers = ledgers
                    .Where(name => !name.Equals(party, StringComparison.OrdinalIgnoreCase))
                    .Select(name => new { Name = name, Group = ResolveLedgerGroup(name, ledgerParents, groupParents) })
                    .Where(item => IsCommerceBusinessGroup(module, item.Group))
                    .ToArray();
                var primaryType = IsPrimaryCommerceVoucherType(module, type);
                var relatedAdjustment = module.Equals("Sales", StringComparison.OrdinalIgnoreCase)
                    ? type.Contains("Credit Note", StringComparison.OrdinalIgnoreCase)
                    : type.Contains("Debit Note", StringComparison.OrdinalIgnoreCase);
                var shouldBeNormal = primaryType && (!relatedAdjustment || businessLedgers.Length > 0);
                return new { Voucher = voucher, Type = type, Party = party, Ledgers = ledgers, BusinessLedgers = businessLedgers, ShouldBeNormal = shouldBeNormal };
            })
            .Where(item => !item.ShouldBeNormal && item.BusinessLedgers.Length > 0)
            .Select(item =>
            {
                var voucher = item.Voucher;
                var reference = voucher.VoucherNumber.Trim();
                if (reference.Length == 0) reference = voucher.Reference.Trim();
                if (reference.Length == 0) reference = $"Tally MID {voucher.MasterId}";
                var business = string.Join(" | ", item.BusinessLedgers.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase));
                var groups = string.Join(" | ", item.BusinessLedgers.Select(x => x.Group).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
                var masterId = voucher.MasterId?.Trim() ?? string.Empty;
                var voucherGuid = voucher.Guid?.Trim() ?? string.Empty;
                return new AuditRecordRow
                {
                    Date = voucher.VoucherDate,
                    Reference = reference,
                    PartyOrLedger = item.Party.Length > 0 ? item.Party : item.Ledgers.FirstOrDefault() ?? string.Empty,
                    VoucherType = item.Type.Length > 0 ? item.Type : "Unclassified",
                    AuthorityId = masterId.Length > 0 ? $"MID {masterId}" : voucherGuid,
                    Category = "Extra / Other Voucher",
                    TransactionClass = item.Type,
                    BusinessLedger = business,
                    BusinessGroup = groups,
                    Details = voucher.Narration.Trim(),
                    OriginalNarration = voucher.Narration.Trim(),
                    Amount = Math.Abs(voucher.Amount),
                    Status = "Excluded from normal commerce totals",
                    EvidenceStatus = $"Voucher type '{item.Type}' touches {module} ledger authority but is not an allowed normal {module} voucher family.",
                    Source = "Explicit Audit Tally Link · Extra"
                };
            })
            .OrderBy(row => row.Date)
            .ThenBy(row => row.Reference, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizePartyGstin(string? value)
    {
        var normalized = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return normalized.Length == 15 ? normalized : string.Empty;
    }

    private static string CommerceSupplyType(AuditRecordRow row) =>
        NormalizePartyGstin(row.Gstin).Length == 15 ? "B2B" : "B2C";

    private static void NormalizeCommerceRateAuthority(IEnumerable<AuditRecordRow> sourceRows)
    {
        foreach (var row in sourceRows)
        {
            if (TryResolveTaxLedgerGstRate(row.TaxLedgers, out var ledgerRate))
            {
                row.GstRate = ledgerRate;
                row.GstRateKnown = true;
                continue;
            }

            if (row.GstRateKnown && TryNormalizeStandardGstRate(row.GstRate, out var normalized))
            {
                row.GstRate = normalized;
                continue;
            }

            var gstForEffectiveRate = Math.Abs(row.Cgst) + Math.Abs(row.Sgst) + Math.Abs(row.Igst) + Math.Abs(row.UnallocatedGst);
            if (Math.Abs(row.TaxableValue) > 0.005m && gstForEffectiveRate > 0.005m)
            {
                var effective = gstForEffectiveRate * 100m / Math.Abs(row.TaxableValue);
                if (TryNormalizeStandardGstRate(effective, out normalized))
                {
                    row.GstRate = normalized;
                    row.GstRateKnown = true;
                    continue;
                }
            }

            // Do not turn a mathematically odd effective percentage into a GST slab.
            if (row.GstRateKnown && !IsRecognisedGstRate(row.GstRate))
            {
                row.GstRate = 0m;
                row.GstRateKnown = false;
            }
        }
    }

    private static bool TryResolveTaxLedgerGstRate(string taxLedgers, out decimal rate)
    {
        rate = 0m;
        var cgst = new HashSet<decimal>();
        var sgst = new HashSet<decimal>();
        var igst = new HashSet<decimal>();
        foreach (var ledger in (taxLedgers ?? string.Empty).Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryExtractPercentFromText(ledger, out var componentRate) || componentRate <= 0m) continue;
            if (ledger.Contains("IGST", StringComparison.OrdinalIgnoreCase)) igst.Add(componentRate);
            else if (ledger.Contains("CGST", StringComparison.OrdinalIgnoreCase)) cgst.Add(componentRate);
            else if (ledger.Contains("SGST", StringComparison.OrdinalIgnoreCase)) sgst.Add(componentRate);
        }

        if (igst.Count == 1 && TryNormalizeStandardGstRate(igst.Single(), out rate)) return true;
        if (cgst.Count == 1 && sgst.Count == 1)
        {
            var combined = cgst.Single() + sgst.Single();
            if (TryNormalizeStandardGstRate(combined, out rate)) return true;
        }
        return false;
    }

    private static bool TryExtractPercentFromText(string value, out decimal percent)
    {
        percent = 0m;
        var text = (value ?? string.Empty).Trim();
        var percentIndex = text.IndexOf('%');
        if (percentIndex <= 0) return false;
        var end = percentIndex - 1;
        while (end >= 0 && char.IsWhiteSpace(text[end])) end--;
        if (end < 0) return false;
        var start = end;
        while (start >= 0 && (char.IsDigit(text[start]) || text[start] == '.')) start--;
        var numeric = text[(start + 1)..(end + 1)];
        return decimal.TryParse(numeric, NumberStyles.Number, CultureInfo.InvariantCulture, out percent);
    }

    private static bool TryNormalizeStandardGstRate(decimal candidate, out decimal rate)
    {
        // Standard slabs seen in the supported Sales/Purchase authorities. Keep a
        // tight tolerance so 17.93/18.01 becomes 18, but 13.47 never masquerades as 12.
        decimal[] standards = [0.1m, 0.25m, 1m, 1.5m, 3m, 5m, 6m, 7.5m, 12m, 18m, 28m];
        var nearest = standards.OrderBy(value => Math.Abs(value - candidate)).First();
        if (Math.Abs(nearest - candidate) <= 0.15m)
        {
            rate = nearest;
            return true;
        }
        rate = 0m;
        return false;
    }

    private static bool IsRecognisedGstRate(decimal rate) =>
        Math.Abs(rate) < 0.0001m || TryNormalizeStandardGstRate(rate, out var normalized) && Math.Abs(normalized - rate) <= 0.0001m;

    private AuditRecordRow[] GetPreferredCommerceRows(string module)
    {
        // R4 R9: Audit commerce authority is independent. Never prefer or merge rows from a
        // LinkedAccountingClientId. The active Audit module already contains only Audit-owned
        // records plus explicitly linked Audit-Tally evidence.
        return Rows
            .Where(row => IsPrimaryCommerceRow(module, row))
            .ToArray();
    }

    private static string CommerceVoucherIdentity(AuditRecordRow row)
    {
        var reference = row.Reference.Trim();
        if (reference.Length == 0) reference = row.AuthorityId.Trim();
        if (reference.Length == 0 || !row.Date.HasValue) return string.Empty;
        var kind = row.Category.Contains("Credit Note", StringComparison.OrdinalIgnoreCase)
            ? "CREDIT NOTE"
            : row.Category.Contains("Debit Note", StringComparison.OrdinalIgnoreCase)
                ? "DEBIT NOTE"
                : row.Category.Contains("Sales", StringComparison.OrdinalIgnoreCase)
                    ? "SALES"
                    : row.Category.Contains("Purchase", StringComparison.OrdinalIgnoreCase)
                        ? "PURCHASE"
                        : row.VoucherType.Trim().ToUpperInvariant();
        return $"{row.Date.Value:yyyyMMdd}|{reference.ToUpperInvariant()}|{kind}";
    }

    private void BuildCommerceBooksReturnComparisons(string module)
    {
        CommerceBooksVsGstr1Rows.Clear();
        CommerceBooksVsTwoARows.Clear();
        CommerceBooksVsTwoBRows.Clear();

        var independent = Rows.ToArray();
        var books = independent
            .Where(row => !row.IsGstr1Evidence && !row.HasTwoAEvidence && !row.HasTwoBEvidence)
            .ToArray();

        if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase))
        {
            var gstr1 = independent.Where(row => row.IsGstr1Evidence).ToArray();
            CommerceBooksVsGstr1Rows.ReplaceWith(BuildCommerceComparisonRows(books, gstr1, "GSTR-1", row => CommerceSideSnapshot.FromAuditRow(row)));
            return;
        }

        var twoA = independent.Where(row => row.HasTwoAEvidence).ToArray();
        var twoB = independent.Where(row => row.HasTwoBEvidence).ToArray();
        CommerceBooksVsTwoARows.ReplaceWith(BuildCommerceComparisonRows(books, twoA, "GSTR-2A", row => CommerceSideSnapshot.FromAuditRow(row)));
        CommerceBooksVsTwoBRows.ReplaceWith(BuildCommerceComparisonRows(books, twoB, "GSTR-2B", row => CommerceSideSnapshot.FromTwoB(row)));
    }

    private static AuditCommerceBooksReturnComparisonRow[] BuildCommerceComparisonRows(
        AuditRecordRow[] booksRows,
        AuditRecordRow[] returnRows,
        string returnName,
        Func<AuditRecordRow, CommerceSideSnapshot> returnSelector)
    {
        static Dictionary<string, CommerceSideSnapshot> Collapse(IEnumerable<AuditRecordRow> rows, Func<AuditRecordRow, CommerceSideSnapshot> selector) =>
            rows.Select(row => new { Key = CommerceComparisonIdentity(row), Side = selector(row) })
                .Where(item => item.Key.Length > 0)
                .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => CommerceSideSnapshot.Combine(group.Select(item => item.Side)),
                    StringComparer.OrdinalIgnoreCase);

        var books = Collapse(booksRows, CommerceSideSnapshot.FromAuditRow);
        var returns = Collapse(returnRows, returnSelector);
        var keys = books.Keys.Concat(returns.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return keys.Select(key =>
        {
            books.TryGetValue(key, out var book);
            returns.TryGetValue(key, out var tax);
            book ??= CommerceSideSnapshot.Empty;
            tax ??= CommerceSideSnapshot.Empty;
            var amountDiff = book.Amount - tax.Amount;
            var taxableDiff = book.Taxable - tax.Taxable;
            var gstDiff = book.Gst - tax.Gst;
            var status = book.Exists && tax.Exists
                ? Math.Abs(amountDiff) <= 0.01m && Math.Abs(taxableDiff) <= 0.01m && Math.Abs(gstDiff) <= 0.01m ? "Matched" : "Difference"
                : book.Exists ? "Only in Books" : $"Only in {returnName}";
            var identitySide = book.Exists ? book : tax;
            return new AuditCommerceBooksReturnComparisonRow
            {
                ReturnName = returnName,
                Date = identitySide.Date,
                Reference = identitySide.Reference,
                Party = identitySide.Party,
                Gstin = identitySide.Gstin,
                VoucherType = identitySide.VoucherType,
                BooksAmount = book.Amount,
                ReturnAmount = tax.Amount,
                AmountDifference = amountDiff,
                BooksTaxable = book.Taxable,
                ReturnTaxable = tax.Taxable,
                TaxableDifference = taxableDiff,
                BooksGst = book.Gst,
                ReturnGst = tax.Gst,
                GstDifference = gstDiff,
                Status = status
            };
        })
        .OrderBy(row => row.Status.Equals("Matched", StringComparison.OrdinalIgnoreCase))
        .ThenBy(row => row.Date)
        .ThenBy(row => row.Reference, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    private static string CommerceComparisonIdentity(AuditRecordRow row)
    {
        var reference = row.Reference.Trim();
        if (reference.Length == 0) reference = row.AuthorityId.Trim();
        if (reference.Length == 0) return string.Empty;
        var family = row.Category.Contains("Credit Note", StringComparison.OrdinalIgnoreCase) ? "CREDITNOTE"
            : row.Category.Contains("Debit Note", StringComparison.OrdinalIgnoreCase) ? "DEBITNOTE"
            : row.Category.Contains("Sales", StringComparison.OrdinalIgnoreCase) ? "SALES"
            : row.Category.Contains("Purchase", StringComparison.OrdinalIgnoreCase) ? "PURCHASE"
            : row.VoucherType.Trim().ToUpperInvariant();
        var party = new string((row.PartyOrLedger ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return $"{family}|{reference.ToUpperInvariant()}|{party}";
    }

    private sealed class CommerceSideSnapshot
    {
        public static CommerceSideSnapshot Empty { get; } = new();
        public bool Exists { get; init; }
        public DateTime? Date { get; init; }
        public string Reference { get; init; } = string.Empty;
        public string Party { get; init; } = string.Empty;
        public string Gstin { get; init; } = string.Empty;
        public string VoucherType { get; init; } = string.Empty;
        public decimal Amount { get; init; }
        public decimal Taxable { get; init; }
        public decimal Gst { get; init; }

        public static CommerceSideSnapshot FromAuditRow(AuditRecordRow row) => new()
        {
            Exists = true, Date = row.Date, Reference = row.Reference, Party = row.PartyOrLedger, Gstin = row.Gstin,
            VoucherType = row.VoucherType, Amount = row.Amount, Taxable = row.TaxableValue, Gst = row.GstAmount
        };

        public static CommerceSideSnapshot FromTwoB(AuditRecordRow row) => new()
        {
            Exists = true, Date = row.Date, Reference = row.Reference, Party = row.PartyOrLedger, Gstin = row.Gstin,
            VoucherType = row.VoucherType,
            Amount = row.TwoBInvoiceValue ?? row.Amount,
            Taxable = row.TwoBTaxableValue ?? row.TaxableValue,
            Gst = row.TwoBGstAmount ?? row.GstAmount
        };

        public static CommerceSideSnapshot Combine(IEnumerable<CommerceSideSnapshot> source)
        {
            var rows = source.Where(row => row.Exists).ToArray();
            if (rows.Length == 0) return Empty;
            var first = rows[0];
            // Document value is commonly repeated on item/GST-rate rows, whereas taxable/tax are line-level.
            // Use the largest document value and aggregate the line-level statutory values.
            return new CommerceSideSnapshot
            {
                Exists = true, Date = first.Date, Reference = first.Reference, Party = first.Party, Gstin = rows.Select(row => row.Gstin).FirstOrDefault(value => value.Length > 0) ?? string.Empty,
                VoucherType = first.VoucherType,
                Amount = rows.Max(row => row.Amount),
                Taxable = rows.Sum(row => row.Taxable),
                Gst = rows.Sum(row => row.Gst)
            };
        }
    }

    private static bool IsCommerceAdjustment(string module, AuditRecordRow row)
    {
        if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase))
            return row.Category.Contains("Credit Note", StringComparison.OrdinalIgnoreCase) || row.Category.Contains("Debit Note", StringComparison.OrdinalIgnoreCase);
        return row.Category.Contains("Debit Note", StringComparison.OrdinalIgnoreCase) || row.Category.Contains("Credit Note", StringComparison.OrdinalIgnoreCase);
    }

    private static string CommerceAlertLabel(AuditRecordRow row)
    {
        if (row.RuleReference.Length > 0) return row.RuleReference;
        if (IsIssueText(row.ReconciliationStatus)) return row.ReconciliationStatus;
        if (IsIssueText(row.Status)) return row.Status;
        if (IsIssueText(row.EvidenceStatus)) return row.EvidenceStatus;
        return "Audit review";
    }

    private AuditMetricTile[] BuildCommerceMetricTiles(string module, AuditRecordRow[] rows)
    {
        var normalRows = rows.Where(row => !IsCommerceAdjustment(module, row)).ToArray();
        var adjustmentRows = rows.Where(row => IsCommerceAdjustment(module, row)).ToArray();
        var gross = normalRows.Sum(row => row.Amount);
        var adjustments = adjustmentRows.Sum(row => row.Amount);
        var taxable = rows.Sum(row => row.TaxableValue);
        var gst = rows.Sum(row => row.GstAmount);
        var alerts = rows.Count(IsExceptionRow);
        var tiles = new List<AuditMetricTile>();

        if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase))
        {
            tiles.Add(new AuditMetricTile("Sales Vouchers", gross.ToString("N2", IndianCulture), $"{normalRows.Length.ToString("N0", IndianCulture)} Sales row(s)"));
            tiles.Add(new AuditMetricTile("Credit Notes", adjustments.ToString("N2", IndianCulture), $"{adjustmentRows.Length.ToString("N0", IndianCulture)} Credit Note / adjustment row(s)"));
            tiles.Add(new AuditMetricTile("Net Sales", (gross - adjustments).ToString("N2", IndianCulture), "Sales less Credit Notes / adjustments"));
            tiles.Add(new AuditMetricTile("Taxable Sales", taxable.ToString("N2", IndianCulture), "Taxable value across the canonical Sales authority"));
            tiles.Add(new AuditMetricTile("Output GST", gst.ToString("N2", IndianCulture), "CGST + SGST + IGST + Cess + unallocated GST"));
            AddTaxComponentTiles(tiles, rows);
            foreach (var rateGroup in rows.Where(row => row.GstRateKnown).GroupBy(row => row.GstRate).OrderBy(group => group.Key))
                tiles.Add(new AuditMetricTile($"Sales @ {rateGroup.Key.ToString("0.##", IndianCulture)}%", rateGroup.Sum(row => row.TaxableValue).ToString("N2", IndianCulture), $"GST {rateGroup.Sum(row => row.GstAmount).ToString("N2", IndianCulture)} · {rateGroup.Count().ToString("N0", IndianCulture)} row(s)"));
            var b2b = rows.Where(row => row.Gstin.Length > 0).ToArray();
            var b2c = rows.Where(row => row.Gstin.Length == 0).ToArray();
            if (b2b.Length > 0) tiles.Add(new AuditMetricTile("B2B", b2b.Sum(row => row.Amount).ToString("N2", IndianCulture), $"{b2b.Length.ToString("N0", IndianCulture)} row(s)"));
            if (b2c.Length > 0) tiles.Add(new AuditMetricTile("B2C", b2c.Sum(row => row.Amount).ToString("N2", IndianCulture), $"{b2c.Length.ToString("N0", IndianCulture)} row(s)"));
            var irn = rows.Count(row => row.Irn.Length > 0);
            var eway = rows.Count(row => row.EWayBillNumber.Length > 0);
            if (irn > 0) tiles.Add(new AuditMetricTile("e-Invoice / IRN", irn.ToString("N0", IndianCulture), "Rows carrying IRN evidence"));
            if (eway > 0) tiles.Add(new AuditMetricTile("e-Way Bill", eway.ToString("N0", IndianCulture), "Rows carrying e-Way Bill evidence"));
            if (alerts > 0) tiles.Add(new AuditMetricTile("Alerts", alerts.ToString("N0", IndianCulture), "Sales audit items requiring review", true));
            return tiles.ToArray();
        }

        tiles.Add(new AuditMetricTile("Purchase Vouchers", gross.ToString("N2", IndianCulture), $"{normalRows.Length.ToString("N0", IndianCulture)} Purchase row(s)"));
        tiles.Add(new AuditMetricTile("Debit Notes", adjustments.ToString("N2", IndianCulture), $"{adjustmentRows.Length.ToString("N0", IndianCulture)} Debit Note / adjustment row(s)"));
        tiles.Add(new AuditMetricTile("Net Purchases", (gross - adjustments).ToString("N2", IndianCulture), "Purchases less Debit Notes / adjustments"));
        tiles.Add(new AuditMetricTile("Taxable Purchases", taxable.ToString("N2", IndianCulture), "Taxable value across the canonical Purchase authority"));
        tiles.Add(new AuditMetricTile("Input GST", gst.ToString("N2", IndianCulture), "CGST + SGST + IGST + Cess + unallocated GST"));
        AddTaxComponentTiles(tiles, rows);
        foreach (var rateGroup in rows.Where(row => row.GstRateKnown).GroupBy(row => row.GstRate).OrderBy(group => group.Key))
            tiles.Add(new AuditMetricTile($"Purchase @ {rateGroup.Key.ToString("0.##", IndianCulture)}%", rateGroup.Sum(row => row.TaxableValue).ToString("N2", IndianCulture), $"GST {rateGroup.Sum(row => row.GstAmount).ToString("N2", IndianCulture)} · {rateGroup.Count().ToString("N0", IndianCulture)} row(s)"));
        var matched = rows.Count(row => row.ReconciliationStatus.Contains("match", StringComparison.OrdinalIgnoreCase) || row.ReconciliationStatus.Contains("accept", StringComparison.OrdinalIgnoreCase));
        var twoBIssues = rows.Count(row => row.ReconciliationStatus.Contains("only", StringComparison.OrdinalIgnoreCase) || row.ReconciliationStatus.Contains("missing", StringComparison.OrdinalIgnoreCase) || row.ReconciliationStatus.Contains("mismatch", StringComparison.OrdinalIgnoreCase));
        if (matched > 0 || twoBIssues > 0) tiles.Add(new AuditMetricTile("2B Status", matched.ToString("N0", IndianCulture), $"{twoBIssues.ToString("N0", IndianCulture)} mismatch / only-in-one-source row(s)", twoBIssues > 0));
        var rcm = rows.Where(row => row.IsRcm).ToArray();
        if (rcm.Length > 0) tiles.Add(new AuditMetricTile("RCM", rcm.Sum(row => row.Amount).ToString("N2", IndianCulture), $"{rcm.Length.ToString("N0", IndianCulture)} RCM row(s)", true));
        if (alerts > 0) tiles.Add(new AuditMetricTile("Alerts", alerts.ToString("N0", IndianCulture), "Purchase audit items requiring review", true));
        return tiles.ToArray();
    }

    private static void AddTaxComponentTiles(ICollection<AuditMetricTile> tiles, IEnumerable<AuditRecordRow> rows)
    {
        var source = rows.ToArray();
        var cgst = source.Sum(row => row.Cgst);
        var sgst = source.Sum(row => row.Sgst);
        var igst = source.Sum(row => row.Igst);
        var cess = source.Sum(row => row.Cess);
        var unallocated = source.Sum(row => row.UnallocatedGst);
        if (cgst != 0m) tiles.Add(new AuditMetricTile("CGST", cgst.ToString("N2", IndianCulture), "Central GST amount"));
        if (sgst != 0m) tiles.Add(new AuditMetricTile("SGST", sgst.ToString("N2", IndianCulture), "State GST amount"));
        if (igst != 0m) tiles.Add(new AuditMetricTile("IGST", igst.ToString("N2", IndianCulture), "Integrated GST amount"));
        if (cess != 0m) tiles.Add(new AuditMetricTile("Cess", cess.ToString("N2", IndianCulture), "Cess amount"));
        if (unallocated != 0m) tiles.Add(new AuditMetricTile("Unallocated GST", unallocated.ToString("N2", IndianCulture), "GST recovered from voucher value but component ledger could not be assigned"));
    }

    private void BuildCommerceReconciliationTiles(string module, AuditRecordRow[] preferredRows)
    {
        CommerceReconciliationTiles.Clear();
        var booksRows = Rows.Where(row => !row.IsGstr1Evidence && !row.HasTwoAEvidence && !row.HasTwoBEvidence).ToArray();
        var returnRows = module.Equals("Sales", StringComparison.OrdinalIgnoreCase)
            ? Rows.Where(row => row.IsGstr1Evidence).ToArray()
            : Rows.Where(row => row.HasTwoBEvidence || row.HasTwoAEvidence).ToArray();
        CommerceReconciliationTiles.Add(new AuditMetricTile("Books / Audit Rows", booksRows.Length.ToString("N0", IndianCulture), booksRows.Sum(row => row.Amount).ToString("N2", IndianCulture)));
        CommerceReconciliationTiles.Add(new AuditMetricTile(module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? "Audit GSTR-1 Evidence" : "Audit 2A/2B Evidence", returnRows.Length.ToString("N0", IndianCulture), returnRows.Sum(row => row.Amount).ToString("N2", IndianCulture)));
        CommerceReconciliationTiles.Add(new AuditMetricTile("Value Difference", Math.Abs(booksRows.Sum(row => row.Amount) - returnRows.Sum(row => row.Amount)).ToString("N2", IndianCulture), "Independent Audit books versus Audit return evidence", booksRows.Length > 0 && returnRows.Length > 0 && Math.Abs(booksRows.Sum(row => row.Amount) - returnRows.Sum(row => row.Amount)) > 0.01m));
        CommerceReconciliationTiles.Add(new AuditMetricTile("Taxable Value", preferredRows.Sum(row => row.TaxableValue).ToString("N2", IndianCulture), "Independent Audit taxable authority"));
        CommerceReconciliationTiles.Add(new AuditMetricTile("GST", preferredRows.Sum(row => row.GstAmount).ToString("N2", IndianCulture), "CGST + SGST + IGST + Cess + unallocated GST"));
        if (module.Equals("Purchase", StringComparison.OrdinalIgnoreCase))
        {
            var rcm = preferredRows.Where(row => row.IsRcm).ToArray();
            CommerceReconciliationTiles.Add(new AuditMetricTile("RCM", rcm.Sum(row => row.Amount).ToString("N2", IndianCulture), $"{rcm.Length.ToString("N0", IndianCulture)} RCM row(s)"));
        }
    }

    private void RebuildFinancePresentation(string module)
    {
        FinanceAccounts.Clear();
        FinancialDetailTiles.Clear();
        PaymentGroupSummaries.Clear();
        ReceiptGroupSummaries.Clear();
        PaymentGroupChartSeries = Array.Empty<ISeries>();
        ReceiptGroupChartSeries = Array.Empty<ISeries>();
        PaymentGroupChartXAxes = Array.Empty<Axis>();
        PaymentGroupChartYAxes = Array.Empty<Axis>();
        ReceiptGroupChartXAxes = Array.Empty<Axis>();
        ReceiptGroupChartYAxes = Array.Empty<Axis>();
        OnPropertyChanged(nameof(PaymentGroupChartSeries));
        OnPropertyChanged(nameof(ReceiptGroupChartSeries));
        OnPropertyChanged(nameof(PaymentGroupChartXAxes));
        OnPropertyChanged(nameof(PaymentGroupChartYAxes));
        OnPropertyChanged(nameof(ReceiptGroupChartXAxes));
        OnPropertyChanged(nameof(ReceiptGroupChartYAxes));
        BankingStatementTiles.Clear();
        BankingVerificationTiles.Clear();
        BankingTransferTiles.Clear();
        BankingPositionTiles.Clear();
        BankingAlertTiles.Clear();
        CashBankMovements.Clear();
        SelectedAccountAlertTiles.Clear();

        if (module is not ("BankingFinance" or "Bank" or "OD" or "Loans" or "Cash"))
        {
            _selectedFinanceAccount = null;
            OnPropertyChanged(nameof(SelectedFinanceAccount));
            return;
        }

        var booksBalancesByAccount = BuildFinanceBooksBalanceLookup();
        var accountRows = Rows
            .Where(row => !string.IsNullOrWhiteSpace(row.PartyOrLedger))
            .GroupBy(row => row.PartyOrLedger.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                booksBalancesByAccount.TryGetValue(group.Key, out var booksBalance);
                return BuildFinanceAccountSummary(group.Key, group.ToArray(), booksBalance.Opening, booksBalance.Closing);
            })
            .OrderBy(summary => summary.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(summary => summary.Account, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        FinanceAccounts.ReplaceWith(accountRows);

        if (module.Equals("BankingFinance", StringComparison.OrdinalIgnoreCase))
            RebuildBankingParentPresentation();

        if (IsFinanceAccountDashboardModule)
        {
            var previousAccount = _pendingFinanceAccountName.Length > 0 ? _pendingFinanceAccountName : _selectedFinanceAccount?.Account;
            _selectedFinanceAccount = previousAccount is not null
                ? FinanceAccounts.FirstOrDefault(account => account.Account.Equals(previousAccount, StringComparison.OrdinalIgnoreCase))
                : null;
            _pendingFinanceAccountName = string.Empty;
            _selectedFinanceAccount ??= FinanceAccounts.FirstOrDefault();
            foreach (var account in FinanceAccounts) account.IsSelected = _selectedFinanceAccount is not null && account.Account.Equals(_selectedFinanceAccount.Account, StringComparison.OrdinalIgnoreCase);
            OnPropertyChanged(nameof(SelectedFinanceAccount));
            RebuildSelectedFinanceAccountPresentation();
        }
    }

    private void RebuildBankingParentPresentation()
    {
        BankingStatementTiles.Clear();
        BankingVerificationTiles.Clear();
        BankingTransferTiles.Clear();
        BankingPositionTiles.Clear();
        BankingAlertTiles.Clear();
        if (!IsBankingFinanceParent) return;

        var statementEligibleAccounts = FinanceAccounts
            .Where(account => account.Category is "Bank" or "OD")
            .ToArray();
        var statementAccounts = BankSummaries
            .Where(summary => !string.IsNullOrWhiteSpace(summary.Account))
            .Select(summary => summary.Account.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var statementsReceived = statementAccounts.Length;
        var statementsPending = Math.Max(0, statementEligibleAccounts.Length - statementAccounts.Length);
        var verifiedAccounts = BankSummaries.Count(summary =>
            (!summary.Difference.HasValue || Math.Round(summary.Difference.Value, 2) == 0m) &&
            (summary.Status.Contains("Reconciled", StringComparison.OrdinalIgnoreCase) || summary.DebitCreditStatus.Contains("verified", StringComparison.OrdinalIgnoreCase)));
        var mismatchAccounts = BankSummaries.Count(summary => summary.Difference.HasValue && Math.Round(summary.Difference.Value, 2) != 0m);
        var movementVerified = BankSummaries.Count(summary => summary.DebitCreditStatus.Contains("verified", StringComparison.OrdinalIgnoreCase));
        var closingEvidence = BankSummaries.Count(summary => !string.IsNullOrWhiteSpace(summary.StatementClosing));
        var missingPeriods = BankSummaries.Count(summary => summary.Status.Contains("missing", StringComparison.OrdinalIgnoreCase) && summary.Status.Contains("period", StringComparison.OrdinalIgnoreCase));

        BankingStatementTiles.ReplaceWith(new[]
        {
            new AuditMetricTile("Statements Received", statementsReceived.ToString("N0", IndianCulture), "Bank / OD accounts with statement evidence"),
            new AuditMetricTile("Statements Pending", statementsPending.ToString("N0", IndianCulture), "Bank / OD accounts awaiting statements"),
            new AuditMetricTile("Movement Verified", movementVerified.ToString("N0", IndianCulture), "Debit and credit totals verified"),
            new AuditMetricTile("Closing Evidence", closingEvidence.ToString("N0", IndianCulture), "Statement closing balance available")
        });

        BankingVerificationTiles.ReplaceWith(new[]
        {
            new AuditMetricTile("Verified Accounts", verifiedAccounts.ToString("N0", IndianCulture), "Books vs statement checks passed"),
            new AuditMetricTile("Pending Accounts", Math.Max(0, statementEligibleAccounts.Length - verifiedAccounts - mismatchAccounts).ToString("N0", IndianCulture), "Awaiting complete evidence"),
            new AuditMetricTile("Balance Differences", mismatchAccounts.ToString("N0", IndianCulture), "Non-zero books vs statement difference"),
            new AuditMetricTile("Missing Periods", missingPeriods.ToString("N0", IndianCulture), "Statement coverage gaps")
        });

        var transferRows = Rows.Where(IsTransferRow).ToArray();
        int DistinctTransferCount(IEnumerable<AuditRecordRow> rows) => rows
            .Select(row => row.AuthorityId.Length > 0 ? row.AuthorityId : $"{row.DateText}|{row.Reference}|{row.Amount:0.00}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var confirmedTransfers = transferRows.Where(row => row.Status.Contains("Statement confirmed inter-bank", StringComparison.OrdinalIgnoreCase)).ToArray();
        var oneSideTransfers = transferRows.Where(row => row.EvidenceStatus.Contains("One statement found", StringComparison.OrdinalIgnoreCase)).ToArray();
        var waitingTransfers = transferRows.Where(row =>
            row.EvidenceStatus.Contains("wait for both bank statements", StringComparison.OrdinalIgnoreCase) ||
            row.Status.Contains("Pending statement confirmation", StringComparison.OrdinalIgnoreCase)).ToArray();
        var transferAlerts = transferRows.Where(IsExceptionRow).ToArray();
        BankingTransferTiles.ReplaceWith(new[]
        {
            new AuditMetricTile("Books Transfers", DistinctTransferCount(transferRows).ToString("N0", IndianCulture), "Contra / inter-bank identities found in books"),
            new AuditMetricTile("Confirmed", DistinctTransferCount(confirmedTransfers).ToString("N0", IndianCulture), "Both statement sides confirmed"),
            new AuditMetricTile("One Side Found", DistinctTransferCount(oneSideTransfers).ToString("N0", IndianCulture), "Waiting for opposite bank statement"),
            new AuditMetricTile("Awaiting Statements", DistinctTransferCount(waitingTransfers).ToString("N0", IndianCulture), "Books pair identified; statement confirmation pending"),
            new AuditMetricTile("Transfer Alerts", DistinctTransferCount(transferAlerts).ToString("N0", IndianCulture), "Transfer evidence requiring review")
        });

        var bankRows = Rows.Where(row => row.Category.Equals("Bank", StringComparison.OrdinalIgnoreCase)).ToArray();
        var odRows = Rows.Where(row => row.Category.Equals("OD", StringComparison.OrdinalIgnoreCase)).ToArray();
        var loanRows = Rows.Where(row => row.Category.Equals("Loans", StringComparison.OrdinalIgnoreCase)).ToArray();
        var cashWithdrawn = Rows.Where(row => row.PaymentReceiptType.Contains("Cash Withdrawal", StringComparison.OrdinalIgnoreCase)).Sum(row => row.Amount);
        var cashDeposited = Rows.Where(row => row.PaymentReceiptType.Contains("Cash Deposit", StringComparison.OrdinalIgnoreCase)).Sum(row => row.Amount);
        BankingPositionTiles.ReplaceWith(new[]
        {
            new AuditMetricTile("Bank Movement", bankRows.Sum(row => row.Amount).ToString("N2", IndianCulture), $"{FormatCount(FinanceAccounts.Count(account => account.Category == "Bank"))} Bank account(s)"),
            new AuditMetricTile("OD Movement", odRows.Sum(row => row.Amount).ToString("N2", IndianCulture), $"{FormatCount(FinanceAccounts.Count(account => account.Category == "OD"))} OD account(s)"),
            new AuditMetricTile("Loan Movement", loanRows.Sum(row => row.Amount).ToString("N2", IndianCulture), $"{FormatCount(FinanceAccounts.Count(account => account.Category == "Loans"))} secured loan account(s)"),
            new AuditMetricTile("Cash Withdrawn", cashWithdrawn.ToString("N2", IndianCulture), "Bank → Cash"),
            new AuditMetricTile("Cash Deposited", cashDeposited.ToString("N2", IndianCulture), "Cash → Bank")
        });

        var unclassified = Rows.Count(row => string.IsNullOrWhiteSpace(row.CounterpartyGroup) || row.CounterpartyGroup.Equals("Other / Unclassified", StringComparison.OrdinalIgnoreCase));
        var statutory = Rows.Count(row => row.RuleReview.Length > 0);
        var pendingTransfers = DistinctTransferCount(waitingTransfers.Concat(oneSideTransfers));
        BankingAlertTiles.ReplaceWith(new[]
        {
            new AuditMetricTile("Missing Statements", statementsPending.ToString("N0", IndianCulture), "Bank / OD statement evidence pending"),
            new AuditMetricTile("Balance Differences", mismatchAccounts.ToString("N0", IndianCulture), "Books vs statement differences"),
            new AuditMetricTile("Pending Transfers", pendingTransfers.ToString("N0", IndianCulture), "Statement-side confirmation incomplete"),
            new AuditMetricTile("Unclassified", unclassified.ToString("N0", IndianCulture), "Counterparty group needs classification"),
            new AuditMetricTile("Statutory Review", statutory.ToString("N0", IndianCulture), "Rule candidates requiring audit review")
        });
    }

    private static bool IsTransferRow(AuditRecordRow row) =>
        row.TransactionClass.Contains("Contra", StringComparison.OrdinalIgnoreCase) ||
        row.TransactionClass.Contains("Inter-bank", StringComparison.OrdinalIgnoreCase) ||
        row.PaymentReceiptType.Contains("Inter-bank", StringComparison.OrdinalIgnoreCase);

    private static AuditBankCashMovementSummary[] BuildCashBankMovementSummary(AuditRecordRow[] rows)
    {
        static string ResolveBank(AuditRecordRow row)
        {
            var names = (row.Counterparty ?? string.Empty)
                .Split(" | ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return names.FirstOrDefault() ?? "Bank account";
        }

        return rows
            .Where(row => row.PaymentReceiptType.Contains("Bank", StringComparison.OrdinalIgnoreCase))
            .GroupBy(ResolveBank, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AuditBankCashMovementSummary
            {
                Bank = group.Key,
                WithdrawalsCount = group.Count(row => row.PaymentReceiptType.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase)),
                WithdrawalAmount = group.Where(row => row.PaymentReceiptType.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase)).Sum(row => row.Amount),
                DepositsCount = group.Count(row => row.PaymentReceiptType.Contains("Deposit", StringComparison.OrdinalIgnoreCase)),
                DepositAmount = group.Where(row => row.PaymentReceiptType.Contains("Deposit", StringComparison.OrdinalIgnoreCase)).Sum(row => row.Amount)
            })
            .OrderByDescending(row => row.WithdrawalAmount + row.DepositAmount)
            .ThenBy(row => row.Bank, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void OpenDebtorCreditorSection(object? parameter)
    {
        var key = (parameter?.ToString() ?? string.Empty).Trim();
        if (key is not "Debtors" and not "Creditors") return;
        var target = Modules.FirstOrDefault(module => module.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (target is not null) SelectedModule = target;
    }

    private void ToggleLedgerVisibility()
    {
        _ledgerVisibilityMode = (_ledgerVisibilityMode + 1) % 3;
        OnPropertyChanged(nameof(LedgerVisibilityFilterText));
        OnPropertyChanged(nameof(LedgerVisibilityFilterHint));
        ApplyRowFilter();
        ApplyFinancialStatementFilter();
    }

    private void OpenFinanceAccountFromParent(object? parameter)
    {
        if (parameter is not AuditFinanceAccountSummary account) return;
        var moduleKey = account.Category switch
        {
            "Bank" => "Bank",
            "OD" => "OD",
            "Loans" => "Loans",
            "Cash" => "Cash",
            _ => string.Empty
        };
        if (moduleKey.Length == 0) return;
        if (SelectedModule?.Key.Equals(moduleKey, StringComparison.OrdinalIgnoreCase) == true)
        {
            SelectedFinanceAccount = FinanceAccounts.FirstOrDefault(item => item.Account.Equals(account.Account, StringComparison.OrdinalIgnoreCase)) ?? account;
            return;
        }
        var target = Modules.FirstOrDefault(module => module.Key.Equals(moduleKey, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;
        _pendingFinanceAccountName = account.Account;
        SelectedModule = target;
    }

    private void RaiseBankingTabVisibility()
    {
        OnPropertyChanged(nameof(IsBankingOverviewTab));
        OnPropertyChanged(nameof(IsBankingAccountsTab));
        OnPropertyChanged(nameof(IsBankingStatementStatusTab));
        OnPropertyChanged(nameof(IsBankingBooksVsStatementTab));
        OnPropertyChanged(nameof(IsBankingTransfersTab));
        OnPropertyChanged(nameof(IsBankingPositionTab));
        OnPropertyChanged(nameof(IsBankingAlertsTab));
    }

    private void RaiseCommerceTabVisibility()
    {
        OnPropertyChanged(nameof(IsCommerceModule));
        OnPropertyChanged(nameof(IsCommerceDashboardVisible));
        OnPropertyChanged(nameof(IsCommerceOverviewTab));
        OnPropertyChanged(nameof(IsCommerceGroupsTab));
        OnPropertyChanged(nameof(IsCommerceVoucherTypesTab));
        OnPropertyChanged(nameof(IsCommerceGstTab));
        OnPropertyChanged(nameof(IsCommerceItemsTab));
        OnPropertyChanged(nameof(IsCommerceHsnTab));
        OnPropertyChanged(nameof(IsCommerceAdjustmentsTab));
        OnPropertyChanged(nameof(IsCommerceBooksVsGstr1Tab));
        OnPropertyChanged(nameof(IsCommerceBooksVs2ATab));
        OnPropertyChanged(nameof(IsCommerceBooksVs2BTab));
        OnPropertyChanged(nameof(IsCommerceBooksVsTaxTab));
        OnPropertyChanged(nameof(IsCommerceRcmTab));
        OnPropertyChanged(nameof(IsCommerceVoucherRegisterTab));
        OnPropertyChanged(nameof(IsCommerceExtraTab));
        OnPropertyChanged(nameof(IsCommerceAlertsTab));
        OnPropertyChanged(nameof(CommercePartyNoun));
        OnPropertyChanged(nameof(CommerceGroupNoun));
        OnPropertyChanged(nameof(CommerceLedgerOverviewTitle));
        OnPropertyChanged(nameof(CommerceTaxTableTitle));
        RaiseCommerceDrilldownVisibility();
    }

    private Dictionary<string, (decimal? Opening, decimal? Closing)> BuildFinanceBooksBalanceLookup()
    {
        var result = new Dictionary<string, (decimal? Opening, decimal? Closing)>(StringComparer.OrdinalIgnoreCase);
        var client = SelectedAuditClient;
        if (client is null || string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)) return result;
        foreach (var ledger in _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey))
        {
            var balances = (ledger.OpeningBalance, ledger.ClosingBalance);
            if (!string.IsNullOrWhiteSpace(ledger.DisplayName)) result[ledger.DisplayName.Trim()] = balances;
            if (!string.IsNullOrWhiteSpace(ledger.ExactName)) result[ledger.ExactName.Trim()] = balances;
        }
        return result;
    }

    private AuditFinanceAccountSummary BuildFinanceAccountSummary(string account, AuditRecordRow[] rows, decimal? booksOpening, decimal? tallyBooksClosing)
    {
        var category = rows.Select(row => row.Category).FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        var matching = BankSummaries.FirstOrDefault(summary => summary.Account.Equals(account, StringComparison.OrdinalIgnoreCase));
        var booksDebit = rows.Sum(row => row.Debit);
        var booksCredit = rows.Sum(row => row.Credit);
        decimal? calculatedBooksClosing = booksOpening.HasValue
            ? Math.Round(booksOpening.Value + booksDebit - booksCredit, 2, MidpointRounding.AwayFromZero)
            : null;
        var booksClosing = tallyBooksClosing ?? calculatedBooksClosing;
        var cashWithdrawn = rows.Where(IsCashWithdrawalMovement).Sum(row => row.Credit > 0m ? row.Credit : row.Debit);
        var cashDeposited = rows.Where(IsCashDepositMovement).Sum(row => row.Debit > 0m ? row.Debit : row.Credit);
        return new AuditFinanceAccountSummary
        {
            Account = account,
            Category = category,
            RowCount = rows.Length,
            BooksOpening = booksOpening,
            BooksDebit = booksDebit,
            BooksCredit = booksCredit,
            BooksClosing = booksClosing,
            BooksClosingFromTally = tallyBooksClosing.HasValue,
            CalculatedBooksClosing = calculatedBooksClosing,
            CashWithdrawn = cashWithdrawn,
            CashDeposited = cashDeposited,
            StatementDebit = matching?.StatementDebit ?? 0m,
            StatementCredit = matching?.StatementCredit ?? 0m,
            Opening = matching?.Opening ?? string.Empty,
            ExpectedClosing = matching?.ExpectedClosing ?? string.Empty,
            StatementClosing = matching?.StatementClosing ?? string.Empty,
            Difference = matching?.Difference,
            ReconciliationStatus = matching?.Status ?? (IsCash ? "Books Cash authority" : category.Equals("Loans", StringComparison.OrdinalIgnoreCase) ? "Books loan authority" : "Statement evidence awaited")
        };
    }

    // Fix99 R2 compatibility proof for moved presentation labels: Statement Debit / Statement Credit / Debit/Credit.
    private void RebuildSelectedFinanceAccountPresentation()
    {
        FinancialDetailTiles.Clear();
        PaymentGroupSummaries.Clear();
        ReceiptGroupSummaries.Clear();
        PaymentGroupChartSeries = Array.Empty<ISeries>();
        ReceiptGroupChartSeries = Array.Empty<ISeries>();
        PaymentGroupChartXAxes = Array.Empty<Axis>();
        PaymentGroupChartYAxes = Array.Empty<Axis>();
        ReceiptGroupChartXAxes = Array.Empty<Axis>();
        ReceiptGroupChartYAxes = Array.Empty<Axis>();
        OnPropertyChanged(nameof(PaymentGroupChartSeries));
        OnPropertyChanged(nameof(ReceiptGroupChartSeries));
        OnPropertyChanged(nameof(PaymentGroupChartXAxes));
        OnPropertyChanged(nameof(PaymentGroupChartYAxes));
        OnPropertyChanged(nameof(ReceiptGroupChartXAxes));
        OnPropertyChanged(nameof(ReceiptGroupChartYAxes));
        CashBankMovements.Clear();
        SelectedAccountAlertTiles.Clear();
        var selected = _selectedFinanceAccount;
        if (selected is null) return;

        var rows = Rows.Where(row => row.PartyOrLedger.Equals(selected.Account, StringComparison.OrdinalIgnoreCase)).ToArray();
        var payments = rows.Where(IsPaymentMovement).ToArray();
        var receipts = rows.Where(IsReceiptMovement).ToArray();
        var cashWithdrawn = rows.Where(IsCashWithdrawalMovement).Sum(row => row.Credit > 0m ? row.Credit : row.Debit);
        var cashDeposited = rows.Where(IsCashDepositMovement).Sum(row => row.Debit > 0m ? row.Debit : row.Credit);

        if (IsCash)
        {
            FinancialDetailTiles.ReplaceWith(new[]
            {
                new AuditMetricTile("Books Opening", selected.BooksOpeningDisplay, "Tally Ledger opening authority", selected.IsOpeningNatureAlert),
                new AuditMetricTile("Books Closing", selected.BooksClosingDisplay, selected.BooksClosingAuthorityText, selected.IsClosingNatureAlert),
                new AuditMetricTile("Cash Receipts", receipts.Sum(row => MovementAmount(row, isPayment: false)).ToString("N2", IndianCulture), $"{FormatCount(receipts.Length)} receipt(s)"),
                new AuditMetricTile("Cash Payments", payments.Sum(row => MovementAmount(row, isPayment: true)).ToString("N2", IndianCulture), $"{FormatCount(payments.Length)} payment(s)"),
                new AuditMetricTile("Bank Withdrawals", cashWithdrawn.ToString("N2", IndianCulture), "Bank → Cash movement"),
                new AuditMetricTile("Bank Deposits", cashDeposited.ToString("N2", IndianCulture), "Cash → Bank movement"),
                new AuditMetricTile("Books Debit", selected.BooksDebit.ToString("N2", IndianCulture), "Cash-in-Hand FY authority"),
                new AuditMetricTile("Books Credit", selected.BooksCredit.ToString("N2", IndianCulture), "Cash-in-Hand FY authority")
            });
        }
        else if (IsOD)
        {
            var interestCharges = rows.Where(IsInterestOrFinanceChargeMovement).Sum(row => row.Amount);
            FinancialDetailTiles.ReplaceWith(new[]
            {
                new AuditMetricTile("Books Opening", selected.BooksOpeningDisplay, "Tally Bank OD ledger opening authority", selected.IsOpeningNatureAlert),
                new AuditMetricTile("Books Closing", selected.BooksClosingDisplay, selected.BooksClosingAuthorityText, selected.IsClosingNatureAlert),
                new AuditMetricTile("Books Debit", selected.BooksDebit.ToString("N2", IndianCulture), "OD debit-side movement · FY"),
                new AuditMetricTile("Books Credit", selected.BooksCredit.ToString("N2", IndianCulture), "OD credit-side movement · FY"),
                new AuditMetricTile("OD Payments / Drawings", payments.Sum(row => MovementAmount(row, isPayment: true)).ToString("N2", IndianCulture), $"{FormatCount(payments.Length)} outward movement(s)"),
                new AuditMetricTile("OD Receipts / Credits", receipts.Sum(row => MovementAmount(row, isPayment: false)).ToString("N2", IndianCulture), $"{FormatCount(receipts.Length)} inward movement(s)"),
                new AuditMetricTile("Interest / Charges", interestCharges.ToString("N2", IndianCulture), "Interest, finance-charge and bank-charge postings"),
                new AuditMetricTile("Balance Nature", selected.ClosingNatureStatus, "Bank OD normally carries a Credit closing balance", selected.IsClosingNatureAlert),
                new AuditMetricTile("Cash Withdrawn", cashWithdrawn.ToString("N2", IndianCulture), "OD → Cash movement"),
                new AuditMetricTile("Cash Deposited", cashDeposited.ToString("N2", IndianCulture), "Cash → OD movement")
            });
        }
        else if (IsLoans)
        {
            var interestCharges = rows.Where(IsInterestOrFinanceChargeMovement).Sum(row => row.Amount);
            FinancialDetailTiles.ReplaceWith(new[]
            {
                new AuditMetricTile("Books Opening", selected.BooksOpeningDisplay, "Tally Secured Loans ledger opening authority", selected.IsOpeningNatureAlert),
                new AuditMetricTile("Books Closing", selected.BooksClosingDisplay, selected.BooksClosingAuthorityText, selected.IsClosingNatureAlert),
                new AuditMetricTile("Loan Additions", receipts.Sum(row => MovementAmount(row, isPayment: false)).ToString("N2", IndianCulture), $"{FormatCount(receipts.Length)} borrowing/addition movement(s)"),
                new AuditMetricTile("Repayments", payments.Sum(row => MovementAmount(row, isPayment: true)).ToString("N2", IndianCulture), $"{FormatCount(payments.Length)} repayment movement(s)"),
                new AuditMetricTile("Interest / Charges", interestCharges.ToString("N2", IndianCulture), "Interest / finance-charge postings linked to the loan"),
                new AuditMetricTile("Books Debit", selected.BooksDebit.ToString("N2", IndianCulture), "Repayment / debit-side authority"),
                new AuditMetricTile("Books Credit", selected.BooksCredit.ToString("N2", IndianCulture), "Borrowing / credit-side authority"),
                new AuditMetricTile("Balance Nature", selected.ClosingNatureStatus, "Secured Loan normally carries a Credit closing balance", selected.IsClosingNatureAlert),
                new AuditMetricTile("Alerts", rows.Count(IsExceptionRow).ToString("N0", IndianCulture), "Loan items requiring review")
            });
        }
        else
        {
            FinancialDetailTiles.ReplaceWith(new[]
            {
                new AuditMetricTile("Books Opening", selected.BooksOpeningDisplay, "Tally Ledger opening authority", selected.IsOpeningNatureAlert),
                new AuditMetricTile("Books Closing", selected.BooksClosingDisplay, selected.BooksClosingAuthorityText, selected.IsClosingNatureAlert),
                new AuditMetricTile("Books Debit", selected.BooksDebit.ToString("N2", IndianCulture), "Selected account · FY"),
                new AuditMetricTile("Books Credit", selected.BooksCredit.ToString("N2", IndianCulture), "Selected account · FY"),
                new AuditMetricTile("Cash Withdrawn", cashWithdrawn.ToString("N2", IndianCulture), "Bank → Cash movement"),
                new AuditMetricTile("Cash Deposited", cashDeposited.ToString("N2", IndianCulture), "Cash → Bank movement"),
                new AuditMetricTile("Statement Status", selected.StatementStatus, "Linked/imported statement evidence"),
                new AuditMetricTile("Verification", selected.VerificationStatus, "Books vs statement status")
            });
        }

        PaymentGroupSummaries.ReplaceWith(BuildMovementGroupSummary(payments, isPayment: true));
        ReceiptGroupSummaries.ReplaceWith(BuildMovementGroupSummary(receipts, isPayment: false));
        (PaymentGroupChartSeries, PaymentGroupChartXAxes, PaymentGroupChartYAxes) = BuildGroupAmountBarChart(PaymentGroupSummaries, "Payments");
        (ReceiptGroupChartSeries, ReceiptGroupChartXAxes, ReceiptGroupChartYAxes) = BuildGroupAmountBarChart(ReceiptGroupSummaries, "Receipts");
        OnPropertyChanged(nameof(PaymentGroupChartSeries));
        OnPropertyChanged(nameof(ReceiptGroupChartSeries));
        OnPropertyChanged(nameof(PaymentGroupChartXAxes));
        OnPropertyChanged(nameof(PaymentGroupChartYAxes));
        OnPropertyChanged(nameof(ReceiptGroupChartXAxes));
        OnPropertyChanged(nameof(ReceiptGroupChartYAxes));
        if (IsCash) CashBankMovements.ReplaceWith(BuildCashBankMovementSummary(rows));

        var accountAlerts = rows.Count(IsExceptionRow);
        var unclassified = rows.Count(row => string.IsNullOrWhiteSpace(row.CounterpartyGroup) || row.CounterpartyGroup.Equals("Other / Unclassified", StringComparison.OrdinalIgnoreCase));
        var transferPending = rows.Count(row => IsTransferRow(row) && !row.Status.Contains("Statement confirmed inter-bank", StringComparison.OrdinalIgnoreCase));
        var statutory = rows.Count(row => row.RuleReview.Length > 0);
        SelectedAccountAlertTiles.ReplaceWith(new[]
        {
            new AuditMetricTile("Statement", IsCash || IsLoans ? "N/A" : selected.StatementStatus, IsCash ? "Cash uses books authority" : IsLoans ? "Secured Loans use books / lender evidence; Bank statement verification does not apply" : "Statement evidence status"),
            new AuditMetricTile("Balance Nature", selected.ClosingNatureStatus, selected.ExpectedBalanceNatureText, selected.IsClosingNatureAlert),
            new AuditMetricTile("Balance Difference", IsLoans ? "N/A" : selected.Difference.HasValue ? selected.Difference.Value.ToString("N2", IndianCulture) : "—", IsLoans ? "Bank statement difference does not apply to loan accounts" : "Books vs statement difference"),
            new AuditMetricTile("Pending Transfers", transferPending.ToString("N0", IndianCulture), "Transfer confirmation incomplete"),
            new AuditMetricTile("Unclassified", unclassified.ToString("N0", IndianCulture), "Counterparty group needs classification"),
            new AuditMetricTile("Statutory Review", statutory.ToString("N0", IndianCulture), "Rule candidates"),
            new AuditMetricTile("Alerts", accountAlerts.ToString("N0", IndianCulture), "Rows requiring review")
        });
    }

    private static bool IsInterestOrFinanceChargeMovement(AuditRecordRow row)
    {
        var text = string.Join(" ", row.CounterpartyGroup, row.Counterparty, row.Details, row.OriginalNarration, row.PaymentReceiptType, row.TransactionClass);
        return text.Contains("interest", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("bank charge", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("bank charges", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("finance charge", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("finance charges", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("processing fee", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPaymentMovement(AuditRecordRow row) =>
        row.Category.Equals("Loans", StringComparison.OrdinalIgnoreCase) ? row.Debit > 0m : row.Credit > 0m;

    private static bool IsReceiptMovement(AuditRecordRow row) =>
        row.Category.Equals("Loans", StringComparison.OrdinalIgnoreCase) ? row.Credit > 0m : row.Debit > 0m;

    private static bool IsCashWithdrawalMovement(AuditRecordRow row) =>
        row.PaymentReceiptType.Equals("Cash Withdrawal", StringComparison.OrdinalIgnoreCase) ||
        row.PaymentReceiptType.Equals("Cash Withdrawal from Bank", StringComparison.OrdinalIgnoreCase) ||
        (row.TransactionClass.Contains("Bank", StringComparison.OrdinalIgnoreCase) &&
         row.TransactionClass.Contains("Cash", StringComparison.OrdinalIgnoreCase) &&
         row.Credit > 0m);

    private static bool IsCashDepositMovement(AuditRecordRow row) =>
        row.PaymentReceiptType.Equals("Cash Deposit", StringComparison.OrdinalIgnoreCase) ||
        row.PaymentReceiptType.Equals("Cash Deposit to Bank", StringComparison.OrdinalIgnoreCase) ||
        (row.TransactionClass.Contains("Bank", StringComparison.OrdinalIgnoreCase) &&
         row.TransactionClass.Contains("Cash", StringComparison.OrdinalIgnoreCase) &&
         row.Debit > 0m);

    private static decimal MovementAmount(AuditRecordRow row, bool isPayment)
    {
        var loan = row.Category.Equals("Loans", StringComparison.OrdinalIgnoreCase);
        return isPayment ? (loan ? row.Debit : row.Credit) : (loan ? row.Credit : row.Debit);
    }

    private static AuditGroupMovementSummary[] BuildMovementGroupSummary(AuditRecordRow[] rows, bool isPayment)
    {
        var total = rows.Sum(row => MovementAmount(row, isPayment));
        return rows
            .GroupBy(row => string.IsNullOrWhiteSpace(row.CounterpartyGroup) ? "Other / Unclassified" : row.CounterpartyGroup.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var amount = group.Sum(row => MovementAmount(row, isPayment));
                return new AuditGroupMovementSummary
                {
                    Group = group.Key,
                    Count = group.Count(),
                    Amount = amount,
                    SharePercent = total == 0m ? 0m : Math.Round(amount / total * 100m, 2, MidpointRounding.AwayFromZero),
                    MovementType = string.Join(" / ", group.Select(row => row.PaymentReceiptType).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                };
            })
            .OrderByDescending(summary => summary.Amount)
            .ThenBy(summary => summary.Group, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (ISeries[] Series, Axis[] XAxes, Axis[] YAxes) BuildGroupAmountBarChart(IEnumerable<AuditGroupMovementSummary> groups, string title)
    {
        var ordered = groups.Where(group => group.Amount > 0m).OrderByDescending(group => group.Amount).ToArray();
        if (ordered.Length == 0) return (Array.Empty<ISeries>(), Array.Empty<Axis>(), Array.Empty<Axis>());
        var series = new ISeries[]
        {
            new RowSeries<double>
            {
                Name = title,
                Values = ordered.Select(group => (double)group.Amount).ToArray(),
                MaxBarWidth = 38,
                Padding = 6
            }
        };
        var xAxes = new[]
        {
            new Axis
            {
                Labeler = value => FormatChartMoney(value),
                MinLimit = 0
            }
        };
        var yAxes = new[]
        {
            new Axis
            {
                Labels = ordered.Select(group => group.Group).ToArray(),
                MinStep = 1,
                ForceStepToMin = true
            }
        };
        return (series, xAxes, yAxes);
    }

    private static string FormatChartMoney(double value)
    {
        var amount = Math.Abs(value);
        if (amount >= 10_000_000d) return $"{(value / 10_000_000d).ToString("0.##", IndianCulture)} Cr";
        if (amount >= 100_000d) return $"{(value / 100_000d).ToString("0.##", IndianCulture)} L";
        if (amount >= 1_000d) return $"{(value / 1_000d).ToString("0.##", IndianCulture)} K";
        return value.ToString("N0", IndianCulture);
    }

    private static string CounterpartyDrilldownKey(AuditRecordRow row) =>
        string.IsNullOrWhiteSpace(row.Counterparty) ? "Other / Unidentified" : row.Counterparty.Trim();

    private static AuditPartyMovementSummary[] BuildPartyMovementSummary(IEnumerable<AuditRecordRow> rows, bool isPayment)
    {
        return rows
            .GroupBy(CounterpartyDrilldownKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AuditPartyMovementSummary
            {
                Counterparty = group.Key,
                Count = group.Count(),
                Amount = group.Sum(row => MovementAmount(row, isPayment)),
                MovementType = string.Join(" / ", group.Select(row => row.PaymentReceiptType).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            })
            .OrderByDescending(summary => summary.Amount)
            .ThenBy(summary => summary.Counterparty, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ShowTransactions()
    {
        if (!IsFinanceAccountDashboardModule || SelectedFinanceAccount is null) return;
        ResetFinanceDrilldown();
        OpenFinanceTransactions($"{SelectedFinanceAccount.Account} · All Transactions");
    }

    private void OpenAllPaymentTransactions()
    {
        if (SelectedFinanceAccount is null) return;
        _financeDrilldownMode = "Payments";
        _financeDrilldownGroup = string.Empty;
        _financeDrilldownBank = string.Empty;
        OpenFinanceTransactions("Payments · All Groups");
    }

    private void OpenAllReceiptTransactions()
    {
        if (SelectedFinanceAccount is null) return;
        _financeDrilldownMode = "Receipts";
        _financeDrilldownGroup = string.Empty;
        _financeDrilldownBank = string.Empty;
        OpenFinanceTransactions("Receipts · All Groups");
    }

    private void OpenPaymentGroupTransactions(object? parameter)
    {
        if (parameter is not AuditGroupMovementSummary summary || SelectedFinanceAccount is null) return;
        OpenFinanceGroupSummary(summary.Group, isPayment: true);
    }

    private void OpenReceiptGroupTransactions(object? parameter)
    {
        if (parameter is not AuditGroupMovementSummary summary || SelectedFinanceAccount is null) return;
        OpenFinanceGroupSummary(summary.Group, isPayment: false);
    }

    private void OpenFinanceGroupSummary(string group, bool isPayment)
    {
        if (SelectedFinanceAccount is null) return;
        var rows = Rows
            .Where(row => row.PartyOrLedger.Equals(SelectedFinanceAccount.Account, StringComparison.OrdinalIgnoreCase))
            .Where(row => (isPayment ? IsPaymentMovement(row) : IsReceiptMovement(row)))
            .Where(row => (string.IsNullOrWhiteSpace(row.CounterpartyGroup) ? "Other / Unclassified" : row.CounterpartyGroup.Trim()).Equals(group, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _transactionsVisible = false;
        _financeGroupSummaryVisible = true;
        _financeGroupSummaryDirection = isPayment ? "Payments" : "Receipts";
        _financeGroupSummaryTitle = $"{_financeGroupSummaryDirection} · {group}";
        _financeDrilldownMode = isPayment ? "PaymentGroupSummary" : "ReceiptGroupSummary";
        _financeDrilldownGroup = group;
        _financeDrilldownBank = string.Empty;
        _financeDrilldownCounterparty = string.Empty;
        FinanceGroupPartySummaries.ReplaceWith(BuildPartyMovementSummary(rows, isPayment));
        OnPropertyChanged(nameof(FinanceGroupSummaryVisible));
        OnPropertyChanged(nameof(FinanceGroupSummaryDirection));
        OnPropertyChanged(nameof(FinanceGroupSummaryTitle));
        OnPropertyChanged(nameof(FinanceTransactionsCloseText));
        OnPropertyChanged(nameof(IsFinanceDashboardVisible));
        OnPropertyChanged(nameof(IsFinanceGroupSummaryVisible));
        OnPropertyChanged(nameof(IsFinanceTransactionsVisible));
        OnPropertyChanged(nameof(IsTransactionToolsVisible));
        CloseGroupSummaryCommand.RaiseCanExecuteChanged();
        ApplyRowFilter();
    }

    private void OpenPartyTransactions(object? parameter)
    {
        if (parameter is not AuditPartyMovementSummary summary || SelectedFinanceAccount is null || !_financeGroupSummaryVisible) return;
        _financeDrilldownCounterparty = summary.Counterparty;
        _financeDrilldownMode = _financeGroupSummaryDirection.Equals("Payments", StringComparison.OrdinalIgnoreCase)
            ? "PaymentGroupParty"
            : "ReceiptGroupParty";
        OpenFinanceTransactions($"{_financeGroupSummaryTitle} · {summary.Counterparty}");
    }

    private void CloseGroupSummary()
    {
        if (!_financeGroupSummaryVisible || _transactionsVisible) return;
        _financeGroupSummaryVisible = false;
        FinanceGroupPartySummaries.Clear();
        ResetFinanceDrilldown();
        OnPropertyChanged(nameof(FinanceGroupSummaryVisible));
        OnPropertyChanged(nameof(FinanceTransactionsCloseText));
        OnPropertyChanged(nameof(IsFinanceDashboardVisible));
        OnPropertyChanged(nameof(IsFinanceGroupSummaryVisible));
        OnPropertyChanged(nameof(IsFinanceTransactionsVisible));
        OnPropertyChanged(nameof(IsTransactionToolsVisible));
        CloseGroupSummaryCommand.RaiseCanExecuteChanged();
        ApplyRowFilter();
    }

    private void OpenCashMovementTransactions(object? parameter)
    {
        if (SelectedFinanceAccount is null) return;
        switch (parameter)
        {
            case AuditBankCashMovementSummary bankMovement:
                _financeDrilldownMode = "CashBank";
                _financeDrilldownBank = bankMovement.Bank;
                _financeDrilldownGroup = string.Empty;
                OpenFinanceTransactions($"Cash Movement · {bankMovement.Bank}");
                break;
            case string direction when direction.Equals("Withdrawal", StringComparison.OrdinalIgnoreCase):
                _financeDrilldownMode = "CashWithdrawal";
                _financeDrilldownGroup = string.Empty;
                _financeDrilldownBank = string.Empty;
                OpenFinanceTransactions("Cash Withdrawals");
                break;
            case string direction when direction.Equals("Deposit", StringComparison.OrdinalIgnoreCase):
                _financeDrilldownMode = "CashDeposit";
                _financeDrilldownGroup = string.Empty;
                _financeDrilldownBank = string.Empty;
                OpenFinanceTransactions("Cash Deposits");
                break;
        }
    }

    private void OpenFinanceTransactions(string title)
    {
        if (!IsFinanceAccountDashboardModule || SelectedFinanceAccount is null) return;
        if (!_financeDrilldownMode.Equals("PaymentGroupParty", StringComparison.OrdinalIgnoreCase) &&
            !_financeDrilldownMode.Equals("ReceiptGroupParty", StringComparison.OrdinalIgnoreCase))
        {
            _financeGroupSummaryVisible = false;
            FinanceGroupPartySummaries.Clear();
        }
        _financeTransactionDrilldownTitle = title;
        _transactionsVisible = true;
        OnPropertyChanged(nameof(FinanceTransactionDrilldownTitle));
        OnPropertyChanged(nameof(FinanceTransactionsCloseText));
        OnPropertyChanged(nameof(TransactionsVisible));
        OnPropertyChanged(nameof(IsFinanceDashboardVisible));
        OnPropertyChanged(nameof(IsFinanceGroupSummaryVisible));
        OnPropertyChanged(nameof(IsFinanceTransactionsVisible));
        OnPropertyChanged(nameof(IsTransactionToolsVisible));
        ShowTransactionsCommand.RaiseCanExecuteChanged();
        CloseTransactionsCommand.RaiseCanExecuteChanged();
        ApplyRowFilter();
    }

    private void ResetFinanceDrilldown()
    {
        _financeDrilldownMode = string.Empty;
        _financeDrilldownGroup = string.Empty;
        _financeDrilldownBank = string.Empty;
        _financeDrilldownCounterparty = string.Empty;
        _financeGroupSummaryDirection = string.Empty;
        _financeGroupSummaryTitle = "Group Detail";
        _financeTransactionDrilldownTitle = "Transactions";
        OnPropertyChanged(nameof(FinanceTransactionDrilldownTitle));
    }

    private void CloseTransactions()
    {
        if (!_transactionsVisible) return;
        var returnToGroupSummary = _financeGroupSummaryVisible &&
            (_financeDrilldownMode.Equals("PaymentGroupParty", StringComparison.OrdinalIgnoreCase) ||
             _financeDrilldownMode.Equals("ReceiptGroupParty", StringComparison.OrdinalIgnoreCase));
        _transactionsVisible = false;
        if (returnToGroupSummary)
        {
            _financeDrilldownMode = _financeGroupSummaryDirection.Equals("Payments", StringComparison.OrdinalIgnoreCase)
                ? "PaymentGroupSummary"
                : "ReceiptGroupSummary";
            _financeDrilldownCounterparty = string.Empty;
            _financeTransactionDrilldownTitle = "Transactions";
            OnPropertyChanged(nameof(FinanceTransactionDrilldownTitle));
        }
        else
        {
            _financeGroupSummaryVisible = false;
            FinanceGroupPartySummaries.Clear();
            ResetFinanceDrilldown();
        }
        OnPropertyChanged(nameof(TransactionsVisible));
        OnPropertyChanged(nameof(FinanceGroupSummaryVisible));
        OnPropertyChanged(nameof(FinanceTransactionsCloseText));
        OnPropertyChanged(nameof(IsFinanceDashboardVisible));
        OnPropertyChanged(nameof(IsFinanceGroupSummaryVisible));
        OnPropertyChanged(nameof(IsFinanceTransactionsVisible));
        OnPropertyChanged(nameof(IsTransactionToolsVisible));
        SelectedRecord = null;
        ShowTransactionsCommand.RaiseCanExecuteChanged();
        CloseTransactionsCommand.RaiseCanExecuteChanged();
        CloseGroupSummaryCommand.RaiseCanExecuteChanged();
        ApplyRowFilter();
    }

    private AuditMetricTile[] BuildTallyAuthorityTiles(AuditClientProfile? client)
    {
        if (client is null || string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey))
            return new[] { new AuditMetricTile("Tally Link", "Not linked", "Link a live Tally company explicitly for this Audit client") };
        EnsureLinkedTallyEvidenceInitialized();
        var ledgers = _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
        var groups = _auditRepository.GetTallyGroupParents(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
        var (from, to) = AdvancedClosingEntryPlanner.FinancialYearRange(client.FinancialYear);
        var vouchers = _tallyRepository.GetVoucherIndexRange(client.LinkedTallyCompanyKey, client.FinancialYear, from, to);
        var sync = _auditRepository.GetTallySyncState(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
        var health = ledgers.Count > 0 && groups.Count > 0 && vouchers.Count > 0 ? "Ready" : "Incomplete";
        var heartbeat = sync is null
            ? "5 sec · establishing"
            : $"5 sec · ALTERID {FormatCount(sync.LastAlterId)}";
        var heartbeatDetail = sync?.LastPulseUtc > DateTime.MinValue
            ? $"Last pulse {sync.LastPulseUtc.ToLocalTime():HH:mm:ss} · {sync.LastStatus}"
            : "Live pulse starts while Audit workspace is active";
        return new[]
        {
            new AuditMetricTile("Linked Company", client.LinkedTallyCompanyName.Length > 0 ? client.LinkedTallyCompanyName : client.LinkedTallyCompanyKey, $"FY {client.FinancialYear}"),
            new AuditMetricTile("Ledger Masters", ledgers.Count.ToString("N0", IndianCulture), "Audit-owned linked Tally Master authority"),
            new AuditMetricTile("Groups", groups.Count.ToString("N0", IndianCulture), "Tally Group hierarchy"),
            new AuditMetricTile("FY Vouchers", vouchers.Count.ToString("N0", IndianCulture), "Cached full-FY voucher authority"),
            new AuditMetricTile("Tally Heartbeat", heartbeat, heartbeatDetail),
            new AuditMetricTile("Authority Health", health, health == "Ready" ? "Masters + Groups + FY vouchers available" : "Use ↻ to repair/refresh linked authority")
        };
    }

    private static string FormatMoney(decimal amount) => amount.ToString("N2", IndianCulture);
    private static string FormatCount(long count) => count.ToString("N0", IndianCulture);

    private static void ApplyFix97StatutoryRulePack(string module, AuditRecordRow[] rows)
    {
        if (rows.Length == 0) return;

        // Fix97 starts the cumulative local Statutory Rule Library. These are deliberately
        // review candidates only: Tally/statement evidence cannot by itself prove every
        // exception, event relationship, outstanding balance or prescribed payment mode.
        if (module.Equals("Cash", StringComparison.OrdinalIgnoreCase))
        {
            var paymentRule = AuditStatutoryRuleCatalog.IncomeTax40A3;
            foreach (var group in rows
                .Where(row => row.Date.HasValue && row.Credit > 0m && row.Counterparty.Trim().Length > 0)
                .GroupBy(row => new { Date = row.Date!.Value.Date, Party = row.Counterparty.Trim().ToUpperInvariant() }))
            {
                var total = group.Sum(row => row.Credit);
                if (total <= paymentRule.Threshold) continue;
                foreach (var row in group)
                    AddStatutoryReview(row, paymentRule, $"Cash payment aggregate {FormatMoney(total)} to this counterparty on {group.Key.Date:dd-MM-yyyy} exceeds {FormatMoney(paymentRule.Threshold)}.");
            }

            var receiptRule = AuditStatutoryRuleCatalog.IncomeTax269ST;
            foreach (var group in rows
                .Where(row => row.Date.HasValue && row.Debit > 0m && row.Counterparty.Trim().Length > 0)
                .GroupBy(row => new { Date = row.Date!.Value.Date, Party = row.Counterparty.Trim().ToUpperInvariant() }))
            {
                var total = group.Sum(row => row.Debit);
                if (total < receiptRule.Threshold) continue;
                foreach (var row in group)
                    AddStatutoryReview(row, receiptRule, $"Cash receipt aggregate {FormatMoney(total)} from this counterparty on {group.Key.Date:dd-MM-yyyy} reaches the {FormatMoney(receiptRule.Threshold)} first-pass threshold.");
            }
        }

        if (module.Equals("BankingFinance", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var row in rows.Where(row => row.Category.Equals("Loans", StringComparison.OrdinalIgnoreCase) && row.TransactionClass.Contains("Cash", StringComparison.OrdinalIgnoreCase)))
            {
                var acceptance = row.Credit > 0m;
                var rule = acceptance ? AuditStatutoryRuleCatalog.IncomeTax269SS : AuditStatutoryRuleCatalog.IncomeTax269T;
                if (row.Amount < rule.Threshold) continue;
                AddStatutoryReview(row, rule, $"Secured-loan Books entry is cash-linked for {FormatMoney(row.Amount)}; review permitted mode, aggregate/outstanding balance and exceptions.");
            }
        }
    }

    private static void AddStatutoryReview(AuditRecordRow row, AuditStatutoryRule rule, string reason)
    {
        if (row.RuleId.Length > 0 && !row.RuleId.Contains(rule.RuleId, StringComparison.OrdinalIgnoreCase))
        {
            row.RuleId += " | " + rule.RuleId;
            row.RuleReference += " | " + rule.Provision;
            row.RuleReview += " | " + reason;
        }
        else if (row.RuleId.Length == 0)
        {
            row.RuleId = rule.RuleId;
            row.RuleReference = $"{rule.Law} · {rule.Provision}";
            row.RuleReview = reason;
        }
        row.IsException = true;
        row.Status = row.Status.Length > 0 ? row.Status + " · Statutory review" : "Statutory review";
        row.EvidenceStatus = row.EvidenceStatus.Length > 0
            ? row.EvidenceStatus + $" · {rule.RuleId} review candidate"
            : $"{rule.RuleId} review candidate";
    }

    private static AuditModuleTab[] BuildModuleTabs(string module, IEnumerable<AuditRecordRow> rows)
    {
        var all = rows.ToArray();
        int Count(Func<AuditRecordRow, bool> predicate) => all.Count(predicate);
        AuditModuleTab Tab(string key, string title, Func<AuditRecordRow, bool>? predicate = null) =>
            new(key, title, predicate is null ? all.Length : Count(predicate));

        return module switch
        {
            "BankingFinance" => new[]
            {
                new AuditModuleTab("Overview", "Overview", -1),
                new AuditModuleTab("StatementStatus", "Statement Status", -1),
                new AuditModuleTab("BooksVsStatement", "Books vs Statement", -1),
                new AuditModuleTab("Transfers", "Transfers", -1),
                new AuditModuleTab("Position", "Position", -1),
                new AuditModuleTab("Alerts", "Alerts", -1)
            },
            "Bank" or "OD" or "Loans" => new[]
            {
                new AuditModuleTab("Dashboard", "Dashboard", -1)
            },
            "Sales" => BuildSalesAuditTabs(all),
            "Purchase" => BuildPurchaseAuditTabs(all),
            "Cash" => new[]
            {
                new AuditModuleTab("Dashboard", "Dashboard", -1)
            },
            "DebtorsCreditors" => Array.Empty<AuditModuleTab>(),
            "Debtors" or "Creditors" => new[]
            {
                Tab("All", "All"),
                Tab("Exceptions", "Exceptions", IsExceptionRow)
            },
            _ => Array.Empty<AuditModuleTab>()
        };
    }

    private static AuditModuleTab[] BuildSalesAuditTabs(AuditRecordRow[] rows)
    {
        var tabs = new List<AuditModuleTab>
        {
            new("Overview", "Overview", -1),
            new("SalesGroups", "Sales Groups", -1),
            new("VoucherTypes", "Voucher Types", -1),
            new("BooksVsGstr1", "GSTR-1 vs Books", -1)
        };
        if (rows.Any(row => row.GstRateKnown || row.GstAmount > 0m)) tabs.Add(new AuditModuleTab("GSTAnalysis", "GST Analysis", -1));
        if (rows.Any(row => !string.IsNullOrWhiteSpace(row.ItemName))) tabs.Add(new AuditModuleTab("Items", "Items", -1));
        if (rows.Any(row => !string.IsNullOrWhiteSpace(row.HsnCode))) tabs.Add(new AuditModuleTab("HSN", "HSN / SAC", -1));
        if (rows.Any(row => IsCommerceAdjustment("Sales", row))) tabs.Add(new AuditModuleTab("Returns", "Returns / Credit Notes", -1));
        tabs.Add(new AuditModuleTab("VoucherRegister", "Voucher Register", -1));
        if (rows.Any(IsExceptionRow)) tabs.Add(new AuditModuleTab("Alerts", "Alerts", -1));
        return tabs.ToArray();
    }

    private static AuditModuleTab[] BuildPurchaseAuditTabs(AuditRecordRow[] rows)
    {
        var tabs = new List<AuditModuleTab>
        {
            new("Overview", "Overview", -1),
            new("PurchaseGroups", "Purchase / Expense Groups", -1),
            new("VoucherTypes", "Voucher Types", -1),
            new("BooksVs2A", "2A vs Books", -1),
            new("BooksVs2B", "2B vs Books", -1)
        };
        if (rows.Any(row => row.GstRateKnown || row.GstAmount > 0m || row.ItcStatus.Length > 0)) tabs.Add(new AuditModuleTab("GstItc", "GST & ITC", -1));
        if (rows.Any(row => !string.IsNullOrWhiteSpace(row.ItemName))) tabs.Add(new AuditModuleTab("Items", "Items", -1));
        if (rows.Any(row => !string.IsNullOrWhiteSpace(row.HsnCode))) tabs.Add(new AuditModuleTab("HSN", "HSN / SAC", -1));
        if (rows.Any(row => IsCommerceAdjustment("Purchase", row))) tabs.Add(new AuditModuleTab("Notes", "Notes / Returns", -1));
        if (rows.Any(row => row.IsRcm)) tabs.Add(new AuditModuleTab("RCM", "RCM", -1));
        tabs.Add(new AuditModuleTab("VoucherRegister", "Voucher Register", -1));
        if (rows.Any(IsExceptionRow)) tabs.Add(new AuditModuleTab("Alerts", "Alerts", -1));
        return tabs.ToArray();
    }

    private static AuditMetricTile[] BuildMetricTiles(
        string module,
        IEnumerable<AuditRecordRow> sourceRows,
        IEnumerable<AuditBankSummaryRow> bankSummaries)
    {
        var rows = sourceRows.ToArray();
        var exceptions = rows.Count(IsExceptionRow);
        var debit = rows.Sum(row => row.Debit);
        var credit = rows.Sum(row => row.Credit);
        var gross = rows.Sum(row => row.Amount);
        static string Money(decimal amount) => amount.ToString("N2", IndianCulture);

        return module switch
        {
            "BankingFinance" or "Bank" or "OD" or "Loans" => BuildBankMetricTiles(rows, bankSummaries.ToArray(), exceptions, debit, credit),
            "Sales" => new[]
            {
                new AuditMetricTile("Entries", rows.Length.ToString("N0", IndianCulture), "All Sales evidence rows"),
                new AuditMetricTile("Sales Invoices", rows.Count(row => row.Category == "Sales Invoices").ToString("N0", IndianCulture), "Invoice tab"),
                new AuditMetricTile("Credit Notes", rows.Count(row => row.Category == "Credit Notes").ToString("N0", IndianCulture), "Credit Note tab"),
                new AuditMetricTile("Gross Value", Money(gross), "Current Audit evidence total"),
                new AuditMetricTile("Exceptions", exceptions.ToString("N0", IndianCulture), "Rows requiring review")
            },
            "Purchase" => new[]
            {
                new AuditMetricTile("Entries", rows.Length.ToString("N0", IndianCulture), "All Purchase evidence rows"),
                new AuditMetricTile("Purchase Invoices", rows.Count(row => row.Category == "Purchase Invoices").ToString("N0", IndianCulture), "Invoice tab"),
                new AuditMetricTile("Debit Notes", rows.Count(row => row.Category == "Debit Notes").ToString("N0", IndianCulture), "Debit Note tab"),
                new AuditMetricTile("Gross Value", Money(gross), "Current Audit evidence total"),
                new AuditMetricTile("Exceptions", exceptions.ToString("N0", IndianCulture), "Rows requiring review")
            },
            "Cash" => new[]
            {
                new AuditMetricTile("Cash Entries", rows.Length.ToString("N0", IndianCulture), "Cash-in-Hand authority"),
                new AuditMetricTile("Receipts", rows.Count(row => row.TransactionClass == "Cash Receipts").ToString("N0", IndianCulture), "Cash receipts"),
                new AuditMetricTile("Payments", rows.Count(row => row.TransactionClass == "Cash Payments").ToString("N0", IndianCulture), "Cash payments"),
                new AuditMetricTile("Bank ↔ Cash", rows.Count(row => row.TransactionClass.Contains("Bank", StringComparison.OrdinalIgnoreCase) && row.TransactionClass.Contains("Cash", StringComparison.OrdinalIgnoreCase)).ToString("N0", IndianCulture), "Contra candidates"),
                new AuditMetricTile("Debit", Money(debit), "Total debit"),
                new AuditMetricTile("Credit", Money(credit), "Total credit"),
                new AuditMetricTile("Statutory Review", rows.Count(row => row.RuleReview.Length > 0).ToString("N0", IndianCulture), "Fix97 Income-tax rule candidates"),
                new AuditMetricTile("Exceptions", exceptions.ToString("N0", IndianCulture), "Rows requiring review")
            },
            "DebtorsCreditors" => new[]
            {
                new AuditMetricTile("Debtors", rows.Count(row => row.Category == "Sundry Debtors").ToString("N0", IndianCulture), $"Closing Dr {Money(rows.Where(row => row.Category == "Sundry Debtors").Sum(row => Math.Max(0m, row.ClosingBalance ?? 0m)))}"),
                new AuditMetricTile("Creditors", rows.Count(row => row.Category == "Sundry Creditors").ToString("N0", IndianCulture), $"Closing Cr {Money(rows.Where(row => row.Category == "Sundry Creditors").Sum(row => Math.Max(0m, -(row.ClosingBalance ?? 0m))))}"),
                new AuditMetricTile("Zero Balance", rows.Count(row => IsEffectiveZeroLedgerBalance(row.OpeningBalance, row.ClosingBalance, row.TransactionCount)).ToString("N0", IndianCulture), "Explicit zero closing or opening zero with no FY activity"),
                new AuditMetricTile("Zero Transactions", rows.Count(row => row.TransactionCount == 0).ToString("N0", IndianCulture), "No Audit-owned FY voucher activity")
            },
            "Debtors" or "Creditors" => new[]
            {
                new AuditMetricTile("Ledgers", rows.Length.ToString("N0", IndianCulture), module == "Debtors" ? "Sundry Debtors" : "Sundry Creditors"),
                new AuditMetricTile("Closing Dr", Money(rows.Sum(row => Math.Max(0m, row.ClosingBalance ?? 0m))), "Debit-nature closing"),
                new AuditMetricTile("Closing Cr", Money(rows.Sum(row => Math.Max(0m, -(row.ClosingBalance ?? 0m)))), "Credit-nature closing"),
                new AuditMetricTile("Transactions", rows.Sum(row => row.TransactionCount).ToString("N0", IndianCulture), "FY ledger voucher participation"),
                new AuditMetricTile("Zero Balance", rows.Count(row => IsEffectiveZeroLedgerBalance(row.OpeningBalance, row.ClosingBalance, row.TransactionCount)).ToString("N0", IndianCulture), "Explicit zero closing or opening zero with no FY activity"),
                new AuditMetricTile("Zero Transactions", rows.Count(row => row.TransactionCount == 0).ToString("N0", IndianCulture), "No FY activity")
            },
            _ => Array.Empty<AuditMetricTile>()
        };
    }

    private static AuditMetricTile[] BuildBankMetricTiles(
        AuditRecordRow[] rows,
        AuditBankSummaryRow[] summaries,
        int exceptions,
        decimal debit,
        decimal credit)
    {
        var accounts = rows
            .Where(row => row.Category is "Bank" or "OD" or "Loans")
            .Select(row => row.PartyOrLedger)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var booksRows = rows.Where(row => row.Source.Equals("Explicit Audit Tally Link", StringComparison.OrdinalIgnoreCase)).ToArray();
        var booksDebit = booksRows.Sum(row => row.Debit);
        var booksCredit = booksRows.Sum(row => row.Credit);
        var cashWithdrawn = rows.Where(row => row.PaymentReceiptType.Contains("Cash Withdrawal", StringComparison.OrdinalIgnoreCase)).Sum(row => row.Amount);
        var cashDeposited = rows.Where(row => row.PaymentReceiptType.Contains("Cash Deposit", StringComparison.OrdinalIgnoreCase)).Sum(row => row.Amount);
        var statementEligible = rows
            .Where(row => row.Category is "Bank" or "OD")
            .Select(row => row.PartyOrLedger)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var statementAccounts = summaries
            .Where(summary => !string.IsNullOrWhiteSpace(summary.Account))
            .Select(summary => summary.Account.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var statementsPending = Math.Max(0, statementEligible - statementAccounts);
        return new[]
        {
            new AuditMetricTile("Accounts", accounts.ToString("N0", IndianCulture), "Bank + OD + Secured Loans"),
            new AuditMetricTile("Bank", rows.Where(row => row.Category == "Bank").Select(row => row.PartyOrLedger).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString("N0", IndianCulture), "Bank Accounts group"),
            new AuditMetricTile("OD", rows.Where(row => row.Category == "OD").Select(row => row.PartyOrLedger).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString("N0", IndianCulture), "Bank OD group"),
            new AuditMetricTile("Loans", rows.Where(row => row.Category == "Loans").Select(row => row.PartyOrLedger).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString("N0", IndianCulture), "Secured Loans group"),
            new AuditMetricTile("Books Debit", booksDebit.ToString("N2", IndianCulture), "Explicit linked Tally authority"),
            new AuditMetricTile("Books Credit", booksCredit.ToString("N2", IndianCulture), "Explicit linked Tally authority"),
            new AuditMetricTile("Cash Withdrawn", cashWithdrawn.ToString("N2", IndianCulture), "Bank → Cash movement"),
            new AuditMetricTile("Cash Deposited", cashDeposited.ToString("N2", IndianCulture), "Cash → Bank movement"),
            new AuditMetricTile("Statements Pending", statementsPending.ToString("N0", IndianCulture), "Bank / OD accounts awaiting evidence"),
            new AuditMetricTile("Alerts", exceptions.ToString("N0", IndianCulture), "Differences / missing / review items")
        };
    }

    private void OpenDashboardCard(object? parameter)
    {
        if (parameter is not AuditDashboardCard card || !card.IsClickable) return;
        AuditNavigationItem? target = card.TargetModuleKey switch
        {
            "Settings" => _settingsModule,
            "Clients" => _clientDirectoryModule,
            _ => Modules.FirstOrDefault(module => module.Key.Equals(card.TargetModuleKey, StringComparison.OrdinalIgnoreCase))
        };
        if (target is not null) SelectedModule = target;
    }

    private void OpenAuditStatistic(object? parameter)
    {
        if (parameter is not AuditStatisticRow statistic || statistic.Count <= 0) return;
        var targetKey = statistic.Name switch
        {
            "Sundry Debtors" => "Debtors",
            "Sundry Creditors" => "Creditors",
            "Balance Sheet Ledgers" => "BalanceSheet",
            "P&L Ledgers" => "ProfitLoss",
            "Sales" or "Credit Notes" => "Sales",
            "Purchase" or "Debit Notes" => "Purchase",
            "Journal" => "Journal",
            "Audit Records" => "Sources",
            _ => "Tally"
        };
        var target = targetKey switch
        {
            "Sources" => Modules.FirstOrDefault(module => module.Key.Equals("Sources", StringComparison.OrdinalIgnoreCase)),
            _ => Modules.FirstOrDefault(module => module.Key.Equals(targetKey, StringComparison.OrdinalIgnoreCase))
        };
        if (target is not null) SelectedModule = target;
    }

    private void OpenDebtorCreditorLedger(object? parameter)
    {
        if ((!IsDebtors && !IsCreditors) || parameter is not AuditRecordRow ledger || SelectedAuditClient is null) return;
        var ledgerName = ledger.PartyOrLedger.Trim();
        if (ledgerName.Length == 0 || string.IsNullOrWhiteSpace(SelectedAuditClient.LinkedTallyCompanyKey)) return;
        var cachedRows = _debtorCreditorVoucherCache
            .Where(row => row.PartyOrLedger.Equals(ledgerName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Date ?? DateTime.MinValue)
            .ThenBy(row => row.Reference, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        DebtorCreditorLedgerTransactions.ReplaceWith(cachedRows);
        _debtorCreditorSettingsVisible = false;
        _debtorCreditorLedgerDrilldownVisible = true;
        _debtorCreditorLedgerDrilldownTitle = $"{ledgerName} · Voucher Drill-down";
        RaiseDebtorCreditorPresentationState();
    }

    private AuditRecordRow[] BuildDebtorCreditorVoucherCache(AuditClientProfile client, IEnumerable<string> ledgerNames)
    {
        var targets = ledgerNames.Select(name => (name ?? string.Empty).Trim())
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0 || string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)) return [];
        EnsureLinkedTallyEvidenceInitialized();
        var (from, to) = AdvancedClosingEntryPlanner.FinancialYearRange(client.FinancialYear);
        var vouchers = _tallyRepository.GetVoucherIndexRange(client.LinkedTallyCompanyKey, client.FinancialYear, from, to);
        var xmlByMasterId = _tallyRepository.GetVoucherXmlAuthorityByMasterIds(client.LinkedTallyCompanyKey, client.FinancialYear, vouchers.Select(voucher => voucher.MasterId))
            .Where(snapshot => snapshot.MasterId.Trim().Length > 0)
            .GroupBy(snapshot => snapshot.MasterId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var emptyGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<AuditRecordRow>();

        foreach (var voucher in vouchers)
        {
            var postings = new List<(string Ledger, bool IsDebit, decimal Amount)>();
            foreach (var posting in voucher.LedgerPostings)
            {
                var name = (posting.LedgerName ?? string.Empty).Trim();
                if (name.Length > 0) postings.Add((name, posting.IsDebit, Math.Abs(posting.Amount)));
            }
            if (postings.Count == 0)
            {
                var masterId = (voucher.MasterId ?? string.Empty).Trim();
                if (masterId.Length > 0 && xmlByMasterId.TryGetValue(masterId, out var snapshot))
                {
                    foreach (var line in ExtractTallySourceLedgerLines(snapshot.OriginalXml, emptyGroups, emptyGroups))
                    {
                        var name = (line.Ledger ?? string.Empty).Trim();
                        if (name.Length == 0) continue;
                        postings.Add((name, line.Direction.Equals("Dr", StringComparison.OrdinalIgnoreCase), Math.Abs(line.Amount)));
                    }
                }
            }

            var allNames = postings.Select(p => p.Ledger)
                .Concat(SplitLedgerNames(voucher.DebitLedgers))
                .Concat(SplitLedgerNames(voucher.CreditLedgers))
                .Append((voucher.PartyLedger ?? string.Empty).Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var matchingTargets = allNames.Where(targets.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var ledgerName in matchingTargets)
            {
                var debit = postings.Where(p => p.Ledger.Equals(ledgerName, StringComparison.OrdinalIgnoreCase) && p.IsDebit).Sum(p => p.Amount);
                var credit = postings.Where(p => p.Ledger.Equals(ledgerName, StringComparison.OrdinalIgnoreCase) && !p.IsDebit).Sum(p => p.Amount);
                var counterparties = allNames.Where(name => !name.Equals(ledgerName, StringComparison.OrdinalIgnoreCase)).ToArray();
                var reference = (voucher.VoucherNumber ?? string.Empty).Trim();
                if (reference.Length == 0) reference = (voucher.Reference ?? string.Empty).Trim();
                rows.Add(new AuditRecordRow
                {
                    Date = voucher.VoucherDate,
                    Reference = reference,
                    VoucherType = (voucher.VoucherType ?? string.Empty).Trim(),
                    PartyOrLedger = ledgerName,
                    Counterparty = string.Join(" | ", counterparties),
                    Debit = debit,
                    Credit = credit,
                    Amount = debit + credit,
                    OriginalNarration = (voucher.Narration ?? string.Empty).Trim(),
                    Details = (voucher.Narration ?? string.Empty).Trim(),
                    AuthorityId = (voucher.MasterId ?? string.Empty).Trim().Length > 0 ? $"MID {(voucher.MasterId ?? string.Empty).Trim()}" : (voucher.Guid ?? string.Empty).Trim(),
                    EvidenceStatus = postings.Any(p => p.Ledger.Equals(ledgerName, StringComparison.OrdinalIgnoreCase)) ? "Prepared Audit cache · exact Tally ledger posting" : "Prepared Audit cache · ledger participation",
                    Source = "Prepared Audit projection · FY voucher authority"
                });
            }
        }
        return rows.OrderBy(row => row.PartyOrLedger, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Date ?? DateTime.MinValue)
            .ThenBy(row => row.Reference, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void CloseDebtorCreditorLedger()
    {
        _debtorCreditorLedgerDrilldownVisible = false;
        DebtorCreditorLedgerTransactions.Clear();
        RaiseDebtorCreditorPresentationState();
    }

    private string DebtorCreditorSettingKey(string name)
    {
        var client = SelectedAuditClient;
        return client is null ? string.Empty : $"DebtorCreditor:{client.Id}:{client.FinancialYear.Trim().ToUpperInvariant()}:{name}";
    }

    private void SaveDebtorCreditorSetting(string name, bool value) => SaveDebtorCreditorSetting(name, value ? "Yes" : "No");

    private void SaveDebtorCreditorSetting(string name, string value)
    {
        if (_loadingDcSettings) return;
        var key = DebtorCreditorSettingKey(name);
        if (key.Length > 0) _auditRepository.SaveWorkspaceSetting(key, value);
    }

    private void LoadDebtorCreditorSettings()
    {
        if (SelectedAuditClient is null) return;
        _loadingDcSettings = true;
        try
        {
            bool ReadBool(string name, bool fallback) =>
                _auditRepository.GetWorkspaceSetting(DebtorCreditorSettingKey(name), fallback ? "Yes" : "No").Equals("Yes", StringComparison.OrdinalIgnoreCase);
            string ReadText(string name, string fallback) =>
                _auditRepository.GetWorkspaceSetting(DebtorCreditorSettingKey(name), fallback);
            DebtorCreditorShowOpeningBalance = ReadBool("ShowOpeningBalance", true);
            DebtorCreditorShowTransactions = ReadBool("ShowTransactions", true);
            DebtorCreditorNetTransactionsOnly = ReadBool("NetTransactionsOnly", false);
            DebtorCreditorShowClosingBalance = ReadBool("ShowClosingBalance", true);
            DebtorCreditorShowPercentages = ReadBool("ShowPercentages", false);
            DebtorCreditorPercentageType = ReadText("PercentageType", "Of Totals");
            DebtorCreditorGroupingType = ReadText("GroupingType", "Ledger-wise");
            DebtorCreditorReportFormat = ReadText("ReportFormat", "Detailed");
            DebtorCreditorExpandDetailed = ReadBool("ExpandDetailed", false);
            DebtorCreditorDisplayNameMode = ReadText("DisplayNameMode", "Name Only");
            DebtorCreditorShowBaseCurrency = ReadBool("ShowBaseCurrency", false);
            DebtorCreditorStripeView = ReadBool("StripeView", false);
        }
        finally
        {
            _loadingDcSettings = false;
        }
        ApplyDebtorCreditorPercentages();
    }

    private void ApplyDebtorCreditorDisplayNames()
    {
        foreach (var row in Rows)
            row.DisplayPartyOrLedger = DebtorCreditorDisplayNameMode.Equals("Name + GSTIN", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(row.Gstin)
                ? $"{row.PartyOrLedger} · {row.Gstin}"
                : row.PartyOrLedger;
        OnPropertyChanged(nameof(VisibleRows));
    }

    private void ApplyDebtorCreditorPercentages()
    {
        if (!IsDebtors && !IsCreditors) return;
        var rows = VisibleRows.ToArray();
        var totalDebit = rows.Sum(row => Math.Abs(row.Debit));
        var totalCredit = rows.Sum(row => Math.Abs(row.Credit));
        var totalClosing = rows.Sum(row => Math.Abs(row.ClosingBalance ?? 0m));
        var totalMovement = totalDebit + totalCredit;
        foreach (var row in Rows)
        {
            decimal numerator;
            decimal denominator;
            switch (DebtorCreditorPercentageType)
            {
                case "Of Debit": numerator = Math.Abs(row.Debit); denominator = totalDebit; break;
                case "Of Credit": numerator = Math.Abs(row.Credit); denominator = totalCredit; break;
                case "Of Closing Balance": numerator = Math.Abs(row.ClosingBalance ?? 0m); denominator = totalClosing; break;
                default: numerator = Math.Abs(row.Debit) + Math.Abs(row.Credit); denominator = totalMovement; break;
            }
            row.PercentageText = denominator <= 0m ? string.Empty : (numerator * 100m / denominator).ToString("N2", IndianCulture) + "%";
        }
        OnPropertyChanged(nameof(VisibleRows));
    }

    private void OpenDebtorCreditorSettings()
    {
        if (!IsDebtors && !IsCreditors) return;
        _debtorCreditorLedgerDrilldownVisible = false;
        _debtorCreditorSettingsVisible = true;
        DebtorCreditorLedgerTransactions.Clear();
        RaiseDebtorCreditorPresentationState();
    }

    private void CloseDebtorCreditorSettings()
    {
        _debtorCreditorSettingsVisible = false;
        RaiseDebtorCreditorPresentationState();
    }

    private void ShowDebtorCreditorInfo()
    {
        if (!IsDebtors && !IsCreditors) return;
        MessageBox.Show(FindAuditOwner(),
            "Debtors / Creditors review\n\nDouble-click a Ledger to open its FY voucher drill-down.\nFilter cycles through Show All, Hide 0 Balance and Hide 0 Transactions.\nF12 opens this module's Settings page. Ctrl+S remains the global Save Current Page shortcut.",
            "Debtors / Creditors", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RaiseDebtorCreditorPresentationState()
    {
        OnPropertyChanged(nameof(IsDebtorCreditorLedgerListVisible));
        OnPropertyChanged(nameof(IsDebtorCreditorSettingsVisible));
        OnPropertyChanged(nameof(IsDebtorCreditorLedgerDrilldownVisible));
        OnPropertyChanged(nameof(DebtorCreditorLedgerDrilldownTitle));
        CloseDebtorCreditorLedgerCommand.RaiseCanExecuteChanged();
        OpenDebtorCreditorSettingsCommand.RaiseCanExecuteChanged();
        CloseDebtorCreditorSettingsCommand.RaiseCanExecuteChanged();
        ShowDebtorCreditorInfoCommand.RaiseCanExecuteChanged();
    }

    private void SelectModuleTab(object? parameter)
    {
        var key = parameter?.ToString()?.Trim() ?? string.Empty;
        if (key.Length == 0) return;
        if (IsCommerceModule)
        {
            ResetCommerceDrilldown();
            CommerceDrilldownPartySummaries.Clear();
            CommercePartyDetailTiles.Clear();
            SelectedCommerceParty = null;
            RaiseCommerceTabVisibility();
        }
        SelectedModuleTab = ModuleTabs.FirstOrDefault(tab => tab.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private void OpenCommerceGroup(object? parameter)
    {
        if (!IsCommerceModule || parameter is not AuditCommerceGroupSummary summary) return;
        _commerceSelectedGroup = summary.Group;
        _commerceSelectedParty = string.Empty;
        _commerceSelectedRate = string.Empty;
        _commerceSelectedItem = string.Empty;
        _commerceSelectedMonthOrder = 0;
        _commerceSelectedSupplyType = string.Empty;
        _commerceSelectedVoucherType = string.Empty;
        _commerceDrilldownStage = "GroupParties";
        _commerceDrilldownTitle = $"{summary.Group} · {CommercePartyNoun} summary";
        CommerceDrilldownPartySummaries.ReplaceWith(CommercePartySummariesForCurrentFilter());
        SelectedCommerceParty = null;
        RaiseCommerceDrilldownVisibility();
    }

    private void OpenCommerceVoucherType(object? parameter)
    {
        if (!IsCommerceModule || parameter is not AuditCommerceGroupSummary summary) return;
        _commerceSelectedGroup = string.Empty;
        _commerceSelectedParty = string.Empty;
        _commerceSelectedRate = string.Empty;
        _commerceSelectedItem = string.Empty;
        _commerceSelectedMonthOrder = 0;
        _commerceSelectedSupplyType = string.Empty;
        _commerceSelectedVoucherType = summary.Group.Trim();
        _commerceDrilldownStage = "GroupParties";
        _commerceDrilldownTitle = $"{summary.Group} · {CommercePartyNoun} summary";
        CommerceDrilldownPartySummaries.ReplaceWith(CommercePartySummariesForCurrentFilter());
        SelectedCommerceParty = null;
        RaiseCommerceDrilldownVisibility();
    }

    private void OpenCommerceParty(object? parameter)
    {
        if (!IsCommerceModule || parameter is not AuditCommercePartySummary summary) return;
        _commerceSelectedParty = summary.Party;
        SelectedCommerceParty = summary;
        _commerceDrilldownStage = "Transactions";
        _commerceDrilldownTitle = $"{summary.Party} · exact {(IsSales ? "sales" : "purchase")} transactions";
        RaiseCommerceDrilldownVisibility();
        ApplyRowFilter();
    }

    private void OpenCommerceRate(object? parameter)
    {
        if (!IsCommerceModule || parameter is not AuditCommerceGstRateSummary summary) return;
        _commerceSelectedGroup = string.Empty;
        _commerceSelectedParty = string.Empty;
        _commerceSelectedItem = string.Empty;
        _commerceSelectedMonthOrder = 0;
        _commerceSelectedSupplyType = string.Empty;
        _commerceSelectedVoucherType = string.Empty;
        _commerceSelectedRate = summary.RateLabel;
        _commerceDrilldownStage = "GroupParties";
        _commerceDrilldownTitle = $"GST rate {summary.RateLabel} · {CommercePartyNoun} summary";
        CommerceDrilldownPartySummaries.ReplaceWith(CommercePartySummariesForCurrentFilter());
        SelectedCommerceParty = null;
        RaiseCommerceDrilldownVisibility();
    }

    private void OpenCommerceItem(object? parameter)
    {
        if (!IsCommerceModule || parameter is not AuditCommerceItemSummary summary) return;
        _commerceSelectedGroup = string.Empty;
        _commerceSelectedParty = string.Empty;
        _commerceSelectedRate = string.Empty;
        _commerceSelectedMonthOrder = 0;
        _commerceSelectedSupplyType = string.Empty;
        _commerceSelectedVoucherType = string.Empty;
        _commerceSelectedItem = summary.Item;
        _commerceDrilldownStage = "GroupParties";
        _commerceDrilldownTitle = $"{summary.Item} · {CommercePartyNoun} summary";
        CommerceDrilldownPartySummaries.ReplaceWith(CommercePartySummariesForCurrentFilter());
        SelectedCommerceParty = null;
        RaiseCommerceDrilldownVisibility();
    }

    private void OpenCommerceMonth(object? parameter)
    {
        if (!IsCommerceModule || parameter is not AuditCommerceMonthSummary summary) return;
        _commerceSelectedGroup = string.Empty;
        _commerceSelectedParty = string.Empty;
        _commerceSelectedRate = string.Empty;
        _commerceSelectedItem = string.Empty;
        _commerceSelectedSupplyType = string.Empty;
        _commerceSelectedVoucherType = string.Empty;
        _commerceSelectedMonthOrder = summary.MonthOrder;
        _commerceDrilldownStage = "GroupParties";
        _commerceDrilldownTitle = $"{summary.Month} · {CommercePartyNoun} summary";
        CommerceDrilldownPartySummaries.ReplaceWith(CommercePartySummariesForCurrentFilter());
        SelectedCommerceParty = null;
        RaiseCommerceDrilldownVisibility();
    }

    private void OpenCommerceSupply(object? parameter)
    {
        if (!IsSales || parameter is not AuditCommerceSupplySummary summary) return;
        _commerceSelectedGroup = string.Empty;
        _commerceSelectedParty = string.Empty;
        _commerceSelectedRate = string.Empty;
        _commerceSelectedItem = string.Empty;
        _commerceSelectedMonthOrder = 0;
        _commerceSelectedVoucherType = string.Empty;
        _commerceSelectedSupplyType = summary.SupplyType;
        _commerceDrilldownStage = "GroupParties";
        _commerceDrilldownTitle = $"{summary.SupplyType} Sales · Customer summary";
        CommerceDrilldownPartySummaries.ReplaceWith(CommercePartySummariesForCurrentFilter());
        SelectedCommerceParty = null;
        RaiseCommerceDrilldownVisibility();
    }

    private AuditCommercePartySummary[] CommercePartySummariesForCurrentFilter()
    {
        var rows = GetPreferredCommerceRows(SelectedModule?.Key ?? string.Empty);
        if (_commerceSelectedGroup.Length > 0)
        {
            var client = SelectedAuditClient;
            var ledgerParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var groupParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (client is not null && !string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey))
            {
                foreach (var pair in _auditRepository.GetTallyGroupParents(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey)) groupParents[pair.Key] = pair.Value;
                foreach (var ledger in _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey))
                {
                    if (!string.IsNullOrWhiteSpace(ledger.DisplayName)) ledgerParents[ledger.DisplayName.Trim()] = ledger.Parent.Trim();
                    if (!string.IsNullOrWhiteSpace(ledger.ExactName)) ledgerParents[ledger.ExactName.Trim()] = ledger.Parent.Trim();
                }
            }
            rows = rows.Where(row =>
            {
                var raw = string.IsNullOrWhiteSpace(row.BusinessGroup) ? row.Category : row.BusinessGroup.Trim();
                var resolved = ResolveLedgerGroup(raw, ledgerParents, groupParents);
                var group = resolved.Length > 0 ? resolved : raw;
                return group.Equals(_commerceSelectedGroup, StringComparison.OrdinalIgnoreCase);
            }).ToArray();
        }
        if (_commerceSelectedRate.Length > 0)
        {
            rows = rows.Where(row =>
            {
                if (!row.GstRateKnown) return false;
                var rateLabel = row.GstRate.ToString("0.##", IndianCulture) + "%";
                return rateLabel.Equals(_commerceSelectedRate, StringComparison.OrdinalIgnoreCase);
            }).ToArray();
        }
        if (_commerceSelectedItem.Length > 0)
            rows = rows.Where(row => row.ItemName.Equals(_commerceSelectedItem, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (_commerceSelectedMonthOrder > 0)
            rows = rows.Where(row => CommerceMonthOrder(row.Date) == _commerceSelectedMonthOrder).ToArray();
        if (_commerceSelectedSupplyType.Length > 0)
            rows = rows.Where(row => CommerceSupplyType(row).Equals(_commerceSelectedSupplyType, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (_commerceSelectedVoucherType.Length > 0)
            rows = rows.Where(row => row.VoucherType.Trim().Equals(_commerceSelectedVoucherType, StringComparison.OrdinalIgnoreCase)).ToArray();

        var balances = BuildCommercePartyBalanceLookup();
        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.PartyOrLedger))
            .GroupBy(row => row.PartyOrLedger.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                balances.TryGetValue(group.Key, out var balance);
                return new AuditCommercePartySummary
                {
                    Party = group.Key,
                    Gstin = group.Select(row => row.Gstin).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
                    Count = group.Count(),
                    GrossAmount = group.Sum(row => row.Amount),
                    AdjustmentAmount = group.Where(row => IsCommerceAdjustment(SelectedModule?.Key ?? string.Empty, row)).Sum(row => row.Amount),
                    TaxableValue = group.Sum(row => row.TaxableValue),
                    GstAmount = group.Sum(row => row.GstAmount),
                    OpeningBalance = balance.Opening,
                    ClosingBalance = balance.Closing,
                    AlertCount = group.Count(IsExceptionRow)
                };
            })
            .OrderByDescending(row => row.GrossAmount)
            .ToArray();
    }

    private Dictionary<string, (decimal? Opening, decimal? Closing)> BuildCommercePartyBalanceLookup()
    {
        var result = new Dictionary<string, (decimal? Opening, decimal? Closing)>(StringComparer.OrdinalIgnoreCase);
        var client = SelectedAuditClient;
        if (client is null || string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)) return result;
        foreach (var ledger in _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey))
        {
            var balance = (ledger.OpeningBalance, ledger.ClosingBalance);
            if (!string.IsNullOrWhiteSpace(ledger.DisplayName)) result[ledger.DisplayName.Trim()] = balance;
            if (!string.IsNullOrWhiteSpace(ledger.ExactName)) result[ledger.ExactName.Trim()] = balance;
        }
        return result;
    }

    private void RebuildCommercePartyDetail(AuditCommercePartySummary summary)
    {
        CommercePartyDetailTiles.Clear();
        var module = SelectedModule?.Key ?? string.Empty;
        var rows = GetPreferredCommerceRows(module)
            .Where(row => row.PartyOrLedger.Equals(summary.Party, StringComparison.OrdinalIgnoreCase))
            .Where(CommerceRowMatchesCurrentDrilldown)
            .ToArray();
        var gross = rows.Where(row => !IsCommerceAdjustment(module, row)).Sum(row => row.Amount);
        var adjustments = rows.Where(row => IsCommerceAdjustment(module, row)).Sum(row => row.Amount);
        CommercePartyDetailTiles.Add(new AuditMetricTile(module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? "Sales" : "Purchases", gross.ToString("N2", IndianCulture), $"{rows.Count(row => !IsCommerceAdjustment(module, row)).ToString("N0", IndianCulture)} invoice row(s)"));
        CommercePartyDetailTiles.Add(new AuditMetricTile("Adjustments", adjustments.ToString("N2", IndianCulture), "Credit/Debit Notes and returns"));
        CommercePartyDetailTiles.Add(new AuditMetricTile("Taxable", rows.Sum(row => row.TaxableValue).ToString("N2", IndianCulture), "Taxable value"));
        CommercePartyDetailTiles.Add(new AuditMetricTile("GST", rows.Sum(row => row.GstAmount).ToString("N2", IndianCulture), "CGST + SGST + IGST + Cess + unallocated GST"));
        CommercePartyDetailTiles.Add(new AuditMetricTile("Invoices / Rows", rows.Length.ToString("N0", IndianCulture), summary.Gstin.Length > 0 ? $"GSTIN {summary.Gstin}" : "GSTIN not available"));
        CommercePartyDetailTiles.Add(new AuditMetricTile("Alerts", rows.Count(IsExceptionRow).ToString("N0", IndianCulture), "Rows requiring audit review", rows.Any(IsExceptionRow)));
    }

    private bool CommerceRowMatchesCurrentDrilldown(AuditRecordRow row)
    {
        if (_commerceSelectedGroup.Length > 0 && !CommerceRowMatchesSelectedGroup(row)) return false;
        if (_commerceSelectedRate.Length > 0)
        {
            if (!row.GstRateKnown) return false;
            var rateLabel = row.GstRate.ToString("0.##", IndianCulture) + "%";
            if (!rateLabel.Equals(_commerceSelectedRate, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (_commerceSelectedItem.Length > 0 && !row.ItemName.Equals(_commerceSelectedItem, StringComparison.OrdinalIgnoreCase)) return false;
        if (_commerceSelectedMonthOrder > 0 && CommerceMonthOrder(row.Date) != _commerceSelectedMonthOrder) return false;
        if (_commerceSelectedSupplyType.Length > 0 && !CommerceSupplyType(row).Equals(_commerceSelectedSupplyType, StringComparison.OrdinalIgnoreCase)) return false;
        if (_commerceSelectedVoucherType.Length > 0 && !row.VoucherType.Trim().Equals(_commerceSelectedVoucherType, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static int CommerceMonthOrder(DateTime? date)
    {
        if (!date.HasValue) return 0;
        return date.Value.Month >= 4 ? date.Value.Month - 3 : date.Value.Month + 9;
    }

    private bool CommerceRowMatchesSelectedGroup(AuditRecordRow row)
    {
        if (_commerceSelectedGroup.Length == 0) return true;
        var raw = string.IsNullOrWhiteSpace(row.BusinessGroup) ? row.Category : row.BusinessGroup.Trim();
        if (raw.Equals(_commerceSelectedGroup, StringComparison.OrdinalIgnoreCase)) return true;
        var client = SelectedAuditClient;
        if (client is null || string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)) return false;
        var ledgers = _auditRepository.GetTallyLedgerAuthority(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
        var groups = _auditRepository.GetTallyGroupParents(client.Id, client.FinancialYear, client.LinkedTallyCompanyKey);
        var parents = ledgers.Where(item => !string.IsNullOrWhiteSpace(item.DisplayName)).GroupBy(item => item.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().Parent.Trim(), StringComparer.OrdinalIgnoreCase);
        var resolved = ResolveLedgerGroup(raw, parents, groups);
        return resolved.Equals(_commerceSelectedGroup, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowCommercePartyTransactions()
    {
        if (!IsCommercePartyDetailVisible || SelectedCommerceParty is null) return;
        _commerceSelectedParty = SelectedCommerceParty.Party;
        _commerceDrilldownStage = "Transactions";
        _commerceDrilldownTitle = $"{SelectedCommerceParty.Party} · exact { (IsSales ? "sales" : "purchase") } transactions";
        RaiseCommerceDrilldownVisibility();
        ApplyRowFilter();
    }

    private void CloseCommerceDrilldown()
    {
        if (!IsCommerceDrilldownActive) return;
        if (_commerceDrilldownStage.Equals("Transactions", StringComparison.OrdinalIgnoreCase) && SelectedCommerceParty is not null)
        {
            _commerceDrilldownStage = "PartyDetail";
            _commerceDrilldownTitle = $"{SelectedCommerceParty.Party} · {CommercePartyNoun} audit dashboard";
        }
        else if (_commerceDrilldownStage.Equals("PartyDetail", StringComparison.OrdinalIgnoreCase) &&
                 (_commerceSelectedGroup.Length > 0 || _commerceSelectedRate.Length > 0 || _commerceSelectedItem.Length > 0 ||
                  _commerceSelectedMonthOrder > 0 || _commerceSelectedSupplyType.Length > 0 || _commerceSelectedVoucherType.Length > 0))
        {
            _commerceDrilldownStage = "GroupParties";
            _commerceDrilldownTitle = _commerceSelectedGroup.Length > 0
                ? $"{_commerceSelectedGroup} · {CommercePartyNoun} summary"
                : _commerceSelectedRate.Length > 0
                    ? $"GST rate {_commerceSelectedRate} · {CommercePartyNoun} summary"
                    : _commerceSelectedItem.Length > 0
                        ? $"{_commerceSelectedItem} · {CommercePartyNoun} summary"
                        : _commerceSelectedMonthOrder > 0
                            ? $"{CommerceMonthSummaries.FirstOrDefault(row => row.MonthOrder == _commerceSelectedMonthOrder)?.Month ?? "Month"} · {CommercePartyNoun} summary"
                            : _commerceSelectedVoucherType.Length > 0
                                ? $"{_commerceSelectedVoucherType} · {CommercePartyNoun} summary"
                                : $"{_commerceSelectedSupplyType} Sales · {CommercePartyNoun} summary";
            SelectedCommerceParty = null;
        }
        else
        {
            ResetCommerceDrilldown();
            CommerceDrilldownPartySummaries.Clear();
            CommercePartyDetailTiles.Clear();
            SelectedCommerceParty = null;
        }
        RaiseCommerceDrilldownVisibility();
        ApplyRowFilter();
    }

    private void ResetCommerceDrilldown()
    {
        _commerceDrilldownStage = string.Empty;
        _commerceSelectedGroup = string.Empty;
        _commerceSelectedParty = string.Empty;
        _commerceSelectedRate = string.Empty;
        _commerceSelectedItem = string.Empty;
        _commerceSelectedMonthOrder = 0;
        _commerceSelectedSupplyType = string.Empty;
        _commerceSelectedVoucherType = string.Empty;
        _commerceDrilldownTitle = string.Empty;
    }

    private void RaiseCommerceDrilldownVisibility()
    {
        OnPropertyChanged(nameof(IsCommerceDrilldownActive));
        OnPropertyChanged(nameof(IsCommerceDashboardVisible));
        OnPropertyChanged(nameof(IsCommerceGroupSummaryVisible));
        OnPropertyChanged(nameof(IsCommercePartyDetailVisible));
        OnPropertyChanged(nameof(IsCommerceTransactionsVisible));
        OnPropertyChanged(nameof(CommerceDrilldownTitle));
        OnPropertyChanged(nameof(IsTransactionToolsVisible));
        ShowCommercePartyTransactionsCommand.RaiseCanExecuteChanged();
        CloseCommerceDrilldownCommand.RaiseCanExecuteChanged();
    }

    private void ClearModuleFilters()
    {
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        SelectedModuleTab = ModuleTabs.FirstOrDefault(tab => tab.Key.Equals("All", StringComparison.OrdinalIgnoreCase)) ?? ModuleTabs.FirstOrDefault();
        ApplyRowFilter();
    }

    private void ApplyRowFilter()
    {
        if (VisibleRows is null) return;
        var tabKey = _selectedModuleTab?.Key ?? "All";
        var tokens = (SearchText ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 0)
            .ToArray();

        bool MatchesTab(AuditRecordRow row) => tabKey switch
        {
            "All" or "Dashboard" or "Overview" or "Accounts" or "StatementStatus" or "BooksVsStatement" or "Position" or "CashMovement" or "BankMovement" or "CashPosition" => true,
            "Payments" => IsPaymentMovement(row),
            "Receipts" => IsReceiptMovement(row),
            "Exceptions" or "Alerts" => IsExceptionRow(row),
            "Contra" or "Transfers" => row.TransactionClass.Contains("Contra", StringComparison.OrdinalIgnoreCase) || row.TransactionClass.Contains("Inter-bank", StringComparison.OrdinalIgnoreCase),
            "Statements" or "StatementStatus" => row.Source.Contains("Bank Statement", StringComparison.OrdinalIgnoreCase),
            "Books" => row.Source.Equals("Explicit Audit Tally Link", StringComparison.OrdinalIgnoreCase),
            "Statutory" => row.RuleReview.Length > 0,
            "BankCash" or "Bank ↔ Cash" => row.TransactionClass.Contains("Bank", StringComparison.OrdinalIgnoreCase) && row.TransactionClass.Contains("Cash", StringComparison.OrdinalIgnoreCase),
            "Cash Receipts" or "Cash Payments" => row.TransactionClass.Equals(tabKey, StringComparison.OrdinalIgnoreCase),
            _ => row.Category.Equals(tabKey, StringComparison.OrdinalIgnoreCase)
        };

        bool MatchesSearch(AuditRecordRow row)
        {
            if (tokens.Length == 0) return true;
            var haystack = string.Join(" | ", new[]
            {
                row.DateText, row.Reference, row.VoucherType, row.AuthorityId, row.PartyOrLedger,
                row.Counterparty, row.CounterpartyGroup, row.BusinessGroup, row.BusinessLedger, row.TaxLedgers, row.Gstin, row.ItemName, row.HsnCode, row.Unit, row.Category, row.TransactionClass, row.Details, row.OriginalNarration,
                row.OrderNumber, row.TrackingNumber, row.Godown, row.BatchName, row.CostCentre, row.SourceSection, row.PlaceOfSupply, row.ReturnPeriod, row.Irn, row.EWayBillNumber, row.InvoiceType, row.TwoBReason, row.DifferenceDetail,
                row.Status, row.ReconciliationStatus, row.ItcStatus, row.EvidenceStatus, row.SourceEvidence, row.Source,
                row.Debit.ToString("0.00"), row.Credit.ToString("0.00"), row.Amount.ToString("0.00"),
                row.TaxableValue.ToString("0.00"), row.GstAmount.ToString("0.00"), row.GstRate.ToString("0.####")
            });
            return tokens.All(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        bool MatchesLedgerVisibility(AuditRecordRow row)
        {
            if (!IsDebtors && !IsCreditors) return true;
            return _ledgerVisibilityMode switch
            {
                1 => !IsEffectiveZeroLedgerBalance(row.OpeningBalance, row.ClosingBalance, row.TransactionCount),
                2 => row.TransactionCount > 0,
                _ => true
            };
        }

        bool MatchesSelectedFinanceAccount(AuditRecordRow row) => !IsFinanceAccountDashboardModule || SelectedFinanceAccount is null || row.PartyOrLedger.Equals(SelectedFinanceAccount.Account, StringComparison.OrdinalIgnoreCase);

        bool MatchesFinanceDrilldown(AuditRecordRow row)
        {
            if (!IsFinanceTransactionsVisible || string.IsNullOrWhiteSpace(_financeDrilldownMode)) return true;
            var group = string.IsNullOrWhiteSpace(row.CounterpartyGroup) ? "Other / Unclassified" : row.CounterpartyGroup.Trim();
            return _financeDrilldownMode switch
            {
                "Payments" => IsPaymentMovement(row),
                "Receipts" => IsReceiptMovement(row),
                // Fix102 inherited compatibility proof: PaymentGroup / ReceiptGroup strict group filter remains a prerequisite.
                "PaymentGroup" => IsPaymentMovement(row) && group.Equals(_financeDrilldownGroup, StringComparison.OrdinalIgnoreCase),
                "ReceiptGroup" => IsReceiptMovement(row) && group.Equals(_financeDrilldownGroup, StringComparison.OrdinalIgnoreCase),
                "PaymentGroupParty" => IsPaymentMovement(row) && group.Equals(_financeDrilldownGroup, StringComparison.OrdinalIgnoreCase) && CounterpartyDrilldownKey(row).Equals(_financeDrilldownCounterparty, StringComparison.OrdinalIgnoreCase),
                "ReceiptGroupParty" => IsReceiptMovement(row) && group.Equals(_financeDrilldownGroup, StringComparison.OrdinalIgnoreCase) && CounterpartyDrilldownKey(row).Equals(_financeDrilldownCounterparty, StringComparison.OrdinalIgnoreCase),
                "CashWithdrawal" => IsCashWithdrawalMovement(row),
                "CashDeposit" => IsCashDepositMovement(row),
                "CashBank" => (row.Counterparty.Split(" | ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                   .Any(value => value.Equals(_financeDrilldownBank, StringComparison.OrdinalIgnoreCase)) ||
                               row.PartyOrLedger.Equals(_financeDrilldownBank, StringComparison.OrdinalIgnoreCase)) &&
                              (IsCashWithdrawalMovement(row) || IsCashDepositMovement(row)),
                _ => true
            };
        }

        bool MatchesCommerceDrilldown(AuditRecordRow row)
        {
            if (!IsCommerceTransactionsVisible) return true;
            var module = SelectedModule?.Key ?? string.Empty;
            var preferredRows = GetPreferredCommerceRows(module).ToHashSet();
            if (!preferredRows.Contains(row)) return false;
            if (_commerceSelectedParty.Length > 0 && !row.PartyOrLedger.Equals(_commerceSelectedParty, StringComparison.OrdinalIgnoreCase)) return false;
            return CommerceRowMatchesCurrentDrilldown(row);
        }

        if (IsCommerceTransactionsVisible)
        {
            VisibleRows.ReplaceWith(Rows.Where(MatchesCommerceDrilldown).Where(MatchesSearch));
        }
        else
        {
            var financeBaseRows = IsFinanceTransactionsVisible
                ? Rows.Where(row => MatchesSelectedFinanceAccount(row) && MatchesFinanceDrilldown(row))
                : Rows.Where(MatchesSelectedFinanceAccount);
            VisibleRows.ReplaceWith(IsFinanceTransactionsVisible
                ? financeBaseRows.Where(MatchesSearch)
                : financeBaseRows.Where(row => MatchesTab(row) && MatchesSearch(row) && MatchesLedgerVisibility(row)));
        }
        if (IsDebtors || IsCreditors) ApplyDebtorCreditorPercentages();
        if (SelectedRecord is not null && !VisibleRows.Contains(SelectedRecord)) SelectedRecord = null;
        var client = SelectedAuditClient;
        if (client is not null && IsStructuredAuditModule)
            Status = IsCommerceGroupSummaryVisible
                ? $"{CommerceDrilldownTitle} · {CommerceDrilldownPartySummaries.Count.ToString("N0", IndianCulture)} {CommercePartyNoun.ToLowerInvariant()}(s) · {client.ClientCode} · FY {client.FinancialYear}."
                : IsCommercePartyDetailVisible
                    ? $"{CommerceDrilldownTitle} · {client.ClientCode} · FY {client.FinancialYear}."
                : IsCommerceTransactionsVisible
                    ? $"{CommerceDrilldownTitle} · {VisibleRows.Count.ToString("N0", IndianCulture)} row(s) · {client.ClientCode} · FY {client.FinancialYear}."
                : IsFinanceGroupSummaryVisible
                ? $"{FinanceGroupSummaryTitle} · {FinanceGroupPartySummaries.Count.ToString("N0", IndianCulture)} party/ledger group(s) · {client.ClientCode} · FY {client.FinancialYear}."
                : IsFinanceAccountDashboardModule && !TransactionsVisible
                ? $"{CurrentPageTitle} dashboard ready · {FormatCount(FinanceAccounts.Count)} account(s) · {client.ClientCode} · FY {client.FinancialYear}."
                : IsBankingFinanceParent
                    ? $"Banking & Finance dashboard ready · {FinanceAccounts.Count.ToString("N0", IndianCulture)} account(s) · {client.ClientCode} · FY {client.FinancialYear}."
                    : IsFinanceTransactionsVisible
                        ? $"{FinanceTransactionDrilldownTitle} · {VisibleRows.Count.ToString("N0", IndianCulture)} matching transaction(s) · {client.ClientCode} · FY {client.FinancialYear}."
                        : $"{CurrentPageTitle} · {VisibleRows.Count.ToString("N0", IndianCulture)} visible of {Rows.Count.ToString("N0", IndianCulture)} row(s) · {client.ClientCode} · FY {client.FinancialYear}.";
    }

    private static bool IsExceptionRow(AuditRecordRow row) =>
        row.IsException || IsIssueText(row.Status) || IsIssueText(row.ReconciliationStatus) || IsIssueText(row.ItcStatus) || IsIssueText(row.EvidenceStatus);

    private void OpenRecordLookup()
    {
        if (SelectedRecord is null) return;
        var dialog = new AuditRecordLookupWindow(SelectedRecord)
        {
            Owner = FindAuditOwner()
        };
        dialog.ShowDialog();
    }

    private void ApplySnapshot(AuditSnapshot snapshot)
    {
        Rows.ReplaceWith(snapshot.Rows);
        _debtorCreditorVoucherCache = snapshot.DebtorCreditorVoucherRows ?? [];
        if (IsDebtors || IsCreditors) ApplyDebtorCreditorDisplayNames();
        OnPropertyChanged(nameof(DebtorCountText));
        OnPropertyChanged(nameof(CreditorCountText));
        OnPropertyChanged(nameof(DebtorClosingText));
        OnPropertyChanged(nameof(CreditorClosingText));
        BankSummaries.ReplaceWith(snapshot.BankSummaries);
        SourceRows.ReplaceWith(snapshot.Sources);
        DashboardCards.ReplaceWith(snapshot.DashboardCards);
        AuditMasterStatistics.ReplaceWith(snapshot.MasterStatistics);
        AuditVoucherStatistics.ReplaceWith(snapshot.VoucherStatistics);
        FinancialStatementRows.ReplaceWith(snapshot.FinancialStatementRows);
        ApplyFinancialStatementFilter();
        AuditFinancialStatements.Refresh(SelectedAuditClient, snapshot.FinancialStatementRows, snapshot.Rows);
        TallyAuthorityTiles.ReplaceWith(BuildTallyAuthorityTiles(SelectedAuditClient));
        RebuildModulePresentation(snapshot.Module);
        UpdateModuleCounts(snapshot.ModuleCounts);
        var client = SelectedAuditClient;
        SourceSummary = client is null
            ? "No Audit client selected."
            : $"Audit Client: {client.ClientName} · Independent Audit data · Tally: {TallyLinkText}";
        Status = client is null
            ? "Create or select an Audit client."
            : IsDashboard
                ? $"Dashboard ready · independent Audit authority snapshot updated · {client.ClientCode} · FY {client.FinancialYear}."
                : IsFinancialStatementModule
                    ? $"{CurrentPageTitle} ready · {FinancialStatementRows.Count(row => !row.IsGroupSummary).ToString("N0", IndianCulture)} ledger(s) across {FinancialStatementRows.Count(row => row.IsGroupSummary).ToString("N0", IndianCulture)} group(s) · {client.ClientCode} · FY {client.FinancialYear}."
                : Rows.Count == 0 && IsStructuredAuditModule && !string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)
                    ? $"{CurrentPageTitle} has no projected rows yet · linked Tally authority will auto-repair/refresh; use ↻ to force a live refresh · {client.ClientCode} · FY {client.FinancialYear}."
                    : $"{CurrentPageTitle} ready · {FormatCount(VisibleRows.Count)} visible of {FormatCount(Rows.Count)} row(s) · {client.ClientCode} · FY {client.FinancialYear}.";
    }

    private void UpdateModuleCounts(IReadOnlyDictionary<string, int> counts)
    {
        foreach (var module in Modules)
        {
            module.Count = module.Key == "Clients" ? AuditClients.Count : counts.GetValueOrDefault(module.Key);
            module.IsVisible = module.Key switch
            {
                "Dashboard" or "Sources" or "Settings" => true,
                "BankingFinance" => counts.GetValueOrDefault("BankingFinance") > 0 || counts.GetValueOrDefault("Bank") > 0 || counts.GetValueOrDefault("OD") > 0 || counts.GetValueOrDefault("Loans") > 0,
                "DebtorsCreditors" => counts.GetValueOrDefault("Debtors") > 0 || counts.GetValueOrDefault("Creditors") > 0,
                "BalanceSheet" or "ProfitLoss" => !string.IsNullOrWhiteSpace(SelectedAuditClient?.LinkedTallyCompanyKey),
                "Bank" => counts.GetValueOrDefault("Bank") > 0,
                "OD" => counts.GetValueOrDefault("OD") > 0,
                "Loans" => counts.GetValueOrDefault("Loans") > 0,
                _ => counts.GetValueOrDefault(module.Key) > 0
            };
        }

        // Fix106: if a live authority update removes the currently open data module, do not
        // leave an invisible/empty page stranded on screen. Return to Dashboard after this
        // snapshot has finished applying; Sources/Settings remain explicitly available.
        if (SelectedModule is { IsVisible: false })
        {
            var dashboard = Modules.FirstOrDefault(module => module.Key.Equals("Dashboard", StringComparison.OrdinalIgnoreCase));
            if (dashboard is not null)
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is not null)
                    _ = dispatcher.BeginInvoke(new Action(() => { if (SelectedModule is { IsVisible: false }) SelectedModule = dashboard; }), DispatcherPriority.Background);
                else
                    SelectedModule = dashboard;
            }
        }
    }

    private void LoadAuditClients(long preferredId = 0)
    {
        var clients = _auditRepository.GetClients(includeArchived: false);
        AuditClients.ReplaceWith(clients);
        _selectedAuditClient = preferredId > 0
            ? AuditClients.FirstOrDefault(x => x.Id == preferredId)
            : null;
        _auditRepository.SetCurrentClient(_selectedAuditClient?.Id ?? 0);
        OnPropertyChanged(nameof(SelectedAuditClient));
        OnPropertyChanged(nameof(CurrentClient));
        OnPropertyChanged(nameof(CurrentFinancialYear));
        OnPropertyChanged(nameof(CurrentClientDetail));
        OnPropertyChanged(nameof(HasActiveWorkspace));
        OnPropertyChanged(nameof(AccountingLinkText));
        OnPropertyChanged(nameof(TallyLinkText));
        OnPropertyChanged(nameof(TallyConnectionStatus));
        OnPropertyChanged(nameof(SelectedLiveTallyCompany));
        OnPropertyChanged(nameof(WorkspaceButtonText));
        OnPropertyChanged(nameof(CurrentPageTitle));
        OnPropertyChanged(nameof(CurrentPageSubtitle));
        RefreshLinkCommandStates();
        if (!_syncingClientSelection)
        {
            _syncingClientSelection = true;
            try { ClientManagement.Reload(_selectedAuditClient?.Id ?? 0); } finally { _syncingClientSelection = false; }
        }
    }

    private void CloseAuditClientWorkspace()
    {
        _auditRepository.SetCurrentClient(0);
        _selectedAuditClient = null;
        OnPropertyChanged(nameof(SelectedAuditClient));
        OnPropertyChanged(nameof(CurrentClient));
        OnPropertyChanged(nameof(CurrentFinancialYear));
        OnPropertyChanged(nameof(CurrentClientDetail));
        OnPropertyChanged(nameof(HasActiveWorkspace));
        OnPropertyChanged(nameof(WorkspaceButtonText));
        OnPropertyChanged(nameof(AccountingLinkText));
        OnPropertyChanged(nameof(TallyLinkText));
        OnPropertyChanged(nameof(TallyConnectionStatus));
        LiveTallyCompanies.Clear();
        SelectedLiveTallyCompany = null;
        ClientManagement.Reload(0);
        SelectedModule = _clientDirectoryModule;
        RefreshLinkCommandStates();
        Status = "Audit Client Directory ready.";
    }

    private void OpenAuditSettings()
    {
        if (SelectedAuditClient is null) return;
        SelectedModule = _settingsModule;
    }

    private void NewAuditClient()
    {
        SelectedModule = _clientDirectoryModule;
        ClientManagement.BeginNew();
    }

    private void EditAuditClient()
    {
        if (SelectedAuditClient is null) return;
        SelectedModule = _clientDirectoryModule;
        ClientManagement.BeginEdit(SelectedAuditClient);
    }


    private void OnAuditClientManagementCurrentChanged(AuditClientProfile? client)
    {
        _syncingClientSelection = true;
        try
        {
            LoadAuditClients(client?.Id ?? 0);
            _selectedAuditClient = client is null ? null : AuditClients.FirstOrDefault(x => x.Id == client.Id) ?? client;
            OnPropertyChanged(nameof(SelectedAuditClient));
            OnPropertyChanged(nameof(CurrentClient));
            OnPropertyChanged(nameof(CurrentFinancialYear));
            OnPropertyChanged(nameof(CurrentClientDetail));
        }
        finally { _syncingClientSelection = false; }
        if (client is not null)
            SelectedModule = Modules.First(x => x.Key == "Dashboard");
        else
            SelectedModule = _clientDirectoryModule;
        _ = RefreshSelectedClientAuthorityAsync();
    }

    private void OnAuditClientDirectoryChanged(object? sender, EventArgs e)
    {
        StartBackgroundPreparation(invalidate: true);
        LoadAuditClients(SelectedAuditClient?.Id ?? 0);
        UpdateModuleCounts(_auditRepository.GetModuleCounts(SelectedAuditClient?.Id ?? 0, SelectedAuditClient?.FinancialYear ?? string.Empty));
    }

    private void ArchiveAuditClient()
    {
        if (SelectedAuditClient is null) return;
        var owner = FindAuditOwner();
        var response = MessageBox.Show(owner,
            $"Archive Audit client '{SelectedAuditClient.ClientName}' for FY {SelectedAuditClient.FinancialYear}?\n\nThis affects only the Audit client directory. The Accounting client is not changed.",
            "Archive Audit Client", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (response != MessageBoxResult.Yes) return;
        _auditRepository.ArchiveClient(SelectedAuditClient.Id, true);
        LoadAuditClients();
        SelectedModule = _clientDirectoryModule;
        _ = RefreshAsync(forceAll: true);
    }

    private void LinkAccountingClient()
    {
        if (SelectedAuditClient is null) return;
        _accountingClientRepository.Initialize(); // explicit Link action opens Accounting identity authority
        var options = _auditRepository.GetAccountingClientOptions()
            .Select(x => new AuditLinkSelectionItem
            {
                Name = x.ClientName,
                Code = x.ClientCode,
                FinancialYear = x.FinancialYear,
                Gstin = x.Gstin,
                Value = x
            }).ToArray();
        var owner = FindAuditOwner();
        var dialog = new AuditLinkSelectionWindow(
            "Link Accounting Client",
            "This is an explicit Audit link. No Accounting client or Accounting module data is shared until you select and link one here.",
            options) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.SelectedItem?.Value is not AuditAccountingClientOption selected) return;

        try
        {
            var updated = SelectedAuditClient.Clone();
            var accountingTallyLink = _tallyRepository.GetLinkForClient(selected.Id);
            var auditKey = (updated.LinkedTallyCompanyKey ?? string.Empty).Trim();

            if (accountingTallyLink is not null && auditKey.Length > 0 &&
                !accountingTallyLink.CompanyKey.Equals(auditKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"This Audit client is linked to {updated.LinkedTallyCompanyName}, while the selected Accounting client is linked to {accountingTallyLink.CompanyName}. Remove one Tally link before linking these clients.");

            updated.LinkedAccountingClientId = selected.Id;
            if (accountingTallyLink is not null && auditKey.Length == 0)
            {
                // Fix110 R1: linking the same business relationship adopts the existing
                // Accounting CompanyKey into Audit instead of leaving two independent links.
                updated.LinkedTallyCompanyKey = accountingTallyLink.CompanyKey;
                updated.LinkedTallyCompanyName = accountingTallyLink.CompanyName;
            }

            // Save the explicit Audit -> Accounting relationship before asking the Accounting
            // repository to adopt an Audit-owned CompanyKey. The shared-ownership validator
            // then sees the two clients as the same business instead of a conflict.
            var saved = _auditRepository.SaveClient(updated);
            if (accountingTallyLink is null && auditKey.Length > 0)
            {
                var live = LiveTallyCompanies.FirstOrDefault(company =>
                    company.CompanyKey.Equals(auditKey, StringComparison.OrdinalIgnoreCase));
                _tallyRepository.ConfirmLink(selected.Id, live ?? new TallyCompanyInfo
                {
                    CompanyKey = auditKey,
                    Name = updated.LinkedTallyCompanyName,
                    Gstin = updated.Gstin,
                    FinancialYear = selected.FinancialYear
                });
                saved = _auditRepository.GetClient(saved.Id) ?? saved;
            }
            LoadAuditClients(saved.Id);
            _ = RefreshAsync(forceAll: true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(owner, exception.Message, "Link Accounting Client", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearAccountingLink()
    {
        if (SelectedAuditClient?.LinkedAccountingClientId is not > 0) return;
        var owner = FindAuditOwner();
        if (MessageBox.Show(owner, "Remove the explicit Accounting link from this Audit client? Audit-owned data is preserved. If the Tally CompanyKey is shared through that Accounting client, the Audit-side Tally link will also be cleared so the same CompanyKey cannot remain owned by two unrelated clients.", "Remove Accounting Link", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var updated = SelectedAuditClient.Clone();
        if (updated.LinkedAccountingClientId is not long accountingId || accountingId <= 0) return;
        var accountingTallyLink = _tallyRepository.GetLinkForClient(accountingId);
        if (accountingTallyLink is not null &&
            updated.LinkedTallyCompanyKey.Equals(accountingTallyLink.CompanyKey, StringComparison.OrdinalIgnoreCase))
        {
            _auditRepository.DeleteTallySyncState(updated.Id, updated.FinancialYear, updated.LinkedTallyCompanyKey);
            updated.LinkedTallyCompanyKey = string.Empty;
            updated.LinkedTallyCompanyName = string.Empty;
        }
        updated.LinkedAccountingClientId = null;
        var saved = _auditRepository.SaveClient(updated);
        LoadAuditClients(saved.Id);
        _ = RefreshAsync(forceAll: true);
    }

    private async void OnAuditTallyHeartbeatTick(object? sender, EventArgs e)
    {
        if (_disposed || !_presentationActive || Interlocked.Exchange(ref _auditTallyHeartbeatRunning, 1) != 0) return;
        try
        {
            var client = SelectedAuditClient?.Clone();
            if (client is null || string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)) return;

            // Fix123: ERP 9 Audit keeps the normal authority path too. The five-second
            // scheduler may skip when another authority operation owns the zero-wait gate,
            // but it never disables company discovery, ALTERID/Voucher refresh or Master
            // refresh merely because the runtime is ERP 9.
            if (!await _tallyAuthorityRefreshGate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                var companies = await _auditTallyLinkService.DiscoverOpenCompaniesAsync().ConfigureAwait(false);
                var company = FindLinkedLiveCompany(client, companies);
                await DispatchAsync(() =>
                {
                    if (companies.Count > 0) LiveTallyCompanies.ReplaceWith(companies);
                    if (company is not null) SelectedLiveTallyCompany = company;
                }).ConfigureAwait(false);
                if (company is null)
                {
                    await DispatchAsync(() => TallyDiscoveryStatus = "Linked · Tally company not currently open · heartbeat will retry").ConfigureAwait(false);
                    return;
                }

                if (!company.CompanyKey.Equals(client.LinkedTallyCompanyKey, StringComparison.OrdinalIgnoreCase))
                {
                    var updated = client.Clone();
                    updated.LinkedTallyCompanyKey = company.CompanyKey;
                    updated.LinkedTallyCompanyName = company.Name;
                    var saved = _auditRepository.SaveClient(updated);
                    client = saved.Clone();
                    await DispatchAsync(() => LoadAuditClients(saved.Id)).ConfigureAwait(false);
                }

                var pulse = await _auditTallyLinkService.PulseAuthorityAsync(client.Id, company, client.FinancialYear).ConfigureAwait(false);
                await DispatchAsync(() =>
                {
                    TallyDiscoveryStatus = pulse.Status + $" · checked {DateTime.Now:HH:mm:ss}";
                    if (pulse.AuthorityChanged)
                        Status = pulse.DeltaRefresh
                            ? $"Tally live update · {FormatCount(pulse.ChangedVoucherCount)} changed FY voucher(s) · ALTERID {FormatCount(pulse.AlterId)}."
                            : $"Tally live authority refreshed · ALTERID {FormatCount(pulse.AlterId)}.";
                }).ConfigureAwait(false);
                if (pulse.AuthorityChanged) await RefreshAsync(forceAll: true).ConfigureAwait(false);
            }
            finally
            {
                _tallyAuthorityRefreshGate.Release();
            }
        }
        catch (Exception exception)
        {
            await DispatchAsync(() => TallyDiscoveryStatus = "Linked · heartbeat retry · " + exception.Message).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _auditTallyHeartbeatRunning, 0);
        }
    }

    private async Task RefreshLiveTallyCompaniesAsync()
    {
        var client = SelectedAuditClient?.Clone();
        if (client is null) return;
        try
        {
            TallyDiscoveryStatus = "Checking Tally…";
            var companies = await _auditTallyLinkService.DiscoverOpenCompaniesAsync();
            await DispatchAsync(() =>
            {
                LiveTallyCompanies.ReplaceWith(companies);
                SelectedLiveTallyCompany = FindLinkedLiveCompany(client, companies) ?? companies.FirstOrDefault();
                TallyDiscoveryStatus = companies.Count == 0
                    ? "Tally offline / no company open"
                    : string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey)
                        ? $"Tally server running · this Audit client not linked · {FormatCount(companies.Count)} compan{(companies.Count == 1 ? "y" : "ies")} open"
                        : $"Tally server running · linked CompanyKey retained · {FormatCount(companies.Count)} compan{(companies.Count == 1 ? "y" : "ies")} open";
            });
        }
        catch (Exception exception)
        {
            await DispatchAsync(() =>
            {
                LiveTallyCompanies.Clear();
                SelectedLiveTallyCompany = null;
                TallyDiscoveryStatus = "Tally not responding";
                Status = "Tally discovery failed: " + exception.Message;
            });
        }
    }

    private async Task RefreshLinkedTallyAuthorityAsync()
    {
        var client = SelectedAuditClient?.Clone();
        if (client is null) return;
        if (string.IsNullOrWhiteSpace(client.LinkedTallyCompanyKey))
        {
            await RefreshLiveTallyCompaniesAsync().ConfigureAwait(false);
            return;
        }
        await EnsureExistingTallyAuthorityAsync(forceLiveRefresh: true).ConfigureAwait(false);
        await RefreshAsync(forceAll: true).ConfigureAwait(false);
    }

    private async Task LinkTallyCompanyAsync()
    {
        var client = SelectedAuditClient?.Clone();
        if (client is null) return;
        if (LiveTallyCompanies.Count == 0)
            await RefreshLiveTallyCompaniesAsync();
        if (LiveTallyCompanies.Count == 0)
        {
            var owner = FindAuditOwner();
            MessageBox.Show(owner,
                "No open Tally company was found. Open the required company in Tally, then press Refresh Tally and Link again.",
                "Link Tally Company", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = SelectedLiveTallyCompany;
        if (selected is null)
        {
            var options = LiveTallyCompanies.Select(x => new AuditLinkSelectionItem
            {
                Name = x.Name,
                Code = x.CompanyKey,
                FinancialYear = string.IsNullOrWhiteSpace(x.FinancialYear) ? client.FinancialYear : x.FinancialYear,
                Gstin = x.Gstin,
                Value = x
            }).ToArray();
            var ownerWindow = FindAuditOwner();
            var dialog = new AuditLinkSelectionWindow(
                "Link Tally Company",
                "Select the live Tally company to link to this independent Audit client. Accounting client links are not read or changed. After linking, AAH reads this Audit FY's voucher authority into the Audit Tally cache.",
                options) { Owner = ownerWindow };
            if (dialog.ShowDialog() != true || dialog.SelectedItem?.Value is not TallyCompanyInfo chosen) return;
            selected = chosen;
        }

        try
        {
            // R4 R9: reserve CompanyKey only inside the independent Audit workspace.
            // Accounting Tally links are deliberately not read or changed here.
            _auditRepository.ValidateSharedTallyOwnership(client, selected.CompanyKey);
            TallyDiscoveryStatus = "Linking and reading Tally…";
            var count = await _auditTallyLinkService.RefreshVoucherAuthorityAsync(client.Id, selected, client.FinancialYear);
            var updated = client.Clone();
            updated.LinkedTallyCompanyKey = selected.CompanyKey;
            updated.LinkedTallyCompanyName = selected.Name;
            var saved = _auditRepository.SaveClient(updated);
            await DispatchAsync(() =>
            {
                LoadAuditClients(saved.Id);
                SelectedLiveTallyCompany = LiveTallyCompanies.FirstOrDefault(x => x.CompanyKey.Equals(selected.CompanyKey, StringComparison.OrdinalIgnoreCase));
                TallyDiscoveryStatus = $"Linked · {FormatCount(count)} FY voucher(s) cached";
                Status = $"Tally linked to {selected.Name}. {FormatCount(count)} FY voucher(s) are ready for Audit modules.";
            });
            await RefreshAsync(forceAll: true);
        }
        catch (Exception exception)
        {
            await DispatchAsync(() =>
            {
                TallyDiscoveryStatus = "Tally link failed";
                Status = "Tally link failed: " + exception.Message;
            });
        }
    }

    private void ClearTallyLink()
    {
        if (SelectedAuditClient is null || string.IsNullOrWhiteSpace(SelectedAuditClient.LinkedTallyCompanyKey)) return;
        var owner = FindAuditOwner();
        if (MessageBox.Show(owner, "Remove the Tally link from this Audit client? Audit data and Masters are retained. Accounting client links are not read or changed.", "Remove Tally Link", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var updated = SelectedAuditClient.Clone();
        var removedKey = updated.LinkedTallyCompanyKey;
        _auditRepository.DeleteTallySyncState(updated.Id, updated.FinancialYear, removedKey);
        updated.LinkedTallyCompanyKey = string.Empty;
        updated.LinkedTallyCompanyName = string.Empty;
        var saved = _auditRepository.SaveClient(updated);
        LoadAuditClients(saved.Id);
        TallyDiscoveryStatus = "Audit Tally link removed";
        _ = RefreshAsync(forceAll: true);
    }

    private string ResolveAccountingLinkText(long accountingClientId)
    {
        _accountingClientRepository.Initialize(); // called only for an existing explicit Accounting link
        var client = _accountingClientRepository.GetById(accountingClientId);
        return client is null ? $"Accounting Client #{accountingClientId}" : $"{client.ClientCode} · {client.ClientName} · FY {client.FinancialYear}";
    }

    private void RefreshLinkCommandStates()
    {
        EditAuditClientCommand.RaiseCanExecuteChanged();
        ArchiveAuditClientCommand.RaiseCanExecuteChanged();
        LinkAccountingCommand.RaiseCanExecuteChanged();
        ClearAccountingLinkCommand.RaiseCanExecuteChanged();
        ModifyEntryCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsAccountingWorkbenchAvailable));
        RefreshTallyCompaniesCommand.RaiseCanExecuteChanged();
        RefreshLinkedTallyAuthorityCommand.RaiseCanExecuteChanged();
        LinkTallyCommand.RaiseCanExecuteChanged();
        OpenAuditSettingsCommand.RaiseCanExecuteChanged();
        ClearTallyLinkCommand.RaiseCanExecuteChanged();
        ImportAuditBankStatementsCommand.RaiseCanExecuteChanged();
        ModifyEntryCommand.RaiseCanExecuteChanged();
    }

    private void RaiseModuleVisibility()
    {
        OnPropertyChanged(nameof(IsDashboard));
        OnPropertyChanged(nameof(IsClients));
        OnPropertyChanged(nameof(IsBankingFinance));
        OnPropertyChanged(nameof(IsBankingFinanceParent));
        OnPropertyChanged(nameof(IsBankOdLoanModule));
        OnPropertyChanged(nameof(IsBankOrOdAccountModule));
        OnPropertyChanged(nameof(IsBank));
        OnPropertyChanged(nameof(IsOD));
        OnPropertyChanged(nameof(IsLoans));
        OnPropertyChanged(nameof(IsCashMovementPanelVisible));
        OnPropertyChanged(nameof(IsFinanceAccountDashboardModule));
        OnPropertyChanged(nameof(IsFinanceDashboardVisible));
        OnPropertyChanged(nameof(IsFinanceGroupSummaryVisible));
        OnPropertyChanged(nameof(IsFinanceTransactionsVisible));
        OnPropertyChanged(nameof(ShowModuleMetricStrip));
        RaiseBankingTabVisibility();
        OnPropertyChanged(nameof(IsDebtorsCreditorsParent));
        OnPropertyChanged(nameof(IsDebtors));
        OnPropertyChanged(nameof(IsCreditors));
        OnPropertyChanged(nameof(IsDebtorCreditorModule));
        OnPropertyChanged(nameof(IsStandardStructuredRowsVisible));
        OnPropertyChanged(nameof(IsDebtorCreditorLedgerListVisible));
        OnPropertyChanged(nameof(IsDebtorCreditorSettingsVisible));
        OnPropertyChanged(nameof(IsDebtorCreditorLedgerDrilldownVisible));
        OnPropertyChanged(nameof(DebtorCreditorLedgerDrilldownTitle));
        OnPropertyChanged(nameof(IsLedgerVisibilityFilterVisible));
        OnPropertyChanged(nameof(LedgerVisibilityFilterText));
        OnPropertyChanged(nameof(LedgerVisibilityFilterHint));
        OnPropertyChanged(nameof(IsTransactionToolsVisible));
        OnPropertyChanged(nameof(IsAccountingWorkbenchAvailable));
        ModifyEntryCommand.RaiseCanExecuteChanged();
        OpenDebtorCreditorSettingsCommand.RaiseCanExecuteChanged();
        CloseDebtorCreditorSettingsCommand.RaiseCanExecuteChanged();
        CloseDebtorCreditorLedgerCommand.RaiseCanExecuteChanged();
        ShowDebtorCreditorInfoCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsBankOrOd));
        OnPropertyChanged(nameof(IsSales));
        OnPropertyChanged(nameof(IsPurchase));
        RaiseCommerceTabVisibility();
        OnPropertyChanged(nameof(IsCash));
        OnPropertyChanged(nameof(IsDebtorsCreditors));
        OnPropertyChanged(nameof(IsBalanceSheet));
        OnPropertyChanged(nameof(IsProfitLoss));
        OnPropertyChanged(nameof(IsFinancialStatements));
        OnPropertyChanged(nameof(IsFinancialStatementModule));
        OnPropertyChanged(nameof(IsStructuredAuditModule));
        OnPropertyChanged(nameof(IsSources));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsClientReconciliation));
        OnPropertyChanged(nameof(IsGenericDataModule));
        OnPropertyChanged(nameof(IsGstModule));
        OnPropertyChanged(nameof(CurrentPageTitle));
        OnPropertyChanged(nameof(CurrentPageSubtitle));
    }

    private static AuditRecordRow ToAuditRow(AuditStoredRecord row)
    {
        if (!string.IsNullOrWhiteSpace(row.PayloadJson))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<AuditRecordRow>(row.PayloadJson);
                if (payload is not null)
                {
                    payload.SourceRecordId = row.Id;
                    payload.SourceModule = row.Module;
                    if (payload.Source.Length == 0) payload.Source = row.Source;
                    if (payload.EvidenceStatus.Length == 0) payload.EvidenceStatus = "Audit accounting workbench";
                    return payload;
                }
            }
            catch { }
        }

        return new AuditRecordRow
        {
            SourceRecordId = row.Id,
            SourceModule = row.Module,
            Date = row.RecordDate,
            Reference = row.Reference,
            VoucherType = row.VoucherType,
            AuthorityId = row.AuthorityId,
            PartyOrLedger = row.PartyOrLedger,
            Details = row.Details,
            OriginalNarration = row.Details,
            Category = row.Module switch
            {
                "Banking" or "BankingFinance" => "Bank",
                "OD" => "OD",
                "Loans" => "Loans",
                "Debtors" => "Sundry Debtors",
                "Creditors" => "Sundry Creditors",
                _ => row.VoucherType
            },
            TransactionClass = row.VoucherType,
            Debit = row.Debit,
            Credit = row.Credit,
            Amount = row.Amount,
            Status = row.Status,
            EvidenceStatus = row.OriginKind.StartsWith("AccountingWorkbench", StringComparison.OrdinalIgnoreCase)
                ? "Audit accounting workbench" : "Audit-owned record",
            Source = row.Source,
            IsException = IsIssueText(row.Status)
        };
    }

    private static string[] SplitLedgerNames(string? value) =>
        (value ?? string.Empty).Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static Window? FindAuditOwner() =>
        System.Windows.Application.Current?.Windows.OfType<AuditWindow>().FirstOrDefault(window => window.IsVisible)
        ?? System.Windows.Application.Current?.MainWindow;

    private static Task DispatchAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action).Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _auditTallyHeartbeatTimer.Stop();
        _auditTallyHeartbeatTimer.Tick -= OnAuditTallyHeartbeatTick;
        _sourceChangeMonitor.Dispose();
        _preparedAudit.Ready -= OnPreparedAuditReady;
        _preparedAudit.Failed -= OnPreparedAuditFailed;
        _preparedAudit.Dispose();
        Interlocked.Increment(ref _refreshVersion);
        _tallyAuthorityRefreshGate.Dispose();
    }

    public sealed class AuditSnapshot
    {
        public AuditSnapshot() { }
        public string Module { get; set; } = string.Empty;
        public AuditRecordRow[] Rows { get; set; } = [];
        public AuditBankSummaryRow[] BankSummaries { get; set; } = [];
        public AuditSourceRow[] Sources { get; set; } = [];
        public AuditDashboardCard[] DashboardCards { get; set; } = [];
        public AuditStatisticRow[] MasterStatistics { get; set; } = [];
        public AuditStatisticRow[] VoucherStatistics { get; set; } = [];
        public AuditRecordRow[] DebtorCreditorVoucherRows { get; set; } = [];
        public AuditFinancialStatementRow[] FinancialStatementRows { get; set; } = [];
        public Dictionary<string, int> ModuleCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}


public sealed class AuditStatisticRow
{
    public AuditStatisticRow(string name, int count, string detail)
    {
        Name = name;
        Count = count;
        Detail = detail;
    }
    public string Name { get; }
    public int Count { get; }
    public string CountText => Count.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string Detail { get; }
}

public sealed class AuditFinancialStatementRow
{
    public string Statement { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string PrimaryGroup { get; set; } = string.Empty;
    public string LedgerGroup { get; set; } = string.Empty;
    public string Ledger { get; set; } = string.Empty;
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }
    public int TransactionCount { get; set; }
    public int LedgerCount { get; set; }
    public string MasterId { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public bool IsGroupSummary { get; set; }
    public decimal OpeningDebit => OpeningBalance > 0m ? Math.Abs(OpeningBalance.Value) : 0m;
    public decimal OpeningCredit => OpeningBalance < 0m ? Math.Abs(OpeningBalance.Value) : 0m;
    public decimal ClosingDebit => ClosingBalance > 0m ? Math.Abs(ClosingBalance.Value) : 0m;
    public decimal ClosingCredit => ClosingBalance < 0m ? Math.Abs(ClosingBalance.Value) : 0m;
    public string RowType => IsGroupSummary ? "GROUP" : "Ledger";
    public string TransactionCountText => TransactionCount.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string LedgerCountText => IsGroupSummary ? LedgerCount.ToString("N0", CultureInfo.GetCultureInfo("en-IN")) : string.Empty;
}

public sealed class AuditNavigationItem : ViewModelBase
{
    private int _count;
    private bool _isVisible = true;
    public AuditNavigationItem(string key, string title, string description, string icon = "•", bool isSubmodule = false, string parentKey = "")
    {
        Key = key;
        Title = title;
        Description = description;
        Icon = icon;
        IsSubmodule = isSubmodule;
        ParentKey = parentKey;
    }
    public string Key { get; }
    public string Title { get; }
    public string Description { get; }
    public string Icon { get; }
    public bool IsSubmodule { get; }
    public string ParentKey { get; }
    public int Count { get => _count; set { if (SetProperty(ref _count, value)) OnPropertyChanged(nameof(CountText)); } }
    public bool IsVisible { get => _isVisible; set => SetProperty(ref _isVisible, value); }
    public string CountText => Count > 0 ? Count.ToString("N0", CultureInfo.GetCultureInfo("en-IN")) : string.Empty;
}

public sealed class AuditRecordRow
{
    public long SourceRecordId { get; set; }
    public string SourceModule { get; set; } = string.Empty;
    public string TallyMasterId { get; set; } = string.Empty;
    public string TallyGuid { get; set; } = string.Empty;
    public DateTime? Date { get; set; }
    public string DateText => Date.HasValue ? Date.Value.ToString("dd-MM-yyyy") : string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string PartyOrLedger { get; set; } = string.Empty;
    public string DisplayPartyOrLedger { get; set; } = string.Empty;
    public string Counterparty { get; set; } = string.Empty;
    public string VoucherType { get; set; } = string.Empty;
    public string AuthorityId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TransactionClass { get; set; } = string.Empty;
    public string CounterpartyGroup { get; set; } = string.Empty;
    public string PaymentReceiptType { get; set; } = string.Empty;
    public string BusinessGroup { get; set; } = string.Empty;
    public string BusinessLedger { get; set; } = string.Empty;
    public string TaxLedgers { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public decimal TaxableValue { get; set; }
    public decimal Igst { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Cess { get; set; }
    public decimal UnallocatedGst { get; set; }
    public decimal GstAmount => Igst + Cgst + Sgst + Cess + UnallocatedGst;
    public string TaxEvidenceSource { get; set; } = string.Empty;
    public bool IsGstr1Evidence { get; set; }
    public bool HasTwoAEvidence { get; set; }
    public bool HasTwoBEvidence { get; set; }
    public decimal? TwoBInvoiceValue { get; set; }
    public decimal? TwoBTaxableValue { get; set; }
    public decimal? TwoBIgst { get; set; }
    public decimal? TwoBCgst { get; set; }
    public decimal? TwoBSgst { get; set; }
    public decimal? TwoBGstAmount => TwoBIgst.HasValue || TwoBCgst.HasValue || TwoBSgst.HasValue
        ? Math.Abs(TwoBIgst ?? 0m) + Math.Abs(TwoBCgst ?? 0m) + Math.Abs(TwoBSgst ?? 0m)
        : null;
    public decimal GstRate { get; set; }
    public bool GstRateKnown { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string HsnCode { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal ItemRate { get; set; }
    public decimal RoundOff { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal DiscountAmount { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string TrackingNumber { get; set; } = string.Empty;
    public string Godown { get; set; } = string.Empty;
    public string BatchName { get; set; } = string.Empty;
    public string CostCentre { get; set; } = string.Empty;
    public string SourceSection { get; set; } = string.Empty;
    public string PlaceOfSupply { get; set; } = string.Empty;
    public string ReturnPeriod { get; set; } = string.Empty;
    public string Irn { get; set; } = string.Empty;
    public string EWayBillNumber { get; set; } = string.Empty;
    public string InvoiceType { get; set; } = string.Empty;
    public string TwoBReason { get; set; } = string.Empty;
    public string DifferenceDetail { get; set; } = string.Empty;
    public string ReconciliationStatus { get; set; } = string.Empty;
    public string ItcStatus { get; set; } = string.Empty;
    public bool IsRcm { get; set; }
    public string Details { get; set; } = string.Empty;
    public string OriginalNarration { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Amount { get; set; }
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }
    public string BalanceNature { get; set; } = string.Empty;
    public int TransactionCount { get; set; }
    public string TransactionCountText => TransactionCount.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string PercentageText { get; set; } = string.Empty;
    public string NetTransactionWithNatureText => Debit > Credit
        ? $"{(Debit - Credit).ToString("N2", CultureInfo.GetCultureInfo("en-IN"))} Dr"
        : Credit > Debit
            ? $"{(Credit - Debit).ToString("N2", CultureInfo.GetCultureInfo("en-IN"))} Cr"
            : "0.00";
    public decimal OpeningDebit => OpeningBalance > 0m ? Math.Abs(OpeningBalance.Value) : 0m;
    public decimal OpeningCredit => OpeningBalance < 0m ? Math.Abs(OpeningBalance.Value) : 0m;
    public decimal ClosingDebit => ClosingBalance > 0m ? Math.Abs(ClosingBalance.Value) : 0m;
    public decimal ClosingCredit => ClosingBalance < 0m ? Math.Abs(ClosingBalance.Value) : 0m;
    public string OpeningDebitText => OpeningBalance > 0m ? Math.Abs(OpeningBalance.Value).ToString("N2", CultureInfo.GetCultureInfo("en-IN")) : string.Empty;
    public string OpeningCreditText => OpeningBalance < 0m ? Math.Abs(OpeningBalance.Value).ToString("N2", CultureInfo.GetCultureInfo("en-IN")) : string.Empty;
    public string ClosingDebitText => ClosingBalance > 0m ? Math.Abs(ClosingBalance.Value).ToString("N2", CultureInfo.GetCultureInfo("en-IN")) : string.Empty;
    public string ClosingCreditText => ClosingBalance < 0m ? Math.Abs(ClosingBalance.Value).ToString("N2", CultureInfo.GetCultureInfo("en-IN")) : string.Empty;
    public string OpeningBalanceText => !OpeningBalance.HasValue ? "—" : Math.Abs(OpeningBalance.Value).ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string OpeningNature => !OpeningBalance.HasValue ? "N/A" : OpeningBalance.Value > 0m ? "Dr" : OpeningBalance.Value < 0m ? "Cr" : "Zero";
    public string OpeningBalanceWithNatureText => !OpeningBalance.HasValue ? "—" : OpeningBalance.Value > 0m ? $"{Math.Abs(OpeningBalance.Value).ToString("N2", CultureInfo.GetCultureInfo("en-IN"))} Dr" : OpeningBalance.Value < 0m ? $"{Math.Abs(OpeningBalance.Value).ToString("N2", CultureInfo.GetCultureInfo("en-IN"))} Cr" : "0.00";
    public string ClosingBalanceText => !ClosingBalance.HasValue ? "—" : Math.Abs(ClosingBalance.Value).ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string ClosingNature => !ClosingBalance.HasValue ? "N/A" : ClosingBalance.Value > 0m ? "Dr" : ClosingBalance.Value < 0m ? "Cr" : "Zero";
    public string ClosingBalanceWithNatureText => !ClosingBalance.HasValue ? "—" : ClosingBalance.Value > 0m ? $"{Math.Abs(ClosingBalance.Value).ToString("N2", CultureInfo.GetCultureInfo("en-IN"))} Dr" : ClosingBalance.Value < 0m ? $"{Math.Abs(ClosingBalance.Value).ToString("N2", CultureInfo.GetCultureInfo("en-IN"))} Cr" : "0.00";
    public string Status { get; set; } = string.Empty;
    public string EvidenceStatus { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public string RuleReference { get; set; } = string.Empty;
    public string RuleReview { get; set; } = string.Empty;
    public string SourceEvidence { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool IsException { get; set; }
    public AuditVoucherAdvancedData AdvancedVoucher { get; set; } = new();
}

public sealed class AuditFinanceAccountSummary : ViewModelBase
{
    private bool _isSelected;
    public string Account { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int RowCount { get; set; }
    public decimal? BooksOpening { get; set; }
    public decimal BooksDebit { get; set; }
    public decimal BooksCredit { get; set; }
    public decimal? BooksClosing { get; set; }
    public bool BooksClosingFromTally { get; set; }
    public decimal? CalculatedBooksClosing { get; set; }
    public decimal CashWithdrawn { get; set; }
    public decimal CashDeposited { get; set; }
    public decimal StatementDebit { get; set; }
    public decimal StatementCredit { get; set; }
    public string Opening { get; set; } = string.Empty;
    public string ExpectedClosing { get; set; } = string.Empty;
    public string StatementClosing { get; set; } = string.Empty;
    public decimal? Difference { get; set; }
    public string ReconciliationStatus { get; set; } = string.Empty;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public string VerificationStatus => string.IsNullOrWhiteSpace(ReconciliationStatus) ? "Pending" : ReconciliationStatus;
    public string StatementStatus => Category.Equals("Loans", StringComparison.OrdinalIgnoreCase) ? "Loan books authority" : StatementDebit != 0m || StatementCredit != 0m || StatementClosing.Length > 0 ? "Statement available" : "Statement awaited";
    public string BooksOpeningDisplay => !BooksOpening.HasValue
        ? "Not available"
        : Category is "Bank" or "OD" or "Loans"
            ? FormatSignedBalanceIndian(BooksOpening)
            : BooksOpening.Value.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string BooksClosingDisplay => !BooksClosing.HasValue
        ? "Not available"
        : Category is "Bank" or "OD" or "Loans"
            ? FormatSignedBalanceIndian(BooksClosing)
            : BooksClosing.Value.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string BooksClosingAuthorityText => BooksClosingFromTally ? "Tally Ledger closing authority" : CalculatedBooksClosing.HasValue ? "Calculated fallback · Opening + FY Debit − FY Credit" : "Closing authority unavailable";
    public bool IsOpeningNatureAlert => BooksOpening.HasValue && (Category.Equals("Bank", StringComparison.OrdinalIgnoreCase)
        ? BooksOpening.Value < 0m
        : (Category.Equals("OD", StringComparison.OrdinalIgnoreCase) || Category.Equals("Loans", StringComparison.OrdinalIgnoreCase)) && BooksOpening.Value > 0m);
    public bool IsClosingNatureAlert => BooksClosing.HasValue && (Category.Equals("Bank", StringComparison.OrdinalIgnoreCase)
        ? BooksClosing.Value < 0m
        : (Category.Equals("OD", StringComparison.OrdinalIgnoreCase) || Category.Equals("Loans", StringComparison.OrdinalIgnoreCase)) && BooksClosing.Value > 0m);
    public bool IsBankOpeningNegative => Category.Equals("Bank", StringComparison.OrdinalIgnoreCase) && BooksOpening.HasValue && BooksOpening.Value < 0m;
    public bool IsBankClosingNegative => Category.Equals("Bank", StringComparison.OrdinalIgnoreCase) && BooksClosing.HasValue && BooksClosing.Value < 0m;
    public string ExpectedBalanceNatureText => Category switch
    {
        "Bank" => "Bank Accounts normally carry a Debit balance; Credit balance requires review.",
        "OD" => "Bank OD normally carries a Credit balance; Debit balance requires review.",
        "Loans" => "Secured Loans normally carry a Credit balance; Debit balance requires review.",
        "Cash" => "Cash-in-Hand normally carries a Debit balance.",
        _ => "Review the ledger's normal balance nature."
    };
    public string ClosingNatureStatus => !BooksClosing.HasValue
        ? "Closing unavailable"
        : IsClosingNatureAlert
            ? Category.Equals("Bank", StringComparison.OrdinalIgnoreCase) ? "Credit balance · Review" : "Debit balance · Review"
            : Category is "OD" or "Loans" ? "Credit balance · Normal" : "Debit balance · Normal";
    public string OpeningDisplay => string.IsNullOrWhiteSpace(Opening) ? "Not available" : Opening;
    public string ExpectedClosingDisplay => string.IsNullOrWhiteSpace(ExpectedClosing) ? "Not available" : ExpectedClosing;
    public string StatementClosingDisplay => string.IsNullOrWhiteSpace(StatementClosing) ? "Not available" : StatementClosing;
    public string DifferenceDisplay => Difference.HasValue ? Difference.Value.ToString("N2", CultureInfo.GetCultureInfo("en-IN")) : "Not available";
    public string Detail => $"{Category} · {RowCount.ToString("N0", CultureInfo.GetCultureInfo("en-IN"))} movement(s)";

    private static string FormatSignedBalanceIndian(decimal? balance)
    {
        if (!balance.HasValue) return "-";
        var rounded = Math.Round(balance.Value, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded) < 0.005m) return "0.00";
        return $"{Math.Abs(rounded).ToString("N2", CultureInfo.GetCultureInfo("en-IN"))} {(rounded > 0m ? "Dr" : "Cr")}";
    }
}

public sealed class AuditGroupMovementSummary
{
    public string Group { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Amount { get; set; }
    public decimal SharePercent { get; set; }
    public string MovementType { get; set; } = string.Empty;
    public string CountDisplay => Count.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string AmountDisplay => Amount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string ShareDisplay => SharePercent.ToString("0.##", CultureInfo.GetCultureInfo("en-IN")) + "%";
}

public sealed class AuditPartyMovementSummary
{
    public string Counterparty { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Amount { get; set; }
    public string MovementType { get; set; } = string.Empty;
    public string CountDisplay => Count.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string AmountDisplay => Amount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
}

public sealed class AuditBankCashMovementSummary
{
    public string Bank { get; set; } = string.Empty;
    public int WithdrawalsCount { get; set; }
    public decimal WithdrawalAmount { get; set; }
    public int DepositsCount { get; set; }
    public decimal DepositAmount { get; set; }
}

public sealed class AuditCommerceGroupSummary
{
    public string Group { get; set; } = string.Empty;
    public int Count { get; set; }
    public int PartyCount { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal GstAmount { get; set; }
    public decimal Amount { get; set; }
    public decimal AdjustmentAmount { get; set; }
    public decimal NetAmount { get; set; }
    public string MonthSummary { get; set; } = string.Empty;
    public decimal SharePercent { get; set; }
    public string Type { get; set; } = string.Empty;
    public string CountDisplay => Count.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string PartyCountDisplay => PartyCount.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string TaxableDisplay => TaxableValue.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string GstDisplay => GstAmount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string AmountDisplay => Amount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string AdjustmentDisplay => AdjustmentAmount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string NetDisplay => NetAmount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string ShareDisplay => SharePercent.ToString("0.##", CultureInfo.GetCultureInfo("en-IN")) + "%";
}

public sealed class AuditCommercePartySummary
{
    public string Party { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal AdjustmentAmount { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal GstAmount { get; set; }
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }
    public int AlertCount { get; set; }
    public string CountDisplay => Count.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string GrossDisplay => GrossAmount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string AdjustmentDisplay => AdjustmentAmount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string TaxableDisplay => TaxableValue.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string GstDisplay => GstAmount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string OpeningDisplay => OpeningBalance.HasValue ? FormatBalance(OpeningBalance.Value) : "Not available";
    public string ClosingDisplay => ClosingBalance.HasValue ? FormatBalance(ClosingBalance.Value) : "Not available";
    private static string FormatBalance(decimal value) => Math.Abs(value).ToString("N2", CultureInfo.GetCultureInfo("en-IN")) + (value < 0 ? " Cr" : value > 0 ? " Dr" : string.Empty);
}

public sealed class AuditCommerceGstRateSummary
{
    public string RateLabel { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public int Count { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal GstAmount { get; set; }
    public decimal Amount { get; set; }
    public decimal SharePercent { get; set; }
    public string CountDisplay => Count.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string TaxableDisplay => TaxableValue.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string GstDisplay => GstAmount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string AmountDisplay => Amount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string ShareDisplay => SharePercent.ToString("0.##", CultureInfo.GetCultureInfo("en-IN")) + "%";
}

public sealed class AuditCommerceItemSummary
{
    public string Item { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public int Count { get; set; }
    public int PartyCount { get; set; }
    public decimal Quantity { get; set; }
    public decimal Amount { get; set; }
    public decimal AverageRate { get; set; }
    public decimal? GstRate { get; set; }
    public decimal SharePercent { get; set; }
    public string QuantityDisplay => Quantity.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string AmountDisplay => Amount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string AverageRateDisplay => AverageRate.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string GstRateDisplay => GstRate.HasValue ? GstRate.Value.ToString("0.##", CultureInfo.GetCultureInfo("en-IN")) + "%" : "-";
    public string ShareDisplay => SharePercent.ToString("0.##", CultureInfo.GetCultureInfo("en-IN")) + "%";
}

public sealed class AuditCommerceHsnSummary
{
    public string Hsn { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Quantity { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Igst { get; set; }
    public decimal Cess { get; set; }
    public decimal GstAmount => Cgst + Sgst + Igst + Cess;
    public decimal Amount { get; set; }
    public string CountDisplay => Count.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string QuantityDisplay => Quantity.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string TaxableDisplay => TaxableValue.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string CgstDisplay => Cgst.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string SgstDisplay => Sgst.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string IgstDisplay => Igst.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string CessDisplay => Cess.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string GstDisplay => GstAmount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string AmountDisplay => Amount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
}

public sealed class AuditCommerceMonthSummary
{
    public int MonthOrder { get; set; }
    public string Month { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Gross { get; set; }
    public decimal Adjustments { get; set; }
    public decimal Net { get; set; }
    public decimal GstAmount { get; set; }
    public string CountDisplay => Count.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string GrossDisplay => Gross.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string AdjustmentsDisplay => Adjustments.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string NetDisplay => Net.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string GstDisplay => GstAmount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
}

public sealed class AuditCommerceSupplySummary
{
    public string SupplyType { get; set; } = string.Empty;
    public int Count { get; set; }
    public int AdjustmentCount { get; set; }
    public int PartyCount { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal AdjustmentAmount { get; set; }
    public decimal NetAmount { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal GstAmount { get; set; }
    public string CountDisplay => Count.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string AdjustmentCountDisplay => AdjustmentCount.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string PartyCountDisplay => PartyCount.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string GrossDisplay => GrossAmount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string AdjustmentDisplay => AdjustmentAmount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string NetDisplay => NetAmount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string TaxableDisplay => TaxableValue.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string GstDisplay => GstAmount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
}

public sealed class AuditCommerceTaxAmountSummary
{
    public string Basis { get; set; } = string.Empty;
    public decimal? Rate { get; set; }
    public int Count { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Igst { get; set; }
    public decimal Cess { get; set; }
    public decimal UnallocatedGst { get; set; }
    public string TaxLedgers { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public decimal TotalTax => Cgst + Sgst + Igst + Cess + UnallocatedGst;
    public string RateDisplay => Rate.HasValue ? Rate.Value.ToString("0.##", CultureInfo.GetCultureInfo("en-IN")) + "%" : "Rate unavailable";
    public string CountDisplay => Count.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string TaxableDisplay => TaxableValue.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string CgstDisplay => Cgst.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string SgstDisplay => Sgst.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string IgstDisplay => Igst.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string CessDisplay => Cess.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string UnallocatedDisplay => UnallocatedGst.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
    public string TotalTaxDisplay => TotalTax.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
}

public sealed class AuditCommerceBooksReturnComparisonRow
{
    public string ReturnName { get; set; } = string.Empty;
    public DateTime? Date { get; set; }
    public string DateText => Date.HasValue ? Date.Value.ToString("dd-MM-yyyy") : string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string Party { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string VoucherType { get; set; } = string.Empty;
    public decimal BooksAmount { get; set; }
    public decimal ReturnAmount { get; set; }
    public decimal AmountDifference { get; set; }
    public decimal BooksTaxable { get; set; }
    public decimal ReturnTaxable { get; set; }
    public decimal TaxableDifference { get; set; }
    public decimal BooksGst { get; set; }
    public decimal ReturnGst { get; set; }
    public decimal GstDifference { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class AuditCommerceAlertSummary
{
    public string Alert { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Amount { get; set; }
    public string CountDisplay => Count.ToString("N0", CultureInfo.GetCultureInfo("en-IN"));
    public string AmountDisplay => Amount.ToString("N2", CultureInfo.GetCultureInfo("en-IN"));
}

public sealed class AuditModuleTab : ViewModelBase
{
    private bool _isSelected;
    public AuditModuleTab(string key, string title, int count)
    {
        Key = key;
        Title = title;
        Count = count;
    }
    public string Key { get; }
    public string Title { get; }
    public int Count { get; }
    public bool ShowCount => Count >= 0;
    public string CountText => Count >= 0 ? Count.ToString("N0", CultureInfo.GetCultureInfo("en-IN")) : string.Empty;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed class AuditMetricTile
{
    public AuditMetricTile(string title, string value, string detail, bool isAlert = false)
    {
        Title = title;
        Value = value;
        Detail = detail;
        IsAlert = isAlert;
    }
    public string Title { get; }
    public string Value { get; }
    public string Detail { get; }
    public bool IsAlert { get; }
}

public sealed class AuditBankSummaryRow
{
    public string Account { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public string Opening { get; set; } = string.Empty;
    public decimal StatementDebit { get; set; }
    public decimal StatementCredit { get; set; }
    public decimal BooksDebit { get; set; }
    public decimal BooksCredit { get; set; }
    public string ExpectedClosing { get; set; } = string.Empty;
    public string StatementClosing { get; set; } = string.Empty;
    public decimal? Difference { get; set; }
    public string DebitCreditStatus { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public sealed class AuditSourceRow
{
    public string Source { get; set; } = string.Empty;
    public string PathOrAuthority { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public sealed class AuditDashboardCard
{
    public AuditDashboardCard(string title, int count, string detail, string targetModuleKey = "")
    {
        Title = title;
        Count = count;
        Detail = detail;
        TargetModuleKey = targetModuleKey ?? string.Empty;
    }
    public string Title { get; }
    public int Count { get; }
    public bool ShowCount => Count >= 0;
    public string CountText => Count >= 0 ? Count.ToString("N0", CultureInfo.GetCultureInfo("en-IN")) : string.Empty;
    public string Detail { get; }
    public string TargetModuleKey { get; }
    public bool IsClickable => TargetModuleKey.Length > 0;
}
