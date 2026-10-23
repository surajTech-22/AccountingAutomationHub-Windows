using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Xml;
using System.Text;
using System.IO.Compression;
using System.Globalization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Collections;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Accounting;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Imports;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Accounting;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Imports;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Spreadsheets;
using AccountingAutomationHub.Infrastructure.Tally;
using Microsoft.Win32;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Application.Clients;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class BankingViewModel : PageViewModel
{
    private sealed record LedgerMappingRebuildResult(
        IReadOnlyList<BankLedgerMapping> Mappings,
        IReadOnlyDictionary<string, int> TallyExportDateCounts);

    private readonly SqliteBankingRepository _repository;
    private readonly BankStatementImportService _importService;
    private readonly BankReconciliationService _reconciliationService;
    private readonly BankTransferReconciliationService _transferService;
    private readonly BankLedgerMappingService _mappingService;
    private readonly CashEntryService _cashEntryService;
    private readonly BankingWorkbookReportService _reportService;
    private readonly SqliteMasterRepository _masterRepository;
    private readonly LedgerTotalService _ledgerTotalService;
    private readonly AdvancedClosingCashRepairService _advancedClosingCashRepairService;
    private readonly ActiveClientContext _clientContext;
    private readonly SqliteImportActivityRepository _activityRepository;
    private readonly SqliteActionRequiredRepository _actionRequiredRepository = new();
    private readonly ImportActivityContext _activityContext;
    private readonly TallyExportSelectionContext _tallyExportSelectionContext;
    private readonly PdfBankStatementPipelineService _pdfPipelineService;
    private readonly BankOcrExcelConversionService _bankOcrService;
    private readonly AccountingLiveChangeHub _liveChangeHub;
    private readonly BackgroundAutomationService? _automationService;
    private readonly ClientWorkspaceStateCache _workspaceStateCache;
    private readonly IClientPreferenceRepository _clientPreferences;
    private readonly TransactionAutomationService _transactionAutomationService;
    private readonly TallyBankStatementReconciliationService? _tallyBankStatementReconciliationService;
    private readonly Func<bool> _isTallyConnected;
    private CancellationTokenSource? _ledgerMappingExistingMatchRefreshCancellation;
    private LedgerMappingWorkbenchRow? _activeLedgerMappingEditorRow;
    private bool _ledgerMappingExistingMatchRefreshDeferred;
    private string _ledgerMappingExistingMatchRefreshFingerprint = string.Empty;
    private bool _ledgerMappingCoverageRepairDeferred;
    private bool _ledgerMappingProjectionUpgradeRequired;
    private bool _ledgerMappingProjectionUpgradeRunning;
    private bool _ledgerMappingProjectionUpgradeDeferred;
    private long _ledgerMappingPreferencesClientId;
    private string _ledgerMappingPreferencesFinancialYear = string.Empty;
    private const string LedgerMappingProjectionVersion = "R45-STABLE-LEDGER-MAPPING-V1";
    private readonly Func<bool> _isCurrentClientTallyLinked;
    private readonly ConcurrentDictionary<string, byte> _tallyBankReconciliationInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _bankBalanceEvidenceRepairInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _advancedClosingCashRepairInFlight = new(StringComparer.OrdinalIgnoreCase);
    // Fix137 R35: remember the exact Advanced Closure-owned Cash state that was last
    // checked. Ordinary Cash changes (including direct Recurring generation) must not
    // rerun the historical closure repair and create a Cash cache refresh feedback loop.
    private readonly ConcurrentDictionary<string, string> _advancedClosingCashRepairFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _tallyBankReconciliationLastAttemptUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly AdvancedBankReconciliationService _advancedReconciliationService;
    private readonly SqliteGovernanceRepository _governanceRepository;
    private bool _loadingLedgerMappingPreferences;

    private long _loadedClientId;
    private string _loadedFinancialYear = string.Empty;
    private bool _requiresDataRefresh = true;
    private bool _ledgerMappingsLoaded;
    private IReadOnlyList<MasterRecord> _activeMasterRecords = [];
    private string _bankBalanceAccountName = "Select a Bank Account";
    private decimal? _bankTallyOpeningBalance;
    private decimal? _bankOriginalOpeningBalance;
    private decimal _bankImportedStatementDebits;
    private decimal _bankImportedStatementCredits;
    private decimal? _bankCalculatedClosingBalance;
    private decimal? _bankOriginalClosingBalance;
    private decimal? _bankOpeningDifference;
    private decimal? _bankClosingDifference;
    private bool _bankHasLinkedTallyAuthority;
    private string _bankBalanceStatus = "Select a Bank Account";

    private BankAccountRecord? _selectedAccount;
    private BankTransactionRecord? _selectedTransaction;
    private bool _isManualCashContraMode;
    private string _manualCashContraLedger = string.Empty;
    private string _manualCashContraStatusText = "Select Bank entries, then choose Manual Cash Contra.";
    private string _manualCashContraStepText = "Step 1 of 2 · Select entries";
    private string _manualCashContraSourceText = "Bank Statement";
    private BankStatementRecord? _selectedStatement;
    private BankReconciliationSummary? _selectedReconciliationRow;
    private StatementMetadataRow? _selectedStatementMetadata;
    private LedgerMappingWorkbenchRow? _selectedLedgerMappingRow;
    private LedgerMappingDetailRow? _selectedLedgerMappingDetail;
    private BankRenameRow? _selectedBankRename;
    private BankLedgerRenameOption? _selectedBankRenameOption;
    private ObVerificationRow? _selectedObVerificationRow;
    private CashEntryRecord? _selectedCashEntry;
    private CashEntryRecord? _cashVoucherDraft;
    private bool _isCashVoucherEditorOpen;
    private CancellationTokenSource? _cashWorkbookWatchCts;
    private LedgerTotalSnapshot? _selectedCashLedgerTotal;
    private string _selectedCashLedgerTotalStatus = "Select a Ledger to view its live total.";
    private int _ledgerTotalRefreshVersion;
    private CashEntryMasterMappingRow? _selectedCashMasterMapping;
    private CashEntryCommonMappingRow? _selectedCashCommonMapping;
    private CashEntrySettings _cashSettings = new();
    private bool _isBusy;
    private bool _includeArchivedTransfers;
    private string _statusText = "Banking workspace ready.";
    private string _tallyInterbankVerificationStatus = "Tally Bank transactions will be converted and sent to Inter-Bank reconciliation automatically for a linked client.";
    private string _cashStatusText = "Cash Entry workspace ready.";
    private string _chainStatusText = string.Empty;
    private int _missingStatementPeriodCount;
    private string _bankAccountPeriodWarningText = string.Empty;
    private bool _createLedgerWhenApplying = true;
    private string _cashLayoutMode = "Debit-Credit";
    private string _ledgerMappingBankFilter = "ALL BANKS";
    private string _ledgerMappingShowMapped = "NO";
    private string _ledgerMappingVoucherFilter = "ALL TYPES";
    private string _ledgerMappingMethodFilter = "ALL METHODS";
    private readonly Dictionary<long, string> _ledgerMappingMethodByTransactionId = new();
    private string _ledgerMappingDateScope = "Full Statement";
    private string _bankStatementDateScope = "Full Statement";
    private string _defaultEmptyLedger = BankingPreferenceKeys.DefaultEmptyLedgerName;
    private string _emptyLedgerDraftName = BankingPreferenceKeys.DefaultEmptyLedgerName;
    private string _emptyLedgerDraftGroup = BankingPreferenceKeys.DefaultEmptyLedgerParent;
    private string _emptyLedgerMasterStatusText = "Master status not checked.";
    private string _emptyLedgerMasterSourceText = string.Empty;
    private string _emptyLedgerResolvedName = string.Empty;
    private string _emptyLedgerResolvedGroup = string.Empty;
    private string _emptyLedgerResolvedParentGroup = string.Empty;
    private string _emptyLedgerGroupStatusText = "Group status not checked.";
    private string _emptyLedgerParentGroupStatusText = "Parent Group status not checked.";
    private bool _ledgerMappingAllRowsSelected = true;
    private bool _updatingLedgerMappingSelection;
    private bool _isBulkLedgerMappingOpen;
    private string _bulkMappingFinalLedger = string.Empty;
    private string _bulkMappingLedgerGroup = string.Empty;
    private string _bulkMappingSelectionType = string.Empty;
    private string _bulkMappingCaption = string.Empty;
    private bool _bulkMappingIsSimple;
    private string _bulkMappingBankFilter = "All Banks";
    private string _bulkMappingVoucherTypeFilter = "All";
    private string _bulkMappingAmountOperator = "Any";
    private string _bulkMappingAmountSearchText = string.Empty;
    private bool _bulkMappingShowMixedVoucherType;
    private string _bulkMappingAmountFromText = string.Empty;
    private string _bulkMappingAmountToText = string.Empty;
    private string _bulkMappingFilterSummary = "Bank: All Banks · Voucher Type: All · Amount: Any";
    private bool _bulkMappingAmountFilterActive;
    private bool _suppressBulkMappingLiveFilterRefresh;
    private bool _bulkMappingUseVoucherTypeGrid;
    private string _bulkSimpleLedgerDraft = string.Empty;
    private string _bulkSimpleGroupDraft = string.Empty;
    private readonly Dictionary<string, (string FinalLedger, string LedgerGroup)> _bulkVoucherDraftsByType = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> _bulkAppliedTransactionIdsThisSession = [];
    private readonly List<BankLedgerMapping> _bulkSelectedBaseMappings = [];
    private readonly List<BankLedgerMapping> _bulkSelectedAtomicMappings = [];
    private string _bankStatementFilterColumn = "ALL COLUMNS";
    private string _bankStatementFilterText = string.Empty;
    private string _ledgerMappingColumnFilter = "ALL COLUMNS";
    private string _ledgerMappingTextFilter = string.Empty;
    private string _ledgerMappingDetailFilterColumn = "ALL COLUMNS";
    private string _ledgerMappingDetailFilterText = string.Empty;
    private int _ledgerMappingScopedPendingTransactionCount;
    private int _ledgerMappingScopedMappedTransactionCount;
    private int _ledgerMappingVisibleTransactionCount;
    private int _ledgerMappingExpectedTransactionCount;
    private string _cashEntryMappingMode = "Master Update";
    private string _selectedCashCommonLedger = string.Empty;
    private string _obFilterType = "Client Name";
    private string _obFilterValue = "ALL";
    private bool _showVerifiedObRows;
    private int _selectedBankingTabIndex;
    private int _selectedReconciliationViewIndex;
    private bool _isBankAccountPopupOpen;
    private bool _isLedgerMappingDetailOpen;
    private bool _isBalanceReviewDetailOpen;
    private bool _isBuildingLedgerMappingOptions;
    private bool _preserveBankMasterParentOnNextSave;
    private bool _ledgerMappingWorkspaceLoaded;
    private bool _ledgerMappingCoverageRepairRunning;
    private readonly HashSet<long> _ledgerMappingDraftCommitIds = [];
    private bool _reportsWorkspaceLoaded;
    private MasterRecord? _selectedBankMasterRecord;
    private IReadOnlyList<ObVerificationRow> _allObVerificationRows = [];

    public BankingViewModel(
        SqliteBankingRepository repository,
        BankStatementImportService importService,
        BankReconciliationService reconciliationService,
        BankTransferReconciliationService transferService,
        BankLedgerMappingService mappingService,
        CashEntryService cashEntryService,
        BankingWorkbookReportService reportService,
        SqliteMasterRepository masterRepository,
        LedgerTotalService ledgerTotalService,
        AdvancedClosingCashRepairService advancedClosingCashRepairService,
        ActiveClientContext clientContext,
        SqliteImportActivityRepository activityRepository,
        ImportActivityContext activityContext,
        TallyExportSelectionContext tallyExportSelectionContext,
        PdfBankStatementPipelineService? pdfPipelineService,
        AccountingLiveChangeHub liveChangeHub,
        ClientWorkspaceStateCache workspaceStateCache,
        IClientPreferenceRepository clientPreferences,
        bool showMappedByDefault = false,
        BackgroundAutomationService? automationService = null,
        TransactionAutomationService? transactionAutomationService = null,
        TallyBankStatementReconciliationService? tallyBankStatementReconciliationService = null,
        Func<bool>? isTallyConnected = null,
        Func<bool>? isCurrentClientTallyLinked = null)
        : base("Banking", "Bank Statement, balances, transfers, Ledger Mapping and workbook authority reports")
    {
        _repository = repository;
        _importService = importService;
        _reconciliationService = reconciliationService;
        _transferService = transferService;
        _mappingService = mappingService;
        _cashEntryService = cashEntryService;
        _reportService = reportService;
        _masterRepository = masterRepository;
        _ledgerTotalService = ledgerTotalService;
        _advancedClosingCashRepairService = advancedClosingCashRepairService;
        _clientContext = clientContext;
        _activityRepository = activityRepository;
        _actionRequiredRepository.Initialize();
        _activityContext = activityContext;
        _tallyExportSelectionContext = tallyExportSelectionContext;
        _pdfPipelineService = pdfPipelineService ?? new PdfBankStatementPipelineService();
        _bankOcrService = new BankOcrExcelConversionService();
        _liveChangeHub = liveChangeHub;
        _workspaceStateCache = workspaceStateCache;
        _clientPreferences = clientPreferences;
        _automationService = automationService;
        _transactionAutomationService = transactionAutomationService ?? throw new ArgumentNullException(nameof(transactionAutomationService));
        _tallyBankStatementReconciliationService = tallyBankStatementReconciliationService;
        _isTallyConnected = isTallyConnected ?? (() => false);
        _isCurrentClientTallyLinked = isCurrentClientTallyLinked ?? (() => false);
        _governanceRepository = new SqliteGovernanceRepository();
        _governanceRepository.Initialize();
        _advancedReconciliationService = new AdvancedBankReconciliationService(_governanceRepository);
        _ledgerMappingShowMapped = showMappedByDefault ? "YES" : "NO";
        _clientContext.PropertyChanged += OnClientContextPropertyChanged;
        _tallyExportSelectionContext.Changed += OnTallyExportSelectionChanged;
        _liveChangeHub.Changed += OnLiveDataChanged;

        RefreshCommand = new RelayCommand(RefreshAndReconcileTally);
        OpenBankStatementCommand = new RelayCommand(OpenBankStatementWorkspace, () => CurrentClientId > 0);
        OpenBankAccountsCommand = new RelayCommand(OpenBankAccountsWorkspace, () => CurrentClientId > 0);
        OpenLedgerMappingCommand = new RelayCommand(OpenLedgerMappingWorkspace, () => CurrentClientId > 0 && CanViewLedgerMapping);
        OpenBalanceReviewCommand = new RelayCommand(OpenBalanceReviewWorkspace, () => CurrentClientId > 0);
        CloseBankAccountPopupCommand = new RelayCommand(CloseBankAccountPopup);
        CloseLedgerMappingDetailCommand = new RelayCommand(CloseLedgerMappingDetail);
        CloseBalanceReviewDetailCommand = new RelayCommand(CloseBalanceReviewDetail);
        ClearFinalLedgerCommand = new RelayCommand(ClearSelectedFinalLedger, () => SelectedLedgerMappingRow is not null && CanEditLedgerMapping);
        PutSuggestedNamesCommand = new RelayCommand(PutSuggestedNames, () => CurrentClientId > 0 && CanEditLedgerMapping);
        OpenBulkSelectedMappingCommand = new RelayCommand(OpenBulkSelectedMapping, () => CurrentClientId > 0 && LedgerMappingSelectedActionCount > 0 && !IsBusy && CanEditLedgerMapping);
        CloseBulkSelectedMappingCommand = new RelayCommand(CloseBulkSelectedMapping);
        ApplyBulkMappingFiltersCommand = new RelayCommand(ApplyBulkMappingFilters, () => IsBulkLedgerMappingOpen && !IsBusy);
        ClearBulkMappingFiltersCommand = new RelayCommand(ClearBulkMappingFilters, () => IsBulkLedgerMappingOpen && !IsBusy);
        ApplyBulkSelectedMappingsCommand = new AsyncRelayCommand(ApplyBulkSelectedMappingsAsync, () => CurrentClientId > 0 && IsBulkLedgerMappingOpen && _bulkSelectedAtomicMappings.Count > 0 && !IsBusy && CanEditLedgerMapping);
        RemoveBulkSelectedFinalLedgersCommand = new AsyncRelayCommand(RemoveBulkSelectedFinalLedgersAsync, () => CurrentClientId > 0 && IsBulkLedgerMappingOpen && _bulkSelectedAtomicMappings.Any(mapping => mapping.TransactionIds.Any(id => id > 0)) && !IsBusy && CanEditLedgerMapping);
        CreateEmptyLedgerManualMasterCommand = new RelayCommand(CreateEmptyLedgerManualMaster, () => CurrentClientId > 0 && CanEditLedgerMapping);
        UseEmptyLedgerMasterCommand = new RelayCommand(UseEmptyLedgerMaster, () => CurrentClientId > 0 && CanEditLedgerMapping);
        ImportBankStatementsCommand = new AsyncRelayCommand(ImportBankStatementsAsync, () => CurrentClientId > 0 && !IsBusy && CanImportBanking);
        ImportPdfStatementsCommand = new AsyncRelayCommand(ImportPdfStatementsAsync, () => CurrentClientId > 0 && !IsBusy && CanImportBanking);
        ExportSuspenseCommand = new RelayCommand(ExportSuspenseWorkbook, () => CurrentClientId > 0 && !IsBusy && CanExportAccounting);
        ImportSuspenseCommand = new RelayCommand(ImportSuspenseWorkbook, () => CurrentClientId > 0 && !IsBusy && CanEditLedgerMapping);
        SaveAccountCommand = new RelayCommand(SaveAccount, () => SelectedAccount is not null && CanEditAccounting);
        ApplyBankRenameCommand = new RelayCommand(ApplyBankRename, () => SelectedBankRename is not null && CanEditAccounting);
        VerifyOpeningBalanceCommand = new RelayCommand(VerifySelectedOpeningBalance, () => SelectedObVerificationRow is not null && CanEditAccounting);
        UseSuggestedObCommand = new RelayCommand(UseSuggestedOpeningBalanceForSelected, () => SelectedObVerificationRow?.SuggestedOpeningBalance is not null && CanEditAccounting);
        SkipOpeningBalanceCommand = new RelayCommand(SkipSelectedOpeningBalance, () => SelectedObVerificationRow is not null && CanEditAccounting);
        DeleteObStatementCommand = new RelayCommand(DeleteSelectedObStatement, () => SelectedObVerificationRow is not null && CanDeleteAccounting);
        SaveTransactionCommand = new RelayCommand(SaveTransaction, () => SelectedTransaction is not null && CanEditAccounting);
        OpenManualCashContraCommand = new RelayCommand(OpenManualCashContra, () => SelectedTransaction is not null && CanEditAccounting);
        SaveManualCashContraCommand = new AsyncRelayCommand(SaveManualCashContraAsync, () => IsManualCashContraMode && ManualCashContraPreviewRows.Count > 0 && !IsBusy && CanEditAccounting);
        CancelManualCashContraCommand = new RelayCommand(CancelManualCashContra, () => IsManualCashContraMode);
        VerifyStatementCommand = new RelayCommand(VerifyStatement, () => SelectedStatement is not null && CanEditAccounting);
        UseSuggestedOpeningCommand = new RelayCommand(UseSuggestedOpening, () => SelectedStatement is not null && CanEditAccounting);
        SkipStatementCommand = new RelayCommand(SkipStatement, () => SelectedStatement is not null && CanEditAccounting);
        DeleteStatementCommand = new RelayCommand(DeleteStatement, () => SelectedStatement is not null && CanDeleteAccounting);
        ReconcileCommand = new RelayCommand(Reconcile, () => CurrentClientId > 0 && CanEditAccounting);
        MatchTransfersCommand = new RelayCommand(MatchTransfers, () => CurrentClientId > 0 && CanEditAccounting);
        RebuildMappingsCommand = new AsyncRelayCommand(RebuildMappingsAsync, () => CurrentClientId > 0 && !IsBusy && CanEditLedgerMapping);
        ApplyReadyMappingsCommand = new AsyncRelayCommand(ApplyReadyMappingsAsync, () => CurrentClientId > 0 && !IsBusy && CanEditLedgerMapping);
        ApplyMappingCommand = new RelayCommand(ApplyMapping, () => SelectedLedgerMappingRow is not null && CanEditLedgerMapping);
        ApplyLedgerMappingDetailSelectionCommand = new RelayCommand(ApplyLedgerMappingDetailSelection);
        AddCashEntryCommand = new RelayCommand(AddCashEntry, () => CurrentClientId > 0 && CanEditAccounting);
        SaveCashVoucherCommand = new RelayCommand(SaveCashVoucher, () => IsCashVoucherEditorOpen && CashVoucherDraft is not null && CanEditAccounting);
        CancelCashVoucherCommand = new RelayCommand(CancelCashVoucher, () => IsCashVoucherEditorOpen);
        ExportCashExcelFormatCommand = new RelayCommand(ExportCashExcelFormat, () => CanExportAccounting);
        SaveCashEntryCommand = new RelayCommand(SaveCashEntry, () => CanEditAccounting && SelectedCashEntry is not null && SelectedCashEntry.Id > 0);
        DeleteCashEntryCommand = new RelayCommand(DeleteCashEntry, () => CanDeleteAccounting && SelectedCashEntry is not null && SelectedCashEntry.Id > 0);
        RepairAdvancedClosureCashCommand = new RelayCommand(RepairAdvancedClosureCashEntries, () => CurrentClientId > 0 && CanEditAccounting);
        CloneCashEntryCommand = new RelayCommand(CloneCashEntry, () => CanEditAccounting && SelectedCashEntry is not null && SelectedCashEntry.Id > 0);
        ReverseCashEntryCommand = new RelayCommand(ReverseCashEntry, () => CanEditAccounting && SelectedCashEntry is not null && SelectedCashEntry.Id > 0);
        MakeCashRecurringCommand = new RelayCommand(MakeCashRecurring, () => CanEditAccounting && SelectedCashEntry is not null && SelectedCashEntry.Id > 0);
        ApproveCashEntryCommand = new RelayCommand(ApproveCashEntry, () => CanApproveAccounting && SelectedCashEntry is not null && SelectedCashEntry.Id > 0);
        SaveCashSettingsCommand = new RelayCommand(SaveCashSettings, () => CurrentClientId > 0 && CanEditAccounting);
        RebuildCashMappingsCommand = new RelayCommand(BuildCashEntryMappingRows, () => CurrentClientId > 0 && CanEditAccounting);
        ApplyCashMappingCommand = new RelayCommand(ApplyCashEntryMapping, () => CurrentClientId > 0 && CanEditAccounting);
        Refresh();
    }

    public BulkObservableCollection<BankAccountRecord> Accounts { get; } = [];
    public BulkObservableCollection<BankStatementRecord> Statements { get; } = [];
    public ObservableCollection<BankStatementImportIssue> ImportIssues { get; } = [];
    public BulkObservableCollection<BankTransactionRecord> Transactions { get; } = [];
    public BulkObservableCollection<BankTransactionRecord> FilteredTransactions { get; } = [];
    public BulkObservableCollection<BankTransactionRecord> ManualCashContraPreviewRows { get; } = [];
    public BulkObservableCollection<BankReconciliationSummary> ReconciliationRows { get; } = [];
    public BulkObservableCollection<BankTransferMatch> TransferMatches { get; } = [];
    public BulkObservableCollection<TallyBankStatementLinkRecord> TallyInterbankVerificationRows { get; } = [];
    public BulkObservableCollection<BankLedgerMapping> LedgerMappings { get; } = [];
    public BulkObservableCollection<CashEntryRecord> CashEntries { get; } = [];
    public BulkObservableCollection<LedgerPickerOption> LedgerPickerOptions { get; } = [];
    public BulkObservableCollection<string> MasterLedgerNames { get; } = [];
    public BulkObservableCollection<string> MasterGroupNames { get; } = [];
    public ObservableCollection<string> BulkLedgerGroupNames { get; } = [];
    public BulkObservableCollection<string> CashInHandLedgerNames { get; } = [];
    public ObservableCollection<BankLedgerRenameOption> BankLedgerRenameOptions { get; } = [];
    public ObservableCollection<BankRenameRow> BankRenameRows { get; } = [];
    public ObservableCollection<BankMissingPeriodGroupRow> MissingPeriodBankGroups { get; } = [];
    public ObservableCollection<ObVerificationRow> PendingObVerificationRows { get; } = [];
    public ObservableCollection<ObVerificationRow> SkippedObVerificationRows { get; } = [];
    public ObservableCollection<ObVerificationRow> VerifiedObVerificationRows { get; } = [];
    public ObservableCollection<string> ObFilterValues { get; } = [];

    public BulkObservableCollection<LedgerMappingWorkbenchRow> LedgerMappingRows { get; } = [];
    public BulkObservableCollection<LedgerMappingWorkbenchRow> FilteredLedgerMappingRows { get; } = [];
    public ObservableCollection<LedgerMappingDetailRow> LedgerMappingDetails { get; } = [];
    public ObservableCollection<LedgerMappingDetailRow> FilteredLedgerMappingDetails { get; } = [];
    public ObservableCollection<LedgerMappingWorkbenchRow> BulkLedgerMappingRows { get; } = [];
    public ObservableCollection<BulkVoucherTypeMappingRow> BulkVoucherTypeMappingRows { get; } = [];
    public ObservableCollection<string> LedgerMappingBankOptions { get; } = [];
    public ObservableCollection<string> LedgerMappingMethodOptions { get; } = [];
    public ObservableCollection<string> BulkMappingBankOptions { get; } = [];

    public ObservableCollection<CashEntryMasterMappingRow> CashEntryMasterMappings { get; } = [];
    public ObservableCollection<CashEntryCommonMappingRow> CashEntryCommonMappings { get; } = [];

    public ObservableCollection<StatementMetadataRow> StatementMetadataReport { get; } = [];
    public ObservableCollection<TransactionRegistryRow> TransactionRegistryReport { get; } = [];
    public ObservableCollection<TransferMapRow> TransferMapReport { get; } = [];
    public ObservableCollection<ContraDebugRow> ContraDebugReport { get; } = [];
    public ObservableCollection<ChainReportRow> ChainReport { get; } = [];
    public ObservableCollection<ChainAccountSummaryRow> ChainAccountSummary { get; } = [];
    public ObservableCollection<DuplicateReportRow> DuplicateReport { get; } = [];
    public ObservableCollection<ReportMetricRow> RegistryReport { get; } = [];
    public ObservableCollection<IntegrityReportRow> IntegrityReport { get; } = [];
    public ObservableCollection<ImportLogRow> ImportLogReport { get; } = [];
    public ObservableCollection<BalanceAuthorityReportRow> BalanceAuthorityReport { get; } = [];
    public ObservableCollection<ImportPerformanceLogRow> ImportPerformanceLogReport { get; } = [];
    public ObservableCollection<CashEntryRegistryRow> CashEntryRegistryReport { get; } = [];

    public IReadOnlyList<string> BankModes { get; } = ["Payment", "Receipt", "Contra"];
    public IReadOnlyList<string> BankStatementFilterColumns { get; } = ["ALL COLUMNS", "Date", "Narration", "Debit", "Credit", "Running Balance", "Mode", "Ledger", "Bank"];
    public IReadOnlyList<string> LedgerMappingColumnFilterOptions { get; } = ["ALL COLUMNS", "Count", "Summary Name", "Type", "Existing Ledger Match", "Suggested Match", "Final Ledger", "Ledger Group", "Status"];
    public IReadOnlyList<string> LedgerMappingDetailFilterOptions { get; } = ["ALL COLUMNS", "Date", "Narration", "Debit", "Credit", "Mode", "Method", "Ledger", "Bank", "Row No"];
    public IReadOnlyList<string> CashLayouts { get; } = ["Debit-Credit", "Single Amount"];
    public IReadOnlyList<string> LedgerMappingVoucherOptions { get; } = ["ALL TYPES", "Payment", "Receipt"];
    public IReadOnlyList<string> CashEntryMappingModes { get; } = ["Master Update", "Map Common Ledger"];
    public IReadOnlyList<string> ObFilterTypes { get; } = ["Client Name", "File Name", "Account Number"];
    public IReadOnlyList<string> BulkMappingVoucherTypeOptions { get; } = ["All", "Payment", "Receipt"];
    public IReadOnlyList<string> BulkMappingAmountOperatorOptions { get; } = ["Any", "Exact", "Greater Than", "Less Than", "Between"];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenBankStatementCommand { get; }
    public RelayCommand OpenBankAccountsCommand { get; }
    public RelayCommand OpenLedgerMappingCommand { get; }
    public RelayCommand OpenBalanceReviewCommand { get; }
    public RelayCommand CloseBankAccountPopupCommand { get; }
    public RelayCommand CloseLedgerMappingDetailCommand { get; }
    public RelayCommand CloseBalanceReviewDetailCommand { get; }
    public RelayCommand ClearFinalLedgerCommand { get; }
    public RelayCommand PutSuggestedNamesCommand { get; }
    public RelayCommand OpenBulkSelectedMappingCommand { get; }
    public RelayCommand CloseBulkSelectedMappingCommand { get; }
    public RelayCommand ApplyBulkMappingFiltersCommand { get; }
    public RelayCommand ClearBulkMappingFiltersCommand { get; }
    public AsyncRelayCommand ApplyBulkSelectedMappingsCommand { get; }
    public AsyncRelayCommand RemoveBulkSelectedFinalLedgersCommand { get; }
    public RelayCommand CreateEmptyLedgerManualMasterCommand { get; }
    public RelayCommand UseEmptyLedgerMasterCommand { get; }
    public AsyncRelayCommand ImportBankStatementsCommand { get; }
    public AsyncRelayCommand ImportPdfStatementsCommand { get; }
    public RelayCommand ExportSuspenseCommand { get; }
    public RelayCommand ImportSuspenseCommand { get; }
    public RelayCommand SaveAccountCommand { get; }
    public RelayCommand ApplyBankRenameCommand { get; }
    public RelayCommand VerifyOpeningBalanceCommand { get; }
    public RelayCommand UseSuggestedObCommand { get; }
    public RelayCommand SkipOpeningBalanceCommand { get; }
    public RelayCommand DeleteObStatementCommand { get; }
    public RelayCommand SaveTransactionCommand { get; }
    public RelayCommand OpenManualCashContraCommand { get; }
    public AsyncRelayCommand SaveManualCashContraCommand { get; }
    public RelayCommand CancelManualCashContraCommand { get; }
    public RelayCommand VerifyStatementCommand { get; }
    public RelayCommand UseSuggestedOpeningCommand { get; }
    public RelayCommand SkipStatementCommand { get; }
    public RelayCommand DeleteStatementCommand { get; }
    public RelayCommand ReconcileCommand { get; }
    public RelayCommand MatchTransfersCommand { get; }
    public AsyncRelayCommand RebuildMappingsCommand { get; }
    public AsyncRelayCommand ApplyReadyMappingsCommand { get; }
    public RelayCommand ApplyMappingCommand { get; }
    public RelayCommand ApplyLedgerMappingDetailSelectionCommand { get; }
    public RelayCommand AddCashEntryCommand { get; }
    public RelayCommand SaveCashVoucherCommand { get; }
    public RelayCommand CancelCashVoucherCommand { get; }
    public RelayCommand ExportCashExcelFormatCommand { get; }
    public RelayCommand SaveCashEntryCommand { get; }
    public RelayCommand DeleteCashEntryCommand { get; }
    public RelayCommand RepairAdvancedClosureCashCommand { get; }
    public RelayCommand CloneCashEntryCommand { get; }
    public RelayCommand ReverseCashEntryCommand { get; }
    public RelayCommand MakeCashRecurringCommand { get; }
    public RelayCommand ApproveCashEntryCommand { get; }
    public RelayCommand SaveCashSettingsCommand { get; }
    public RelayCommand RebuildCashMappingsCommand { get; }
    public RelayCommand ApplyCashMappingCommand { get; }

    public BankAccountRecord? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value)) return;
            SaveAccountCommand.RaiseCanExecuteChanged();
            LoadSelectedBankAccountWorkspace();
            RefreshBankBalanceSnapshot();
        }
    }

    public BankTransactionRecord? SelectedTransaction
    {
        get => _selectedTransaction;
        set
        {
            if (!SetProperty(ref _selectedTransaction, value)) return;
            SaveTransactionCommand.RaiseCanExecuteChanged();
            OpenManualCashContraCommand.RaiseCanExecuteChanged();
            SaveManualCashContraCommand.RaiseCanExecuteChanged();
            if (IsManualCashContraMode && value is null) CancelManualCashContra();
            if (value is not null)
                SelectedAccount = Accounts.FirstOrDefault(item => item.Id == value.BankAccountId);
        }
    }

    public bool IsManualCashContraMode
    {
        get => _isManualCashContraMode;
        private set
        {
            if (!SetProperty(ref _isManualCashContraMode, value)) return;
            SaveManualCashContraCommand.RaiseCanExecuteChanged();
            CancelManualCashContraCommand.RaiseCanExecuteChanged();
        }
    }

    public string ManualCashContraLedger
    {
        get => _manualCashContraLedger;
        set => SetProperty(ref _manualCashContraLedger, value?.Trim() ?? string.Empty);
    }

    public string ManualCashContraStatusText
    {
        get => _manualCashContraStatusText;
        private set => SetProperty(ref _manualCashContraStatusText, value);
    }

    public string ManualCashContraStepText
    {
        get => _manualCashContraStepText;
        private set => SetProperty(ref _manualCashContraStepText, value);
    }

    public string ManualCashContraSourceText
    {
        get => _manualCashContraSourceText;
        private set => SetProperty(ref _manualCashContraSourceText, value);
    }

    public int ManualCashContraSelectedCount => ManualCashContraPreviewRows.Count;

    public BankStatementRecord? SelectedStatement
    {
        get => _selectedStatement;
        set
        {
            if (!SetProperty(ref _selectedStatement, value)) return;
            VerifyStatementCommand.RaiseCanExecuteChanged();
            UseSuggestedOpeningCommand.RaiseCanExecuteChanged();
            SkipStatementCommand.RaiseCanExecuteChanged();
            DeleteStatementCommand.RaiseCanExecuteChanged();
        }
    }

    public BankReconciliationSummary? SelectedReconciliationRow
    {
        get => _selectedReconciliationRow;
        set
        {
            if (!SetProperty(ref _selectedReconciliationRow, value)) return;
            IsBalanceReviewDetailOpen = value is not null;
            if (value is not null)
            {
                SelectedStatement = Statements.FirstOrDefault(item => item.Id == value.StatementId);
                if (SelectedAccount?.Id != value.BankAccountId)
                    SelectedAccount = Accounts.FirstOrDefault(item => item.Id == value.BankAccountId);
            }
        }
    }

    public StatementMetadataRow? SelectedStatementMetadata
    {
        get => _selectedStatementMetadata;
        set
        {
            if (!SetProperty(ref _selectedStatementMetadata, value)) return;
            SelectedStatement = value is null ? null : Statements.FirstOrDefault(item => item.Id == value.StatementDatabaseId);
            SelectedAccount = value is null ? null : Accounts.FirstOrDefault(item => item.Id == value.BankAccountDatabaseId);
        }
    }

    public LedgerMappingWorkbenchRow? SelectedLedgerMappingRow
    {
        get => _selectedLedgerMappingRow;
        set
        {
            if (!SetProperty(ref _selectedLedgerMappingRow, value)) return;
            ApplyMappingCommand.RaiseCanExecuteChanged();
            ApplyLedgerMappingDetailSelectionCommand.RaiseCanExecuteChanged();
            ClearFinalLedgerCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(SelectedLedgerSummary));
        }
    }

    public LedgerMappingDetailRow? SelectedLedgerMappingDetail
    {
        get => _selectedLedgerMappingDetail;
        set => SetProperty(ref _selectedLedgerMappingDetail, value);
    }

    public bool LedgerMappingDetailAllRowsSelected
    {
        get => FilteredLedgerMappingDetails.Count > 0 && FilteredLedgerMappingDetails.All(detail => detail.IsSelected);
        set
        {
            // The header selector acts only on the rows currently visible after the
            // expanded Summary Name search/filter. Hidden rows keep their selection.
            foreach (var detail in FilteredLedgerMappingDetails) detail.IsSelected = value;
            RefreshLedgerMappingDetailSelectionState();
        }
    }

    public int LedgerMappingDetailSelectedCount => LedgerMappingDetails.Count(detail => detail.IsSelected);
    public int LedgerMappingDetailVisibleCount => FilteredLedgerMappingDetails.Count;
    public string LedgerMappingDetailSelectionText => $"{LedgerMappingDetailSelectedCount:N0} selected • {LedgerMappingDetailVisibleCount:N0} visible of {LedgerMappingDetails.Count:N0}";
    public string LedgerMappingDetailFilterCountText => $"{LedgerMappingDetailVisibleCount:N0} of {LedgerMappingDetails.Count:N0}";

    public string LedgerMappingDetailFilterColumn
    {
        get => _ledgerMappingDetailFilterColumn;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "ALL COLUMNS" : value.Trim();
            if (SetProperty(ref _ledgerMappingDetailFilterColumn, normalized)) ApplyLedgerMappingDetailFilter();
        }
    }

    public string LedgerMappingDetailFilterText
    {
        get => _ledgerMappingDetailFilterText;
        set
        {
            if (SetProperty(ref _ledgerMappingDetailFilterText, value ?? string.Empty)) ApplyLedgerMappingDetailFilter();
        }
    }

    public void ClearLedgerMappingDetailFilter()
    {
        _ledgerMappingDetailFilterColumn = "ALL COLUMNS";
        _ledgerMappingDetailFilterText = string.Empty;
        OnPropertyChanged(nameof(LedgerMappingDetailFilterColumn));
        OnPropertyChanged(nameof(LedgerMappingDetailFilterText));
        ApplyLedgerMappingDetailFilter();
    }

    public void ClearLedgerMappingDetailSelection(bool visibleOnly)
    {
        var rows = visibleOnly ? FilteredLedgerMappingDetails : LedgerMappingDetails;
        foreach (var detail in rows) detail.IsSelected = false;
        RefreshLedgerMappingDetailSelectionState();
    }

    public void RefreshLedgerMappingDetailSelectionState()
    {
        OnPropertyChanged(nameof(LedgerMappingDetailAllRowsSelected));
        OnPropertyChanged(nameof(LedgerMappingDetailSelectedCount));
        OnPropertyChanged(nameof(LedgerMappingDetailVisibleCount));
        OnPropertyChanged(nameof(LedgerMappingDetailSelectionText));
        OnPropertyChanged(nameof(LedgerMappingDetailFilterCountText));
        ApplyMappingCommand.RaiseCanExecuteChanged();
        ApplyLedgerMappingDetailSelectionCommand.RaiseCanExecuteChanged();
    }

    public BankRenameRow? SelectedBankRename
    {
        get => _selectedBankRename;
        set
        {
            if (!SetProperty(ref _selectedBankRename, value)) return;
            if (_selectedBankRenameOption is not null)
            {
                _selectedBankRenameOption = null;
                OnPropertyChanged(nameof(SelectedBankRenameOption));
            }
            ApplyBankRenameCommand.RaiseCanExecuteChanged();
            if (value is not null && SelectedAccount?.Id != value.BankAccountId)
                SelectedAccount = Accounts.FirstOrDefault(item => item.Id == value.BankAccountId);
        }
    }

    public BankLedgerRenameOption? SelectedBankRenameOption
    {
        get => _selectedBankRenameOption;
        set
        {
            if (!SetProperty(ref _selectedBankRenameOption, value)) return;
            if (value is not null && SelectedBankRename is not null)
            {
                // Dedicated selection authority for Bank Rename. Do not depend on an
                // editable ComboBox Text binding to survive popup focus changes.
                SelectedBankRename.RenameTo = value.Name.Trim();
                StatusText = $"Rename target selected: {value.Name} ({value.Source}).";
            }
        }
    }

    public ObVerificationRow? SelectedObVerificationRow
    {
        get => _selectedObVerificationRow;
        set
        {
            if (!SetProperty(ref _selectedObVerificationRow, value)) return;
            VerifyOpeningBalanceCommand.RaiseCanExecuteChanged();
            UseSuggestedObCommand.RaiseCanExecuteChanged();
            SkipOpeningBalanceCommand.RaiseCanExecuteChanged();
            DeleteObStatementCommand.RaiseCanExecuteChanged();
        }
    }

    public CashEntryRecord? CashVoucherDraft
    {
        get => _cashVoucherDraft;
        private set
        {
            if (!SetProperty(ref _cashVoucherDraft, value)) return;
            SaveCashVoucherCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsCashVoucherEditorOpen
    {
        get => _isCashVoucherEditorOpen;
        private set
        {
            if (!SetProperty(ref _isCashVoucherEditorOpen, value)) return;
            SaveCashVoucherCommand.RaiseCanExecuteChanged();
            CancelCashVoucherCommand.RaiseCanExecuteChanged();
        }
    }

    public CashEntryRecord? SelectedCashEntry
    {
        get => _selectedCashEntry;
        set
        {
            var previous = _selectedCashEntry;
            if (!SetProperty(ref _selectedCashEntry, value)) return;
            if (previous is not null && !ReferenceEquals(previous, value))
                previous.SetClosingBalanceSuggestion(null);
            SaveCashEntryCommand.RaiseCanExecuteChanged();
            DeleteCashEntryCommand.RaiseCanExecuteChanged();
            CloneCashEntryCommand.RaiseCanExecuteChanged();
            ReverseCashEntryCommand.RaiseCanExecuteChanged();
            MakeCashRecurringCommand.RaiseCanExecuteChanged();
            ApproveCashEntryCommand.RaiseCanExecuteChanged();
            QueueSelectedCashLedgerTotalRefresh(value);
        }
    }

    public CashEntryMasterMappingRow? SelectedCashMasterMapping
    {
        get => _selectedCashMasterMapping;
        set => SetProperty(ref _selectedCashMasterMapping, value);
    }

    public CashEntryCommonMappingRow? SelectedCashCommonMapping
    {
        get => _selectedCashCommonMapping;
        set => SetProperty(ref _selectedCashCommonMapping, value);
    }

    public LedgerTotalSnapshot? SelectedCashLedgerTotal
    {
        get => _selectedCashLedgerTotal;
        private set
        {
            if (!SetProperty(ref _selectedCashLedgerTotal, value)) return;
            OnPropertyChanged(nameof(HasSelectedCashLedgerTotal));
            OnPropertyChanged(nameof(SelectedCashLedgerName));
            OnPropertyChanged(nameof(SelectedCashLedgerGroup));
            OnPropertyChanged(nameof(SelectedCashLedgerOpeningText));
            OnPropertyChanged(nameof(SelectedCashLedgerDebitText));
            OnPropertyChanged(nameof(SelectedCashLedgerCreditText));
            OnPropertyChanged(nameof(SelectedCashLedgerAsOnLabel));
            OnPropertyChanged(nameof(SelectedCashLedgerAsOnText));
            OnPropertyChanged(nameof(SelectedCashLedgerClosingText));
            OnPropertyChanged(nameof(SelectedCashLedgerActivityText));
        }
    }

    public string SelectedCashLedgerTotalStatus
    {
        get => _selectedCashLedgerTotalStatus;
        private set => SetProperty(ref _selectedCashLedgerTotalStatus, value);
    }

    public bool HasSelectedCashLedgerTotal => SelectedCashLedgerTotal is not null;
    public string SelectedCashLedgerName => SelectedCashLedgerTotal?.LedgerName ?? "No Ledger selected";
    public string SelectedCashLedgerGroup => string.IsNullOrWhiteSpace(SelectedCashLedgerTotal?.ParentGroup)
        ? "Select a Master Ledger"
        : SelectedCashLedgerTotal!.ParentGroup;
    public string SelectedCashLedgerOpeningText => LedgerPickerOption.FormatBalance(SelectedCashLedgerTotal?.OpeningBalance ?? 0m);
    public string SelectedCashLedgerDebitText => $"{(SelectedCashLedgerTotal?.DebitToDate ?? 0m):N2}";
    public string SelectedCashLedgerCreditText => $"{(SelectedCashLedgerTotal?.CreditToDate ?? 0m):N2}";
    public string SelectedCashLedgerAsOnLabel => SelectedCashLedgerTotal is null
        ? "Balance as on voucher date"
        : $"Balance as on {SelectedCashLedgerTotal.AsOnDate:dd.MM.yyyy}";
    public string SelectedCashLedgerAsOnText => LedgerPickerOption.FormatBalance(SelectedCashLedgerTotal?.BalanceAsOnDate ?? 0m);
    public string SelectedCashLedgerClosingText => LedgerPickerOption.FormatBalance(SelectedCashLedgerTotal?.ClosingBalance ?? 0m);
    public string SelectedCashLedgerActivityText => SelectedCashLedgerTotal is null
        ? string.Empty
        : $"FY movement: Dr {SelectedCashLedgerTotal.TotalDebit:N2} • Cr {SelectedCashLedgerTotal.TotalCredit:N2} • {SelectedCashLedgerTotal.MovementCount:N0} entr{(SelectedCashLedgerTotal.MovementCount == 1 ? "y" : "ies")}";

    public CashEntrySettings CashSettings { get => _cashSettings; private set => SetProperty(ref _cashSettings, value); }
    public bool CreateLedgerWhenApplying
    {
        get => _createLedgerWhenApplying;
        set
        {
            if (!SetProperty(ref _createLedgerWhenApplying, value)) return;
            PersistLedgerMappingPreference("CreateLedgerWhenApplying", value ? "YES" : "NO");
        }
    }

    public string CashLayoutMode
    {
        get => _cashLayoutMode;
        set
        {
            if (!SetProperty(ref _cashLayoutMode, value)) return;
            CashSettings.LayoutMode = value;
            OnPropertyChanged(nameof(IsSingleAmountLayout));
            OnPropertyChanged(nameof(IsDebitCreditLayout));
        }
    }

    public bool IsSingleAmountLayout
    {
        get => string.Equals(CashLayoutMode, "Single Amount", StringComparison.OrdinalIgnoreCase);
        set { if (value) CashLayoutMode = "Single Amount"; }
    }
    public bool IsDebitCreditLayout
    {
        get => !IsSingleAmountLayout;
        set { if (value) CashLayoutMode = "Debit-Credit"; }
    }

    public bool IncludeArchivedTransfers
    {
        get => _includeArchivedTransfers;
        set
        {
            if (SetProperty(ref _includeArchivedTransfers, value)) LoadTransactions();
        }
    }

    public string BankStatementDateScope
    {
        get => _bankStatementDateScope;
        set
        {
            var normalized = string.Equals(value, "Tally Export Dates", StringComparison.OrdinalIgnoreCase)
                ? "Tally Export Dates"
                : "Full Statement";
            if (!SetProperty(ref _bankStatementDateScope, normalized)) return;
            OnPropertyChanged(nameof(IsBankStatementFullStatement));
            OnPropertyChanged(nameof(IsBankStatementTallyExportDates));
            OnPropertyChanged(nameof(BankStatementDateScopeText));
            PersistLedgerMappingPreference("BankStatementDateScope", normalized);
            ApplyBankStatementFilters();
        }
    }

    public bool IsBankStatementFullStatement
    {
        get => !BankStatementDateScope.Equals("Tally Export Dates", StringComparison.OrdinalIgnoreCase);
        set { if (value) BankStatementDateScope = "Full Statement"; }
    }

    public bool IsBankStatementTallyExportDates
    {
        get => !IsBankStatementFullStatement;
        set { if (value) BankStatementDateScope = "Tally Export Dates"; }
    }

    public string BankStatementDateScopeText
    {
        get
        {
            if (IsBankStatementFullStatement)
                return $"Full Bank Statement • {Transactions.Count:N0} row(s) in the current archived-row setting.";

            return _tallyExportSelectionContext.TryGetBankRanges(CurrentClientId, CurrentFinancialYear, out _, out var description)
                ? $"Tally Export Dates • {description} • {FilteredTransactions.Count:N0} visible row(s)."
                : "Tally Export Dates • no Bank Statement export period is currently selected.";
        }
    }

    public string DefaultEmptyLedger
    {
        get => _defaultEmptyLedger;
        set
        {
            var normalized = AccountingText.DisplayName(value);
            if (normalized.Equals(BankingPreferenceKeys.LegacyDefaultEmptyLedgerName, StringComparison.OrdinalIgnoreCase))
                normalized = BankingPreferenceKeys.DefaultEmptyLedgerName;
            if (normalized.Length == 0) normalized = BankingPreferenceKeys.DefaultEmptyLedgerName;
            if (!SetProperty(ref _defaultEmptyLedger, normalized)) return;
            PersistLedgerMappingPreference("DefaultEmptyLedger", normalized);
            _emptyLedgerDraftName = normalized;
            OnPropertyChanged(nameof(EmptyLedgerDraftName));
            RefreshEmptyLedgerMasterPanelState();
            OnPropertyChanged(nameof(DefaultEmptyLedgerStatusText));
        }
    }

    public string DefaultEmptyLedgerStatusText =>
        $"Blank Bank Ledger exports use '{DefaultEmptyLedger}'. Existing mapped Ledgers are never overwritten.";

    public int EmptyLedgerFullStatementCount => Transactions.Count(IsEmptyLedgerForExport);

    public int EmptyLedgerExportPeriodCount
    {
        get
        {
            var emptyRows = Transactions.Where(IsEmptyLedgerForExport);
            if (!_tallyExportSelectionContext.TryGetBankRanges(CurrentClientId, CurrentFinancialYear, out var ranges, out _))
                return 0;
            return emptyRows.Count(row => ranges.Any(range => range.Contains(row.TransactionDate)));
        }
    }

    public string EmptyLedgerDraftName
    {
        get => _emptyLedgerDraftName;
        set
        {
            var normalized = AccountingText.DisplayName(value);
            if (!SetProperty(ref _emptyLedgerDraftName, normalized)) return;
            RefreshEmptyLedgerMasterPanelState();
            OnPropertyChanged(nameof(EmptyLedgerHierarchyText));
        }
    }

    public string EmptyLedgerDraftGroup
    {
        get => _emptyLedgerDraftGroup;
        set
        {
            var normalized = string.Equals(value?.Trim(), "Primary", StringComparison.OrdinalIgnoreCase)
                ? "Primary"
                : AccountingText.DisplayName(value);
            if (!SetProperty(ref _emptyLedgerDraftGroup, normalized)) return;
            RefreshEmptyLedgerMasterPanelState();
            OnPropertyChanged(nameof(EmptyLedgerHierarchyText));
        }
    }

    public string EmptyLedgerMasterStatusText
    {
        get => _emptyLedgerMasterStatusText;
        private set => SetProperty(ref _emptyLedgerMasterStatusText, value ?? string.Empty);
    }

    public string EmptyLedgerMasterSourceText
    {
        get => _emptyLedgerMasterSourceText;
        private set => SetProperty(ref _emptyLedgerMasterSourceText, value ?? string.Empty);
    }

    public string EmptyLedgerResolvedName
    {
        get => _emptyLedgerResolvedName;
        private set => SetProperty(ref _emptyLedgerResolvedName, value ?? string.Empty);
    }

    public string EmptyLedgerResolvedGroup
    {
        get => _emptyLedgerResolvedGroup;
        private set => SetProperty(ref _emptyLedgerResolvedGroup, value ?? string.Empty);
    }

    /// <summary>
    /// Third layer in the Empty Ledger hierarchy: Ledger -> Ledger Group -> Parent Group.
    /// This is display/validation metadata only; Master creation still remains explicit.
    /// </summary>
    public string EmptyLedgerResolvedParentGroup
    {
        get => _emptyLedgerResolvedParentGroup;
        private set => SetProperty(ref _emptyLedgerResolvedParentGroup, value ?? string.Empty);
    }

    public string EmptyLedgerGroupStatusText
    {
        get => _emptyLedgerGroupStatusText;
        private set => SetProperty(ref _emptyLedgerGroupStatusText, value ?? string.Empty);
    }

    public string EmptyLedgerParentGroupStatusText
    {
        get => _emptyLedgerParentGroupStatusText;
        private set => SetProperty(ref _emptyLedgerParentGroupStatusText, value ?? string.Empty);
    }

    public string EmptyLedgerHierarchyText
    {
        get
        {
            var ledger = EmptyLedgerResolvedName.Length > 0 ? EmptyLedgerResolvedName : AccountingText.DisplayName(EmptyLedgerDraftName);
            var group = EmptyLedgerResolvedGroup.Length > 0 ? EmptyLedgerResolvedGroup : AccountingText.DisplayName(EmptyLedgerDraftGroup);
            var parent = EmptyLedgerResolvedParentGroup;
            return $"{(ledger.Length > 0 ? ledger : "(Ledger)")}  ->  {(group.Length > 0 ? group : "(Group)")}  ->  {(parent.Length > 0 ? parent : "(Parent Group)")}";
        }
    }

    public bool HasExistingEmptyLedgerMaster => EmptyLedgerResolvedName.Length > 0;

    /// <summary>
    /// Reusable action-selection layer for Ledger Mapping. This is intentionally
    /// independent of the DataGrid's current cell/row selection so future bulk
    /// actions can reuse the checkboxes without changing keyboard navigation.
    /// </summary>
    public bool LedgerMappingAllRowsSelected
    {
        get => _ledgerMappingAllRowsSelected;
        set
        {
            if (_ledgerMappingAllRowsSelected == value) return;
            _ledgerMappingAllRowsSelected = value;
            OnPropertyChanged();
            if (_updatingLedgerMappingSelection) return;
            foreach (var row in FilteredLedgerMappingRows) row.IsSelected = value;
        }
    }

    public int LedgerMappingSelectedActionCount => FilteredLedgerMappingRows.Count(row => row.IsSelected);
    public bool CanImportBanking => HasGovernancePermission(GovernancePermissions.Import) &&
        (!NetworkClientRuntime.IsNetworkClient || !NetworkClientRuntime.AccountingAccessSuspended);
    public bool CanEditAccounting => DesktopPermissionService.CanMutateAccounting(_governanceRepository, GovernancePermissions.EditAccounting);
    public bool CanDeleteAccounting => DesktopPermissionService.CanMutateAccounting(_governanceRepository, GovernancePermissions.DeleteAccounting);
    public bool CanApproveAccounting => !NetworkClientRuntime.IsNetworkClient && HasGovernancePermission(GovernancePermissions.Approve);
    public bool CanExportAccounting => HasGovernancePermission(GovernancePermissions.ExportData) || HasGovernancePermission(GovernancePermissions.BankExport);
    public bool IsAccountingReadOnly => !CanEditAccounting;
    public string AccountingPermissionSummary => $"Role: {DesktopPermissionService.EffectiveRoleName(_governanceRepository)} • {(NetworkClientRuntime.IsNetworkClient ? "Windows processing node • " : string.Empty)}Import {(CanImportBanking ? "allowed" : "denied")} • Edit {(CanEditAccounting ? "allowed" : "denied")} • Delete {(CanDeleteAccounting ? "allowed" : "denied")}";

    public bool CanViewLedgerMapping => HasGovernancePermission(GovernancePermissions.LedgerMappingView) || CanEditLedgerMapping;
    public bool CanEditLedgerMapping => (!NetworkClientRuntime.IsNetworkClient || !NetworkClientRuntime.AccountingAccessSuspended) &&
        (HasGovernancePermission(GovernancePermissions.LedgerMappingEdit) ||
         HasGovernancePermission(GovernancePermissions.BankMap) ||
         HasGovernancePermission(GovernancePermissions.LedgerMapping));
    public bool IsLedgerMappingReadOnly => !CanEditLedgerMapping;
    public string LedgerMappingPermissionSummary => CanEditLedgerMapping
        ? "Ledger Mapping access: EDIT · Final Ledger/Group and mapping actions are enabled."
        : CanViewLedgerMapping
            ? "Ledger Mapping access: READ ONLY · suggestions, mappings and transaction membership can be inspected; all mapping changes are disabled."
            : "Ledger Mapping access: DENIED by role policy.";

    public void RefreshLedgerMappingActionSelectionState()
    {
        _updatingLedgerMappingSelection = true;
        try
        {
            var allSelected = FilteredLedgerMappingRows.Count == 0 || FilteredLedgerMappingRows.All(row => row.IsSelected);
            if (_ledgerMappingAllRowsSelected != allSelected)
            {
                _ledgerMappingAllRowsSelected = allSelected;
                OnPropertyChanged(nameof(LedgerMappingAllRowsSelected));
            }
            OnPropertyChanged(nameof(LedgerMappingSelectedActionCount));
            OpenBulkSelectedMappingCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            _updatingLedgerMappingSelection = false;
        }
    }

    private void SetLedgerMappingActionSelection(bool selected)
    {
        _updatingLedgerMappingSelection = true;
        try
        {
            foreach (var row in FilteredLedgerMappingRows) row.IsSelected = selected;
            _ledgerMappingAllRowsSelected = selected;
            OnPropertyChanged(nameof(LedgerMappingAllRowsSelected));
            OnPropertyChanged(nameof(LedgerMappingSelectedActionCount));
            OpenBulkSelectedMappingCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            _updatingLedgerMappingSelection = false;
        }
    }

    public bool IsBulkLedgerMappingOpen
    {
        get => _isBulkLedgerMappingOpen;
        private set
        {
            if (!SetProperty(ref _isBulkLedgerMappingOpen, value)) return;
            ApplyBulkSelectedMappingsCommand.RaiseCanExecuteChanged();
            RemoveBulkSelectedFinalLedgersCommand.RaiseCanExecuteChanged();
            SaveManualCashContraCommand.RaiseCanExecuteChanged();
            ApplyBulkMappingFiltersCommand.RaiseCanExecuteChanged();
            ClearBulkMappingFiltersCommand.RaiseCanExecuteChanged();
        }
    }

    public string BulkMappingFinalLedger
    {
        get => _bulkMappingFinalLedger;
        set => SetProperty(ref _bulkMappingFinalLedger, value ?? string.Empty);
    }

    public string BulkMappingLedgerGroup
    {
        get => _bulkMappingLedgerGroup;
        set => SetProperty(ref _bulkMappingLedgerGroup, value ?? string.Empty);
    }

    public string BulkMappingSelectionType
    {
        get => _bulkMappingSelectionType;
        private set => SetProperty(ref _bulkMappingSelectionType, value ?? string.Empty);
    }

    public string BulkMappingCaption
    {
        get => _bulkMappingCaption;
        private set => SetProperty(ref _bulkMappingCaption, value ?? string.Empty);
    }

    public string BulkMappingBankFilter
    {
        get => _bulkMappingBankFilter;
        set
        {
            var normalized = NormalizeBulkBankFilter(value);
            if (!SetProperty(ref _bulkMappingBankFilter, normalized)) return;
            OnPropertyChanged(nameof(BulkMappingHasActiveFilter));
            OnPropertyChanged(nameof(BulkMappingActionLabel));
            RefreshBulkMappingFiltersLive();
        }
    }

    public string BulkMappingVoucherTypeFilter
    {
        get => _bulkMappingVoucherTypeFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "All" : value.Trim();
            if (!SetProperty(ref _bulkMappingVoucherTypeFilter, normalized)) return;
            OnPropertyChanged(nameof(BulkMappingHasActiveFilter));
            OnPropertyChanged(nameof(BulkMappingActionLabel));
            RefreshBulkMappingFiltersLive();
        }
    }

    public bool BulkMappingShowMixedVoucherType
    {
        get => _bulkMappingShowMixedVoucherType;
        set
        {
            if (!SetProperty(ref _bulkMappingShowMixedVoucherType, value)) return;
            RefreshBulkMappingFiltersLive();
        }
    }

    public string BulkMappingAmountSearchText
    {
        get => _bulkMappingAmountSearchText;
        set
        {
            if (!SetProperty(ref _bulkMappingAmountSearchText, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(BulkMappingHasActiveFilter));
            OnPropertyChanged(nameof(BulkMappingActionLabel));
            RefreshBulkMappingFiltersLive();
        }
    }

    public string BulkMappingAmountOperator
    {
        get => _bulkMappingAmountOperator;
        set
        {
            var normalized = NormalizeBulkAmountOperator(value);
            if (!SetProperty(ref _bulkMappingAmountOperator, normalized)) return;

            var alreadySuppressed = _suppressBulkMappingLiveFilterRefresh;
            _suppressBulkMappingLiveFilterRefresh = true;
            try
            {
                if (normalized.Equals("Any", StringComparison.OrdinalIgnoreCase))
                {
                    BulkMappingAmountFromText = string.Empty;
                    BulkMappingAmountToText = string.Empty;
                }
                else if (!normalized.Equals("Between", StringComparison.OrdinalIgnoreCase))
                {
                    BulkMappingAmountToText = string.Empty;
                }
            }
            finally
            {
                _suppressBulkMappingLiveFilterRefresh = alreadySuppressed;
            }

            OnPropertyChanged(nameof(BulkMappingAmountUsesValue));
            OnPropertyChanged(nameof(BulkMappingAmountIsBetween));
            OnPropertyChanged(nameof(BulkMappingAmountPrimaryLabel));
            OnPropertyChanged(nameof(BulkMappingHasActiveFilter));
            OnPropertyChanged(nameof(BulkMappingActionLabel));
            RefreshBulkMappingFiltersLive();
        }
    }

    public bool BulkMappingAmountUsesValue => !BulkMappingAmountOperator.Equals("Any", StringComparison.OrdinalIgnoreCase);
    public bool BulkMappingAmountIsBetween => BulkMappingAmountOperator.Equals("Between", StringComparison.OrdinalIgnoreCase);
    public string BulkMappingAmountPrimaryLabel => BulkMappingAmountIsBetween ? "From" : "Amount";
    public bool BulkMappingHasActiveFilter =>
        !NormalizeBulkBankFilter(BulkMappingBankFilter).Equals("All Banks", StringComparison.OrdinalIgnoreCase) ||
        NormalizeBulkVoucherTypeScope(BulkMappingVoucherTypeFilter).Length > 0 ||
        !NormalizeBulkAmountOperator(BulkMappingAmountOperator).Equals("Any", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(BulkMappingAmountSearchText);
    // Fix131: main Ledger Mapping and Bulk Mapping expose the same action name and the
    // same exact-transaction commit semantics. Filters narrow the frozen scope; they do
    // not change Map All into a different operation.
    public string BulkMappingActionLabel => "Map All";
    public int BulkMappingTransactionCount => _bulkSelectedAtomicMappings
        .SelectMany(mapping => mapping.TransactionIds)
        .Where(id => id > 0)
        .Distinct()
        .Count();

    public string BulkMappingAmountFromText
    {
        get => _bulkMappingAmountFromText;
        set
        {
            if (!SetProperty(ref _bulkMappingAmountFromText, value ?? string.Empty)) return;
            RefreshBulkMappingFiltersLive();
        }
    }

    public string BulkMappingAmountToText
    {
        get => _bulkMappingAmountToText;
        set
        {
            if (!SetProperty(ref _bulkMappingAmountToText, value ?? string.Empty)) return;
            RefreshBulkMappingFiltersLive();
        }
    }

    public string BulkMappingFilterSummary
    {
        get => _bulkMappingFilterSummary;
        private set => SetProperty(ref _bulkMappingFilterSummary, value ?? string.Empty);
    }

    public bool BulkMappingIsSimple
    {
        get => _bulkMappingIsSimple;
        private set
        {
            if (!SetProperty(ref _bulkMappingIsSimple, value)) return;
            OnPropertyChanged(nameof(BulkMappingIsMixed));
        }
    }

    public bool BulkMappingIsMixed => !BulkMappingIsSimple;

    public string SelectedLedgerDetailStatistics
    {
        get
        {
            if (LedgerMappingDetails.Count == 0) return "0 transaction(s)";
            var debit = LedgerMappingDetails.Sum(item => item.Debit);
            var credit = LedgerMappingDetails.Sum(item => item.Credit);
            var first = LedgerMappingDetails.Min(item => item.Date);
            var last = LedgerMappingDetails.Max(item => item.Date);
            var banks = LedgerMappingDetails.Select(item => item.Bank).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return $"{LedgerMappingDetails.Count:N0} transaction(s) • Debit {debit:N2} • Credit {credit:N2} • {first:dd-MMM-yyyy} to {last:dd-MMM-yyyy} • {banks:N0} bank account(s)";
        }
    }

    public string BankStatementFilterColumn
    {
        get => _bankStatementFilterColumn;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "ALL COLUMNS" : value.Trim();
            if (SetProperty(ref _bankStatementFilterColumn, normalized)) ApplyBankStatementFilters();
        }
    }

    public string BankStatementFilterText
    {
        get => _bankStatementFilterText;
        set
        {
            var normalized = value ?? string.Empty;
            if (SetProperty(ref _bankStatementFilterText, normalized)) ApplyBankStatementFilters();
        }
    }

    public string LedgerMappingColumnFilter
    {
        get => _ledgerMappingColumnFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "ALL COLUMNS" : value.Trim();
            if (SetProperty(ref _ledgerMappingColumnFilter, normalized))
            {
                PersistLedgerMappingPreference("ColumnFilter", normalized);
                ApplyLedgerMappingFilters();
            }
        }
    }

    public string LedgerMappingTextFilter
    {
        get => _ledgerMappingTextFilter;
        set
        {
            var normalized = value ?? string.Empty;
            if (SetProperty(ref _ledgerMappingTextFilter, normalized))
            {
                PersistLedgerMappingPreference("TextFilter", normalized);
                ApplyLedgerMappingFilters();
            }
        }
    }

    public void ClearBankStatementFilter()
    {
        _bankStatementFilterColumn = "ALL COLUMNS";
        _bankStatementFilterText = string.Empty;
        OnPropertyChanged(nameof(BankStatementFilterColumn));
        OnPropertyChanged(nameof(BankStatementFilterText));
        ApplyBankStatementFilters();
    }

    public void ClearLedgerMappingTextFilter()
    {
        _ledgerMappingColumnFilter = "ALL COLUMNS";
        _ledgerMappingTextFilter = string.Empty;
        OnPropertyChanged(nameof(LedgerMappingColumnFilter));
        OnPropertyChanged(nameof(LedgerMappingTextFilter));
        PersistLedgerMappingPreference("ColumnFilter", _ledgerMappingColumnFilter);
        PersistLedgerMappingPreference("TextFilter", _ledgerMappingTextFilter);
        ApplyLedgerMappingFilters();
    }

    public void ApplyBankStatementHeaderFilter(string column, string? value)
    {
        BankStatementFilterColumn = string.IsNullOrWhiteSpace(column) ? "ALL COLUMNS" : column.Trim();
        BankStatementFilterText = value ?? string.Empty;
    }

    public void ApplyLedgerMappingHeaderFilter(string column, string? value)
    {
        LedgerMappingColumnFilter = string.IsNullOrWhiteSpace(column) ? "ALL COLUMNS" : column.Trim();
        LedgerMappingTextFilter = value ?? string.Empty;
    }

    public IReadOnlyList<string> GetBankStatementHeaderValues(string column)
    {
        static string Money(decimal value) => value.ToString("N2");

        IEnumerable<BankTransactionRecord> source = Transactions;
        if (BankStatementDateScope.Equals("Tally Export Dates", StringComparison.OrdinalIgnoreCase))
        {
            if (_tallyExportSelectionContext.TryGetBankRanges(CurrentClientId, CurrentFinancialYear, out var ranges, out _))
                source = source.Where(row => ranges.Any(range => range.Contains(row.TransactionDate)));
            else
                source = [];
        }

        IEnumerable<string> values = column.ToUpperInvariant() switch
        {
            "DATE" => source.Select(row => row.TransactionDate.ToString("dd.MM.yyyy")),
            "NARRATION" => source.Select(row => row.Narration ?? string.Empty),
            "DEBIT" => source.Select(row => Money(row.Debit)),
            "CREDIT" => source.Select(row => Money(row.Credit)),
            "RUNNING BALANCE" => source.Where(row => row.RunningBalance.HasValue).Select(row => Money(row.RunningBalance!.Value)),
            "MODE" => source.Select(row => row.Mode ?? string.Empty),
            "LEDGER" => source.Select(row => row.LedgerName ?? string.Empty),
            "BANK" => source.Select(row => row.BankAccountName ?? string.Empty),
            _ => []
        };
        return values.Select(value => value.Trim()).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> GetLedgerMappingHeaderValues(string column)
    {
        IEnumerable<string> values = column.ToUpperInvariant() switch
        {
            "COUNT" => LedgerMappingRows.Select(row => row.Count.ToString()),
            "SUMMARY NAME" => LedgerMappingRows.Select(row => row.SummaryName ?? string.Empty),
            "TYPE" => LedgerMappingRows.Select(row => row.Type ?? string.Empty),
            "EXACT MATCH" or "EXISTING LEDGER MATCH" => LedgerMappingRows.Select(row => row.ExactMatch ?? string.Empty),
            "SUGGESTED MATCH" => LedgerMappingRows.Select(row => row.SuggestedMatch ?? string.Empty),
            "FINAL LEDGER" => LedgerMappingRows.Select(row => row.FinalLedger ?? string.Empty),
            "LEDGER GROUP" => LedgerMappingRows.Select(row => row.LedgerGroup ?? string.Empty),
            "STATUS" => LedgerMappingRows.Select(row => row.Status ?? string.Empty),
            _ => []
        };
        return values.Select(value => value.Trim()).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void OpenPendingLedgerMappingFromDashboard()
    {
        if (!CanViewLedgerMapping)
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "view Ledger Mapping");
            return;
        }

        SelectedBankingTabIndex = 4;
        LedgerMappingShowMapped = "NO";
        PrepareLedgerMappingWorkspace();
    }

    public string LedgerMappingFilterCountText =>
        $"{FilteredLedgerMappingRows.Count:N0} group(s) shown • " +
        $"{_ledgerMappingVisibleTransactionCount:N0} visible / {_ledgerMappingExpectedTransactionCount:N0} expected transaction(s) • " +
        $"{LedgerMappingCoverageMatchState}";

    public string LedgerMappingCoverageMatchState =>
        !string.IsNullOrWhiteSpace(LedgerMappingTextFilter)
            ? "FILTERED"
            : _ledgerMappingVisibleTransactionCount == _ledgerMappingExpectedTransactionCount
                ? "MATCH"
                : "MISMATCH";

    public string LedgerMappingBankFilter
    {
        get => _ledgerMappingBankFilter;
        set
        {
            if (_isBuildingLedgerMappingOptions && string.IsNullOrWhiteSpace(value)) return;
            var normalized = string.IsNullOrWhiteSpace(value) ? "ALL BANKS" : value.Trim();
            if (SetProperty(ref _ledgerMappingBankFilter, normalized) && !_isBuildingLedgerMappingOptions)
            {
                PersistLedgerMappingPreference("BankFilter", normalized);
                CloseLedgerMappingPreview();
                // Excel applies Bank before visible grouping; rebuild only the lightweight view rows.
                BuildLedgerMappingRows(normalized, _ledgerMappingShowMapped, _ledgerMappingVoucherFilter);
            }
        }
    }

    public string LedgerMappingShowMapped
    {
        get => _ledgerMappingShowMapped;
        set
        {
            var normalized = NormalizeLedgerMappingShowMapped(value);
            if (SetProperty(ref _ledgerMappingShowMapped, normalized))
            {
                OnPropertyChanged(nameof(IsLedgerMappingUnmapped));
                OnPropertyChanged(nameof(IsLedgerMappingMapped));
                OnPropertyChanged(nameof(IsLedgerMappingAll));
                if (_isBuildingLedgerMappingOptions) return;
                PersistLedgerMappingPreference("ShowMapped", normalized);
                CloseLedgerMappingPreview();
                ApplyLedgerMappingFilters();
            }
        }
    }

    public bool IsLedgerMappingUnmapped
    {
        get => LedgerMappingShowMapped.Equals("NO", StringComparison.OrdinalIgnoreCase);
        set { if (value) LedgerMappingShowMapped = "NO"; }
    }
    public bool IsLedgerMappingMapped
    {
        get => LedgerMappingShowMapped.Equals("YES", StringComparison.OrdinalIgnoreCase);
        set { if (value) LedgerMappingShowMapped = "YES"; }
    }
    public bool IsLedgerMappingAll
    {
        get => LedgerMappingShowMapped.Equals("ALL", StringComparison.OrdinalIgnoreCase);
        set { if (value) LedgerMappingShowMapped = "ALL"; }
    }

    public string LedgerMappingVoucherFilter
    {
        get => _ledgerMappingVoucherFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "ALL TYPES" : value.Trim();
            if (SetProperty(ref _ledgerMappingVoucherFilter, normalized) && !_isBuildingLedgerMappingOptions)
            {
                PersistLedgerMappingPreference("VoucherFilter", normalized);
                CloseLedgerMappingPreview();
                ApplyLedgerMappingFilters();
            }
        }
    }

    public string LedgerMappingMethodFilter
    {
        get => _ledgerMappingMethodFilter;
        set
        {
            if (_isBuildingLedgerMappingOptions && string.IsNullOrWhiteSpace(value)) return;
            var normalized = NormalizeLedgerMappingMethodFilter(value);
            if (SetProperty(ref _ledgerMappingMethodFilter, normalized) && !_isBuildingLedgerMappingOptions)
            {
                PersistLedgerMappingPreference("MethodFilter", normalized);
                CloseLedgerMappingPreview();
                ApplyLedgerMappingFilters();
            }
        }
    }

    public string LedgerMappingDateScope
    {
        get => _ledgerMappingDateScope;
        set
        {
            var normalized = value.Equals("Tally Export Dates", StringComparison.OrdinalIgnoreCase)
                ? "Tally Export Dates"
                : "Full Statement";
            if (!SetProperty(ref _ledgerMappingDateScope, normalized))
            {
                return;
            }
            OnPropertyChanged(nameof(IsLedgerMappingFullStatement));
            OnPropertyChanged(nameof(IsLedgerMappingTallyExportDates));
            if (_isBuildingLedgerMappingOptions) return;
            PersistLedgerMappingPreference("DateScope", normalized);
            CloseLedgerMappingPreview();
            BuildLedgerMappingRows(
                _ledgerMappingBankFilter,
                _ledgerMappingShowMapped,
                _ledgerMappingVoucherFilter);
        }
    }

    public bool IsLedgerMappingFullStatement
    {
        get => !LedgerMappingDateScope.Equals("Tally Export Dates", StringComparison.OrdinalIgnoreCase);
        set { if (value) LedgerMappingDateScope = "Full Statement"; }
    }
    public bool IsLedgerMappingTallyExportDates
    {
        get => !IsLedgerMappingFullStatement;
        set { if (value) LedgerMappingDateScope = "Tally Export Dates"; }
    }

    public string LedgerMappingDateScopeText
    {
        get
        {
            var activeIds = Transactions
                .Where(item => item.Id > 0 && !item.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Id)
                .ToHashSet();
            var coveredIds = LedgerMappings
                .SelectMany(mapping => mapping.TransactionIds)
                .Where(id => id > 0 && activeIds.Contains(id))
                .Distinct()
                .ToArray();
            var mappedIds = LedgerMappings
                .Where(mapping => mapping.Status.Equals("Mapped", StringComparison.OrdinalIgnoreCase))
                .SelectMany(mapping => mapping.TransactionIds)
                .Where(id => id > 0 && activeIds.Contains(id))
                .Distinct()
                .ToHashSet();
            var pendingCount = coveredIds.Count(id => !mappedIds.Contains(id));
            var coverage = $"Bank Statement {activeIds.Count:N0} • Ledger Mapping {coveredIds.Length:N0} ({pendingCount:N0} pending + {mappedIds.Count:N0} mapped)";

            if (!LedgerMappingDateScope.Equals("Tally Export Dates", StringComparison.OrdinalIgnoreCase))
                return $"Showing the full Bank Statement. {coverage}.";

            return _tallyExportSelectionContext.TryGetBankRanges(
                CurrentClientId,
                CurrentFinancialYear,
                out _,
                out var description)
                ? $"Showing Tally export period(s): {description}. The grid/counts and Map All are limited to the exact Bank Statement transactions in that selected period. Full-statement coverage: {coverage}."
                : $"No Tally Bank Statement export dates are currently selected. Full-statement coverage: {coverage}.";
        }
    }

    public string CashEntryMappingMode
    {
        get => _cashEntryMappingMode;
        set
        {
            if (!SetProperty(ref _cashEntryMappingMode, value)) return;
            OnPropertyChanged(nameof(IsCashMasterUpdateMode));
            OnPropertyChanged(nameof(IsCashCommonLedgerMode));
        }
    }

    public bool IsCashMasterUpdateMode
    {
        get => string.Equals(CashEntryMappingMode, "Master Update", StringComparison.OrdinalIgnoreCase);
        set { if (value) CashEntryMappingMode = "Master Update"; }
    }
    public bool IsCashCommonLedgerMode
    {
        get => !IsCashMasterUpdateMode;
        set { if (value) CashEntryMappingMode = "Map Common Ledger"; }
    }

    public string SelectedCashCommonLedger
    {
        get => _selectedCashCommonLedger;
        set => SetProperty(ref _selectedCashCommonLedger, value);
    }

    public string ObFilterType
    {
        get => _obFilterType;
        set
        {
            if (!SetProperty(ref _obFilterType, value)) return;
            LoadObFilterValues();
            ApplyObVerificationFilter();
        }
    }

    public string ObFilterValue
    {
        get => _obFilterValue;
        set
        {
            if (SetProperty(ref _obFilterValue, value)) ApplyObVerificationFilter();
        }
    }

    public bool ShowVerifiedObRows
    {
        get => _showVerifiedObRows;
        set => SetProperty(ref _showVerifiedObRows, value);
    }

    public bool IsBankAccountPopupOpen
    {
        get => _isBankAccountPopupOpen;
        private set => SetProperty(ref _isBankAccountPopupOpen, value);
    }

    public bool IsLedgerMappingDetailOpen
    {
        get => _isLedgerMappingDetailOpen;
        private set
        {
            if (!SetProperty(ref _isLedgerMappingDetailOpen, value)) return;
            ApplyLedgerMappingDetailSelectionCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBalanceReviewDetailOpen
    {
        get => _isBalanceReviewDetailOpen;
        private set => SetProperty(ref _isBalanceReviewDetailOpen, value);
    }

    public int SelectedReconciliationViewIndex
    {
        get => _selectedReconciliationViewIndex;
        set => SetProperty(ref _selectedReconciliationViewIndex, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanImportBanking));
        OnPropertyChanged(nameof(CanEditAccounting));
        OnPropertyChanged(nameof(CanDeleteAccounting));
        OnPropertyChanged(nameof(CanApproveAccounting));
        OnPropertyChanged(nameof(CanExportAccounting));
        OnPropertyChanged(nameof(IsAccountingReadOnly));
        OnPropertyChanged(nameof(AccountingPermissionSummary));
        ImportBankStatementsCommand.RaiseCanExecuteChanged();
            ImportPdfStatementsCommand.RaiseCanExecuteChanged();
            ExportSuspenseCommand.RaiseCanExecuteChanged();
            ImportSuspenseCommand.RaiseCanExecuteChanged();
            RebuildMappingsCommand.RaiseCanExecuteChanged();
            ApplyReadyMappingsCommand.RaiseCanExecuteChanged();
            OpenBulkSelectedMappingCommand.RaiseCanExecuteChanged();
            ApplyBulkSelectedMappingsCommand.RaiseCanExecuteChanged();
            RemoveBulkSelectedFinalLedgersCommand.RaiseCanExecuteChanged();
            SaveManualCashContraCommand.RaiseCanExecuteChanged();
        }
    }

    public int SelectedBankingTabIndex
    {
        get => _selectedBankingTabIndex;
        set
        {
            if (!SetProperty(ref _selectedBankingTabIndex, value)) return;
            if (value == 4) PrepareLedgerMappingWorkspace();
            else if (value == 5) PrepareReportsWorkspace();
        }
    }

    public MasterRecord? SelectedBankMasterRecord
    {
        get => _selectedBankMasterRecord;
        private set
        {
            if (!SetProperty(ref _selectedBankMasterRecord, value)) return;
            OnPropertyChanged(nameof(SelectedBankMasterSourceDisplay));
        }
    }

    public string SelectedBankMasterSourceDisplay =>
        BankLedgerRenameOption.NormalizeSourceLabel(SelectedBankMasterRecord?.Source);

    public string SelectedBankCurrentName => SelectedAccount?.AuthorityName ?? string.Empty;
    public bool HasSelectedBankAccount => SelectedAccount is not null;
    public string BankBalanceAccountName { get => _bankBalanceAccountName; private set => SetProperty(ref _bankBalanceAccountName, value); }
    public decimal? BankTallyOpeningBalance { get => _bankTallyOpeningBalance; private set => SetProperty(ref _bankTallyOpeningBalance, value); }
    public decimal? BankOriginalOpeningBalance { get => _bankOriginalOpeningBalance; private set => SetProperty(ref _bankOriginalOpeningBalance, value); }
    public decimal BankImportedStatementDebits { get => _bankImportedStatementDebits; private set => SetProperty(ref _bankImportedStatementDebits, value); }
    public decimal BankImportedStatementCredits { get => _bankImportedStatementCredits; private set => SetProperty(ref _bankImportedStatementCredits, value); }
    public decimal? BankCalculatedClosingBalance { get => _bankCalculatedClosingBalance; private set => SetProperty(ref _bankCalculatedClosingBalance, value); }
    public decimal? BankOriginalClosingBalance { get => _bankOriginalClosingBalance; private set => SetProperty(ref _bankOriginalClosingBalance, value); }
    public decimal? BankOpeningDifference { get => _bankOpeningDifference; private set => SetProperty(ref _bankOpeningDifference, value); }
    public decimal? BankClosingDifference { get => _bankClosingDifference; private set => SetProperty(ref _bankClosingDifference, value); }
    public string BankTallyOpeningBalanceText => !_bankHasLinkedTallyAuthority
        ? "Not linked"
        : BankBusinessBalance.Format(BankTallyOpeningBalance);
    public string BankOriginalOpeningBalanceText => BankBusinessBalance.Format(BankOriginalOpeningBalance);
    public string BankCalculatedClosingBalanceText => BankBusinessBalance.Format(BankCalculatedClosingBalance);
    public string BankOriginalClosingBalanceText => BankBusinessBalance.Format(BankOriginalClosingBalance);
    public string BankOpeningDifferenceText => BankOpeningDifference.HasValue ? $"{BankOpeningDifference.Value:N2}" : "-";
    public string BankClosingDifferenceText => BankClosingDifference.HasValue ? $"{BankClosingDifference.Value:N2}" : "-";
    public decimal BankBusinessDebitMovements => BankImportedStatementCredits;
    public decimal BankBusinessCreditMovements => BankImportedStatementDebits;
    public string BankOpeningComparisonText =>
        $"Books {BankTallyOpeningBalanceText}  |  Statement {BankOriginalOpeningBalanceText}  |  Difference {BankOpeningDifferenceText}";
    public string BankMovementSummaryText =>
        $"Business Dr {BankBusinessDebitMovements:N2}  |  Business Cr {BankBusinessCreditMovements:N2}";
    public string BankClosingComparisonText =>
        $"Imported Calc {BankCalculatedClosingBalanceText}  |  Statement {BankOriginalClosingBalanceText}  |  Difference {BankClosingDifferenceText}";
    public string BankBalanceStatus { get => _bankBalanceStatus; private set => SetProperty(ref _bankBalanceStatus, value); }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string TallyInterbankVerificationStatus { get => _tallyInterbankVerificationStatus; private set => SetProperty(ref _tallyInterbankVerificationStatus, value); }
    public string CashStatusText { get => _cashStatusText; private set => SetProperty(ref _cashStatusText, value); }
    public string ChainStatusText { get => _chainStatusText; private set => SetProperty(ref _chainStatusText, value); }
    public int MissingStatementPeriodCount => _missingStatementPeriodCount;
    public bool HasBankAccountPeriodWarnings => _missingStatementPeriodCount > 0;
    public string BankAccountPeriodWarningText => _bankAccountPeriodWarningText;
    public string BankAccountPeriodWarningSummaryText => string.IsNullOrWhiteSpace(_bankAccountPeriodWarningText)
        ? string.Empty
        : _bankAccountPeriodWarningText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    public string BankAccountPeriodWarningPanelText => string.IsNullOrWhiteSpace(_bankAccountPeriodWarningText)
        ? "No missing statement periods for the current coverage window."
        : _bankAccountPeriodWarningText;
    public string SelectedLedgerSummary => SelectedLedgerMappingRow?.SummaryName ?? string.Empty;
    public int BankStatementVisibleCount => FilteredTransactions.Count;
    public int LedgerMappingVisibleCount => FilteredLedgerMappingRows.Count;
    public int TransactionCount => Transactions.Count;
    public int AccountCount => Accounts.Count;
    public int PendingMappingCount
    {
        get
        {
            // "Mapping Required" must mean transactions that actually belong to the
            // Ledger Mapping workbench. Technical/no-party narration rows intentionally
            // excluded by the parser must not inflate this tile. Frozen member IDs are
            // the same authority used by Count <-> Detail in the workbench.
            var activeIds = Transactions
                .Where(item => !item.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Id)
                .Where(id => id > 0)
                .ToHashSet();
            if (LedgerMappings.Count == 0 || activeIds.Count == 0) return 0;
            return LedgerMappings
                .Where(mapping => !mapping.Status.Equals("Mapped", StringComparison.OrdinalIgnoreCase))
                .SelectMany(mapping => mapping.TransactionIds)
                .Where(id => id > 0 && activeIds.Contains(id))
                .Distinct()
                .Count();
        }
    }
    public int CompletedMappingCount
    {
        get
        {
            var activeIds = Transactions
                .Where(item => !item.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Id)
                .Where(id => id > 0)
                .ToHashSet();
            if (LedgerMappings.Count == 0 || activeIds.Count == 0) return 0;
            return LedgerMappings
                .Where(mapping => mapping.Status.Equals("Mapped", StringComparison.OrdinalIgnoreCase))
                .SelectMany(mapping => mapping.TransactionIds)
                .Where(id => id > 0 && activeIds.Contains(id))
                .Distinct()
                .Count();
        }
    }
    public int MappingTransactionCount => PendingMappingCount + CompletedMappingCount;

    public int ReadyMappingCount
    {
        get
        {
            var activeIds = Transactions
                .Where(item => !item.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Id)
                .Where(id => id > 0)
                .ToHashSet();
            if (LedgerMappingRows.Count == 0 || activeIds.Count == 0) return 0;

            return LedgerMappingRows
                .Where(row => row.Status.StartsWith("Ready", StringComparison.OrdinalIgnoreCase))
                .SelectMany(row => row.MemberMappings.Count > 0 ? row.MemberMappings : new[] { row.Mapping })
                .SelectMany(mapping => mapping.TransactionIds)
                .Where(id => id > 0 && activeIds.Contains(id))
                .Distinct()
                .Count();
        }
    }

    public int ReadyOrMappedMappingCount => Math.Min(MappingTransactionCount, CompletedMappingCount + ReadyMappingCount);

    public double MappingReadyPercent => MappingTransactionCount == 0
        ? 0d
        : Math.Round(ReadyOrMappedMappingCount * 100d / MappingTransactionCount, 1);

    public double MappingCompletionPercent => MappingTransactionCount == 0
        ? 0d
        : Math.Round(CompletedMappingCount * 100d / MappingTransactionCount, 1);
    public int ReconciliationIssueCount => ReconciliationRows.Count(item => !string.Equals(item.Status, "Reconciled", StringComparison.OrdinalIgnoreCase) && !string.Equals(item.Status, "Skipped", StringComparison.OrdinalIgnoreCase));
    public int CashEntryCount => CashEntries.Count;
    public int CashReadyCount => CashEntries.Count(item => item.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase));
    public int CashPaymentCount => CashEntries.Count(item => item.VoucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase));
    public int CashReceiptCount => CashEntries.Count(item => item.VoucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase));
    public int CashJournalCount => CashEntries.Count(item => item.VoucherType.Equals("Journal", StringComparison.OrdinalIgnoreCase));
    public int CashContraCount => CashEntries.Count(item => item.VoucherType.Equals("Contra", StringComparison.OrdinalIgnoreCase));
    public int CashAttentionCount => CashEntries.Count(item => !item.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase));
    public decimal CashOpeningBalance => CashSettings.OpeningBalance;
    public decimal CashReceiptTotal => CashEntries.Where(item => item.VoucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase)).Sum(item => item.Amount);
    public decimal CashPaymentTotal => CashEntries.Where(item => item.VoucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase)).Sum(item => item.Amount);
    public decimal CashClosingBalance => CashOpeningBalance + CashReceiptTotal - CashPaymentTotal;
    public bool IsCashClosingNegative => CashClosingBalance < 0m;
    public int CashDraftCount => CashEntries.Count(item => item.WorkflowStatus.Equals("Draft", StringComparison.OrdinalIgnoreCase));
    public int CashApprovedCount => CashEntries.Count(item => item.WorkflowStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase));
    public int CashRecurringCount => CashEntries.Count(item => item.RecurringProfileId.HasValue);
    public int CashReversedCount => CashEntries.Count(item => item.ReversalOfId.HasValue);
    public int PendingObCount => PendingObVerificationRows.Count;
    public int SkippedObCount => SkippedObVerificationRows.Count;
    public int VerifiedObCount => VerifiedObVerificationRows.Count;

    private long CurrentClientId => _clientContext.CurrentClient?.Id ?? 0;
    private string CurrentFinancialYear => _clientContext.CurrentClient?.FinancialYear ?? string.Empty;
    public long RecurringClientId => CurrentClientId;
    public string RecurringFinancialYear => CurrentFinancialYear;

    private void OnLiveDataChanged(object? sender, AccountingLiveChange change)
    {
        if (change.ClientId != CurrentClientId ||
            !change.FinancialYear.Equals(CurrentFinancialYear, StringComparison.OrdinalIgnoreCase)) return;

        _requiresDataRefresh = true;

        // MainWindow owns module-scoped cache invalidation/reload. This ViewModel must not
        // fan a live change back out to every module or reread Masters synchronously.
        if (change.Area is AccountingLiveArea.Banking or AccountingLiveArea.Cash or AccountingLiveArea.Purchase or AccountingLiveArea.Sales or AccountingLiveArea.Journal or AccountingLiveArea.Masters)
            QueueLedgerTotalsRefresh(invalidate: true);

        if (change.Area == AccountingLiveArea.Banking)
            StatusText = "Banking data changed • refreshing Banking streams…";
        else if (change.Area == AccountingLiveArea.Cash)
            CashStatusText = "Cash data changed • refreshing Cash snapshot…";
        else if (change.Area == AccountingLiveArea.Masters)
            StatusText = "Master data changed • refreshing Master dependencies…";
    }

    private static IReadOnlyList<BankTransferMatch> CompletedTransferMatches(IEnumerable<BankTransferMatch> matches) =>
        matches.Where(BankTransferWorkflowState.IsCompleted)
            .OrderBy(item => item.MatchDate)
            .ThenBy(item => item.MatchId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void RefreshBankBalanceSnapshot()
    {
        var account = SelectedAccount ?? Accounts.FirstOrDefault();
        if (account is null)
        {
            BankBalanceAccountName = "Select a Bank Account";
            _bankHasLinkedTallyAuthority = _isCurrentClientTallyLinked();
            BankTallyOpeningBalance = null;
            BankOriginalOpeningBalance = null;
            BankImportedStatementDebits = 0m;
            BankImportedStatementCredits = 0m;
            BankCalculatedClosingBalance = null;
            BankOriginalClosingBalance = null;
            BankOpeningDifference = null;
            BankClosingDifference = null;
            BankBalanceStatus = "No bank selected";
            NotifyBankBalanceSnapshotText();
            return;
        }

        BankBalanceAccountName = account.AuthorityName;
        var accountMasterKeys = new[] { account.AuthorityName, account.LedgerName, account.DisplayName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(TallyMasterIdentity.Key)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ledgerMasters = _activeMasterRecords
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var exactAccountMasters = ledgerMasters
            .Where(master => accountMasterKeys.Contains(TallyMasterIdentity.Key(master.Name)))
            .ToArray();
        var accountDigits = new string((account.AccountNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        var balanceMasterCandidates = exactAccountMasters.Length > 0
            ? exactAccountMasters
            : accountDigits.Length < 6
                ? Array.Empty<MasterRecord>()
                : ledgerMasters.Where(master =>
                {
                    var masterDigits = new string((master.Name ?? string.Empty).Where(char.IsDigit).ToArray());
                    return masterDigits.Contains(accountDigits, StringComparison.Ordinal);
                }).ToArray();
        // Fix110 R1: a running Tally server is not authority for this client. The
        // Balance Snapshot may show a Tally opening only when this Accounting client
        // owns a confirmed CompanyKey, and only from a MASTERID-backed live Tally Master.
        _bankHasLinkedTallyAuthority = _isCurrentClientTallyLinked();
        var tallyMaster = _bankHasLinkedTallyAuthority
            ? balanceMasterCandidates
                .Where(master => master.TallyMasterId.Trim().Length > 0 && MasterAuthority.IsTallySource(master.Source))
                .OrderByDescending(master => master.OpeningBalance.HasValue)
                .ThenBy(master => master.PersistentId)
                .FirstOrDefault()
            : null;

        // Master opening balances already follow accounting-books convention:
        // positive = Dr, negative = Cr. Never reinterpret them as statement-side DR/CR.
        BankTallyOpeningBalance = tallyMaster?.OpeningBalance;

        // Balance Snapshot is a source-statement control, not an active-voucher count.
        // Read statement authority fresh from SQLite so the tile cannot lag behind a
        // reconciliation/update. Only rows that actually belong to those source statements
        // participate. Inter-Bank/Tally retirement is still included because it removes only
        // an accounting duplicate; technical duplicate/generated-charge rows are excluded.
        var accountStatements = _repository
            .GetStatements(account.ClientId, account.FinancialYear)
            .Where(statement => statement.BankAccountId == account.Id)
            .ToArray();
        var sourceTransactions = _repository
            .GetTransactions(account.ClientId, account.FinancialYear, includeArchived: true)
            .Where(row => row.BankAccountId == account.Id)
            .Where(BankBalanceAuthority.IsSourceStatementTransaction)
            .ToArray();
        var authoritySnapshot = BankBalanceAuthority.BuildAccountSnapshot(account.Id, accountStatements, sourceTransactions);
        BankImportedStatementDebits = Math.Round(authoritySnapshot.TotalDebit, 2, MidpointRounding.AwayFromZero);
        BankImportedStatementCredits = Math.Round(authoritySnapshot.TotalCredit, 2, MidpointRounding.AwayFromZero);
        BankOriginalOpeningBalance = authoritySnapshot.OpeningBalance;
        BankOriginalClosingBalance = authoritySnapshot.ClosingBalance;

        // Bank closing verification must not depend on Tally being online or on a Tally
        // opening balance. The imported statement itself is arithmetically verified as:
        // Actual Bank OB + imported statement credits - imported statement debits = calculated CB.
        BankCalculatedClosingBalance = authoritySnapshot.CalculatedClosingBalance;
        BankOpeningDifference = BankTallyOpeningBalance.HasValue && BankOriginalOpeningBalance.HasValue
            ? Math.Round(BankTallyOpeningBalance.Value - BankOriginalOpeningBalance.Value, 2, MidpointRounding.AwayFromZero)
            : null;
        BankClosingDifference = BankCalculatedClosingBalance.HasValue && BankOriginalClosingBalance.HasValue
            ? Math.Round(BankCalculatedClosingBalance.Value - BankOriginalClosingBalance.Value, 2, MidpointRounding.AwayFromZero)
            : null;

        var closingGate = new BankReconciliationService(_repository)
            .VerifyExactClosingBalances(account.ClientId, account.FinancialYear, [account.Id]);

        var bankClosingExact = BankClosingDifference.HasValue && BankClosingDifference.Value == 0m;
        var tallyOpeningExact = BankOpeningDifference.HasValue && BankOpeningDifference.Value == 0m;
        if (!BankOriginalOpeningBalance.HasValue || !BankOriginalClosingBalance.HasValue)
            BankBalanceStatus = "BANK REVIEW • Statement opening/closing evidence is incomplete";
        else if (!BankCalculatedClosingBalance.HasValue)
            BankBalanceStatus = "BANK REVIEW • Imported debit/credit closing calculation is unavailable";
        else if (!bankClosingExact)
            BankBalanceStatus = $"BANK REVIEW • Statement bridge / imported movement difference {Math.Abs(BankClosingDifference!.Value):N2}";
        else if (!closingGate.IsExact)
            BankBalanceStatus = "BANK REVIEW • Imported totals match closing, but file/chain source verification requires review";
        else if (!BankTallyOpeningBalance.HasValue)
            BankBalanceStatus = "BANK EXACT • Imported debit/credit totals verify closing • Tally OB unavailable";
        else if (!tallyOpeningExact)
            BankBalanceStatus = $"BANK EXACT • Closing verified • Tally opening difference {BankOpeningDifferenceText}";
        else
            BankBalanceStatus = "EXACT MATCH • Bank imported totals closing difference 0.00 • Tally opening difference 0.00";

        NotifyBankBalanceSnapshotText();
    }

    private void NotifyBankBalanceSnapshotText()
    {
        OnPropertyChanged(nameof(BankTallyOpeningBalanceText));
        OnPropertyChanged(nameof(BankOriginalOpeningBalanceText));
        OnPropertyChanged(nameof(BankCalculatedClosingBalanceText));
        OnPropertyChanged(nameof(BankOriginalClosingBalanceText));
        OnPropertyChanged(nameof(BankOpeningDifferenceText));
        OnPropertyChanged(nameof(BankClosingDifferenceText));
        OnPropertyChanged(nameof(BankBusinessDebitMovements));
        OnPropertyChanged(nameof(BankBusinessCreditMovements));
        OnPropertyChanged(nameof(BankOpeningComparisonText));
        OnPropertyChanged(nameof(BankMovementSummaryText));
        OnPropertyChanged(nameof(BankClosingComparisonText));
    }

    public void RefreshCashLedgerTotalFor(CashEntryRecord? entry)
    {
        if (entry is not null && ReferenceEquals(CashVoucherDraft, entry))
        {
            QueueSelectedCashLedgerTotalRefresh(entry);
            return;
        }

        if (entry is not null && !ReferenceEquals(SelectedCashEntry, entry))
            SelectedCashEntry = entry;
        else
            QueueSelectedCashLedgerTotalRefresh(entry ?? SelectedCashEntry);
    }

    private void QueueLedgerTotalsRefresh(bool invalidate)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess())
            _ = RefreshLedgerTotalsAsync(invalidate);
        else
            dispatcher.BeginInvoke(() => _ = RefreshLedgerTotalsAsync(invalidate));
    }

    private async Task RefreshLedgerTotalsAsync(bool invalidate)
    {
        var clientId = CurrentClientId;
        var financialYear = CurrentFinancialYear;
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear))
        {
            LedgerPickerOptions.Clear();
            ApplySelectedCashLedgerTotal(null, "Open a client to view Ledger totals.");
            return;
        }

        var version = ++_ledgerTotalRefreshVersion;
        if (invalidate) _ledgerTotalService.Invalidate(clientId, financialYear);

        IReadOnlyList<LedgerPickerOption> options;
        try
        {
            options = await Task.Run(() =>
            {
                // The picker is Master-driven, not movement-driven. Every active Ledger
                // Master must be selectable, including newly created Manual/local Masters
                // with zero postings. Totals enrich the options when available but can
                // never filter a valid Master out of Cash Entry.
                var masters = _masterRepository
                    .Get(clientId, financialYear, false, string.Empty)
                    .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                   !item.IsArchived &&
                                   !string.IsNullOrWhiteSpace(item.Name))
                    .GroupBy(item => TallyMasterIdentity.Key(item.Name), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group
                        .OrderByDescending(item => MasterAuthority.IsTallySource(item.Source))
                        .ThenByDescending(item => item.IsUserEdited)
                        .ThenBy(item => item.PersistentId)
                        .First())
                    .ToArray();

                var totals = _ledgerTotalService.GetAll(clientId, financialYear)
                    .GroupBy(item => TallyMasterIdentity.Key(item.LedgerName), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                return masters
                    .Select(master =>
                    {
                        totals.TryGetValue(TallyMasterIdentity.Key(master.Name), out var snapshot);
                        return snapshot is not null
                            ? new LedgerPickerOption
                            {
                                Name = master.Name.Trim(),
                                ParentGroup = string.IsNullOrWhiteSpace(master.Parent) ? snapshot.ParentGroup : master.Parent.Trim(),
                                OpeningBalance = snapshot.OpeningBalance,
                                DebitTotal = snapshot.TotalDebit,
                                CreditTotal = snapshot.TotalCredit,
                                ClosingBalance = snapshot.ClosingBalance,
                                MovementCount = snapshot.MovementCount
                            }
                            : new LedgerPickerOption
                            {
                                Name = master.Name.Trim(),
                                ParentGroup = master.Parent?.Trim() ?? string.Empty,
                                OpeningBalance = master.OpeningBalance ?? 0m,
                                ClosingBalance = master.OpeningBalance ?? 0m
                            };
                    })
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            });
        }
        catch (Exception exception)
        {
            if (version == _ledgerTotalRefreshVersion)
                ApplySelectedCashLedgerTotal(null, $"Ledger totals could not be refreshed: {exception.Message}");
            return;
        }

        if (version != _ledgerTotalRefreshVersion || CurrentClientId != clientId ||
            !CurrentFinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)) return;

        LedgerPickerOptions.ReplaceRange(options);
        await RefreshSelectedCashLedgerTotalAsync(SelectedCashEntry, version);
    }

    private void QueueSelectedCashLedgerTotalRefresh(CashEntryRecord? entry)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess())
            _ = RefreshSelectedCashLedgerTotalAsync(entry);
        else
            dispatcher.BeginInvoke(() => _ = RefreshSelectedCashLedgerTotalAsync(entry));
    }

    private async Task RefreshSelectedCashLedgerTotalAsync(CashEntryRecord? entry, int? requiredVersion = null)
    {
        var clientId = CurrentClientId;
        var financialYear = CurrentFinancialYear;
        var ledgerName = ResolveCashLedgerName(entry);
        if (clientId <= 0 || ledgerName.Length == 0)
        {
            ApplySelectedCashLedgerTotal(null, entry is null
                ? "Select a Cash row to view its Ledger total."
                : "Select a Ledger to view opening, movement and balance.");
            return;
        }

        LedgerTotalSnapshot? snapshot;
        try
        {
            var asOnDate = entry?.EntryDate == default ? (DateTime?)null : entry?.EntryDate.Date;
            snapshot = await Task.Run(() => _ledgerTotalService.Get(clientId, financialYear, ledgerName, asOnDate));
        }
        catch (Exception exception)
        {
            ApplySelectedCashLedgerTotal(null, $"Ledger total unavailable: {exception.Message}");
            return;
        }

        if (requiredVersion.HasValue && requiredVersion.Value != _ledgerTotalRefreshVersion) return;
        if (CurrentClientId != clientId || !CurrentFinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)) return;
        var current = ReferenceEquals(CashVoucherDraft, entry) ? CashVoucherDraft : SelectedCashEntry;
        var currentLedger = ResolveCashLedgerName(current);
        if (!currentLedger.Equals(ledgerName, StringComparison.OrdinalIgnoreCase)) return;

        ApplySelectedCashLedgerTotal(
            snapshot,
            snapshot is null
                ? $"'{ledgerName}' is not available in the active Ledger total book."
                : "Posted client/FY movements • unsaved edits are excluded until the row is saved.");
    }

    private static string ResolveCashLedgerName(CashEntryRecord? entry)
    {
        if (entry is null) return string.Empty;
        // The current editable Ledger is the user's active choice. MappedMasterLedger
        // may still contain the previously saved value until validation completes.
        var selected = entry.EntryLedger?.Trim() ?? string.Empty;
        if (selected.Length > 0) return selected;
        return entry.MappedMasterLedger?.Trim() ?? string.Empty;
    }

    private void ApplySelectedCashLedgerTotal(LedgerTotalSnapshot? snapshot, string status)
    {
        SelectedCashLedgerTotal = snapshot;
        SelectedCashLedgerTotalStatus = status;
        if (CashVoucherDraft is not null && IsCashVoucherEditorOpen)
            CashVoucherDraft.SetClosingBalanceSuggestion(snapshot?.ClosingBalance);
        else
            _selectedCashEntry?.SetClosingBalanceSuggestion(snapshot?.ClosingBalance);
    }

    public void PrepareForDisplay()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) { StatusText = "Open a client to use Banking."; return; }
        // R44: MainWindow navigation owns the exact visible-module dependency set.
        // This ViewModel must not silently add Cash/Masters/Ledger Mapping work simply
        // because one Banking method is reused by multiple pages.
        if (_requiresDataRefresh || _loadedClientId != client.Id ||
            !_loadedFinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            StatusText = "Banking streams are preparing in the background…";
        else NotifyCounts();
    }

    public void RefreshFromPreparedCache()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var requestedModules = new[]
        {
            ClientWorkspaceModule.Banking, ClientWorkspaceModule.LedgerMapping,
            ClientWorkspaceModule.Cash, ClientWorkspaceModule.Masters
        };
        if (!_workspaceStateCache.TryGetModulesForPresentation(client.Id, client.FinancialYear, requestedModules, out var snapshot)) return;
        foreach (var module in requestedModules)
        {
            if (_workspaceStateCache.IsModuleReady(client.Id, client.FinancialYear, module))
                WarmFromPreparedSnapshot(snapshot, module);
        }
    }

    public void WarmFromPreparedSnapshot(ClientWorkspaceSnapshot snapshot, ClientWorkspaceModule module)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != snapshot.ClientId ||
            !client.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;

        switch (module)
        {
            case ClientWorkspaceModule.Banking:
            {
                var accountId = SelectedAccount?.Id ?? 0L;
                var statementId = SelectedStatement?.Id ?? 0L;
                var transactionId = SelectedTransaction?.Id ?? 0L;
                Accounts.ReplaceRange(snapshot.BankAccounts);
                Statements.ReplaceRange(snapshot.BankStatements);
                LoadTransactions(snapshot.BankTransactions);
                ReconciliationRows.ReplaceRange(snapshot.BankReconciliation);
                ProjectResolvedBankBalancesToUi(snapshot.BankTransactions);
                TransferMatches.ReplaceRange(CompletedTransferMatches(snapshot.BankTransfers));
                SelectedAccount = Accounts.FirstOrDefault(item => item.Id == accountId) ?? Accounts.FirstOrDefault();
                SelectedStatement = Statements.FirstOrDefault(item => item.Id == statementId);
                SelectedTransaction = Transactions.FirstOrDefault(item => item.Id == transactionId);
                BuildBankRenameRows();
                BuildObVerificationRows(client.ClientName);
                RefreshBankBalanceSnapshot();
                ChainStatusText = BuildChainStatus();
                _loadedClientId = client.Id; _loadedFinancialYear = client.FinancialYear; _requiresDataRefresh = false;
                var balanceReviews = Statements.Count(item => item.ReconciliationStatus.Contains("Review", StringComparison.OrdinalIgnoreCase) || item.ReconciliationStatus.Equals("Difference", StringComparison.OrdinalIgnoreCase));
                StatusText = $"Banking ready • {Transactions.Count:N0} transaction(s), {balanceReviews:N0} balance review(s) from Banking cache stream.";
                QueueBankRenameHistoryHydration(client.Id, client.FinancialYear, snapshot.BankAccounts);
                // Ledger Mapping can paint from its persisted stream before Banking transactions
                // finish hydrating. Re-check coverage/projection only after the authoritative Bank
                // transaction set is present so a zero-row early paint can never suppress the
                // required automatic rebuild. This work is background-only and never resets an
                // active Final Ledger/Ledger Group editor.
                QueueLedgerMappingProjectionUpgradeIfNeeded();
                if ((_ledgerMappingWorkspaceLoaded || SelectedBankingTabIndex == 4) && HasLedgerMappingCoverageGap())
                    _ = RepairLedgerMappingCoverageIfNeededAsync();
                NotifyCounts();
                var hasUnregisteredPhysicalStatementSource = snapshot.BankTransactions
                    .Where(BankBalanceAuthority.IsSourceStatementTransaction)
                    .Where(row => row.BankAccountId > 0 && !string.IsNullOrWhiteSpace(row.SourceFile))
                    .Any(row => !snapshot.BankStatements.Any(statement =>
                        statement.BankAccountId == row.BankAccountId &&
                        string.Equals((statement.SourceFile ?? string.Empty).Trim(), (row.SourceFile ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(row.SourceSheet) ||
                         string.IsNullOrWhiteSpace(statement.SourceSheet) ||
                         string.Equals((statement.SourceSheet ?? string.Empty).Trim(), (row.SourceSheet ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))));
                if (hasUnregisteredPhysicalStatementSource ||
                    Statements.Any(statement =>
                        !statement.OpeningBalance.HasValue ||
                        !statement.ClosingBalance.HasValue ||
                        statement.OpeningBalanceSource.Length == 0 ||
                        statement.ClosingBalanceSource.Length == 0 ||
                        statement.OpeningBalanceSource.Equals("LEGACY UNVERIFIED", StringComparison.OrdinalIgnoreCase) ||
                        statement.ClosingBalanceSource.Equals("LEGACY UNVERIFIED", StringComparison.OrdinalIgnoreCase)))
                    QueueBankBalanceEvidenceRepair(client);
                break;
            }
            case ClientWorkspaceModule.LedgerMapping:
            {
                var sameAuthority = LedgerMappingAuthorityFingerprint(LedgerMappings)
                    .Equals(LedgerMappingAuthorityFingerprint(snapshot.BankLedgerMappings), StringComparison.Ordinal);
                if (!sameAuthority)
                    LedgerMappings.ReplaceRange(snapshot.BankLedgerMappings);
                _ledgerMappingsLoaded = true;
                if (_ledgerMappingWorkspaceLoaded || SelectedBankingTabIndex == 4)
                {
                    _ledgerMappingWorkspaceLoaded = true;
                    if (sameAuthority && LedgerMappingRows.Count > 0)
                    {
                        // A Master/Tally cache generation must not ReplaceRange the visible
                        // workbench. Keep the same row/editor instances and update Existing Match
                        // silently so Final Ledger never drops keyboard focus.
                        QueueLedgerMappingExistingMatchRefresh();
                    }
                    else
                    {
                        BuildLedgerMappingRows(_ledgerMappingBankFilter, _ledgerMappingShowMapped, _ledgerMappingVoucherFilter);
                    }
                }
                NotifyCounts();
                break;
            }
            case ClientWorkspaceModule.Cash:
                CashEntries.ReplaceRange(snapshot.CashEntries);
                BuildCashEntryMappingRows();
                CashStatusText = $"Cash Entry ready • {CashEntries.Count:N0} row(s) from its own cache stream.";
                QueueCashSettingsHydration(client);
                QueueAdvancedClosureCashRepair(client, showNoChangeStatus: false, observedRows: snapshot.CashEntries);
                NotifyCounts();
                break;
            case ClientWorkspaceModule.Masters:
                _activeMasterRecords = snapshot.Masters.Where(item => !item.IsArchived).ToArray();
                LoadMasterLists(client.Id, client.FinancialYear, _activeMasterRecords);
                BuildBankRenameRows();
                BuildCashEntryMappingRows();
                RefreshEmptyLedgerMasterPanelState();
                // Tally connection/master synchronization is background authority. Resolve
                // Existing Ledger Match off the dispatcher and mutate current rows in-place;
                // never rebuild the visible DataGrid merely because new Masters arrived.
                if (_ledgerMappingWorkspaceLoaded || SelectedBankingTabIndex == 4)
                    QueueLedgerMappingExistingMatchRefresh();
                QueueLedgerTotalsRefresh(invalidate: true);
                NotifyCounts();
                break;
        }
    }

    private void QueueBankRenameHistoryHydration(long clientId, string financialYear, IReadOnlyList<BankAccountRecord> accounts)
    {
        _ = Task.Run(() =>
        {
            HydrateBankRenameHistory(accounts);
            return accounts;
        }).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                var current = _clientContext.CurrentClient;
                if (current is null || current.Id != clientId || !current.FinancialYear.Equals(financialYear,StringComparison.OrdinalIgnoreCase)) return;
                BuildBankRenameRows();
            }), DispatcherPriority.Background);
        });
    }

    private void QueueCashSettingsHydration(AccountingAutomationHub.Domain.Clients.ClientProfile client)
    {
        var clientId = client.Id; var financialYear = client.FinancialYear;
        _ = Task.Run(() => _cashEntryService.EnsureSettings(clientId, financialYear)).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                var current = _clientContext.CurrentClient;
                if (current is null || current.Id != clientId || !current.FinancialYear.Equals(financialYear,StringComparison.OrdinalIgnoreCase)) return;
                CashSettings = task.Result;
                CashLayoutMode = CashSettings.LayoutMode;
                SelectedCashCommonLedger = CashSettings.DefaultCommonLedger;
                BuildCashEntryMappingRows();
            }), DispatcherPriority.Background);
        });
    }

    public void RefreshCashAfterRecurring()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Cash);
    }

    public void Refresh()
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            StatusText = "Open a client to use Banking.";
            CashStatusText = "Open a client to use Cash Entry.";
            NotifyCounts();
            return;
        }

        // Completion 61: never blank the Banking/Cash/Ledger Mapping UI while a refresh is
        // running. Queue the independent module workers and keep the last committed cache
        // visible until each replacement arrives.
        _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Banking);
        _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.LedgerMapping);
        _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Cash);
        _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Masters);
        RefreshFromPreparedCache();
        StatusText = Transactions.Count > 0
            ? $"Banking refresh running in parallel • showing {Transactions.Count:N0} cached transaction(s)."
            : "Banking cache streams are preparing in parallel…";
        CashStatusText = CashEntries.Count > 0
            ? $"Cash refresh running in parallel • showing {CashEntries.Count:N0} cached row(s)."
            : "Cash cache stream is preparing in parallel…";
        QueueTallyBankStatementReconciliation(client, force: false, reason: "Banking refresh queued");
    }

    private void RefreshAndReconcileTally()
    {
        Refresh();
        var client = _clientContext.CurrentClient;
        if (client is not null)
        {
            QueueBankBalanceEvidenceRepair(client);
            QueueTallyBankStatementReconciliation(client, force: true, reason: "Banking Refresh");
        }
    }

    public void BeginLedgerMappingEditor(LedgerMappingWorkbenchRow? row)
    {
        if (row is not null) _activeLedgerMappingEditorRow = row;
    }

    public void EndLedgerMappingEditor(LedgerMappingWorkbenchRow? row)
    {
        if (row is not null && !ReferenceEquals(_activeLedgerMappingEditorRow, row)) return;
        _activeLedgerMappingEditorRow = null;

        // Do not start another whole-board Existing Match pass merely because a cell lost
        // focus. Only replay work that was genuinely deferred by a Master/coverage/projection
        // change while this editor owned focus. A dropdown selection therefore stages once
        // and closes without causing a second background refresh wave.
        if (_ledgerMappingExistingMatchRefreshDeferred)
        {
            _ledgerMappingExistingMatchRefreshDeferred = false;
            QueueLedgerMappingExistingMatchRefresh(force: true);
        }
        if (_ledgerMappingCoverageRepairDeferred)
        {
            _ledgerMappingCoverageRepairDeferred = false;
            _ = RepairLedgerMappingCoverageIfNeededAsync();
        }
        if (_ledgerMappingProjectionUpgradeDeferred)
        {
            _ledgerMappingProjectionUpgradeDeferred = false;
            QueueLedgerMappingProjectionUpgradeIfNeeded();
        }
    }

    private sealed record LedgerMappingExistingMatchUpdate(
        LedgerMappingWorkbenchRow Row, string ExistingLedger, string ExistingGroup, bool MultipleMatches);

    private void QueueLedgerMappingExistingMatchRefresh(bool force = false)
    {
        if (LedgerMappingRows.Count == 0 || _activeMasterRecords.Count == 0) return;

        var fingerprint = BuildLedgerMappingExistingMatchRefreshFingerprint();
        if (!force && fingerprint.Equals(_ledgerMappingExistingMatchRefreshFingerprint, StringComparison.Ordinal)) return;
        _ledgerMappingExistingMatchRefreshFingerprint = fingerprint;
        if (_activeLedgerMappingEditorRow is not null)
            _ledgerMappingExistingMatchRefreshDeferred = true;

        _ledgerMappingExistingMatchRefreshCancellation?.Cancel();
        _ledgerMappingExistingMatchRefreshCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _ledgerMappingExistingMatchRefreshCancellation = cancellation;

        var masterLedgers = LedgerMappingMasterResolver.DistinctByMasterAuthority(_activeMasterRecords
            .Where(item => !item.IsArchived && item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)));
        var rows = LedgerMappingRows.ToArray();

        _ = Task.Run(() =>
        {
            var updates = new List<LedgerMappingExistingMatchUpdate>(rows.Length);
            foreach (var row in rows)
            {
                if (cancellation.IsCancellationRequested) return Array.Empty<LedgerMappingExistingMatchUpdate>();
                var distinctFinals = (row.MemberMappings.Count > 0 ? row.MemberMappings : new[] { row.Mapping })
                    .Where(item => !string.IsNullOrWhiteSpace(item.LedgerName))
                    .GroupBy(item => BankNarrationParser.NormalizeLedgerExactMatchKey(item.LedgerName), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First().LedgerName.Trim())
                    .Where(value => value.Length > 0)
                    .ToList();
                var explicitExisting = distinctFinals.Count == 1
                    ? distinctFinals[0]
                    : row.Mapping.SourceLedgerName;
                var resolution = LedgerMappingMasterResolver.Resolve(row.SummaryName, explicitExisting, masterLedgers);
                updates.Add(new LedgerMappingExistingMatchUpdate(
                    row,
                    resolution.Match?.Name.Trim() ?? string.Empty,
                    resolution.Match?.Parent?.Trim() ?? string.Empty,
                    resolution.HasMultipleMatches));
            }
            return updates.ToArray();
        }, cancellation.Token).ContinueWith(task =>
        {
            if (task.IsCanceled || task.IsFaulted || cancellation.IsCancellationRequested) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (cancellation.IsCancellationRequested) return;
                foreach (var update in task.Result)
                {
                    var row = update.Row;
                    if (!LedgerMappingRows.Contains(row)) continue;
                    var oldExisting = row.ExactMatch;
                    row.ExactMatch = update.ExistingLedger;

                    // Existing Match is presentation-only and may update silently while the
                    // user types. Automatic Final Ledger/Group fill is deferred for the active
                    // editor so a Tally Master refresh can never steal focus or replace text.
                    if (ReferenceEquals(_activeLedgerMappingEditorRow, row)) continue;
                    if (update.MultipleMatches)
                    {
                        if (string.IsNullOrWhiteSpace(row.FinalLedger)) row.Status = "Multiple Master Matches";
                        continue;
                    }
                    if (update.ExistingLedger.Length == 0) continue;

                    var finalWasAutomatic = string.IsNullOrWhiteSpace(row.FinalLedger) ||
                        (!string.IsNullOrWhiteSpace(oldExisting) &&
                         BankNarrationParser.NormalizeLedgerExactMatchKey(row.FinalLedger)
                             .Equals(BankNarrationParser.NormalizeLedgerExactMatchKey(oldExisting), StringComparison.OrdinalIgnoreCase));
                    if (!finalWasAutomatic) continue;
                    row.FinalLedger = update.ExistingLedger;
                    row.LedgerGroup = update.ExistingGroup;
                    row.Status = "Ready";
                }
            }));
        }, TaskScheduler.Default);
    }

    private string BuildLedgerMappingExistingMatchRefreshFingerprint()
    {
        var masters = string.Join("\n", _activeMasterRecords
            .Where(item => !item.IsArchived && item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => BankNarrationParser.NormalizeLedgerExactMatchKey(item.Name), StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PersistentId)
            .Select(item => string.Join("|",
                item.PersistentId,
                BankNarrationParser.NormalizeLedgerExactMatchKey(item.Name),
                AccountingText.DuplicateKey(item.Parent))));
        var rows = string.Join("\n", LedgerMappingRows.Select(LedgerMappingWorkbenchStableKey));
        return masters + "\n--ROWS--\n" + rows;
    }

    private static string LedgerMappingWorkbenchStableKey(LedgerMappingWorkbenchRow row)
    {
        var source = (row.MemberMappings.Count > 0 ? row.MemberMappings : new[] { row.Mapping })
            .Select(item => AccountingText.DuplicateKey(item.SourceLedgerName))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        var ids = (row.MemberMappings.Count > 0 ? row.MemberMappings : new[] { row.Mapping })
            .SelectMany(item => item.TransactionIds)
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id);
        return string.Join("|",
            BankNarrationParser.Normalize(row.SummaryName ?? string.Empty),
            string.Join(",", source),
            string.Join(",", ids));
    }

    private static string LedgerMappingAuthorityFingerprint(IEnumerable<BankLedgerMapping> mappings)
        => string.Join("\n", mappings
            .Select(item => string.Join("|",
                BankNarrationParser.Normalize(item.SummaryName ?? string.Empty),
                AccountingText.DuplicateKey(item.SourceLedgerName),
                item.VoucherType?.Trim().ToUpperInvariant() ?? string.Empty,
                AccountingText.DuplicateKey(item.LedgerName),
                AccountingText.DuplicateKey(item.MasterGroup),
                item.Status?.Trim().ToUpperInvariant() ?? string.Empty,
                string.Join(",", item.TransactionIds.Where(id => id > 0).Distinct().OrderBy(id => id))))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    public void NotifyTallyConnectionChanged(bool isConnected)
    {
        if (!isConnected) return;
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        // Do not rebuild the visible Ledger Mapping grid on the connection edge. Tally's
        // coordinator publishes its Master delta into the prepared cache; Existing Match is
        // resolved off-dispatcher and painted in-place by the Masters snapshot completion.
        QueueTallyBankStatementReconciliation(client, force: true, reason: "Linked Tally connection available");
    }

    public void SaveTransactionRecord(BankTransactionRecord? transaction)
    {
        if (transaction is null) return;
        if (!DemandGovernancePermission(GovernancePermissions.EditAccounting, "edit Bank Statement transactions")) return;
        try
        {
            if (transaction.TransactionDate == default) throw new InvalidOperationException("A valid transaction date is required.");
            if (transaction.Debit < 0 || transaction.Credit < 0) throw new InvalidOperationException("Debit and Credit cannot be negative.");
            if (transaction.Debit > 0 && transaction.Credit > 0) throw new InvalidOperationException("Enter either Debit or Credit, not both.");
            if (transaction.Debit <= 0 && transaction.Credit <= 0) throw new InvalidOperationException("Debit or Credit amount is required.");
            transaction.NormalizedNarration = BankNarrationParser.Normalize(transaction.Narration);
            if (string.IsNullOrWhiteSpace(transaction.Mode)) transaction.Mode = transaction.Debit > 0 ? "Payment" : "Receipt";
            if (!BankModes.Contains(transaction.Mode, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Mode must be Payment, Receipt or Contra.");
            if (!string.IsNullOrWhiteSpace(transaction.LedgerName) && !MasterLedgerNames.Contains(transaction.LedgerName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected Ledger does not exist in Master.");
            transaction.MappingStatus = string.IsNullOrWhiteSpace(transaction.LedgerName) ? "Mapping Required" : "Mapped";
            _repository.SaveTransaction(transaction);
            _reconciliationService.Reconcile(transaction.ClientId, transaction.FinancialYear);
            _mappingService.Rebuild(transaction.ClientId, transaction.FinancialYear);
            _ledgerTotalService.Invalidate(transaction.ClientId, transaction.FinancialYear);
            RefreshBankBalanceSnapshot();
            QueueWorkspaceCacheRefresh();
            _liveChangeHub.Publish(transaction.ClientId, transaction.FinancialYear, AccountingLiveArea.Banking, "Bank Statement row saved");
            StatusText = "Bank Statement row saved; Ledger Mapping and balances refreshed.";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            MessageBox.Show(exception.Message, "Bank Statement", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void SaveCashEntryRecord(CashEntryRecord? entry)
    {
        if (entry is null) return;
        if (!DemandGovernancePermission(GovernancePermissions.EditAccounting, "edit Cash entries")) return;
        try
        {
            _cashEntryService.ValidateAndSave(entry, CashSettings);
            QueueWorkspaceCacheRefresh();
            CashStatusText = "Cash Entry row validated and saved.";
            _liveChangeHub.Publish(entry.ClientId, entry.FinancialYear, AccountingLiveArea.Cash, "Cash Entry saved");
        }
        catch (Exception exception)
        {
            CashStatusText = exception.Message;
            MessageBox.Show(exception.Message, "Cash Entry", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void ApplyBankRenameRow(BankRenameRow? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.RenameTo)) return;
        SelectedBankRename = row;
        ApplyBankRename();
    }

    public void VerifyOpeningBalanceRow(ObVerificationRow? row)
    {
        if (row is null) return;
        SelectedObVerificationRow = row;
        VerifySelectedOpeningBalance();
    }

    public void UseSuggestedOpeningBalanceRow(ObVerificationRow? row)
    {
        if (row is null) return;
        SelectedObVerificationRow = row;
        UseSuggestedOpeningBalanceForSelected();
    }

    public void SkipOpeningBalanceRow(ObVerificationRow? row)
    {
        if (row is null) return;
        SelectedObVerificationRow = row;
        SkipSelectedOpeningBalance();
    }

    public void DeleteOpeningBalanceStatementRow(ObVerificationRow? row)
    {
        if (row is null) return;
        SelectedObVerificationRow = row;
        DeleteSelectedObStatement();
    }

    private void VerifySelectedOpeningBalance()
    {
        var row = SelectedObVerificationRow;
        if (row is null || !row.EnteredOpeningBalance.HasValue) return;

        var statementRows = Transactions
            .Where(item => item.StatementId == row.Statement.Id && BankBalanceAuthority.IsSourceStatementTransaction(item))
            .OrderBy(item => item.SourceRow)
            .ThenBy(item => item.Id)
            .ToArray();
        var entered = row.EnteredOpeningBalance.Value;
        var resolvedClosing = BankBalanceAuthority.ResolveClosingBalance(row.Statement, statementRows, entered);
        if (!resolvedClosing.IndependentValue.HasValue || !resolvedClosing.CalculatedValue.HasValue)
        {
            row.Status = "CLOSING BALANCE EVIDENCE MISSING";
            StatusText = "Opening Balance cannot be verified until the statement has source, running-balance or explicitly verified Closing Balance evidence.";
            return;
        }

        var difference = Math.Round(resolvedClosing.IndependentValue.Value - resolvedClosing.CalculatedValue.Value, 2, MidpointRounding.AwayFromZero);
        if (difference != 0m)
        {
            row.Status = "INVALID";
            StatusText = $"Opening Balance is invalid in business-books convention (Dr positive / Cr negative). Closing difference: {difference:N2}.";
            return;
        }

        var source = row.SuggestedOpeningBalance.HasValue && Math.Round(row.SuggestedOpeningBalance.Value - entered, 2, MidpointRounding.AwayFromZero) == 0m
            ? "CHAIN"
            : "MANUAL";
        row.Statement.OpeningBalance = entered;
        row.Statement.OpeningBalanceSource = source;
        row.Statement.ClosingBalance = resolvedClosing.IndependentValue;
        row.Statement.ClosingBalanceSource = resolvedClosing.Source;
        row.Statement.Difference = difference;
        row.Statement.ReconciliationStatus = "Reconciled";
        _repository.SaveStatementBalances(row.Statement.Id, entered, row.Statement.ClosingBalance, "Reconciled", difference, source, resolvedClosing.Source);
        _reconciliationService.Reconcile(row.Statement.ClientId, row.Statement.FinancialYear);
        QueueWorkspaceCacheRefresh();
        StatusText = "Opening Balance verified against authoritative Closing Balance evidence and Statement_Metadata updated.";
    }

    private void UseSuggestedOpeningBalanceForSelected()
    {
        var row = SelectedObVerificationRow;
        if (row?.SuggestedOpeningBalance is not decimal suggested) return;
        row.EnteredOpeningBalance = suggested;
        row.Status = "READY TO VERIFY";
    }

    private void SkipSelectedOpeningBalance()
    {
        var row = SelectedObVerificationRow;
        if (row is null) return;
        _repository.SaveStatementBalances(
            row.Statement.Id,
            row.Statement.OpeningBalance,
            row.Statement.ClosingBalance,
            "Skipped",
            row.Statement.Difference,
            row.Statement.OpeningBalanceSource);
        QueueWorkspaceCacheRefresh();
        StatusText = "Opening Balance verification skipped.";
    }

    private void DeleteSelectedObStatement()
    {
        if (!DemandGovernancePermission(GovernancePermissions.DeleteAccounting, "delete Bank statements")) return;
        var row = SelectedObVerificationRow;
        if (row is null) return;
        if (MessageBox.Show(
                $"Delete {row.FileName} and all of its Bank Statement rows?",
                "OB Verification Dashboard",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var deletedSourcePath = row.Statement.SourcePath;
        _repository.DeleteStatement(row.Statement.Id);
        _automationService?.NotifyBankStatementDeleted(CurrentClientId, CurrentFinancialYear, deletedSourcePath);
        _transferService.Reconcile(CurrentClientId, CurrentFinancialYear);
        _mappingService.Rebuild(CurrentClientId, CurrentFinancialYear);
        _ledgerTotalService.Invalidate(CurrentClientId, CurrentFinancialYear);
        Statements.Remove(row.Statement);
        LoadTransactions(Transactions.Where(item => item.StatementId != row.Statement.Id).ToArray());
        ReconciliationRows.ReplaceRange(ReconciliationRows.Where(item => item.StatementId != row.Statement.Id).ToArray());
        SelectedStatement = Statements.FirstOrDefault();
        BuildObVerificationRows(_clientContext.CurrentClient?.ClientName ?? string.Empty);
        QueueWorkspaceCacheRefresh();
        _liveChangeHub.Publish(CurrentClientId, CurrentFinancialYear, AccountingLiveArea.Banking, "Bank Statement deleted");
        StatusText = "Statement deleted; Banking cache streams are refreshing. Re-pasting the source into its Automation Folder can import it again.";
    }

    private void BuildObVerificationRows(string clientName)
    {
        var allRows = new List<ObVerificationRow>();
        var accountNames = Accounts.ToDictionary(item => item.Id, item => item.AuthorityName);
        var suggestedByStatement = ReconciliationRows.ToDictionary(item => item.StatementId, item => item.SuggestedOpeningBalance);
        foreach (var statement in Statements.OrderBy(item => item.BankAccountId).ThenBy(item => item.PeriodFrom ?? DateTime.MinValue).ThenBy(item => item.Id))
        {
            suggestedByStatement.TryGetValue(statement.Id, out var suggested);
            var status = statement.ReconciliationStatus.Equals("Reconciled", StringComparison.OrdinalIgnoreCase)
                ? "VERIFIED"
                : statement.ReconciliationStatus.Equals("Skipped", StringComparison.OrdinalIgnoreCase)
                    ? "SKIPPED"
                    : "PENDING";
            allRows.Add(new ObVerificationRow
            {
                Statement = statement,
                ClientName = clientName,
                AccountNumber = accountNames.TryGetValue(statement.BankAccountId, out var account) ? account : string.Empty,
                SuggestedOpeningBalance = suggested,
                EnteredOpeningBalance = statement.OpeningBalance ?? suggested,
                Status = status
            });
        }
        _allObVerificationRows = allRows;
        LoadObFilterValues();
        ApplyObVerificationFilter();
    }

    private void LoadObFilterValues()
    {
        var previous = ObFilterValue;
        ObFilterValues.Clear();
        ObFilterValues.Add("ALL");
        IEnumerable<string> values = ObFilterType switch
        {
            "File Name" => _allObVerificationRows.Select(item => item.FileName),
            "Account Number" => _allObVerificationRows.Select(item => item.AccountNumber),
            _ => _allObVerificationRows.Select(item => item.ClientName)
        };
        foreach (var value in values.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            ObFilterValues.Add(value);
        ObFilterValue = ObFilterValues.Contains(previous, StringComparer.OrdinalIgnoreCase) ? previous : "ALL";
    }

    private void ApplyObVerificationFilter()
    {
        PendingObVerificationRows.Clear();
        SkippedObVerificationRows.Clear();
        VerifiedObVerificationRows.Clear();
        foreach (var row in _allObVerificationRows.Where(PassesObFilter))
        {
            if (row.Status.Equals("VERIFIED", StringComparison.OrdinalIgnoreCase)) VerifiedObVerificationRows.Add(row);
            else if (row.Status.Equals("SKIPPED", StringComparison.OrdinalIgnoreCase)) SkippedObVerificationRows.Add(row);
            else PendingObVerificationRows.Add(row);
        }
        OnPropertyChanged(nameof(PendingObCount));
        OnPropertyChanged(nameof(SkippedObCount));
        OnPropertyChanged(nameof(VerifiedObCount));
    }

    private bool PassesObFilter(ObVerificationRow row)
    {
        if (ObFilterValue.Equals("ALL", StringComparison.OrdinalIgnoreCase)) return true;
        var value = ObFilterType switch
        {
            "File Name" => row.FileName,
            "Account Number" => row.AccountNumber,
            _ => row.ClientName
        };
        return value.Equals(ObFilterValue, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearReportCollections()
    {
        StatementMetadataReport.Clear();
        TransactionRegistryReport.Clear();
        TransferMapReport.Clear();
        ContraDebugReport.Clear();
        ChainReport.Clear();
        ChainAccountSummary.Clear();
        DuplicateReport.Clear();
        RegistryReport.Clear();
        IntegrityReport.Clear();
        ImportLogReport.Clear();
        BalanceAuthorityReport.Clear();
        ImportPerformanceLogReport.Clear();
        CashEntryRegistryReport.Clear();
    }

    private static void ReplaceStringOptionsIfChanged(BulkObservableCollection<string> target, IReadOnlyList<string> values)
    {
        if (target.Count == values.Count && target.Zip(values, (left, right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase)).All(equal => equal))
            return;
        target.ReplaceRange(values);
    }

    private void LoadMasterLists(long clientId, string financialYear, IReadOnlyList<MasterRecord>? preparedMasters = null)
    {
        var masters = preparedMasters ?? (_activeMasterRecords.Count > 0
            ? _activeMasterRecords
            : _masterRepository.Get(clientId, financialYear, false, string.Empty));
        var activeMasters = masters.Where(item => !item.IsArchived).ToArray();
        if (_activeMasterRecords.Count == 0) _activeMasterRecords = activeMasters;

        // Master cache refresh is one atomic collection Reset per selector, not Clear + N Add
        // events. SuggestTextBox therefore sees one changed list instead of reopening/re-filtering
        // the dropdown for every individual Tally Master that arrives.
        var ledgerNames = activeMasters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .Select(master => master.Name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ReplaceStringOptionsIfChanged(MasterLedgerNames, ledgerNames);

        var cashNames = activeMasters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             master.Parent.Equals("Cash-in-Hand", StringComparison.OrdinalIgnoreCase))
            .Select(master => master.Name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ReplaceStringOptionsIfChanged(CashInHandLedgerNames, cashNames);

        var groupNames = new List<string>();
        foreach (var name in MasterSynchronizationService.TallyPrebuiltLedgerGroupNames)
            if (!groupNames.Contains(name, StringComparer.OrdinalIgnoreCase)) groupNames.Add(name);
        foreach (var name in activeMasters
                     .Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
                     .Select(master => master.Name.Trim())
                     .Where(name => name.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            if (!groupNames.Contains(name, StringComparer.OrdinalIgnoreCase)) groupNames.Add(name);
        ReplaceStringOptionsIfChanged(MasterGroupNames, groupNames);

        // Rebuild Bank Rename choices from authoritative active Ledger Masters.
        // If the same name is present from more than one local provenance, prefer the
        // live/Tally authority row so the dropdown does not mislabel it as Bank Statement.
        var orderedBankLedgers = masters
            .Where(master => !master.IsArchived &&
                             master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             IsBankOrOverdraftMasterGroup(master.Parent))
            .GroupBy(master => AccountingText.DuplicateKey(master.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(master =>
                    BankLedgerRenameOption.NormalizeSourceLabel(master.Source)
                        .Equals("Tally", StringComparison.OrdinalIgnoreCase))
                .ThenBy(master => master.PersistentId)
                .First())
            .Select(master => new BankLedgerRenameOption
            {
                Name = master.Name.Trim(),
                Source = BankLedgerRenameOption.NormalizeSourceLabel(master.Source),
                Group = master.Parent?.Trim() ?? string.Empty
            })
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.Source, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        BankLedgerRenameOptions.Clear();
        foreach (var option in orderedBankLedgers) BankLedgerRenameOptions.Add(option);

        if (!CashInHandLedgerNames.Contains(CashSettings.DefaultCommonLedger, StringComparer.OrdinalIgnoreCase))
        {
            CashSettings.DefaultCommonLedger = CashInHandLedgerNames.FirstOrDefault() ?? string.Empty;
        }
        if (!CashInHandLedgerNames.Contains(SelectedCashCommonLedger, StringComparer.OrdinalIgnoreCase))
        {
            SelectedCashCommonLedger = CashSettings.DefaultCommonLedger;
        }
        RefreshEmptyLedgerMasterPanelState();
    }

    private void BuildBankRenameRows()
    {
        foreach (var account in Accounts.OrderBy(item => item.AuthorityName, StringComparer.OrdinalIgnoreCase))
        {
            BankRenameRows.Add(new BankRenameRow
            {
                BankAccountId = account.Id,
                CurrentBankName = account.AuthorityName
            });
        }
    }

    private void HydrateBankRenameHistory(IEnumerable<BankAccountRecord> accounts)
    {
        foreach (var account in accounts)
        {
            try
            {
                account.PreviousAuthorityName = _clientPreferences.Get(
                    account.ClientId, BankRenameHistoryKey(account.FinancialYear, account.Id)).Trim();
            }
            catch
            {
                account.PreviousAuthorityName = string.Empty;
            }
        }
    }

    private void RememberPreviousBankName(BankAccountRecord account, string previousName)
    {
        var cleaned = (previousName ?? string.Empty).Trim();
        if (cleaned.Length == 0) return;
        account.PreviousAuthorityName = cleaned;
        try
        {
            _clientPreferences.Set(
                account.ClientId, BankRenameHistoryKey(account.FinancialYear, account.Id), cleaned);
        }
        catch
        {
            // Rename history is presentation metadata only. A preference write must
            // never block the accounting/master rename itself.
        }
    }

    private static string BankRenameHistoryKey(string financialYear, long bankAccountId) =>
        $"BankRenameHistory.{financialYear.Trim()}.Account.{bankAccountId}.PreviousName";

    private void ReloadBankAccountsAfterRename(long selectedAccountId)
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var refreshed = _repository.GetAccounts(client.Id, client.FinancialYear).ToArray();
        HydrateBankRenameHistory(refreshed);
        Accounts.ReplaceRange(refreshed);
        BankRenameRows.Clear();
        BuildBankRenameRows();
        SelectedAccount = Accounts.FirstOrDefault(item => item.Id == selectedAccountId);
        OnPropertyChanged(nameof(AccountCount));
    }

    private static string LedgerMappingDraftKey(LedgerMappingWorkbenchRow row)
    {
        var ids = (row.MemberMappings.Count > 0 ? row.MemberMappings : new[] { row.Mapping })
            .SelectMany(mapping => mapping.TransactionIds).Where(id => id > 0).Distinct().OrderBy(id => id);
        return $"{BankNarrationParser.Normalize(row.SummaryName)}|{string.Join(',', ids)}";
    }

    private bool LedgerMappingDraftTouchesCommit(LedgerMappingWorkbenchRow row)
    {
        if (_ledgerMappingDraftCommitIds.Count == 0) return false;
        return (row.MemberMappings.Count > 0 ? row.MemberMappings : new[] { row.Mapping })
            .SelectMany(mapping => mapping.TransactionIds)
            .Any(id => id > 0 && _ledgerMappingDraftCommitIds.Contains(id));
    }

    private void BuildLedgerMappingRows(
        string? preservedBankFilter = null,
        string? preservedShowMapped = null,
        string? preservedVoucherFilter = null,
        IReadOnlyDictionary<string, int>? precomputedTallyExportDateCounts = null,
        string? preservedMethodFilter = null)
    {
        var retainedBankFilter = NormalizeLedgerMappingBankFilter(preservedBankFilter ?? _ledgerMappingBankFilter);
        var showMapped = NormalizeLedgerMappingShowMapped(preservedShowMapped ?? _ledgerMappingShowMapped);
        var voucherFilter = NormalizeLedgerMappingVoucherFilter(preservedVoucherFilter ?? _ledgerMappingVoucherFilter);
        var retainedMethodFilter = NormalizeLedgerMappingMethodFilter(preservedMethodFilter ?? _ledgerMappingMethodFilter);
        var stagedDrafts = LedgerMappingRows
            .Where(row => (!IsLedgerMappingWorkbenchRowMapped(row) || HasVisibleLedgerMappingChange(row)) && !LedgerMappingDraftTouchesCommit(row))
            .Where(row => !string.IsNullOrWhiteSpace(row.FinalLedger) || !string.IsNullOrWhiteSpace(row.LedgerGroup))
            .GroupBy(LedgerMappingDraftKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (group.Last().FinalLedger, group.Last().LedgerGroup), StringComparer.OrdinalIgnoreCase);

        _isBuildingLedgerMappingOptions = true;
        var builtRows = new List<LedgerMappingWorkbenchRow>();
        LedgerMappingBankOptions.Clear();
        LedgerMappingBankOptions.Add("ALL BANKS");
        foreach (var account in Accounts.OrderBy(item => item.AuthorityName, StringComparer.OrdinalIgnoreCase))
        {
            var authorityName = account.AuthorityName?.Trim() ?? string.Empty;
            if (authorityName.Length > 0 && !LedgerMappingBankOptions.Contains(authorityName, StringComparer.OrdinalIgnoreCase))
                LedgerMappingBankOptions.Add(authorityName);
        }

        retainedBankFilter = LedgerMappingBankOptions.Contains(retainedBankFilter, StringComparer.OrdinalIgnoreCase)
            ? LedgerMappingBankOptions.First(item => string.Equals(item, retainedBankFilter, StringComparison.OrdinalIgnoreCase))
            : "ALL BANKS";

        var masters = _activeMasterRecords.Count > 0
            ? _activeMasterRecords.Where(item => !item.IsArchived).ToList()
            : new List<MasterRecord>();
        if (_activeMasterRecords.Count == 0 && CurrentClientId > 0)
            _workspaceStateCache.QueueModuleRefresh(CurrentClientId, CurrentFinancialYear, ClientWorkspaceModule.Masters);
        var masterLedgers = LedgerMappingMasterResolver.DistinctByMasterAuthority(masters
            .Where(item => string.Equals(item.Type, "Ledger", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.Name)))
            .ToList();

        var masterLedgerGroups = masterLedgers
            .Select(master => new { Master = master, Key = BankNarrationParser.NormalizeLedgerExactMatchKey(master.Name) })
            .Where(item => item.Key.Length > 0)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Master).ToArray(), StringComparer.OrdinalIgnoreCase);
        var masterByExactKey = masterLedgerGroups
            .Where(pair => pair.Value.Length == 1)
            .ToDictionary(
                pair => pair.Key,
                pair => (Name: pair.Value[0].Name.Trim(), Group: pair.Value[0].Parent?.Trim() ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
        var validMasterGroups = masters
            .Where(item => item.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && !item.IsArchived && !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => AccountingText.DuplicateKey(item.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Name.Trim(), StringComparer.OrdinalIgnoreCase);

        // Fix109: Ledger Mapping must remain usable before this client has a Tally company link.
        // Built-in Tally groups are valid local Master parents even when no live CompanyKey has
        // been confirmed yet. Never validate a row against whatever unrelated company merely
        // happens to be open in Tally.
        foreach (var prebuiltGroup in MasterSynchronizationService.TallyPrebuiltLedgerGroupNames)
        {
            var display = AccountingText.DisplayName(prebuiltGroup);
            var groupKey = AccountingText.DuplicateKey(display);
            if (display.Length > 0 && groupKey.Length > 0 && !validMasterGroups.ContainsKey(groupKey))
                validMasterGroups[groupKey] = display;
        }

        var summaryCounts = LedgerMappings
            .Select(item => new { Name = BankNarrationParser.Normalize(item.SummaryName ?? string.Empty), Count = Math.Max(0, item.MatchCount) })
            .Where(item => item.Name.Length > 0)
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count), StringComparer.OrdinalIgnoreCase);

        var useTallyExportDates = LedgerMappingDateScope.Equals("Tally Export Dates", StringComparison.OrdinalIgnoreCase);
        // Full Statement is the normal Ledger Mapping view. Do not re-parse every
        // Bank transaction and rebuild export-date summary counts unless the user
        // actually selected Tally Export Dates. This was a major avoidable cost on
        // every Ledger Mapping open.
        IReadOnlyDictionary<string, int> tallyExportDateCounts = useTallyExportDates
            ? precomputedTallyExportDateCounts is not null
                ? new Dictionary<string, int>(precomputedTallyExportDateCounts, StringComparer.OrdinalIgnoreCase)
                : BuildTallyExportDateSummaryCounts(summaryCounts)
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Count and Bank scope are derived from the exact frozen transaction membership.
        // Persisted BankFilter text can become stale after an in-place Bank rename; filtering
        // by that old text was silently removing valid Ledger Mapping rows from the UI even
        // though diagnostic Excel still contained them. Current transaction/account identity
        // is authoritative whenever frozen membership exists.
        var activeMappingTransactionsById = Transactions
            .Where(item => item.Id > 0 && !item.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First());

        // Completion 63: Method filtering reuses the existing Banking narration parser and
        // caches the result once per active transaction. Changing the Method dropdown never
        // reparses the narration or touches SQLite.
        _ledgerMappingMethodByTransactionId.Clear();
        foreach (var transaction in activeMappingTransactionsById.Values)
            _ledgerMappingMethodByTransactionId[transaction.Id] = BankNarrationParser.DetectMethod(transaction.Narration ?? string.Empty);

        IReadOnlySet<string> CurrentBanksForMapping(BankLedgerMapping mapping)
        {
            var currentBanks = mapping.TransactionIds
                .Where(activeMappingTransactionsById.ContainsKey)
                .Select(id => activeMappingTransactionsById[id].BankAccountName?.Trim() ?? string.Empty)
                .Where(value => value.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (currentBanks.Count > 0) return currentBanks;

            var stored = mapping.BankFilter?.Trim() ?? string.Empty;
            return stored.Length > 0
                ? new HashSet<string>(new[] { stored }, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // Excel applies Bank before rendering the grouped cache and does NOT include Bank
        // in the visible GroupKey. All-bank view must never discard a mapping just because
        // its persisted BankFilter predates a rename.
        var scopedMappings = LedgerMappings
            .Where(mapping => !ExcelLedgerMappingParser.IsLedgerMappingNoiseSummary(mapping.SummaryName ?? string.Empty))
            .Where(mapping => retainedBankFilter.Equals("ALL BANKS", StringComparison.OrdinalIgnoreCase) ||
                              CurrentBanksForMapping(mapping).Contains(retainedBankFilter))
            .ToList();

        LedgerMappingMethodOptions.Clear();
        LedgerMappingMethodOptions.Add("ALL METHODS");
        var scopedMethodTransactionIds = scopedMappings
            .SelectMany(mapping => mapping.TransactionIds)
            .Where(id => id > 0)
            .ToHashSet();
        foreach (var method in scopedMethodTransactionIds
                     .Where(_ledgerMappingMethodByTransactionId.ContainsKey)
                     .Select(id => _ledgerMappingMethodByTransactionId[id])
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            LedgerMappingMethodOptions.Add(method);
        }
        retainedMethodFilter = LedgerMappingMethodOptions.Contains(retainedMethodFilter, StringComparer.OrdinalIgnoreCase)
            ? LedgerMappingMethodOptions.First(item => string.Equals(item, retainedMethodFilter, StringComparison.OrdinalIgnoreCase))
            : "ALL METHODS";

        static string WorkbookViewGroupKey(BankLedgerMapping mapping)
        {
            // Summary Name is the visible workbench identity.  The same Summary Name
            // is intentionally allowed to span multiple bank accounts in ALL BANKS.
            // Bank remains available as a filter/member attribute, but it must not split
            // one Summary Name into separate visible groups.
            var summary = BankNarrationParser.Normalize(mapping.SummaryName ?? string.Empty);
            var source = AccountingText.DuplicateKey(mapping.SourceLedgerName);
            return $"{summary}\u001f{(source.Length == 0 ? "<BLANK>" : "<MISSING>" + source)}";
        }

        // Count is derived from the exact frozen transaction membership, not from
        // persisted MatchCount totals. This guarantees the summary count and the
        // opened detail rows stay identical even after parser migrations/rebuilds.

        foreach (var members in scopedMappings
                     .GroupBy(WorkbookViewGroupKey, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.ToList())
                     .OrderByDescending(group => group.Sum(item => item.MatchCount))
                     .ThenBy(group => group[0].SummaryName ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var representative = members.OrderByDescending(item => item.MatchCount).ThenBy(item => item.Id).First();
            var summaryName = representative.SummaryName?.Trim() ?? string.Empty;
            if (summaryName.Length == 0) continue;

            var exactMatch = string.Empty;
            var exactGroup = string.Empty;
            var exactKey = BankNarrationParser.NormalizeLedgerExactMatchKey(summaryName);
            var summaryHasDuplicateMasters = exactKey.Length > 0 && masterLedgerGroups.TryGetValue(exactKey, out var summaryMasterMatches) && summaryMasterMatches.Length > 1;
            if (exactKey.Length > 0 && masterByExactKey.TryGetValue(exactKey, out var exactInfo) && exactInfo.Name.Length > 0)
            {
                exactMatch = exactInfo.Name;
                exactGroup = exactInfo.Group;
            }

            var frozenMemberIds = members
                .SelectMany(mapping => mapping.TransactionIds)
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
            var frozenTransactions = frozenMemberIds
                .Where(activeMappingTransactionsById.ContainsKey)
                .Select(id => activeMappingTransactionsById[id])
                .ToArray();
            var hasFrozenMembership = frozenMemberIds.Length > 0;
            var fullPaymentCount = hasFrozenMembership
                ? frozenTransactions.Count(item => item.Mode.Equals("Payment", StringComparison.OrdinalIgnoreCase))
                : members.Sum(mapping => mapping.PaymentMatchCount > 0
                    ? mapping.PaymentMatchCount
                    : mapping.VoucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase) ? mapping.MatchCount : 0);
            var fullReceiptCount = hasFrozenMembership
                ? frozenTransactions.Count(item => item.Mode.Equals("Receipt", StringComparison.OrdinalIgnoreCase))
                : members.Sum(mapping => mapping.ReceiptMatchCount > 0
                    ? mapping.ReceiptMatchCount
                    : mapping.VoucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase) ? mapping.MatchCount : 0);
            var fullCount = hasFrozenMembership
                ? frozenTransactions.Length
                : members.Sum(mapping => Math.Max(0, mapping.MatchCount));
            var displayType = fullPaymentCount > 0 && fullReceiptCount > 0 ? "Mixed"
                : fullPaymentCount > 0 ? "Payment"
                : fullReceiptCount > 0 ? "Receipt"
                : representative.VoucherType;

            var tallyCount = 0;
            var tallyPayment = 0;
            var tallyReceipt = 0;
            foreach (var mapping in members)
            {
                var banks = CurrentBanksForMapping(mapping);
                var summary = mapping.SummaryName?.Trim() ?? string.Empty;
                var sourceLedger = mapping.SourceLedgerName;
                foreach (var bank in banks)
                {
                    if (tallyExportDateCounts.TryGetValue(BuildLedgerDateCountKey(bank, mapping.VoucherType, summary, sourceLedger), out var count)) tallyCount += count;
                    if (tallyExportDateCounts.TryGetValue(BuildLedgerDateCountKey(bank, "Payment", summary, sourceLedger), out var payment)) tallyPayment += payment;
                    if (tallyExportDateCounts.TryGetValue(BuildLedgerDateCountKey(bank, "Receipt", summary, sourceLedger), out var receipt)) tallyReceipt += receipt;
                }
            }
            // Mixed atomic rows can make the type-specific keys overlap the total; payment+receipt
            // is the workbook's actual transaction count and therefore authoritative when present.
            if (tallyPayment + tallyReceipt > 0) tallyCount = tallyPayment + tallyReceipt;

            var distinctFinals = members
                .Where(item => !string.IsNullOrWhiteSpace(item.LedgerName))
                .GroupBy(item => BankNarrationParser.NormalizeLedgerExactMatchKey(item.LedgerName), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First().LedgerName.Trim())
                .Where(value => value.Length > 0)
                .ToList();

            // Existing Ledger Match is an application-wide Master match, not only a
            // Tally-origin match. A saved Final/Source Ledger that already exists in Ledger
            // Master is authoritative only by full normalized Ledger identity. Pending rows
            // never use one-word, fuzzy or party-equivalence matching.
            var explicitExistingLedger = distinctFinals.Count == 1
                ? distinctFinals[0]
                : representative.SourceLedgerName;
            var existingResolution = LedgerMappingMasterResolver.Resolve(summaryName, explicitExistingLedger, masterLedgers);
            if (existingResolution.HasMultipleMatches) summaryHasDuplicateMasters = true;
            if (existingResolution.Match is not null)
            {
                exactMatch = existingResolution.Match.Name.Trim();
                exactGroup = existingResolution.Match.Parent?.Trim() ?? string.Empty;
            }

            // Fix131: clearing/rejecting an automatically resolved Existing Ledger Match is
            // a durable user decision for this Summary+Ledger pair. Rebuild must not stage the
            // same automatic name again until the user explicitly maps it later.
            var rejectedAutomaticLedgerKey = string.Empty;
            if (exactMatch.Length > 0 &&
                _mappingService.IsSuggestionRejected(CurrentClientId, CurrentFinancialYear, exactKey, exactMatch))
            {
                rejectedAutomaticLedgerKey = BankNarrationParser.NormalizeLedgerExactMatchKey(exactMatch);
                exactMatch = string.Empty;
                exactGroup = string.Empty;
            }

            var mappedMembers = members.Count(item => string.Equals(item.Status, "Mapped", StringComparison.OrdinalIgnoreCase));

            string finalLedger;
            string ledgerGroup;
            string status;
            if (distinctFinals.Count > 1)
            {
                finalLedger = string.Empty;
                ledgerGroup = string.Empty;
                status = mappedMembers == members.Count ? "Mapped - Split by Voucher Type" : "Split";
            }
            else if (distinctFinals.Count == 1)
            {
                finalLedger = distinctFinals[0];
                ledgerGroup = members.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.MasterGroup))?.MasterGroup?.Trim() ?? string.Empty;
                var finalKey = BankNarrationParser.NormalizeLedgerExactMatchKey(finalLedger);
                if (finalKey.Length > 0 && masterLedgerGroups.TryGetValue(finalKey, out var finalMatches) && finalMatches.Length > 1)
                {
                    finalLedger = AccountingText.DisplayName(finalLedger);
                    ledgerGroup = string.Empty;
                    status = "Multiple Master Matches";
                }
                else if (finalKey.Length > 0 && masterByExactKey.TryGetValue(finalKey, out var finalInfo))
                {
                    finalLedger = finalInfo.Name;
                    ledgerGroup = finalInfo.Group;
                    status = mappedMembers == members.Count ? "Mapped" : mappedMembers > 0 ? "In Progress" : "Ready";
                }
                else if (ledgerGroup.Length == 0)
                {
                    finalLedger = AccountingText.DisplayName(finalLedger);
                    status = "Select Ledger Group";
                }
                else if (!validMasterGroups.TryGetValue(AccountingText.DuplicateKey(ledgerGroup), out var validGroup))
                {
                    finalLedger = AccountingText.DisplayName(finalLedger);
                    status = "Invalid Ledger Group";
                }
                else
                {
                    finalLedger = AccountingText.DisplayName(finalLedger);
                    ledgerGroup = validGroup;
                    status = "Ready - New Ledger";
                }
            }
            else if (summaryHasDuplicateMasters)
            {
                finalLedger = string.Empty;
                ledgerGroup = string.Empty;
                status = "Multiple Master Matches";
            }
            else if (exactMatch.Length > 0)
            {
                finalLedger = exactMatch;
                ledgerGroup = exactGroup;
                status = "Ready";
            }
            else if (!string.IsNullOrWhiteSpace(representative.SourceLedgerName) &&
                     (rejectedAutomaticLedgerKey.Length == 0 ||
                      !BankNarrationParser.NormalizeLedgerExactMatchKey(representative.SourceLedgerName).Equals(rejectedAutomaticLedgerKey, StringComparison.OrdinalIgnoreCase)))
            {
                // A missing/ambiguous source ledger may be prefilled only while the user has
                // not explicitly rejected that same automatic identity. Rebuild must not
                // resurrect a cleared Existing Ledger Match through SourceLedgerName.
                finalLedger = AccountingText.DisplayName(representative.SourceLedgerName);
                ledgerGroup = string.Empty;
                status = "Select Ledger Group";
            }
            else
            {
                finalLedger = string.Empty;
                ledgerGroup = string.Empty;
                status = "Pending";
            }

            // Summary Name remains the parser result. Existing Ledger Match is shown
            // in its own column so the user can see exactly what was parsed versus what
            // existing Master the Hub resolved it to.
            var displaySummary = AccountingText.DisplayName(summaryName);
            var suggestionEvidence = _mappingService.ResolveSuggestionEvidence(CurrentClientId, CurrentFinancialYear, displaySummary, exactMatch);
            var suggestedMatch = exactMatch.Length > 0 ? string.Empty : suggestionEvidence.SuggestedLedger;
            if (suggestedMatch.Length > 0 &&
                _mappingService.IsSuggestionRejected(CurrentClientId, CurrentFinancialYear, exactKey, suggestedMatch))
                suggestedMatch = string.Empty;
            builtRows.Add(new LedgerMappingWorkbenchRow
            {
                Mapping = representative,
                MemberMappings = members,
                MemberBanks = members.SelectMany(CurrentBanksForMapping).Where(value => value.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase),
                TransactionFacets = frozenTransactions
                    .Select(transaction => new LedgerMappingTransactionFacet(
                        transaction.Id,
                        transaction.TransactionDate,
                        transaction.Mode?.Trim() ?? string.Empty,
                        _ledgerMappingMethodByTransactionId.TryGetValue(transaction.Id, out var cachedMethod) ? cachedMethod : BankNarrationParser.DetectMethod(transaction.Narration ?? string.Empty),
                        transaction.BankAccountName?.Trim() ?? string.Empty))
                    .ToArray(),
                SummaryName = displaySummary,
                FullStatementCount = fullCount,
                FullStatementPaymentCount = fullPaymentCount,
                FullStatementReceiptCount = fullReceiptCount,
                TallyExportDateCount = tallyCount,
                TallyExportPaymentCount = tallyPayment,
                TallyExportReceiptCount = tallyReceipt,
                Count = useTallyExportDates ? tallyCount : fullCount,
                Type = displayType,
                ExactMatch = exactMatch,
                SuggestedMatch = suggestedMatch,
                SuggestionConfidence = suggestionEvidence.Confidence,
                SuggestionReason = suggestionEvidence.Reason,
                IsSelected = true,
                FinalLedger = finalLedger,
                LedgerGroup = ledgerGroup,
                Status = status
            });
        }

        if (stagedDrafts.Count > 0)
        {
            foreach (var row in builtRows)
            {
                if (!stagedDrafts.TryGetValue(LedgerMappingDraftKey(row), out var draft)) continue;
                row.FinalLedger = draft.FinalLedger;
                row.LedgerGroup = draft.LedgerGroup;
                StageLedgerMappingRow(row);
            }
        }
        LedgerMappingRows.ReplaceRange(builtRows);
        _ledgerMappingBankFilter = retainedBankFilter;
        _ledgerMappingShowMapped = showMapped;
        _ledgerMappingVoucherFilter = voucherFilter;
        _ledgerMappingMethodFilter = retainedMethodFilter;
        OnPropertyChanged(nameof(LedgerMappingShowMapped));
        OnPropertyChanged(nameof(IsLedgerMappingUnmapped));
        OnPropertyChanged(nameof(IsLedgerMappingMapped));
        OnPropertyChanged(nameof(IsLedgerMappingAll));
        OnPropertyChanged(nameof(LedgerMappingVoucherFilter));
        _isBuildingLedgerMappingOptions = false;
        // Fix131: publish selected Bank/Method only after their option collections are complete.
        // Notifying while the rebuild guard was still active could leave WPF displaying a blank
        // ComboBox even though the backing field already contained ALL BANKS / ALL METHODS.
        OnPropertyChanged(nameof(LedgerMappingBankFilter));
        OnPropertyChanged(nameof(LedgerMappingMethodFilter));
        // Rendering/rebuilding the board is read-only. Filter persistence belongs only to
        // the explicit property setters; a paint must never write ClientPreferences and
        // trigger the SQLite/WAL change monitor into another cache-refresh cycle.
        ApplyLedgerMappingFilters();
        NotifyLedgerMappingProgressVisuals();
    }

    private Dictionary<string, int> BuildTallyExportDateSummaryCounts(
        IReadOnlyDictionary<string, int> summaryCounts)
    {
        if (!_tallyExportSelectionContext.TryGetBankRanges(
                CurrentClientId,
                CurrentFinancialYear,
                out var ranges,
                out _))
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var accountNames = Accounts.ToDictionary(item => item.Id, item => item.AuthorityName);
        var frozenIds = LedgerMappings.SelectMany(item => item.TransactionIds).Where(id => id > 0).ToHashSet();
        IReadOnlyList<BankTransactionRecord> transactions = Transactions
            .Where(item => !item.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase))
            .Where(item => frozenIds.Count == 0 || frozenIds.Contains(item.Id))
            .ToArray();

        return BuildTallyExportDateSummaryCountsCore(summaryCounts, accountNames, ranges, transactions);
    }

    private static Dictionary<string, int> BuildTallyExportDateSummaryCountsCore(
        IReadOnlyDictionary<string, int> summaryCounts,
        IReadOnlyDictionary<long, string> accountNames,
        IReadOnlyList<TallyVoucherExportRange> ranges,
        IReadOnlyList<BankTransactionRecord> transactions)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (ranges.Count == 0 || transactions.Count == 0) return result;

        // Hub coverage extension keeps active Contra/reversal rows represented so
        // Full Statement and Tally-date Ledger Mapping membership cannot silently
        // fall below the active Bank Statement transaction total.

        // Excel pass 2 canonical representatives are fixed from the complete
        // exact-count dictionary before any date-filtered rows are expanded.
        var groupMap = ExcelLedgerMappingParser.BuildFastGroupMap(summaryCounts);
        foreach (var transaction in transactions)
        {
            if (!ranges.Any(range => range.Contains(transaction.TransactionDate)) ||
                ExcelLedgerMappingParser.IsLedgerMappingCoverageExcludedMode(transaction.Mode))
            {
                continue;
            }
            var bank = accountNames.TryGetValue(transaction.BankAccountId, out var authorityName)
                ? authorityName
                : string.Empty;
            if (bank.Length == 0) continue;

            var exactSummary = ExcelLedgerMappingParser.BuildNormalizedSummary(transaction.Narration ?? string.Empty, transaction.Mode);
            if (exactSummary.Length == 0 || ExcelLedgerMappingParser.IsLedgerMappingNoiseSummary(exactSummary))
                exactSummary = BankNarrationParser.BuildLedgerMappingFallbackSummary(transaction.Narration ?? string.Empty, transaction.Mode);
            if (exactSummary.Length == 0 || ExcelLedgerMappingParser.IsLedgerMappingNoiseSummary(exactSummary)) continue;
            var canonicalSummary = groupMap.TryGetValue(exactSummary, out var representative)
                ? representative
                : exactSummary;
            if (canonicalSummary.Length == 0) continue;

            var sourceLedger = transaction.LedgerName?.Trim() ?? string.Empty;
            var key = BuildLedgerDateCountKey(bank, transaction.Mode, canonicalSummary, sourceLedger);
            result[key] = result.TryGetValue(key, out var count) ? count + 1 : 1;
            var mixedKey = BuildLedgerDateCountKey(bank, "Mixed", canonicalSummary, sourceLedger);
            result[mixedKey] = result.TryGetValue(mixedKey, out var mixedCount) ? mixedCount + 1 : 1;
        }
        return result;
    }

    private static string BuildLedgerDateCountKey(
        string bank,
        string voucherType,
        string summaryName,
        string? sourceLedgerName) =>
        string.Join(
            "|",
            bank.Trim().ToUpperInvariant(),
            voucherType.Trim().ToUpperInvariant(),
            BankNarrationParser.Normalize(summaryName),
            (sourceLedgerName ?? string.Empty).Trim().ToUpperInvariant());

    private string LedgerMappingPreferenceKey(string suffix, string? financialYear = null)
    {
        var fy = string.IsNullOrWhiteSpace(financialYear) ? CurrentFinancialYear : financialYear.Trim();
        return BankingPreferenceKeys.LedgerMapping(fy, suffix);
    }

    private void PersistLedgerMappingPreference(string suffix, string value)
    {
        if (_loadingLedgerMappingPreferences || _isBuildingLedgerMappingOptions || CurrentClientId <= 0) return;
        try { _clientPreferences.Set(CurrentClientId, LedgerMappingPreferenceKey(suffix), value ?? string.Empty); }
        catch { /* preference persistence must never block accounting work */ }
    }

    private void EnsureLedgerMappingPreferencesLoaded(long clientId, string financialYear)
    {
        if (_ledgerMappingPreferencesClientId == clientId &&
            _ledgerMappingPreferencesFinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)) return;
        LoadLedgerMappingPreferences(clientId, financialYear);
        _ledgerMappingPreferencesClientId = clientId;
        _ledgerMappingPreferencesFinancialYear = financialYear.Trim();
    }

    private void LoadLedgerMappingPreferences(long clientId, string financialYear)
    {
        _loadingLedgerMappingPreferences = true;
        try
        {
            // Client/FY preferences are isolated. A client without a saved Bank Statement
            // scope must not inherit Tally Export Dates from the previously opened client.
            _bankStatementDateScope = "Full Statement";

            string Read(string suffix)
            {
                try { return _clientPreferences.Get(clientId, LedgerMappingPreferenceKey(suffix, financialYear)); }
                catch { return string.Empty; }
            }

            var bank = Read("BankFilter");
            var mapped = Read("ShowMapped");
            var voucher = Read("VoucherFilter");
            var method = Read("MethodFilter");
            var dateScope = Read("DateScope");
            var bankStatementDateScope = Read("BankStatementDateScope");
            var defaultEmptyLedger = Read("DefaultEmptyLedger");
            var column = Read("ColumnFilter");
            var text = Read("TextFilter");
            var create = Read("CreateLedgerWhenApplying");
            var projectionVersion = Read("ProjectionVersion");
            _ledgerMappingProjectionUpgradeRequired = !projectionVersion.Equals(LedgerMappingProjectionVersion, StringComparison.Ordinal);

            _ledgerMappingMethodFilter = "ALL METHODS";
            if (!string.IsNullOrWhiteSpace(bank)) _ledgerMappingBankFilter = NormalizeLedgerMappingBankFilter(bank);
            if (!string.IsNullOrWhiteSpace(mapped)) _ledgerMappingShowMapped = NormalizeLedgerMappingShowMapped(mapped);
            if (!string.IsNullOrWhiteSpace(voucher)) _ledgerMappingVoucherFilter = NormalizeLedgerMappingVoucherFilter(voucher);
            if (!string.IsNullOrWhiteSpace(method)) _ledgerMappingMethodFilter = NormalizeLedgerMappingMethodFilter(method);
            if (!string.IsNullOrWhiteSpace(dateScope))
                _ledgerMappingDateScope = dateScope.Equals("Tally Export Dates", StringComparison.OrdinalIgnoreCase) ? "Tally Export Dates" : "Full Statement";
            if (!string.IsNullOrWhiteSpace(bankStatementDateScope))
                _bankStatementDateScope = bankStatementDateScope.Equals("Tally Export Dates", StringComparison.OrdinalIgnoreCase) ? "Tally Export Dates" : "Full Statement";
            _defaultEmptyLedger = string.IsNullOrWhiteSpace(defaultEmptyLedger)
                ? BankingPreferenceKeys.DefaultEmptyLedgerName
                : AccountingText.DisplayName(defaultEmptyLedger);
            if (_defaultEmptyLedger.Equals(BankingPreferenceKeys.LegacyDefaultEmptyLedgerName, StringComparison.OrdinalIgnoreCase))
            {
                _defaultEmptyLedger = BankingPreferenceKeys.DefaultEmptyLedgerName;
                try { _clientPreferences.Set(clientId, LedgerMappingPreferenceKey("DefaultEmptyLedger", financialYear), _defaultEmptyLedger); }
                catch { /* legacy preference migration must never block Banking */ }
            }
            if (_defaultEmptyLedger.Length == 0) _defaultEmptyLedger = BankingPreferenceKeys.DefaultEmptyLedgerName;
            if (!string.IsNullOrWhiteSpace(column)) _ledgerMappingColumnFilter = column.Trim();
            if (text is not null) _ledgerMappingTextFilter = text;
            if (!string.IsNullOrWhiteSpace(create)) _createLedgerWhenApplying = !create.Equals("NO", StringComparison.OrdinalIgnoreCase);

            OnPropertyChanged(nameof(LedgerMappingBankFilter));
            OnPropertyChanged(nameof(LedgerMappingShowMapped));
            OnPropertyChanged(nameof(IsLedgerMappingUnmapped));
            OnPropertyChanged(nameof(IsLedgerMappingMapped));
            OnPropertyChanged(nameof(IsLedgerMappingAll));
            OnPropertyChanged(nameof(LedgerMappingVoucherFilter));
            OnPropertyChanged(nameof(LedgerMappingMethodFilter));
            OnPropertyChanged(nameof(LedgerMappingDateScope));
            OnPropertyChanged(nameof(IsLedgerMappingFullStatement));
            OnPropertyChanged(nameof(IsLedgerMappingTallyExportDates));
            OnPropertyChanged(nameof(BankStatementDateScope));
            OnPropertyChanged(nameof(IsBankStatementFullStatement));
            OnPropertyChanged(nameof(IsBankStatementTallyExportDates));
            OnPropertyChanged(nameof(BankStatementDateScopeText));
            _emptyLedgerDraftName = _defaultEmptyLedger;
            OnPropertyChanged(nameof(DefaultEmptyLedger));
            OnPropertyChanged(nameof(EmptyLedgerDraftName));
            OnPropertyChanged(nameof(DefaultEmptyLedgerStatusText));
            OnPropertyChanged(nameof(LedgerMappingColumnFilter));
            OnPropertyChanged(nameof(LedgerMappingTextFilter));
            OnPropertyChanged(nameof(CreateLedgerWhenApplying));
        }
        finally
        {
            _loadingLedgerMappingPreferences = false;
        }
    }

    public void PrepareEmptyLedgerExportPanel()
    {
        if (CurrentClientId > 0 && !string.IsNullOrWhiteSpace(CurrentFinancialYear))
        {
            _activeMasterRecords = _masterRepository.Get(CurrentClientId, CurrentFinancialYear, false, string.Empty);
        }
        _emptyLedgerDraftName = AccountingText.DisplayName(DefaultEmptyLedger);
        if (_emptyLedgerDraftName.Length == 0) _emptyLedgerDraftName = BankingPreferenceKeys.DefaultEmptyLedgerName;
        OnPropertyChanged(nameof(EmptyLedgerDraftName));
        RefreshEmptyLedgerMasterPanelState();
        NotifyEmptyLedgerCounts();
    }

    private static bool IsEmptyLedgerForExport(BankTransactionRecord row)
    {
        var ledger = row.LedgerName?.Trim() ?? string.Empty;
        return ledger.Length == 0 ||
               ledger.Equals("Suspense A/c", StringComparison.OrdinalIgnoreCase) ||
               ledger.Equals("Suspense A/C", StringComparison.OrdinalIgnoreCase);
    }

    private void NotifyEmptyLedgerCounts()
    {
        OnPropertyChanged(nameof(EmptyLedgerFullStatementCount));
        OnPropertyChanged(nameof(EmptyLedgerExportPeriodCount));
        OnPropertyChanged(nameof(DefaultEmptyLedgerStatusText));
    }

    private void RefreshEmptyLedgerMasterPanelState()
    {
        if (CurrentClientId <= 0 || string.IsNullOrWhiteSpace(CurrentFinancialYear))
        {
            EmptyLedgerResolvedName = string.Empty;
            EmptyLedgerResolvedGroup = string.Empty;
            EmptyLedgerResolvedParentGroup = string.Empty;
            EmptyLedgerMasterSourceText = string.Empty;
            EmptyLedgerMasterStatusText = "No client/FY selected.";
            EmptyLedgerGroupStatusText = "No client/FY selected.";
            EmptyLedgerParentGroupStatusText = "No client/FY selected.";
            OnPropertyChanged(nameof(HasExistingEmptyLedgerMaster));
            OnPropertyChanged(nameof(EmptyLedgerHierarchyText));
            return;
        }

        var requested = AccountingText.DisplayName(_emptyLedgerDraftName);
        if (requested.Length == 0) requested = BankingPreferenceKeys.DefaultEmptyLedgerName;
        var key = BankNarrationParser.NormalizeLedgerExactMatchKey(requested);
        var matches = _activeMasterRecords
            .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !item.IsArchived)
            .Where(item => BankNarrationParser.NormalizeLedgerExactMatchKey(item.Name).Equals(key, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.PersistentId)
            .Select(group => group.First())
            .Take(2)
            .ToArray();

        if (matches.Length == 1)
        {
            var master = matches[0];
            EmptyLedgerResolvedName = master.Name.Trim();
            EmptyLedgerResolvedGroup = master.Parent?.Trim() ?? string.Empty;
            EmptyLedgerMasterSourceText = string.IsNullOrWhiteSpace(master.Source) ? "Existing Master" : master.Source.Trim();
            EmptyLedgerMasterStatusText = "Existing Ledger Master found. It will be reused; no duplicate Master will be created.";
            _emptyLedgerDraftGroup = EmptyLedgerResolvedGroup.Length > 0 ? EmptyLedgerResolvedGroup : BankingPreferenceKeys.DefaultEmptyLedgerParent;
            OnPropertyChanged(nameof(EmptyLedgerDraftGroup));
        }
        else if (matches.Length > 1)
        {
            EmptyLedgerResolvedName = string.Empty;
            EmptyLedgerResolvedGroup = string.Empty;
            EmptyLedgerMasterSourceText = string.Empty;
            EmptyLedgerMasterStatusText = "Multiple equivalent Ledger Masters exist. Resolve the duplicate Masters before using this export fallback.";
        }
        else
        {
            EmptyLedgerResolvedName = string.Empty;
            EmptyLedgerResolvedGroup = string.Empty;
            EmptyLedgerMasterSourceText = "Manual Master required";
            EmptyLedgerMasterStatusText = "No existing Ledger Master matches this name. Select a valid Ledger Group and create the Manual Master first.";
            if (string.IsNullOrWhiteSpace(_emptyLedgerDraftGroup))
            {
                _emptyLedgerDraftGroup = BankingPreferenceKeys.DefaultEmptyLedgerParent;
                OnPropertyChanged(nameof(EmptyLedgerDraftGroup));
            }
        }

        RefreshEmptyLedgerGroupHierarchyState();
        OnPropertyChanged(nameof(HasExistingEmptyLedgerMaster));
        OnPropertyChanged(nameof(EmptyLedgerHierarchyText));
    }

    private void RefreshEmptyLedgerGroupHierarchyState()
    {
        var groupName = AccountingText.DisplayName(
            EmptyLedgerResolvedGroup.Length > 0 ? EmptyLedgerResolvedGroup : _emptyLedgerDraftGroup);

        if (groupName.Length == 0)
        {
            EmptyLedgerResolvedParentGroup = string.Empty;
            EmptyLedgerGroupStatusText = "Select a Ledger Group.";
            EmptyLedgerParentGroupStatusText = "Parent Group cannot be resolved until the Ledger Group is selected.";
            return;
        }

        if (groupName.Equals("Primary", StringComparison.OrdinalIgnoreCase))
        {
            EmptyLedgerResolvedParentGroup = "Primary";
            EmptyLedgerGroupStatusText = "Primary is the root Master Group.";
            EmptyLedgerParentGroupStatusText = "Primary is the top-level parent.";
            return;
        }

        var groupKey = AccountingText.DuplicateKey(groupName);
        var groupMatches = _activeMasterRecords
            .Where(item => item.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && !item.IsArchived)
            .Where(item => AccountingText.DuplicateKey(item.Name) == groupKey)
            .GroupBy(item => item.PersistentId)
            .Select(items => items.First())
            .Take(2)
            .ToArray();

        if (groupMatches.Length == 0)
        {
            EmptyLedgerResolvedParentGroup = string.Empty;
            EmptyLedgerGroupStatusText = $"Ledger Group '{groupName}' is not present in the active Master list.";
            EmptyLedgerParentGroupStatusText = "Parent Group is unavailable because the Ledger Group is missing.";
            return;
        }

        if (groupMatches.Length > 1)
        {
            EmptyLedgerResolvedParentGroup = string.Empty;
            EmptyLedgerGroupStatusText = $"Multiple active Group Masters match '{groupName}'.";
            EmptyLedgerParentGroupStatusText = "Resolve the duplicate Group Masters before creating the Ledger.";
            return;
        }

        var groupMaster = groupMatches[0];
        var exactGroupName = groupMaster.Name.Trim();
        var parent = groupMaster.Parent?.Trim() ?? string.Empty;
        if (parent.Length == 0) parent = "Primary";
        EmptyLedgerResolvedParentGroup = parent;
        EmptyLedgerGroupStatusText = $"Existing Group Master found: {exactGroupName}.";

        if (parent.Equals("Primary", StringComparison.OrdinalIgnoreCase))
        {
            EmptyLedgerParentGroupStatusText = "Parent Group: Primary (root).";
            return;
        }

        var parentKey = AccountingText.DuplicateKey(parent);
        var parentMatches = _activeMasterRecords
            .Where(item => item.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && !item.IsArchived)
            .Where(item => AccountingText.DuplicateKey(item.Name) == parentKey)
            .GroupBy(item => item.PersistentId)
            .Select(items => items.First())
            .Take(2)
            .ToArray();

        EmptyLedgerParentGroupStatusText = parentMatches.Length switch
        {
            1 => $"Existing Parent Group found: {parentMatches[0].Name.Trim()}.",
            > 1 => $"Multiple active Parent Groups match '{parent}'.",
            _ => $"Parent Group '{parent}' is not present in the active Master list."
        };
    }

    private void UseEmptyLedgerMaster()
    {
        if (!DemandGovernancePermission(GovernancePermissions.LedgerMappingEdit, "select a Ledger Mapping Master")) return;
        RefreshEmptyLedgerMasterPanelState();
        if (!HasExistingEmptyLedgerMaster)
        {
            MessageBox.Show(EmptyLedgerMasterStatusText, "Empty Ledger Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DefaultEmptyLedger = EmptyLedgerResolvedName;
        _emptyLedgerDraftName = EmptyLedgerResolvedName;
        _emptyLedgerDraftGroup = EmptyLedgerResolvedGroup;
        OnPropertyChanged(nameof(EmptyLedgerDraftName));
        OnPropertyChanged(nameof(EmptyLedgerDraftGroup));
        StatusText = $"Empty Ledger Export default set to '{DefaultEmptyLedger}' ({EmptyLedgerResolvedGroup}).";
    }

    private void CreateEmptyLedgerManualMaster()
    {
        if (!DemandGovernancePermission(GovernancePermissions.LedgerMappingEdit, "create a Ledger from Ledger Mapping")) return;
        if (CurrentClientId <= 0) return;
        var name = AccountingText.DisplayName(EmptyLedgerDraftName);
        var group = AccountingText.DisplayName(EmptyLedgerDraftGroup);
        if (name.Length == 0)
        {
            MessageBox.Show("Enter a Ledger name first.", "Empty Ledger Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _activeMasterRecords = _masterRepository.Get(CurrentClientId, CurrentFinancialYear, false, string.Empty);
        RefreshEmptyLedgerMasterPanelState();
        if (HasExistingEmptyLedgerMaster)
        {
            UseEmptyLedgerMaster();
            return;
        }

        if (group.Length == 0) group = BankingPreferenceKeys.DefaultEmptyLedgerParent;
        var groupKey = AccountingText.DuplicateKey(group);
        var groupMatches = _activeMasterRecords
            .Where(item => item.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && !item.IsArchived)
            .Where(item => AccountingText.DuplicateKey(item.Name) == groupKey)
            .GroupBy(item => item.PersistentId)
            .Select(items => items.First())
            .Take(2)
            .ToArray();
        if (groupMatches.Length != 1)
        {
            MessageBox.Show($"Ledger Group '{group}' must match one active Master Group before the Manual Ledger can be created.",
                "Empty Ledger Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var record = new MasterRecord
        {
            ClientId = CurrentClientId,
            FinancialYear = CurrentFinancialYear,
            Name = name,
            Parent = groupMatches[0].Name,
            Type = "Ledger",
            Company = _clientContext.CurrentClient?.ClientName ?? string.Empty,
            Source = "Manual (Bank Empty-Ledger Export)",
            IsArchived = false,
            IsUserEdited = true
        };

        try
        {
            _masterRepository.Save(record);
            _activeMasterRecords = _masterRepository.Get(CurrentClientId, CurrentFinancialYear, false, string.Empty);
            MasterLedgerNames.Clear();
            MasterGroupNames.Clear();
            CashInHandLedgerNames.Clear();
            BankLedgerRenameOptions.Clear();
            LoadMasterLists(CurrentClientId, CurrentFinancialYear, _activeMasterRecords);
            _emptyLedgerDraftName = name;
            OnPropertyChanged(nameof(EmptyLedgerDraftName));
            RefreshEmptyLedgerMasterPanelState();
            UseEmptyLedgerMaster();
            _liveChangeHub.Publish(CurrentClientId, CurrentFinancialYear, AccountingLiveArea.Masters, "Empty Ledger Export Manual Master created");
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Empty Ledger Export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string NormalizeLedgerMappingBankFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "ALL BANKS" : value.Trim();

    private static string NormalizeLedgerMappingShowMapped(string? value)
    {
        if (string.Equals(value, "YES", StringComparison.OrdinalIgnoreCase)) return "YES";
        if (string.Equals(value, "ALL", StringComparison.OrdinalIgnoreCase)) return "ALL";
        return "NO";
    }

    private static string NormalizeLedgerMappingVoucherFilter(string? value)
    {
        if (string.Equals(value, "Payment", StringComparison.OrdinalIgnoreCase)) return "Payment";
        if (string.Equals(value, "Receipt", StringComparison.OrdinalIgnoreCase)) return "Receipt";
        return "ALL TYPES";
    }

    private static string NormalizeLedgerMappingMethodFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("ALL METHODS", StringComparison.OrdinalIgnoreCase)
            ? "ALL METHODS"
            : value.Trim().ToUpperInvariant();

    private string GetLedgerMappingMethod(BankTransactionRecord transaction)
    {
        if (transaction.Id > 0 && _ledgerMappingMethodByTransactionId.TryGetValue(transaction.Id, out var method)) return method;
        return BankNarrationParser.DetectMethod(transaction.Narration ?? string.Empty);
    }

    private void RestoreLedgerMappingFilters(string? bankFilter, string? showMapped, string? voucherFilter)
    {
        _isBuildingLedgerMappingOptions = true;
        _ledgerMappingBankFilter = LedgerMappingBankOptions.Contains(NormalizeLedgerMappingBankFilter(bankFilter), StringComparer.OrdinalIgnoreCase)
            ? LedgerMappingBankOptions.First(item => string.Equals(item, NormalizeLedgerMappingBankFilter(bankFilter), StringComparison.OrdinalIgnoreCase))
            : "ALL BANKS";
        _ledgerMappingShowMapped = NormalizeLedgerMappingShowMapped(showMapped);
        _ledgerMappingVoucherFilter = NormalizeLedgerMappingVoucherFilter(voucherFilter);
        _ledgerMappingMethodFilter = LedgerMappingMethodOptions.Contains(NormalizeLedgerMappingMethodFilter(_ledgerMappingMethodFilter), StringComparer.OrdinalIgnoreCase)
            ? LedgerMappingMethodOptions.First(item => string.Equals(item, NormalizeLedgerMappingMethodFilter(_ledgerMappingMethodFilter), StringComparison.OrdinalIgnoreCase))
            : "ALL METHODS";
        OnPropertyChanged(nameof(LedgerMappingBankFilter));
        OnPropertyChanged(nameof(LedgerMappingMethodFilter));
        OnPropertyChanged(nameof(LedgerMappingShowMapped));
        OnPropertyChanged(nameof(IsLedgerMappingUnmapped));
        OnPropertyChanged(nameof(IsLedgerMappingMapped));
        OnPropertyChanged(nameof(IsLedgerMappingAll));
        OnPropertyChanged(nameof(LedgerMappingVoucherFilter));
        _isBuildingLedgerMappingOptions = false;
        PersistLedgerMappingPreference("BankFilter", _ledgerMappingBankFilter);
        PersistLedgerMappingPreference("ShowMapped", _ledgerMappingShowMapped);
        PersistLedgerMappingPreference("VoucherFilter", _ledgerMappingVoucherFilter);
        PersistLedgerMappingPreference("MethodFilter", _ledgerMappingMethodFilter);
        ApplyLedgerMappingFilters();
    }

    private void ApplyLedgerMappingFilters()
    {
        var selectedKey = SelectedLedgerMappingRow is null ? string.Empty : LedgerMappingWorkbenchStableKey(SelectedLedgerMappingRow);
        var visibleRows = new List<LedgerMappingWorkbenchRow>();
        var scopedPendingTransactions = 0;
        var scopedMappedTransactions = 0;
        var paymentOnly = string.Equals(LedgerMappingVoucherFilter, "Payment", StringComparison.OrdinalIgnoreCase);
        var receiptOnly = string.Equals(LedgerMappingVoucherFilter, "Receipt", StringComparison.OrdinalIgnoreCase);
        var methodOnly = !LedgerMappingMethodFilter.Equals("ALL METHODS", StringComparison.OrdinalIgnoreCase);
        var useTallyExportDates = LedgerMappingDateScope.Equals("Tally Export Dates", StringComparison.OrdinalIgnoreCase);
        var mappedTransactionIds = LedgerMappings
            .Where(mapping => mapping.Status.Equals("Mapped", StringComparison.OrdinalIgnoreCase))
            .SelectMany(mapping => mapping.TransactionIds)
            .Where(id => id > 0)
            .ToHashSet();
        IReadOnlyList<TallyVoucherExportRange> tallyRanges = [];
        var hasTallyRanges = !useTallyExportDates || _tallyExportSelectionContext.TryGetBankRanges(
            CurrentClientId, CurrentFinancialYear, out tallyRanges, out _);

        foreach (var row in LedgerMappingRows)
        {
            if (row.TransactionFacets.Count > 0)
            {
                IEnumerable<LedgerMappingTransactionFacet> scopedFacets = row.TransactionFacets;
                if (!LedgerMappingBankFilter.Equals("ALL BANKS", StringComparison.OrdinalIgnoreCase))
                    scopedFacets = scopedFacets.Where(item => item.Bank.Equals(LedgerMappingBankFilter, StringComparison.OrdinalIgnoreCase));
                if (paymentOnly) scopedFacets = scopedFacets.Where(item => item.VoucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase));
                else if (receiptOnly) scopedFacets = scopedFacets.Where(item => item.VoucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase));
                if (methodOnly) scopedFacets = scopedFacets.Where(item => item.Method.Equals(LedgerMappingMethodFilter, StringComparison.OrdinalIgnoreCase));
                if (useTallyExportDates) scopedFacets = hasTallyRanges
                    ? scopedFacets.Where(item => tallyRanges.Any(range => range.Contains(item.Date)))
                    : Array.Empty<LedgerMappingTransactionFacet>();

                var baseScoped = scopedFacets.ToArray();
                scopedMappedTransactions += baseScoped.Count(item => mappedTransactionIds.Contains(item.TransactionId));
                scopedPendingTransactions += baseScoped.Count(item => !mappedTransactionIds.Contains(item.TransactionId));
                if (LedgerMappingShowMapped.Equals("YES", StringComparison.OrdinalIgnoreCase))
                    scopedFacets = baseScoped.Where(item => mappedTransactionIds.Contains(item.TransactionId));
                else if (LedgerMappingShowMapped.Equals("NO", StringComparison.OrdinalIgnoreCase))
                    scopedFacets = baseScoped.Where(item => !mappedTransactionIds.Contains(item.TransactionId));
                else
                    scopedFacets = baseScoped;

                var scoped = scopedFacets.ToArray();
                row.Count = scoped.Length;
                if (row.Count <= 0) continue;
                var hasPayments = scoped.Any(item => item.VoucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase));
                var hasReceipts = scoped.Any(item => item.VoucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase));
                row.Type = hasPayments && hasReceipts ? "Mixed" : hasPayments ? "Payment" : hasReceipts ? "Receipt" : row.Mapping.VoucherType;
            }
            else
            {
                // Backward-compatible mappings without frozen transaction membership cannot
                // prove a Method value. They remain available under ALL METHODS and regain
                // exact Method filtering after the next explicit Mapping rebuild.
                if (methodOnly) continue;
                if (paymentOnly && row.FullStatementPaymentCount <= 0) continue;
                if (receiptOnly && row.FullStatementReceiptCount <= 0) continue;
                row.Type = paymentOnly ? "Payment" : receiptOnly ? "Receipt" : row.Mapping.VoucherType;
                row.Count = useTallyExportDates
                    ? paymentOnly ? row.TallyExportPaymentCount
                        : receiptOnly ? row.TallyExportReceiptCount
                        : row.TallyExportDateCount
                    : paymentOnly ? row.FullStatementPaymentCount
                        : receiptOnly ? row.FullStatementReceiptCount
                        : row.FullStatementCount;
                if (row.Count <= 0 && (paymentOnly || receiptOnly || useTallyExportDates)) continue;
                var legacyIds = GetVisibleLedgerMappingTransactionIds(row, applyMappedState: false);
                scopedMappedTransactions += legacyIds.Count(id => mappedTransactionIds.Contains(id));
                scopedPendingTransactions += legacyIds.Count(id => !mappedTransactionIds.Contains(id));
                if (LedgerMappingShowMapped.Equals("YES", StringComparison.OrdinalIgnoreCase)) legacyIds = legacyIds.Where(mappedTransactionIds.Contains).ToHashSet();
                else if (LedgerMappingShowMapped.Equals("NO", StringComparison.OrdinalIgnoreCase)) legacyIds = legacyIds.Where(id => !mappedTransactionIds.Contains(id)).ToHashSet();
                row.Count = legacyIds.Count;
                if (row.Count <= 0) continue;
            }

            // Pending/Mapped figures beside Visible must use the SAME date, bank and
            // voucher scope as the rows underneath.  Previously they counted every
            // full-statement summary row, so Export Dates could say 27 pending while
            // only 8 pending groups were actually eligible to be shown.
            // R44: Pending/Mapped is transaction-ID authority, not a whole-summary-row label.
            // Partially mapped groups remain visible in the selected state with only their
            // matching transaction members/counts.
            if (!MatchesLedgerMappingColumnFilter(row, LedgerMappingColumnFilter, LedgerMappingTextFilter)) continue;
            visibleRows.Add(row);
        }
        _ledgerMappingScopedPendingTransactionCount = scopedPendingTransactions;
        _ledgerMappingScopedMappedTransactionCount = scopedMappedTransactions;
        _ledgerMappingVisibleTransactionCount = visibleRows.Sum(row => Math.Max(0, row.Count));
        // Fallback/guard count is calculated from authoritative Bank Statement transaction IDs,
        // not from the rows we just rendered. If the view silently drops a mapping group (for
        // example because of stale bank text after a rename), this becomes MISMATCH instead of
        // falsely reporting that the reduced view is complete.
        _ledgerMappingExpectedTransactionCount = CalculateExpectedLedgerMappingTransactionCount();
        var sameVisibleRows = FilteredLedgerMappingRows.Count == visibleRows.Count &&
                              FilteredLedgerMappingRows.Zip(visibleRows, (left, right) => ReferenceEquals(left, right)).All(equal => equal);
        if (!sameVisibleRows)
            FilteredLedgerMappingRows.ReplaceRange(visibleRows);
        RefreshLedgerMappingActionSelectionState();
        SelectedLedgerMappingRow = selectedKey.Length > 0
            ? FilteredLedgerMappingRows.FirstOrDefault(item => LedgerMappingWorkbenchStableKey(item).Equals(selectedKey, StringComparison.OrdinalIgnoreCase))
            : null;
        OnPropertyChanged(nameof(LedgerMappingVisibleCount));
        OnPropertyChanged(nameof(LedgerMappingDateScopeText));
        OnPropertyChanged(nameof(LedgerMappingFilterCountText));
        OnPropertyChanged(nameof(LedgerMappingCoverageMatchState));
    }

    private IReadOnlySet<long> GetVisibleLedgerMappingTransactionIds(LedgerMappingWorkbenchRow row, bool applyMappedState = true)
    {
        if (row.TransactionFacets.Count > 0)
        {
            IEnumerable<LedgerMappingTransactionFacet> facets = row.TransactionFacets;
            if (!LedgerMappingBankFilter.Equals("ALL BANKS", StringComparison.OrdinalIgnoreCase))
                facets = facets.Where(item => item.Bank.Equals(LedgerMappingBankFilter, StringComparison.OrdinalIgnoreCase));
            if (LedgerMappingVoucherFilter.Equals("Payment", StringComparison.OrdinalIgnoreCase))
                facets = facets.Where(item => item.VoucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase));
            else if (LedgerMappingVoucherFilter.Equals("Receipt", StringComparison.OrdinalIgnoreCase))
                facets = facets.Where(item => item.VoucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase));
            if (!LedgerMappingMethodFilter.Equals("ALL METHODS", StringComparison.OrdinalIgnoreCase))
                facets = facets.Where(item => item.Method.Equals(LedgerMappingMethodFilter, StringComparison.OrdinalIgnoreCase));
            if (LedgerMappingDateScope.Equals("Tally Export Dates", StringComparison.OrdinalIgnoreCase))
            {
                if (!_tallyExportSelectionContext.TryGetBankRanges(CurrentClientId, CurrentFinancialYear, out var ranges, out _))
                    return new HashSet<long>();
                facets = facets.Where(item => ranges.Any(range => range.Contains(item.Date)));
            }
            var facetIds = facets.Select(item => item.TransactionId).Where(id => id > 0).ToHashSet();
            if (applyMappedState) ApplyLedgerMappingStateFilter(facetIds);
            return facetIds;
        }

        // Legacy fallback: retain only provable frozen membership. Current rebuilt mappings
        // should always have TransactionFacets; this branch exists solely for older caches.
        var ids = (row.MemberMappings.Count > 0 ? row.MemberMappings : new[] { row.Mapping })
            .SelectMany(mapping => mapping.TransactionIds)
            .Where(id => id > 0)
            .Distinct()
            .ToHashSet();
        if (ids.Count == 0) return ids;
        if (LedgerMappingVoucherFilter.Equals("Payment", StringComparison.OrdinalIgnoreCase) ||
            LedgerMappingVoucherFilter.Equals("Receipt", StringComparison.OrdinalIgnoreCase))
        {
            var type = LedgerMappingVoucherFilter;
            ids.IntersectWith(Transactions.Where(item => item.Mode.Equals(type, StringComparison.OrdinalIgnoreCase)).Select(item => item.Id));
        }
        if (applyMappedState) ApplyLedgerMappingStateFilter(ids);
        return ids;
    }

    private void ApplyLedgerMappingStateFilter(HashSet<long> ids)
    {
        if (ids.Count == 0 || LedgerMappingShowMapped.Equals("ALL", StringComparison.OrdinalIgnoreCase)) return;
        var mapped = Transactions
            .Where(transaction => transaction.Id > 0 &&
                (transaction.MappingStatus.Equals("Mapped", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(transaction.LedgerName)))
            .Select(transaction => transaction.Id)
            .ToHashSet();
        if (LedgerMappingShowMapped.Equals("YES", StringComparison.OrdinalIgnoreCase)) ids.IntersectWith(mapped);
        else ids.RemoveWhere(id => mapped.Contains(id));
    }

    private IReadOnlyList<BankLedgerMapping> BuildVisibleLedgerMappingFragments(LedgerMappingWorkbenchRow row)
    {
        var visibleIds = GetVisibleLedgerMappingTransactionIds(row);
        if (visibleIds.Count == 0) return [];
        var scopedMembers = new List<BankLedgerMapping>();
        foreach (var mapping in row.MemberMappings.Count > 0 ? row.MemberMappings : new[] { row.Mapping })
        {
            var ids = mapping.TransactionIds.Where(visibleIds.Contains).Distinct().ToArray();
            if (ids.Length == 0) continue;
            scopedMembers.Add(CloneBulkVoucherTypeFragment(mapping, mapping.VoucherType, ids));
        }
        return BuildBulkVoucherTypeFragments(scopedMembers, string.Empty);
    }

    private int CalculateExpectedLedgerMappingTransactionCount()
    {
        IEnumerable<BankTransactionRecord> eligible = Transactions
            .Where(item => item.Id > 0 && !item.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase));

        if (!LedgerMappingBankFilter.Equals("ALL BANKS", StringComparison.OrdinalIgnoreCase))
            eligible = eligible.Where(item => item.BankAccountName.Equals(LedgerMappingBankFilter, StringComparison.OrdinalIgnoreCase));

        if (LedgerMappingVoucherFilter.Equals("Payment", StringComparison.OrdinalIgnoreCase))
            eligible = eligible.Where(item => item.Mode.Equals("Payment", StringComparison.OrdinalIgnoreCase));
        else if (LedgerMappingVoucherFilter.Equals("Receipt", StringComparison.OrdinalIgnoreCase))
            eligible = eligible.Where(item => item.Mode.Equals("Receipt", StringComparison.OrdinalIgnoreCase));

        if (!LedgerMappingMethodFilter.Equals("ALL METHODS", StringComparison.OrdinalIgnoreCase))
            eligible = eligible.Where(item => GetLedgerMappingMethod(item).Equals(LedgerMappingMethodFilter, StringComparison.OrdinalIgnoreCase));

        if (LedgerMappingDateScope.Equals("Tally Export Dates", StringComparison.OrdinalIgnoreCase))
        {
            if (!_tallyExportSelectionContext.TryGetBankRanges(CurrentClientId, CurrentFinancialYear, out var ranges, out _))
                return 0;
            eligible = eligible.Where(item => ranges.Any(range => range.Contains(item.TransactionDate)));
        }

        var eligibleIds = eligible.Select(item => item.Id).ToHashSet();
        if (eligibleIds.Count == 0) return 0;

        var mappedIds = LedgerMappings
            .Where(mapping => mapping.Status.Equals("Mapped", StringComparison.OrdinalIgnoreCase))
            .SelectMany(mapping => mapping.TransactionIds)
            .Where(eligibleIds.Contains)
            .ToHashSet();

        if (LedgerMappingShowMapped.Equals("YES", StringComparison.OrdinalIgnoreCase)) return mappedIds.Count;
        if (LedgerMappingShowMapped.Equals("NO", StringComparison.OrdinalIgnoreCase)) return eligibleIds.Count - mappedIds.Count;
        return eligibleIds.Count;
    }

    private static bool MatchesLedgerMappingColumnFilter(
        LedgerMappingWorkbenchRow row,
        string column,
        string filterText)
    {
        var needle = (filterText ?? string.Empty).Trim();
        if (needle.Length == 0) return true;
        var exact = needle.StartsWith("=", StringComparison.Ordinal);
        if (exact) needle = needle[1..].Trim();

        bool Has(string? value, string text) => exact
            ? string.Equals((value ?? string.Empty).Trim(), text, StringComparison.OrdinalIgnoreCase)
            : (value ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase);

        return column.ToUpperInvariant() switch
        {
            "COUNT" => Has(row.Count.ToString(), needle),
            "SUMMARY NAME" => Has(row.SummaryName, needle),
            "TYPE" => Has(row.Type, needle),
            "EXACT MATCH" or "EXISTING LEDGER MATCH" => Has(row.ExactMatch, needle),
            "SUGGESTED MATCH" => Has(row.SuggestedMatch, needle),
            "FINAL LEDGER" => Has(row.FinalLedger, needle),
            "LEDGER GROUP" => Has(row.LedgerGroup, needle),
            "STATUS" => Has(row.Status, needle),
            _ => new[]
            {
                row.Count.ToString(), row.SummaryName, row.Type, row.ExactMatch,
                row.SuggestedMatch, row.FinalLedger, row.LedgerGroup, row.Status
            }.Any(value => Has(value, needle))
        };
    }

    private void ApplyBankStatementFilters()
    {
        var selectedId = SelectedTransaction?.Id ?? 0;
        var useTallyExportDates = BankStatementDateScope.Equals("Tally Export Dates", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<TallyVoucherExportRange> tallyRanges = [];
        var hasTallyRanges = !useTallyExportDates || _tallyExportSelectionContext.TryGetBankRanges(
            CurrentClientId,
            CurrentFinancialYear,
            out tallyRanges,
            out _);

        var visible = Transactions
            .Where(row => !useTallyExportDates || (hasTallyRanges && tallyRanges.Any(range => range.Contains(row.TransactionDate))))
            .Where(row => MatchesBankStatementColumnFilter(row, BankStatementFilterColumn, BankStatementFilterText))
            .ToArray();
        FilteredTransactions.ReplaceRange(visible);

        if (selectedId > 0 && FilteredTransactions.All(item => item.Id != selectedId))
            SelectedTransaction = null;

        OnPropertyChanged(nameof(BankStatementVisibleCount));
        OnPropertyChanged(nameof(BankStatementDateScopeText));
    }

    private static bool MatchesBankStatementColumnFilter(
        BankTransactionRecord row,
        string column,
        string filterText)
    {
        var needle = (filterText ?? string.Empty).Trim();
        if (needle.Length == 0) return true;
        var exact = needle.StartsWith("=", StringComparison.Ordinal);
        if (exact) needle = needle[1..].Trim();

        bool Has(string? value, string text) => exact
            ? string.Equals((value ?? string.Empty).Trim(), text, StringComparison.OrdinalIgnoreCase)
            : (value ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase);

        var dateValues = new[]
        {
            row.TransactionDate.ToString("dd.MM.yyyy"),
            row.TransactionDate.ToString("dd/MM/yyyy"),
            row.TransactionDate.ToString("yyyy-MM-dd")
        };
        var debit = row.Debit.ToString("N2");
        var credit = row.Credit.ToString("N2");
        var runningBalance = row.RunningBalance?.ToString("N2") ?? string.Empty;

        return column.ToUpperInvariant() switch
        {
            "DATE" => dateValues.Any(value => Has(value, needle)),
            "NARRATION" => Has(row.Narration, needle),
            "DEBIT" => Has(debit, needle) || Has(row.Debit.ToString(), needle),
            "CREDIT" => Has(credit, needle) || Has(row.Credit.ToString(), needle),
            "RUNNING BALANCE" => Has(runningBalance, needle) || (row.RunningBalance.HasValue && Has(row.RunningBalance.Value.ToString(), needle)),
            "MODE" => Has(row.Mode, needle),
            "LEDGER" => Has(row.LedgerName, needle),
            "BANK" => Has(row.BankAccountName, needle),
            _ => dateValues.Any(value => Has(value, needle)) ||
                 Has(row.Narration, needle) || Has(debit, needle) || Has(credit, needle) || Has(runningBalance, needle) ||
                 Has(row.Mode, needle) || Has(row.LedgerName, needle) || Has(row.BankAccountName, needle)
        };
    }

    private void ApplyLedgerMappingDetailFilter()
    {
        var needle = (_ledgerMappingDetailFilterText ?? string.Empty).Trim();
        var column = string.IsNullOrWhiteSpace(_ledgerMappingDetailFilterColumn) ? "ALL COLUMNS" : _ledgerMappingDetailFilterColumn.Trim();
        var filtered = LedgerMappingDetails.Where(detail => MatchesLedgerMappingDetailFilter(detail, column, needle)).ToList();

        // Completion 64 Build Repair 2: once the user narrows the expanded Summary Name,
        // rows hidden by that active filter are no longer implicit Apply Selected targets.
        // Clearing/changing the filter never silently re-selects those hidden rows.
        if (needle.Length > 0)
        {
            var visibleTransactions = filtered.Select(detail => detail.Transaction.Id).ToHashSet();
            foreach (var hidden in LedgerMappingDetails.Where(detail => !visibleTransactions.Contains(detail.Transaction.Id)))
                hidden.IsSelected = false;
        }

        FilteredLedgerMappingDetails.Clear();
        foreach (var detail in filtered) FilteredLedgerMappingDetails.Add(detail);
        if (SelectedLedgerMappingDetail is not null && !FilteredLedgerMappingDetails.Contains(SelectedLedgerMappingDetail))
            SelectedLedgerMappingDetail = null;
        RefreshLedgerMappingDetailSelectionState();
    }

    private static bool MatchesLedgerMappingDetailFilter(LedgerMappingDetailRow detail, string column, string needle)
    {
        if (needle.Length == 0) return true;
        static bool Has(string? value, string text) => (value ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase);
        var dateValues = new[] { detail.Date.ToString("dd.MM.yyyy"), detail.Date.ToString("dd/MM/yyyy"), detail.Date.ToString("yyyy-MM-dd"), detail.Date.ToString("dd-MM-yyyy") };
        var debit = detail.Debit.ToString("N2");
        var credit = detail.Credit.ToString("N2");
        var rowNo = detail.RowNo.ToString(CultureInfo.InvariantCulture);

        return column.ToUpperInvariant() switch
        {
            "DATE" => dateValues.Any(value => Has(value, needle)),
            "NARRATION" => Has(detail.Narration, needle),
            "DEBIT" => Has(debit, needle) || Has(detail.Debit.ToString(CultureInfo.InvariantCulture), needle),
            "CREDIT" => Has(credit, needle) || Has(detail.Credit.ToString(CultureInfo.InvariantCulture), needle),
            "MODE" => Has(detail.Mode, needle),
            "METHOD" => Has(detail.Method, needle),
            "LEDGER" => Has(detail.Ledger, needle),
            "BANK" => Has(detail.Bank, needle),
            "ROW NO" => Has(rowNo, needle),
            _ => dateValues.Any(value => Has(value, needle)) || Has(detail.Narration, needle) ||
                 Has(debit, needle) || Has(credit, needle) || Has(detail.Mode, needle) || Has(detail.Method, needle) ||
                 Has(detail.Ledger, needle) || Has(detail.Bank, needle) || Has(rowNo, needle)
        };
    }

    private void RefreshLedgerMappingDetails()
    {
        LedgerMappingDetails.Clear();
        FilteredLedgerMappingDetails.Clear();
        var row = SelectedLedgerMappingRow;
        if (row is null || CurrentClientId <= 0) return;

        try
        {
            var accountNames = Accounts.ToDictionary(item => item.Id, item => item.AuthorityName);
            var transactions = Transactions
                .Where(item => !item.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var useTallyExportDates = LedgerMappingDateScope.Equals(
                "Tally Export Dates",
                StringComparison.OrdinalIgnoreCase);
            var hasTallyRange = _tallyExportSelectionContext.TryGetBankRanges(
                CurrentClientId,
                CurrentFinancialYear,
                out var tallyRanges,
                out _);

            var paymentOnly = string.Equals(LedgerMappingVoucherFilter, "Payment", StringComparison.OrdinalIgnoreCase);
            var receiptOnly = string.Equals(LedgerMappingVoucherFilter, "Receipt", StringComparison.OrdinalIgnoreCase);
            var methodOnly = !LedgerMappingMethodFilter.Equals("ALL METHODS", StringComparison.OrdinalIgnoreCase);
            var exactVisibleIds = GetVisibleLedgerMappingTransactionIds(row);
            var frozenIds = row.MemberMappings
                .SelectMany(mapping => mapping.TransactionIds)
                .Where(id => id > 0)
                .ToHashSet();

            if (frozenIds.Count > 0)
            {
                // Fix 4 stores the exact Excel frozen-cache membership. Preview never
                // reparses or widens the group; it expands only those source rows.
                foreach (var item in transactions.Select((transaction, index) => new { Transaction = transaction, RowNo = transaction.SourceRow > 0 ? transaction.SourceRow : index + 2 }))
                {
                    var transaction = item.Transaction;
                    if (!frozenIds.Contains(transaction.Id) || !exactVisibleIds.Contains(transaction.Id)) continue;
                    if (useTallyExportDates && (!hasTallyRange || !tallyRanges.Any(range => range.Contains(transaction.TransactionDate)))) continue;
                    if (paymentOnly && !string.Equals(transaction.Mode, "Payment", StringComparison.OrdinalIgnoreCase)) continue;
                    if (receiptOnly && !string.Equals(transaction.Mode, "Receipt", StringComparison.OrdinalIgnoreCase)) continue;
                    if (methodOnly && !GetLedgerMappingMethod(transaction).Equals(LedgerMappingMethodFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    transaction.BankAccountName = accountNames.TryGetValue(transaction.BankAccountId, out var bank) ? bank?.Trim() ?? string.Empty : string.Empty;
                    LedgerMappingDetails.Add(new LedgerMappingDetailRow
                    {
                        Transaction = transaction,
                        RowNo = item.RowNo,
                        Method = GetLedgerMappingMethod(transaction),
                        IsSelected = true
                    });
                }
            }
            else
            {
                var allowedBanks = row.MemberBanks.Count > 0
                    ? row.MemberBanks
                    : new HashSet<string>(new[] { row.Mapping.BankFilter?.Trim() ?? string.Empty }, StringComparer.OrdinalIgnoreCase);
                var selectedSummary = row.Mapping.SummaryName?.Trim() ?? string.Empty;
                var sourceLedger = row.Mapping.SourceLedgerName?.Trim() ?? string.Empty;
                // Backward-compatible path for mappings created before Fix 4. It is
                // removed naturally as soon as the user performs one explicit rebuild.
                var parsed = transactions
                    .Select((transaction, index) => new
                    {
                        Transaction = transaction,
                        RowNo = transaction.SourceRow > 0 ? transaction.SourceRow : index + 2,
                        Bank = accountNames.TryGetValue(transaction.BankAccountId, out var bank) ? bank?.Trim() ?? string.Empty : string.Empty,
                        SourceLedger = transaction.LedgerName?.Trim() ?? string.Empty,
                        ExactSummary = ExcelLedgerMappingParser.IsLedgerMappingCoverageExcludedMode(transaction.Mode)
                            ? string.Empty
                            : ExcelLedgerMappingParser.BuildNormalizedSummary(transaction.Narration ?? string.Empty, transaction.Mode)
                    })
                    .ToList();
                var summaryCounts = parsed
                    .Where(item => item.ExactSummary.Length > 0)
                    .GroupBy(item => item.ExactSummary, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
                var groupMap = ExcelLedgerMappingParser.BuildFastGroupMap(summaryCounts);

                var bankFilter = row.Mapping.BankFilter?.Trim() ?? string.Empty;
                foreach (var item in parsed)
                {
                    var transaction = item.Transaction;
                    if (!exactVisibleIds.Contains(transaction.Id)) continue;
                    if (useTallyExportDates && (!hasTallyRange || !tallyRanges.Any(range => range.Contains(transaction.TransactionDate)))) continue;
                    if (allowedBanks.Count == 1 && !string.Equals(bankFilter, item.Bank, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!allowedBanks.Contains(item.Bank)) continue;
                    if (!string.Equals(sourceLedger, item.SourceLedger, StringComparison.OrdinalIgnoreCase)) continue;
                    if (paymentOnly && !string.Equals(transaction.Mode, "Payment", StringComparison.OrdinalIgnoreCase)) continue;
                    if (receiptOnly && !string.Equals(transaction.Mode, "Receipt", StringComparison.OrdinalIgnoreCase)) continue;
                    if (methodOnly && !GetLedgerMappingMethod(transaction).Equals(LedgerMappingMethodFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (item.ExactSummary.Length == 0) continue;
                    var canonicalSummary = groupMap.TryGetValue(item.ExactSummary, out var representative) ? representative : item.ExactSummary;
                    if (!string.Equals(canonicalSummary, selectedSummary, StringComparison.OrdinalIgnoreCase)) continue;
                    transaction.BankAccountName = item.Bank;
                    LedgerMappingDetails.Add(new LedgerMappingDetailRow
                    {
                        Transaction = transaction,
                        RowNo = item.RowNo,
                        Method = GetLedgerMappingMethod(transaction),
                        IsSelected = true
                    });
                }
            }

            // Excel synchronizes visible mapping state from the underlying detail rows.
            // One ledger on every row => Mapped; one ledger on only some rows => In Progress;
            // more than one distinct ledger => Split and the aggregate F:G cannot be assumed.
            if (!methodOnly)
            {
                var mappedDetails = LedgerMappingDetails
                    .Where(detail => !string.IsNullOrWhiteSpace(detail.Ledger))
                    .ToList();
                var distinctDetailLedgers = mappedDetails
                    .GroupBy(detail => BankNarrationParser.NormalizeLedgerExactMatchKey(detail.Ledger), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First().Ledger.Trim())
                    .Where(value => value.Length > 0)
                    .ToList();
                if (distinctDetailLedgers.Count > 1)
                {
                    row.FinalLedger = string.Empty;
                    row.LedgerGroup = string.Empty;
                    row.Status = "Split";
                }
                else if (distinctDetailLedgers.Count == 1)
                {
                    row.FinalLedger = distinctDetailLedgers[0];
                    if (TryResolveMasterLedger(row.FinalLedger, out var actualLedger, out var actualGroup))
                    {
                        row.FinalLedger = actualLedger;
                        row.LedgerGroup = actualGroup;
                    }
                    row.Status = mappedDetails.Count == LedgerMappingDetails.Count ? "Mapped" : "In Progress";
                }
            }

            // The summary number and the opened detail rows are one identity. Keep
            // the visible count synchronized with current transactions even when a
            // persisted mapping count is stale after an import.
            row.Count = LedgerMappingDetails.Count;
            // A Method filter is a view-only transaction facet. Never write its reduced
            // detail count back into the full Payment/Receipt/date aggregate counters,
            // otherwise clearing Method would leave a stale reduced summary count.
            if (!methodOnly)
            {
                if (!useTallyExportDates)
                {
                    if (paymentOnly) row.FullStatementPaymentCount = LedgerMappingDetails.Count;
                    else if (receiptOnly) row.FullStatementReceiptCount = LedgerMappingDetails.Count;
                    else row.FullStatementCount = LedgerMappingDetails.Count;
                }
                else
                {
                    if (paymentOnly) row.TallyExportPaymentCount = LedgerMappingDetails.Count;
                    else if (receiptOnly) row.TallyExportReceiptCount = LedgerMappingDetails.Count;
                    else row.TallyExportDateCount = LedgerMappingDetails.Count;
                }
            }
            ApplyLedgerMappingDetailFilter();
            OnPropertyChanged(nameof(SelectedLedgerDetailStatistics));
            RefreshLedgerMappingDetailSelectionState();
        }
        catch (Exception exception)
        {
            StatusText = $"Ledger Mapping detail review could not be loaded: {exception.Message}";
        }
    }

    private void BuildCashEntryMappingRows()
    {
        CashEntryMasterMappings.Clear();
        CashEntryCommonMappings.Clear();
        var masters = _masterRepository.Get(CurrentClientId, CurrentFinancialYear, false, string.Empty);

        // Fix75 MASTERID-safe display-name collision handling. Tally can legitimately
        // contain two distinct Ledger MASTERIDs whose raw names differ only by invisible
        // characters (for example a trailing CR/LF). AAH cleans those names for display,
        // therefore a display name is NOT a unique key. Keep every candidate here and
        // fail closed only when a Cash mapping actually needs an ambiguous display name.
        // Never merge/delete either Master and never choose one MASTERID merely by name.
        var ledgerGroups = masters
            .Where(item => string.Equals(item.Type, "Ledger", StringComparison.OrdinalIgnoreCase) &&
                           !item.IsArchived &&
                           !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.TallyMasterId.Trim().Length == 0 ? 1 : 0)
                    .ThenBy(item => item.TallyMasterId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.PersistentId)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var group in CashEntries.Where(item => !string.IsNullOrWhiteSpace(item.EntryLedger))
                     .GroupBy(item => item.EntryLedger, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var lookupName = group.Key.Trim();
            ledgerGroups.TryGetValue(lookupName, out var candidates);
            candidates ??= Array.Empty<MasterRecord>();

            var identities = candidates
                .Select(item => item.TallyMasterId.Trim().Length > 0
                    ? "T:" + item.TallyMasterId.Trim()
                    : "L:" + item.PersistentId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var ambiguous = identities.Length > 1;
            var mapped = identities.Length == 1;
            var parents = candidates
                .Select(item => item.Parent?.Trim() ?? string.Empty)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var parent = parents.Length == 1 ? parents[0] : "Indirect Expenses";

            CashEntryMasterMappings.Add(new CashEntryMasterMappingRow
            {
                // Missing names may still be selected for the existing create/map flow.
                // Ambiguous names stay unselected because the user-visible name cannot
                // prove which existing Tally MASTERID is intended.
                IsSelected = !mapped && !ambiguous,
                EntryLedger = group.Key,
                EntryCount = group.Count(),
                MasterStatus = ambiguous ? "Ambiguous Tally Master" : mapped ? "Mapped" : "Missing",
                MapCreateAs = group.Key,
                ParentGroup = parent,
                Result = ambiguous
                    ? $"Multiple MASTERIDs: {string.Join(", ", candidates.Select(item => item.TallyMasterId.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))}"
                    : mapped ? "Mapped" : string.Empty
            });
        }

        foreach (var entry in CashEntries.OrderBy(item => item.EntryDate).ThenBy(item => item.Id))
            CashEntryCommonMappings.Add(new CashEntryCommonMappingRow { Entry = entry });
    }

    private void LoadWorkbookReports(long clientId, string financialYear, string clientName)
    {
        var reports = _reportService.Build(clientId, financialYear, clientName);
        foreach (var item in reports.StatementMetadata) StatementMetadataReport.Add(item);
        foreach (var item in reports.TransactionRegistry) TransactionRegistryReport.Add(item);
        foreach (var item in reports.TransferMap) TransferMapReport.Add(item);
        foreach (var item in reports.ContraDebug) ContraDebugReport.Add(item);
        foreach (var item in reports.ChainReport) ChainReport.Add(item);
        foreach (var item in reports.ChainAccountSummary) ChainAccountSummary.Add(item);
        foreach (var item in reports.DuplicateReport) DuplicateReport.Add(item);
        foreach (var item in reports.RegistryReport) RegistryReport.Add(item);
        foreach (var item in reports.IntegrityReport) IntegrityReport.Add(item);
        foreach (var item in reports.ImportLog) ImportLogReport.Add(item);
        foreach (var item in reports.BalanceAuthority) BalanceAuthorityReport.Add(item);
        foreach (var activity in _activityRepository.GetRecent(clientId, 100)
                     .Where(item => item.ImportType.Contains("Bank Statement", StringComparison.OrdinalIgnoreCase) &&
                                    !item.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase)))
        {
            ImportLogReport.Add(new ImportLogRow
            {
                Timestamp = AppTime.ToLocal(activity.ImportedUtc),
                FileName = activity.SourceSummary,
                RowsImported = activity.RecordCount,
                Status = activity.Status,
                Notes = activity.Details
            });
        }
        foreach (var item in reports.ImportPerformanceLog) ImportPerformanceLogReport.Add(item);
        foreach (var item in reports.CashEntryRegistry) CashEntryRegistryReport.Add(item);
    }

    private void AppendTransientImportIssuesToReport()
    {
        foreach (var issue in ImportIssues)
        {
            if (ImportLogReport.Any(item => item.FileName.Equals(issue.SourceFile, StringComparison.OrdinalIgnoreCase) && item.Notes.Contains(issue.Message, StringComparison.OrdinalIgnoreCase))) continue;
            ImportLogReport.Add(new ImportLogRow
            {
                Timestamp = DateTime.Now,
                FileName = issue.SourceFile,
                Status = "FAILED / SKIPPED",
                Notes = $"{issue.ErrorCodeText} | {issue.Stage} | {issue.FriendlyMessage} | {issue.TechnicalMessage}"
            });
        }
    }

    private void LoadTransactions(IReadOnlyList<BankTransactionRecord>? preparedTransactions = null)
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            Transactions.ReplaceRange([]);
            FilteredTransactions.ReplaceRange([]);
            return;
        }
        var accountNames = Accounts.Count > 0
            ? Accounts.ToDictionary(item => item.Id, item => item.AuthorityName)
            : new Dictionary<long, string>();
        var source = preparedTransactions
            ?? (_workspaceStateCache.TryGetModulesForPresentation(
                    client.Id, client.FinancialYear,
                    new[] { ClientWorkspaceModule.Banking }, out var cached)
                ? cached.BankTransactions
                : Array.Empty<BankTransactionRecord>());
        var rows = source
            .Where(item => IncludeArchivedTransfers || !item.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var item in rows)
            item.BankAccountName = accountNames.TryGetValue(item.BankAccountId, out var accountName) ? accountName : $"Account {item.BankAccountId}";
        Transactions.ReplaceRange(rows);
        ApplyBankStatementFilters();
        NotifyCounts();
    }

    private async Task ImportBankStatementsAsync()
    {
        if (!CanImportBanking)
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "import Bank statements", accountingMutation: true);
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Select Excel or CSV Bank Statements",
            Filter = "Bank Statements (*.xlsx;*.xlsm;*.xls;*.csv)|*.xlsx;*.xlsm;*.xls;*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        var passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Do not prompt merely because an .xlsx file is not a readable ZIP. Encrypted
        // OpenXML is an OLE container, and bank portals also mislabel text/HTML as .xls.
        // The importer now exhausts content detection, filename/local password candidates,
        // and Excel recovery first. Manual password entry is the final fallback only.
        var selectedFiles = dialog.FileNames.ToList();
        if (selectedFiles.Count == 0) return;

        var clear = AskImportMode();
        if (!clear.HasValue) return;
        await RunBankImportBatchAsync(selectedFiles, clear.Value, passwords, "Excel / CSV Bank Statement", null);
    }

    private async Task ImportPdfStatementsAsync()
    {
        if (!CanImportBanking)
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "import Bank PDF statements", accountingMutation: true);
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Select PDF or Image Bank Statements",
            Filter =
                "Bank Statement PDF / Images (*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.webp)|*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.webp|" +
                "PDF Bank Statements (*.pdf)|*.pdf|" +
                "Image Bank Statements (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.webp|" +
                "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        var clear = AskImportMode();
        if (!clear.HasValue) return;

        ImportIssues.Clear();
        IsBusy = true;
        var aggregate = new BankStatementImportResult { FilesSelected = dialog.FileNames.Length };
        var processedSources = new List<string>();
        var clearPending = clear.Value;
        var ocrSources = new List<string>();
        var deferredPdfIssues = new List<BankStatementImportIssue>();
        var deferredGeneratedImportFailures = new List<(BankStatementImportResult Result, string SourceName)>();
        BankOcrBatchConversionResult? ocrBatch = null;

        try
        {
            // Digital PDFs always keep the existing external PDF pipeline as first authority.
            // Images go directly to the advanced OCR batch. A PDF is queued for OCR only when
            // normal extraction/import does not produce a usable Bank Statement workbook.
            for (var index = 0; index < dialog.FileNames.Length; index++)
            {
                var sourcePath = dialog.FileNames[index];
                var sourceName = Path.GetFileName(sourcePath);
                var isPdf = Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

                if (!isPdf)
                {
                    ocrSources.Add(sourcePath);
                    continue;
                }

                // Classify before import; OCR handles scanned, mixed and uncertain files.
                var pdfKind = BankPdfDocumentClassifier.Classify(sourcePath);
                if (pdfKind != BankPdfDocumentKind.Digital)
                {
                    StatusText = $"PDF {index + 1:N0} of {dialog.FileNames.Length:N0}: {pdfKind} PDF routed to advanced OCR…";
                    ocrSources.Add(sourcePath);
                    continue;
                }

                PdfPipelineFileResult? pipeline = null;
                try
                {
                    StatusText =
                        $"PDF {index + 1:N0} of {dialog.FileNames.Length:N0}: running normal digital PDF extraction first for {sourceName}…";
                    pipeline = await _pdfPipelineService.ProcessAsync(sourcePath);

                    if (!pipeline.WasSuccessful)
                    {
                        if (pipeline.Issue is not null) deferredPdfIssues.Add(pipeline.Issue);
                        ocrSources.Add(sourcePath);
                        continue;
                    }

                    StatusText =
                        $"PDF {index + 1:N0} of {dialog.FileNames.Length:N0}: importing normally extracted Excel workbook…";
                    var displayMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [pipeline.GeneratedWorkbookPath] = sourceName
                    };
                    var identityMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [pipeline.GeneratedWorkbookPath] = sourcePath
                    };
                    var imported = await ImportSingleWithRetryAsync(
                        pipeline.GeneratedWorkbookPath,
                        clearPending,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        displayMap,
                        identityMap,
                        true);

                    if (imported.FilesImported > 0)
                    {
                        clearPending = false;
                        processedSources.Add(sourceName);
                        MergeImportResult(aggregate, imported, sourceName);
                        continue;
                    }

                    // Do not report the generated-workbook failure yet. The same PDF now gets
                    // one chance through the proven PaddleOCR-VL production OCR document path.
                    deferredGeneratedImportFailures.Add((imported, sourceName));
                    ocrSources.Add(sourcePath);
                }
                catch (Exception exception)
                {
                    deferredPdfIssues.Add(BankStatementImportIssue.Create(
                        sourceName,
                        "PDF Extraction Before OCR",
                        BankImportErrorCode.E015_UnknownFailure,
                        exception.Message,
                        retryable: true));
                    ocrSources.Add(sourcePath);
                }
                finally
                {
                    if (pipeline is not null)
                    {
                        var cleanupIssue = await _pdfPipelineService.CleanupAsync(pipeline);
                        if (cleanupIssue is not null) aggregate.Issues.Add(cleanupIssue);
                    }
                }
            }

            if (ocrSources.Count > 0)
            {
                StatusText = ocrSources.Count == 1
                    ? $"Advanced OCR: processing {Path.GetFileName(ocrSources[0])} with PaddleOCR-VL 1.6 production OCR…"
                    : $"Advanced OCR: processing {ocrSources.Count:N0} scanned files together so pages can be grouped and ordered before import…";

                ocrBatch = await _bankOcrService.ProcessBatchAsync(
                    ocrSources,
                    message => System.Windows.Application.Current.Dispatcher.Invoke(() => StatusText = message));

                if (!ocrBatch.WasSuccessful)
                {
                    // Normal PDF failures become relevant only when OCR also cannot recover them.
                    foreach (var issue in deferredPdfIssues) aggregate.Issues.Add(issue);
                    foreach (var failure in deferredGeneratedImportFailures)
                        MergeImportResult(aggregate, failure.Result, failure.SourceName);
                    if (ocrBatch.Issue is not null) aggregate.Issues.Add(ocrBatch.Issue);
                }
                else
                {
                    foreach (var document in ocrBatch.Documents.OrderBy(item => item.DocumentNumber))
                    {
                        var sourceDisplay = document.SourcePaths.Count == 1
                            ? Path.GetFileName(document.PrimarySourcePath)
                            : $"OCR Statement {document.DocumentNumber:N0} ({document.SourcePaths.Count:N0} selected source files)";

                        if (!document.VerificationStatus.Equals("VERIFIED", StringComparison.OrdinalIgnoreCase) &&
                            !document.VerificationStatus.Equals("VERIFIED_WITH_CORRECTIONS", StringComparison.OrdinalIgnoreCase))
                        {
                            aggregate.Issues.Add(BankStatementImportIssue.Create(
                                sourceDisplay,
                                "Advanced OCR Verification",
                                BankImportErrorCode.O005_OcrTableDetectionFailure,
                                $"PaddleOCR-VL completed, but AAH marked this statement '{document.VerificationStatus}'. Page status: {document.PageStatus}; summary status: {document.SummaryStatus}. Review the AAH verification audit before importing.",
                                retryable: true));
                            continue;
                        }

                        StatusText =
                            $"Advanced OCR verified statement {document.DocumentNumber:N0}: {document.TransactionRowsPrepared:N0} transaction row(s), {document.CorrectionsApplied:N0} proven correction(s). Importing through the normal Bank Statement engine…";

                        var displayMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [document.GeneratedWorkbookPath] = sourceDisplay
                        };
                        var identityMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [document.GeneratedWorkbookPath] = document.PrimarySourcePath
                        };

                        var imported = await ImportSingleWithRetryAsync(
                            document.GeneratedWorkbookPath,
                            clearPending,
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                            displayMap,
                            identityMap,
                            true);

                        if (imported.FilesImported > 0)
                        {
                            clearPending = false;
                            foreach (var source in document.SourcePaths)
                            {
                                var name = Path.GetFileName(source);
                                if (!processedSources.Contains(name, StringComparer.OrdinalIgnoreCase))
                                    processedSources.Add(name);
                            }
                            MergeImportResult(aggregate, imported, sourceDisplay);
                        }
                        else
                        {
                            MergeImportResult(aggregate, imported, sourceDisplay);
                            aggregate.Issues.Add(BankStatementImportIssue.Create(
                                sourceDisplay,
                                "Advanced OCR Verified Workbook Import",
                                BankImportErrorCode.P007_GeneratedWorkbookImportFailure,
                                "PaddleOCR-VL production OCR verified and normalized the statement, but the existing Bank Statement importer still rejected the generated workbook. The normal importer remains the accounting authority; review the listed metadata/header/reconciliation issue.",
                                retryable: true));
                        }
                    }

                    if (ocrBatch.SourceErrorCount > 0)
                    {
                        aggregate.Issues.Add(BankStatementImportIssue.Create(
                            "Advanced OCR batch",
                            "OCR Source Processing",
                            BankImportErrorCode.O002_OcrSourceUnreadable,
                            $"{ocrBatch.SourceErrorCount:N0} selected OCR source file(s) could not be read. Other verified statement documents were still processed. OCR log: {ocrBatch.LogPath}",
                            retryable: true));
                    }

                    foreach (var warning in ocrBatch.ConversionWarnings.Take(4))
                    {
                        aggregate.Issues.Add(BankStatementImportIssue.Create(
                            "Advanced OCR batch",
                            "Verified Workbook Normalization",
                            BankImportErrorCode.O006_OcrWorkbookFailure,
                            warning,
                            retryable: true));
                    }
                }
            }

            if (ocrBatch is not null)
            {
                var cleanupIssues = await _bankOcrService.CleanupAsync(ocrBatch);
                foreach (var issue in cleanupIssues) aggregate.Issues.Add(issue);
                ocrBatch = null;
            }

            CompleteBankImportBatch(
                client,
                aggregate,
                "PDF / Image Bank Statement",
                processedSources.Count > 0
                    ? processedSources
                    : dialog.FileNames.Select(path => Path.GetFileName(path) ?? path),
                dialog.FileNames);
        }
        catch (Exception exception)
        {
            aggregate.Issues.Add(BankStatementImportIssue.Create(
                "PDF / Image batch",
                "Advanced Bank OCR Import",
                BankImportErrorCode.E015_UnknownFailure,
                exception.Message,
                retryable: false));
            CompleteBankImportBatch(
                client,
                aggregate,
                "PDF / Image Bank Statement",
                dialog.FileNames.Select(path => Path.GetFileName(path) ?? path),
                dialog.FileNames);
        }
        finally
        {
            if (ocrBatch is not null)
            {
                var cleanupIssues = await _bankOcrService.CleanupAsync(ocrBatch);
                foreach (var issue in cleanupIssues) aggregate.Issues.Add(issue);
            }
            IsBusy = false;
        }
    }

    private bool? AskImportMode()
    {
        if (Transactions.Count == 0) return false;
        var choice = MessageBox.Show(
            "Yes = Clear existing Bank Statement records\nNo = Append and import only new records\nCancel = Stop",
            "Bank Statement Import Mode",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return null;
        return choice == MessageBoxResult.Yes;
    }

    private async Task RunBankImportBatchAsync(
        IReadOnlyList<string> selectedFiles,
        bool clear,
        Dictionary<string, string> passwords,
        string importType,
        IReadOnlyDictionary<string, string>? sourceDisplayNames)
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        ImportIssues.Clear();
        IsBusy = true;
        try
        {
            var result = new BankStatementImportResult { FilesSelected = selectedFiles.Count };
            var troubleQueue = new List<string>();
            var clearPending = clear;

            // Excel-compatible first pass: clean files are completed immediately;
            // retryable failures are collected and processed only after the first pass.
            for (var index = 0; index < selectedFiles.Count; index++)
            {
                var file = selectedFiles[index];
                StatusText = $"Importing {index + 1:N0} of {selectedFiles.Count:N0}: {Path.GetFileName(file)}…";
                var current = await _importService.ImportAsync(new BankStatementImportRequest
                {
                    ClientId = client.Id,
                    FinancialYear = client.FinancialYear,
                    FilePaths = [file],
                    ClearExisting = clearPending,
                    Passwords = passwords,
                    Attempt = 1,
                    RecoveryMode = false,
                    StrictMetadata = true,
                    SourceDisplayNames = sourceDisplayNames ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    SourceIdentityPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    PreImportReconciliationAsync = _tallyBankStatementReconciliationService is null || !_isTallyConnected() ? null : ct => _tallyBankStatementReconciliationService.ReconcileBeforeImportAsync(client, ct),
                    PostImportCheckpointAsync = _tallyBankStatementReconciliationService is null || !_isTallyConnected() ? null : ct => _tallyBankStatementReconciliationService.CheckpointAfterBankImportAsync(client, ct)
                });
                clearPending = false;
                MergeImportResult(result, current, ResolveDisplayName(file, sourceDisplayNames));
                if (current.FilesImported == 0 && current.Issues.Any(issue => issue.IsRetryable)) troubleQueue.Add(file);
            }

            foreach (var file in troubleQueue)
            {
                result.FilesRetried++;
                var displayName = ResolveDisplayName(file, sourceDisplayNames);
                // Manual password entry is deliberately NOT requested from the first failure.
                // The recovery pass must first retry the same source with all automatic
                // filename/small-file candidates and Excel repair/extract-data modes.
                StatusText = $"Trouble queue: recovering {displayName}…";
                var retry = await _importService.ImportAsync(new BankStatementImportRequest
                {
                    ClientId = client.Id,
                    FinancialYear = client.FinancialYear,
                    FilePaths = [file],
                    ClearExisting = false,
                    Passwords = passwords,
                    Attempt = 2,
                    RecoveryMode = true,
                    StrictMetadata = true,
                    SourceDisplayNames = sourceDisplayNames ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    SourceIdentityPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    PreImportReconciliationAsync = _tallyBankStatementReconciliationService is null || !_isTallyConnected() ? null : ct => _tallyBankStatementReconciliationService.ReconcileBeforeImportAsync(client, ct),
                    PostImportCheckpointAsync = _tallyBankStatementReconciliationService is null || !_isTallyConnected() ? null : ct => _tallyBankStatementReconciliationService.CheckpointAfterBankImportAsync(client, ct)
                });
                MergeImportResult(result, retry, displayName);

                // Some legacy Excel builds report the first password failure as a generic
                // open/recovery error. If recovery is the first stage that can identify it as
                // password-protected, manual entry still remains the last fallback rather than
                // leaving the file stranded after the trouble queue.
                if (retry.FilesImported == 0 &&
                    retry.Issues.Any(issue => issue.ErrorCode == BankImportErrorCode.E002_PasswordFailure) &&
                    !passwords.ContainsKey(file))
                {
                    var password = PasswordPromptService.Prompt(displayName);
                    if (password is not null)
                    {
                        passwords[file] = password;
                        var finalRetry = await _importService.ImportAsync(new BankStatementImportRequest
                        {
                            ClientId = client.Id,
                            FinancialYear = client.FinancialYear,
                            FilePaths = [file],
                            ClearExisting = false,
                            Passwords = passwords,
                            Attempt = 3,
                            RecoveryMode = true,
                            StrictMetadata = true,
                            SourceDisplayNames = sourceDisplayNames ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                            SourceIdentityPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                            PreImportReconciliationAsync = _tallyBankStatementReconciliationService is null || !_isTallyConnected() ? null : ct => _tallyBankStatementReconciliationService.ReconcileBeforeImportAsync(client, ct),
                            PostImportCheckpointAsync = _tallyBankStatementReconciliationService is null || !_isTallyConnected() ? null : ct => _tallyBankStatementReconciliationService.CheckpointAfterBankImportAsync(client, ct)
                        });
                        MergeImportResult(result, finalRetry, displayName);
                    }
                }
            }

            CompleteBankImportBatch(
                client,
                result,
                importType,
                selectedFiles.Select(file => ResolveDisplayName(file, sourceDisplayNames)),
                selectedFiles);
            // Action Center must reflect the final Bank import result, including any reconciliation
            // issue added by CompleteBankImportBatch rather than only pre-reconciliation file errors.
            SyncManualBankImportActions(client, result, selectedFiles, sourceDisplayNames);
        }
        catch (Exception exception)
        {
            StatusText = $"Bank Statement import stopped: {exception.Message}";
            MessageBox.Show(StatusText, "Bank Statement", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<BankStatementImportResult> ImportSingleWithRetryAsync(
        string file,
        bool clear,
        Dictionary<string, string> passwords,
        IReadOnlyDictionary<string, string> sourceDisplayNames,
        IReadOnlyDictionary<string, string> sourceIdentityPaths,
        bool strictMetadata)
    {
        var client = _clientContext.CurrentClient ?? throw new InvalidOperationException("Open a client before importing.");
        var first = await _importService.ImportAsync(new BankStatementImportRequest
        {
            ClientId = client.Id,
            FinancialYear = client.FinancialYear,
            FilePaths = [file],
            ClearExisting = clear,
            Passwords = passwords,
            Attempt = 1,
            RecoveryMode = false,
            StrictMetadata = strictMetadata,
            SourceDisplayNames = sourceDisplayNames,
            SourceIdentityPaths = sourceIdentityPaths,
            PreImportReconciliationAsync = _tallyBankStatementReconciliationService is null || !_isTallyConnected() ? null : ct => _tallyBankStatementReconciliationService.ReconcileBeforeImportAsync(client, ct),
            PostImportCheckpointAsync = _tallyBankStatementReconciliationService is null || !_isTallyConnected() ? null : ct => _tallyBankStatementReconciliationService.CheckpointAfterBankImportAsync(client, ct)
        });
        if (first.FilesImported > 0 || !first.Issues.Any(issue => issue.IsRetryable)) return first;

        // Do not prompt from the first password/open failure. The recovery attempt below
        // first exhausts content recovery plus filename/small-file password candidates.
        // Manual entry remains the final fallback only if that recovery still reports E002.
        first.FilesRetried++;
        var retry = await _importService.ImportAsync(new BankStatementImportRequest
        {
            ClientId = client.Id,
            FinancialYear = client.FinancialYear,
            FilePaths = [file],
            ClearExisting = false,
            Passwords = passwords,
            Attempt = 2,
            RecoveryMode = true,
            StrictMetadata = strictMetadata,
            SourceDisplayNames = sourceDisplayNames,
            SourceIdentityPaths = sourceIdentityPaths,
            PreImportReconciliationAsync = _tallyBankStatementReconciliationService is null || !_isTallyConnected() ? null : ct => _tallyBankStatementReconciliationService.ReconcileBeforeImportAsync(client, ct),
            PostImportCheckpointAsync = _tallyBankStatementReconciliationService is null || !_isTallyConnected() ? null : ct => _tallyBankStatementReconciliationService.CheckpointAfterBankImportAsync(client, ct)
        });
        MergeImportResult(first, retry, ResolveDisplayName(file, sourceDisplayNames));

        if (retry.FilesImported == 0 &&
            retry.Issues.Any(issue => issue.ErrorCode == BankImportErrorCode.E002_PasswordFailure) &&
            !passwords.ContainsKey(file))
        {
            var password = PasswordPromptService.Prompt(Path.GetFileName(file));
            if (password is null) return first;
            passwords[file] = password;
            var finalRetry = await _importService.ImportAsync(new BankStatementImportRequest
            {
                ClientId = client.Id,
                FinancialYear = client.FinancialYear,
                FilePaths = [file],
                ClearExisting = false,
                Passwords = passwords,
                Attempt = 3,
                RecoveryMode = true,
                StrictMetadata = strictMetadata,
                SourceDisplayNames = sourceDisplayNames,
                SourceIdentityPaths = sourceIdentityPaths,
                PreImportReconciliationAsync = _tallyBankStatementReconciliationService is null || !_isTallyConnected() ? null : ct => _tallyBankStatementReconciliationService.ReconcileBeforeImportAsync(client, ct),
                PostImportCheckpointAsync = _tallyBankStatementReconciliationService is null || !_isTallyConnected() ? null : ct => _tallyBankStatementReconciliationService.CheckpointAfterBankImportAsync(client, ct)
            });
            MergeImportResult(first, finalRetry, ResolveDisplayName(file, sourceDisplayNames));
        }
        return first;
    }

    private void SyncManualBankImportActions(
        AccountingAutomationHub.Domain.Clients.ClientProfile client,
        BankStatementImportResult result,
        IReadOnlyList<string> sourceFiles,
        IReadOnlyDictionary<string, string>? sourceDisplayNames)
    {
        foreach (var sourcePath in sourceFiles)
        {
            var displayName = ResolveDisplayName(sourcePath, sourceDisplayNames);
            var sourceKey = BuildManualBankActionSourceKey(sourcePath);
            var fileIssues = result.Issues.Where(issue =>
                    issue.SourceFile.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
                    issue.SourceFile.Equals(Path.GetFileName(sourcePath), StringComparison.OrdinalIgnoreCase) ||
                    issue.SourceFile.Equals("Batch", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (fileIssues.Length == 0)
            {
                _actionRequiredRepository.ResolveBySourceKey(client.Id, client.FinancialYear, "Banking", sourceKey);
                continue;
            }

            var passwordFailure = fileIssues.Any(issue => issue.ErrorCode == BankImportErrorCode.E002_PasswordFailure);
            var corruptFailure = fileIssues.Any(issue => issue.ErrorCode is BankImportErrorCode.E001_WorkbookOpenFailure or BankImportErrorCode.E003_RecoveryFailure);
            var actionType = passwordFailure ? "Password Required" : corruptFailure ? "Bank Import Review" : "Bank Import Review";
            var detail = string.Join(Environment.NewLine, fileIssues.Select(issue =>
                $"{issue.ErrorCodeText} | {issue.Stage} | {issue.FriendlyMessage} | {FlattenLogText(issue.TechnicalMessage)}"));
            detail += Environment.NewLine + $"Source: {sourcePath}";

            _actionRequiredRepository.Upsert(new ActionRequiredItem
            {
                ClientId = client.Id,
                FinancialYear = client.FinancialYear,
                Module = "Banking",
                ActionType = actionType,
                Title = passwordFailure ? $"Password required - {displayName}" : $"Bank import needs review - {displayName}",
                Detail = detail,
                SourceKey = sourceKey
            });
        }
    }

    private static string BuildManualBankActionSourceKey(string sourcePath)
    {
        try
        {
            if (File.Exists(sourcePath))
            {
                using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                return $"MANUALBANK|{hash}";
            }
        }
        catch { }
        var normalized = Path.GetFullPath(sourcePath).ToUpperInvariant();
        return "MANUALBANK|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private void CompleteBankImportBatch(
        AccountingAutomationHub.Domain.Clients.ClientProfile client,
        BankStatementImportResult result,
        string importType,
        IEnumerable<string> sourceNames,
        IEnumerable<string>? sourcePaths = null)
    {
        try
        {
            _reconciliationService.Reconcile(client.Id, client.FinancialYear);
            // Completion 36: linked-Tally matching is now a PRE-IMPORT staging step.
            // Never replay the Tally workflow after commit. Always run the ordinary
            // imported-to-imported Inter-Bank pass for the remaining Active rows; it
            // preserves any completed Tally matches and is required even when Tally was
            // offline/unlinked during intake. A later Tally connection handles catch-up once.
            _transferService.Reconcile(client.Id, client.FinancialYear);
            _mappingService.Rebuild(client.Id, client.FinancialYear);
        }
        catch (Exception exception)
        {
            result.Issues.Add(BankStatementImportIssue.Create("Batch", "Reconciliation", BankImportErrorCode.E013_ReconciliationFailure, exception.Message, retryable: false));
        }

        result.FilesSkipped = Math.Max(0, result.FilesSelected - result.FilesImported);
        var details = result.Summary;
        if (result.Issues.Count > 0)
        {
            var issueDetails = string.Join(Environment.NewLine, result.Issues.Select(issue =>
                $"{issue.ErrorCodeText} | {issue.SourceFile} | {issue.Stage} | {issue.FriendlyMessage} | {FlattenLogText(issue.TechnicalMessage)}"));
            details += Environment.NewLine + Environment.NewLine + "Import errors:" + Environment.NewLine + issueDetails;
        }
        _activityRepository.Record(new ImportActivity
        {
            ClientId = client.Id,
            ImportType = importType,
            PeriodFrom = result.PeriodFrom,
            PeriodTo = result.PeriodTo,
            RecordCount = result.TransactionsImported,
            BankCount = _repository.GetAccounts(client.Id, client.FinancialYear).Count,
            SourceSummary = string.Join(", ", sourceNames),
            Status = result.Issues.Count == 0 ? "Imported" : "Review Required",
            Details = details,
            ImportedUtc = DateTime.UtcNow
        });
        _activityContext.NotifyChanged();
        foreach (var issue in result.Issues) ImportIssues.Add(issue);

        if (result.TransactionsImported > 0 && _automationService is not null)
        {
            var sourcePath = sourcePaths?.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? string.Empty;
            _automationService.PublishCommittedAccountingImport(
                client.Id,
                client.FinancialYear,
                "Banking",
                result.TransactionsImported,
                sourcePath,
                "Manual Import");
        }
        else
        {
            QueueWorkspaceCacheRefresh();
        }
        StatusText = details;
    }

    private void QueueBankBalanceEvidenceRepair(
        AccountingAutomationHub.Domain.Clients.ClientProfile client)
    {
        var key = $"{client.Id}|{client.FinancialYear.Trim().ToUpperInvariant()}";
        if (!_bankBalanceEvidenceRepairInFlight.TryAdd(key, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var repaired = await _importService
                    .RepairPersistedBalanceEvidenceAsync(client.Id, client.FinancialYear)
                    .ConfigureAwait(false);
                if (repaired <= 0) return;

                _reconciliationService.Reconcile(client.Id, client.FinancialYear);
                _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Banking);

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    var current = _clientContext.CurrentClient;
                    if (current?.Id != client.Id ||
                        !current.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
                        return;

                    var selectedAccountId = SelectedAccount?.Id ?? 0L;
                    var selectedStatementId = SelectedStatement?.Id ?? 0L;
                    Accounts.ReplaceRange(_repository.GetAccounts(client.Id, client.FinancialYear));
                    Statements.ReplaceRange(_repository.GetStatements(client.Id, client.FinancialYear));
                    var repairedTransactions = _repository.GetTransactions(client.Id, client.FinancialYear, includeArchived: true);
                    LoadTransactions(repairedTransactions);
                    ReconciliationRows.ReplaceRange(_repository.GetReconciliation(client.Id, client.FinancialYear));
                    ProjectResolvedBankBalancesToUi(repairedTransactions);
                    SelectedAccount = Accounts.FirstOrDefault(item => item.Id == selectedAccountId) ?? Accounts.FirstOrDefault();
                    SelectedStatement = Statements.FirstOrDefault(item => item.Id == selectedStatementId);
                    BuildObVerificationRows(client.ClientName);
                    RefreshBankBalanceSnapshot();
                    ChainStatusText = BuildChainStatus();

                    if (_reportsWorkspaceLoaded)
                    {
                        ClearReportCollections();
                        LoadWorkbookReports(client.Id, client.FinancialYear, client.ClientName);
                        AppendTransientImportIssuesToReport();
                    }

                    NotifyCounts();
                    StatusText = $"Bank balance evidence repaired from {repaired:N0} source statement(s); OB/CB and statement-period coverage refreshed.";
                    _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Banking, "Bank balance evidence repaired from source statements");
                }));
            }
            catch
            {
                // Source-evidence recovery is non-destructive and best-effort. A source that
                // cannot be reopened remains review-required instead of breaking Banking.
            }
            finally
            {
                _bankBalanceEvidenceRepairInFlight.TryRemove(key, out _);
            }
        });
    }

    private void QueueTallyBankStatementReconciliation(
        AccountingAutomationHub.Domain.Clients.ClientProfile client,
        bool force = false,
        string reason = "")
    {
        if (_tallyBankStatementReconciliationService is null) return;

        var key = $"{client.Id}|{client.FinancialYear.Trim().ToUpperInvariant()}";
        if (!force && _tallyBankReconciliationLastAttemptUtc.TryGetValue(key, out var lastAttempt) &&
            DateTime.UtcNow - lastAttempt < TimeSpan.FromMinutes(2))
            return;
        if (!_tallyBankReconciliationInFlight.TryAdd(key, 0)) return;

        TallyInterbankVerificationStatus = $"Checking saved Tally Bank changes{(string.IsNullOrWhiteSpace(reason) ? string.Empty : $" - {reason}")}...";
        _ = Task.Run(async () =>
        {
            try
            {
                var tallyResult = await _tallyBankStatementReconciliationService
                    .ReconcileLinkedClientAsync(client)
                    .ConfigureAwait(false);

                // Connection-time catch-up is authority work, not presentation work. Keep all
                // repository/rebuild cost off the WPF dispatcher; the prepared cache publishes
                // the committed result later at background priority. This prevents Tally connect
                // from freezing Ledger Mapping and then repainting Existing Match all at once.
                var verificationRows = _tallyBankStatementReconciliationService.GetVerificationRows(client).ToArray();
                _tallyBankReconciliationLastAttemptUtc[key] = DateTime.UtcNow;

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new System.Action(() =>
                {
                    var current = _clientContext.CurrentClient;
                    if (current?.Id == client.Id &&
                        current.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
                    {
                        TallyInterbankVerificationRows.ReplaceRange(verificationRows);
                        TallyInterbankVerificationStatus = verificationRows.Length > 0
                            ? $"{tallyResult.Status} Showing {verificationRows.Length:N0} converted/matched Tally Bank row(s)."
                            : $"{tallyResult.Status} No converted Tally Bank row is available for the current statement data.";

                        // Connection-time reconciliation is not a Ledger Mapping rebuild trigger.
                        // Refresh Banking only when reconciliation actually changed persisted Bank
                        // authority; the Banking snapshot then performs one changed-only coverage
                        // check. Master deltas independently update Existing Match in-place.
                        var bankAuthorityChanged = tallyResult.ConvertedBankTransactions > 0 ||
                                                   tallyResult.ImportedTransactionsRetired > 0 ||
                                                   tallyResult.Matched > 0 ||
                                                   tallyResult.SingleStatementLinked > 0 ||
                                                   tallyResult.VerificationConfirmed > 0 ||
                                                   tallyResult.AlteredAndVerified > 0;
                        if (bankAuthorityChanged)
                        {
                            _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Banking);
                            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Banking, "Tally Bank transaction conversion reconciliation");
                        }
                        StatusText = string.IsNullOrWhiteSpace(tallyResult.Status) ? StatusText : $"{StatusText} | {tallyResult.Status}";
                    }
                }));
            }
            catch (Exception exception)
            {
                _tallyBankReconciliationLastAttemptUtc[key] = DateTime.UtcNow;
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    if (_clientContext.CurrentClient?.Id == client.Id)
                    {
                        TallyInterbankVerificationStatus = $"Tally Bank transaction conversion/reconciliation could not complete: {exception.Message}";
                        StatusText = $"{StatusText} | Tally Bank conversion review: {exception.Message}";
                    }
                }));
            }
            finally
            {
                _tallyBankReconciliationInFlight.TryRemove(key, out _);
            }
        });
    }

    private static string ResolveDisplayName(string file, IReadOnlyDictionary<string, string>? sourceDisplayNames) =>
        sourceDisplayNames is not null && sourceDisplayNames.TryGetValue(file, out var display) && !string.IsNullOrWhiteSpace(display)
            ? display
            : Path.GetFileName(file);

    private static string FlattenLogText(string value) =>
        (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();

    private void SaveAccount()
    {
        if (SelectedAccount is null) return;
        try
        {
            var account = SelectedAccount;
            if (string.IsNullOrWhiteSpace(account.BankName))
                throw new InvalidOperationException("Bank Name is required.");

            // Manual-entry commit boundary: never rewrite while typing. Match Excel's
            // cleaned identity, but display bank/master names professionally.
            account.BankName = AccountingText.DisplayName(account.BankName);
            account.BranchName = AccountingText.DisplayName(account.BranchName);
            account.IfscCode = AccountingText.CleanWhitespace(account.IfscCode).ToUpperInvariant();
            account.AccountType = NormalizeManualBankAccountType(account.AccountType);
            account.AccountNumber = AccountingText.CleanWhitespace(account.AccountNumber);

            var oldAuthorityName = account.AuthorityName.Trim();
            var currentMasters = _masterRepository.Get(account.ClientId, account.FinancialYear, true, string.Empty);
            // Fix77: MASTERID identifies the existing Master, but changing the Ledger NAME
            // creates/selects an independent Master. A same-name Parent-only edit may stay
            // attached to the bound row; name lookup remains the unbound/new-Master path.
            var currentMaster = account.TallyLedgerMasterId.Trim().Length > 0
                ? currentMasters.FirstOrDefault(item =>
                    item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                    item.TallyMasterId.Trim().Equals(account.TallyLedgerMasterId.Trim(), StringComparison.OrdinalIgnoreCase))
                : null;
            currentMaster ??= currentMasters.FirstOrDefault(item =>
                item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                item.Name.Trim().Equals(oldAuthorityName, StringComparison.OrdinalIgnoreCase));

            var editorMaster = SelectedBankMasterRecord ?? currentMaster ?? CreateBankMasterRecord(account);
            var requestedLedgerName = editorMaster.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(requestedLedgerName))
                requestedLedgerName = string.IsNullOrWhiteSpace(account.LedgerName)
                    ? account.DisplayName
                    : account.LedgerName.Trim();
            if (string.IsNullOrWhiteSpace(requestedLedgerName))
                throw new InvalidOperationException("Master Ledger Name is required.");

            var existingTargets = currentMasters.Where(item =>
                item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                item.Name.Trim().Equals(requestedLedgerName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (existingTargets.Length > 1)
                throw new InvalidOperationException($"Multiple duplicate Ledger Masters resolve to '{requestedLedgerName}'. Resolve the duplicate Masters first.");
            var existingTarget = existingTargets.SingleOrDefault();

            MasterRecord targetMaster;
            // Compare the requested name to the Master actually bound by MASTERID, not to
            // the mutable BankAccount.LedgerName field. This stays correct even when the UI
            // has already changed the account display value before Save is pressed.
            var boundAuthorityName = currentMaster?.Name.Trim() ?? oldAuthorityName;
            var boundNameChanged = account.TallyLedgerMasterId.Trim().Length > 0 &&
                                   currentMaster is not null &&
                                   !requestedLedgerName.Trim().Equals(currentMaster.Name.Trim(), StringComparison.OrdinalIgnoreCase);

            if (existingTarget is not null && existingTarget.PersistentId != editorMaster.PersistentId)
            {
                if (!IsBankOrOverdraftMasterGroup(existingTarget.Parent))
                    throw new InvalidOperationException(
                        $"'{existingTarget.Name}' exists under '{existingTarget.Parent}'. Rename To can link only to an existing Bank/OD/Cash Credit ledger, or you can type a new bank ledger name.");

                // Explicitly selecting an existing Bank/OD Master links this Bank Account
                // to that independent Master. The old Master remains active and untouched.
                targetMaster = existingTarget;
            }
            else if (boundNameChanged && existingTarget is null)
            {
                // Fix77: a changed name on a Tally-bound Bank Ledger is a NEW Master,
                // not an ALTER/rename of the old MASTERID. Keep the old Tally Master active;
                // create a separate local Master with no Tally identity and let Master-first
                // export CREATE it. Only Tally's CREATE response may bind the new MASTERID.
                targetMaster = new MasterRecord
                {
                    ClientId = account.ClientId,
                    FinancialYear = account.FinancialYear,
                    Name = AccountingText.DisplayName(requestedLedgerName),
                    Parent = string.IsNullOrWhiteSpace(editorMaster.Parent) ? "Bank Accounts" : editorMaster.Parent.Trim(),
                    Type = "Ledger",
                    Source = "Bank Statement",
                    IsArchived = false,
                    IsUserEdited = true
                };
                _masterRepository.Upsert(account.ClientId, account.FinancialYear, [targetMaster], true);
                targetMaster = _masterRepository.Get(account.ClientId, account.FinancialYear, false, string.Empty)
                    .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                   item.TallyMasterId.Trim().Length == 0 &&
                                   item.Name.Trim().Equals(requestedLedgerName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.UpdatedUtc)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException($"The new independent Bank Ledger '{requestedLedgerName}' could not be saved locally.");
            }
            else
            {
                targetMaster = editorMaster;
                targetMaster.ClientId = account.ClientId;
                targetMaster.FinancialYear = account.FinancialYear;
                targetMaster.Name = AccountingText.DisplayName(requestedLedgerName);

                // A Parent-only edit on the same Ledger remains a same-MASTERID change.
                // A genuinely new independent Ledger receives the selected parent/default.
                if (_preserveBankMasterParentOnNextSave &&
                    currentMaster is not null &&
                    currentMaster.PersistentId == targetMaster.PersistentId)
                {
                    targetMaster.Parent = currentMaster.Parent;
                }
                else if (string.IsNullOrWhiteSpace(targetMaster.Parent))
                {
                    targetMaster.Parent = "Bank Accounts";
                }

                targetMaster.Type = "Ledger";
                targetMaster.Source = string.IsNullOrWhiteSpace(targetMaster.Source)
                    ? "Bank Statement"
                    : targetMaster.Source.Trim();
                targetMaster.IsArchived = false;
                targetMaster.IsUserEdited = true;

                if (targetMaster.PersistentId > 0)
                    _masterRepository.Save(targetMaster);
                else
                    _masterRepository.Upsert(account.ClientId, account.FinancialYear, [targetMaster], true);
            }

            account.LedgerName = targetMaster.Name.Trim();
            requestedLedgerName = account.LedgerName;
            if (!boundAuthorityName.Equals(requestedLedgerName, StringComparison.OrdinalIgnoreCase))
                RememberPreviousBankName(account, boundAuthorityName);

            if (targetMaster.TallyMasterId.Trim().Length > 0)
            {
                account.TallyLedgerMasterId = targetMaster.TallyMasterId.Trim();
                account.TallyLedgerGuid = targetMaster.TallyGuid.Trim();
                account.TallyLedgerAlterId = targetMaster.TallyAlterId;
                account.TallyLedgerVerifiedName = targetMaster.Name.Trim();
            }
            else if (currentMaster is null || targetMaster.PersistentId != currentMaster.PersistentId || boundNameChanged)
            {
                // New independent Master: the old MASTERID belongs only to the old Ledger.
                // Never carry it onto the new row. CREATE must return a new Tally MASTERID.
                account.TallyLedgerMasterId = string.Empty;
                account.TallyLedgerGuid = string.Empty;
                account.TallyLedgerAlterId = 0;
                account.TallyLedgerVerifiedName = string.Empty;
            }
            _repository.SaveAccount(account);
            // Rebuild immediately so any stale Bank Filter alias (for example OD -> CA)
            // migrates to the current Bank Account authority name without losing the
            // previously saved Ledger Mapping decision.
            _mappingService.Rebuild(account.ClientId, account.FinancialYear);
            _liveChangeHub.Publish(account.ClientId, account.FinancialYear, AccountingLiveArea.Masters, "Bank Account/Master changed");

            // Fix77: never auto-archive the old Bank Master when a different Ledger is
            // selected/created. Old and new Masters are independent accounting identities.

            QueueWorkspaceCacheRefresh();
            // Reload the account collection from SQLite immediately. BankAccountRecord
            // is a plain domain model (no INotifyPropertyChanged), so simply mutating
            // LedgerName left the already-rendered Bank Accounts list showing the old
            // value until the client was reopened. Replacing the rows makes the new
            // authority name visible at once and hydrates the persisted old-name tooltip.
            ReloadBankAccountsAfterRename(account.Id);
            IsBankAccountPopupOpen = true;
            StatusText = targetMaster.TallyMasterId.Trim().Length > 0
                ? "Bank account Master link saved. The selected existing Tally MASTERID remains independent."
                : "New independent Bank Ledger saved locally. Tally CREATE will assign its own MASTERID before transactions continue.";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            MessageBox.Show(exception.Message, "Bank Accounts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _preserveBankMasterParentOnNextSave = false;
        }
    }

    private static string NormalizeManualBankAccountType(string? value)
    {
        var clean = AccountingText.CleanWhitespace(value);
        if (clean.Length == 0) return string.Empty;
        return clean.ToUpperInvariant() switch
        {
            "SAVING" or "SAVINGS" or "SAVINGS ACCOUNT" or "SB A/C" or "SB ACCOUNT" => "SB",
            "CURRENT" or "CURRENT ACCOUNT" or "CA A/C" or "CA ACCOUNT" => "CA",
            "OVERDRAFT" or "OVERDRAFT ACCOUNT" or "OD A/C" or "OD ACCOUNT" => "OD",
            "CASH CREDIT" or "CC A/C" or "CC ACCOUNT" => "CC",
            _ when clean.Length <= 4 => clean.ToUpperInvariant(),
            _ => AccountingText.DisplayName(clean)
        };
    }

    private static bool IsBankOrOverdraftMasterGroup(string? groupName)
    {
        var group = (groupName ?? string.Empty).Trim().ToUpperInvariant();
        if (group.Length == 0) return false;
        return group.Contains("BANK", StringComparison.Ordinal) ||
               group.Contains("OVERDRAFT", StringComparison.Ordinal) ||
               group.Contains("OD A/C", StringComparison.Ordinal) ||
               group.Contains("OD ACCOUNT", StringComparison.Ordinal) ||
               group.Equals("OD", StringComparison.Ordinal) ||
               group.Contains("CASH CREDIT", StringComparison.Ordinal) ||
               group.Contains("OCC", StringComparison.Ordinal);
    }

    private void ApplyBankRename()
    {
        var row = SelectedBankRename;
        if (row is null || string.IsNullOrWhiteSpace(row.RenameTo)) return;
        var account = Accounts.FirstOrDefault(item => item.Id == row.BankAccountId);
        if (account is null) return;
        SelectedAccount = account;
        SelectedBankMasterRecord ??= CreateBankMasterRecord(account);
        SelectedBankMasterRecord.Name = row.RenameTo.Trim();
        _preserveBankMasterParentOnNextSave = true;
        SaveAccount();
    }

    private void VerifyStatement()
    {
        if (SelectedStatement is null) return;
        if (!SelectedStatement.OpeningBalance.HasValue || !SelectedStatement.ClosingBalance.HasValue)
        {
            StatusText = "Enter both Opening Balance and Closing Balance before manual verification.";
            return;
        }

        // Verify & Save is an explicit user verification action. Persist both entered
        // boundary balances as MANUAL evidence before running the shared authority.
        var statementId = SelectedStatement.Id;
        // Manual verification must use the same unique imported Debit/Credit movement as the
        // shared Bank Balance Authority. Historical Statement totals can be incomplete when a
        // physical file was recovered/mis-linked, so never verify a manual CB against those
        // stale stored totals.
        var importedRows = _repository.GetTransactions(SelectedStatement.ClientId, SelectedStatement.FinancialYear, includeArchived: true)
            .Where(BankBalanceAuthority.IsSourceStatementTransaction)
            .ToArray();
        var movementRows = BankBalanceAuthority.GetStatementPeriodTransactions(SelectedStatement, importedRows);
        var importedDebit = movementRows.Sum(row => row.Debit);
        var importedCredit = movementRows.Sum(row => row.Credit);
        var calculatedClosing = BankBalanceAuthority.CalculateClosingBalance(
            SelectedStatement.OpeningBalance.Value,
            importedDebit,
            importedCredit);
        SelectedStatement.TotalDebit = importedDebit;
        SelectedStatement.TotalCredit = importedCredit;
        var difference = Math.Round(SelectedStatement.ClosingBalance.Value - calculatedClosing, 2, MidpointRounding.AwayFromZero);
        var manualStatus = difference == 0m ? "Reconciled" : "Difference";
        SelectedStatement.OpeningBalanceSource = "MANUAL";
        SelectedStatement.ClosingBalanceSource = "MANUAL";
        SelectedStatement.Difference = difference;
        SelectedStatement.ReconciliationStatus = manualStatus;
        _repository.SaveStatementBalances(
            statementId,
            SelectedStatement.OpeningBalance,
            SelectedStatement.ClosingBalance,
            manualStatus,
            difference,
            "MANUAL",
            "MANUAL");
        _reconciliationService.VerifyStatement(SelectedStatement);
        QueueWorkspaceCacheRefresh();
        var refreshed = Statements.FirstOrDefault(item => item.Id == statementId);
        StatusText = refreshed?.ReconciliationStatus == "Reconciled"
            ? "Opening and closing balances manually verified against the shared Bank Balance Authority."
            : "The entered balances do not reconcile. Correct them or mark the statement Skipped.";
    }

    private void UseSuggestedOpening()
    {
        if (SelectedStatement is null) return;
        var suggestion = ReconciliationRows.FirstOrDefault(item => item.StatementId == SelectedStatement.Id)?.SuggestedOpeningBalance;
        if (!suggestion.HasValue)
        {
            StatusText = "No previous closing balance is available as an opening-balance suggestion.";
            return;
        }
        SelectedStatement.OpeningBalance = suggestion.Value;
        SelectedStatement.OpeningBalanceSource = "CHAIN";
        _repository.SaveStatementBalances(
            SelectedStatement.Id,
            suggestion.Value,
            SelectedStatement.ClosingBalance,
            SelectedStatement.ReconciliationStatus,
            SelectedStatement.Difference,
            "CHAIN",
            SelectedStatement.ClosingBalanceSource);
        _reconciliationService.VerifyStatement(SelectedStatement);
        QueueWorkspaceCacheRefresh();
        StatusText = $"Suggested chain opening balance {suggestion.Value:N2} applied through the shared Bank Balance Authority.";
    }

    private void SkipStatement()
    {
        if (SelectedStatement is null) return;
        _reconciliationService.SkipStatement(SelectedStatement);
        QueueWorkspaceCacheRefresh();
        StatusText = "Selected Statement_Metadata verification marked Skipped.";
    }

    private void DeleteStatement()
    {
        if (!DemandGovernancePermission(GovernancePermissions.DeleteAccounting, "delete Bank statements")) return;
        if (SelectedStatement is null) return;
        if (MessageBox.Show("Delete the selected statement and all transactions imported from it?", "Statement_Metadata", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var deletedSourcePath = SelectedStatement.SourcePath;
        _repository.DeleteStatement(SelectedStatement.Id);
        _reconciliationService.Reconcile(CurrentClientId, CurrentFinancialYear);
        _automationService?.NotifyBankStatementDeleted(CurrentClientId, CurrentFinancialYear, deletedSourcePath);
        _transferService.Reconcile(CurrentClientId, CurrentFinancialYear);
        _mappingService.Rebuild(CurrentClientId, CurrentFinancialYear);
        _ledgerTotalService.Invalidate(CurrentClientId, CurrentFinancialYear);
        var deletedStatementId = SelectedStatement.Id;
        Statements.Remove(SelectedStatement);
        LoadTransactions(Transactions.Where(item => item.StatementId != deletedStatementId).ToArray());
        ReconciliationRows.ReplaceRange(ReconciliationRows.Where(item => item.StatementId != deletedStatementId).ToArray());
        SelectedStatement = Statements.FirstOrDefault();
        BuildObVerificationRows(_clientContext.CurrentClient?.ClientName ?? string.Empty);
        QueueWorkspaceCacheRefresh();
        _liveChangeHub.Publish(CurrentClientId, CurrentFinancialYear, AccountingLiveArea.Banking, "Bank Statement deleted");
        StatusText = "Selected statement deleted; Banking cache streams are refreshing. Re-pasting the source into its Automation Folder can import it again.";
    }

    private void OpenManualCashContra()
    {
        if (SelectedTransaction is null) return;
        OpenManualCashContra([SelectedTransaction], "Bank Statement");
    }

    public void OpenManualCashContra(IEnumerable<BankTransactionRecord> transactions, string source)
    {
        if (!DemandGovernancePermission(GovernancePermissions.EditAccounting, "create a manual Bank Cash Contra")) return;
        var rows = transactions
            .Where(item => item is not null && item.Id > 0 && !item.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .OrderBy(item => item.TransactionDate)
            .ThenBy(item => item.Id)
            .ToArray();
        if (rows.Length == 0)
        {
            ManualCashContraStatusText = "Select one or more active imported Bank entries.";
            return;
        }
        ManualCashContraPreviewRows.ReplaceRange(rows);
        ManualCashContraSourceText = string.IsNullOrWhiteSpace(source) ? "Banking" : source.Trim();
        var currentLedgers = rows.Select(item => item.LedgerName?.Trim() ?? string.Empty)
            .Where(value => CashInHandLedgerNames.Contains(value, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ManualCashContraLedger = currentLedgers.Length == 1 ? currentLedgers[0] : CashInHandLedgerNames.FirstOrDefault() ?? string.Empty;
        ManualCashContraStepText = "Step 2 of 2 · Review selected entries and choose Cash ledger";
        ManualCashContraStatusText = $"{rows.Length:N0} selected entr{(rows.Length == 1 ? "y" : "ies")} from {ManualCashContraSourceText}. Review below before Apply Contra.";
        IsManualCashContraMode = true;
        OnPropertyChanged(nameof(ManualCashContraSelectedCount));
        SaveManualCashContraCommand.RaiseCanExecuteChanged();
    }

    public void OpenManualCashContraFromLedgerMapping()
    {
        var ids = FilteredLedgerMappingRows.Where(row => row.IsSelected)
            .SelectMany(row => GetVisibleLedgerMappingTransactionIds(row))
            .Where(id => id > 0).Distinct().ToHashSet();
        var rows = Transactions.Where(item => ids.Contains(item.Id)).ToArray();
        OpenManualCashContra(rows, "Ledger Mapping");
    }

    private void CancelManualCashContra()
    {
        IsManualCashContraMode = false;
        ManualCashContraLedger = string.Empty;
        ManualCashContraPreviewRows.Clear();
        ManualCashContraSourceText = "Bank Statement";
        ManualCashContraStepText = "Step 1 of 2 · Select entries";
        ManualCashContraStatusText = "Select Bank entries, then choose Manual Cash Contra.";
        OnPropertyChanged(nameof(ManualCashContraSelectedCount));
    }

    private async Task SaveManualCashContraAsync()
    {
        var rows = ManualCashContraPreviewRows.Where(item => item.Id > 0).ToArray();
        if (rows.Length == 0 || !IsManualCashContraMode) return;
        if (!DemandGovernancePermission(GovernancePermissions.EditAccounting, "create a manual Bank Cash Contra")) return;
        var cashLedger = ManualCashContraLedger.Trim();
        if (cashLedger.Length == 0 || !CashInHandLedgerNames.Contains(cashLedger, StringComparer.OrdinalIgnoreCase))
        {
            ManualCashContraStatusText = "Choose an existing Ledger under Cash-in-hand.";
            return;
        }
        if (rows.Any(transaction => transaction.Debit <= 0 && transaction.Credit <= 0))
        {
            ManualCashContraStatusText = "One or more selected Bank entries have no Debit/Credit amount.";
            return;
        }

        IsBusy = true;
        try
        {
            foreach (var transaction in rows)
            {
                transaction.Mode = "Contra";
                transaction.LedgerName = cashLedger;
                transaction.MappingStatus = "Mapped";
                transaction.NormalizedNarration = BankNarrationParser.Normalize(transaction.Narration);
            }
            var clientId = rows[0].ClientId;
            var financialYear = rows[0].FinancialYear;
            await Task.Run(() =>
            {
                foreach (var transaction in rows) _repository.SaveTransaction(transaction);
                _reconciliationService.Reconcile(clientId, financialYear);
                _mappingService.Rebuild(clientId, financialYear);
                _ledgerTotalService.Invalidate(clientId, financialYear);
            });
            _workspaceStateCache.QueueModuleRefresh(clientId, financialYear, ClientWorkspaceModule.Banking);
            _workspaceStateCache.QueueModuleRefresh(clientId, financialYear, ClientWorkspaceModule.LedgerMapping);
            _liveChangeHub.Publish(clientId, financialYear, AccountingLiveArea.Banking, "Manual Cash Contra saved");
            RefreshBankBalanceSnapshot();
            ManualCashContraStatusText = $"{rows.Length:N0} Contra entr{(rows.Length == 1 ? "y" : "ies")} saved against {cashLedger}.";
            StatusText = $"Manual Cash Contra saved · {rows.Length:N0} entr{(rows.Length == 1 ? "y" : "ies")} · {cashLedger}.";
            IsManualCashContraMode = false;
            ManualCashContraPreviewRows.Clear();
            OnPropertyChanged(nameof(ManualCashContraSelectedCount));
        }
        catch (Exception exception)
        {
            ManualCashContraStatusText = exception.Message;
            MessageBox.Show(exception.Message, "Manual Cash Contra", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SaveTransaction() => SaveTransactionRecord(SelectedTransaction);

    private void Reconcile()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        _reconciliationService.Reconcile(client.Id, client.FinancialYear);
        QueueWorkspaceCacheRefresh();
        StatusText = "Statement_Metadata, Chain_Report and Integrity_Report refreshed.";
    }

    public void ManualLinkInterbank(IReadOnlyList<BankTransactionRecord> selectedRows)
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        if (selectedRows.Count != 2)
        {
            StatusText = "Manual Inter-Bank Link requires exactly two selected Bank Statement entries.";
            MessageBox.Show(StatusText, "Manual Inter-Bank Link", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var match = _transferService.ManualLink(client.Id, client.FinancialYear, selectedRows[0], selectedRows[1]);
            var refreshedTransactions = _repository.GetTransactions(client.Id, client.FinancialYear, includeArchived: true);
            LoadTransactions(refreshedTransactions);
            TransferMatches.ReplaceRange(CompletedTransferMatches(_repository.GetTransferMatches(client.Id, client.FinancialYear)));
            if (_reportsWorkspaceLoaded)
            {
                ClearReportCollections();
                LoadWorkbookReports(client.Id, client.FinancialYear, client.ClientName);
                AppendTransientImportIssuesToReport();
            }
            var mappings = _mappingService.Rebuild(client.Id, client.FinancialYear);
            LedgerMappings.ReplaceRange(mappings);
            _ledgerMappingsLoaded = true;
            if (_ledgerMappingWorkspaceLoaded)
                BuildLedgerMappingRows(_ledgerMappingBankFilter, _ledgerMappingShowMapped, _ledgerMappingVoucherFilter);
            QueueWorkspaceCacheRefresh();
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Banking, "Manual inter-bank link");
            StatusText = $"Manual inter-bank link created: {match.Amount:N2} on {match.MatchDate:dd.MM.yyyy}. Narration was intentionally ignored.";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            MessageBox.Show(exception.Message, "Manual Inter-Bank Link", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MatchTransfers()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var matches = _transferService.Reconcile(client.Id, client.FinancialYear);
        var completedMatches = CompletedTransferMatches(matches);
        var pendingTally = matches.Count(item => BankTransferWorkflowState.IsTallyMatch(item) && !BankTransferWorkflowState.IsCompleted(item));
        TransferMatches.ReplaceRange(completedMatches);
        if (_reportsWorkspaceLoaded)
        {
            ClearReportCollections();
            LoadWorkbookReports(client.Id, client.FinancialYear, client.ClientName);
            AppendTransientImportIssuesToReport();
        }
        QueueWorkspaceCacheRefresh();
        StatusText = $"{completedMatches.Count:N0} completed Inter-Bank transfer(s) are in Transfer Matches/Transfer_Map; {pendingTally:N0} Tally workflow candidate(s) remain staged in Tally Bank Conversion/Contra Debug until Contra and final balance verification complete.";
    }

    private async Task RebuildMappingsAsync()
    {
        if (!DemandGovernancePermission(GovernancePermissions.LedgerMappingEdit, "rebuild Ledger Mapping")) return;
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var bankFilter = LedgerMappingBankFilter;
        var showMapped = LedgerMappingShowMapped;
        var voucherFilter = LedgerMappingVoucherFilter;
        var accountNames = Accounts.ToDictionary(item => item.Id, item => item.AuthorityName);
        var preparedTransactions = Transactions.Where(item => !item.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase)).ToArray();
        // Ledger Mapping is authoritative for the complete active Bank Statement.
        // Never rebuild from FilteredTransactions: a Bank Statement header/date filter
        // must not delete unseen transactions from the persisted mapping workbench.
        // Date/Bank/Voucher/Pending filters are presentation scopes applied AFTER this
        // complete frozen membership has been rebuilt.
        var completeMappingTransactionIds = preparedTransactions
            .Where(item => !ExcelLedgerMappingParser.IsLedgerMappingCoverageExcludedMode(item.Mode))
            .Select(item => item.Id)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        _tallyExportSelectionContext.TryGetBankRanges(
            client.Id,
            client.FinancialYear,
            out var selectedRanges,
            out _);

        CloseLedgerMappingPreview();
        IsBusy = true;
        StatusText = "Rebuilding Ledger Mapping in the background… You can continue using the application.";
        try
        {
            // Everything expensive stays off the WPF dispatcher: parser rebuild,
            // transaction reload for date counts, and canonical date-count work.
            var result = await Task.Run(() =>
            {
                var rebuilt = _mappingService.Rebuild(client.Id, client.FinancialYear, completeMappingTransactionIds).ToList();
                var summaryCounts = rebuilt
                    .Select(item => new
                    {
                        Name = BankNarrationParser.Normalize(item.SummaryName ?? string.Empty),
                        Count = Math.Max(0, item.MatchCount)
                    })
                    .Where(item => item.Name.Length > 0)
                    .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Sum(item => item.Count), StringComparer.OrdinalIgnoreCase);

                var frozenIds = rebuilt.SelectMany(item => item.TransactionIds).Where(id => id > 0).ToHashSet();
                var transactions = selectedRanges.Count == 0
                    ? Array.Empty<BankTransactionRecord>()
                    : preparedTransactions
                        .Where(item => frozenIds.Count == 0 || frozenIds.Contains(item.Id))
                        .ToArray();
                var dateCounts = BuildTallyExportDateSummaryCountsCore(
                    summaryCounts,
                    accountNames,
                    selectedRanges,
                    transactions);
                return new LedgerMappingRebuildResult(rebuilt, dateCounts);
            });

            // Do not call the broad Banking Refresh here. It reloads reconciliation,
            // reports, cash workbench and parser-derived date counts on the UI thread.
            // Only the Ledger Mapping collections are replaced after the rebuild.
            LedgerMappings.Clear();
            foreach (var mapping in result.Mappings) LedgerMappings.Add(mapping);
            BuildLedgerMappingRows(bankFilter, showMapped, voucherFilter, result.TallyExportDateCounts);

            // Do not reload the Bank Statement transaction grid here. Its collection
            // change handler performs full-column auto-fit measurement, which can make
            // a large statement look frozen immediately after rebuild. The next normal
            // Banking refresh will pick up any retained mapped transaction statuses.
            NotifyCounts();
            QueueWorkspaceCacheRefresh();
            StatusText = $"{LedgerMappingRows.Count:N0} Ledger Mapping row(s) rebuilt. Filters retained.";
        }
        catch (Exception exception)
        {
            StatusText = $"Ledger Mapping rebuild failed: {exception.Message}";
            MessageBox.Show(StatusText, "Ledger Mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool HasVisibleLedgerMappingChange(LedgerMappingWorkbenchRow row)
    {
        var finalKey = TallyMasterIdentity.Key(row.FinalLedger);
        if (finalKey.Length == 0) return false;
        var ids = GetVisibleLedgerMappingTransactionIds(row, applyMappedState: false);
        return Transactions.Where(x => ids.Contains(x.Id)).Any(x => TallyMasterIdentity.Key(x.LedgerName) != finalKey);
    }

    private async Task ApplyReadyMappingsAsync()
    {
        if (!DemandGovernancePermission(GovernancePermissions.LedgerMappingEdit, "apply ready Ledger Mappings")) return;
        if (CurrentClientId <= 0 || IsBusy) return;

        try
        {
            // Main Map All acts on every visible ready row, but an already-mapped row is
            // resubmitted only when its staged Final Ledger actually differs from the visible
            // transaction authority. This avoids rewriting unchanged Mapped rows in All/Mapped views.
            var actionableRows = FilteredLedgerMappingRows
                .Where(row => !IsLedgerMappingWorkbenchRowMapped(row) || HasVisibleLedgerMappingChange(row))
                .ToArray();
            foreach (var row in actionableRows) StageLedgerMappingRow(row);
            var readyRows = actionableRows
                .Where(row => row.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase) ||
                              row.Status.Equals("Ready - New Ledger", StringComparison.OrdinalIgnoreCase))
                .Where(row => !string.IsNullOrWhiteSpace(row.FinalLedger) && !string.IsNullOrWhiteSpace(row.LedgerGroup))
                .ToList();
            if (readyRows.Count == 0)
                throw new InvalidOperationException("No Ready Ledger Mapping rows are available. Enter Final Ledger and Ledger Group first.");

            // Exact visible Bank/Voucher/Method/Date membership is always frozen first.
            // If that membership is the complete mapping membership, persist the confirmed
            // broad rule/learning. A genuinely partial filtered scope stays transaction-only.
            var completeRuleMappings = new List<BankLedgerMapping>();
            var exactOnlyMappings = new List<BankLedgerMapping>();
            var mappedIdsByRow = new Dictionary<LedgerMappingWorkbenchRow, HashSet<long>>();
            foreach (var row in readyRows)
            {
                var fragments = BuildVisibleLedgerMappingFragments(row);
                if (fragments.Count == 0) continue;
                var ids = new HashSet<long>();
                foreach (var mapping in fragments)
                {
                    mapping.LedgerName = row.FinalLedger;
                    mapping.MasterGroup = row.LedgerGroup;
                    ids.UnionWith(mapping.TransactionIds.Where(id => id > 0));
                }
                var allMemberIds = (row.MemberMappings.Count > 0 ? row.MemberMappings : new[] { row.Mapping })
                    .SelectMany(mapping => mapping.TransactionIds).Where(id => id > 0).Distinct().ToHashSet();
                var completeVisibleMembership = allMemberIds.Count > 0 && allMemberIds.SetEquals(ids);
                foreach (var mapping in fragments)
                {
                    if (completeVisibleMembership) completeRuleMappings.Add(mapping);
                    else exactOnlyMappings.Add(mapping);
                }
                mappedIdsByRow[row] = ids;
            }
            if (completeRuleMappings.Count == 0 && exactOnlyMappings.Count == 0)
                throw new InvalidOperationException("No exact Bank Statement transaction membership is available for the current Map All scope. Rebuild Ledger Mapping first.");

            IsBusy = true;
            var affected = 0;
            if (completeRuleMappings.Count > 0)
                affected += await Task.Run(() => _mappingService.ApplyMany(completeRuleMappings, CreateLedgerWhenApplying, exactTransactionScope: false));
            if (exactOnlyMappings.Count > 0)
                affected += await Task.Run(() => _mappingService.ApplyMany(exactOnlyMappings, CreateLedgerWhenApplying, exactTransactionScope: true));

            // Update in-memory transaction authority before rebuilding so Pending/Mapped filters
            // paint the committed state immediately. Rebuild then refreshes the summary/rule rows.
            foreach (var ready in readyRows)
            {
                if (!mappedIdsByRow.TryGetValue(ready, out var ids)) continue;
                foreach (var transaction in Transactions.Where(item => ids.Contains(item.Id)))
                {
                    transaction.LedgerName = ready.FinalLedger;
                    transaction.MappingStatus = "Mapped";
                }
            }

            var bankFilter = LedgerMappingBankFilter;
            var showMapped = LedgerMappingShowMapped;
            var voucherFilter = LedgerMappingVoucherFilter;
            var methodFilter = LedgerMappingMethodFilter;
            _ledgerMappingDraftCommitIds.Clear();
            foreach (var id in mappedIdsByRow.Values.SelectMany(ids => ids)) _ledgerMappingDraftCommitIds.Add(id);
            try
            {
                var rebuiltMappings = await Task.Run(() => _mappingService.Rebuild(CurrentClientId, CurrentFinancialYear));
                LedgerMappings.ReplaceRange(rebuiltMappings);
                BuildLedgerMappingRows(bankFilter, showMapped, voucherFilter, preservedMethodFilter: methodFilter);
            }
            finally
            {
                _ledgerMappingDraftCommitIds.Clear();
            }
            ApplyBankStatementFilters();
            QueueWorkspaceCacheRefresh();
            NotifyCounts();
            StatusText = $"Mapped {readyRows.Count:N0} Ready mapping row(s) to {affected:N0} Bank Statement transaction(s).";
            _liveChangeHub.Publish(CurrentClientId, CurrentFinancialYear, AccountingLiveArea.Banking, "Ledger Mapping Map All applied");
            if (CreateLedgerWhenApplying)
                _liveChangeHub.Publish(CurrentClientId, CurrentFinancialYear, AccountingLiveArea.Masters, "Ledger Mapping created/resolved Masters");
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            MessageBox.Show(exception.Message, "Ledger Mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyMapping() => ApplyLedgerMappingCore(requireExpandedSelection: false);

    private void ApplyLedgerMappingDetailSelection() => ApplyLedgerMappingCore(requireExpandedSelection: true);

    private void ApplyLedgerMappingCore(bool requireExpandedSelection)
    {
        if (!DemandGovernancePermission(GovernancePermissions.LedgerMappingEdit, "apply a Ledger Mapping")) return;
        var row = SelectedLedgerMappingRow;
        if (row is null) return;
        try
        {
            StageLedgerMappingRow(row);
            if (string.IsNullOrWhiteSpace(row.FinalLedger))
                throw new InvalidOperationException("Enter or select the Final Ledger.");
            if (row.Status.Equals("Multiple Master Matches", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Resolve the duplicate/ambiguous Master Ledgers before applying this mapping.");
            if (row.Status.Equals("Invalid Ledger Group", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Select a valid existing Ledger Group before applying this mapping.");
            if (row.Status.Equals("Select Ledger Group", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Select the Ledger Group for the new Final Ledger.");

            // Apply Selected follows the visible expanded selection. An active search/filter
            // must never carry hidden rows into the split merely because they were selected
            // before the filter was typed. The filter path also clears hidden selection, but
            // using the visible collection here makes the apply contract explicit and safe.
            var selectedDetails = (requireExpandedSelection || IsLedgerMappingDetailOpen)
                ? FilteredLedgerMappingDetails.Where(detail => detail.IsSelected).ToArray()
                : LedgerMappingDetails.Where(detail => detail.IsSelected).ToArray();
            if ((requireExpandedSelection || IsLedgerMappingDetailOpen) && selectedDetails.Length == 0)
                throw new InvalidOperationException("Select at least one visible expanded Ledger Mapping transaction before applying the Final Ledger.");

            var members = row.MemberMappings.Count > 0 ? row.MemberMappings : new[] { row.Mapping };
            var fullMembershipIds = members.SelectMany(item => item.TransactionIds).Where(id => id > 0).ToHashSet();
            var selectedTransactionIds = (requireExpandedSelection || IsLedgerMappingDetailOpen)
                ? selectedDetails.Select(detail => detail.Transaction.Id).Where(id => id > 0).ToHashSet()
                : fullMembershipIds;
            if ((requireExpandedSelection || IsLedgerMappingDetailOpen) && selectedTransactionIds.Count == 0)
                throw new InvalidOperationException("The selected expanded rows do not contain valid Bank transaction IDs.");

            var exactSelectedScope = (requireExpandedSelection || IsLedgerMappingDetailOpen) && selectedTransactionIds.Count > 0 &&
                                     (fullMembershipIds.Count == 0 || !selectedTransactionIds.SetEquals(fullMembershipIds));

            int count;
            if (exactSelectedScope)
            {
                // Completion 64: partial expanded selection is a first-class split. Only
                // checked transaction IDs are written, and the resulting mapped group is
                // persisted with Summary Name = Final Ledger. Unchecked rows remain in the
                // original parser Summary Name group.
                var scopedMapping = new BankLedgerMapping
                {
                    ClientId = row.Mapping.ClientId,
                    FinancialYear = row.Mapping.FinancialYear,
                    BankFilter = row.Mapping.BankFilter,
                    VoucherType = selectedDetails.Select(detail => detail.Mode).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                        ? selectedDetails[0].Mode
                        : "Mixed",
                    SummaryName = row.FinalLedger,
                    NarrationPattern = row.Mapping.NarrationPattern,
                    SourceLedgerName = row.FinalLedger,
                    LedgerName = row.FinalLedger,
                    MasterGroup = row.LedgerGroup,
                    MatchCount = selectedTransactionIds.Count,
                    PaymentMatchCount = selectedDetails.Count(detail => detail.Mode.Equals("Payment", StringComparison.OrdinalIgnoreCase)),
                    ReceiptMatchCount = selectedDetails.Count(detail => detail.Mode.Equals("Receipt", StringComparison.OrdinalIgnoreCase)),
                    Status = "Mapped",
                    TransactionIds = selectedTransactionIds.OrderBy(id => id).ToList(),
                    UpdatedUtc = DateTime.UtcNow
                };

                count = _mappingService.ApplyMany([scopedMapping], CreateLedgerWhenApplying, exactTransactionScope: true);
                if (count <= 0)
                    throw new InvalidOperationException("No selected Bank transactions were updated. The original Summary Name was left unchanged.");

                // Rebuild from the transaction-ledger split, then stamp only the rebuilt
                // mapping members that contain the exact selected IDs. This preserves the
                // original parser Summary Name for unchecked rows while the new mapped
                // group visibly carries the Final Ledger name.
                var rebuilt = _mappingService.Rebuild(CurrentClientId, CurrentFinancialYear).ToList();
                var finalLedgerKey = BankNarrationParser.NormalizeLedgerExactMatchKey(row.FinalLedger);
                foreach (var split in rebuilt.Where(mapping =>
                             BankNarrationParser.NormalizeLedgerExactMatchKey(mapping.SourceLedgerName) == finalLedgerKey &&
                             mapping.TransactionIds.Any(selectedTransactionIds.Contains)))
                {
                    split.SummaryName = row.FinalLedger;
                    split.LedgerName = row.FinalLedger;
                    split.MasterGroup = row.LedgerGroup;
                    split.Status = "Mapped";
                    split.UpdatedUtc = DateTime.UtcNow;
                }
                _repository.ReplaceLedgerMappings(CurrentClientId, CurrentFinancialYear, rebuilt);

                LedgerMappings.Clear();
                foreach (var mapping in rebuilt) LedgerMappings.Add(mapping);
            }
            else
            {
                count = 0;
                foreach (var mapping in members)
                {
                    mapping.LedgerName = row.FinalLedger;
                    mapping.MasterGroup = row.LedgerGroup;
                    count += _mappingService.Apply(mapping, CreateLedgerWhenApplying);
                }

                LedgerMappings.Clear();
                foreach (var mapping in _repository.GetLedgerMappings(CurrentClientId, CurrentFinancialYear))
                    LedgerMappings.Add(mapping);
            }

            var mappedIds = exactSelectedScope ? selectedTransactionIds : fullMembershipIds;
            foreach (var transaction in Transactions.Where(item => mappedIds.Contains(item.Id)))
            {
                transaction.LedgerName = row.FinalLedger;
                transaction.MappingStatus = "Mapped";
            }

            var bankFilter = LedgerMappingBankFilter;
            var showMapped = LedgerMappingShowMapped;
            var voucherFilter = LedgerMappingVoucherFilter;
            BuildLedgerMappingRows(bankFilter, showMapped, voucherFilter);
            ApplyBankStatementFilters();
            QueueWorkspaceCacheRefresh();
            StatusText = exactSelectedScope
                ? $"Created '{row.FinalLedger}' as a separate Ledger Mapping group for {count:N0} selected transaction(s). Unchecked rows remain in the original Summary Name."
                : $"Ledger Mapping applied to {count:N0} Bank Statement row(s).";
            _liveChangeHub.Publish(CurrentClientId, CurrentFinancialYear, AccountingLiveArea.Banking, exactSelectedScope
                ? "Ledger Mapping selected expanded transactions split into Final Ledger group"
                : "Ledger Mapping applied");
            if (CreateLedgerWhenApplying)
                _liveChangeHub.Publish(CurrentClientId, CurrentFinancialYear, AccountingLiveArea.Masters, "Ledger Mapping created/resolved Master");

            if (exactSelectedScope || requireExpandedSelection)
                CloseLedgerMappingPreview();
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            MessageBox.Show(exception.Message, "Ledger Mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddCashEntry()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var today = DateTime.Today;
        var entryDate = IsDateInsideFinancialYear(today, client.FinancialYear)
            ? today
            : FinancialYearStart(client.FinancialYear);

        CashVoucherDraft = new CashEntryRecord
        {
            ClientId = client.Id,
            FinancialYear = client.FinancialYear,
            EntryDate = entryDate,
            CommonLedger = CashSettings.DefaultCommonLedger,
            WorkflowStatus = "Draft",
            Status = "Draft"
        };
        IsCashVoucherEditorOpen = true;
        SelectedCashEntry = null;
        ApplySelectedCashLedgerTotal(null, "Choose the Ledger in Add Voucher to view its closing balance and amount suggestion.");
        CashStatusText = "Add Voucher ready. Nothing is saved until Save Voucher is pressed.";
    }

    private void SaveCashVoucher()
    {
        var draft = CashVoucherDraft;
        if (draft is null) return;
        if (!DemandGovernancePermission(GovernancePermissions.EditAccounting, "add Cash vouchers")) return;

        try
        {
            // Capture the exact Add Voucher values before validation. The DatePicker
            // and LedgerPicker commit into the draft before this command executes; the
            // accounting service may normalize spelling/amounts but must never change
            // the chosen date or Ledger identity.
            var requestedDate = draft.EntryDate.Date;
            var requestedLedgerKey = TallyMasterIdentity.Key(draft.EntryLedger);
            _cashEntryService.ValidateAndSave(draft, CashSettings);
            if (!draft.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(draft.Status);
            if (draft.EntryDate.Date != requestedDate)
                throw new InvalidOperationException("Cash voucher date changed during save. The voucher was not accepted by the Add Voucher workflow.");
            if (!TallyMasterIdentity.Key(draft.EntryLedger).Equals(requestedLedgerKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cash voucher Ledger identity changed during save. Re-select the Ledger and save again.");

            var savedId = draft.Id;
            IsCashVoucherEditorOpen = false;
            CashVoucherDraft = null;
            ReloadCashEntries(savedId);
            CashStatusText = $"Cash voucher {draft.EntryId} saved as {draft.VoucherType}.";
            _liveChangeHub.Publish(draft.ClientId, draft.FinancialYear, AccountingLiveArea.Cash, "Cash voucher added");
        }
        catch (Exception exception)
        {
            CashStatusText = exception.Message;
            MessageBox.Show(exception.Message, "Add Cash Voucher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelCashVoucher()
    {
        CashVoucherDraft?.SetClosingBalanceSuggestion(null);
        CashVoucherDraft = null;
        IsCashVoucherEditorOpen = false;
        ApplySelectedCashLedgerTotal(null, "Select a saved Cash row to view its Ledger total.");
        CashStatusText = "Add Voucher cancelled. No Cash Entry was created.";
    }

    private void ExportSuspenseWorkbook()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var rows = Transactions
            .Where(row => !row.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase) && IsEmptyLedgerForExport(row))
            .OrderBy(row => row.TransactionDate)
            .ThenBy(row => row.Id)
            .ToArray();
        if (rows.Length == 0)
        {
            StatusText = "There are no active Suspense / empty-ledger Bank Statement rows to export.";
            MessageBox.Show(StatusText, "Export Suspense", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var folder = ResolveAccountingFormatFolder(client.Id, client.FinancialYear);
            Directory.CreateDirectory(folder);
            var clientName = SafeClientFormatFilePart(
                string.IsNullOrWhiteSpace(client.ClientName) ? client.ClientCode : client.ClientName);
            var path = Path.Combine(folder, $"{clientName}.xlsx");

            WriteSuspenseWorkbook(path, client, rows, Accounts);
            StatusText =
                $"Exported {rows.Length:N0} Suspense / empty-ledger Bank Statement row(s) automatically: {path}. " +
                "Fill only the yellow Ledger column and save; Auto Import recognizes this AAH Suspense workbook directly.";
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            StatusText = $"Suspense export failed: {exception.Message}";
            MessageBox.Show(exception.Message, "Export Suspense", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportSuspenseWorkbook()
    {
        if (!DemandGovernancePermission(GovernancePermissions.LedgerMappingEdit, "import Ledger Mapping changes from Suspense")) return;
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Import Suspense Ledger Mapping",
            Filter = "Excel Workbook (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var service = new BankSuspenseWorkbookService(_repository, _masterRepository, _mappingService);
            var result = service.Import(dialog.FileName, client.Id, client.FinancialYear);

            if (result.Updated > 0)
            {
                LedgerMappings.ReplaceRange(_repository.GetLedgerMappings(client.Id, client.FinancialYear));
                _ledgerMappingsLoaded = true;
                _ledgerTotalService.Invalidate(client.Id, client.FinancialYear);
                LoadTransactions(_repository.GetTransactions(client.Id, client.FinancialYear, includeArchived: IncludeArchivedTransfers));
                RefreshBankBalanceSnapshot();
                if (_automationService is not null)
                    _automationService.PublishCommittedAccountingImport(
                        client.Id, client.FinancialYear, "Banking", result.Updated, dialog.FileName, "Manual Import");
                else
                {
                    QueueWorkspaceCacheRefresh();
                }
                // FIXED31: a Suspense mapping workbook is not raw Bank Statement intake;
                // it is an explicit Final Ledger assignment. Publish that accounting change
                // even when the generic Manual Import bridge also ran so Automatic Tally Sync
                // can verify the saved Suspense Voucher version and ALTER the same MASTERID.
                _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Banking, "Final Ledger mapping imported");
                if (_ledgerMappingWorkspaceLoaded)
                    BuildLedgerMappingRows(_ledgerMappingBankFilter, _ledgerMappingShowMapped, _ledgerMappingVoucherFilter);
            }

            NotifyCounts();
            StatusText = result.Summary;
            if (result.HasReview)
            {
                var detail = result.Errors.Count > 0 ? $" First error: {result.Errors[0]}" : string.Empty;
                MessageBox.Show(StatusText + detail, "Import Suspense", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(StatusText, "Import Suspense", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            StatusText = $"Suspense import failed: {exception.Message}";
            MessageBox.Show(exception.Message, "Import Suspense", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string ReadWorkbookMeta(OpenXmlWorkbookData workbook, string sheetName, string key)
    {
        if (!workbook.TryGetWorksheet(sheetName, out var meta)) return string.Empty;
        for (var row = 1; row <= meta.MaxRow; row++)
            if (meta.GetCell(row, 1).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return meta.GetCell(row, 2).Trim();
        return string.Empty;
    }

    internal static void WriteSuspenseWorkbook(
        string path,
        AccountingAutomationHub.Domain.Clients.ClientProfile client,
        IReadOnlyList<BankTransactionRecord> rows,
        IEnumerable<BankAccountRecord> accounts)
    {
        var accountNames = accounts.ToDictionary(account => account.Id, account => account.AuthorityName);
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteCashXlsxEntry(archive, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
        WriteCashXlsxEntry(archive, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        WriteCashXlsxEntry(archive, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Suspense Mapping\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"__AAH_SUSPENSE_META\" sheetId=\"2\" r:id=\"rId2\" state=\"hidden\"/></sheets></workbook>");
        WriteCashXlsxEntry(archive, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        WriteCashXlsxEntry(archive, "xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"dd.mm.yyyy\"/><numFmt numFmtId=\"165\" formatCode=\"#,##0.00\"/></numFmts><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><color rgb=\"FFFFFFFF\"/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"4\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E78\"/><bgColor indexed=\"64\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFF2CC\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"2\"><border/><border><left style=\"thin\"><color rgb=\"FFD9E2F3\"/></left><right style=\"thin\"><color rgb=\"FFD9E2F3\"/></right><top style=\"thin\"><color rgb=\"FFD9E2F3\"/></top><bottom style=\"thin\"><color rgb=\"FFD9E2F3\"/></bottom><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs><cellXfs count=\"6\"><xf xfId=\"0\"/><xf xfId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf><xf xfId=\"0\" fillId=\"3\" borderId=\"1\" applyFill=\"1\" applyBorder=\"1\"/><xf xfId=\"0\" numFmtId=\"164\" borderId=\"1\" applyNumberFormat=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\"/></xf><xf xfId=\"0\" numFmtId=\"165\" borderId=\"1\" applyNumberFormat=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\"/></xf><xf xfId=\"0\" borderId=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment wrapText=\"1\" vertical=\"top\"/></xf></cellXfs></styleSheet>");

        // User-facing format follows the Bank Statement grid. Only Ledger is editable/imported.
        // Permanent database identities remain in hidden trailing columns for safe round-trip matching.
        var headers = new[] { "Date", "Narration", "Debit", "Credit", "Mode", "Ledger", "Bank", "Reference", "Remarks", "Transaction ID", "Transaction DB ID" };
        var lastRow = Math.Max(1, rows.Count + 1);
        var sb = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><dimension ref=\"A1:K{lastRow}\"/><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><sheetFormatPr defaultRowHeight=\"15\"/><cols><col min=\"1\" max=\"1\" width=\"13\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"60\" customWidth=\"1\"/><col min=\"3\" max=\"4\" width=\"15\" customWidth=\"1\"/><col min=\"5\" max=\"5\" width=\"12\" customWidth=\"1\"/><col min=\"6\" max=\"6\" width=\"34\" customWidth=\"1\"/><col min=\"7\" max=\"7\" width=\"34\" customWidth=\"1\"/><col min=\"8\" max=\"8\" width=\"24\" customWidth=\"1\"/><col min=\"9\" max=\"9\" width=\"30\" customWidth=\"1\"/><col min=\"10\" max=\"10\" width=\"2\" hidden=\"1\" customWidth=\"1\"/><col min=\"11\" max=\"11\" width=\"2\" hidden=\"1\" customWidth=\"1\"/></cols><sheetData>");
        sb.Append("<row r=\"1\" ht=\"24\" customHeight=\"1\">");
        for (var c = 0; c < headers.Length; c++) sb.Append(CashCell(c + 1, 1, headers[c], 1));
        sb.Append("</row>");
        for (var i = 0; i < rows.Count; i++)
        {
            var transaction = rows[i];
            var r = i + 2;
            var bank = accountNames.TryGetValue(transaction.BankAccountId, out var accountName) ? accountName : transaction.BankAccountName;
            sb.Append($"<row r=\"{r}\">");
            sb.Append(SuspenseNumberCell(1, r, transaction.TransactionDate.Date.ToOADate(), 3));
            sb.Append(CashCell(2, r, transaction.Narration, 5));
            sb.Append(transaction.Debit > 0m ? SuspenseDecimalCell(3, r, transaction.Debit, 4) : SuspenseBlankCell(3, r, 4));
            sb.Append(transaction.Credit > 0m ? SuspenseDecimalCell(4, r, transaction.Credit, 4) : SuspenseBlankCell(4, r, 4));
            sb.Append(CashCell(5, r, transaction.Mode, 0));
            sb.Append(CashCell(6, r, string.Empty, 2));
            sb.Append(CashCell(7, r, bank, 0));
            sb.Append(CashCell(8, r, transaction.ReferenceNumber, 0));
            sb.Append(CashCell(9, r, string.Empty, 0));
            sb.Append(CashCell(10, r, transaction.TransactionId, 0));
            sb.Append(CashCell(11, r, transaction.Id.ToString(CultureInfo.InvariantCulture), 0));
            sb.Append("</row>");
        }
        sb.Append($"</sheetData><autoFilter ref=\"A1:I{lastRow}\"/></worksheet>");
        WriteCashXlsxEntry(archive, "xl/worksheets/sheet1.xml", sb.ToString());

        var meta = new[]
        {
            ("Kind", "Accounting Automation Hub Bank Suspense Mapping"),
            ("SchemaVersion", "2"),
            ("ClientId", client.Id.ToString(CultureInfo.InvariantCulture)),
            ("ClientCode", client.ClientCode),
            ("ClientName", client.ClientName),
            ("FinancialYear", client.FinancialYear),
            ("ExportedRows", rows.Count.ToString(CultureInfo.InvariantCulture)),
            ("ExportedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            ("EditRule", "Fill only the yellow Ledger column. Date/Narration/Debit/Credit/Mode/Bank/Reference are read-only authority fields."),
            ("IdentityRule", "Transaction ID and Transaction DB ID are hidden authority columns and must not be edited.")
        };
        var metaSb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var i = 0; i < meta.Length; i++)
        {
            var r = i + 1;
            metaSb.Append($"<row r=\"{r}\">{CashCell(1, r, meta[i].Item1, 1)}{CashCell(2, r, meta[i].Item2, 0)}</row>");
        }
        metaSb.Append("</sheetData></worksheet>");
        WriteCashXlsxEntry(archive, "xl/worksheets/sheet2.xml", metaSb.ToString());
    }

    private static string SuspenseNumberCell(int column, int row, double value, int style)
    {
        var number = value.ToString("0.###############", CultureInfo.InvariantCulture);
        return $"<c r=\"{CashColumnName(column)}{row}\" s=\"{style}\" t=\"n\"><v>{number}</v></c>";
    }

    private static string SuspenseDecimalCell(int column, int row, decimal value, int style)
    {
        // Money stays decimal all the way to the XLSX text representation. Do not
        // introduce a binary-double round trip merely to write a visible 2-decimal value.
        var number = decimal.Round(value, 2, MidpointRounding.AwayFromZero)
            .ToString("0.00", CultureInfo.InvariantCulture);
        return $"<c r=\"{CashColumnName(column)}{row}\" s=\"{style}\" t=\"n\"><v>{number}</v></c>";
    }

    private static string SuspenseBlankCell(int column, int row, int style) =>
        $"<c r=\"{CashColumnName(column)}{row}\" s=\"{style}\"/>";

    private void ExportCashExcelFormat()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        try
        {
            var folder = ResolveCashWorkbookFolder(client.Id, client.FinancialYear);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(
                folder,
                $"AAH_CASH_ENTRY_{SafeFilePart(client.ClientCode)}_{SafeFilePart(client.FinancialYear)}_{DateTime.Now:yyyyMMdd-HHmmss}.xlsx");

            WriteCashEntryWorkbook(path, client, CashSettings);
            StartCashWorkbookCloseWatch(path, client.Id, client.FinancialYear);
            CashStatusText = $"Cash Excel format opened. Fill rows, save and close Excel; the Hub will import valid rows automatically: {path}";
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            CashStatusText = $"Cash Excel format could not be created: {exception.Message}";
            MessageBox.Show(exception.Message, "Cash Excel Format", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string ResolveAccountingFormatFolder(long clientId, string financialYear)
    {
        var linked = _automationService?.GetFolders()
            .Where(item => item.ClientId == clientId &&
                           item.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase) &&
                           Directory.Exists(item.FolderPath))
            .OrderBy(item => item.FolderPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var root = linked?.FolderPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Accounting Automation Hub",
            "Import Formats");
        return Path.Combine(root, "AAH Formats", "Suspense");
    }

    private static string SafeClientFormatFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = (value ?? string.Empty).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var result = new string(chars).Trim();
        return result.Length == 0 ? "Client" : result;
    }

    private string ResolveCashWorkbookFolder(long clientId, string financialYear)
    {
        var linked = _automationService?.GetFolders()
            .Where(item => item.ClientId == clientId &&
                           item.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase) &&
                           Directory.Exists(item.FolderPath))
            .OrderBy(item => item.FolderPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (linked is not null) return linked.FolderPath;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Accounting Automation Hub",
            "Cash Entry");
    }

    private void StartCashWorkbookCloseWatch(string path, long clientId, string financialYear)
    {
        _cashWorkbookWatchCts?.Cancel();
        _cashWorkbookWatchCts?.Dispose();
        _cashWorkbookWatchCts = new CancellationTokenSource();
        var token = _cashWorkbookWatchCts.Token;

        _ = Task.Run(async () =>
        {
            var lockPath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "~$" + Path.GetFileName(path));
            var sawExcelLock = false;
            var lastWrite = File.GetLastWriteTimeUtc(path);
            var stableSince = DateTime.UtcNow;

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(700, token).ConfigureAwait(false);
                if (!File.Exists(path)) return;

                var lockExists = File.Exists(lockPath);
                if (lockExists) sawExcelLock = true;

                var write = File.GetLastWriteTimeUtc(path);
                if (write != lastWrite)
                {
                    lastWrite = write;
                    stableSince = DateTime.UtcNow;
                }

                // Preferred close detection: Excel's owner file existed and is now gone.
                // Fallback supports other spreadsheet editors: file must be stable and
                // exclusively openable before import.
                var closed = sawExcelLock
                    ? !lockExists
                    : DateTime.UtcNow - stableSince >= TimeSpan.FromSeconds(3) && CanOpenExclusive(path);
                if (!closed) continue;

                await Task.Delay(500, token).ConfigureAwait(false);
                string result;
                try
                {
                    result = ImportCashEntryWorkbook(path, clientId, financialYear);
                }
                catch (Exception exception)
                {
                    result = $"Cash Excel closed, but automatic import was stopped: {exception.Message}";
                }
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is not null)
                {
                    _ = dispatcher.BeginInvoke(() =>
                    {
                        CashStatusText = result;
                        ReloadCashEntries();
                        QueueLedgerTotalsRefresh(invalidate: true);
                    });
                }
                return;
            }
        }, token);
    }

    private string ImportCashEntryWorkbook(string path, long clientId, string financialYear)
    {
        var reader = new FlexibleWorkbookReader();
        var workbook = reader.Read(path, CancellationToken.None);
        if (!workbook.TryGetWorksheet("Cash Entry Import", out var sheet))
            throw new InvalidOperationException("Cash Entry Import sheet is missing.");

        var clientIdText = ReadCashMeta(workbook, "ClientId");
        var fy = ReadCashMeta(workbook, "FinancialYear");
        if (!long.TryParse(clientIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var workbookClientId) ||
            workbookClientId != clientId ||
            !fy.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This Cash workbook belongs to a different Client/FY.");

        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (sheet.Rows.TryGetValue(1, out var headerRow))
        {
            foreach (var pair in headerRow)
            {
                var header = pair.Value.Trim();
                if (header.Length > 0 && !headers.ContainsKey(header))
                {
                    headers[header] = pair.Key;
                }
            }
        }

        string Cell(int row, string name) =>
            headers.TryGetValue(name, out var col) ? sheet.GetCell(row, col).Trim() : string.Empty;

        var existingFingerprints = _repository.GetCashEntries(clientId, financialYear)
            .Select(row => row.SourceRunId ?? string.Empty)
            .Where(value => value.StartsWith("CASHXLS|", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();
        for (var row = 2; row <= sheet.MaxRow; row++)
        {
            var dateText = Cell(row, "Date");
            var ledger = AccountingText.DisplayName(Cell(row, "Ledger Name"));
            var narration = Cell(row, "Narration");
            var debitText = Cell(row, "Debit");
            var creditText = Cell(row, "Credit");
            var amountText = Cell(row, "Amount");
            var direction = Cell(row, "Dr/Cr");
            var reference = Cell(row, "Reference");
            var common = AccountingText.DisplayName(Cell(row, "Common Ledger"));

            if (dateText.Length == 0 && ledger.Length == 0 && narration.Length == 0 &&
                debitText.Length == 0 && creditText.Length == 0 && amountText.Length == 0)
                continue;

            try
            {
                if (!TryCashDate(dateText, out var date))
                    throw new InvalidOperationException("valid Date required");
                var debit = ParseCashAmount(debitText);
                var credit = ParseCashAmount(creditText);
                var amount = ParseCashAmount(amountText);
                if (debit <= 0m && credit <= 0m && amount > 0m)
                {
                    var drcr = direction.Trim().ToUpperInvariant();
                    if (drcr is "DR" or "D" or "PAYMENT" or "P") debit = amount;
                    else if (drcr is "CR" or "C" or "RECEIPT" or "R") credit = amount;
                    else throw new InvalidOperationException("Dr/Cr must be Dr or Cr when Amount is used");
                }

                var fingerprintMaterial = $"{date:yyyyMMdd}|{TallyMasterIdentity.Key(ledger)}|{debit:0.00}|{credit:0.00}|{reference.Trim()}|{narration.Trim()}";
                var fingerprint = "CASHXLS|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintMaterial)));
                if (existingFingerprints.Contains(fingerprint)) { skipped++; continue; }

                var entry = new CashEntryRecord
                {
                    ClientId = clientId,
                    FinancialYear = financialYear,
                    EntryDate = date,
                    EntryLedger = ledger,
                    Debit = debit,
                    Credit = credit,
                    CommonLedger = common.Length == 0 ? CashSettings.DefaultCommonLedger : common,
                    ReferenceNumber = reference,
                    Narration = narration,
                    WorkflowStatus = "Draft",
                    Status = "Draft",
                    SourceRunId = fingerprint,
                    IsUserEdited = true
                };
                _cashEntryService.ValidateAndSave(entry, CashSettings);
                if (!entry.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(entry.Status);
                existingFingerprints.Add(fingerprint);
                imported++;
            }
            catch (Exception exception)
            {
                errors.Add($"row {row}: {exception.Message}");
            }
        }

        if (imported > 0)
        {
            if (_automationService is not null)
                _automationService.PublishCommittedAccountingImport(
                    clientId, financialYear, "Cash", imported, path, "Manual Import");
            else
                _liveChangeHub.Publish(clientId, financialYear, AccountingLiveArea.Cash, "Cash Excel workbook imported");
        }

        if (errors.Count > 0)
            return $"Cash Excel closed • imported {imported:N0}, duplicate/skipped {skipped:N0}, errors {errors.Count:N0}. First error: {errors[0]}";
        return $"Cash Excel closed • imported {imported:N0} voucher(s), duplicate/skipped {skipped:N0}.";
    }

    private static string ReadCashMeta(OpenXmlWorkbookData workbook, string key)
    {
        if (!workbook.TryGetWorksheet("__AAH_CASH_META", out var meta)) return string.Empty;
        for (var row = 1; row <= meta.MaxRow; row++)
            if (meta.GetCell(row, 1).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return meta.GetCell(row, 2).Trim();
        return string.Empty;
    }

    private static bool CanOpenExclusive(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return stream.Length >= 0;
        }
        catch { return false; }
    }

    private static bool TryCashDate(string text, out DateTime value)
    {
        var input = (text ?? string.Empty).Trim();
        var formats = new[] { "dd.MM.yyyy", "dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd", "d.M.yyyy", "d/M/yyyy" };
        if (DateTime.TryParseExact(input, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value) ||
            DateTime.TryParse(input, CultureInfo.CurrentCulture, DateTimeStyles.None, out value))
            return true;

        // FlexibleWorkbookReader can surface a native Excel date cell as its OLE serial.
        if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) &&
            serial >= 1d && serial <= 2958465d)
        {
            try
            {
                value = DateTime.FromOADate(serial).Date;
                return true;
            }
            catch (ArgumentException) { }
        }

        value = default;
        return false;
    }

    private static decimal ParseCashAmount(string text) =>
        decimal.TryParse((text ?? string.Empty).Replace(",", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? Math.Abs(value)
            : 0m;

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = (value ?? string.Empty).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var result = new string(chars).Trim().Replace(' ', '_');
        return result.Length == 0 ? "Client" : result;
    }

    private static void WriteCashEntryWorkbook(string path, AccountingAutomationHub.Domain.Clients.ClientProfile client, CashEntrySettings settings)
    {
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteCashXlsxEntry(archive, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
        WriteCashXlsxEntry(archive, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        WriteCashXlsxEntry(archive, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Cash Entry Import\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"__AAH_CASH_META\" sheetId=\"2\" r:id=\"rId2\" state=\"hidden\"/></sheets></workbook>");
        WriteCashXlsxEntry(archive, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        WriteCashXlsxEntry(archive, "xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"dd.mm.yyyy\"/><numFmt numFmtId=\"165\" formatCode=\"#,##0.00\"/></numFmts><fonts count=\"3\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><color rgb=\"FFFFFFFF\"/><sz val=\"11\"/><name val=\"Calibri\"/></font><font><i/><color rgb=\"FF666666\"/><sz val=\"10\"/><name val=\"Calibri\"/></font></fonts><fills count=\"4\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E78\"/><bgColor indexed=\"64\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEAF2F8\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"2\"><border/><border><left style=\"thin\"><color rgb=\"FFD9E2F3\"/></left><right style=\"thin\"><color rgb=\"FFD9E2F3\"/></right><top style=\"thin\"><color rgb=\"FFD9E2F3\"/></top><bottom style=\"thin\"><color rgb=\"FFD9E2F3\"/></bottom><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs><cellXfs count=\"7\"><xf xfId=\"0\"/><xf xfId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf><xf xfId=\"0\" fillId=\"3\" borderId=\"1\" applyFill=\"1\" applyBorder=\"1\"/><xf xfId=\"0\" fontId=\"2\" borderId=\"1\" applyFont=\"1\" applyBorder=\"1\"/><xf xfId=\"0\" numFmtId=\"164\" borderId=\"1\" applyNumberFormat=\"1\" applyBorder=\"1\"/><xf xfId=\"0\" numFmtId=\"165\" borderId=\"1\" applyNumberFormat=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\"/></xf><xf xfId=\"0\" borderId=\"1\" applyBorder=\"1\"/></cellXfs></styleSheet>");

        var headers = new[] { "Date", "Ledger Name", "Debit", "Credit", "Amount", "Dr/Cr", "Common Ledger", "Reference", "Narration" };
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><dimension ref=\"A1:I203\"/><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><sheetFormatPr defaultRowHeight=\"18\"/><cols><col min=\"1\" max=\"1\" width=\"14\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"32\" customWidth=\"1\"/><col min=\"3\" max=\"5\" width=\"14\" customWidth=\"1\"/><col min=\"6\" max=\"6\" width=\"10\" customWidth=\"1\"/><col min=\"7\" max=\"8\" width=\"24\" customWidth=\"1\"/><col min=\"9\" max=\"9\" width=\"52\" customWidth=\"1\"/></cols><sheetData>");
        sb.Append("<row r=\"1\" ht=\"30\" customHeight=\"1\">");
        for (var c = 0; c < headers.Length; c++)
            sb.Append(CashCell(c + 1, 1, headers[c], 1));
        sb.Append("</row>");
        for (var r = 2; r <= 203; r++)
        {
            sb.Append($"<row r=\"{r}\">");
            sb.Append(CashBlankCell(1, r, 4));
            sb.Append(CashBlankCell(2, r, 6));
            sb.Append(CashBlankCell(3, r, 5));
            sb.Append(CashBlankCell(4, r, 5));
            sb.Append(CashBlankCell(5, r, 5));
            sb.Append(CashBlankCell(6, r, 6));
            sb.Append(CashCell(7, r, settings.DefaultCommonLedger, r == 2 ? 3 : 2));
            sb.Append(CashBlankCell(8, r, 6));
            sb.Append(CashBlankCell(9, r, 6));
            sb.Append("</row>");
        }
        sb.Append("</sheetData><autoFilter ref=\"A1:I203\"/></worksheet>");
        WriteCashXlsxEntry(archive, "xl/worksheets/sheet1.xml", sb.ToString());

        var meta = new[]
        {
            ("Kind", GeneratedAccountingWorkbookIdentity.CashWorkbookKind),
            ("SchemaVersion", "1"),
            ("ClientId", client.Id.ToString(CultureInfo.InvariantCulture)),
            ("ClientCode", client.ClientCode),
            ("ClientName", client.ClientName),
            ("FinancialYear", client.FinancialYear),
            ("DefaultCommonLedger", settings.DefaultCommonLedger),
            ("ExportedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
        };
        var metaSb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var i = 0; i < meta.Length; i++)
        {
            var r = i + 1;
            metaSb.Append($"<row r=\"{r}\">{CashCell(1, r, meta[i].Item1, 1)}{CashCell(2, r, meta[i].Item2, 0)}</row>");
        }
        metaSb.Append("</sheetData></worksheet>");
        WriteCashXlsxEntry(archive, "xl/worksheets/sheet2.xml", metaSb.ToString());
    }

    private static string CashCell(int column, int row, string value, int style) =>
        $"<c r=\"{CashColumnName(column)}{row}\" t=\"inlineStr\" s=\"{style}\"><is><t xml:space=\"preserve\">{CashXml(value)}</t></is></c>";

    private static string CashBlankCell(int column, int row, int style) =>
        $"<c r=\"{CashColumnName(column)}{row}\" s=\"{style}\"/>";

    private static string CashColumnName(int column)
    {
        var result = string.Empty;
        while (column > 0)
        {
            column--;
            result = (char)('A' + column % 26) + result;
            column /= 26;
        }
        return result;
    }

    private static string CashXml(string? value)
    {
        var source = value ?? string.Empty;
        var cleaned = new string(source.Where(XmlConvert.IsXmlChar).ToArray());
        return System.Security.SecurityElement.Escape(cleaned) ?? string.Empty;
    }

    private static void WriteCashXlsxEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private void SaveCashEntry() => SaveCashEntryRecord(SelectedCashEntry);

    private void CloneCashEntry()
    {
        var source = SelectedCashEntry;
        if (source is null || source.Id <= 0) return;
        try
        {
            var clone = _transactionAutomationService.CloneCash(source, source.EntryDate);
            ReloadCashEntries(clone.Id);
            CashStatusText = $"Cash Entry {source.EntryId} cloned as a new Draft.";
        }
        catch (Exception ex) { CashStatusText = $"Cash clone failed: {ex.Message}"; }
    }

    public void CreateCashCloneSeries(IReadOnlyList<CloneScheduleEntry> schedule)
    {
        var source = SelectedCashEntry;
        if (source is null || source.Id <= 0 || schedule is null || schedule.Count == 0) return;
        try
        {
            var created = _transactionAutomationService.CloneCashSeries(
                source,
                schedule.Select(item => (item.Date, item.Amount)).ToArray());
            ReloadCashEntries(created.LastOrDefault()?.Id ?? 0L);
            CashStatusText = $"Created {created.Count:N0} Cash clone(s) from {source.EntryId}. All generated entries are Draft/Review Required.";
            NotifyCounts();
        }
        catch (Exception ex) { CashStatusText = $"Advanced Cash clone failed: {ex.Message}"; }
    }

    private void ReverseCashEntry()
    {
        var source = SelectedCashEntry;
        if (source is null || source.Id <= 0) return;
        var date = DateTime.Today;
        if (!IsDateInsideFinancialYear(date, source.FinancialYear)) date = source.EntryDate;
        try
        {
            var reversal = _transactionAutomationService.ReverseCash(source, date);
            ReloadCashEntries(reversal.Id);
            CashStatusText = $"Reverse Cash Entry created for {source.EntryId}; the original entry was retained.";
        }
        catch (Exception ex) { CashStatusText = $"Cash reversal failed: {ex.Message}"; }
    }

    public void CreateCashRecurringSchedule(string profileName, string frequency, DateTime startDate, DateTime? endDate)
    {
        var source = SelectedCashEntry;
        if (source is null || source.Id <= 0) return;
        try
        {
            var profile = _transactionAutomationService.CreateCashRecurringProfile(source, profileName, frequency, startDate, endDate);
            CashStatusText = $"Recurring Cash profile '{profile.ProfileName}' created. Next run: {profile.NextRunDate:dd-MMM-yyyy}.";
            NotifyCounts();
        }
        catch (Exception ex) { CashStatusText = $"Recurring Cash setup failed: {ex.Message}"; }
    }

    private void MakeCashRecurring()
    {
        var source = SelectedCashEntry;
        if (source is null || source.Id <= 0) return;
        try
        {
            var profile = _transactionAutomationService.CreateCashRecurringProfile(
                source, $"{source.EntryLedger} recurring cash", "Monthly", source.EntryDate.AddMonths(1), null);
            CashStatusText = $"Recurring Cash profile '{profile.ProfileName}' created. Next run: {profile.NextRunDate:dd-MMM-yyyy}.";
            NotifyCounts();
        }
        catch (Exception ex) { CashStatusText = $"Recurring Cash setup failed: {ex.Message}"; }
    }

    private void ApproveCashEntry()
    {
        var source = SelectedCashEntry;
        if (source is null || source.Id <= 0) return;
        if (!source.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
        {
            CashStatusText = "Cash Entry must be Ready before approval.";
            return;
        }
        try
        {
            _transactionAutomationService.ApproveCash(source);
            ReloadCashEntries(source.Id);
            CashStatusText = $"Cash Entry {source.EntryId} approved by {Environment.UserName}.";
        }
        catch (Exception ex) { CashStatusText = $"Cash approval failed: {ex.Message}"; }
    }

    private void ReloadCashEntries(long selectedId = 0)
    {
        CashEntries.ReplaceRange(_repository.GetCashEntries(CurrentClientId, CurrentFinancialYear));
        SelectedCashEntry = selectedId > 0 ? CashEntries.FirstOrDefault(x => x.Id == selectedId) : CashEntries.FirstOrDefault();
        BuildCashEntryMappingRows();
        QueueWorkspaceCacheRefresh();
        NotifyCounts();
        _liveChangeHub.Publish(CurrentClientId, CurrentFinancialYear, AccountingLiveArea.Cash, "Cash Entry automation changed");
    }

    private static bool IsDateInsideFinancialYear(DateTime date, string financialYear)
    {
        var first = financialYear.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return !int.TryParse(first, out var year) || (date.Date >= new DateTime(year, 4, 1) && date.Date <= new DateTime(year + 1, 3, 31));
    }

    private void RepairAdvancedClosureCashEntries()
    {
        if (!DemandGovernancePermission(GovernancePermissions.EditAccounting, "repair Advanced Closure Cash entries")) return;
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        CashStatusText = "Checking previously generated Advanced Closure Cash entries against the corrected amount/date plan...";
        QueueAdvancedClosureCashRepair(client, showNoChangeStatus: true, force: true);
    }

    private void QueueAdvancedClosureCashRepair(
        AccountingAutomationHub.Domain.Clients.ClientProfile client,
        bool showNoChangeStatus,
        IReadOnlyList<CashEntryRecord>? observedRows = null,
        bool force = false)
    {
        var clientId = client.Id;
        var financialYear = client.FinancialYear.Trim();
        var key = $"{clientId}|{financialYear.ToUpperInvariant()}";

        // Fix137 R35: the old implementation ran historical Advanced Closure repair on
        // every Cash cache warm. Recurring generation changes ordinary Cash rows, which
        // refreshes the Cash cache; the cache warm then ran closure repair again and a
        // repair write queued another Cash refresh. Gate the automatic repair by the
        // exact AAH-owned Transaction-Date Closure rows only. A manual Repair command
        // remains force=true and bypasses this gate.
        var fingerprint = BuildAdvancedClosureCashFingerprint(
            observedRows ?? _repository.GetCashEntries(clientId, financialYear));
        if (!force &&
            _advancedClosingCashRepairFingerprints.TryGetValue(key, out var checkedFingerprint) &&
            checkedFingerprint.Equals(fingerprint, StringComparison.Ordinal))
            return;

        if (!_advancedClosingCashRepairInFlight.TryAdd(key, 0)) return;

        _ = Task.Run(() => _advancedClosingCashRepairService.Repair(clientId, financialYear)).ContinueWith(task =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                _advancedClosingCashRepairInFlight.TryRemove(key, out _);
                return;
            }

            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var current = _clientContext.CurrentClient;
                    if (current is null || current.Id != clientId ||
                        !current.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)) return;

                    if (!task.IsCompletedSuccessfully)
                    {
                        if (showNoChangeStatus)
                            CashStatusText = $"Advanced Closure Cash repair failed: {task.Exception?.GetBaseException().Message ?? "Unknown error"}";
                        return;
                    }

                    var result = task.Result;
                    var repairedRows = _repository.GetCashEntries(clientId, financialYear);
                    _advancedClosingCashRepairFingerprints[key] = BuildAdvancedClosureCashFingerprint(repairedRows);
                    if (result.Changed > 0)
                    {
                        CashEntries.ReplaceRange(repairedRows);
                        SelectedCashEntry = CashEntries.FirstOrDefault();
                        BuildCashEntryMappingRows();
                        QueueWorkspaceCacheRefresh();
                        NotifyCounts();
                        CashStatusText = $"{result.Detail} Balance Sheet/Ledger totals were invalidated for live rebuild.";
                        _liveChangeHub.Publish(clientId, financialYear, AccountingLiveArea.Cash, "Historical Advanced Closure Cash entries corrected");
                    }
                    else if (showNoChangeStatus)
                    {
                        CashStatusText = result.Detail;
                    }
                }
                finally
                {
                    _advancedClosingCashRepairInFlight.TryRemove(key, out _);
                }
            }), DispatcherPriority.Background);
        });
    }

    private static string BuildAdvancedClosureCashFingerprint(IEnumerable<CashEntryRecord> rows)
    {
        // Only rows owned by the Transaction-Date Advanced Closure engine participate.
        // Recurring/manual Cash rows are intentionally excluded, so those changes cannot
        // schedule the repair loop. Stable ordering makes the fingerprint deterministic.
        return string.Join("\n", rows
            .Where(AdvancedClosingCashRepairService.IsOwnedTransactionDateCash)
            .OrderBy(row => row.Id)
            .ThenBy(row => row.EntryId, StringComparer.OrdinalIgnoreCase)
            .Select(row => string.Join("|",
                row.Id.ToString(CultureInfo.InvariantCulture),
                row.EntryDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                row.Debit.ToString("0.00", CultureInfo.InvariantCulture),
                row.Credit.ToString("0.00", CultureInfo.InvariantCulture),
                (row.EntryLedger ?? string.Empty).Trim(),
                (row.EntryId ?? string.Empty).Trim(),
                (row.SourceRunId ?? string.Empty).Trim(),
                (row.SourceJobId ?? string.Empty).Trim(),
                row.UpdatedUtc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture))));
    }

    public void DeleteSelectedCashEntries(IReadOnlyCollection<CashEntryRecord> selectedRows)
    {
        if (selectedRows is null || selectedRows.Count == 0)
        {
            CashStatusText = "Select one or more Cash rows first. Ctrl/Shift multi-selection is supported.";
            return;
        }
        if (!DemandGovernancePermission(GovernancePermissions.DeleteAccounting, "bulk delete Cash entries")) return;

        var rows = selectedRows
            .Where(item => item.Id > 0 && item.ClientId == CurrentClientId &&
                           item.FinancialYear.Equals(CurrentFinancialYear, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToArray();
        if (rows.Length == 0)
        {
            CashStatusText = "No valid Cash rows are selected for the current Client/FY.";
            return;
        }

        var closureCount = rows.Count(AdvancedClosingCashRepairService.IsOwnedTransactionDateCash);
        var closureNote = closureCount > 0
            ? $"\n\n{closureCount:N0} selected row(s) are AAH-generated Advanced Closure entries."
            : string.Empty;
        var message = $"Delete {rows.Length:N0} selected Cash entr{(rows.Length == 1 ? "y" : "ies")}?{closureNote}\n\nThis deletes the selected AAH Cash rows only; unselected Cash entries are untouched.";
        if (MessageBox.Show(message, "Bulk Delete Cash Entries", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var deleted = _repository.DeleteCashEntries(CurrentClientId, CurrentFinancialYear, rows.Select(item => item.Id).ToArray());
        if (deleted <= 0)
        {
            CashStatusText = "No Cash entries were deleted.";
            return;
        }

        _ledgerTotalService.Invalidate(CurrentClientId, CurrentFinancialYear);
        ReloadCashEntries();
        CashStatusText = $"Deleted {deleted:N0} selected Cash entr{(deleted == 1 ? "y" : "ies")}. Unselected rows were retained.";
    }

    private void DeleteCashEntry()
    {
        if (SelectedCashEntry is null || SelectedCashEntry.Id <= 0) return;
        if (!DemandGovernancePermission(GovernancePermissions.DeleteAccounting, "delete Cash entries")) return;
        if (MessageBox.Show("Delete the selected Cash Entry?", "Cash Entry", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var deletedClientId = SelectedCashEntry.ClientId;
        var deletedFinancialYear = SelectedCashEntry.FinancialYear;
        _repository.DeleteCashEntry(SelectedCashEntry.Id);
        _ledgerTotalService.Invalidate(deletedClientId, deletedFinancialYear);
        ReloadCashEntries();
        CashStatusText = "Cash Entry deleted.";
        _liveChangeHub.Publish(deletedClientId, deletedFinancialYear, AccountingLiveArea.Cash, "Cash Entry deleted");
    }

    private void SaveCashSettings()
    {
        if (!DemandGovernancePermission(GovernancePermissions.EditAccounting, "change Cash Entry Control settings")) return;
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        CashSettings.ClientId = client.Id;
        CashSettings.FinancialYear = client.FinancialYear;
        CashSettings.LayoutMode = CashLayoutMode;
        CashSettings.UpdatedUtc = DateTime.UtcNow;
        _repository.SaveCashSettings(CashSettings);
        QueueWorkspaceCacheRefresh();
        NotifyCounts();
        CashStatusText = "Cash Entry Control saved.";
    }

    private bool HasGovernancePermission(string permission)
    {
        if (NetworkClientRuntime.IsNetworkClient) return NetworkClientRuntime.HasPermission(permission);
        return _governanceRepository.HasPermission(Environment.UserName, permission);
    }

    private bool DemandGovernancePermission(string permission, string action)
    {
        if (DesktopPermissionService.CanMutateAccounting(_governanceRepository, permission)) return true;
        var message = DesktopPermissionService.DeniedMessage(_governanceRepository, action, accountingMutation: true);
        StatusText = message;
        CashStatusText = message;
        MessageBox.Show(message, "Permissions", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void ApplyCashEntryMapping()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        try
        {
            if (IsCashMasterUpdateMode)
            {
                var rows = CashEntryMasterMappings.Where(item => item.IsSelected).ToList();
                if (rows.Count == 0 && SelectedCashMasterMapping is not null) rows.Add(SelectedCashMasterMapping);
                if (rows.Count == 0) throw new InvalidOperationException("Select at least one Master Update row.");
                if (rows.Any(row => row.MasterStatus.Equals("Ambiguous Tally Master", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("An Entry Ledger resolves to more than one Tally Ledger MASTERID. Resolve the duplicate Master by MASTERID in Master Centre before applying Cash Master mapping; AAH will not choose by display name.");
                var total = 0;
                foreach (var row in rows)
                {
                    total += _cashEntryService.ApplyCashMasterMapping(
                        client.Id,
                        client.FinancialYear,
                        row.EntryLedger,
                        row.MapCreateAs,
                        row.ParentGroup,
                        string.Empty,
                        CreateLedgerWhenApplying);
                    row.Result = "Updated";
                }
                QueueWorkspaceCacheRefresh();
                CashStatusText = $"Cash Entry Mapping updated {total:N0} row(s).";
                _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Cash, "Cash Entry Master mapping applied");
                _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, "Cash Entry Master mapping changed Masters");
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedCashCommonLedger)) throw new InvalidOperationException("Select the Common Ledger.");
            if (!CashInHandLedgerNames.Contains(SelectedCashCommonLedger, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("The selected Common Ledger must be under Cash-in-hand.");
            var commonRows = CashEntryCommonMappings.Where(item => item.IsSelected).ToList();
            if (commonRows.Count == 0 && SelectedCashCommonMapping is not null) commonRows.Add(SelectedCashCommonMapping);
            if (commonRows.Count == 0) throw new InvalidOperationException("Select at least one Map Common Ledger row.");
            foreach (var row in commonRows)
            {
                row.Entry.CommonLedger = SelectedCashCommonLedger.Trim();
                _cashEntryService.ValidateAndSave(row.Entry, CashSettings);
            }
            QueueWorkspaceCacheRefresh();
            CashStatusText = $"Common Ledger applied to {commonRows.Count:N0} Cash Entry row(s).";
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Cash, "Cash Entry Common Ledger mapping applied");
        }
        catch (Exception exception)
        {
            CashStatusText = exception.Message;
            MessageBox.Show(exception.Message, "Cash Entry Mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }


    public AdvancedReconciliationPreview PreviewAdvancedReconciliation(
        IReadOnlyList<BankTransactionRecord> transactions, decimal bookAmount, string references, decimal adjustmentAmount, string adjustmentLedger)
    {
        return _advancedReconciliationService.Preview(
            transactions, bookAmount,
            references.Split(new[] { '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            adjustmentAmount, adjustmentLedger);
    }

    public AdvancedReconciliationPreview CommitAdvancedReconciliation(AdvancedReconciliationPreview preview)
    {
        var committed = _advancedReconciliationService.Commit(CurrentClientId, CurrentFinancialYear, preview);
        StatusText = $"Advanced reconciliation {committed.MatchId} committed.";
        return committed;
    }

    public void PrepareLedgerMappingWorkspace() =>
        PrepareLedgerMappingWorkspace(_ledgerMappingBankFilter, _ledgerMappingShowMapped, _ledgerMappingVoucherFilter);

    private void PrepareLedgerMappingWorkspace(string bankFilter, string showMapped, string voucherFilter)
    {
        if (CurrentClientId <= 0) return;
        try
        {
            EnsureLedgerMappingPreferencesLoaded(CurrentClientId, CurrentFinancialYear);
            bankFilter = _ledgerMappingBankFilter;
            showMapped = _ledgerMappingShowMapped;
            voucherFilter = _ledgerMappingVoucherFilter;
            if (LedgerMappingRows.Count == 0)
            {
                if (!_ledgerMappingsLoaded)
                {
                    if (_workspaceStateCache.TryGetModulesForPresentation(
                        CurrentClientId, CurrentFinancialYear,
                        new[] { ClientWorkspaceModule.LedgerMapping }, out var cached))
                    {
                        LedgerMappings.ReplaceRange(cached.BankLedgerMappings);
                        _ledgerMappingsLoaded = true;
                    }
                    else
                    {
                        _workspaceStateCache.QueueModuleRefresh(CurrentClientId, CurrentFinancialYear, ClientWorkspaceModule.LedgerMapping);
                        StatusText = "Preparing Ledger Mapping stream in the background…";
                        return;
                    }
                }
                BuildLedgerMappingRows(bankFilter, showMapped, voucherFilter);
                _ledgerMappingWorkspaceLoaded = true;
            }
            else
            {
                ApplyLedgerMappingFilters();
                _ledgerMappingWorkspaceLoaded = true;
            }
            QueueLedgerMappingProjectionUpgradeIfNeeded();
            if (HasLedgerMappingCoverageGap())
            {
                StatusText = $"Ledger Mapping loaded • {LedgerMappingFilterCountText}. Recovering missing membership in the background…";
                _ = RepairLedgerMappingCoverageIfNeededAsync();
            }
            else
            {
                StatusText = $"Ledger Mapping ready • {LedgerMappingFilterCountText}.";
            }
        }
        catch (Exception exception)
        {
            LedgerMappingRows.Clear();
            FilteredLedgerMappingRows.Clear();
            LedgerMappingDetails.Clear();
            StatusText = $"Ledger Mapping could not be opened: {exception.Message}";
            MessageBox.Show(StatusText, "Ledger Mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool HasLedgerMappingCoverageGap()
    {
        // Never judge mapping coverage against an unhydrated Banking stream. An early
        // Ledger Mapping snapshot can arrive before BankTransactions; treating that temporary
        // zero-row state as authoritative used to trigger an empty rebuild that deleted the
        // board and forced the user to press Rebuild Mapping later.
        if (CurrentClientId <= 0 || !_workspaceStateCache.IsModuleReady(CurrentClientId, CurrentFinancialYear, ClientWorkspaceModule.Banking))
            return false;

        var activeIds = Transactions
            .Where(item => item.Id > 0 && !item.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase))
            .Where(item => !ExcelLedgerMappingParser.IsLedgerMappingCoverageExcludedMode(item.Mode))
            .Select(item => item.Id)
            .ToArray();

        // Coverage means exactly one mapping membership per active Bank Statement
        // transaction. Set equality alone misses overlapping/duplicate memberships,
        // which can make the Ledger Mapping Count exceed the Bank Statement total.
        return BankLedgerMappingService.HasCoverageIntegrityIssue(LedgerMappings, activeIds);
    }

    private async Task RepairLedgerMappingCoverageIfNeededAsync()
    {
        if (_ledgerMappingCoverageRepairRunning || CurrentClientId <= 0 || !HasLedgerMappingCoverageGap()) return;
        if (_activeLedgerMappingEditorRow is not null)
        {
            _ledgerMappingCoverageRepairDeferred = true;
            return;
        }

        var clientId = CurrentClientId;
        var financialYear = CurrentFinancialYear;
        var bankFilter = LedgerMappingBankFilter;
        var showMapped = LedgerMappingShowMapped;
        var voucherFilter = LedgerMappingVoucherFilter;
        _ledgerMappingCoverageRepairRunning = true;
        try
        {
            var activeIds = Transactions
                .Where(item => item.Id > 0 && !item.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase))
                .Where(item => !ExcelLedgerMappingParser.IsLedgerMappingCoverageExcludedMode(item.Mode))
                .Select(item => item.Id)
                .Distinct()
                .ToArray();
            var repaired = await Task.Run(() => _mappingService.Rebuild(clientId, financialYear, activeIds).ToArray());
            if (CurrentClientId != clientId || !CurrentFinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)) return;

            var sameAuthority = LedgerMappingAuthorityFingerprint(LedgerMappings)
                .Equals(LedgerMappingAuthorityFingerprint(repaired), StringComparison.Ordinal);
            if (!sameAuthority)
                LedgerMappings.ReplaceRange(repaired);
            _ledgerMappingsLoaded = true;
            if (sameAuthority && LedgerMappingRows.Count > 0)
                QueueLedgerMappingExistingMatchRefresh();
            else
                BuildLedgerMappingRows(bankFilter, showMapped, voucherFilter);
            _workspaceStateCache.QueueModuleRefresh(clientId, financialYear, ClientWorkspaceModule.LedgerMapping);
            var coverageStillInvalid = HasLedgerMappingCoverageGap();
            if (!coverageStillInvalid && _ledgerMappingProjectionUpgradeRequired)
            {
                _ledgerMappingProjectionUpgradeRequired = false;
                _ledgerMappingProjectionUpgradeDeferred = false;
                PersistLedgerMappingPreference("ProjectionVersion", LedgerMappingProjectionVersion);
            }
            StatusText = coverageStillInvalid
                ? $"Ledger Mapping recovery completed but coverage still reports {LedgerMappingFilterCountText}."
                : $"Ledger Mapping coverage recovered • {LedgerMappingFilterCountText}.";
        }
        catch (Exception exception)
        {
            StatusText = $"Ledger Mapping coverage recovery could not complete: {exception.Message}. Use Rebuild Mapping to retry.";
        }
        finally
        {
            _ledgerMappingCoverageRepairRunning = false;
        }
    }

    private void QueueLedgerMappingProjectionUpgradeIfNeeded()
    {
        if (!_ledgerMappingProjectionUpgradeRequired || _ledgerMappingProjectionUpgradeRunning || CurrentClientId <= 0) return;
        if (_activeLedgerMappingEditorRow is not null)
        {
            _ledgerMappingProjectionUpgradeDeferred = true;
            return;
        }

        var clientId = CurrentClientId;
        var financialYear = CurrentFinancialYear;
        // R46 fast-load contract: an application projection-version change must never turn
        // page opening into a full Ledger Mapping rebuild. The already prepared Banking +
        // LedgerMapping cache streams paint first. Only a genuine membership integrity gap
        // is repaired; otherwise the new projection version is accepted immediately.
        if (!_workspaceStateCache.IsModuleReady(clientId, financialYear, ClientWorkspaceModule.Banking) ||
            !_workspaceStateCache.IsModuleReady(clientId, financialYear, ClientWorkspaceModule.LedgerMapping)) return;

        if (HasLedgerMappingCoverageGap())
        {
            _ledgerMappingProjectionUpgradeDeferred = true;
            _ = RepairLedgerMappingCoverageIfNeededAsync();
            return;
        }

        _ledgerMappingProjectionUpgradeRunning = true;
        try
        {
            _ledgerMappingProjectionUpgradeRequired = false;
            _ledgerMappingProjectionUpgradeDeferred = false;
            PersistLedgerMappingPreference("ProjectionVersion", LedgerMappingProjectionVersion);
        }
        finally
        {
            _ledgerMappingProjectionUpgradeRunning = false;
        }
    }

    private void PrepareReportsWorkspace()
    {
        if (CurrentClientId <= 0 || _reportsWorkspaceLoaded) return;
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        try
        {
            ClearReportCollections();
            LoadWorkbookReports(client.Id, client.FinancialYear, client.ClientName);
            AppendTransientImportIssuesToReport();
            _reportsWorkspaceLoaded = true;
            StatusText = "Banking reports loaded on demand.";
        }
        catch (Exception exception)
        {
            StatusText = $"Banking reports could not be loaded: {exception.Message}";
        }
    }

    private void OpenBankStatementWorkspace()
    {
        SelectedBankingTabIndex = 0;
        ApplyBankStatementFilters();
        StatusText = $"Bank Statement opened • {BankStatementVisibleCount:N0} active transaction(s) visible.";
    }

    private void OpenBankAccountsWorkspace()
    {
        SelectedAccount ??= Accounts.FirstOrDefault();
        IsBankAccountPopupOpen = SelectedAccount is not null;
        StatusText = SelectedAccount is null
            ? "No Bank Accounts are available. Import a Bank Statement first."
            : "Bank rename and Master ledger details opened in a separate panel.";
    }

    private void CloseBankAccountPopup() => IsBankAccountPopupOpen = false;

    private void OpenLedgerMappingWorkspace()
    {
        IsLedgerMappingDetailOpen = false;
        SelectedLedgerMappingRow = null;
        if (SelectedBankingTabIndex == 4)
            PrepareLedgerMappingWorkspace();
        else
            SelectedBankingTabIndex = 4;
    }

    private void OpenBalanceReviewWorkspace()
    {
        SelectedReconciliationViewIndex = 0;
        IsBalanceReviewDetailOpen = false;
        SelectedReconciliationRow = null;
        SelectedBankingTabIndex = 2;
        StatusText = ReconciliationIssueCount > 0
            ? $"Balance Review opened • {ReconciliationIssueCount:N0} item(s) require attention."
            : "Balance Review opened • no pending balance differences.";
    }

    private void CloseLedgerMappingDetail() => CloseLedgerMappingPreview();

    public void OpenLedgerMappingPreview(LedgerMappingWorkbenchRow? row)
    {
        if (row is null) return;
        SelectedLedgerMappingRow = row;
        RefreshLedgerMappingDetails();
        IsLedgerMappingDetailOpen = true;
        StatusText = $"Ledger Mapping preview opened for {row.SummaryName}.";
    }

    public void CloseLedgerMappingPreview()
    {
        IsLedgerMappingDetailOpen = false;
        LedgerMappingDetails.Clear();
        FilteredLedgerMappingDetails.Clear();
        _ledgerMappingDetailFilterColumn = "ALL COLUMNS";
        _ledgerMappingDetailFilterText = string.Empty;
        OnPropertyChanged(nameof(LedgerMappingDetailFilterColumn));
        OnPropertyChanged(nameof(LedgerMappingDetailFilterText));
        SelectedLedgerMappingDetail = null;
        OnPropertyChanged(nameof(SelectedLedgerDetailStatistics));
        RefreshLedgerMappingDetailSelectionState();
    }

    private void CloseBalanceReviewDetail()
    {
        IsBalanceReviewDetailOpen = false;
        SelectedReconciliationRow = null;
    }

    public void UseSuggestedLedger(LedgerMappingWorkbenchRow? row)
    {
        if (!DemandGovernancePermission(GovernancePermissions.LedgerMappingEdit, "change a Ledger Mapping suggestion")) return;
        if (row is null || string.IsNullOrWhiteSpace(row.SuggestedMatch)) return;
        row.FinalLedger = row.SuggestedMatch.Trim();
        if (ResolveMasterLedgerMatches(row.FinalLedger).Count == 0)
        {
            if (row.Type.Equals("Payment", StringComparison.OrdinalIgnoreCase)) row.LedgerGroup = "Sundry Creditors";
            else if (row.Type.Equals("Receipt", StringComparison.OrdinalIgnoreCase)) row.LedgerGroup = "Sundry Debtors";
        }
        StageLedgerMappingRow(row);
        StatusText = $"Suggested Match copied to Final Ledger for {row.SummaryName}.";
    }

    private void OpenBulkSelectedMapping()
    {
        if (!DemandGovernancePermission(GovernancePermissions.LedgerMappingEdit, "bulk edit Ledger Mapping")) return;
        var selected = FilteredLedgerMappingRows
            .Where(row => row.IsSelected)
            .ToArray();

        if (selected.Length == 0)
        {
            StatusText = "Select at least one Ledger Mapping row before opening Bulk Mapping.";
            return;
        }

        try { RefreshBulkMappingMasterChoices(); }
        catch (Exception exception)
        {
            StatusText = "Could not load the active Ledger/Group Master lists for Bulk Mapping: " + exception.Message;
            MessageBox.Show(StatusText, "Bulk Ledger Mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BulkLedgerMappingRows.Clear();
        foreach (var row in selected) BulkLedgerMappingRows.Add(row);

        // Fix131: freeze the exact transaction IDs represented by the CURRENT visible
        // Ledger Mapping row scope (Bank + Voucher Type + Method + Date). The Bulk window
        // must never reopen a row's full historical membership and show a different count.
        // Mixed is display-only: frozen membership is split into real Payment/Receipt fragments.
        var selectedAtomicMappings = new List<BankLedgerMapping>();
        var visibleVoucherType = NormalizeBulkVoucherTypeScope(LedgerMappingVoucherFilter);
        foreach (var row in selected)
            selectedAtomicMappings.AddRange(BuildVisibleLedgerMappingFragments(row));

        _bulkSelectedBaseMappings.Clear();
        foreach (var mapping in selectedAtomicMappings) _bulkSelectedBaseMappings.Add(mapping);

        BulkMappingBankOptions.Clear();
        BulkMappingBankOptions.Add("All Banks");
        foreach (var bank in _bulkSelectedBaseMappings
                     .SelectMany(CurrentBulkBanksForMapping)
                     .Where(bank => bank.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(bank => bank, StringComparer.OrdinalIgnoreCase))
            BulkMappingBankOptions.Add(bank);

        // The Bulk Mapping layout is fixed when the workspace opens. A live Voucher Type
        // filter must narrow rows inside the current panel; it must never switch a mixed
        // Payment/Receipt workspace into the separate single-mapping panel.
        var baseVoucherTypes = BuildBulkVoucherTypeFragments(_bulkSelectedBaseMappings, visibleVoucherType)
            .Select(mapping => NormalizeBulkVoucherType(mapping.VoucherType))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _bulkMappingUseVoucherTypeGrid = baseVoucherTypes.Length > 1;
        _bulkVoucherDraftsByType.Clear();
        _bulkAppliedTransactionIdsThisSession.Clear();
        _bulkSimpleLedgerDraft = string.Empty;
        _bulkSimpleGroupDraft = string.Empty;

        _suppressBulkMappingLiveFilterRefresh = true;
        try
        {
            BulkMappingBankFilter = "All Banks";
            BulkMappingVoucherTypeFilter = visibleVoucherType.Length > 0 ? visibleVoucherType : "All";
            BulkMappingAmountOperator = "Any";
            BulkMappingAmountFromText = string.Empty;
            BulkMappingAmountToText = string.Empty;
            BulkMappingAmountSearchText = string.Empty;
            BulkMappingShowMixedVoucherType = false;
        }
        finally
        {
            _suppressBulkMappingLiveFilterRefresh = false;
        }

        IsBulkLedgerMappingOpen = true;
        RebuildBulkMappingFilteredScope();
        StatusText = _bulkSelectedAtomicMappings.Count > 0
            ? "Bulk Mapping opened. Bank, Voucher Type and Amount filters apply only to the frozen checked selection."
            : "No selected Bank transactions match the current Bulk Mapping filters.";
    }

    private IReadOnlySet<string> CurrentBulkBanksForMapping(BankLedgerMapping mapping)
    {
        // Persisted BankFilter text can predate an account rename. The exact frozen
        // transaction membership is authoritative for both Bulk Mapping grouping and
        // its Bank filter, matching the normal Ledger Mapping workbench behavior.
        var transactionIds = mapping.TransactionIds.Where(id => id > 0).ToHashSet();
        if (transactionIds.Count > 0)
        {
            var banks = Transactions
                .Where(transaction => transactionIds.Contains(transaction.Id))
                .Select(transaction => transaction.BankAccountName?.Trim() ?? string.Empty)
                .Where(bank => bank.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (banks.Count > 0) return banks;
        }

        var stored = mapping.BankFilter?.Trim() ?? string.Empty;
        return stored.Length > 0
            ? new HashSet<string>(new[] { stored }, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private void RefreshBulkMappingMasterChoices()
    {
        var clientId=CurrentClientId;
        var financialYear=CurrentFinancialYear;
        if(clientId<=0||string.IsNullOrWhiteSpace(financialYear))return;

        // Bulk Mapping must never depend on a stale screen cache for Master dropdowns. Read the
        // committed active Master repository immediately before opening the popup so newly added,
        // restored or archived Ledger/Group Masters are reflected without reopening the client.
        var masters=_masterRepository.Get(clientId,financialYear,false,string.Empty)
            .Where(master=>!master.IsArchived)
            .ToArray();
        _activeMasterRecords=masters;

        MasterLedgerNames.Clear();
        foreach(var name in masters.Where(master=>master.Type.Equals("Ledger",StringComparison.OrdinalIgnoreCase))
                     .Select(master=>master.Name.Trim()).Where(name=>name.Length>0)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name=>name,StringComparer.OrdinalIgnoreCase))
            MasterLedgerNames.Add(name);

        MasterGroupNames.Clear();
        foreach(var name in MasterSynchronizationService.TallyPrebuiltLedgerGroupNames)
            if(!MasterGroupNames.Contains(name,StringComparer.OrdinalIgnoreCase)) MasterGroupNames.Add(name);
        foreach(var name in masters.Where(master=>master.Type.Equals("Group",StringComparison.OrdinalIgnoreCase))
                     .Select(master=>master.Name.Trim()).Where(name=>name.Length>0)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name=>name,StringComparer.OrdinalIgnoreCase))
            if(!MasterGroupNames.Contains(name,StringComparer.OrdinalIgnoreCase)) MasterGroupNames.Add(name);

        // Bulk Mapping deliberately includes the canonical prebuilt Tally-format Groups
        // even when a client has not yet synchronized every built-in Group into its local
        // Master cache. Prebuilt Tally Groups appear first in Tally hierarchy order; any
        // additional live/custom Groups follow alphabetically. Duplicate spelling variants
        // are collapsed through the same accounting duplicate key used by Master validation.
        BulkLedgerGroupNames.Clear();
        var seenBulkGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in MasterSynchronizationService.TallyPrebuiltLedgerGroupNames)
        {
            var key = AccountingText.DuplicateKey(name);
            if (key.Length == 0 || !seenBulkGroups.Add(key)) continue;
            BulkLedgerGroupNames.Add(name);
        }
        foreach (var name in MasterGroupNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var key = AccountingText.DuplicateKey(name);
            if (key.Length == 0 || !seenBulkGroups.Add(key)) continue;
            BulkLedgerGroupNames.Add(name);
        }
    }

    private void ApplyBulkMappingFilters()
    {
        // Retained for keyboard/backward command compatibility. The Bulk Mapping
        // filters are live now, so this simply performs the same non-destructive
        // reconciliation immediately. No Apply Filter button is required.
        RefreshBulkMappingFiltersLive(force: true);
    }

    private void RefreshBulkMappingFiltersLive(bool force = false)
    {
        if (_suppressBulkMappingLiveFilterRefresh || !IsBulkLedgerMappingOpen) return;
        if (IsBusy && !force) return;

        try
        {
            RebuildBulkMappingFilteredScope();
            StatusText = _bulkSelectedAtomicMappings.Count > 0
                ? $"Live Bulk Mapping filter: {BulkMappingFilterSummary}."
                : $"Live Bulk Mapping filter has no matching selected transactions: {BulkMappingFilterSummary}.";
        }
        catch (Exception exception)
        {
            // Live typing must never show a modal validation popup for every partial
            // keystroke. More importantly, stale pre-filter membership must not remain
            // mappable while an Amount expression is incomplete or invalid.
            _bulkSelectedAtomicMappings.Clear();
            BulkVoucherTypeMappingRows.Clear();
            BulkMappingSelectionType = "Filter incomplete";
            BulkMappingIsSimple = !_bulkMappingUseVoucherTypeGrid;
            BulkMappingFilterSummary = BuildBulkMappingLiveInputSummary();
            BulkMappingCaption = exception.Message + " The filter will update automatically as you type.";
            StatusText = BulkMappingCaption;
            OnPropertyChanged(nameof(BulkMappingTransactionCount));
            ApplyBulkSelectedMappingsCommand.RaiseCanExecuteChanged();
            RemoveBulkSelectedFinalLedgersCommand.RaiseCanExecuteChanged();
            SaveManualCashContraCommand.RaiseCanExecuteChanged();
        }
    }

    private string BuildBulkMappingLiveInputSummary()
    {
        var bank = NormalizeBulkBankFilter(BulkMappingBankFilter);
        var voucherType = NormalizeBulkVoucherTypeScope(BulkMappingVoucherTypeFilter);
        var operatorName = NormalizeBulkAmountOperator(BulkMappingAmountOperator);
        var amountText = operatorName switch
        {
            "Exact" => $"= {BulkMappingAmountFromText.Trim()}",
            "Greater Than" => $"> {BulkMappingAmountFromText.Trim()}",
            "Less Than" => $"< {BulkMappingAmountFromText.Trim()}",
            "Between" => $"{BulkMappingAmountFromText.Trim()} to {BulkMappingAmountToText.Trim()}",
            _ => "Any"
        };
        var expression = BulkMappingAmountSearchText.Trim();
        if (expression.Length > 0) amountText = expression;
        return $"Bank: {bank} · Voucher Type: {(voucherType.Length == 0 ? "All" : voucherType)} · Amount: {amountText}";
    }

    private void ClearBulkMappingFilters()
    {
        if (!IsBulkLedgerMappingOpen) return;
        _suppressBulkMappingLiveFilterRefresh = true;
        try
        {
            BulkMappingBankFilter = "All Banks";
            BulkMappingVoucherTypeFilter = "All";
            BulkMappingAmountOperator = "Any";
            BulkMappingAmountFromText = string.Empty;
            BulkMappingAmountToText = string.Empty;
            BulkMappingAmountSearchText = string.Empty;
            BulkMappingShowMixedVoucherType = false;
        }
        finally
        {
            _suppressBulkMappingLiveFilterRefresh = false;
        }
        RebuildBulkMappingFilteredScope();
        StatusText = "Bulk Mapping Bank, Voucher Type and Amount live filters cleared.";
    }

    private void RebuildBulkMappingFilteredScope()
    {
        // Preserve what the user has typed while the live filter changes. Filtering is a
        // view operation inside the existing mapping panel, not a destructive rebuild of
        // the user's Payment/Receipt mapping drafts.
        if (_bulkMappingUseVoucherTypeGrid)
        {
            foreach (var row in BulkVoucherTypeMappingRows)
            {
                var type = NormalizeBulkVoucherType(row.VoucherType);
                if (type.Length == 0) continue;
                _bulkVoucherDraftsByType[type] = (row.FinalLedger ?? string.Empty, row.LedgerGroup ?? string.Empty);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(BulkMappingFinalLedger)) _bulkSimpleLedgerDraft = BulkMappingFinalLedger;
            if (!string.IsNullOrWhiteSpace(BulkMappingLedgerGroup)) _bulkSimpleGroupDraft = BulkMappingLedgerGroup;
        }

        var bankFilter = NormalizeBulkBankFilter(BulkMappingBankFilter);
        var voucherType = NormalizeBulkVoucherTypeScope(BulkMappingVoucherTypeFilter);
        var amountOperator = NormalizeBulkAmountOperator(BulkMappingAmountOperator);
        decimal? amountValue = null;
        decimal? amountTo = null;
        if (!amountOperator.Equals("Any", StringComparison.OrdinalIgnoreCase))
        {
            amountValue = ParseRequiredBulkAmount(BulkMappingAmountFromText, BulkMappingAmountIsBetween ? "Amount From" : "Amount");
            if (amountOperator.Equals("Between", StringComparison.OrdinalIgnoreCase))
            {
                amountTo = ParseRequiredBulkAmount(BulkMappingAmountToText, "Amount To");
                if (amountValue.Value > amountTo.Value)
                    throw new InvalidOperationException("Bulk Mapping Amount From cannot be greater than Amount To.");
            }
        }
        var amountSearchExpression = BulkMappingAmountSearchText.Trim();
        _bulkMappingAmountFilterActive = !amountOperator.Equals("Any", StringComparison.OrdinalIgnoreCase) || amountSearchExpression.Length > 0;

        var bankScopedMappings = bankFilter.Equals("All Banks", StringComparison.OrdinalIgnoreCase)
            ? _bulkSelectedBaseMappings
            : _bulkSelectedBaseMappings
                .Where(mapping => CurrentBulkBanksForMapping(mapping).Contains(bankFilter))
                .ToList();
        var fragments = BuildBulkVoucherTypeFragments(bankScopedMappings, voucherType).ToList();
        if (_bulkAppliedTransactionIdsThisSession.Count > 0)
        {
            var remainingFragments = new List<BankLedgerMapping>();
            foreach (var mapping in fragments)
            {
                if (mapping.TransactionIds.Count == 0)
                {
                    // Legacy mapping without exact membership cannot be subtracted safely.
                    remainingFragments.Add(mapping);
                    continue;
                }

                var remainingIds = mapping.TransactionIds
                    .Where(id => id > 0 && !_bulkAppliedTransactionIdsThisSession.Contains(id))
                    .Distinct()
                    .ToArray();
                if (remainingIds.Length == 0) continue;
                remainingFragments.Add(CloneBulkVoucherTypeFragment(mapping, mapping.VoucherType, remainingIds));
            }
            fragments = remainingFragments;
        }
        if (_bulkMappingAmountFilterActive)
        {
            var requiredIds = fragments.SelectMany(mapping => mapping.TransactionIds).Where(id => id > 0).Distinct().ToHashSet();
            var transactions = Transactions.Where(transaction => requiredIds.Contains(transaction.Id)).ToDictionary(transaction => transaction.Id);
            if (transactions.Count < requiredIds.Count)
            {
                foreach (var transaction in _repository.GetTransactions(CurrentClientId, CurrentFinancialYear, false))
                    if (requiredIds.Contains(transaction.Id) && !transactions.ContainsKey(transaction.Id)) transactions[transaction.Id] = transaction;
            }

            var amountFiltered = new List<BankLedgerMapping>();
            foreach (var mapping in fragments)
            {
                // Amount filtering is exact-transaction scoped. Payment uses Debit and
                // Receipt uses Credit, matching the Bank Statement voucher direction;
                // it must not use max(Debit,Credit), which can hide side/data errors.
                if (mapping.TransactionIds.Count == 0) continue;
                var ids = mapping.TransactionIds
                    .Where(id => transactions.TryGetValue(id, out _))
                    .Where(id => BulkAmountExpressionMatches(
                        GetBulkTransactionAmount(transactions[id], mapping.VoucherType),
                        amountSearchExpression, amountOperator, amountValue, amountTo))
                    .Distinct()
                    .ToList();
                if (ids.Count > 0) amountFiltered.Add(CloneBulkVoucherTypeFragment(mapping, mapping.VoucherType, ids));
            }
            fragments = amountFiltered;
        }

        _bulkSelectedAtomicMappings.Clear();
        foreach (var fragment in fragments) _bulkSelectedAtomicMappings.Add(fragment);
        BulkVoucherTypeMappingRows.Clear();

        var normalizedTypes = _bulkSelectedAtomicMappings
            .Select(mapping => NormalizeBulkVoucherType(mapping.VoucherType))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(BulkVoucherTypeSortOrder)
            .ToArray();
        // Fix131: panel shape is frozen when Bulk Mapping opens. Live filters may hide one
        // direction but must never replace the Payment/Receipt grid with the simple panel.
        var simpleType = !_bulkMappingUseVoucherTypeGrid;
        BulkMappingIsSimple = simpleType;
        BulkMappingSelectionType = normalizedTypes.Length == 1 ? normalizedTypes[0] : normalizedTypes.Length == 0 ? "No matching transactions" : string.Join(" / ", normalizedTypes);

        var ledgers = _bulkSelectedAtomicMappings.Select(mapping => mapping.LedgerName.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var groups = _bulkSelectedAtomicMappings.Select(mapping => mapping.MasterGroup.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        BulkMappingFinalLedger = simpleType
            ? (_bulkSimpleLedgerDraft.Length > 0 ? _bulkSimpleLedgerDraft : ledgers.Length == 1 ? ledgers[0] : string.Empty)
            : string.Empty;
        BulkMappingLedgerGroup = simpleType
            ? (_bulkSimpleGroupDraft.Length > 0 ? _bulkSimpleGroupDraft : groups.Length == 1 ? groups[0] : string.Empty)
            : string.Empty;

        if (!simpleType)
        {
            foreach (var typeGroup in _bulkSelectedAtomicMappings
                         .GroupBy(mapping => NormalizeBulkVoucherType(mapping.VoucherType), StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Key.Equals("Payment", StringComparison.OrdinalIgnoreCase) || group.Key.Equals("Receipt", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(group => BulkVoucherTypeSortOrder(group.Key)))
            {
                var mappings = typeGroup.ToArray();
                var typeLedgers = mappings.Select(mapping => mapping.LedgerName.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var typeGroups = mappings.Select(mapping => mapping.MasterGroup.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var draft = _bulkVoucherDraftsByType.GetValueOrDefault(typeGroup.Key);
                var typeRow = new BulkVoucherTypeMappingRow
                {
                    VoucherType = typeGroup.Key,
                    MappingCount = mappings.Length,
                    TransactionCount = mappings.SelectMany(mapping => mapping.TransactionIds).Where(id => id > 0).Distinct().Count(),
                    Mappings = mappings,
                    FinalLedger = !string.IsNullOrWhiteSpace(draft.FinalLedger) ? draft.FinalLedger : typeLedgers.Length == 1 ? typeLedgers[0] : string.Empty,
                    LedgerGroup = !string.IsNullOrWhiteSpace(draft.LedgerGroup) ? draft.LedgerGroup : typeGroups.Length == 1 ? typeGroups[0] : string.Empty
                };
                StageBulkVoucherTypeMappingRow(typeRow);
                BulkVoucherTypeMappingRows.Add(typeRow);
            }
        }

        var transactionCount = _bulkSelectedAtomicMappings.SelectMany(mapping => mapping.TransactionIds).Where(id => id > 0).Distinct().Count();
        var amountText = amountSearchExpression.Length > 0
            ? amountSearchExpression
            : amountOperator switch
            {
                "Exact" when amountValue.HasValue => $"= {amountValue.Value:N2}",
                "Greater Than" when amountValue.HasValue => $"> {amountValue.Value:N2}",
                "Less Than" when amountValue.HasValue => $"< {amountValue.Value:N2}",
                "Between" when amountValue.HasValue && amountTo.HasValue => $"{amountValue.Value:N2} to {amountTo.Value:N2}",
                _ => "Any"
            };
        BulkMappingFilterSummary = $"Bank: {bankFilter} · Voucher Type: {(voucherType.Length == 0 ? "All" : voucherType)} · Amount: {amountText}";
        BulkMappingCaption = transactionCount == 0
            ? $"0 selected transactions match the current filter. {BulkMappingFilterSummary}."
            : simpleType
                ? $"{transactionCount:N0} filtered {BulkMappingSelectionType} transaction(s). Choose one Ledger/Group. {BulkMappingFilterSummary}."
                : $"{transactionCount:N0} filtered Bank transaction(s). Map Payment and Receipt once each below. {BulkMappingFilterSummary}.";

        OnPropertyChanged(nameof(BulkMappingTransactionCount));
        ApplyBulkSelectedMappingsCommand.RaiseCanExecuteChanged();
    }

    private static decimal ParseRequiredBulkAmount(string? text, string label)
    {
        var value=(text??string.Empty).Trim();
        if(value.Length==0) throw new InvalidOperationException($"Enter {label} for Bulk Mapping.");
        if(decimal.TryParse(value,NumberStyles.Number|NumberStyles.AllowCurrencySymbol,CultureInfo.CurrentCulture,out var parsed) ||
           decimal.TryParse(value,NumberStyles.Number|NumberStyles.AllowCurrencySymbol,CultureInfo.InvariantCulture,out parsed))
        {
            if(parsed<0)throw new InvalidOperationException($"{label} cannot be negative.");
            return parsed;
        }
        throw new InvalidOperationException($"Enter a valid numeric {label} for Bulk Mapping.");
    }

    private static decimal GetBulkTransactionAmount(BankTransactionRecord transaction, string voucherType)
    {
        if (voucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase)) return Math.Abs(transaction.Debit);
        if (voucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase)) return Math.Abs(transaction.Credit);
        return transaction.Mode.Equals("Payment", StringComparison.OrdinalIgnoreCase)
            ? Math.Abs(transaction.Debit)
            : transaction.Mode.Equals("Receipt", StringComparison.OrdinalIgnoreCase)
                ? Math.Abs(transaction.Credit)
                : Math.Max(Math.Abs(transaction.Debit), Math.Abs(transaction.Credit));
    }

    private static bool BulkAmountExpressionMatches(decimal amount, string expression, string fallbackOperator, decimal? fallbackValue, decimal? fallbackTo)
    {
        var text = (expression ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            if (fallbackOperator.Equals("Any", StringComparison.OrdinalIgnoreCase)) return true;
            return fallbackValue.HasValue && BulkAmountMatches(amount, fallbackOperator, fallbackValue.Value, fallbackTo);
        }
        foreach (var raw in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var term = raw.Replace("₹", string.Empty).Replace(",", string.Empty).Trim();
            string op; string valueText;
            if (term.StartsWith(">=", StringComparison.Ordinal)) { op = ">="; valueText = term[2..]; }
            else if (term.StartsWith("<=", StringComparison.Ordinal)) { op = "<="; valueText = term[2..]; }
            else if (term.StartsWith(">", StringComparison.Ordinal)) { op = ">"; valueText = term[1..]; }
            else if (term.StartsWith("<", StringComparison.Ordinal)) { op = "<"; valueText = term[1..]; }
            else if (term.StartsWith("=", StringComparison.Ordinal)) { op = "="; valueText = term[1..]; }
            else { op = "="; valueText = term; }
            if (!decimal.TryParse(valueText.Trim(), NumberStyles.Number | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var expected) &&
                !decimal.TryParse(valueText.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out expected)) return false;
            var ok = op switch { ">=" => amount >= expected, "<=" => amount <= expected, ">" => amount > expected, "<" => amount < expected, _ => amount == expected };
            if (!ok) return false;
        }
        return true;
    }

    private static bool BulkAmountMatches(decimal amount, string amountOperator, decimal value, decimal? valueTo) => amountOperator switch
    {
        "Exact" => amount == value,
        "Greater Than" => amount > value,
        "Less Than" => amount < value,
        "Between" => valueTo.HasValue && amount >= value && amount <= valueTo.Value,
        _ => true
    };

    private static string NormalizeBulkAmountOperator(string? value)
    {
        if (string.Equals(value, "Exact", StringComparison.OrdinalIgnoreCase)) return "Exact";
        if (string.Equals(value, "Greater Than", StringComparison.OrdinalIgnoreCase)) return "Greater Than";
        if (string.Equals(value, "Less Than", StringComparison.OrdinalIgnoreCase)) return "Less Than";
        if (string.Equals(value, "Between", StringComparison.OrdinalIgnoreCase)) return "Between";
        return "Any";
    }

    private IReadOnlyList<BankLedgerMapping> BuildBulkVoucherTypeFragments(
        IReadOnlyCollection<BankLedgerMapping> mappings,
        string visibleVoucherType)
    {
        var requiredIds = mappings.SelectMany(mapping => mapping.TransactionIds).Where(id => id > 0).Distinct().ToHashSet();
        var modeById = Transactions
            .Where(transaction => requiredIds.Contains(transaction.Id))
            .ToDictionary(transaction => transaction.Id, transaction => NormalizeBulkVoucherType(transaction.Mode));

        if (modeById.Count < requiredIds.Count)
        {
            foreach (var transaction in _repository.GetTransactions(CurrentClientId, CurrentFinancialYear, false))
            {
                if (requiredIds.Contains(transaction.Id) && !modeById.ContainsKey(transaction.Id))
                    modeById[transaction.Id] = NormalizeBulkVoucherType(transaction.Mode);
            }
        }

        var fragments = new List<BankLedgerMapping>();
        foreach (var mapping in mappings)
        {
            if (mapping.TransactionIds.Count > 0)
            {
                foreach (var voucherType in new[] { "Payment", "Receipt" })
                {
                    if (visibleVoucherType.Length > 0 && !voucherType.Equals(visibleVoucherType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var transactionIds = mapping.TransactionIds
                        .Where(id => id > 0 && modeById.TryGetValue(id, out var mode) && mode.Equals(voucherType, StringComparison.OrdinalIgnoreCase))
                        .Distinct()
                        .ToList();
                    if (transactionIds.Count == 0) continue;

                    fragments.Add(CloneBulkVoucherTypeFragment(mapping, voucherType, transactionIds));
                }
                continue;
            }

            // Legacy mappings without frozen members can participate only when their
            // persisted Voucher Type is already explicitly Payment or Receipt. A
            // legacy Mixed mapping cannot be safely split without transaction IDs.
            var legacyType = NormalizeBulkVoucherType(mapping.VoucherType);
            if (legacyType.Length == 0 ||
                (visibleVoucherType.Length > 0 && !legacyType.Equals(visibleVoucherType, StringComparison.OrdinalIgnoreCase)))
                continue;
            fragments.Add(CloneBulkVoucherTypeFragment(mapping, legacyType, []));
        }

        return fragments;
    }

    private static BankLedgerMapping CloneBulkVoucherTypeFragment(
        BankLedgerMapping source,
        string voucherType,
        IReadOnlyCollection<long> transactionIds)
    {
        var count = transactionIds.Count > 0
            ? transactionIds.Count
            : voucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(source.PaymentMatchCount, source.VoucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase) ? source.MatchCount : 0)
                : voucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(source.ReceiptMatchCount, source.VoucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase) ? source.MatchCount : 0)
                    : Math.Max(source.MatchCount, source.PaymentMatchCount + source.ReceiptMatchCount);

        return new BankLedgerMapping
        {
            ClientId = source.ClientId,
            FinancialYear = source.FinancialYear,
            BankFilter = source.BankFilter,
            VoucherType = voucherType,
            SummaryName = source.SummaryName,
            NarrationPattern = source.NarrationPattern,
            SourceLedgerName = source.SourceLedgerName,
            LedgerName = source.LedgerName,
            MasterGroup = source.MasterGroup,
            MatchCount = count,
            PaymentMatchCount = voucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase) ? count : 0,
            ReceiptMatchCount = voucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase) ? count : 0,
            Status = source.Status,
            TransactionIds = transactionIds.Where(id => id > 0).Distinct().ToList(),
            UpdatedUtc = source.UpdatedUtc
        };
    }

    private static string NormalizeBulkBankFilter(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ||
               normalized.Equals("All", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("All Banks", StringComparison.OrdinalIgnoreCase)
            ? "All Banks"
            : normalized;
    }

    private static string NormalizeBulkVoucherTypeScope(string? value)
    {
        if (string.Equals(value, "Payment", StringComparison.OrdinalIgnoreCase)) return "Payment";
        if (string.Equals(value, "Receipt", StringComparison.OrdinalIgnoreCase)) return "Receipt";
        return string.Empty;
    }

    private static string NormalizeBulkVoucherType(string? value)
    {
        if (string.Equals(value, "Payment", StringComparison.OrdinalIgnoreCase)) return "Payment";
        if (string.Equals(value, "Receipt", StringComparison.OrdinalIgnoreCase)) return "Receipt";
        if (string.Equals(value, "Mixed", StringComparison.OrdinalIgnoreCase)) return "Mixed";
        return string.Empty;
    }

    private static int BulkVoucherTypeSortOrder(string value) =>
        value.Equals("Payment", StringComparison.OrdinalIgnoreCase) ? 0 :
        value.Equals("Receipt", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    private static string BulkMappingPersistenceKey(BankLedgerMapping mapping) =>
        string.Join("\u001f",
            mapping.ClientId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            mapping.FinancialYear.Trim(),
            mapping.BankFilter.Trim().ToUpperInvariant(),
            mapping.VoucherType.Trim().ToUpperInvariant(),
            mapping.NarrationPattern.Trim().ToUpperInvariant(),
            mapping.SourceLedgerName.Trim().ToUpperInvariant());

    private static bool IsLedgerMappingWorkbenchRowMapped(LedgerMappingWorkbenchRow row) =>
        row.Status.StartsWith("Mapped", StringComparison.OrdinalIgnoreCase);

    public void StageBulkVoucherTypeMappingRow(BulkVoucherTypeMappingRow? row)
    {
        if (row is null) return;

        row.FinalLedger = AccountingText.DisplayName(row.FinalLedger);
        row.LedgerGroup = AccountingText.DisplayName(row.LedgerGroup);
        if (row.FinalLedger.Length == 0)
        {
            row.LedgerGroup = string.Empty;
            row.Status = "Pending";
            return;
        }

        var ledgerMatches = ResolveMasterLedgerMatches(row.FinalLedger);
        if (ledgerMatches.Count > 1)
        {
            row.LedgerGroup = string.Empty;
            row.Status = "Multiple Master Matches";
            return;
        }

        if (ledgerMatches.Count == 1)
        {
            var master = ledgerMatches[0];
            row.FinalLedger = master.Name.Trim();
            row.LedgerGroup = master.Parent?.Trim() ?? string.Empty;
            row.Status = "Ready";
            return;
        }

        if (row.LedgerGroup.Length == 0)
        {
            // Fix131 accounting-direction default applies only to a genuinely NEW suggested
            // Bulk Ledger. Existing Masters keep their actual parent above.
            if (row.VoucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase))
                row.LedgerGroup = "Sundry Creditors";
            else if (row.VoucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase))
                row.LedgerGroup = "Sundry Debtors";
            else
            {
                row.Status = "Select Ledger Group";
                return;
            }
        }

        // Fix110 R1: standard Tally parent groups are canonical local authority.
        // Accept them before inspecting duplicate local Group rows; duplicate copies of
        // a standard Group must not make a valid parent such as Indirect Expenses invalid.
        if (MasterSynchronizationService.TryResolveTallyPrebuiltLedgerGroup(row.LedgerGroup, out var prebuiltGroup))
        {
            row.LedgerGroup = prebuiltGroup;
            row.Status = "Ready - New Ledger";
            return;
        }

        var groupKey = AccountingText.DuplicateKey(row.LedgerGroup);
        var groupMatches = _activeMasterRecords
            .Where(item => item.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && !item.IsArchived)
            .Where(item => AccountingText.DuplicateKey(item.Name) == groupKey)
            .GroupBy(item => item.PersistentId)
            .Select(group => group.First())
            .Take(2)
            .ToList();
        if (groupMatches.Count > 1)
        {
            row.Status = "Invalid Ledger Group";
            return;
        }
        if (groupMatches.Count == 1)
        {
            row.LedgerGroup = groupMatches[0].Name.Trim();
            row.Status = "Ready - New Ledger";
            return;
        }

        row.Status = "Invalid Ledger Group";
    }

    private void CloseBulkSelectedMapping()
    {
        IsBulkLedgerMappingOpen = false;
        BulkLedgerMappingRows.Clear();
        BulkVoucherTypeMappingRows.Clear();
        BulkMappingBankOptions.Clear();
        _bulkSelectedBaseMappings.Clear();
        _bulkSelectedAtomicMappings.Clear();
        _bulkVoucherDraftsByType.Clear();
        _bulkAppliedTransactionIdsThisSession.Clear();
        _bulkMappingUseVoucherTypeGrid = false;
        _bulkSimpleLedgerDraft = string.Empty;
        _bulkSimpleGroupDraft = string.Empty;
        _suppressBulkMappingLiveFilterRefresh = true;
        try
        {
            BulkMappingBankFilter = "All Banks";
            BulkMappingVoucherTypeFilter = "All";
            BulkMappingAmountOperator = "Any";
            BulkMappingAmountFromText = string.Empty;
            BulkMappingAmountToText = string.Empty;
        }
        finally
        {
            _suppressBulkMappingLiveFilterRefresh = false;
        }
        BulkMappingFilterSummary = "Bank: All Banks · Voucher Type: All · Amount: Any";
        _bulkMappingAmountFilterActive = false;
        BulkMappingFinalLedger = string.Empty;
        BulkMappingLedgerGroup = string.Empty;
        BulkMappingSelectionType = string.Empty;
        BulkMappingCaption = string.Empty;
        BulkMappingIsSimple = false;
        OnPropertyChanged(nameof(BulkMappingTransactionCount));
    }

    private async Task RemoveBulkSelectedFinalLedgersAsync()
    {
        if (!DemandGovernancePermission(GovernancePermissions.LedgerMappingEdit, "bulk remove Final Ledger")) return;
        if (CurrentClientId <= 0 || !IsBulkLedgerMappingOpen || IsBusy) return;

        var transactionIds = _bulkSelectedAtomicMappings
            .SelectMany(mapping => mapping.TransactionIds)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (transactionIds.Length == 0)
        {
            StatusText = "The current Bulk Mapping scope has no exact transaction membership. Rebuild Mapping once and try Remove Final Ledger again.";
            MessageBox.Show(StatusText, "Bulk Ledger Mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var requestedIds = transactionIds.ToHashSet();
        var transactionLedgerById = Transactions
            .Where(item => requestedIds.Contains(item.Id))
            .ToDictionary(item => item.Id, item => item.LedgerName ?? string.Empty);
        if (transactionLedgerById.Count < requestedIds.Count)
        {
            foreach (var item in _repository.GetTransactions(CurrentClientId, CurrentFinancialYear, true))
                if (requestedIds.Contains(item.Id) && !transactionLedgerById.ContainsKey(item.Id))
                    transactionLedgerById[item.Id] = item.LedgerName ?? string.Empty;
        }
        var mappedTransactionIds = transactionLedgerById
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => item.Key)
            .Distinct()
            .ToArray();
        var mappedCount = mappedTransactionIds.Length;
        if (mappedCount == 0)
        {
            StatusText = "The current Bulk Mapping scope does not contain any Final Ledger values to remove.";
            return;
        }

        var scopeText = BulkMappingHasActiveFilter ? "current filtered selected scope" : "checked selected scope";
        if (MessageBox.Show(
                $"Remove Final Ledger from {mappedCount:N0} mapped Bank Statement transaction(s) in the {scopeText}?\n\n" +
                "Transactions are retained. Ledger Group is cleared with Final Ledger. Genuine/manual/Tally Masters are never deleted; only an unused Ledger Master created by Bank Ledger Mapping can be archived.",
                "Remove Final Ledger - Bulk Selected",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var frozenIds = _bulkSelectedBaseMappings
            .SelectMany(mapping => mapping.TransactionIds)
            .Where(id => id > 0)
            .Distinct()
            .ToHashSet();
        if (frozenIds.Count == 0) frozenIds.UnionWith(transactionIds);
        var affectedVoucherTypes = _bulkSelectedAtomicMappings
            .Select(mapping => NormalizeBulkVoucherType(mapping.VoucherType))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        IsBusy = true;
        try
        {
            var result = await Task.Run(() => _mappingService.ClearMany(CurrentClientId, CurrentFinancialYear, mappedTransactionIds));

            var clearedIds = mappedTransactionIds.ToHashSet();
            foreach (var transaction in Transactions.Where(item => clearedIds.Contains(item.Id)))
            {
                transaction.LedgerName = string.Empty;
                transaction.MappingStatus = "Mapping Required";
            }

            var bankFilter = LedgerMappingBankFilter;
            var showMapped = LedgerMappingShowMapped;
            var voucherFilter = LedgerMappingVoucherFilter;
            var rebuiltMappings = await Task.Run(() => _mappingService.Rebuild(CurrentClientId, CurrentFinancialYear));
            LedgerMappings.Clear();
            foreach (var mapping in rebuiltMappings) LedgerMappings.Add(mapping);
            BuildLedgerMappingRows(bankFilter, showMapped, voucherFilter);
            ApplyBankStatementFilters();

            // Keep the popup on the same frozen checkbox membership. The database rebuild may
            // regroup rows after Final Ledger is removed, so reconstruct only the original
            // frozen transaction IDs rather than widening the popup to newly similar rows.
            _bulkSelectedBaseMappings.Clear();
            foreach (var mapping in rebuiltMappings)
            {
                var scopedIds = mapping.TransactionIds.Where(id => id > 0 && frozenIds.Contains(id)).Distinct().ToArray();
                if (scopedIds.Length == 0) continue;
                _bulkSelectedBaseMappings.Add(CloneBulkVoucherTypeFragment(mapping, mapping.VoucherType, scopedIds));
            }

            _bulkSimpleLedgerDraft = string.Empty;
            _bulkSimpleGroupDraft = string.Empty;
            foreach (var voucherType in affectedVoucherTypes) _bulkVoucherDraftsByType.Remove(voucherType);
            BulkMappingFinalLedger = string.Empty;
            BulkMappingLedgerGroup = string.Empty;
            RebuildBulkMappingFilteredScope();

            QueueWorkspaceCacheRefresh();
            NotifyCounts();
            StatusText = $"Removed Final Ledger from {result.ClearedTransactions:N0} Bank Statement transaction(s) in the {scopeText}." +
                         (result.ArchivedMasters > 0 ? $" {result.ArchivedMasters:N0} unused auto-created Ledger Master(s) were archived." : string.Empty);
            _liveChangeHub.Publish(CurrentClientId, CurrentFinancialYear, AccountingLiveArea.Banking, "Bulk selected Final Ledger removed");
            if (result.ArchivedMasters > 0)
                _liveChangeHub.Publish(CurrentClientId, CurrentFinancialYear, AccountingLiveArea.Masters, "Unused Bank Ledger Mapping Masters archived");
        }
        catch (Exception exception)
        {
            StatusText = "Bulk Final Ledger removal failed: " + exception.Message;
            MessageBox.Show(StatusText, "Bulk Ledger Mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            ApplyBulkSelectedMappingsCommand.RaiseCanExecuteChanged();
            RemoveBulkSelectedFinalLedgersCommand.RaiseCanExecuteChanged();
            SaveManualCashContraCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task ApplyBulkSelectedMappingsAsync()
    {
        if (!DemandGovernancePermission(GovernancePermissions.LedgerMappingEdit, "apply bulk Ledger Mapping changes")) return;
        if (CurrentClientId <= 0 || !IsBulkLedgerMappingOpen || _bulkSelectedAtomicMappings.Count == 0 || IsBusy) return;

        try
        {
            var atomicMappings = new List<BankLedgerMapping>();
            var resolvedByTransactionId = new Dictionary<long, string>();

            if (BulkMappingIsSimple)
            {
                var validationRow = new BulkVoucherTypeMappingRow
                {
                    VoucherType = BulkMappingSelectionType,
                    MappingCount = _bulkSelectedAtomicMappings.Count,
                    TransactionCount = _bulkSelectedAtomicMappings.SelectMany(mapping => mapping.TransactionIds).Where(id => id > 0).Distinct().Count(),
                    Mappings = _bulkSelectedAtomicMappings.ToArray(),
                    FinalLedger = BulkMappingFinalLedger,
                    LedgerGroup = BulkMappingLedgerGroup
                };
                StageBulkVoucherTypeMappingRow(validationRow);
                ValidateBulkVoucherTypeMappingRow(validationRow);
                BulkMappingFinalLedger = validationRow.FinalLedger;
                BulkMappingLedgerGroup = validationRow.LedgerGroup;

                foreach (var mapping in _bulkSelectedAtomicMappings)
                {
                    mapping.LedgerName = validationRow.FinalLedger;
                    mapping.MasterGroup = validationRow.LedgerGroup;
                    atomicMappings.Add(mapping);
                    foreach (var id in mapping.TransactionIds.Where(id => id > 0))
                        resolvedByTransactionId[id] = validationRow.FinalLedger;
                }
            }
            else
            {
                if (BulkVoucherTypeMappingRows.Count == 0)
                    throw new InvalidOperationException("No Voucher Type mappings are available for the selected rows.");

                foreach (var typeRow in BulkVoucherTypeMappingRows)
                {
                    var finalLedger = (typeRow.FinalLedger ?? string.Empty).Trim();
                    var ledgerGroup = (typeRow.LedgerGroup ?? string.Empty).Trim();

                    // Payment and Receipt are independent actions. A completely blank side
                    // means "do not map this voucher type"; it must not block a fully filled
                    // opposite side. A partially filled side is still validated and rejected
                    // so an accidental half-mapping can never be committed.
                    if (finalLedger.Length == 0 && ledgerGroup.Length == 0) continue;

                    StageBulkVoucherTypeMappingRow(typeRow);
                    ValidateBulkVoucherTypeMappingRow(typeRow);
                    foreach (var mapping in typeRow.Mappings)
                    {
                        mapping.LedgerName = finalLedger;
                        mapping.MasterGroup = ledgerGroup;
                        atomicMappings.Add(mapping);
                        foreach (var id in mapping.TransactionIds.Where(id => id > 0))
                            resolvedByTransactionId[id] = finalLedger;
                    }
                }
            }

            if (atomicMappings.Count == 0)
                throw new InvalidOperationException(_bulkMappingUseVoucherTypeGrid
                    ? "Fill at least one Payment or Receipt Final Ledger/Group before mapping. A blank voucher side is skipped."
                    : "No selected Voucher Type mappings are available to apply.");

            IsBusy = true;
            var uniqueMappings = atomicMappings
                .GroupBy(BulkMappingPersistenceKey, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var first = group.First();
                    first.TransactionIds = group.SelectMany(item => item.TransactionIds).Where(id => id > 0).Distinct().ToList();
                    first.MatchCount = first.TransactionIds.Count > 0 ? first.TransactionIds.Count : group.Sum(item => Math.Max(0, item.MatchCount));
                    first.PaymentMatchCount = first.VoucherType.Equals("Payment", StringComparison.OrdinalIgnoreCase) ? first.MatchCount : 0;
                    first.ReceiptMatchCount = first.VoucherType.Equals("Receipt", StringComparison.OrdinalIgnoreCase) ? first.MatchCount : 0;
                    return first;
                })
                .ToArray();
            // Fix131: Bulk Mapping is always an exact frozen-transaction operation, not a
            // summary-rule rewrite. This keeps main Map All and Bulk Map All identical.
            var affected = await Task.Run(() => _mappingService.ApplyMany(uniqueMappings, CreateLedgerWhenApplying, exactTransactionScope: true));

            var mappedIds = resolvedByTransactionId.Keys.ToHashSet();
            foreach (var transaction in Transactions.Where(item => mappedIds.Contains(item.Id)))
            {
                if (resolvedByTransactionId.TryGetValue(transaction.Id, out var ledger)) transaction.LedgerName = ledger;
                transaction.MappingStatus = "Mapped";
            }

            var bankFilter = LedgerMappingBankFilter;
            var showMapped = LedgerMappingShowMapped;
            var voucherFilter = LedgerMappingVoucherFilter;
            _ledgerMappingDraftCommitIds.Clear();
            foreach (var id in mappedIds) _ledgerMappingDraftCommitIds.Add(id);
            try
            {
                var rebuiltMappings = await Task.Run(() => _mappingService.Rebuild(CurrentClientId, CurrentFinancialYear));
                LedgerMappings.ReplaceRange(rebuiltMappings);
                BuildLedgerMappingRows(bankFilter, showMapped, voucherFilter);
            }
            finally
            {
                _ledgerMappingDraftCommitIds.Clear();
            }
            ApplyBankStatementFilters();
            QueueWorkspaceCacheRefresh();
            NotifyCounts();

            foreach (var id in mappedIds) _bulkAppliedTransactionIdsThisSession.Add(id);
            RebuildBulkMappingFilteredScope();

            StatusText = $"Bulk Mapping applied {uniqueMappings.Length:N0} selected Voucher Type mapping record(s) to {affected:N0} Bank Statement transaction(s). The Bulk Mapping window remains open; mapped transactions are removed from this session scope and any blank Payment/Receipt side remains available.";
            _liveChangeHub.Publish(CurrentClientId, CurrentFinancialYear, AccountingLiveArea.Banking, "Ledger Mapping selected Voucher Type batch applied");
            if (CreateLedgerWhenApplying)
                _liveChangeHub.Publish(CurrentClientId, CurrentFinancialYear, AccountingLiveArea.Masters, "Ledger Mapping selected batch created/resolved Masters");
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
            MessageBox.Show(exception.Message, "Bulk Ledger Mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            ApplyBulkSelectedMappingsCommand.RaiseCanExecuteChanged();
            RemoveBulkSelectedFinalLedgersCommand.RaiseCanExecuteChanged();
            SaveManualCashContraCommand.RaiseCanExecuteChanged();
        }
    }

    private static void ValidateBulkVoucherTypeMappingRow(BulkVoucherTypeMappingRow row)
    {
        if (string.IsNullOrWhiteSpace(row.FinalLedger))
            throw new InvalidOperationException($"{row.VoucherType} requires a Final Ledger.");
        if (row.Status.Equals("Multiple Master Matches", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{row.VoucherType} has multiple Master matches. Resolve it before applying the selected batch.");
        if (row.Status.Equals("Invalid Ledger Group", StringComparison.OrdinalIgnoreCase) ||
            row.Status.Equals("Select Ledger Group", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(row.LedgerGroup))
            throw new InvalidOperationException($"{row.VoucherType} requires a valid Ledger Group before applying the selected batch.");
    }

    private void PutSuggestedNames()
    {
        if (!DemandGovernancePermission(GovernancePermissions.LedgerMappingEdit, "change Ledger Mapping suggestions")) return;
        var candidates = FilteredLedgerMappingRows
            .Where(row => row.IsSelected)
            .Where(row => string.IsNullOrWhiteSpace(row.ExactMatch))
            .Where(row => !string.IsNullOrWhiteSpace(row.SuggestedMatch))
            .Where(row => row.Type.Equals("Payment", StringComparison.OrdinalIgnoreCase) || row.Type.Equals("Receipt", StringComparison.OrdinalIgnoreCase))
            .Where(row => !IsLedgerMappingWorkbenchRowMapped(row))
            .Where(row => string.IsNullOrWhiteSpace(row.FinalLedger))
            .ToArray();

        if (candidates.Length == 0)
        {
            StatusText = LedgerMappingSelectedActionCount == 0
                ? "Select at least one Ledger Mapping row before using Put Suggested Name."
                : "No selected pending rows without an Existing Ledger Match are available for Put Suggested Name.";
            return;
        }

        foreach (var row in candidates)
        {
            row.FinalLedger = AccountingText.DisplayName(row.SuggestedMatch);
            row.LedgerGroup = row.Type.Equals("Payment", StringComparison.OrdinalIgnoreCase)
                ? "Sundry Creditors"
                : "Sundry Debtors";
            StageLedgerMappingRow(row);
        }

        // Per requested workflow, every visible action checkbox returns to selected
        // after the bulk suggestion operation. The selection layer is reusable by
        // future actions and is not coupled to DataGrid cell selection.
        SetLedgerMappingActionSelection(true);
        NotifyLedgerMappingProgressVisuals();
        StatusText = $"Put Suggested Name staged {candidates.Length:N0} selected pending mapping row(s): Payment → Sundry Creditors, Receipt → Sundry Debtors. Mixed rows require Payment/Receipt review in Bulk Mapping. Existing Ledger Match rows were not changed.";
    }

    public void StageLedgerMappingRow(LedgerMappingWorkbenchRow? row)
    {
        if (row is null) return;
        try
        {
            row.FinalLedger = AccountingText.DisplayName(row.FinalLedger);
            row.LedgerGroup = AccountingText.DisplayName(row.LedgerGroup);

            if (row.FinalLedger.Length == 0)
            {
                row.LedgerGroup = string.Empty;
                row.Status = "Pending";
                return;
            }

            var ledgerMatches = ResolveMasterLedgerMatches(row.FinalLedger);
            if (ledgerMatches.Count > 1)
            {
                row.LedgerGroup = string.Empty;
                row.Status = "Multiple Master Matches";
                return;
            }

            if (ledgerMatches.Count == 1)
            {
                var master = ledgerMatches[0];
                row.FinalLedger = master.Name.Trim();
                row.LedgerGroup = master.Parent?.Trim() ?? string.Empty;
                row.Status = "Ready";
                return;
            }

            // Current workbook: a genuinely new ledger cannot be Ready until the
            // Master Group is explicitly selected, and a typed group must itself
            // resolve to one valid Master Group.
            if (row.LedgerGroup.Length == 0)
            {
                row.Status = "Select Ledger Group";
                return;
            }

            // Fix110 R1: standard Tally parent groups are canonical local authority.
            // Resolve them before duplicate local Group records so a valid built-in
            // parent such as Indirect Expenses stays usable while the client is unlinked.
            if (MasterSynchronizationService.TryResolveTallyPrebuiltLedgerGroup(row.LedgerGroup, out var prebuiltGroup))
            {
                row.LedgerGroup = prebuiltGroup;
                row.Status = "Ready - New Ledger";
                return;
            }

            var groupKey = AccountingText.DuplicateKey(row.LedgerGroup);
            var groupMatches = _activeMasterRecords
                .Where(item => item.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && !item.IsArchived)
                .Where(item => AccountingText.DuplicateKey(item.Name) == groupKey)
                .GroupBy(item => item.PersistentId)
                .Select(group => group.First())
                .Take(2)
                .ToList();
            if (groupMatches.Count == 1)
            {
                row.LedgerGroup = groupMatches[0].Name.Trim();
                row.Status = "Ready - New Ledger";
                return;
            }
            if (groupMatches.Count > 1)
            {
                row.Status = "Invalid Ledger Group";
                return;
            }

            row.Status = "Invalid Ledger Group";
        }
        finally
        {
            NotifyLedgerMappingProgressVisuals();
        }
    }

    private void NotifyLedgerMappingProgressVisuals()
    {
        OnPropertyChanged(nameof(ReadyMappingCount));
        OnPropertyChanged(nameof(ReadyOrMappedMappingCount));
        OnPropertyChanged(nameof(MappingReadyPercent));
        OnPropertyChanged(nameof(MappingCompletionPercent));
    }

    private IReadOnlyList<MasterRecord> ResolveMasterLedgerMatches(string ledgerName)
    {
        var key = BankNarrationParser.NormalizeLedgerExactMatchKey(ledgerName);
        if (key.Length == 0) return [];

        return LedgerMappingMasterResolver.DistinctByMasterAuthority(_activeMasterRecords
            .Where(item => string.Equals(item.Type, "Ledger", StringComparison.OrdinalIgnoreCase) && !item.IsArchived)
            .Where(item => string.Equals(
                BankNarrationParser.NormalizeLedgerExactMatchKey(item.Name),
                key,
                StringComparison.OrdinalIgnoreCase)));
    }

    public IReadOnlyList<MasterRecord> GetLedgerMappingActiveMasters() =>
        LedgerMappingMasterResolver.DistinctByMasterAuthority(_activeMasterRecords
            .Where(item => !item.IsArchived && item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.TallyMasterId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private bool TryResolveMasterLedger(string ledgerName, out string actualLedger, out string ledgerGroup)
    {
        actualLedger = string.Empty;
        ledgerGroup = string.Empty;
        var matches = ResolveMasterLedgerMatches(ledgerName);
        if (matches.Count != 1) return false;
        actualLedger = matches[0].Name.Trim();
        ledgerGroup = matches[0].Parent?.Trim() ?? string.Empty;
        return actualLedger.Length > 0;
    }

    public void ClearFinalLedger(LedgerMappingWorkbenchRow? row)
    {
        if (row is null) return;
        try
        {
            // Fix131: remember rejection of an automatic Existing/Suggested match before
            // clearing the mapping so Rebuild cannot immediately put the same name back.
            var rejectedLedger = !string.IsNullOrWhiteSpace(row.ExactMatch)
                ? row.ExactMatch
                : !string.IsNullOrWhiteSpace(row.SuggestedMatch) &&
                  BankNarrationParser.NormalizeLedgerExactMatchKey(row.FinalLedger) == BankNarrationParser.NormalizeLedgerExactMatchKey(row.SuggestedMatch)
                    ? row.SuggestedMatch
                    : string.Empty;
            if (!string.IsNullOrWhiteSpace(rejectedLedger))
            {
                _mappingService.RecordSuggestionRejection(
                    CurrentClientId, CurrentFinancialYear,
                    BankNarrationParser.NormalizeLedgerExactMatchKey(row.SummaryName),
                    rejectedLedger, row.LedgerGroup,
                    "User cleared automatic Ledger Mapping match");
            }

            var archivedMaster = false;
            foreach (var mapping in row.MemberMappings.Count > 0 ? row.MemberMappings : new[] { row.Mapping })
                archivedMaster |= _mappingService.Clear(mapping);

            var clearedIds = (row.MemberMappings.Count > 0 ? row.MemberMappings : new[] { row.Mapping })
                .SelectMany(item => item.TransactionIds).ToHashSet();
            row.FinalLedger = string.Empty;
            row.LedgerGroup = string.Empty;
            row.Status = "Pending";
            NotifyLedgerMappingProgressVisuals();
            foreach (var transaction in Transactions.Where(item => clearedIds.Contains(item.Id)))
            {
                transaction.LedgerName = string.Empty;
                transaction.MappingStatus = "Mapping Required";
            }
            ApplyBankStatementFilters();
            QueueWorkspaceCacheRefresh();
            StatusText = archivedMaster
                ? $"Final Ledger cleared for {row.SummaryName}. Its unused auto-created Ledger Master was removed from the active Master list."
                : $"Final Ledger cleared for {row.SummaryName}. The Ledger Mapping row was retained.";
            _liveChangeHub.Publish(CurrentClientId, CurrentFinancialYear, AccountingLiveArea.Banking, "Ledger Mapping cleared");
            if (archivedMaster)
                _liveChangeHub.Publish(CurrentClientId, CurrentFinancialYear, AccountingLiveArea.Masters, "Unused Bank Ledger Mapping Master archived");
        }
        catch (Exception exception)
        {
            StatusText = $"Final Ledger could not be cleared: {exception.Message}";
        }
    }

    private void ClearSelectedFinalLedger()
    {
        if (!DemandGovernancePermission(GovernancePermissions.LedgerMappingEdit, "clear a Ledger Mapping")) return;
        ClearFinalLedger(SelectedLedgerMappingRow);
    }

    private void LoadSelectedBankAccountWorkspace()
    {
        OnPropertyChanged(nameof(SelectedBankCurrentName));
        OnPropertyChanged(nameof(HasSelectedBankAccount));

        var account = SelectedAccount;
        if (account is null)
        {
            SelectedBankMasterRecord = null;
            return;
        }

        var renameRow = BankRenameRows.FirstOrDefault(item => item.BankAccountId == account.Id);
        if (!ReferenceEquals(_selectedBankRename, renameRow))
        {
            _selectedBankRename = renameRow;
            OnPropertyChanged(nameof(SelectedBankRename));
            ApplyBankRenameCommand.RaiseCanExecuteChanged();
        }

        if (_selectedBankRenameOption is not null)
        {
            _selectedBankRenameOption = null;
            OnPropertyChanged(nameof(SelectedBankRenameOption));
        }

        // Fix77: reopen a still-bound Bank Account by MASTERID. A changed Ledger name is
        // detached during Save/export and then resolves through its independent local Master.
        var master = account.TallyLedgerMasterId.Trim().Length > 0
            ? _activeMasterRecords.FirstOrDefault(item =>
                string.Equals(item.Type, "Ledger", StringComparison.OrdinalIgnoreCase) &&
                item.TallyMasterId.Trim().Equals(account.TallyLedgerMasterId.Trim(), StringComparison.OrdinalIgnoreCase))
            : null;
        master ??= _activeMasterRecords.FirstOrDefault(item =>
                string.Equals(item.Type, "Ledger", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Name, account.AuthorityName, StringComparison.OrdinalIgnoreCase))
            ?? _masterRepository.Get(account.ClientId, account.FinancialYear, true, string.Empty)
                .FirstOrDefault(item =>
                    string.Equals(item.Type, "Ledger", StringComparison.OrdinalIgnoreCase) &&
                    (account.TallyLedgerMasterId.Trim().Length > 0
                        ? item.TallyMasterId.Trim().Equals(account.TallyLedgerMasterId.Trim(), StringComparison.OrdinalIgnoreCase)
                        : string.Equals(item.Name, account.AuthorityName, StringComparison.OrdinalIgnoreCase)));
        SelectedBankMasterRecord = master ?? CreateBankMasterRecord(account);
    }

    private static MasterRecord CreateBankMasterRecord(BankAccountRecord account) =>
        new()
        {
            ClientId = account.ClientId,
            FinancialYear = account.FinancialYear,
            Name = account.AuthorityName,
            Parent = "Bank Accounts",
            Type = "Ledger",
            Source = "Bank Statement",
            IsUserEdited = true
        };

    private void ProjectResolvedBankBalancesToUi(IReadOnlyList<BankTransactionRecord> authoritativeTransactions)
    {
        var reconciliationByStatement = ReconciliationRows
            .GroupBy(item => item.StatementId)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var accountGroup in Statements.GroupBy(item => item.BankAccountId))
        {
            BankStatementRecord? previousComparable = null;
            decimal? previousComparableClosing = null;
            foreach (var statement in accountGroup
                         .OrderBy(item => BankBalanceAuthority.ResolveStatementTransactionDateRange(item, authoritativeTransactions)?.From ?? item.PeriodFrom ?? item.ImportedUtc)
                         .ThenBy(item => BankBalanceAuthority.ResolveStatementTransactionDateRange(item, authoritativeTransactions)?.To ?? item.PeriodTo ?? item.ImportedUtc)
                         .ThenBy(item => item.Id))
            {
                var statementRows = BankBalanceAuthority.GetStatementSourceTransactions(statement, authoritativeTransactions);
                var movementRows = BankBalanceAuthority.GetStatementPeriodTransactions(statement, authoritativeTransactions);
                var partialOrDelta = BankBalanceAuthority.IsPartialOrDeltaStatement(statement);
                var balanceChainExcluded = BankBalanceAuthority.IsBalanceChainExcludedStatement(statement);
                // CR3 UI projection must use the same Bank Account + date movement authority as
                // reconciliation/reports. Source-file rows remain evidence/provenance only.
                var chainClosing = !balanceChainExcluded && previousComparable is not null &&
                                   BankBalanceAuthority.CanCarryBalanceForward(previousComparable, statement, authoritativeTransactions)
                    ? previousComparableClosing
                    : null;
                var opening = BankBalanceAuthority.ResolveOpeningBalance(statement, statementRows, chainClosing, movementRows);
                var closing = BankBalanceAuthority.ResolveClosingBalance(statement, statementRows, opening.Value, movementRows);
                var difference = closing.IndependentValue.HasValue && closing.CalculatedValue.HasValue
                    ? Math.Round(closing.IndependentValue.Value - closing.CalculatedValue.Value, 2, MidpointRounding.AwayFromZero)
                    : (decimal?)null;

                var trustedOpening = BankBalanceAuthority.IsTrustedOpeningEvidence(opening);
                var closingNeedsReview = !closing.IndependentValue.HasValue ||
                                         closing.Source.Equals("CALCULATED", StringComparison.OrdinalIgnoreCase) ||
                                         closing.Source.Equals("LEGACY UNVERIFIED", StringComparison.OrdinalIgnoreCase);
                statement.TotalDebit = movementRows.Sum(item => item.Debit);
                statement.TotalCredit = movementRows.Sum(item => item.Credit);
                statement.TransactionCount = movementRows.Count;
                statement.OpeningBalance = opening.Value;
                statement.OpeningBalanceSource = opening.Source;
                statement.ClosingBalance = closing.Value;
                statement.ClosingBalanceSource = closing.Source;
                statement.Difference = difference ?? 0m;
                if (!partialOrDelta)
                {
                    statement.ReconciliationStatus = !trustedOpening || !closing.Value.HasValue || closingNeedsReview
                        ? "Balance Review Required"
                        : difference.HasValue && difference.Value != 0m
                            ? "Difference"
                            : "Reconciled";
                }

                if (reconciliationByStatement.TryGetValue(statement.Id, out var summary))
                {
                    summary.OpeningBalance = opening.Value;
                    summary.ExpectedClosingBalance = closing.CalculatedValue;
                    summary.StatementClosingBalance = closing.Value;
                    summary.Difference = difference;
                    summary.Status = !trustedOpening || !closing.Value.HasValue || closingNeedsReview
                        ? closing.Source.Equals("CALCULATED", StringComparison.OrdinalIgnoreCase)
                            ? "Missing Balance Evidence / Calculated Closing"
                            : "Missing Balance Evidence"
                        : difference.HasValue && difference.Value != 0m
                            ? "Difference"
                            : partialOrDelta ? statement.ReconciliationStatus : "Reconciled";
                }

                if (!balanceChainExcluded)
                {
                    previousComparable = statement;
                    previousComparableClosing = closing.Value;
                }
            }
        }
    }

    private string BuildChainStatus()
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            MissingPeriodBankGroups.Clear();
            _missingStatementPeriodCount = 0;
            _bankAccountPeriodWarningText = string.Empty;
            OnPropertyChanged(nameof(MissingStatementPeriodCount));
            OnPropertyChanged(nameof(HasBankAccountPeriodWarnings));
            OnPropertyChanged(nameof(BankAccountPeriodWarningText));
            OnPropertyChanged(nameof(BankAccountPeriodWarningSummaryText));
            OnPropertyChanged(nameof(BankAccountPeriodWarningPanelText));
            return string.Empty;
        }

        var (fyFrom, fyTo) = TallyLiveConnectionCoordinator.FinancialYearDates(client.FinancialYear);
        var today = AppTime.LocalNow.Date;
        var coverageTo = today < fyFrom.Date ? fyFrom.Date : today < fyTo.Date ? today : fyTo.Date;
        var accountNames = Accounts.ToDictionary(item => item.Id, item => item.DisplayName);
        var issues = new List<string>();
        var missingPeriods = new List<string>();
        MissingPeriodBankGroups.Clear();

        foreach (var account in Accounts)
        {
            var accountStatements = Statements
                .Where(item => item.BankAccountId == account.Id)
                .ToArray();
            var snapshot = BankBalanceAuthority.BuildAccountSnapshot(
                account.Id,
                accountStatements,
                Transactions,
                fyFrom.Date,
                coverageTo);
            account.OpeningBalance = snapshot.OpeningBalance;
            account.ClosingBalance = snapshot.ClosingBalance;
            account.BalanceStatus = snapshot.Status;
            account.MissingPeriodText = snapshot.MissingPeriodText;

            if (snapshot.MissingPeriods.Count > 0)
            {
                MissingPeriodBankGroups.Add(new BankMissingPeriodGroupRow(
                    account.DisplayName,
                    snapshot.MissingPeriods.Select(range => range.Label).ToArray()));
            }
            foreach (var range in snapshot.MissingPeriods)
            {
                var text = $"{account.DisplayName}: missing statement period {range.Label}.";
                missingPeriods.Add(text);
                issues.Add(text);
            }
            foreach (var range in snapshot.OverlapPeriods)
                issues.Add($"{account.DisplayName}: statement overlap {range.Label}.");

            if (accountStatements.Length > 0 && snapshot.Status.Contains("Missing Balance Evidence", StringComparison.OrdinalIgnoreCase))
                issues.Add($"{account.DisplayName}: missing opening/closing balance evidence; reprocess the source statement or verify the balance manually.");

            if (snapshot.Difference.HasValue && snapshot.Difference.Value != 0m && snapshot.MissingPeriods.Count == 0)
                issues.Add($"{account.DisplayName}: whole-account closing mismatch — calculated {BankBusinessBalance.Format(snapshot.CalculatedClosingBalance)} vs latest statement {BankBusinessBalance.Format(snapshot.ClosingBalance)} (difference {snapshot.Difference.Value:N2}).");

            var ordered = accountStatements
                .Where(item => !BankBalanceAuthority.IsBalanceChainExcludedStatement(item))
                .OrderBy(item => BankBalanceAuthority.ResolveStatementTransactionDateRange(item, Transactions)?.From ?? item.PeriodFrom ?? item.ImportedUtc)
                .ThenBy(item => BankBalanceAuthority.ResolveStatementTransactionDateRange(item, Transactions)?.To ?? item.PeriodTo ?? item.ImportedUtc)
                .ThenBy(item => item.Id)
                .ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                if (!BankBalanceAuthority.ArePeriodsContiguous(previous, current, Transactions) ||
                    !previous.ClosingBalance.HasValue || !current.OpeningBalance.HasValue) continue;
                var difference = Math.Round(current.OpeningBalance.Value - previous.ClosingBalance.Value, 2, MidpointRounding.AwayFromZero);
                if (difference != 0m)
                    issues.Add($"{account.DisplayName}: chronological balance mismatch — previous closing {BankBusinessBalance.Format(previous.ClosingBalance)} vs next opening {BankBusinessBalance.Format(current.OpeningBalance)} (difference {difference:N2}).");
            }
        }

        _missingStatementPeriodCount = missingPeriods.Count;
        _bankAccountPeriodWarningText = missingPeriods.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, missingPeriods);
        OnPropertyChanged(nameof(MissingStatementPeriodCount));
        OnPropertyChanged(nameof(HasBankAccountPeriodWarnings));
        OnPropertyChanged(nameof(BankAccountPeriodWarningText));
        OnPropertyChanged(nameof(BankAccountPeriodWarningSummaryText));
        OnPropertyChanged(nameof(BankAccountPeriodWarningPanelText));

        if (issues.Count > 0) return string.Join(Environment.NewLine, issues.Distinct(StringComparer.OrdinalIgnoreCase));
        return Statements.Count > 0
            ? "Statement chain: original transaction-date balance anchors and whole-account totals are continuous."
            : "Statement chain: no statements imported.";
    }

    private static void MergeImportResult(BankStatementImportResult destination, BankStatementImportResult source, string retriedFile)
    {
        destination.FilesImported += source.FilesImported;
        destination.FilesRetried += source.FilesRetried;
        destination.DuplicateStatementsSkipped += source.DuplicateStatementsSkipped;
        destination.TransactionsImported += source.TransactionsImported;
        destination.TransactionsMatchedBeforeImport += source.TransactionsMatchedBeforeImport;
        if (!string.IsNullOrWhiteSpace(source.PreImportTallyStatus)) destination.PreImportTallyStatus = source.PreImportTallyStatus;
        destination.DuplicateTransactionsSkipped += source.DuplicateTransactionsSkipped;
        destination.RowsOutsideFinancialYearSkipped += source.RowsOutsideFinancialYearSkipped;
        destination.AccountsCreated += source.AccountsCreated;
        destination.PeriodFrom = !destination.PeriodFrom.HasValue ? source.PeriodFrom : !source.PeriodFrom.HasValue ? destination.PeriodFrom : destination.PeriodFrom.Value <= source.PeriodFrom.Value ? destination.PeriodFrom : source.PeriodFrom;
        destination.PeriodTo = !destination.PeriodTo.HasValue ? source.PeriodTo : !source.PeriodTo.HasValue ? destination.PeriodTo : destination.PeriodTo.Value >= source.PeriodTo.Value ? destination.PeriodTo : source.PeriodTo;
        if (source.FilesImported > 0) destination.Issues.RemoveAll(issue => issue.SourceFile.Equals(retriedFile, StringComparison.OrdinalIgnoreCase));
        destination.Issues.AddRange(source.Issues);
    }

    private void QueueWorkspaceCacheRefresh()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        _requiresDataRefresh = true;
        _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Banking);
        _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.LedgerMapping);
        _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Cash);
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(BankStatementVisibleCount));
        OnPropertyChanged(nameof(TransactionCount));
        OnPropertyChanged(nameof(AccountCount));
        OnPropertyChanged(nameof(MissingStatementPeriodCount));
        OnPropertyChanged(nameof(HasBankAccountPeriodWarnings));
        OnPropertyChanged(nameof(BankAccountPeriodWarningText));
        OnPropertyChanged(nameof(BankAccountPeriodWarningSummaryText));
        OnPropertyChanged(nameof(BankAccountPeriodWarningPanelText));
        OnPropertyChanged(nameof(PendingMappingCount));
        OnPropertyChanged(nameof(CompletedMappingCount));
        OnPropertyChanged(nameof(MappingTransactionCount));
        OnPropertyChanged(nameof(ReadyMappingCount));
        OnPropertyChanged(nameof(ReadyOrMappedMappingCount));
        OnPropertyChanged(nameof(MappingReadyPercent));
        OnPropertyChanged(nameof(MappingCompletionPercent));
        OnPropertyChanged(nameof(ReconciliationIssueCount));
        OnPropertyChanged(nameof(CashEntryCount));
        OnPropertyChanged(nameof(CashReadyCount));
        OnPropertyChanged(nameof(CashPaymentCount));
        OnPropertyChanged(nameof(CashReceiptCount));
        OnPropertyChanged(nameof(CashJournalCount));
        OnPropertyChanged(nameof(CashContraCount));
        OnPropertyChanged(nameof(CashAttentionCount));
        OnPropertyChanged(nameof(CashOpeningBalance));
        OnPropertyChanged(nameof(CashReceiptTotal));
        OnPropertyChanged(nameof(CashPaymentTotal));
        OnPropertyChanged(nameof(CashClosingBalance));
        OnPropertyChanged(nameof(IsCashClosingNegative));
        OnPropertyChanged(nameof(CashDraftCount));
        OnPropertyChanged(nameof(CashApprovedCount));
        OnPropertyChanged(nameof(CashRecurringCount));
        OnPropertyChanged(nameof(CashReversedCount));
        NotifyEmptyLedgerCounts();
        PutSuggestedNamesCommand.RaiseCanExecuteChanged();
        CreateEmptyLedgerManualMasterCommand.RaiseCanExecuteChanged();
        UseEmptyLedgerMasterCommand.RaiseCanExecuteChanged();
        OpenBankStatementCommand.RaiseCanExecuteChanged();
        OpenBankAccountsCommand.RaiseCanExecuteChanged();
        OpenLedgerMappingCommand.RaiseCanExecuteChanged();
    }

    private void OnTallyExportSelectionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LedgerMappingDateScopeText));
        OnPropertyChanged(nameof(BankStatementDateScopeText));
        NotifyEmptyLedgerCounts();

        if (BankStatementDateScope.Equals("Tally Export Dates", StringComparison.OrdinalIgnoreCase))
            ApplyBankStatementFilters();

        if (!LedgerMappingDateScope.Equals("Tally Export Dates", StringComparison.OrdinalIgnoreCase) ||
            CurrentClientId <= 0)
        {
            return;
        }

        var bankFilter = LedgerMappingBankFilter;
        var showMapped = LedgerMappingShowMapped;
        var voucherFilter = LedgerMappingVoucherFilter;
        CloseLedgerMappingPreview();
        BuildLedgerMappingRows(bankFilter, showMapped, voucherFilter);
        StatusText = $"Tally export date filter refreshed • {LedgerMappingVisibleCount:N0} visible summary row(s).";
    }

    private void OnClientContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveClientContext.CurrentClient)) return;
        // Application startup preloads every client into ClientWorkspaceStateCache.
        // Client switching therefore only binds the prepared snapshot; it does not
        // launch a new full SQLite scan on the UI thread.
        ImportIssues.Clear();
        _loadedClientId = 0;
        _loadedFinancialYear = string.Empty;
        _requiresDataRefresh = true;
        _ledgerMappingsLoaded = false;
        _activeMasterRecords = [];
        _ledgerMappingExistingMatchRefreshFingerprint = string.Empty;
        _ledgerMappingExistingMatchRefreshDeferred = false;
        _ledgerMappingCoverageRepairDeferred = false;
        _ledgerMappingProjectionUpgradeDeferred = false;
        _ledgerMappingProjectionUpgradeRunning = false;
        _ledgerMappingProjectionUpgradeRequired = false;
        _ledgerMappingPreferencesClientId = 0;
        _ledgerMappingPreferencesFinancialYear = string.Empty;
        var client = _clientContext.CurrentClient;
        var cacheReady = client is not null &&
                         _workspaceStateCache.TryGet(client.Id, client.FinancialYear, out _);
        if (client is not null && !cacheReady)
            _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Banking);
        StatusText = client is null
            ? "Open a client to use Banking."
            : cacheReady
                ? "Banking ready from client cache."
                : "Banking cache is preparing in the background.";
        ImportBankStatementsCommand.RaiseCanExecuteChanged();
        ImportPdfStatementsCommand.RaiseCanExecuteChanged();
        ExportSuspenseCommand.RaiseCanExecuteChanged();
        ImportSuspenseCommand.RaiseCanExecuteChanged();
    }

    private static DateTime FinancialYearStart(string financialYear)
    {
        var first = financialYear.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, out var year) ? new DateTime(year, 4, 1) : DateTime.Today;
    }
}

public sealed record BankMissingPeriodGroupRow(
    string BankName,
    IReadOnlyList<string> Periods);
