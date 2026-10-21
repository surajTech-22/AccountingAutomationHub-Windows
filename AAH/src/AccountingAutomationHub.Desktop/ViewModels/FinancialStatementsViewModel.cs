using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.ComponentModel;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Infrastructure.Tally;
using AccountingAutomationHub.Application.Settings;
using AccountingAutomationHub.Domain.Accounting;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Accounting;
using AccountingAutomationHub.Infrastructure.Automation;
using AccountingAutomationHub.Infrastructure.Masters;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class FinancialStatementsViewModel : PageViewModel
{
    private readonly ActiveClientContext _clientContext;
    private readonly SqliteMasterRepository _masterRepository;
    private readonly SqliteStockMasterRepository _stockRepository;
    private readonly LedgerTotalService _ledgerTotals;
    private readonly ApplicationPreferenceService _preferences;
    private readonly SqliteAutomationRepository _automationRepository;
    private readonly ClientWorkspaceStateCache? _workspaceStateCache;
    private string _statusText = "Financial Statements ready.";
    private string _selectedMainTab = "Basic Info";
    private string _selectedNote = "All Notes";
    private CaProfileRow? _selectedCaProfile;
    private FinancialStatementBasicInfo _basicInfo = new();
    private bool _isNotesPreparationVisible;
    private bool _hideZeroClosingBalance = true;
    private bool _showUnmappedOnly;
    private FinancialStatementLedgerRow? _selectedFyTrialBalanceRow;
    private FinancialStatementStockRow? _selectedFyStockRow;
    private FinancialStatementLedgerRow? _selectedFyDebtorRow;
    private FinancialStatementLedgerRow? _selectedFyCreditorRow;
    private string _sameTallyGroupScope = "FY only";
    private bool _financialStatementsEnabled;
    private string _previousYearDataMode = "Actual Previous Year Data";
    private IReadOnlyList<FinancialStatementMappingOption> _mappingCatalog = [];
    private CancellationTokenSource? _nceTallyRefreshCancellation;
    private long _nceTallyRefreshGeneration;
    private TallyLiveConnectionCoordinator? _tallyCoordinator;
    private string _preparationStatusText = "Financial Statements preparation waiting for data.";
    private bool _preparationRefreshQueued;
    private bool _updatingPreparedState;

    // R14 prepared Financial Statements cache. The durable preference snapshot remains the
    // cross-restart authority; this memory layer makes repeated Client/FY/page navigation paint
    // immediately without reparsing large JSON/NCE payloads. Callers only read these snapshots.
    private static readonly object PreparedSnapshotGate = new();
    private static readonly Dictionary<string, FinancialStatementDataSnapshot> PreparedDataSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, NceFinancialStatementFeedSnapshot> PreparedNceSnapshots = new(StringComparer.OrdinalIgnoreCase);

    public FinancialStatementsViewModel(
        ActiveClientContext clientContext,
        SqliteMasterRepository masterRepository,
        SqliteStockMasterRepository stockRepository,
        LedgerTotalService ledgerTotals,
        ApplicationPreferenceService preferences,
        ClientWorkspaceStateCache? workspaceStateCache = null)
        : base("Financial Statements", "Preparation, mapping, notes and complete financial-statement exports")
    {
        _clientContext = clientContext;
        _masterRepository = masterRepository;
        _stockRepository = stockRepository;
        _ledgerTotals = ledgerTotals;
        _preferences = preferences;
        _automationRepository = new SqliteAutomationRepository();
        _automationRepository.Initialize();
        _workspaceStateCache = workspaceStateCache;
        MainTabs = ["Basic Info", "TB", "Stock Summary", "Debtors", "Creditors"];
        Notes = new ObservableCollection<string>();
        NotePreviewRows = [];
        Signatories = [];
        OwnerPartners = [];
        ShareCapitalDetails = [];
        FyTrialBalance = [];
        PyTrialBalance = [];
        FyStock = [];
        PyStock = [];
        FyDebtors = [];
        PyDebtors = [];
        FyCreditors = [];
        PyCreditors = [];
        CaProfiles = [];
        NceTypes = new ObservableCollection<string>(FinancialStatementWorkbookRules.NceEntityTypes);
        RoundingOptions = new ObservableCollection<string>(FinancialStatementWorkbookRules.RoundOffChoices);
        PpeBlockOptions = new ObservableCollection<string>(FinancialStatementWorkbookRules.PpeBlockChoices);
        DebtorCreditorDetailOptions = new ObservableCollection<string>(FinancialStatementWorkbookRules.DebtorCreditorDetailChoices);
        StockDetailOptions = new ObservableCollection<string>(FinancialStatementWorkbookRules.StockDetailChoices);
        FsHeadOptions = [];
        NoteHeadOptions = [];
        NoteSubHeadOptions = [];
        SameTallyGroupScopes = ["FY only", "FY + PY"];
        PreviousYearDataModes = ["Actual Previous Year Data", "No Previous Year / First Year", "Use Current FY Opening Balances as PY"];
        FyTrialBalanceView = CollectionViewSource.GetDefaultView(FyTrialBalance);
        PyTrialBalanceView = CollectionViewSource.GetDefaultView(PyTrialBalance);
        FyStockView = CollectionViewSource.GetDefaultView(FyStock);
        PyStockView = CollectionViewSource.GetDefaultView(PyStock);
        FyDebtorsView = CollectionViewSource.GetDefaultView(FyDebtors);
        PyDebtorsView = CollectionViewSource.GetDefaultView(PyDebtors);
        FyCreditorsView = CollectionViewSource.GetDefaultView(FyCreditors);
        PyCreditorsView = CollectionViewSource.GetDefaultView(PyCreditors);
        FyTrialBalanceView.Filter = FilterTrialBalanceRow;
        PyTrialBalanceView.Filter = FilterTrialBalanceRow;
        FyDebtorsView.Filter = FilterScheduleRow;
        PyDebtorsView.Filter = FilterScheduleRow;
        FyCreditorsView.Filter = FilterScheduleRow;
        PyCreditorsView.Filter = FilterScheduleRow;
        FyStockView.Filter = FilterStockRow;
        PyStockView.Filter = FilterStockRow;
        TrackPreparationChanges(FyTrialBalance);
        TrackPreparationChanges(PyTrialBalance);
        TrackPreparationChanges(FyStock);
        TrackPreparationChanges(PyStock);
        TrackPreparationChanges(FyDebtors);
        TrackPreparationChanges(PyDebtors);
        TrackPreparationChanges(FyCreditors);
        TrackPreparationChanges(PyCreditors);
        ExportWithLinksCommand = new AsyncRelayCommand(() => ExportWorkbookAsync(withLinks: true));
        ExportWithoutLinksCommand = new AsyncRelayCommand(() => ExportWorkbookAsync(withLinks: false));
        ExportPdfCommand = new AsyncRelayCommand(ExportPdfAsync);
        NotesPreparationCommand = new RelayCommand(() =>
        {
            IsNotesPreparationVisible = !IsNotesPreparationVisible;
            RefreshNotePreview();
            StatusText = IsNotesPreparationVisible ? $"Notes Preparation · {SelectedNote}" : "Notes Preparation closed.";
        });
        SaveMappingsCommand = new RelayCommand(SaveMappings);
        ApplyMappingToSameTallyGroupCommand = new RelayCommand(ApplyMappingToSameTallyGroup);
        CopyFyToPyMappingCommand = new RelayCommand(() => CopyCrossYearMappings(copyFyToPy: true));
        CopyPyToFyMappingCommand = new RelayCommand(() => CopyCrossYearMappings(copyFyToPy: false));
        ApplyStockMappingToSameGroupCommand = new RelayCommand(ApplyStockMappingToSameGroup);
        CopyStockFyToPyMappingCommand = new RelayCommand(() => CopyStockCrossYearMappings(copyFyToPy: true));
        CopyStockPyToFyMappingCommand = new RelayCommand(() => CopyStockCrossYearMappings(copyFyToPy: false));
        ApplyDebtorScheduleToSameGroupCommand = new RelayCommand(() => ApplyScheduleMappingToSameGroup(SelectedFyDebtorRow, FyDebtors, PyDebtors, "Debtors"));
        CopyDebtorFyToPyMappingCommand = new RelayCommand(() => CopyScheduleCrossYearMappings(FyDebtors, PyDebtors, "Debtors", "FY → PY"));
        CopyDebtorPyToFyMappingCommand = new RelayCommand(() => CopyScheduleCrossYearMappings(PyDebtors, FyDebtors, "Debtors", "PY → FY"));
        ApplyCreditorScheduleToSameGroupCommand = new RelayCommand(() => ApplyScheduleMappingToSameGroup(SelectedFyCreditorRow, FyCreditors, PyCreditors, "Creditors"));
        CopyCreditorFyToPyMappingCommand = new RelayCommand(() => CopyScheduleCrossYearMappings(FyCreditors, PyCreditors, "Creditors", "FY → PY"));
        CopyCreditorPyToFyMappingCommand = new RelayCommand(() => CopyScheduleCrossYearMappings(PyCreditors, FyCreditors, "Creditors", "PY → FY"));
        ConnectPreviousYearTallyCommand = new AsyncRelayCommand(ConnectPreviousYearTallyAsync);
        UseActualPreviousYearCommand = new RelayCommand(() => PreviousYearDataMode = "Actual Previous Year Data");
        UseFyOpeningAsPreviousYearCommand = new RelayCommand(() => PreviousYearDataMode = "Use Current FY Opening Balances as PY");
        SaveBasicInfoCommand = new RelayCommand(SaveBasicInfo);
        RefreshCommand = new RelayCommand(Refresh);
        _clientContext.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActiveClientContext.CurrentClient))
                Refresh();
        };
        Refresh();
    }

    public ObservableCollection<string> MainTabs { get; }
    public ObservableCollection<string> Notes { get; }
    public ObservableCollection<FinancialStatementNotePreviewRow> NotePreviewRows { get; }
    public ObservableCollection<EntitySignatoryRow> Signatories { get; }
    public ObservableCollection<OwnerPartnerRow> OwnerPartners { get; }
    public ObservableCollection<ShareCapitalDetailRow> ShareCapitalDetails { get; }
    public ObservableCollection<FinancialStatementLedgerRow> FyTrialBalance { get; }
    public ObservableCollection<FinancialStatementLedgerRow> PyTrialBalance { get; }
    public ObservableCollection<FinancialStatementStockRow> FyStock { get; }
    public ObservableCollection<FinancialStatementStockRow> PyStock { get; }
    public ObservableCollection<FinancialStatementLedgerRow> FyDebtors { get; }
    public ObservableCollection<FinancialStatementLedgerRow> PyDebtors { get; }
    public ObservableCollection<FinancialStatementLedgerRow> FyCreditors { get; }
    public ObservableCollection<FinancialStatementLedgerRow> PyCreditors { get; }
    public ObservableCollection<CaProfileRow> CaProfiles { get; }
    public ObservableCollection<string> NceTypes { get; }
    public ObservableCollection<string> RoundingOptions { get; }
    public ObservableCollection<string> PpeBlockOptions { get; }
    public string PpeBlockSelection
    {
        get => string.IsNullOrWhiteSpace(BasicInfo.PpeBlock) ? "Gross Block" : BasicInfo.PpeBlock;
        set
        {
            var requested = PpeBlockOptions.FirstOrDefault(x => x.Equals(value ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                ?? "Gross Block";
            var changed = !BasicInfo.PpeBlock.Equals(requested, StringComparison.OrdinalIgnoreCase);
            if (!changed)
            {
                OnPropertyChanged(nameof(PpeBlockSelection));
                return;
            }

            BasicInfo.PpeBlock = requested;
            BasicInfo.PpeBlockSelectionOrigin = "User";
            OnPropertyChanged(nameof(PpeBlockSelection));

            // R52 FIXED2: changing the presentation selector may re-evaluate only generic
            // automatic PPE mappings. Fresh Tally-NCE authority, copied mappings and explicit
            // user mappings are source evidence and must never be rewritten by this selector.
            RecalculateAutomaticPpeMappingsForSelection(requested);
            RebuildTemplatePresentation();
            RefreshNotePreview();
            UpdatePreparedFinancialStatementState();
        }
    }
    public ObservableCollection<string> DebtorCreditorDetailOptions { get; }
    public ObservableCollection<string> StockDetailOptions { get; }
    public ObservableCollection<string> FsHeadOptions { get; }
    public ObservableCollection<string> NoteHeadOptions { get; }
    public ObservableCollection<string> NoteSubHeadOptions { get; }
    public ObservableCollection<string> SameTallyGroupScopes { get; }
    public ObservableCollection<string> PreviousYearDataModes { get; }
    public ICollectionView FyTrialBalanceView { get; }
    public ICollectionView PyTrialBalanceView { get; }
    public ICollectionView FyStockView { get; }
    public ICollectionView PyStockView { get; }
    public ICollectionView FyDebtorsView { get; }
    public ICollectionView PyDebtorsView { get; }
    public ICollectionView FyCreditorsView { get; }
    public ICollectionView PyCreditorsView { get; }

    public AsyncRelayCommand ExportWithLinksCommand { get; }
    public AsyncRelayCommand ExportWithoutLinksCommand { get; }
    public AsyncRelayCommand ExportPdfCommand { get; }
    public RelayCommand NotesPreparationCommand { get; }
    public RelayCommand SaveMappingsCommand { get; }
    public RelayCommand ApplyMappingToSameTallyGroupCommand { get; }
    public RelayCommand CopyFyToPyMappingCommand { get; }
    public RelayCommand CopyPyToFyMappingCommand { get; }
    public RelayCommand ApplyStockMappingToSameGroupCommand { get; }
    public RelayCommand CopyStockFyToPyMappingCommand { get; }
    public RelayCommand CopyStockPyToFyMappingCommand { get; }
    public RelayCommand ApplyDebtorScheduleToSameGroupCommand { get; }
    public RelayCommand CopyDebtorFyToPyMappingCommand { get; }
    public RelayCommand CopyDebtorPyToFyMappingCommand { get; }
    public RelayCommand ApplyCreditorScheduleToSameGroupCommand { get; }
    public RelayCommand CopyCreditorFyToPyMappingCommand { get; }
    public RelayCommand CopyCreditorPyToFyMappingCommand { get; }
    public AsyncRelayCommand ConnectPreviousYearTallyCommand { get; }
    public RelayCommand UseActualPreviousYearCommand { get; }
    public RelayCommand UseFyOpeningAsPreviousYearCommand { get; }
    public RelayCommand SaveBasicInfoCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string PreparationStatusText { get => _preparationStatusText; private set => SetProperty(ref _preparationStatusText, value); }
    public bool ShowClientFinancialStatementSettings => FinancialStatementsEnabled;
    public bool IsFinancialStatementsDisabled => !FinancialStatementsEnabled;
    public bool NoPreviousYearData
    {
        get => PreviousYearDataMode.Equals("No Previous Year / First Year", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value == NoPreviousYearData) return;
            PreviousYearDataMode = value ? "No Previous Year / First Year" : "Actual Previous Year Data";
        }
    }
    public string PreviousYearModeDisplay => PreviousYearDataMode.Equals("Use Current FY Opening Balances as PY", StringComparison.OrdinalIgnoreCase)
        ? "Use Current FY Opening Balances as PY"
        : "Actual Previous Year Data";
    public bool ShowPreviousYearSourceOptions => !NoPreviousYearData;
    public bool ShowConnectPreviousYearTally => FinancialStatementsEnabled &&
        PreviousYearDataMode.Equals("Actual Previous Year Data", StringComparison.OrdinalIgnoreCase);
    public bool FinancialStatementsReleaseAvailable => ReleaseFeatureGates.FinancialStatements;
    public string FinancialStatementsCollectionStatus => !ReleaseFeatureGates.FinancialStatements
        ? ReleaseFeatureGates.FinancialStatementsDisabledReason + " No FS-specific Tally/NCE collection runs."
        : FinancialStatementsEnabled
            ? "Enabled for this Client/FY · saved Financial Statements authority remains available offline and refreshes from Tally when available."
            : "Off for this Client/FY · saved Financial Statements data is retained; no FS-specific Tally collection runs.";
    public string CrossYearMappingSummary { get; private set; } = string.Empty;
    public FinancialStatementLedgerRow? SelectedFyTrialBalanceRow { get => _selectedFyTrialBalanceRow; set => SetProperty(ref _selectedFyTrialBalanceRow, value); }
    public FinancialStatementStockRow? SelectedFyStockRow { get => _selectedFyStockRow; set => SetProperty(ref _selectedFyStockRow, value); }
    public FinancialStatementLedgerRow? SelectedFyDebtorRow { get => _selectedFyDebtorRow; set => SetProperty(ref _selectedFyDebtorRow, value); }
    public FinancialStatementLedgerRow? SelectedFyCreditorRow { get => _selectedFyCreditorRow; set => SetProperty(ref _selectedFyCreditorRow, value); }
    public string SameTallyGroupScope { get => _sameTallyGroupScope; set => SetProperty(ref _sameTallyGroupScope, string.IsNullOrWhiteSpace(value) ? "FY only" : value); }
    public bool ShowUnmappedOnly
    {
        get => _showUnmappedOnly;
        set
        {
            if (!SetProperty(ref _showUnmappedOnly, value)) return;
            RefreshMappingViews();
        }
    }
    public bool FinancialStatementsEnabled
    {
        get => ReleaseFeatureGates.FinancialStatements && _financialStatementsEnabled;
        set
        {
            if (!ReleaseFeatureGates.FinancialStatements)
            {
                StatusText = ReleaseFeatureGates.FinancialStatementsDisabledReason;
                OnPropertyChanged(nameof(FinancialStatementsEnabled));
                OnPropertyChanged(nameof(IsFinancialStatementsDisabled));
                OnPropertyChanged(nameof(ShowClientFinancialStatementSettings));
                OnPropertyChanged(nameof(ShowConnectPreviousYearTally));
                OnPropertyChanged(nameof(FinancialStatementsCollectionStatus));
                return;
            }
            if (!SetProperty(ref _financialStatementsEnabled, value)) return;
            SaveFeatureSettings();
            OnPropertyChanged(nameof(IsFinancialStatementsDisabled));
            OnPropertyChanged(nameof(ShowClientFinancialStatementSettings));
            OnPropertyChanged(nameof(ShowConnectPreviousYearTally));
            OnPropertyChanged(nameof(FinancialStatementsCollectionStatus));
            if (value)
            {
                StatusText = "Financial Statements enabled for this Client/FY. Saved preparation retained; Tally authority will refresh from the normal heartbeat when available.";
                Refresh();
                OnTallyHeartbeatChanged(this, EventArgs.Empty);
            }
            else
            {
                _nceTallyRefreshCancellation?.Cancel();
                IsNotesPreparationVisible = false;
                ClearCollections();
                StatusText = "Financial Statements disabled for this Client/FY. Existing preparation and mappings are retained; no Financial Statements data is shown and no FS-specific Tally collection will run.";
            }
        }
    }
    public string PreviousYearDataMode
    {
        get => _previousYearDataMode;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Actual Previous Year Data" : value.Trim();
            if (!SetProperty(ref _previousYearDataMode, normalized)) return;
            BasicInfo.PreviousYearDataMode = normalized;
            SaveFeatureSettings();
            OnPropertyChanged(nameof(NoPreviousYearData));
            OnPropertyChanged(nameof(PreviousYearModeDisplay));
            OnPropertyChanged(nameof(ShowPreviousYearSourceOptions));
            OnPropertyChanged(nameof(ShowConnectPreviousYearTally));
            OnPropertyChanged(nameof(PreviousYearDataAvailable));
            OnPropertyChanged(nameof(PreviousYearStatus));
            Refresh();
        }
    }
    public string SelectedMainTab { get => _selectedMainTab; set => SetProperty(ref _selectedMainTab, value); }
    public string EntityTypeSelection
    {
        get => BasicInfo.EntityType;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (BasicInfo.EntityType.Equals(normalized, StringComparison.Ordinal)) return;
            BasicInfo.EntityType = normalized;
            if (!IsNceEntityType) BasicInfo.NceType = string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNceEntityType));
            OnPropertyChanged(nameof(IsCompanyScheduleIIIEntityType));
            OnPropertyChanged(nameof(RegistrationIdLabel));
            OnPropertyChanged(nameof(BasicInfo));
            RebuildTemplatePresentation();
            ReloadSelectedProfileMappings();
        }
    }
    public bool IsNceEntityType => BasicInfo.EntityType.Equals("NCE", StringComparison.OrdinalIgnoreCase) || BasicInfo.EntityType.StartsWith("NCE -", StringComparison.OrdinalIgnoreCase);
    public bool IsCompanyScheduleIIIEntityType => FinancialStatementWorkbookService.ResolveProfileSheet(BasicInfo.EntityType).Equals("C_AS", StringComparison.OrdinalIgnoreCase);
    public string RegistrationIdLabel => IsCompanyScheduleIIIEntityType
        ? "Corporate Identification Number (CIN)"
        : "Limited Liability Partnership Identification Number (LLPIN)";
    public bool HideZeroClosingBalance
    {
        get => _hideZeroClosingBalance;
        set
        {
            if (!SetProperty(ref _hideZeroClosingBalance, value)) return;
            RefreshMappingViews();
        }
    }
    public string SelectedNote
    {
        get => _selectedNote;
        set
        {
            if (SetProperty(ref _selectedNote, value)) RefreshNotePreview();
        }
    }
    public bool IsNotesPreparationVisible { get => _isNotesPreparationVisible; private set => SetProperty(ref _isNotesPreparationVisible, value); }
    public FinancialStatementBasicInfo BasicInfo { get => _basicInfo; private set => SetProperty(ref _basicInfo, value); }
    public CaProfileRow? SelectedCaProfile
    {
        get => _selectedCaProfile;
        set
        {
            if (!SetProperty(ref _selectedCaProfile, value) || value is null) return;
            BasicInfo.CaName = value.CaName;
            BasicInfo.CaDesignation = value.Designation;
            BasicInfo.CaMembershipNumber = value.MembershipNumber;
            BasicInfo.AuditFirmName = value.FirmName;
            BasicInfo.FirmRegistrationNumber = value.FirmRegistrationNumber;
            BasicInfo.AuditPlace = value.Place;
            OnPropertyChanged(nameof(BasicInfo));
        }
    }

    public string FyCaption => _clientContext.CurrentClient?.FinancialYear ?? string.Empty;
    public string PyCaption => PreviousFinancialYear(FyCaption);
    public bool PreviousYearDataAvailable => PreviousYearDataMode.Equals("No Previous Year / First Year", StringComparison.OrdinalIgnoreCase) || PyTrialBalance.Count > 0 || PyStock.Count > 0;
    public string PreviousYearStatus
        => PreviousYearDataMode.Equals("No Previous Year / First Year", StringComparison.OrdinalIgnoreCase)
            ? $"PY {PyCaption}: Not applicable / first year"
            : PreviousYearDataMode.Equals("Use Current FY Opening Balances as PY", StringComparison.OrdinalIgnoreCase)
                ? $"PY {PyCaption}: derived from available FY opening balances"
                : PreviousYearDataAvailable
                    ? $"PY {PyCaption} data ready"
                    : $"PY {PyCaption} data not found in the current Client authority. Connect/select the previous-year Tally company when required.";


    public void AttachTallyCoordinator(TallyLiveConnectionCoordinator coordinator)
    {
        if (ReferenceEquals(_tallyCoordinator, coordinator)) return;
        if (_tallyCoordinator is not null) _tallyCoordinator.Changed -= OnTallyHeartbeatChanged;
        _tallyCoordinator = coordinator;
        _tallyCoordinator.Changed += OnTallyHeartbeatChanged;
    }

    private string _lastNceHeartbeatAuthorityKey = string.Empty;

    private void OnTallyHeartbeatChanged(object? sender, EventArgs e)
    {
        if (!FinancialStatementsEnabled || !IsNceEntityType || _tallyCoordinator?.IsConnected != true || _tallyCoordinator.IsBusy) return;
        var client = _clientContext.CurrentClient;
        var company = _tallyCoordinator.SelectedCompany;
        if (client is null || company is null) return;
        // The normal 5-second Tally heartbeat is only the trigger. Expensive Financial
        // Statements collection runs once for a new company/change-token authority, never
        // every five seconds, and never when this Client/FY has the feature switched off.
        var authorityKey = $"{client.Id}|{client.FinancialYear}|{company.CompanyKey}|{company.ChangeToken}";
        if (authorityKey.Equals(_lastNceHeartbeatAuthorityKey, StringComparison.Ordinal)) return;
        if (_nceTallyRefreshCancellation is { IsCancellationRequested: false }) return;
        _lastNceHeartbeatAuthorityKey = authorityKey;
        ScheduleNceTallyRefresh(client);
    }

    public void PrepareForDisplay()
    {
        var client = _clientContext.CurrentClient;
        if (client is not null)
        {
            // Bind the Client/FY feature + PY mode before deciding what background cache work is
            // legal. This prevents a previous Client's PY mode from scheduling the wrong year and
            // keeps the long-standing rule that disabled Financial Statements start no FS cache work.
            LoadFeatureSettings(client);
        }

        if (client is not null && FinancialStatementsEnabled && _workspaceStateCache is not null)
        {
            // Reuse the same prepared-cache pipeline as Accounting pages. Navigation never
            // waits for these reads: the FS-specific saved snapshot paints first, while
            // Masters/Stock/Statement authority is prepared in the background for a cold load.
            _workspaceStateCache.EnsureModulePrepared(client.Id, client.FinancialYear, ClientWorkspaceModule.Masters);
            _workspaceStateCache.EnsureModulePrepared(client.Id, client.FinancialYear, ClientWorkspaceModule.Stock);
            _workspaceStateCache.EnsureModulePrepared(client.Id, client.FinancialYear, ClientWorkspaceModule.Statements);
            if (PreviousYearDataMode.Equals("Actual Previous Year Data", StringComparison.OrdinalIgnoreCase))
            {
                var py = PreviousFinancialYear(client.FinancialYear);
                _workspaceStateCache.EnsureModulePrepared(client.Id, py, ClientWorkspaceModule.Masters);
                _workspaceStateCache.EnsureModulePrepared(client.Id, py, ClientWorkspaceModule.Stock);
                _workspaceStateCache.EnsureModulePrepared(client.Id, py, ClientWorkspaceModule.Statements);
            }
        }
        Refresh();
    }

    private void Refresh()
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            ClearCollections();
            StatusText = "Open a Client/FY to prepare Financial Statements.";
            return;
        }

        LoadCaProfiles();
        LoadBasicInfo(client);
        LoadFeatureSettings(client);
        if (!FinancialStatementsEnabled)
        {
            ClearCollections();
            _nceTallyRefreshCancellation?.Cancel();
            StatusText = ReleaseFeatureGates.FinancialStatements
                ? "Financial Statements is disabled for this Client/FY. Enable it to show the saved preparation data."
                : ReleaseFeatureGates.FinancialStatementsDisabledReason;
            return;
        }
        ApplyLinkedCaProfile(client);
        var fy = client.FinancialYear;
        var py = PreviousFinancialYear(fy);
        // R9 FIX4: load live TB authority before building the owner-aware mapping catalog.
        // This lets NCE C3 use saved Basic Info owner names first and safe Capital-ledger
        // name fallbacks second; the old order built the catalog while the TB was still empty.
        // R9 FIXED15: TallyPrime 7.1+ Financial Statements/NCE does not feed the
        // current-period Profit & Loss A/c Balance-Sheet balance into the CY TB data.
        // Its official FSforNCE workbook carries that CY row as zero and derives the
        // period result from income/expense + stock movement, then includes the result
        // through the owners' capital/current-account movement schedule. Neutralise only
        // the NCE current-year projection; never mutate the saved ledger/master authority,
        // and never erase the previous-year comparative Profit & Loss A/c balance.
        // Paint the compact prepared data snapshot first. Exact NCE XML is the durable
        // refresh authority, but the prepared projection is intentionally the fast navigation path.
        var restoredStoredAuthority = RestoreDataSnapshot(client.Id, fy) || TryRestoreNceFeedSnapshot(client.Id, fy);
        // R52 FIXED13: a prepared data snapshot saved before FIXED9 can contain the correct visible
        // TB/Stock/Debtor/Creditor rows and all user mappings, but it cannot contain the later
        // hidden XMLStatement period/schedule authority flags. Rehydrate only those hidden totals
        // from the durable last-good NCE feed. Never repopulate rows and never alter a mapping.
        if (restoredStoredAuthority && IsNceEntityType)
            RehydrateNceSourceAuthorityFromSavedFeed(client.Id, fy);
        if (!restoredStoredAuthority)
        {
            var fyPrepared = TryLoadPeriodFromPreparedCache(client.Id, fy, FyTrialBalance, FyStock, FyDebtors, FyCreditors, neutralizeCurrentNceProfitLoss: IsNceEntityType);
            var pyPrepared = TryLoadPeriodFromPreparedCache(client.Id, py, PyTrialBalance, PyStock, PyDebtors, PyCreditors, neutralizeCurrentNceProfitLoss: false);
            if (!fyPrepared)
                LoadPeriod(client.Id, fy, FyTrialBalance, FyStock, FyDebtors, FyCreditors, neutralizeCurrentNceProfitLoss: IsNceEntityType);
            if (!pyPrepared)
                LoadPeriod(client.Id, py, PyTrialBalance, PyStock, PyDebtors, PyCreditors, neutralizeCurrentNceProfitLoss: false);
            if (FinancialStatementsEnabled) SaveDataSnapshot(client.Id, fy);
        }
        ApplyPreviousYearMode();
        SeedOwnerPartnersFromCapitalLedgersWhenBlank();
        RebuildTemplatePresentation();
        var mappings = LoadMappings(client.Id, fy);
        ApplyMappings(mappings, FyTrialBalance, FyStock);
        ApplyMappings(mappings, PyTrialBalance, PyStock);
        ApplyScheduleMappings(mappings, FyDebtors.Concat(FyCreditors), "FY");
        ApplyScheduleMappings(mappings, PyDebtors.Concat(PyCreditors), "PY");
        SynchronizePpePresentationToUniqueSourceAuthority();
        AutoCopyExactLedgerMappings();
        SynchronizePartyScheduleAuthority();
        BuildNoteList(BasicInfo.EntityType);
        RefreshNotePreview();
        OnPropertyChanged(nameof(FyCaption));
        OnPropertyChanged(nameof(PyCaption));
        OnPropertyChanged(nameof(PreviousYearDataAvailable));
        OnPropertyChanged(nameof(PreviousYearStatus));
        StatusText = $"{client.ClientName} · FY {fy} · Financial Statements preparation ready.";
        UpdatePreparedFinancialStatementState();

        // R22 NCE authority: paint the saved/local preparation immediately, then replace only
        // the Financial Statements data layer from the proven background NCE feed when Tally
        // is available. A Tally timeout/failure never clears the usable local statement data.
        if (!FinancialStatementsEnabled || !IsNceEntityType)
        {
            _nceTallyRefreshCancellation?.Cancel();
            _nceTallyRefreshCancellation?.Dispose();
            _nceTallyRefreshCancellation = null;
        }
    }

    private void ScheduleNceTallyRefresh(ClientProfile client)
    {
        // CL017: Financial Statements cannot start unrequested background Tally
        // NCE current/previous-FY XML reads during voucher import or company
        // heartbeat. Saved/current local FS data remains available unchanged.
        // Explicit user-initiated FS connection commands retain their own path.
        _nceTallyRefreshCancellation?.Cancel();
        _nceTallyRefreshCancellation?.Dispose();
        _nceTallyRefreshCancellation = null;
    }

    private async Task RefreshNceFromTallyAsync(ClientProfile client, long generation, CancellationToken cancellationToken)
    {
        try
        {
            var company = FirstNonBlank(BasicInfo.CurrentYearCompanyName, client.ClientName, client.TradeName, client.LegalName);
            var previousCompany = FirstNonBlank(BasicInfo.PreviousYearTallyCompany, BasicInfo.PreviousYearCompanyName, company);
            using var http = new TallyHttpService(
                _preferences.Get(ApplicationSettingKeys.TallyHost),
                _preferences.GetInt(ApplicationSettingKeys.TallyPort, 9000));

            var feed = await http.ExportNceFinancialStatementFeedAsync(
                company,
                client.FinancialYear,
                previousCompany,
                cancellationToken).ConfigureAwait(true);

            if (generation != Volatile.Read(ref _nceTallyRefreshGeneration) || cancellationToken.IsCancellationRequested) return;
            var current = _clientContext.CurrentClient;
            if (current is null || current.Id != client.Id ||
                !current.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase) || !IsNceEntityType || !FinancialStatementsEnabled)
                return;

            ApplyNceFeed(client, feed);
            StatusText = $"{client.ClientName} · FY {client.FinancialYear} · NCE Financial Statements refreshed from Tally background authority.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Fail closed: keep the already-painted local Financial Statements authority.
            // AAH must never blank the page merely because Tally is closed, busy or an NCE
            // plug-in request needs a clean Tally restart.
            if (generation == Volatile.Read(ref _nceTallyRefreshGeneration))
                StatusText = $"Financial Statements local data retained. Tally NCE background refresh deferred: {exception.Message}";
        }
    }

    private void ApplyNceFeed(ClientProfile client, NceFinancialStatementFeed feed)
    {
        SaveNceFeedSnapshot(client.Id, client.FinancialYear, feed);
        PopulateNcePeriod(feed.CurrentYear, "CY", feed.DetailCollections, FyTrialBalance, FyStock, FyDebtors, FyCreditors);
        PopulateNcePeriod(feed.PreviousYear, "PY", feed.DetailCollections, PyTrialBalance, PyStock, PyDebtors, PyCreditors);
        SaveDataSnapshot(client.Id, client.FinancialYear);

        // The report's MappingHeadOne/Two and final C1/C2 values are plug-in authority.
        // Configure the current workbook catalog after those rows exist. Fresh plug-in TB
        // hierarchy is the automatic authority; a deliberate manual mapping can override an
        // ordinary ledger, but copied/automatic/legacy preferences cannot replace live NCE
        // evidence. The standard P&L bridge always follows the fresh plug-in hierarchy.
        SeedOwnerPartnersFromCapitalLedgersWhenBlank();
        RebuildTemplatePresentation();
        var mappings = LoadMappings(client.Id, client.FinancialYear);
        ApplyMappings(mappings, FyTrialBalance, FyStock);
        ApplyMappings(mappings, PyTrialBalance, PyStock);
        ApplyScheduleMappings(mappings, FyDebtors.Concat(FyCreditors), "FY");
        ApplyScheduleMappings(mappings, PyDebtors.Concat(PyCreditors), "PY");
        SynchronizePpePresentationToUniqueSourceAuthority();
        ApplyPreviousYearMode();
        AutoCopyExactLedgerMappings();
        SynchronizePartyScheduleAuthority();
        BuildNoteList(BasicInfo.EntityType);
        RefreshNotePreview();
        FyTrialBalanceView.Refresh();
        PyTrialBalanceView.Refresh();
        FyStockView.Refresh();
        PyStockView.Refresh();
        FyDebtorsView.Refresh();
        PyDebtorsView.Refresh();
        FyCreditorsView.Refresh();
        PyCreditorsView.Refresh();
        UpdatePreparedFinancialStatementState();
        OnPropertyChanged(nameof(PreviousYearDataAvailable));
        OnPropertyChanged(nameof(PreviousYearStatus));
    }

    private void PopulateNcePeriod(
        NceFinancialStatementPeriod period,
        string periodPrefix,
        IReadOnlyDictionary<string, IReadOnlyList<NceFinancialStatementDetailRow>> detailCollections,
        ObservableCollection<FinancialStatementLedgerRow> tb,
        ObservableCollection<FinancialStatementStockRow> stock,
        ObservableCollection<FinancialStatementLedgerRow> debtors,
        ObservableCollection<FinancialStatementLedgerRow> creditors)
    {
        tb.Clear(); stock.Clear(); debtors.Clear(); creditors.Clear();
        var sourceOrder = 0;
        foreach (var source in period.TrialBalance)
        {
            var row = new FinancialStatementLedgerRow
            {
                MasterId = source.MasterId,
                Ledger = source.LedgerName,
                ParentGroup = source.Parent,
                PrimaryGroup = source.PrimaryGroup,
                Opening = source.OpeningBalance,
                Debit = source.DebitAmount,
                Credit = source.CreditAmount,
                Closing = source.ClosingBalance,
                SourceOrder = sourceOrder++
            };
            if (!string.IsNullOrWhiteSpace(source.MappingHeadOne) || !string.IsNullOrWhiteSpace(source.MappingHeadTwo))
                row.SetPartialHierarchy(string.Empty, string.Empty, source.MappingHeadOne, source.MappingHeadTwo, string.Empty, "Tally NCE");
            tb.Add(row);
        }

        // R32: XMLStatement remains the complete CY/PY report and statutory-total authority,
        // while the exact final plug-in detail collections drive the visible workbook mode.
        // This mirrors FSforNCE: Item-wise/Ledger-wise Stock and Party-wise/Bill-wise
        // Debtors/Creditors come from their own final collections rather than being rebuilt
        // from XMLStatement bill rows. No Debtor collection is ever mixed with a Creditor
        // collection when resolving C1/C2 mapping evidence.
        PopulateNceStockRows(period, periodPrefix, detailCollections, stock);
        PopulateNcePartyRows(period, periodPrefix, detailCollections, debtors, creditors);
    }

    private void PopulateNceStockRows(
        NceFinancialStatementPeriod period,
        string periodPrefix,
        IReadOnlyDictionary<string, IReadOnlyList<NceFinancialStatementDetailRow>> detailCollections,
        ObservableCollection<FinancialStatementStockRow> target)
    {
        var wantsLedgerWise = BasicInfo.StockDetail.Equals("Ledger-wise", StringComparison.OrdinalIgnoreCase);
        var selectedKey = $"{periodPrefix}_{(wantsLedgerWise ? "STOCK_LEDGER" : "STOCK_ITEM")}";
        // FIXED9 backward compatibility: snapshots saved before the explicit section flags
        // were introduced still contain the full StockSummary rows. Treat those rows themselves
        // as proof of statutory stock authority so an offline client does not fall back to stale
        // item-detail totals after upgrading.
        var hasOfficialAuthority = period.HasStockSummaryAuthority || period.StockSummary.Count > 0;
        var officialOpening = period.StockSummary.Sum(x => x.OpeningValue);
        var officialClosing = period.StockSummary.Sum(x => x.ClosingValue);

        if (detailCollections.TryGetValue(selectedKey, out var selectedRows) && selectedRows.Count > 0)
        {
            // detailReconciles: retained historical verifier marker. FIXED9 no longer discards
            // user detail rows when their amounts differ; the official total travels separately.
            // R52 FIXED9: keep the exact detail rows so every user-selected Inventory
            // Classification remains attached to the same row. XMLStatement totals travel as
            // hidden accounting authority instead of replacing/remapping those rows.
            foreach (var detail in selectedRows)
            {
                var row = new FinancialStatementStockRow
                {
                    Item = FirstNonBlank(detail.StockItemName, detail.Name),
                    Group = FirstNonBlank(detail.StockGroup, detail.Parent),
                    Category = FirstNonBlank(detail.StockCategory, detail.C3MappingGroup),
                    OpeningBalance = detail.OpeningValue,
                    ClosingBalance = detail.ClosingValue
                };

                var automaticMapping = ResolveNceStockDetailClassification(detail, period.StockSummary);
                if (!string.IsNullOrWhiteSpace(automaticMapping)) row.RestoreMapping(automaticMapping, "Tally NCE");
                target.Add(row);
            }

            AttachStockPeriodAuthority(target, hasOfficialAuthority, officialOpening, officialClosing);
            return;
        }

        // Missing optional detail collection: keep XMLStatement rows as the visible fallback.
        var allStockDetails = detailCollections
            .Where(x => x.Key.StartsWith(periodPrefix + "_STOCK_", StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.Value)
            .ToArray();
        foreach (var source in period.StockSummary)
        {
            var row = new FinancialStatementStockRow
            {
                Item = source.StockItemName,
                Group = source.StockGroup,
                Category = source.StockCategory,
                OpeningBalance = source.OpeningValue,
                ClosingBalance = source.ClosingValue
            };
            var automaticMapping = ResolveNceStockClassification(source, allStockDetails);
            if (!string.IsNullOrWhiteSpace(automaticMapping)) row.RestoreMapping(automaticMapping, "Tally NCE");
            target.Add(row);
        }

        AttachStockPeriodAuthority(target, hasOfficialAuthority, officialOpening, officialClosing);
    }

    private static void AttachStockPeriodAuthority(
        ObservableCollection<FinancialStatementStockRow> target,
        bool hasAuthority,
        decimal opening,
        decimal closing)
    {
        if (!hasAuthority || target.Count == 0) return;
        // One carrier is enough and avoids multiplying the period total when rows are aggregated.
        target[0].HasPeriodSourceAuthority = true;
        target[0].PeriodSourceOpeningTotal = opening;
        target[0].PeriodSourceClosingTotal = closing;
    }

    private void PopulateNcePartyRows(
        NceFinancialStatementPeriod period,
        string periodPrefix,
        IReadOnlyDictionary<string, IReadOnlyList<NceFinancialStatementDetailRow>> detailCollections,
        ObservableCollection<FinancialStatementLedgerRow> debtors,
        ObservableCollection<FinancialStatementLedgerRow> creditors)
    {
        var billWise = BasicInfo.DebtorCreditorDetail.Equals("Bill-wise", StringComparison.OrdinalIgnoreCase);
        var debtorKey = $"{periodPrefix}_DEBTOR_{(billWise ? "BILL" : "LEDGER")}";
        var creditorKey = $"{periodPrefix}_CREDITOR_{(billWise ? "BILL" : "LEDGER")}";

        var debtorDetails = detailCollections.TryGetValue(debtorKey, out var dd) ? dd : Array.Empty<NceFinancialStatementDetailRow>();
        var creditorDetails = detailCollections.TryGetValue(creditorKey, out var cd) ? cd : Array.Empty<NceFinancialStatementDetailRow>();

        // Keep the user's prepared/detail rows exactly as-is. The official XMLStatement
        // section total is attached separately and is used only by the accounting result engine.
        if (debtorDetails.Count > 0)
            AddPluginPartyRows(debtorDetails, debtors, "Debtor", billWise);
        else
            AddOfficialPartyRows(period.TradeReceivables, debtors, "Debtor");

        if (creditorDetails.Count > 0)
            AddPluginPartyRows(creditorDetails, creditors, "Creditor", billWise);
        else
            AddOfficialPartyRows(period.TradePayables, creditors, "Creditor");

        // Old last-good NCE snapshots predate explicit empty-section flags. A complete period
        // with TB + Stock + payable authority came from the same XMLStatement report, so an
        // empty receivables list in that legacy snapshot is a proved statutory zero rather than
        // permission to reuse stale plug-in debtor details.
        var legacyCompletePeriod =
            period.TrialBalance.Count > 0 &&
            period.StockSummary.Count > 0 &&
            (period.TradePayables.Count > 0 || creditorDetails.Count > 0);
        var hasDebtorAuthority =
            period.HasTradeReceivablesAuthority ||
            period.TradeReceivables.Count > 0 ||
            legacyCompletePeriod;
        var hasCreditorAuthority =
            period.HasTradePayablesAuthority ||
            period.TradePayables.Count > 0 ||
            legacyCompletePeriod;

        AttachPartyScheduleAuthority(
            debtors,
            hasDebtorAuthority,
            OfficialPartyScheduleTotal(period.TradeReceivables, "Trade Receivables"));
        AttachPartyScheduleAuthority(
            creditors,
            hasCreditorAuthority,
            OfficialPartyScheduleTotal(period.TradePayables, "Trade Payables"));
    }

    private static decimal OfficialPartyScheduleTotal(IEnumerable<NcePartyOutstandingRow> rows, string expectedHead)
    {
        var materialized = rows.ToArray();
        if (materialized.Length == 0) return 0m;

        // R52 FIXED17: the XML party collections can contain opposite-balance disclosure rows.
        // When the plug-in supplies MAPPINGHEADONE, only the statutory head owned by the
        // collection is allowed to contribute to its authoritative total. This prevents
        // receivables/advances leaking into Trade Payables and prevents stale debtor rows from
        // manufacturing Trade Receivables when the official receivable head is empty.
        var hasHeadAuthority = materialized.Any(x => !string.IsNullOrWhiteSpace(x.MappingHeadOne));
        var selected = hasHeadAuthority
            ? materialized.Where(x => x.MappingHeadOne.Equals(expectedHead, StringComparison.OrdinalIgnoreCase))
            : materialized;
        return selected.Sum(x => Math.Abs(x.PendingAmount != 0m ? x.PendingAmount : x.OnAccountAmount));
    }

    private static void AttachPartyScheduleAuthority(
        ObservableCollection<FinancialStatementLedgerRow> target,
        bool hasAuthority,
        decimal total)
    {
        if (!hasAuthority || target.Count == 0) return;
        target[0].HasScheduleSourceAuthority = true;
        target[0].ScheduleSourceAuthorityTotal = total;
    }

    private static void AddPluginPartyRows(
        IReadOnlyList<NceFinancialStatementDetailRow> detailRows,
        ObservableCollection<FinancialStatementLedgerRow> target,
        string scheduleContext,
        bool billWise)
    {
        // R50: Party-wise and Bill-wise are genuinely different source views. The previous
        // implementation emitted one row for every plug-in detail even when Party-wise was
        // selected, so one party (for example a customer with many bills) appeared repeatedly.
        // Party-wise now aggregates exact party + statutory schedule identity; Bill-wise keeps
        // the individual bill/ref identity visible. No opposite-side collection is mixed in.
        var prepared = detailRows.Select((detail, index) =>
        {
            var party = FirstNonBlank(detail.PartyName, detail.Parent, detail.Name);
            var rawC1 = FirstNonBlank(detail.C1MappingGroup, detail.MappingHeadOne);
            var rawC2 = FirstNonBlank(detail.C2MappingGroup, detail.MappingHeadTwo, detail.MsmeType);
            var normalizedSchedule = NormalizeNcePartySchedule(scheduleContext, rawC1, rawC2, detail.IsMsmeParty || detail.MsmeType.Contains("MSME", StringComparison.OrdinalIgnoreCase));
            var c1 = normalizedSchedule.C1;
            var c2 = normalizedSchedule.C2;
            var signed = NceScheduleSignedAmount(c1, NcePartyDetailMagnitude(detail), scheduleContext);
            var reference = FirstNonBlank(detail.PartyRefNo, billWise ? detail.Name : string.Empty);
            var parent = FirstNonBlank(detail.ParentGroupName,
                scheduleContext.Equals("Debtor", StringComparison.OrdinalIgnoreCase) ? "Sundry Debtors" : "Sundry Creditors");
            return new
            {
                Detail = detail, Index = index, Party = party, C1 = c1, C2 = c2, Signed = signed,
                Reference = reference, Parent = parent
            };
        }).Where(x => !string.IsNullOrWhiteSpace(x.Party)).ToArray();

        var order = 0;
        if (billWise)
        {
            foreach (var item in prepared)
            {
                var display = string.IsNullOrWhiteSpace(item.Reference)
                    ? item.Party
                    : $"{item.Party} · {item.Reference}";
                var row = new FinancialStatementLedgerRow
                {
                    MasterId = item.Detail.MasterId,
                    Ledger = display,
                    ParentGroup = item.Parent,
                    PrimaryGroup = item.Detail.PrimaryGroup,
                    Closing = item.Signed,
                    SourceOrder = order++
                };
                row.ConfigureScheduleContext(scheduleContext);
                row.RestoreScheduleMapping(item.C1, item.C2, "Tally NCE");
                target.Add(row);
            }
            return;
        }

        static string PartyKey(string party)
            => "NM:" + new string((party ?? string.Empty).Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

        foreach (var group in prepared.GroupBy(x =>
                     $"{PartyKey(x.Party)}\u001f{x.C1.Trim().ToUpperInvariant()}\u001f{x.C2.Trim().ToUpperInvariant()}",
                     StringComparer.OrdinalIgnoreCase))
        {
            var first = group.OrderBy(x => x.Index).First();
            var masterIds = group.Select(x => x.Detail.MasterId).Where(FinancialStatementPresentationExportService.IsUsableTallyMasterId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var row = new FinancialStatementLedgerRow
            {
                MasterId = masterIds.Length == 1 ? masterIds[0] : first.Detail.MasterId,
                Ledger = first.Party,
                ParentGroup = first.Parent,
                PrimaryGroup = first.Detail.PrimaryGroup,
                Closing = group.Sum(x => x.Signed),
                SourceOrder = order++
            };
            row.ConfigureScheduleContext(scheduleContext);
            row.RestoreScheduleMapping(first.C1, first.C2, "Tally NCE");
            target.Add(row);
        }
    }

    private static void AddOfficialPartyRows(
        IReadOnlyList<NcePartyOutstandingRow> officialRows,
        ObservableCollection<FinancialStatementLedgerRow> target,
        string scheduleContext)
    {
        // Only a fallback for a missing final collection. It intentionally does not search
        // the opposite-side collection for mapping evidence.
        var order = 0;
        foreach (var official in officialRows)
        {
            var rawC1 = FirstNonBlank(official.MappingHeadOne,
                scheduleContext.Equals("Debtor", StringComparison.OrdinalIgnoreCase) ? "Trade Receivables" : "Trade Payables");
            var normalizedSchedule = NormalizeNcePartySchedule(scheduleContext, rawC1, string.Empty, false);
            var c1 = normalizedSchedule.C1;
            var c2 = normalizedSchedule.C2;
            var amount = official.PendingAmount != 0m ? official.PendingAmount : official.OnAccountAmount;
            var row = new FinancialStatementLedgerRow
            {
                Ledger = official.PartyName,
                ParentGroup = scheduleContext.Equals("Debtor", StringComparison.OrdinalIgnoreCase) ? "Sundry Debtors" : "Sundry Creditors",
                Closing = NceScheduleSignedAmount(c1, Math.Abs(amount), scheduleContext),
                SourceOrder = order++
            };
            row.ConfigureScheduleContext(scheduleContext);
            row.RestoreScheduleMapping(c1, c2, "Tally NCE XMLStatement fallback");
            target.Add(row);
        }
    }

    private static (string C1, string C2) NormalizeNcePartySchedule(string scheduleContext, string? c1, string? c2, bool isMsme)
    {
        var context = (scheduleContext ?? string.Empty).Trim();
        var parent = (c1 ?? string.Empty).Trim();
        var child = FinancialStatementWorkbookRules.NormalizeScheduleC2(parent, c2);

        bool IsAny(string value, params string[] choices)
            => choices.Any(x => value.Equals(x, StringComparison.OrdinalIgnoreCase));

        if (context.Equals("Debtor", StringComparison.OrdinalIgnoreCase))
        {
            // A debtor collection can validly carry a credit/opposite balance into Other Current
            // Liabilities, but it can never become Trade Payables merely because a plug-in field
            // leaked the creditor C1/C2 labels. Normal debtor rows therefore canonicalise to
            // Trade Receivables.
            var valid = IsAny(parent, "Trade Receivables", "Other Non-Current Assets - Trade Receivables",
                "Other Current Assets", "Other Non-current Assets", "Other Current Liabilities", "Other Non-Current Liabilities");
            if (!valid || parent.Equals("Trade Payables", StringComparison.OrdinalIgnoreCase) ||
                parent.Equals("Other Non-Current Liabilities - Trade Payables", StringComparison.OrdinalIgnoreCase))
                parent = "Trade Receivables";
        }
        else
        {
            // Creditor collections may legitimately have debit/opposite balances classified as
            // Other Current Assets, but must not silently become Trade Receivables.
            var valid = IsAny(parent, "Trade Payables", "Other Non-Current Liabilities - Trade Payables",
                "Other Current Liabilities", "Other Non-Current Liabilities", "Other Current Assets", "Other Non-current Assets");
            if (!valid || parent.Equals("Trade Receivables", StringComparison.OrdinalIgnoreCase) ||
                parent.Equals("Other Non-Current Assets - Trade Receivables", StringComparison.OrdinalIgnoreCase))
                parent = "Trade Payables";
        }

        var choices = FinancialStatementWorkbookRules.GetScheduleC2Choices(parent);
        child = FinancialStatementWorkbookRules.NormalizeScheduleC2(parent, child);
        if (!choices.Contains(child, StringComparer.OrdinalIgnoreCase))
        {
            child = parent switch
            {
                "Trade Payables" or "Other Non-Current Liabilities - Trade Payables" => isMsme ? "MSME" : "Others",
                "Trade Receivables" or "Other Non-Current Assets - Trade Receivables" => "Unsecured considered good - Undisputed",
                "Other Current Liabilities" or "Other Non-Current Liabilities" => "Other Payables",
                "Other Current Assets" or "Other Non-current Assets" => "Advance to suppliers",
                _ => string.Empty
            };
        }
        return (parent, child);
    }

    private static decimal NcePartyDetailMagnitude(NceFinancialStatementDetailRow detail)
    {
        foreach (var candidate in new[] { detail.TallyClosingBalance, detail.ClosingBalance, detail.OnAccountValue })
            if (candidate != 0m) return Math.Abs(candidate);
        return 0m;
    }

    private static decimal NceScheduleSignedAmount(string c1, decimal magnitude, string scheduleContext)
    {
        var amount = Math.Abs(magnitude);
        var key = (c1 ?? string.Empty).Trim();
        // FinancialStatementBalanceDisplayConverter uses negative=Dr / positive=Cr.
        // Use the plug-in's statutory C1 as authority, never the raw collection sign,
        // because individual final plug-in collections expose different sign conventions.
        if (key.Equals("Trade Receivables", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Other Current Assets", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Other Non-Current Assets", StringComparison.OrdinalIgnoreCase))
            return -amount;
        if (key.Equals("Trade Payables", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Other Current Liabilities", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Other Non-Current Liabilities", StringComparison.OrdinalIgnoreCase))
            return amount;
        return scheduleContext.Equals("Debtor", StringComparison.OrdinalIgnoreCase) ? -amount : amount;
    }

    private static string ResolveNceStockDetailClassification(
        NceFinancialStatementDetailRow detail,
        IReadOnlyList<NceStockSummaryRow> officialRows)
    {
        static string Normalize(string? value) => new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        static string Canonical(string? value)
        {
            var key = Normalize(value);
            if (key.Length == 0) return string.Empty;
            foreach (var choice in FinancialStatementWorkbookRules.StockChoices)
                if (Normalize(choice) == key) return choice;
            return key switch
            {
                "RAWMATERIAL" or "RAWMATERIALS" => "Raw Materials",
                "WORKINPROGRESS" or "WORKINPROCESS" or "WIP" => "Work-in-Progress",
                "FINISHEDGOOD" or "FINISHEDGOODS" => "Finished Goods",
                "STOCKINTRADE" or "TRADINGGOODS" => "Stock-in-Trade",
                "PACKINGMATERIAL" or "PACKINGMATERIALS" => "Packing Materials",
                "OTHERMATERIAL" or "OTHERMATERIALS" => "Other Materials",
                _ => string.Empty
            };
        }

        foreach (var candidate in new[] { detail.C1MappingGroup, detail.C2MappingGroup, detail.C3MappingGroup, detail.MappingHeadOne, detail.MappingHeadTwo, detail.StockCategory, detail.StockGroup })
        {
            var mapped = Canonical(candidate);
            if (mapped.Length > 0) return mapped;
        }

        // The current Build-33 Stock final collections do not expose C1/C2 on every row.
        // Match back to XMLStatement only when the same stock identity has one unambiguous
        // workbook classification. Otherwise leave it reviewable instead of guessing.
        var name = Normalize(FirstNonBlank(detail.StockItemName, detail.Name));
        var matches = officialRows.Where(x =>
            Normalize(x.StockItemName) == name ||
            (name.Length > 0 && Normalize(x.StockGroup) == name) ||
            (Normalize(detail.Parent).Length > 0 && Normalize(x.StockGroup) == Normalize(detail.Parent)))
            .SelectMany(x => new[] { Canonical(x.StockCategory), Canonical(x.StockGroup) })
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : string.Empty;
    }

    private static string ResolveNceStockClassification(
        NceStockSummaryRow source,
        IReadOnlyList<NceFinancialStatementDetailRow> details)
    {
        static string Normalize(string? value) => new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        static string Canonical(string? value)
        {
            var key = Normalize(value);
            if (key.Length == 0) return string.Empty;
            foreach (var choice in FinancialStatementWorkbookRules.StockChoices)
                if (Normalize(choice) == key) return choice;
            return key switch
            {
                "RAWMATERIAL" or "RAWMATERIALS" => "Raw Materials",
                "WORKINPROGRESS" or "WORKINPROCESS" or "WIP" => "Work-in-Progress",
                "FINISHEDGOOD" or "FINISHEDGOODS" => "Finished Goods",
                "STOCKINTRADE" or "TRADINGGOODS" => "Stock-in-Trade",
                "PACKINGMATERIAL" or "PACKINGMATERIALS" => "Packing Materials",
                "OTHERMATERIAL" or "OTHERMATERIALS" => "Other Materials",
                _ => string.Empty
            };
        }

        static string MappingFrom(NceFinancialStatementDetailRow row)
        {
            foreach (var candidate in new[] { row.C1MappingGroup, row.C2MappingGroup, row.C3MappingGroup, row.MappingHeadOne, row.MappingHeadTwo, row.StockCategory, row.StockGroup })
            {
                var mapped = Canonical(candidate);
                if (mapped.Length > 0) return mapped;
            }
            return string.Empty;
        }

        var itemKey = Normalize(source.StockItemName);
        var groupKey = Normalize(source.StockGroup);
        var categoryKey = Normalize(source.StockCategory);
        var ranked = details
            .Select(row => new
            {
                Row = row,
                Score =
                    ((Normalize(FirstNonBlank(row.StockItemName, row.Name)) == itemKey && itemKey.Length > 0) ? 8 : 0) +
                    ((Normalize(row.StockGroup) == groupKey && groupKey.Length > 0) ? 3 : 0) +
                    ((Normalize(row.StockCategory) == categoryKey && categoryKey.Length > 0) ? 2 : 0)
            })
            .Where(x => x.Score >= 8 || x.Score >= 5)
            .OrderByDescending(x => x.Score)
            .ToArray();

        var mapped = ranked.Select(x => MappingFrom(x.Row)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        if (mapped.Length == 1) return mapped[0];

        foreach (var candidate in new[] { source.StockCategory, source.StockGroup })
        {
            var direct = Canonical(candidate);
            if (direct.Length > 0) return direct;
        }
        return string.Empty;
    }

    private static void ApplyNceScheduleClassification(
        FinancialStatementLedgerRow row,
        NceFinancialStatementDetailRow? detail,
        NcePartyOutstandingRow official,
        string scheduleContext)
    {
        var defaultC1 = scheduleContext.Equals("Debtor", StringComparison.OrdinalIgnoreCase) ? "Trade Receivables" : "Trade Payables";
        var candidates = new[]
        {
            detail?.C1MappingGroup ?? string.Empty, detail?.C2MappingGroup ?? string.Empty, detail?.C3MappingGroup ?? string.Empty,
            detail?.MappingHeadOne ?? string.Empty, detail?.MappingHeadTwo ?? string.Empty, official.MappingHeadOne ?? string.Empty
        };
        var c1 = candidates.FirstOrDefault(x => FinancialStatementWorkbookRules.ScheduleC1Choices.Contains(x.Trim(), StringComparer.OrdinalIgnoreCase))?.Trim() ?? defaultC1;
        var c2Choices = FinancialStatementWorkbookRules.GetScheduleC2Choices(c1);
        var c2 = string.Empty;
        foreach (var candidate in candidates.Concat(new[] { detail?.MsmeType ?? string.Empty }))
        {
            var normalized = FinancialStatementWorkbookRules.NormalizeScheduleC2(c1, candidate);
            var exact = c2Choices.FirstOrDefault(x => x.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (exact is null) continue;
            c2 = exact;
            break;
        }
        if (c2.Length == 0 && scheduleContext.Equals("Creditor", StringComparison.OrdinalIgnoreCase) && detail?.IsMsmeParty == true)
            c2 = "MSME";
        row.RestoreScheduleMapping(c1, c2);
    }

    private static NceFinancialStatementDetailRow? MatchNcePartyDetail(
        NcePartyOutstandingRow official,
        IReadOnlyList<NceFinancialStatementDetailRow> detailRows)
    {
        var party = NormalizeNceMatchText(official.PartyName);
        var reference = NormalizeNceMatchText(official.BillRef);
        var amount = Math.Abs(official.PendingAmount != 0m ? official.PendingAmount : official.OnAccountAmount);
        return detailRows
            .Select(row => new
            {
                Row = row,
                Score =
                    (NormalizeNceMatchText(FirstNonBlank(row.PartyName, row.Parent, row.Name)) == party ? 5 : 0) +
                    (reference.Length > 0 && NormalizeNceMatchText(FirstNonBlank(row.PartyRefNo, row.Name)) == reference ? 5 : 0) +
                    (amount != 0m && Math.Abs(amount - Math.Abs(row.TallyClosingBalance != 0m ? row.TallyClosingBalance : row.ClosingBalance != 0m ? row.ClosingBalance : row.OnAccountValue)) < 0.01m ? 3 : 0)
            })
            .Where(x => x.Score >= 8 || (x.Score >= 5 && amount != 0m &&
                Math.Abs(amount - Math.Abs(x.Row.TallyClosingBalance != 0m ? x.Row.TallyClosingBalance : x.Row.ClosingBalance != 0m ? x.Row.ClosingBalance : x.Row.OnAccountValue)) < 0.01m))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Row)
            .FirstOrDefault();
    }

    private static string NormalizeNceMatchText(string? value) =>
        new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private bool TryLoadPeriodFromPreparedCache(
        long clientId,
        string fy,
        ObservableCollection<FinancialStatementLedgerRow> tb,
        ObservableCollection<FinancialStatementStockRow> stock,
        ObservableCollection<FinancialStatementLedgerRow> debtors,
        ObservableCollection<FinancialStatementLedgerRow> creditors,
        bool neutralizeCurrentNceProfitLoss)
    {
        if (_workspaceStateCache is null || string.IsNullOrWhiteSpace(fy) ||
            !_workspaceStateCache.TryGet(clientId, fy, out var snapshot))
            return false;

        if (snapshot.Masters.Count == 0 && snapshot.LedgerTotals.Count == 0 && snapshot.StockItems.Count == 0)
            return false;

        tb.Clear(); stock.Clear(); debtors.Clear(); creditors.Clear();
        var masters = snapshot.Masters;
        var ledgers = masters.Where(x => x.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)).ToArray();
        var groupParents = masters
            .Where(x => x.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last().Parent ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var totals = snapshot.LedgerTotals
            .GroupBy(x => x.LedgerName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);

        var periodRows = new List<FinancialStatementLedgerRow>();
        var sourceOrder = 0;
        foreach (var ledger in ledgers)
        {
            totals.TryGetValue(ledger.Name, out var total);
            var opening = total?.OpeningBalance ?? ledger.OpeningBalance ?? 0m;
            var debit = total?.TotalDebit ?? 0m;
            var credit = total?.TotalCredit ?? 0m;
            var closing = total?.ClosingBalance ?? ledger.OpeningBalance ?? 0m;
            if (neutralizeCurrentNceProfitLoss && FinancialStatementTrialBalanceOrder.IsProfitLossAccountLedger(ledger.Name))
                opening = debit = credit = closing = 0m;

            periodRows.Add(new FinancialStatementLedgerRow
            {
                Ledger = ledger.Name,
                ParentGroup = ledger.Parent ?? string.Empty,
                PrimaryGroup = FinancialStatementTrialBalanceOrder.ResolvePrimaryGroup(ledger.Parent ?? string.Empty, groupParents),
                SourceOrder = sourceOrder++,
                MasterId = ledger.TallyMasterId,
                Opening = opening,
                Debit = debit,
                Credit = credit,
                Closing = closing
            });
        }

        foreach (var row in FinancialStatementTrialBalanceOrder.Order(periodRows))
        {
            tb.Add(row);
            var grouping = $"{row.ParentGroup} {row.PrimaryGroup}";
            if (ContainsGroup(grouping, "Sundry Debtors")) { row.ConfigureScheduleContext("Debtor"); debtors.Add(row); }
            if (ContainsGroup(grouping, "Sundry Creditors")) { row.ConfigureScheduleContext("Creditor"); creditors.Add(row); }
        }

        foreach (var item in snapshot.StockItems)
        {
            stock.Add(new FinancialStatementStockRow
            {
                Item = item.Name,
                Group = item.StockGroup,
                Category = item.StockCategory,
                Unit = item.BaseUnit,
                HsnSac = item.HsnSac,
                GstRate = item.GstRate ?? 0m,
                ValuationMethod = item.ValuationMethod
            });
        }

        return tb.Count > 0 || stock.Count > 0;
    }

    private void LoadPeriod(long clientId, string fy, ObservableCollection<FinancialStatementLedgerRow> tb,
        ObservableCollection<FinancialStatementStockRow> stock, ObservableCollection<FinancialStatementLedgerRow> debtors,
        ObservableCollection<FinancialStatementLedgerRow> creditors, bool neutralizeCurrentNceProfitLoss)
    {
        tb.Clear(); stock.Clear(); debtors.Clear(); creditors.Clear();
        if (string.IsNullOrWhiteSpace(fy)) return;
        IReadOnlyList<MasterRecord> masters;
        try { masters = _masterRepository.Get(clientId, fy, includeArchived: false); }
        catch { masters = []; }
        var ledgers = masters.Where(x => x.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)).ToArray();
        var groupParents = masters
            .Where(x => x.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last().Parent ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var totals = new Dictionary<string, LedgerTotalSnapshot>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in _ledgerTotals.GetAll(clientId, fy)) totals[item.LedgerName] = item;
        }
        catch { }
        var periodRows = new List<FinancialStatementLedgerRow>();
        var sourceOrder = 0;
        foreach (var ledger in ledgers)
        {
            totals.TryGetValue(ledger.Name, out var total);
            var opening = total?.OpeningBalance ?? ledger.OpeningBalance ?? 0m;
            var debit = total?.TotalDebit ?? 0m;
            var credit = total?.TotalCredit ?? 0m;
            var closing = total?.ClosingBalance ?? ledger.OpeningBalance ?? 0m;

            // R9 FIXED15: this is presentation authority only. Tally's official NCE
            // FSforNCE.xlsm export shows the CURRENT-YEAR standard Profit & Loss A/c row
            // as zero even when Tally's Balance Sheet displays the current-period result
            // (for the verified sample, a 739,975.90 loss on the asset side). The same
            // workbook independently recomputes that result from the P&L lines. Keeping
            // the live Tally P&L balance here as well would double-count the current result
            // once FIXED14 binds final P&L into the owner/partner capital movement.
            if (neutralizeCurrentNceProfitLoss &&
                FinancialStatementTrialBalanceOrder.IsProfitLossAccountLedger(ledger.Name))
            {
                opening = 0m;
                debit = 0m;
                credit = 0m;
                closing = 0m;
            }

            periodRows.Add(new FinancialStatementLedgerRow
            {
                Ledger = ledger.Name,
                ParentGroup = ledger.Parent ?? string.Empty,
                PrimaryGroup = FinancialStatementTrialBalanceOrder.ResolvePrimaryGroup(ledger.Parent ?? string.Empty, groupParents),
                SourceOrder = sourceOrder++,
                MasterId = ledger.TallyMasterId,
                Opening = opening,
                Debit = debit,
                Credit = credit,
                Closing = closing
            });
        }
        foreach (var row in FinancialStatementTrialBalanceOrder.Order(periodRows))
        {
            tb.Add(row);
            var grouping = $"{row.ParentGroup} {row.PrimaryGroup}";
            if (ContainsGroup(grouping, "Sundry Debtors")) { row.ConfigureScheduleContext("Debtor"); debtors.Add(row); }
            if (ContainsGroup(grouping, "Sundry Creditors")) { row.ConfigureScheduleContext("Creditor"); creditors.Add(row); }
        }
        try
        {
            foreach (var item in _stockRepository.GetStockItems(clientId, fy))
                stock.Add(new FinancialStatementStockRow
                {
                    Item = item.Name,
                    Group = item.StockGroup,
                    Category = item.StockCategory,
                    Unit = item.BaseUnit,
                    HsnSac = item.HsnSac,
                    GstRate = item.GstRate ?? 0m,
                    ValuationMethod = item.ValuationMethod
                });
        }
        catch { }
    }

    private void LoadBasicInfo(ClientProfile client)
    {
        var key = StateKey(client.Id, client.FinancialYear);
        FinancialStatementBasicInfo? saved = null;
        var savedBasicInfoJson = _preferences.Get(key);
        try { saved = JsonSerializer.Deserialize<FinancialStatementBasicInfo>(savedBasicInfoJson); } catch { }
        BasicInfo = saved ?? new FinancialStatementBasicInfo();

        // R52 FIXED2 migration: before this release Gross Block was the silent default, while
        // selecting Net Block required an explicit user action. Preserve a legacy saved Net Block
        // as user intent. Legacy Gross Block remains Auto so a unique proved Net source can repair
        // the old default mismatch shown by the R52 reconciliation message.
        if (!savedBasicInfoJson.Contains("PpeBlockSelectionOrigin", StringComparison.OrdinalIgnoreCase))
            BasicInfo.PpeBlockSelectionOrigin = BasicInfo.PpeBlock.Equals("Net Block", StringComparison.OrdinalIgnoreCase)
                ? "User"
                : "Auto";
        if (BasicInfo.EntityType.StartsWith("NCE -", StringComparison.OrdinalIgnoreCase)) BasicInfo.EntityType = "NCE";
        if (BasicInfo.NceType.Equals("Individual / Proprietorship", StringComparison.OrdinalIgnoreCase)) BasicInfo.NceType = "Individual";
        if (!FinancialStatementWorkbookRules.RoundOffChoices.Contains(BasicInfo.Rounding, StringComparer.OrdinalIgnoreCase)) BasicInfo.Rounding = "Default";
        if (!FinancialStatementWorkbookRules.PpeBlockChoices.Contains(BasicInfo.PpeBlock, StringComparer.OrdinalIgnoreCase)) BasicInfo.PpeBlock = "Gross Block";
        if (!FinancialStatementWorkbookRules.DebtorCreditorDetailChoices.Contains(BasicInfo.DebtorCreditorDetail, StringComparer.OrdinalIgnoreCase)) BasicInfo.DebtorCreditorDetail = "Party-wise";
        if (!FinancialStatementWorkbookRules.StockDetailChoices.Contains(BasicInfo.StockDetail, StringComparer.OrdinalIgnoreCase)) BasicInfo.StockDetail = "Item-wise";
        if (IsNceEntityType && !string.IsNullOrWhiteSpace(BasicInfo.NceType) &&
            !FinancialStatementWorkbookRules.NceEntityTypes.Contains(BasicInfo.NceType, StringComparer.OrdinalIgnoreCase)) BasicInfo.NceType = string.Empty;
        Signatories.Clear();
        foreach (var row in BasicInfo.Signatories ?? []) Signatories.Add(row);
        if (Signatories.Count == 0 && !string.IsNullOrWhiteSpace(BasicInfo.SignatoryName))
            Signatories.Add(new EntitySignatoryRow { Name = BasicInfo.SignatoryName, Designation = BasicInfo.SignatoryDesignation, Din = BasicInfo.Din, Place = BasicInfo.SigningPlace, SigningDate = BasicInfo.SigningDate });
        // Original Basic Info reserves rows 21:27 for entity signatories.
        while (Signatories.Count < 7) Signatories.Add(new EntitySignatoryRow());
        OwnerPartners.Clear();
        foreach (var row in BasicInfo.OwnerPartners ?? []) OwnerPartners.Add(row);
        while (OwnerPartners.Count < 11) OwnerPartners.Add(new OwnerPartnerRow());
        ShareCapitalDetails.Clear();
        foreach (var row in BasicInfo.ShareCapitalDetails ?? []) ShareCapitalDetails.Add(row);
        while (ShareCapitalDetails.Count < 11) ShareCapitalDetails.Add(new ShareCapitalDetailRow());
        BasicInfo.EntityName = FirstNonBlank(BasicInfo.EntityName, client.LegalName, client.TradeName, client.ClientName);
        BasicInfo.CurrentYearCompanyName = FirstNonBlank(BasicInfo.CurrentYearCompanyName, client.ClientName, client.TradeName, client.LegalName, BasicInfo.EntityName);
        BasicInfo.PreviousYearCompanyName = FirstNonBlank(BasicInfo.PreviousYearCompanyName, BasicInfo.PreviousYearTallyCompany, BasicInfo.CurrentYearCompanyName);
        BasicInfo.MailingName = FirstNonBlank(BasicInfo.MailingName, client.TradeName, client.ClientName);
        BasicInfo.Address = FirstNonBlank(BasicInfo.Address, client.Address);
        BasicInfo.Pan = FirstNonBlank(BasicInfo.Pan, client.Pan);
        BasicInfo.Gstin = FirstNonBlank(BasicInfo.Gstin, client.Gstin);
        BasicInfo.CurrentYearEnd = FinancialYearEnd(client.FinancialYear);
        BasicInfo.PreviousYearEnd = FinancialYearEnd(PreviousFinancialYear(client.FinancialYear));
        if (!string.IsNullOrWhiteSpace(BasicInfo.CaName))
            SelectedCaProfile = CaProfiles.FirstOrDefault(x => x.CaName.Equals(BasicInfo.CaName, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(BasicInfo));
        OnPropertyChanged(nameof(PpeBlockSelection));
        OnPropertyChanged(nameof(EntityTypeSelection));
        OnPropertyChanged(nameof(IsNceEntityType));
        OnPropertyChanged(nameof(IsCompanyScheduleIIIEntityType));
        OnPropertyChanged(nameof(RegistrationIdLabel));
    }

    private void SaveBasicInfo()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        if (IsCompanyScheduleIIIEntityType)
        {
            var incompleteShare = ShareCapitalDetails.FirstOrDefault(x =>
                string.IsNullOrWhiteSpace(x.TypeOfShares) != (x.FaceValue == 0m));
            if (incompleteShare is not null || ShareCapitalDetails.Any(x => !string.IsNullOrWhiteSpace(x.TypeOfShares) && x.FaceValue <= 0m))
            {
                StatusText = "Complete both Type of Shares and a positive Face Value for each Share Capital Details row before saving.";
                return;
            }
        }
        var firstSignatory = Signatories.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Name));
        if (firstSignatory is not null)
        {
            BasicInfo.SignatoryName = firstSignatory.Name;
            BasicInfo.SignatoryDesignation = firstSignatory.Designation;
            BasicInfo.Din = firstSignatory.Din;
            BasicInfo.SigningPlace = string.IsNullOrWhiteSpace(firstSignatory.Place) ? BasicInfo.SigningPlace : firstSignatory.Place;
            BasicInfo.SigningDate = string.IsNullOrWhiteSpace(firstSignatory.SigningDate) ? BasicInfo.SigningDate : firstSignatory.SigningDate;
        }
        BasicInfo.Signatories = Signatories.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Clone()).ToList();
        BasicInfo.OwnerPartners = OwnerPartners.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Clone()).ToList();
        BasicInfo.ShareCapitalDetails = ShareCapitalDetails.Where(x => !string.IsNullOrWhiteSpace(x.TypeOfShares) || x.FaceValue != 0m).Select(x => x.Clone()).ToList();
        _preferences.Set(StateKey(client.Id, client.FinancialYear), JsonSerializer.Serialize(BasicInfo));

        // R32: Party-wise/Bill-wise and Item-wise/Ledger-wise are source-mode selectors,
        // not cosmetic labels. Re-project the last-good exact plug-in feed immediately so
        // changing either selector updates the visible tabs without another live Tally call.
        var reprojectedNceFeed = IsNceEntityType && TryRestoreNceFeedSnapshot(client.Id, client.FinancialYear);
        if (reprojectedNceFeed)
        {
            var mappings = LoadMappings(client.Id, client.FinancialYear);
            ApplyMappings(mappings, FyTrialBalance, FyStock);
            ApplyMappings(mappings, PyTrialBalance, PyStock);
            ApplyScheduleMappings(mappings, FyDebtors.Concat(FyCreditors), "FY");
            ApplyScheduleMappings(mappings, PyDebtors.Concat(PyCreditors), "PY");
            SynchronizePartyScheduleAuthority();
            RefreshMappingViews();
        }
        SynchronizePpePresentationToUniqueSourceAuthority();
        RebuildTemplatePresentation();
        UpdatePreparedFinancialStatementState();
        StatusText = reprojectedNceFeed
            ? "Financial Statements Basic Info saved. NCE Stock/Debtors/Creditors were re-projected from the saved exact plug-in XML feed for the selected report-detail modes."
            : "Financial Statements Basic Info saved for this Client/FY. Owner/partner names refreshed in Capital/Current A/c mapping choices.";
    }

    private static bool IsNetPpePresentation(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains("PPE (Net)", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("PPE & IA (Net)", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Intangible Assets (Net)", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGrossPpePresentation(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains("PPE (Gross)", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Intangible Assets (Gross)", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAccumulatedPpePresentation(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains("PPE (Accumulated Depreciation)", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Intangible Assets (Accumulated Depreciation)", StringComparison.OrdinalIgnoreCase);
    }

    private bool SynchronizePpePresentationToUniqueSourceAuthority()
    {
        if (!IsNceEntityType) return false;

        // Once the user explicitly chooses Gross/Net, that selection is authoritative and the
        // R52 fail-closed source reconciliation remains in force. Legacy/default Basic Info did
        // not record whether Gross Block was intentionally selected, so R52 FIXED2 lets a single,
        // unambiguous mapped source basis initialise the presentation selector instead.
        if (BasicInfo.PpeBlockSelectionOrigin.Equals("User", StringComparison.OrdinalIgnoreCase))
            return false;

        var ppeRows = FyTrialBalance.Concat(PyTrialBalance)
            .Where(x => x.HasResolvedMapping && x.Note.Equals("13", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var hasNet = ppeRows.Any(x => IsNetPpePresentation(x.Classification3));
        var hasGross = ppeRows.Any(x => IsGrossPpePresentation(x.Classification3));

        // Mixed Gross/Net evidence is never guessed. Keep the current selection so the existing
        // source-authority reconciliation can explain the conflict.
        if (hasNet == hasGross) return false;

        var provedBasis = hasNet ? "Net Block" : "Gross Block";
        if (BasicInfo.PpeBlock.Equals(provedBasis, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(BasicInfo.PpeBlockSelectionOrigin))
                BasicInfo.PpeBlockSelectionOrigin = "Auto Source";
            return false;
        }

        BasicInfo.PpeBlock = provedBasis;
        BasicInfo.PpeBlockSelectionOrigin = "Auto Source";
        OnPropertyChanged(nameof(PpeBlockSelection));
        OnPropertyChanged(nameof(BasicInfo));

        var client = _clientContext.CurrentClient;
        if (client is not null)
            _preferences.Set(StateKey(client.Id, client.FinancialYear), JsonSerializer.Serialize(BasicInfo));
        return true;
    }

    private void RecalculateAutomaticPpeMappingsForSelection(string selectedBasis)
    {
        if (!IsNceEntityType) return;

        static bool IsGenericAutomatic(FinancialStatementLedgerRow row)
            => row.MappingOrigin.Equals("Auto Mapped", StringComparison.OrdinalIgnoreCase);

        string? TargetClass3(FinancialStatementLedgerRow row)
        {
            var current = row.Classification3 ?? string.Empty;
            if (IsAccumulatedPpePresentation(current)) return null;
            if (current.Contains("Intangible Assets", StringComparison.OrdinalIgnoreCase))
                return selectedBasis.Equals("Net Block", StringComparison.OrdinalIgnoreCase)
                    ? "Intangible Assets (Net)"
                    : "Intangible Assets (Gross)";
            if (IsNetPpePresentation(current) || IsGrossPpePresentation(current))
                return selectedBasis.Equals("Net Block", StringComparison.OrdinalIgnoreCase)
                    ? "PPE (Net)"
                    : "PPE (Gross)";
            return null;
        }

        foreach (var row in FyTrialBalance.Concat(PyTrialBalance).Where(IsGenericAutomatic))
        {
            var target = TargetClass3(row);
            if (string.IsNullOrWhiteSpace(target) || target.Equals(row.Classification3, StringComparison.OrdinalIgnoreCase))
                continue;

            row.SetPartialHierarchy(
                row.Classification1,
                row.Classification2,
                target,
                row.Classification4,
                row.Classification5,
                "Auto Mapped");
        }

        RefreshMappingViews();
    }

    private void SeedOwnerPartnersFromCapitalLedgersWhenBlank()
    {
        if (!IsNceEntityType) return;

        // FIXED8: the original workbook Owner/Partners table is the only capital C3 authority.
        // Older R9 builds incorrectly mined every person-like Capital ledger and wrote those
        // names back into Basic Info. For an Individual there can only be one proprietor, so
        // repair that legacy pollution when multiple blank-detail rows exactly match the old
        // ledger-derived candidate set. Partnerships/HUFs/LLPs are never rewritten here.
        var populated = OwnerPartners.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToArray();
        if (BasicInfo.NceType.Equals("Individual", StringComparison.OrdinalIgnoreCase) && populated.Length > 1 &&
            populated.All(x => string.IsNullOrWhiteSpace(x.Designation) && x.ShareOfProfitLoss == 0m))
        {
            var legacyCandidates = FyTrialBalance
                .Select(x => FinancialStatementTrialBalanceOrder.OwnerNameCandidate(x.Ledger, x.ParentGroup, x.PrimaryGroup))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (populated.All(x => legacyCandidates.Contains(x.Name.Trim())))
            {
                foreach (var row in OwnerPartners) { row.Name = string.Empty; row.Designation = string.Empty; row.ShareOfProfitLoss = 0m; }
                populated = Array.Empty<OwnerPartnerRow>();
            }
        }

        if (OwnerPartners.Any(x => !string.IsNullOrWhiteSpace(x.Name))) return;

        // Only an unambiguous Individual signatory may seed the proprietor row. Never seed from
        // ledger names, because Drawings/insurance/salary/other capital-group ledgers are not
        // proprietor identities.
        if (BasicInfo.NceType.Equals("Individual", StringComparison.OrdinalIgnoreCase))
        {
            var signatoryNames = Signatories.Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => x.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            if (signatoryNames.Length == 1 && OwnerPartners.Count > 0) OwnerPartners[0].Name = signatoryNames[0];
        }
        BasicInfo.OwnerPartners = OwnerPartners.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Clone()).ToList();
        OnPropertyChanged(nameof(BasicInfo));
    }

    private void LoadCaProfiles()
    {
        CaProfiles.Clear();
        try
        {
            var rows = JsonSerializer.Deserialize<List<CaProfileRow>>(_preferences.Get(SettingsViewModel.CaProfilesPreferenceKey)) ?? [];
            var migratedIdentity = false;
            foreach (var row in rows)
                if (string.IsNullOrWhiteSpace(row.ProfileId)) { row.ProfileId = Guid.NewGuid().ToString("N"); migratedIdentity = true; }
            if (migratedIdentity) _preferences.Set(SettingsViewModel.CaProfilesPreferenceKey, JsonSerializer.Serialize(rows));
            foreach (var row in rows.OrderBy(x => x.FirmName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.CaName, StringComparer.OrdinalIgnoreCase)) CaProfiles.Add(row);
        }
        catch { }
    }

    private void ApplyLinkedCaProfile(ClientProfile client)
    {
        try
        {
            var groups = ClientGroupingCatalog.Load(_preferences.Get(ClientGroupingCatalog.PreferenceKey));
            var profileId = ClientGroupingCatalog.FindLinkedCaProfileId(groups, client.Id);
            if (string.IsNullOrWhiteSpace(profileId)) return;
            var profile = CaProfiles.FirstOrDefault(x => x.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (profile is not null) SelectedCaProfile = profile;
        }
        catch
        {
            // Folder organisation must never block Financial Statements opening.
        }
    }

    private void BuildNoteList(string entityType)
    {
        Notes.Clear();
        var template = TemplatePath();
        foreach (var value in FinancialStatementWorkbookService.BuildNoteSelector(template, entityType)) Notes.Add(value);
        if (Notes.Count == 0) Notes.Add("All Notes");
        if (!Notes.Contains(SelectedNote)) SelectedNote = Notes[0];
    }

    private void RefreshNotePreview()
    {
        NotePreviewRows.Clear();
        var template = TemplatePath();
        foreach (var row in FinancialStatementWorkbookService.BuildNotePreview(template, BasicInfo.EntityType, SelectedNote, FyTrialBalance, PyTrialBalance))
            NotePreviewRows.Add(row);
    }

    private IReadOnlyList<FinancialStatementMappingPreference> LoadMappings(long clientId, string fy)
    {
        var profileRows = ReadMappings(ProfileMappingKey(clientId, fy));
        if (profileRows.Count > 0) return profileRows;

        // R15 R2 migration: R15/R15 R1 used one mapping bucket for every statement
        // profile. Reuse only legacy rows that are valid for the currently selected
        // profile, then future saves stay isolated under that profile key.
        return ReadMappings(LegacyMappingKey(clientId, fy))
            .Where(x => x.Kind.Equals("Stock", StringComparison.OrdinalIgnoreCase) || IsLegacyMappingCompatibleWithActiveProfile(x))
            .ToArray();
    }

    private bool IsLegacyMappingCompatibleWithActiveProfile(FinancialStatementMappingPreference item)
    {
        if (!item.Kind.Equals("Ledger", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(item.MappingKey)) return false;
        var candidates = _mappingCatalog.Where(x => x.MappingKey.Equals(item.MappingKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0) return false;
        // R15/R15 R1 persisted the visible C1/C2/C3 values. Require those values to
        // agree with the active profile as well as the key so a coincidentally reused
        // MappingKey from another statutory format cannot be migrated into this profile.
        if (string.IsNullOrWhiteSpace(item.FsHead) && string.IsNullOrWhiteSpace(item.NoteHead) && string.IsNullOrWhiteSpace(item.NoteSubHead)) return true;
        return candidates.Any(x =>
            x.FsHead.Equals(item.FsHead ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            x.NoteHead.Equals(item.NoteHead ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            x.NoteSubHead.Equals(item.NoteSubHead ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<FinancialStatementMappingPreference> ReadMappings(string key)
    {
        try { return JsonSerializer.Deserialize<List<FinancialStatementMappingPreference>>(_preferences.Get(key)) ?? []; }
        catch { return []; }
    }

    private void ApplyMappings(IReadOnlyList<FinancialStatementMappingPreference> saved, IEnumerable<FinancialStatementLedgerRow> ledgers, IEnumerable<FinancialStatementStockRow> stock)
    {
        var previousPeriod = ReferenceEquals(ledgers, PyTrialBalance);
        var allowAutomaticSuggestion = !previousPeriod;
        var period = previousPeriod ? "PY" : "FY";
        bool PeriodMatch(FinancialStatementMappingPreference x) => string.IsNullOrWhiteSpace(x.Period) || x.Period.Equals(period, StringComparison.OrdinalIgnoreCase);
        var ledgerById = saved.Where(x => x.Kind == "Ledger" && PeriodMatch(x) &&
                                          FinancialStatementPresentationExportService.IsUsableTallyMasterId(x.Identity) &&
                                          !x.Identity.Equals(x.Name, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Identity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var ledgerByName = saved.Where(x => x.Kind == "Ledger" && PeriodMatch(x) && x.Name.Length > 0)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var profile = FinancialStatementWorkbookService.ResolveProfileSheet(BasicInfo.EntityType);
        var ownerNames = OwnerMappingNames();

        void ApplyAutomaticOrUnmapped(FinancialStatementLedgerRow row)
        {
            if (allowAutomaticSuggestion) row.ApplyTrialSuggestion(FinancialStatementPresentationExportService.SuggestTrialHierarchy(row, BasicInfo.EntityType));
            else row.ClearMappingPresentation();
        }

        foreach (var row in ledgers)
        {
            row.ConfigureMappingCatalog(_mappingCatalog, profile, ownerNames);
            // R39: preserve fresh NCE MappingHeadOne/MappingHeadTwo evidence. Earlier builds
            // populated the plug-in hierarchy first, then immediately replaced it here with
            // a generic Tally-group suggestion or a stale copied mapping. The live plug-in is
            // the primary automatic mapping authority; only a deliberate direct User Mapped
            // choice may override it for an ordinary ledger. Standard Profit & Loss A/c stays
            // on exact plug-in authority whenever the plug-in returned a hierarchy.
            var hasFreshNceHierarchy = IsNceEntityType &&
                row.MappingOrigin.StartsWith("Tally NCE", StringComparison.OrdinalIgnoreCase) &&
                row.HasResolvedMapping;
            FinancialStatementMappingPreference? item = null;
            if (FinancialStatementPresentationExportService.IsUsableTallyMasterId(row.MasterId)) ledgerById.TryGetValue(row.MasterId.Trim(), out item);
            if (item is null) ledgerByName.TryGetValue(row.Ledger.Trim(), out item);

            if (item is null)
            {
                if (hasFreshNceHierarchy) continue;
                ApplyAutomaticOrUnmapped(row);
                continue;
            }

            var directUserOverride = item.MappingOrigin.Equals("User Mapped", StringComparison.OrdinalIgnoreCase);
            if (hasFreshNceHierarchy &&
                (!directUserOverride || FinancialStatementTrialBalanceOrder.IsProfitLossAccountLedger(row.Ledger)))
            {
                // Automatic/Copied/legacy preferences cannot displace current plug-in mapping.
                // For the standard P&L bridge, current plug-in hierarchy also supersedes an
                // older direct mapping so the row cannot remain stuck on a stale fallback.
                continue;
            }

            row.RestoreNatureOverride(item.NatureOverride);
            row.AmountSign = string.IsNullOrWhiteSpace(item.AmountSign) ? "Auto" : item.AmountSign;
            row.NonCurrentAmount = item.NonCurrentAmount;
            var migratedLegacySchedule = row.RestoreScheduleMappingWithLegacy(item.ScheduleClass1, item.ScheduleClass2, item.Class5);
            if (migratedLegacySchedule)
            {
                row.SetPartialHierarchy(item.Class1, item.Class2, item.Class3, item.Class4, string.Empty,
                    string.IsNullOrWhiteSpace(item.MappingOrigin) ? "User Mapped" : item.MappingOrigin);
                continue;
            }

            var explicitUserMapping = item.MappingOrigin.Equals("User Mapped", StringComparison.OrdinalIgnoreCase) ||
                                      item.MappingOrigin.StartsWith("Copied from ", StringComparison.OrdinalIgnoreCase);

            // R39/R25: if the live plug-in did not supply a hierarchy for the standard Tally
            // Profit & Loss A/c, retain the verified workbook fallback. Fresh plug-in hierarchy
            // was already protected above and therefore never reaches this fallback.
            if (FinancialStatementTrialBalanceOrder.IsProfitLossAccountLedger(row.Ledger) &&
                !item.MappingOrigin.Equals("User Mapped", StringComparison.OrdinalIgnoreCase))
            {
                row.ApplyTrialSuggestion(FinancialStatementPresentationExportService.SuggestTrialHierarchy(row, BasicInfo.EntityType));
                continue;
            }

            // PY is comparative authority. Only an explicit saved PY mapping or exact-ledger
            // FY/PY carry-forward may classify it; legacy/automatic/Tally-derived unmatched PY
            // rows remain visibly Unmapped as requested. Nature overrides remain preserved.
            if (previousPeriod && !explicitUserMapping)
            {
                row.ClearMappingPresentation();
                continue;
            }

            // FIXED8: re-evaluate historical automatic FY mapping against the current workbook.
            if (item.MappingOrigin.Equals("Auto Mapped", StringComparison.OrdinalIgnoreCase))
            {
                ApplyAutomaticOrUnmapped(row);
                continue;
            }

            var legacyBlankOwnerC3 = profile.Equals("NCE", StringComparison.OrdinalIgnoreCase) &&
                item.Class3.Equals("Owner/Partners Capital Account", StringComparison.OrdinalIgnoreCase) &&
                (item.Class4.Equals("Capital Account", StringComparison.OrdinalIgnoreCase) ||
                 item.Class4.Equals("Current Account", StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(item.Class5) ||
                 item.Class5.StartsWith("Capital A/c ", StringComparison.OrdinalIgnoreCase) ||
                 item.Class5.StartsWith("Current A/c ", StringComparison.OrdinalIgnoreCase));
            if (legacyBlankOwnerC3)
            {
                var ownerDetail = row.ResolveOwnerThirdLevelFromLedger(item.Class3, item.Class4);
                row.SetPartialHierarchy(item.Class1, item.Class2, item.Class3, item.Class4, ownerDetail,
                    string.IsNullOrWhiteSpace(item.MappingOrigin) ? "User Mapped" : item.MappingOrigin);
                continue;
            }

            FinancialStatementMappingOption? option = null;
            if (!string.IsNullOrWhiteSpace(item.MappingKey))
            {
                // Restore the saved visible hierarchy first via a stable identity; never by shared MappingKey alone.
                // R15 R12: stable hierarchy identity is independent from the visible owner/partner label.
                // This prevents a shared MappingKey from jumping to the first catalog row when saved H1-H4 are reloaded.
                var stableId = item.StableHierarchyId;
                if (string.IsNullOrWhiteSpace(stableId) &&
                    (!string.IsNullOrWhiteSpace(item.Class1) || !string.IsNullOrWhiteSpace(item.Class4)))
                    stableId = FinancialStatementClassificationService.StableHierarchyId(
                        item.MappingKey, item.Class1, item.Class2, item.Class3, item.Class4, item.Class5);
                if (!string.IsNullOrWhiteSpace(stableId))
                    option = _mappingCatalog.FirstOrDefault(x => x.StableHierarchyId.Equals(stableId, StringComparison.OrdinalIgnoreCase));

                option ??= _mappingCatalog.FirstOrDefault(x =>
                    x.MappingKey.Equals(item.MappingKey, StringComparison.OrdinalIgnoreCase) &&
                    x.Class1.Equals(item.Class1 ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                    x.Class2.Equals(item.Class2 ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                    x.Class3.Equals(item.Class3 ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                    x.Class4.Equals(item.Class4 ?? string.Empty, StringComparison.OrdinalIgnoreCase));

                var keyMatches = _mappingCatalog.Where(x => x.MappingKey.Equals(item.MappingKey, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (option is null && keyMatches.Length == 1) option = keyMatches[0];
            }
            if (string.IsNullOrWhiteSpace(item.MappingOrigin) && FinancialStatementPresentationExportService.ShouldReReviewLegacyMapping(item.MappingKey))
                option = null;

            var explicitUserHasVisibleHierarchy = !string.IsNullOrWhiteSpace(item.Class3) ||
                                                  !string.IsNullOrWhiteSpace(item.Class4) ||
                                                  !string.IsNullOrWhiteSpace(item.Class5);
            var explicitUserVisiblePathIsValid = explicitUserMapping &&
                FinancialStatementWorkbookRules.IsValidVisiblePath(profile, item.Class3, item.Class4, item.Class5);
            if (explicitUserVisiblePathIsValid)
            {
                row.SetPartialHierarchy(item.Class1, item.Class2, item.Class3, item.Class4, item.Class5,
                    string.IsNullOrWhiteSpace(item.MappingOrigin) ? "User Mapped" : item.MappingOrigin);
                if (row.HasResolvedMapping) continue;
            }

            if (option is not null &&
                (!explicitUserMapping || !explicitUserHasVisibleHierarchy) &&
                !FinancialStatementPresentationExportService.IsObviouslyIncompatibleHierarchy(row, item.Class1, item.Class2, item.Class3, item.Class4, profile) &&
                !FinancialStatementPresentationExportService.IsObviouslyIncompatibleMapping(row, option.MappingKey, profile))
            {
                row.SetMappingOption(option,
                    string.IsNullOrWhiteSpace(item.MappingOrigin) ? "User Mapped" : item.MappingOrigin,
                    item.Class5);
                continue;
            }

            if (!item.MappingOrigin.Equals("User Mapped", StringComparison.OrdinalIgnoreCase) &&
                !item.MappingOrigin.StartsWith("Copied from ", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(item.MappingKey) && string.IsNullOrWhiteSpace(item.Class4))
            {
                ApplyAutomaticOrUnmapped(row);
                continue;
            }

            if (!FinancialStatementLedgerRow.IsHierarchySequenceContiguous(item.Class1, item.Class2, item.Class3, item.Class4, item.Class5))
            {
                ApplyAutomaticOrUnmapped(row);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.Class1) || !string.IsNullOrWhiteSpace(item.Class2) ||
                !string.IsNullOrWhiteSpace(item.Class3) || !string.IsNullOrWhiteSpace(item.Class4))
            {
                if (!explicitUserMapping &&
                    FinancialStatementPresentationExportService.IsObviouslyIncompatibleHierarchy(row, item.Class1, item.Class2, item.Class3, item.Class4, profile))
                {
                    ApplyAutomaticOrUnmapped(row);
                    continue;
                }
                row.SetPartialHierarchy(item.Class1, item.Class2, item.Class3, item.Class4, item.Class5,
                    string.IsNullOrWhiteSpace(item.MappingOrigin) ? "User Mapped" : item.MappingOrigin);
                if (!explicitUserMapping && !string.IsNullOrWhiteSpace(row.Mapping) &&
                    FinancialStatementPresentationExportService.IsObviouslyIncompatibleMapping(row, row.Mapping, profile))
                    ApplyAutomaticOrUnmapped(row);
                continue;
            }

            ApplyAutomaticOrUnmapped(row);
        }

        var stockByName = saved.Where(x => x.Kind == "Stock" && PeriodMatch(x) && x.Name.Length > 0)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in stock)
        {
            if (!stockByName.TryGetValue(row.Item.Trim(), out var item)) continue;
            var directUserOverride = item.MappingOrigin.Equals("User Mapped", StringComparison.OrdinalIgnoreCase);
            if (IsNceEntityType && row.MappingOrigin.StartsWith("Tally NCE", StringComparison.OrdinalIgnoreCase) && !directUserOverride)
                continue;
            row.RestoreMapping(item.MappingKey, string.IsNullOrWhiteSpace(item.MappingOrigin) ? "User Mapped" : item.MappingOrigin);
        }
    }

    private void ApplyScheduleMappings(IReadOnlyList<FinancialStatementMappingPreference> saved, IEnumerable<FinancialStatementLedgerRow> scheduleRows, string period)
    {
        bool PeriodMatch(FinancialStatementMappingPreference x) => string.IsNullOrWhiteSpace(x.Period) || x.Period.Equals(period, StringComparison.OrdinalIgnoreCase);
        var byId = saved.Where(x => x.Kind.Equals("Schedule", StringComparison.OrdinalIgnoreCase) && PeriodMatch(x) &&
                                    FinancialStatementPresentationExportService.IsUsableTallyMasterId(x.Identity))
            .GroupBy(x => x.Identity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var byName = saved.Where(x => x.Kind.Equals("Schedule", StringComparison.OrdinalIgnoreCase) && PeriodMatch(x) && x.Name.Length > 0)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in scheduleRows)
        {
            FinancialStatementMappingPreference? item = null;
            if (FinancialStatementPresentationExportService.IsUsableTallyMasterId(row.MasterId)) byId.TryGetValue(row.MasterId.Trim(), out item);
            if (item is null) byName.TryGetValue(row.Ledger.Trim(), out item);
            if (item is null) continue;

            // R39: the exact NCE plug-in C1/C2 is authoritative over automatic, copied and
            // legacy schedule saves. Only a deliberate direct user selection may override a
            // fresh Tally NCE classification. A copied value remains valid when no fresh plug-in
            // classification exists, but it never displaces current-year/current-source evidence.
            var directUserScheduleOverride = item.MappingOrigin.Equals("User Mapped", StringComparison.OrdinalIgnoreCase);
            if (IsNceEntityType && row.ScheduleMappingOrigin.StartsWith("Tally NCE", StringComparison.OrdinalIgnoreCase) &&
                row.HasCompleteScheduleMapping && !directUserScheduleOverride)
                continue;

            row.RestoreScheduleMapping(item.ScheduleClass1, item.ScheduleClass2,
                string.IsNullOrWhiteSpace(item.MappingOrigin) ? "User Mapped" : item.MappingOrigin);
        }
    }

    private void SaveMappings()
    {
        SynchronizePartyScheduleAuthority();
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var rows = new List<FinancialStatementMappingPreference>();
        var mappedCount = 0;
        void AddLedgerRows(IEnumerable<FinancialStatementLedgerRow> sourceRows, string period)
        {
            foreach (var row in sourceRows)
            {
                if (row.HasResolvedMapping && row.HasCompleteScheduleMapping) mappedCount++;
                var hasHierarchy = !string.IsNullOrWhiteSpace(row.Classification1) || !string.IsNullOrWhiteSpace(row.Classification2) ||
                                   !string.IsNullOrWhiteSpace(row.Classification3) || !string.IsNullOrWhiteSpace(row.Classification4) ||
                                   !string.IsNullOrWhiteSpace(row.Classification5);
                var hasSchedule = !string.IsNullOrWhiteSpace(row.ScheduleClass1) || !string.IsNullOrWhiteSpace(row.ScheduleClass2);
                if (!string.IsNullOrWhiteSpace(row.Mapping) || hasHierarchy || hasSchedule || !string.IsNullOrWhiteSpace(row.NatureOverride))
                {
                    rows.Add(new FinancialStatementMappingPreference(
                        "Ledger", MappingIdentity(row), row.Ledger.Trim(), row.Mapping.Trim(), row.Note.Trim(), row.AmountSign,
                        row.FsHead, row.NoteHead, row.NoteSubHead,
                        row.Classification1, row.Classification2, row.Classification3, row.Classification4, row.Classification5,
                        row.MappingOrigin, row.NonCurrentAmount, row.CanonicalMappingId, null, row.StableHierarchyId,
                        period, row.ScheduleClass1, row.ScheduleClass2, row.NatureOverride));
                }
            }
        }
        AddLedgerRows(FyTrialBalance, "FY");
        AddLedgerRows(PyTrialBalance, "PY");

        void AddScheduleRows(IEnumerable<FinancialStatementLedgerRow> sourceRows, string period)
        {
            foreach (var row in sourceRows)
            {
                if (!row.HasScheduleContext) continue;
                rows.Add(new FinancialStatementMappingPreference(
                    "Schedule", MappingIdentity(row), row.Ledger.Trim(), string.Empty, string.Empty, "Auto",
                    MappingOrigin: row.ScheduleMappingOrigin, Period: period, ScheduleClass1: row.ScheduleClass1, ScheduleClass2: row.ScheduleClass2));
            }
        }
        AddScheduleRows(FyDebtors.Concat(FyCreditors), "FY");
        AddScheduleRows(PyDebtors.Concat(PyCreditors), "PY");

        foreach (var row in FyStock)
            if (!string.IsNullOrWhiteSpace(row.Mapping))
                rows.Add(new FinancialStatementMappingPreference("Stock", row.Item.Trim(), row.Item.Trim(), row.Mapping.Trim(), string.Empty, "Auto", MappingOrigin: row.MappingOrigin, Period: "FY"));
        foreach (var row in PyStock)
            if (!string.IsNullOrWhiteSpace(row.Mapping))
                rows.Add(new FinancialStatementMappingPreference("Stock", row.Item.Trim(), row.Item.Trim(), row.Mapping.Trim(), string.Empty, "Auto", MappingOrigin: row.MappingOrigin, Period: "PY"));
        _preferences.Set(ProfileMappingKey(client.Id, client.FinancialYear), JsonSerializer.Serialize(rows));

        // Save is deliberately non-destructive. Reapplying the PY restore path here used to
        // clear live comparative mappings whose origin was automatic/copied, making the grid
        // look blank until Refresh rebuilt the cross-year state. Persist exactly what is on
        // screen and leave every live FY/PY row intact.
        RefreshMappingViews();
        RefreshNotePreview();
        UpdatePreparedFinancialStatementState();
        var partial = FyTrialBalance.Count(x => (!HideZeroClosingBalance || x.Closing != 0m) && !x.HasResolvedMapping);
        var schedulePending = FyTrialBalance.Count(x => (!HideZeroClosingBalance || x.Closing != 0m) && x.HasScheduleContext && !x.HasCompleteScheduleMapping);
        StatusText = $"Financial-statement mappings saved · {mappedCount} fully ready · {partial} TB row(s) still require classification" +
                     (schedulePending > 0 ? $" · {schedulePending} party schedule row(s) still require classification." : ".");
    }

    private static string NceFeedSnapshotKey(long clientId, string fy) => $"FinancialStatements.NceFeedSnapshot.{clientId}.{fy.Trim()}";

    private void SaveNceFeedSnapshot(long clientId, string fy, NceFinancialStatementFeed feed)
    {
        // Never replace the last-good exact XML authority with an empty/partial response.
        if (!HasUsableNceFeed(feed)) return;
        try
        {
            var snapshot = new NceFinancialStatementFeedSnapshot
            {
                Feed = feed,
                SavedUtc = DateTime.UtcNow
            };
            var key = NceFeedSnapshotKey(clientId, fy);
            lock (PreparedSnapshotGate) PreparedNceSnapshots[key] = snapshot;
            _preferences.Set(key, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // Live Tally data must remain usable even if persistence is temporarily unavailable.
            // The prior last-good snapshot is left untouched.
        }
    }

    private bool TryRestoreNceFeedSnapshot(long clientId, string fy)
    {
        try
        {
            var key = NceFeedSnapshotKey(clientId, fy);
            NceFinancialStatementFeedSnapshot? snapshot;
            lock (PreparedSnapshotGate) PreparedNceSnapshots.TryGetValue(key, out snapshot);
            if (snapshot is null)
            {
                var json = _preferences.Get(key);
                if (string.IsNullOrWhiteSpace(json)) return false;
                snapshot = JsonSerializer.Deserialize<NceFinancialStatementFeedSnapshot>(json);
                if (snapshot is not null)
                    lock (PreparedSnapshotGate) PreparedNceSnapshots[key] = snapshot;
            }
            if (snapshot?.Feed is null || !HasUsableNceFeed(snapshot.Feed)) return false;
            if (!string.IsNullOrWhiteSpace(snapshot.Feed.FinancialYear) &&
                !snapshot.Feed.FinancialYear.Equals(fy, StringComparison.OrdinalIgnoreCase)) return false;
            PopulateNcePeriod(snapshot.Feed.CurrentYear, "CY", snapshot.Feed.DetailCollections, FyTrialBalance, FyStock, FyDebtors, FyCreditors);
            PopulateNcePeriod(snapshot.Feed.PreviousYear, "PY", snapshot.Feed.DetailCollections, PyTrialBalance, PyStock, PyDebtors, PyCreditors);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasUsableNceFeed(NceFinancialStatementFeed feed)
        => feed.CurrentYear.TrialBalance.Count > 0 || feed.PreviousYear.TrialBalance.Count > 0 ||
           feed.CurrentYear.StockSummary.Count > 0 || feed.PreviousYear.StockSummary.Count > 0 ||
           feed.CurrentYear.TradeReceivables.Count > 0 || feed.CurrentYear.TradePayables.Count > 0 ||
           feed.PreviousYear.TradeReceivables.Count > 0 || feed.PreviousYear.TradePayables.Count > 0;

    // R52 FIXED13: authority-only restore. This deliberately does not call PopulateNcePeriod,
    // so existing prepared rows, explicit FY/PY mappings and schedule classifications remain
    // byte-for-byte/user-for-user as prepared. It only restores the hidden official period totals
    // required by Note 17/24 and Note 10/18 accounting.
    private void RehydrateNceSourceAuthorityFromSavedFeed(long clientId, string fy)
    {
        try
        {
            var key = NceFeedSnapshotKey(clientId, fy);
            NceFinancialStatementFeedSnapshot? snapshot;
            lock (PreparedSnapshotGate) PreparedNceSnapshots.TryGetValue(key, out snapshot);
            if (snapshot is null)
            {
                var json = _preferences.Get(key);
                if (string.IsNullOrWhiteSpace(json)) return;
                snapshot = JsonSerializer.Deserialize<NceFinancialStatementFeedSnapshot>(json);
                if (snapshot is not null)
                    lock (PreparedSnapshotGate) PreparedNceSnapshots[key] = snapshot;
            }
            if (snapshot?.Feed is null || !HasUsableNceFeed(snapshot.Feed)) return;
            if (!string.IsNullOrWhiteSpace(snapshot.Feed.FinancialYear) &&
                !snapshot.Feed.FinancialYear.Equals(fy, StringComparison.OrdinalIgnoreCase)) return;

            RehydrateNcePeriodSourceAuthority(snapshot.Feed.CurrentYear, FyStock, FyDebtors, FyCreditors);
            RehydrateNcePeriodSourceAuthority(snapshot.Feed.PreviousYear, PyStock, PyDebtors, PyCreditors);
        }
        catch
        {
            // A missing/corrupt last-good feed must not damage the already-painted preparation.
        }
    }

    private static void RehydrateNcePeriodSourceAuthority(
        NceFinancialStatementPeriod period,
        ObservableCollection<FinancialStatementStockRow> stock,
        ObservableCollection<FinancialStatementLedgerRow> debtors,
        ObservableCollection<FinancialStatementLedgerRow> creditors)
    {
        if (stock.Count > 0)
        {
            foreach (var row in stock)
            {
                row.HasPeriodSourceAuthority = false;
                row.PeriodSourceOpeningTotal = 0m;
                row.PeriodSourceClosingTotal = 0m;
            }
            var hasStockAuthority = period.HasStockSummaryAuthority || period.StockSummary.Count > 0;
            AttachStockPeriodAuthority(
                stock,
                hasStockAuthority,
                period.StockSummary.Sum(x => x.OpeningValue),
                period.StockSummary.Sum(x => x.ClosingValue));
        }

        foreach (var row in debtors)
        {
            row.HasScheduleSourceAuthority = false;
            row.ScheduleSourceAuthorityTotal = 0m;
        }
        foreach (var row in creditors)
        {
            row.HasScheduleSourceAuthority = false;
            row.ScheduleSourceAuthorityTotal = 0m;
        }

        var legacyCompletePeriod =
            period.TrialBalance.Count > 0 &&
            period.StockSummary.Count > 0 &&
            (period.TradePayables.Count > 0 || creditors.Count > 0);
        var hasDebtorAuthority =
            period.HasTradeReceivablesAuthority ||
            period.TradeReceivables.Count > 0 ||
            legacyCompletePeriod;
        var hasCreditorAuthority =
            period.HasTradePayablesAuthority ||
            period.TradePayables.Count > 0 ||
            legacyCompletePeriod;

        AttachPartyScheduleAuthority(debtors, hasDebtorAuthority, OfficialPartyScheduleTotal(period.TradeReceivables, "Trade Receivables"));
        AttachPartyScheduleAuthority(creditors, hasCreditorAuthority, OfficialPartyScheduleTotal(period.TradePayables, "Trade Payables"));
    }

    private static string DataSnapshotKey(long clientId, string fy) => $"FinancialStatements.DataSnapshot.{clientId}.{fy.Trim()}";

    private void SaveDataSnapshot(long clientId, string fy)
    {
        var snapshot = new FinancialStatementDataSnapshot
        {
            FyTrialBalance = FyTrialBalance.Select(FinancialStatementDataLedger.From).ToList(),
            PyTrialBalance = PyTrialBalance.Select(FinancialStatementDataLedger.From).ToList(),
            FyDebtors = FyDebtors.Select(FinancialStatementDataLedger.From).ToList(),
            PyDebtors = PyDebtors.Select(FinancialStatementDataLedger.From).ToList(),
            FyCreditors = FyCreditors.Select(FinancialStatementDataLedger.From).ToList(),
            PyCreditors = PyCreditors.Select(FinancialStatementDataLedger.From).ToList(),
            FyStock = FyStock.Select(FinancialStatementDataStock.From).ToList(),
            PyStock = PyStock.Select(FinancialStatementDataStock.From).ToList(),
            SavedUtc = DateTime.UtcNow
        };
        var key = DataSnapshotKey(clientId, fy);
        lock (PreparedSnapshotGate) PreparedDataSnapshots[key] = snapshot;
        _preferences.Set(key, JsonSerializer.Serialize(snapshot));
    }

    private bool RestoreDataSnapshot(long clientId, string fy)
    {
        try
        {
            var key = DataSnapshotKey(clientId, fy);
            FinancialStatementDataSnapshot? snapshot;
            lock (PreparedSnapshotGate) PreparedDataSnapshots.TryGetValue(key, out snapshot);
            if (snapshot is null)
            {
                var json = _preferences.Get(key);
                if (string.IsNullOrWhiteSpace(json)) return false;
                snapshot = JsonSerializer.Deserialize<FinancialStatementDataSnapshot>(json);
                if (snapshot is not null)
                    lock (PreparedSnapshotGate) PreparedDataSnapshots[key] = snapshot;
            }
            if (snapshot is null) return false;
            RestoreLedgerSnapshot(FyTrialBalance, snapshot.FyTrialBalance, string.Empty);
            RestoreLedgerSnapshot(PyTrialBalance, snapshot.PyTrialBalance, string.Empty);
            RestoreLedgerSnapshot(FyDebtors, snapshot.FyDebtors, "Debtor");
            RestoreLedgerSnapshot(PyDebtors, snapshot.PyDebtors, "Debtor");
            RestoreLedgerSnapshot(FyCreditors, snapshot.FyCreditors, "Creditor");
            RestoreLedgerSnapshot(PyCreditors, snapshot.PyCreditors, "Creditor");
            RestoreStockSnapshot(FyStock, snapshot.FyStock);
            RestoreStockSnapshot(PyStock, snapshot.PyStock);
            return FyTrialBalance.Count > 0 || PyTrialBalance.Count > 0 || FyStock.Count > 0 || PyStock.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RestoreLedgerSnapshot(ObservableCollection<FinancialStatementLedgerRow> target, IEnumerable<FinancialStatementDataLedger>? rows, string scheduleContext)
    {
        target.Clear();
        foreach (var source in rows ?? Array.Empty<FinancialStatementDataLedger>())
            target.Add(source.ToRow(scheduleContext));
    }

    private static void RestoreStockSnapshot(ObservableCollection<FinancialStatementStockRow> target, IEnumerable<FinancialStatementDataStock>? rows)
    {
        target.Clear();
        foreach (var source in rows ?? Array.Empty<FinancialStatementDataStock>()) target.Add(source.ToRow());
    }

    private static string FeatureEnabledKey(long clientId, string fy) => $"FinancialStatements.Enabled.{clientId}.{fy.Trim()}";
    private static string PreviousYearModeKey(long clientId, string fy) => $"FinancialStatements.PreviousYearMode.{clientId}.{fy.Trim()}";

    public bool IsEnabledFor(ClientProfile client)
        => ReleaseFeatureGates.FinancialStatements && client is not null &&
           _preferences.Get(FeatureEnabledKey(client.Id, client.FinancialYear)).Equals("1", StringComparison.OrdinalIgnoreCase);

    private void LoadFeatureSettings(ClientProfile client)
    {
        _financialStatementsEnabled = _preferences.Get(FeatureEnabledKey(client.Id, client.FinancialYear)).Equals("1", StringComparison.OrdinalIgnoreCase);
        var mode = _preferences.Get(PreviousYearModeKey(client.Id, client.FinancialYear));
        _previousYearDataMode = PreviousYearDataModes.Contains(mode, StringComparer.OrdinalIgnoreCase) ? mode : "Actual Previous Year Data";
        BasicInfo.PreviousYearDataMode = _previousYearDataMode;
        OnPropertyChanged(nameof(FinancialStatementsEnabled));
        OnPropertyChanged(nameof(FinancialStatementsReleaseAvailable));
        OnPropertyChanged(nameof(IsFinancialStatementsDisabled));
        OnPropertyChanged(nameof(ShowClientFinancialStatementSettings));
        OnPropertyChanged(nameof(PreviousYearDataMode));
        OnPropertyChanged(nameof(NoPreviousYearData));
        OnPropertyChanged(nameof(PreviousYearModeDisplay));
        OnPropertyChanged(nameof(ShowPreviousYearSourceOptions));
        OnPropertyChanged(nameof(ShowConnectPreviousYearTally));
        OnPropertyChanged(nameof(FinancialStatementsCollectionStatus));
    }

    private void SaveFeatureSettings()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        // The release kill switch must never erase the client's saved ON/OFF preference.
        // When the module is re-enabled in a future release, the pre-existing preference returns.
        if (ReleaseFeatureGates.FinancialStatements)
            _preferences.Set(FeatureEnabledKey(client.Id, client.FinancialYear), FinancialStatementsEnabled ? "1" : "0");
        _preferences.Set(PreviousYearModeKey(client.Id, client.FinancialYear), PreviousYearDataMode);
    }

    private void ApplyPreviousYearMode()
    {
        if (PreviousYearDataMode.Equals("No Previous Year / First Year", StringComparison.OrdinalIgnoreCase))
        {
            PyTrialBalance.Clear();
            PyStock.Clear();
            PyDebtors.Clear();
            PyCreditors.Clear();
            OnPropertyChanged(nameof(PreviousYearDataAvailable));
            OnPropertyChanged(nameof(PreviousYearStatus));
            RefreshNotePreview();
            return;
        }
        if (!PreviousYearDataMode.Equals("Use Current FY Opening Balances as PY", StringComparison.OrdinalIgnoreCase)) return;

        PyTrialBalance.Clear(); PyDebtors.Clear(); PyCreditors.Clear();
        var order = 0;
        foreach (var fyRow in FyTrialBalance.Where(x => x.Opening != 0m))
        {
            var row = new FinancialStatementLedgerRow
            {
                Ledger = fyRow.Ledger, ParentGroup = fyRow.ParentGroup, PrimaryGroup = fyRow.PrimaryGroup, MasterId = string.Empty,
                Opening = 0m, Debit = 0m, Credit = 0m, Closing = fyRow.Opening, SourceOrder = order++
            };
            row.ConfigureMappingCatalog(_mappingCatalog, FinancialStatementWorkbookService.ResolveProfileSheet(BasicInfo.EntityType), OwnerMappingNames());
            var grouping = $"{row.ParentGroup} {row.PrimaryGroup}";
            if (ContainsGroup(grouping, "Sundry Debtors"))
            {
                row.ConfigureScheduleContext("Debtor");
                row.RestoreScheduleMapping(fyRow.ScheduleClass1, fyRow.ScheduleClass2);
                PyDebtors.Add(row);
            }
            if (ContainsGroup(grouping, "Sundry Creditors"))
            {
                row.ConfigureScheduleContext("Creditor");
                row.RestoreScheduleMapping(fyRow.ScheduleClass1, fyRow.ScheduleClass2);
                PyCreditors.Add(row);
            }
            PyTrialBalance.Add(row);
        }
        PyStock.Clear();
        foreach (var stock in FyStock.Where(x => x.OpeningBalance.HasValue))
        {
            var pyStock = new FinancialStatementStockRow
            {
                Item = stock.Item, Group = stock.Group, Category = stock.Category, Unit = stock.Unit, HsnSac = stock.HsnSac,
                GstRate = stock.GstRate, ValuationMethod = stock.ValuationMethod, ClosingBalance = stock.OpeningBalance
            };
            pyStock.RestoreMapping(stock.Mapping, string.IsNullOrWhiteSpace(stock.Mapping) ? string.Empty : "Copied from FY");
            PyStock.Add(pyStock);
        }
        AutoCopyExactLedgerMappings();
        OnPropertyChanged(nameof(PreviousYearDataAvailable));
        OnPropertyChanged(nameof(PreviousYearStatus));
        RefreshNotePreview();
    }

    private static string CrossYearLedgerKey(string value)
        => string.Join(' ', (value ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private void AutoCopyExactLedgerMappings()
    {
        if (PreviousYearDataMode.Equals("No Previous Year / First Year", StringComparison.OrdinalIgnoreCase))
        {
            CrossYearMappingSummary = "PY not applicable; cross-year mapping copy is disabled.";
            OnPropertyChanged(nameof(CrossYearMappingSummary));
            return;
        }
        var fy = FyTrialBalance.Where(x => CrossYearLedgerKey(x.Ledger).Length > 0).GroupBy(x => CrossYearLedgerKey(x.Ledger)).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var py = PyTrialBalance.Where(x => CrossYearLedgerKey(x.Ledger).Length > 0).GroupBy(x => CrossYearLedgerKey(x.Ledger)).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var matched = 0; var copied = 0; var same = 0; var conflicts = 0;
        foreach (var key in fy.Keys.Intersect(py.Keys, StringComparer.OrdinalIgnoreCase))
        {
            matched++;
            var a = fy[key]; var b = py[key];
            // R25: standard Profit & Loss A/c has period-specific NCE plug-in authority.
            // Never auto-copy its mapping between FY and PY.
            if (FinancialStatementTrialBalanceOrder.IsProfitLossAccountLedger(a.Ledger) ||
                FinancialStatementTrialBalanceOrder.IsProfitLossAccountLedger(b.Ledger))
            {
                same++;
                continue;
            }
            if (a.HasResolvedMapping && !b.HasResolvedMapping) { if (b.TryApplyCrossYearMappingFrom(a, "Copied from FY")) copied++; continue; }
            if (b.HasResolvedMapping && !a.HasResolvedMapping) { if (a.TryApplyCrossYearMappingFrom(b, "Copied from PY")) copied++; continue; }
            if (!a.HasResolvedMapping || !b.HasResolvedMapping) continue;
            if (a.CanonicalMappingId.Equals(b.CanonicalMappingId, StringComparison.OrdinalIgnoreCase) && a.NatureDisplay.Equals(b.NatureDisplay, StringComparison.OrdinalIgnoreCase)) same++;
            else conflicts++;
        }
        var unmatched = FyTrialBalance.Count + PyTrialBalance.Count - matched * 2;
        CrossYearMappingSummary = $"Exact-ledger FY/PY sync · matched {matched} · copied {copied} · already same {same} · conflicts {conflicts} · unmatched {unmatched}.";
        OnPropertyChanged(nameof(CrossYearMappingSummary));
    }

    private void CopyCrossYearMappings(bool copyFyToPy)
    {
        var source = copyFyToPy ? FyTrialBalance : PyTrialBalance;
        var target = copyFyToPy ? PyTrialBalance : FyTrialBalance;
        var targetByName = target.Where(x => CrossYearLedgerKey(x.Ledger).Length > 0).GroupBy(x => CrossYearLedgerKey(x.Ledger)).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var copied = 0; var conflicts = 0; var already = 0;
        foreach (var row in source.Where(x => x.HasResolvedMapping))
        {
            if (FinancialStatementTrialBalanceOrder.IsProfitLossAccountLedger(row.Ledger)) continue;
            if (!targetByName.TryGetValue(CrossYearLedgerKey(row.Ledger), out var destination)) continue;
            if (FinancialStatementTrialBalanceOrder.IsProfitLossAccountLedger(destination.Ledger)) continue;
            if (!destination.HasResolvedMapping) { if (destination.TryApplyCrossYearMappingFrom(row, copyFyToPy ? "Copied from FY" : "Copied from PY")) copied++; }
            else if (destination.CanonicalMappingId.Equals(row.CanonicalMappingId, StringComparison.OrdinalIgnoreCase) && destination.NatureDisplay.Equals(row.NatureDisplay, StringComparison.OrdinalIgnoreCase)) already++;
            else conflicts++;
        }
        RefreshNotePreview();
        CrossYearMappingSummary = $"Copy {(copyFyToPy ? "FY → PY" : "PY → FY")} · copied {copied:N0} · already same {already:N0} · conflicts preserved {conflicts:N0}. Unmatched ledgers remain Unmapped.";
        OnPropertyChanged(nameof(CrossYearMappingSummary));
        StatusText = CrossYearMappingSummary;
    }


    private void ApplyStockMappingToSameGroup()
    {
        var source = SelectedFyStockRow;
        if (source is null) { StatusText = "Select one FY Stock Summary row first."; return; }
        if (!source.HasResolvedMapping) { StatusText = "Choose an Inventory Classification for the selected FY Stock row first."; return; }
        var group = source.Group.Trim();
        if (group.Length == 0) { StatusText = $"{source.Item} has no Stock Group, so a group mapping cannot be applied."; return; }

        static (int Applied, int Preserved, int Skipped) Apply(FinancialStatementStockRow src, IEnumerable<FinancialStatementStockRow> rows, string groupName)
        {
            var applied = 0; var preserved = 0; var skipped = 0;
            foreach (var target in rows.Where(x => x.Group.Trim().Equals(groupName, StringComparison.OrdinalIgnoreCase)))
            {
                if (ReferenceEquals(src, target)) continue;
                if (target.HasResolvedMapping) { preserved++; continue; }
                if (target.TryApplyMappingFrom(src, "User Mapped")) applied++;
                else skipped++;
            }
            return (applied, preserved, skipped);
        }

        var fy = Apply(source, FyStock, group);
        var py = SameTallyGroupScope.Equals("FY + PY", StringComparison.OrdinalIgnoreCase) ? Apply(source, PyStock, group) : (0, 0, 0);
        RefreshMappingViews();
        StatusText = $"Inventory same-group '{group}' · FY applied {fy.Applied} · already mapped preserved {fy.Preserved} · skipped {fy.Skipped}" +
                     (SameTallyGroupScope.Equals("FY + PY", StringComparison.OrdinalIgnoreCase)
                         ? $" · PY applied {py.Item1} · already mapped preserved {py.Item2} · skipped {py.Item3}."
                         : ".");
    }

    private void CopyStockCrossYearMappings(bool copyFyToPy)
    {
        var source = copyFyToPy ? FyStock : PyStock;
        var target = copyFyToPy ? PyStock : FyStock;
        var targetByItem = target.Where(x => CrossYearLedgerKey(x.Item).Length > 0)
            .GroupBy(x => CrossYearLedgerKey(x.Item)).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());
        var copied = 0; var same = 0; var conflicts = 0;
        foreach (var row in source.Where(x => x.HasResolvedMapping))
        {
            if (!targetByItem.TryGetValue(CrossYearLedgerKey(row.Item), out var destination)) continue;
            if (!destination.HasResolvedMapping) { if (destination.TryApplyMappingFrom(row, copyFyToPy ? "Copied from FY" : "Copied from PY")) copied++; }
            else if (destination.Mapping.Equals(row.Mapping, StringComparison.OrdinalIgnoreCase)) same++;
            else conflicts++;
        }
        RefreshMappingViews();
        StatusText = $"Stock copy {(copyFyToPy ? "FY → PY" : "PY → FY")} · copied {copied:N0} · already same {same:N0} · conflicts preserved {conflicts:N0}.";
    }

    private void ApplyScheduleMappingToSameGroup(
        FinancialStatementLedgerRow? source,
        IEnumerable<FinancialStatementLedgerRow> fyRows,
        IEnumerable<FinancialStatementLedgerRow> pyRows,
        string caption)
    {
        if (source is null) { StatusText = $"Select one FY {caption} row first."; return; }
        if (!source.HasCompleteScheduleMapping) { StatusText = $"Complete the selected {caption} schedule mapping first."; return; }
        var group = source.ParentGroup.Trim();
        if (group.Length == 0) { StatusText = $"{source.Ledger} has no Tally Group, so a group schedule mapping cannot be applied."; return; }

        static (int Applied, int Preserved, int Skipped) Apply(FinancialStatementLedgerRow src, IEnumerable<FinancialStatementLedgerRow> rows, string groupName)
        {
            var applied = 0; var preserved = 0; var skipped = 0;
            foreach (var target in rows.Where(x => x.ParentGroup.Trim().Equals(groupName, StringComparison.OrdinalIgnoreCase)))
            {
                if (ReferenceEquals(src, target)) continue;
                // Never overwrite a completed OR partially-entered schedule. Batch mapping is
                // only a convenience for genuinely unmapped rows.
                if (target.HasAnyScheduleSelection) { preserved++; continue; }
                target.RestoreScheduleMapping(src.ScheduleClass1, src.ScheduleClass2, "User Mapped");
                if (target.HasCompleteScheduleMapping) applied++; else skipped++;
            }
            return (applied, preserved, skipped);
        }

        var fy = Apply(source, fyRows, group);
        var py = SameTallyGroupScope.Equals("FY + PY", StringComparison.OrdinalIgnoreCase) ? Apply(source, pyRows, group) : (0, 0, 0);
        SynchronizePartyScheduleAuthority();
        RefreshMappingViews();
        StatusText = $"{caption} same-group '{group}' · FY applied {fy.Applied} · already/partly mapped preserved {fy.Preserved} · skipped {fy.Skipped}" +
                     (SameTallyGroupScope.Equals("FY + PY", StringComparison.OrdinalIgnoreCase)
                         ? $" · PY applied {py.Item1} · already/partly mapped preserved {py.Item2} · skipped {py.Item3}."
                         : ".");
    }

    private void CopyScheduleCrossYearMappings(
        IEnumerable<FinancialStatementLedgerRow> sourceRows,
        IEnumerable<FinancialStatementLedgerRow> targetRows,
        string caption,
        string direction)
    {
        var targetByName = targetRows.Where(x => CrossYearLedgerKey(x.Ledger).Length > 0)
            .GroupBy(x => CrossYearLedgerKey(x.Ledger)).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());
        var copied = 0; var same = 0; var conflicts = 0;
        foreach (var row in sourceRows.Where(x => x.HasCompleteScheduleMapping))
        {
            if (!targetByName.TryGetValue(CrossYearLedgerKey(row.Ledger), out var destination)) continue;
            if (!destination.HasCompleteScheduleMapping)
            {
                destination.RestoreScheduleMapping(row.ScheduleClass1, row.ScheduleClass2,
                    direction.StartsWith("FY", StringComparison.OrdinalIgnoreCase) ? "Copied from FY" : "Copied from PY");
                copied++;
            }
            else if (destination.ScheduleClass1.Equals(row.ScheduleClass1, StringComparison.OrdinalIgnoreCase) &&
                     destination.ScheduleClass2.Equals(row.ScheduleClass2, StringComparison.OrdinalIgnoreCase)) same++;
            else conflicts++;
        }
        SynchronizePartyScheduleAuthority();
        RefreshMappingViews();
        StatusText = $"{caption} copy {direction} · copied {copied:N0} · already same {same:N0} · conflicts preserved {conflicts:N0}.";
    }

    private void ApplyMappingToSameTallyGroup()
    {
        var source = SelectedFyTrialBalanceRow;
        if (source is null)
        {
            StatusText = "Select one FY Trial Balance ledger first.";
            return;
        }

        if (FinancialStatementTrialBalanceOrder.IsProfitLossAccountLedger(source.Ledger))
        {
            StatusText = "Profit & Loss A/c uses the NCE plug-in/workbook period authority and is not propagated to other ledgers.";
            return;
        }

        var tallyGroup = source.ParentGroup.Trim();
        if (tallyGroup.Length == 0)
        {
            StatusText = $"{source.Ledger} has no Tally Parent Group, so a group mapping cannot be applied.";
            return;
        }

        // R15 R14 R6: same-group propagation must start from one fully resolved factory mapping.
        // The old code accepted any single C1-C5 value, so clicking C4 first could copy an
        // invalid partial hierarchy such as only "Capital Account" to every ledger in the group.
        if (!source.HasResolvedMapping)
        {
            StatusText = "Complete a valid workbook mapping for the selected ledger first. Apply to Same Tally Group copies only C1-C3; schedule-only detail is never propagated.";
            return;
        }

        var fyResult = ApplyHierarchyToGroup(source, FyTrialBalance, tallyGroup);
        var pyResult = SameTallyGroupScope.Equals("FY + PY", StringComparison.OrdinalIgnoreCase)
            ? ApplyHierarchyToGroup(source, PyTrialBalance, tallyGroup)
            : (Applied: 0, Preserved: 0, Skipped: 0);
        RefreshNotePreview();
        StatusText = $"Tally Group '{tallyGroup}' · FY applied {fyResult.Applied} · already/partly mapped preserved {fyResult.Preserved} · incompatible skipped {fyResult.Skipped}" +
                     (SameTallyGroupScope.Equals("FY + PY", StringComparison.OrdinalIgnoreCase)
                         ? $" · PY applied {pyResult.Applied} · already/partly mapped preserved {pyResult.Preserved} · incompatible skipped {pyResult.Skipped}."
                         : ".");
    }

    private static (int Applied, int Preserved, int Skipped) ApplyHierarchyToGroup(
        FinancialStatementLedgerRow source, IEnumerable<FinancialStatementLedgerRow> rows, string tallyGroup)
    {
        var applied = 0; var preserved = 0; var skipped = 0;
        foreach (var target in rows.Where(x => x.ParentGroup.Trim().Equals(tallyGroup, StringComparison.OrdinalIgnoreCase)))
        {
            if (ReferenceEquals(source, target)) continue;
            if (FinancialStatementTrialBalanceOrder.IsProfitLossAccountLedger(target.Ledger)) { skipped++; continue; }
            // R50: Apply to Same Tally Group is an unmapped-row helper only. Never overwrite
            // a resolved mapping or a partially entered C1/C2/C3 path.
            if (target.HasAnyMappingSelection) { preserved++; continue; }
            if (target.TryApplyResolvedHierarchyFrom(source, "User Mapped")) applied++;
            else skipped++;
        }
        return (applied, preserved, skipped);
    }

    private static string MappingIdentity(FinancialStatementLedgerRow row)
        => FinancialStatementPresentationExportService.IsUsableTallyMasterId(row.MasterId) ? row.MasterId.Trim() : row.Ledger.Trim();

    private static bool SameLedgerIdentity(FinancialStatementLedgerRow left, FinancialStatementLedgerRow right)
    {
        if (FinancialStatementPresentationExportService.IsUsableTallyMasterId(left.MasterId) &&
            FinancialStatementPresentationExportService.IsUsableTallyMasterId(right.MasterId))
            return left.MasterId.Trim().Equals(right.MasterId.Trim(), StringComparison.OrdinalIgnoreCase);
        return left.Ledger.Trim().Equals(right.Ledger.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildTemplatePresentation()
    {
        var ownerNames = OwnerMappingNames();
        _mappingCatalog = FinancialStatementClassificationService.ApplyOwnerAccountLabels(
            FinancialStatementWorkbookService.BuildMappingCatalog(TemplatePath(), BasicInfo.EntityType),
            ownerNames);
        ReplaceOptions(FsHeadOptions, _mappingCatalog.Select(x => x.FsHead));
        ReplaceOptions(NoteHeadOptions, _mappingCatalog.Select(x => x.NoteHead));
        ReplaceOptions(NoteSubHeadOptions, _mappingCatalog.Select(x => x.NoteSubHead));
        foreach (var row in FyTrialBalance.Concat(PyTrialBalance))
            row.ConfigureMappingCatalog(_mappingCatalog, FinancialStatementWorkbookService.ResolveProfileSheet(BasicInfo.EntityType), ownerNames);
        BuildNoteList(BasicInfo.EntityType);
        RefreshNotePreview();
    }

    private void ReloadSelectedProfileMappings()
    {
        var client = _clientContext.CurrentClient;
        if (client is null || (FyTrialBalance.Count == 0 && PyTrialBalance.Count == 0 && FyStock.Count == 0 && PyStock.Count == 0)) return;
        var mappings = LoadMappings(client.Id, client.FinancialYear);
        ApplyMappings(mappings, FyTrialBalance, FyStock);
        ApplyMappings(mappings, PyTrialBalance, PyStock);
        RefreshNotePreview();
        StatusText = $"Mapping profile changed to {BasicInfo.EntityType}. C1-C3 now follow this profile's original-workbook hierarchy.";
    }

    private IReadOnlyList<string> OwnerMappingNames()
    {
        // Original NCE workbook authority: C3 for Owner/Partners Capital Account is fed only
        // by the Owner/Partners details maintained in Basic Info. Never mine arbitrary Capital
        // group ledgers for names: that previously leaked Drawings, insurance, salary and other
        // unrelated ledgers into the proprietor/partner dropdown.
        var names = OwnerPartners.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Name.Trim()).ToList();
        if (names.Count == 0)
            names.AddRange(Signatories.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Name.Trim()));
        if (names.Count == 0 && !string.IsNullOrWhiteSpace(BasicInfo.SignatoryName)) names.Add(BasicInfo.SignatoryName.Trim());
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Take(11).ToArray();
    }

    private static void ReplaceOptions(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (seen.Add(value)) target.Add(value);
    }

    private FinancialStatementMappingOption? ResolveMappingOption(FinancialStatementLedgerRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.StableHierarchyId))
        {
            var stable = _mappingCatalog.FirstOrDefault(x => x.StableHierarchyId.Equals(row.StableHierarchyId, StringComparison.OrdinalIgnoreCase));
            if (stable is not null) return stable;
        }
        if (!string.IsNullOrWhiteSpace(row.Classification1) && !string.IsNullOrWhiteSpace(row.Classification2) &&
            !string.IsNullOrWhiteSpace(row.Classification3) && !string.IsNullOrWhiteSpace(row.Classification4))
        {
            var exact = _mappingCatalog.FirstOrDefault(x =>
                (string.IsNullOrWhiteSpace(row.Mapping) || x.MappingKey.Equals(row.Mapping, StringComparison.OrdinalIgnoreCase)) &&
                x.Class1.Equals(row.Classification1, StringComparison.OrdinalIgnoreCase) &&
                x.Class2.Equals(row.Classification2, StringComparison.OrdinalIgnoreCase) &&
                x.Class3.Equals(row.Classification3, StringComparison.OrdinalIgnoreCase) &&
                x.Class4.Equals(row.Classification4, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        if (string.IsNullOrWhiteSpace(row.Mapping)) return null;
        var keyMatches = _mappingCatalog.Where(x => x.MappingKey.Equals(row.Mapping, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return keyMatches.Length == 1 ? keyMatches[0] : null;
    }
    // R36: the NCE workbook keeps Trial Balance mapping and Debtor/Creditor Q/R schedule
    // mapping as separate authorities, but final output is rendered from TB rows. Link the exact
    // party schedule back to the matching TB ledger before preview/export. MASTERID outranks name
    // when both sides expose one; otherwise use one exact normalized ledger identity. Debtor and
    // Creditor sources are never searched together, preserving opposite-balance classifications.
    private void SynchronizePartyScheduleAuthority()
    {
        SynchronizePartyScheduleAuthority(FyTrialBalance, FyDebtors, FyCreditors);
        SynchronizePartyScheduleAuthority(PyTrialBalance, PyDebtors, PyCreditors);
    }

    private static void SynchronizePartyScheduleAuthority(
        IEnumerable<FinancialStatementLedgerRow> trialBalance,
        IEnumerable<FinancialStatementLedgerRow> debtors,
        IEnumerable<FinancialStatementLedgerRow> creditors)
    {
        var debtorRows = debtors.ToArray();
        var creditorRows = creditors.ToArray();

        foreach (var row in trialBalance)
        {
            row.ClearScheduleContext();
            var groupText = string.Join(" ", new[] { row.ParentGroup, row.PrimaryGroup }).ToUpperInvariant();
            var expectsDebtor = groupText.Contains("SUNDRY DEBTOR", StringComparison.Ordinal) ||
                                groupText.Contains("TRADE RECEIV", StringComparison.Ordinal);
            var expectsCreditor = groupText.Contains("SUNDRY CREDITOR", StringComparison.Ordinal) ||
                                  groupText.Contains("TRADE PAYABLE", StringComparison.Ordinal);

            var debtorMatches = FindExactPartyScheduleMatches(row, debtorRows);
            var creditorMatches = FindExactPartyScheduleMatches(row, creditorRows);
            if (expectsDebtor) ApplyLinkedSchedule(row, debtorMatches, "Debtor");
            else if (expectsCreditor) ApplyLinkedSchedule(row, creditorMatches, "Creditor");
            else if (debtorMatches.Length > 0 && creditorMatches.Length == 0) ApplyLinkedSchedule(row, debtorMatches, "Debtor");
            else if (creditorMatches.Length > 0 && debtorMatches.Length == 0) ApplyLinkedSchedule(row, creditorMatches, "Creditor");
            // A name that genuinely exists on both source sides without a usable MASTERID is
            // deliberately left unlinked; final export must review that ambiguity rather than guess.
        }
    }

    private static FinancialStatementLedgerRow[] FindExactPartyScheduleMatches(
        FinancialStatementLedgerRow trialRow,
        IReadOnlyList<FinancialStatementLedgerRow> scheduleRows)
    {
        if (FinancialStatementPresentationExportService.IsUsableTallyMasterId(trialRow.MasterId))
        {
            var id = trialRow.MasterId.Trim();
            var byId = scheduleRows.Where(x =>
                FinancialStatementPresentationExportService.IsUsableTallyMasterId(x.MasterId) &&
                x.MasterId.Trim().Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byId.Length > 0) return byId;
        }

        var key = ExactPartyIdentityKey(trialRow.Ledger);
        if (key.Length == 0) return Array.Empty<FinancialStatementLedgerRow>();
        return scheduleRows.Where(x => ExactPartyIdentityKey(x.Ledger).Equals(key, StringComparison.Ordinal)).ToArray();
    }

    private static string ExactPartyIdentityKey(string? value) =>
        new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static void ApplyLinkedSchedule(
        FinancialStatementLedgerRow trialRow,
        IReadOnlyList<FinancialStatementLedgerRow> matches,
        string scheduleContext)
    {
        if (matches.Count == 0) return;
        trialRow.ConfigureScheduleContext(scheduleContext);

        var distinct = matches
            .Select(x => new
            {
                C1 = (x.ScheduleClass1 ?? string.Empty).Trim(),
                C2 = (x.ScheduleClass2 ?? string.Empty).Trim(),
                Origin = x.ScheduleMappingOrigin
            })
            .GroupBy(x => $"{x.C1}\u001f{x.C2}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => ExplicitScheduleOriginRank(x.Origin)).First())
            .Take(2)
            .ToArray();

        if (distinct.Length == 1)
        {
            trialRow.RestoreScheduleMapping(distinct[0].C1, distinct[0].C2, distinct[0].Origin);
            return;
        }

        // Bill-wise rows can legitimately split one ledger over different statutory disclosures.
        // A single TB row cannot represent two Q/R buckets, so mark it incomplete and stop final
        // output instead of silently choosing one bill's classification.
        var commonC1 = distinct.Select(x => x.C1).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        trialRow.RestoreScheduleMapping(commonC1.Length == 1 ? commonC1[0] : string.Empty, string.Empty, "Schedule conflict");
    }

    private static int ExplicitScheduleOriginRank(string? origin)
    {
        var value = (origin ?? string.Empty).Trim();
        if (value.Equals("User Mapped", StringComparison.OrdinalIgnoreCase)) return 3;
        if (value.StartsWith("Copied from ", StringComparison.OrdinalIgnoreCase)) return 2;
        if (value.StartsWith("Tally NCE", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    private void TrackPreparationChanges<T>(ObservableCollection<T> collection) where T : INotifyPropertyChanged
    {
        foreach (var item in collection) item.PropertyChanged += OnPreparationRowPropertyChanged;
        collection.CollectionChanged += (_, args) =>
        {
            if (args.OldItems is not null)
                foreach (var item in args.OldItems.OfType<INotifyPropertyChanged>()) item.PropertyChanged -= OnPreparationRowPropertyChanged;
            if (args.NewItems is not null)
                foreach (var item in args.NewItems.OfType<INotifyPropertyChanged>()) item.PropertyChanged += OnPreparationRowPropertyChanged;
            QueuePreparedFinancialStatementRefresh();
        };
    }

    private void OnPreparationRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingPreparedState) return;
        QueuePreparedFinancialStatementRefresh();
    }

    private void QueuePreparedFinancialStatementRefresh()
    {
        if (_preparationRefreshQueued || _updatingPreparedState) return;
        _preparationRefreshQueued = true;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _preparationRefreshQueued = false;
            UpdatePreparedFinancialStatementState();
            return;
        }
        dispatcher.BeginInvoke(new Action(() =>
        {
            _preparationRefreshQueued = false;
            UpdatePreparedFinancialStatementState();
        }));
    }

    private void UpdatePreparedFinancialStatementState()
    {
        if (_updatingPreparedState) return;
        if (!FinancialStatementsEnabled)
        {
            PreparationStatusText = "Financial Statements preparation is disabled for this Client/FY.";
            return;
        }

        _updatingPreparedState = true;
        try
        {
            SynchronizePartyScheduleAuthority();
            RefreshNotePreview();
            if (_mappingCatalog.Count == 0)
            {
                PreparationStatusText = "Financial Statements preparation mapping catalog is unavailable.";
                return;
            }

            var assessment = CleanFinancialStatementExportService.AssessPreparation(
                BasicInfo, FyTrialBalance, ExportPyTrialBalance(), _mappingCatalog);
            if (assessment.FyPending > 0 || assessment.PyPending > 0)
            {
                PreparationStatusText = $"Preparation updating · FY {assessment.FyPending} / PY {assessment.PyPending} material mapping(s) pending.";
                return;
            }

            PreparationStatusText = "Financial Statements prepared from current mappings · TB, Stock and party schedules are current for FY/PY.";
        }
        catch (Exception ex)
        {
            PreparationStatusText = $"Financial Statements preparation pending: {ex.Message}";
        }
        finally
        {
            _updatingPreparedState = false;
        }
    }

    private void RefreshMappingViews()
    {
        FyTrialBalanceView.Refresh();
        PyTrialBalanceView.Refresh();
        FyStockView.Refresh();
        PyStockView.Refresh();
        FyDebtorsView.Refresh();
        PyDebtorsView.Refresh();
        FyCreditorsView.Refresh();
        PyCreditorsView.Refresh();
    }

    private bool FilterTrialBalanceRow(object item)
    {
        if (item is not FinancialStatementLedgerRow row) return true;
        if (HideZeroClosingBalance && row.Closing == 0m) return false;
        return !ShowUnmappedOnly || !row.HasResolvedMapping;
    }

    private bool FilterScheduleRow(object item)
    {
        if (item is not FinancialStatementLedgerRow row) return true;
        if (HideZeroClosingBalance && row.Closing == 0m) return false;
        return !ShowUnmappedOnly || !row.HasCompleteScheduleMapping;
    }

    private bool FilterStockRow(object item)
    {
        if (item is not FinancialStatementStockRow row) return true;
        if (HideZeroClosingBalance && row.ClosingBalance.HasValue && row.ClosingBalance.Value == 0m) return false;
        return !ShowUnmappedOnly || !row.HasResolvedMapping;
    }

    private async Task ConnectPreviousYearTallyAsync()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) { StatusText = "Open a Client/FY first."; return; }
        try
        {
            using var http = new TallyHttpService(
                _preferences.Get(ApplicationSettingKeys.TallyHost),
                _preferences.GetInt(ApplicationSettingKeys.TallyPort, 9000));
            var companies = await http.DiscoverCompaniesAsync().ConfigureAwait(true);
            var py = PyCaption;
            var normalizedGstin = TallyCompanyInfo.NormalizeGstin(client.Gstin);
            var candidates = companies
                .Where(c => TallyCompanyInfo.NormalizeFinancialYear(c.FinancialYear, c.BooksFrom).Equals(py, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => normalizedGstin.Length > 0 && TallyCompanyInfo.NormalizeGstin(c.Gstin).Equals(normalizedGstin, StringComparison.OrdinalIgnoreCase))
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (candidates.Length == 0)
            {
                StatusText = $"No open Tally company for PY {py} was found on the configured local Tally endpoint.";
                return;
            }
            var selected = candidates[0];
            BasicInfo.PreviousYearTallyCompany = selected.Name;
            BasicInfo.PreviousYearCompanyName = selected.Name;
            BasicInfo.PreviousYearTallyCompanyKey = selected.CompanyKey;

            PyTrialBalance.Clear(); PyDebtors.Clear(); PyCreditors.Clear(); PyStock.Clear();
            var ledgerAuthority = await http.ExportLedgerNameAuthoritiesAsync(selected).ConfigureAwait(true);
            var pySourceOrder = 0;
            var pyRows = ledgerAuthority.Select(ledger => new FinancialStatementLedgerRow
            {
                Ledger = string.IsNullOrWhiteSpace(ledger.DisplayName) ? ledger.ExactName : ledger.DisplayName,
                ParentGroup = ledger.Parent ?? string.Empty,
                PrimaryGroup = FinancialStatementTrialBalanceOrder.ResolvePrimaryGroup(ledger.Parent ?? string.Empty, null),
                MasterId = ledger.MasterId,
                Opening = ledger.OpeningBalance ?? 0m,
                Closing = ledger.ClosingBalance ?? ledger.OpeningBalance ?? 0m,
                SourceOrder = pySourceOrder++
            }).ToArray();
            foreach (var row in FinancialStatementTrialBalanceOrder.Order(pyRows))
            {
                PyTrialBalance.Add(row);
                var grouping = $"{row.ParentGroup} {row.PrimaryGroup}";
                if (ContainsGroup(grouping, "Sundry Debtors")) { row.ConfigureScheduleContext("Debtor"); PyDebtors.Add(row); }
                if (ContainsGroup(grouping, "Sundry Creditors")) { row.ConfigureScheduleContext("Creditor"); PyCreditors.Add(row); }
            }

            try
            {
                var stockXml = await http.ExportStockMastersAsync(selected).ConfigureAwait(true);
                var parsed = new TallyMasterImportService().ParseXmlContent(stockXml, client.Id, py, selected.Name, "Financial Statements PY Tally Stock");
                foreach (var item in parsed.Records.Where(x => x.Type.Equals("Stock Item", StringComparison.OrdinalIgnoreCase)))
                    PyStock.Add(new FinancialStatementStockRow
                    {
                        Item = item.Name, Group = item.Parent, Category = item.StockCategoryName, Unit = item.BaseUnit,
                        HsnSac = item.HsnSac, GstRate = item.GstRate ?? 0m, ValuationMethod = item.ValuationMethod
                    });
            }
            catch { }

            // R9 FIX4: PY can contribute owner/partner Capital ledgers when Basic Info is
            // incomplete, so rebuild the owner-aware catalog after the live PY authority arrives.
            RebuildTemplatePresentation();
            var mappings = LoadMappings(client.Id, client.FinancialYear);
            ApplyMappings(mappings, PyTrialBalance, PyStock);
            ApplyScheduleMappings(mappings, PyDebtors.Concat(PyCreditors), "PY");
            OnPropertyChanged(nameof(BasicInfo));
            OnPropertyChanged(nameof(PreviousYearDataAvailable));
            OnPropertyChanged(nameof(PreviousYearStatus));
            SaveBasicInfo();
            RefreshNotePreview();
            UpdatePreparedFinancialStatementState();
            StatusText = candidates.Length == 1
                ? $"Previous-year Tally source selected: {selected.Name} · FY {py}. PY TB/party authority refreshed."
                : $"Previous-year Tally source selected: {selected.Name} · FY {py}. {candidates.Length} matching PY companies were open; GSTIN/name match was preferred. PY TB/party authority refreshed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Previous-year Tally discovery failed: {ex.Message}";
        }
    }

    private IEnumerable<FinancialStatementLedgerRow> ExportPyTrialBalance()
        => PreviousYearDataMode.Equals("No Previous Year / First Year", StringComparison.OrdinalIgnoreCase)
            ? Array.Empty<FinancialStatementLedgerRow>()
            : PyTrialBalance;

    private async Task ExportWorkbookAsync(bool withLinks)
    {
        var client = _clientContext.CurrentClient;
        if (client is null) { StatusText = "Open a Client/FY first."; return; }
        // R49: snapshot the preparation collections before leaving the UI thread. Excel COM is
        // then run on a dedicated STA worker so the visible AAH window remains responsive.
        SynchronizePartyScheduleAuthority();
        // R52 FIXED13: export must not depend on whether the current session happened to receive
        // a fresh Tally background callback. Rehydrate hidden NCE totals from the durable feed
        // immediately before taking the immutable export snapshot.
        if (IsNceEntityType) RehydrateNceSourceAuthorityFromSavedFeed(client.Id, client.FinancialYear);
        UpdatePreparedFinancialStatementState();
        var template = TemplatePath();
        if (!File.Exists(template)) { StatusText = "Financial Statement template library is missing from the application package."; return; }

        var fyTb = FyTrialBalance.ToArray();
        var pyTb = ExportPyTrialBalance().ToArray();
        var fyStock = FyStock.ToArray();
        var pyStock = NoPreviousYearData ? Array.Empty<FinancialStatementStockRow>() : PyStock.ToArray();
        var fyDebtors = FyDebtors.ToArray();
        var pyDebtors = NoPreviousYearData ? Array.Empty<FinancialStatementLedgerRow>() : PyDebtors.ToArray();
        var fyCreditors = FyCreditors.ToArray();
        var pyCreditors = NoPreviousYearData ? Array.Empty<FinancialStatementLedgerRow>() : PyCreditors.ToArray();
        var signatories = Signatories.Select(x => x.Clone()).ToArray();
        var owners = OwnerPartners.Select(x => x.Clone()).ToArray();
        var basicInfo = CloneBasicInfoForExport();

        try
        {
            var destination = BuildLinkedExportPath(client, withLinks ? "Linked" : "Values", ".xlsx");
            var presentation = PresentationTemplatePath();
            StatusText = withLinks ? "Exporting Linked Financial Statements…" : "Exporting final values-only Financial Statements…";
            await RunExcelStaAsync(() => FinancialStatementPresentationExportService.Export(
                presentation, template, destination, basicInfo, fyTb, pyTb,
                signatories, owners, withLinks, allowIncompleteLinkedWorkbook: withLinks,
                fyStock: fyStock, pyStock: pyStock,
                fyDebtors: fyDebtors, pyDebtors: pyDebtors,
                fyCreditors: fyCreditors, pyCreditors: pyCreditors));
            CleanupHistoricalFinancialStatementExports(client, withLinks ? "Linked" : "Values", ".xlsx", destination);
            TryOpenExport(destination);
            StatusText = withLinks
                ? $"Linked Excel replaced in the Client/FY Automation Folder and opened: {destination}"
                : $"Final values-only Excel replaced in the Client/FY Automation Folder and opened: {destination}";
        }
        catch (Exception ex)
        {
            StatusText = withLinks
                ? $"Linked Excel export failed: {ex.Message}"
                : $"Excel without Links is available only after all mappings and Balance Sheet matching pass: {ex.Message}";
        }
    }

    private async Task ExportPdfAsync()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) { StatusText = "Open a Client/FY first."; return; }
        SynchronizePartyScheduleAuthority();
        // R52 FIXED13: export must not depend on whether the current session happened to receive
        // a fresh Tally background callback. Rehydrate hidden NCE totals from the durable feed
        // immediately before taking the immutable export snapshot.
        if (IsNceEntityType) RehydrateNceSourceAuthorityFromSavedFeed(client.Id, client.FinancialYear);
        UpdatePreparedFinancialStatementState();
        var template = TemplatePath();
        if (!File.Exists(template)) { StatusText = "Financial Statement template library is missing from the application package."; return; }
        var temp = Path.Combine(Path.GetTempPath(), $"AAH_FS_{Guid.NewGuid():N}.xlsx");
        var fyTb = FyTrialBalance.ToArray();
        var pyTb = ExportPyTrialBalance().ToArray();
        var fyStock = FyStock.ToArray();
        var pyStock = NoPreviousYearData ? Array.Empty<FinancialStatementStockRow>() : PyStock.ToArray();
        var fyDebtors = FyDebtors.ToArray();
        var pyDebtors = NoPreviousYearData ? Array.Empty<FinancialStatementLedgerRow>() : PyDebtors.ToArray();
        var fyCreditors = FyCreditors.ToArray();
        var pyCreditors = NoPreviousYearData ? Array.Empty<FinancialStatementLedgerRow>() : PyCreditors.ToArray();
        var signatories = Signatories.Select(x => x.Clone()).ToArray();
        var owners = OwnerPartners.Select(x => x.Clone()).ToArray();
        var basicInfo = CloneBasicInfoForExport();
        try
        {
            var destination = BuildLinkedExportPath(client, "PDF", ".pdf");
            var presentation = PresentationTemplatePath();
            StatusText = "Exporting final Financial Statements PDF…";
            await RunExcelStaAsync(() => FinancialStatementPresentationExportService.Export(
                presentation, template, temp, basicInfo, fyTb, pyTb,
                signatories, owners, true, destination, allowIncompleteLinkedWorkbook: false,
                fyStock: fyStock, pyStock: pyStock,
                fyDebtors: fyDebtors, pyDebtors: pyDebtors,
                fyCreditors: fyCreditors, pyCreditors: pyCreditors));
            CleanupHistoricalFinancialStatementExports(client, "PDF", ".pdf", destination);
            StatusText = $"Final financial-statement PDF replaced in the Client/FY Automation Folder: {destination}";
        }
        catch (Exception ex) { StatusText = $"PDF export requires all mappings/balance checks plus desktop Microsoft Excel: {ex.Message}"; }
        finally { try { File.Delete(temp); } catch { } }
    }

    private FinancialStatementBasicInfo CloneBasicInfoForExport()
    {
        try
        {
            return JsonSerializer.Deserialize<FinancialStatementBasicInfo>(JsonSerializer.Serialize(BasicInfo)) ?? BasicInfo;
        }
        catch { return BasicInfo; }
    }

    private static Task RunExcelStaAsync(Action action)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() =>
        {
            try { action(); completion.TrySetResult(null); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })
        {
            IsBackground = true,
            Name = "AAH Financial Statements Export"
        };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private string BuildLinkedExportPath(ClientProfile client, string variant, string extension)
    {
        var linked = _automationRepository.GetFolders()
            .Where(folder => folder.ClientId == client.Id &&
                             folder.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(folder => Directory.Exists(folder.FolderPath))
            .ThenByDescending(folder => folder.UpdatedUtc)
            .FirstOrDefault();
        if (linked is null || string.IsNullOrWhiteSpace(linked.FolderPath))
            throw new InvalidOperationException("This Client/FY has no linked Automation Folder. Link the folder first; Financial Statements exports do not ask for another save location.");
        if (!Directory.Exists(linked.FolderPath))
            throw new DirectoryNotFoundException($"The linked Automation Folder is unavailable: {linked.FolderPath}");

        // R49: one authoritative output per Client/FY/variant. Re-export replaces this exact
        // path instead of producing a new timestamped workbook every day/run.
        var stem = $"AAH_FINANCIAL_STATEMENTS_{Safe(client.ClientCode)}_{Safe(client.FinancialYear)}_{variant}";
        return Path.Combine(linked.FolderPath, stem + extension);
    }

    private void CleanupHistoricalFinancialStatementExports(ClientProfile client, string variant, string extension, string authoritativePath)
    {
        try
        {
            var folder = Path.GetDirectoryName(authoritativePath);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            var legacyPrefix = $"AAH_FINANCIAL_STATEMENTS_{Safe(client.ClientCode)}_{Safe(client.FinancialYear)}_{variant}_";
            foreach (var path in Directory.EnumerateFiles(folder, legacyPrefix + "*" + extension, SearchOption.TopDirectoryOnly))
            {
                if (path.Equals(authoritativePath, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(path); } catch { }
            }
        }
        catch { }
    }

    private static void TryOpenExport(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // Export success is authoritative even if Windows has no file association.
        }
    }

    private static string TemplatePath() => Path.Combine(AppContext.BaseDirectory, "Assets", "AAH_FS_Final_Complete_Library.xlsx");
    private static string PresentationTemplatePath() => Path.Combine(AppContext.BaseDirectory, "Assets", "AAH_FS_Presentation_Master.xlsx");

    private void ClearCollections() { FyTrialBalance.Clear(); PyTrialBalance.Clear(); FyStock.Clear(); PyStock.Clear(); FyDebtors.Clear(); PyDebtors.Clear(); FyCreditors.Clear(); PyCreditors.Clear(); }
    private static bool ContainsGroup(string value, string target) => (value ?? string.Empty).Contains(target, StringComparison.OrdinalIgnoreCase);
    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
    private static string StateKey(long clientId, string fy) => $"FinancialStatements.BasicInfo.{clientId}.{fy.Trim()}";
    private static string LegacyMappingKey(long clientId, string fy) => $"FinancialStatements.Mapping.{clientId}.{fy.Trim()}";
    private string ProfileMappingKey(long clientId, string fy)
        => $"{LegacyMappingKey(clientId, fy)}.{FinancialStatementWorkbookService.ResolveProfileSheet(BasicInfo.EntityType)}";
    private static string Safe(string value) => string.Concat((value ?? string.Empty).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
    private static string PreviousFinancialYear(string fy)
    {
        var parts = (fy ?? string.Empty).Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var start)) return $"{start - 1}-{start % 100:00}";
        return string.Empty;
    }
    private static string FinancialYearEnd(string fy)
    {
        var parts = (fy ?? string.Empty).Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var start)) return $"31-Mar-{start + 1}";
        return string.Empty;
    }
}

public sealed class FinancialStatementLedgerRow : ViewModelBase
{
    private string _mapping = string.Empty;
    private string _note = string.Empty;
    private string _amountSign = "Auto";
    private string _fsHead = string.Empty;
    private string _noteHead = string.Empty;
    private string _noteSubHead = string.Empty;
    private string _class1 = string.Empty;
    private string _class2 = string.Empty;
    private string _class3 = string.Empty;
    private string _class4 = string.Empty;
    private string _class5 = string.Empty;
    private string _mappingOrigin = "Unmapped";
    private string _stableHierarchyId = string.Empty;
    private decimal? _nonCurrentAmount;
    private string _bifurcationValidation = string.Empty;
    private string _classificationProfile = "NCE";
    private string _natureOverride = string.Empty;
    private string _scheduleClass1 = string.Empty;
    private string _scheduleClass2 = string.Empty;
    private string _scheduleMappingOrigin = "Unmapped";
    private string _scheduleContext = string.Empty;
    private IReadOnlyList<string> _ownerThirdLevelChoices = Array.Empty<string>();
    private bool _suppressScheduleChange;
    private bool _suppressHierarchyChange;
    private IReadOnlyList<FinancialStatementMappingOption> _mappingCatalog = [];
    private IReadOnlyList<FinancialStatementMappingOption> _fullMappingCatalog = [];

    public FinancialStatementLedgerRow()
    {
        FsHeadChoices = [];
        NoteHeadChoices = [];
        NoteSubHeadChoices = [];
        Class1Choices = [];
        Class2Choices = [];
        Class3Choices = [];
        Class4Choices = [];
        Class5Choices = [];
        WorkbookClass3Choices = [];
        ScheduleClass1Choices = [];
        ScheduleClass2Choices = [];
        RefreshScheduleChoices();
    }

    public string Ledger { get; set; } = string.Empty;
    public string ParentGroup { get; set; } = string.Empty;
    public string PrimaryGroup { get; set; } = string.Empty;
    public string MasterId { get; set; } = string.Empty;
    public decimal Opening { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Closing { get; set; }
    public int SourceOrder { get; set; }

    // R52 FIXED9: source-authority metadata is accounting-only. It never changes C1/C2/C3,
    // schedule mapping, row identity, or anything the user selected. XMLStatement can therefore
    // own the final statutory amount while the preparation grid keeps the exact saved mapping.
    public bool HasScheduleSourceAuthority { get; set; }
    public decimal ScheduleSourceAuthorityTotal { get; set; }

    // Original Debtors/Creditors schedule Q/R fields are independent from TB C1/C2/C3.
    // Q chooses the financial-statement schedule header; R is rebuilt from Q.
    public ObservableCollection<string> ScheduleClass1Choices { get; }
    public ObservableCollection<string> ScheduleClass2Choices { get; }
    public string ScheduleClass1
    {
        get => _scheduleClass1;
        set
        {
            if (!SetProperty(ref _scheduleClass1, value ?? string.Empty)) return;
            if (_suppressScheduleChange) return;
            _scheduleClass2 = string.Empty;
            _scheduleMappingOrigin = string.IsNullOrWhiteSpace(_scheduleClass1) ? "Unmapped" : "User Mapped";
            OnPropertyChanged(nameof(ScheduleClass2));
            OnPropertyChanged(nameof(ScheduleMappingOrigin));
            RefreshScheduleChoices();
            OnPropertyChanged(nameof(HasCompleteScheduleMapping));
            OnPropertyChanged(nameof(ScheduleMappedStatus));
            OnPropertyChanged(nameof(MappedStatus));
        }
    }
    public string ScheduleClass2
    {
        get => _scheduleClass2;
        set
        {
            if (!SetProperty(ref _scheduleClass2, value ?? string.Empty)) return;
            if (!_suppressScheduleChange)
            {
                _scheduleMappingOrigin = string.IsNullOrWhiteSpace(_scheduleClass1) && string.IsNullOrWhiteSpace(_scheduleClass2) ? "Unmapped" : "User Mapped";
                OnPropertyChanged(nameof(ScheduleMappingOrigin));
            }
            OnPropertyChanged(nameof(HasCompleteScheduleMapping));
            OnPropertyChanged(nameof(ScheduleMappedStatus));
            OnPropertyChanged(nameof(MappedStatus));
        }
    }
    public void ConfigureScheduleContext(string? context)
    {
        _scheduleContext = (context ?? string.Empty).Trim();
        _suppressScheduleChange = true;
        try
        {
            if (string.IsNullOrWhiteSpace(_scheduleClass1))
            {
                if (_scheduleContext.Equals("Creditor", StringComparison.OrdinalIgnoreCase))
                    SetProperty(ref _scheduleClass1, "Trade Payables", nameof(ScheduleClass1));
                else if (_scheduleContext.Equals("Debtor", StringComparison.OrdinalIgnoreCase))
                    SetProperty(ref _scheduleClass1, "Trade Receivables", nameof(ScheduleClass1));
            }
            RefreshScheduleChoices();
        }
        finally { _suppressScheduleChange = false; }
        OnPropertyChanged(nameof(HasScheduleContext));
        OnPropertyChanged(nameof(HasCompleteScheduleMapping));
        OnPropertyChanged(nameof(ScheduleMappedStatus));
        OnPropertyChanged(nameof(MappedStatus));
    }

    public string ScheduleMappingOrigin => _scheduleMappingOrigin;

    public void RestoreScheduleMapping(string? class1, string? class2, string? origin = null)
    {
        _suppressScheduleChange = true;
        try
        {
            var c1 = (class1 ?? string.Empty).Trim();
            if (_scheduleContext.Equals("Debtor", StringComparison.OrdinalIgnoreCase) &&
                c1.Equals("Trade Payables", StringComparison.OrdinalIgnoreCase))
                c1 = "Trade Receivables";
            else if (_scheduleContext.Equals("Creditor", StringComparison.OrdinalIgnoreCase) &&
                     c1.Equals("Trade Receivables", StringComparison.OrdinalIgnoreCase))
                c1 = "Trade Payables";
            if (c1.Length == 0)
            {
                if (_scheduleContext.Equals("Creditor", StringComparison.OrdinalIgnoreCase)) c1 = "Trade Payables";
                else if (_scheduleContext.Equals("Debtor", StringComparison.OrdinalIgnoreCase)) c1 = "Trade Receivables";
            }
            SetProperty(ref _scheduleClass1, c1, nameof(ScheduleClass1));
            RefreshScheduleChoices();
            var candidate = FinancialStatementWorkbookRules.NormalizeScheduleC2(c1, class2);
            SetProperty(ref _scheduleClass2, ScheduleClass2Choices.Contains(candidate, StringComparer.OrdinalIgnoreCase)
                ? ScheduleClass2Choices.First(x => x.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                : string.Empty, nameof(ScheduleClass2));
        }
        finally { _suppressScheduleChange = false; }
        _scheduleMappingOrigin = string.IsNullOrWhiteSpace(_scheduleClass1) && string.IsNullOrWhiteSpace(_scheduleClass2)
            ? "Unmapped"
            : string.IsNullOrWhiteSpace(origin) ? "Mapped" : origin.Trim();
        OnPropertyChanged(nameof(ScheduleMappingOrigin));
        OnPropertyChanged(nameof(HasScheduleContext));
        OnPropertyChanged(nameof(HasCompleteScheduleMapping));
        OnPropertyChanged(nameof(ScheduleMappedStatus));
        OnPropertyChanged(nameof(MappedStatus));
    }

    public void ClearScheduleContext()
    {
        _suppressScheduleChange = true;
        try
        {
            _scheduleContext = string.Empty;
            SetProperty(ref _scheduleClass1, string.Empty, nameof(ScheduleClass1));
            SetProperty(ref _scheduleClass2, string.Empty, nameof(ScheduleClass2));
            _scheduleMappingOrigin = "Unmapped";
            RefreshScheduleChoices();
        }
        finally { _suppressScheduleChange = false; }
        OnPropertyChanged(nameof(ScheduleMappingOrigin));
        OnPropertyChanged(nameof(HasScheduleContext));
        OnPropertyChanged(nameof(HasCompleteScheduleMapping));
        OnPropertyChanged(nameof(ScheduleMappedStatus));
        OnPropertyChanged(nameof(MappedStatus));
    }

    public bool RestoreScheduleMappingWithLegacy(string? class1, string? class2, string? legacyTbDetail)
    {
        if (!string.IsNullOrWhiteSpace(class1) || !string.IsNullOrWhiteSpace(class2))
        {
            RestoreScheduleMapping(class1, class2);
            return false;
        }

        var parent = (ParentGroup ?? string.Empty).Trim().ToUpperInvariant();
        var detail = (legacyTbDetail ?? string.Empty).Trim();
        string schedule1 = string.Empty;
        string schedule2 = string.Empty;
        if (parent.Contains("SUNDRY CREDITOR", StringComparison.Ordinal) || parent.Contains("TRADE PAYABLE", StringComparison.Ordinal))
        {
            schedule1 = "Trade Payables";
            schedule2 = detail switch
            {
                "MSME" => "MSME",
                "Non-MSME" => "Others",
                "Disputed MSME" => "Disputed MSME",
                "Disputed Other" or "Disputed Others" => "Disputed Others",
                _ => string.Empty
            };
        }
        else if (parent.Contains("SUNDRY DEBTOR", StringComparison.Ordinal) || parent.Contains("TRADE RECEIV", StringComparison.Ordinal))
        {
            schedule1 = "Trade Receivables";
            schedule2 = detail switch
            {
                "Secured Considered Good" => "Secured considered good - Undisputed",
                "Unsecured Considered Good" => "Unsecured considered good - Undisputed",
                "Doubtful" => "Doubtful - Undisputed",
                "Less: Allowance for Doubtful Debts" => "Less: Provisions for doubtful debts",
                _ => string.Empty
            };
        }

        RestoreScheduleMapping(schedule1, schedule2);
        return schedule2.Length > 0;
    }

    private void RefreshScheduleChoices()
    {
        ReplaceChoices(ScheduleClass1Choices, FinancialStatementWorkbookRules.ScheduleC1Choices);
        if (string.IsNullOrWhiteSpace(_scheduleClass1))
        {
            if (_scheduleContext.Equals("Creditor", StringComparison.OrdinalIgnoreCase)) _scheduleClass1 = "Trade Payables";
            else if (_scheduleContext.Equals("Debtor", StringComparison.OrdinalIgnoreCase)) _scheduleClass1 = "Trade Receivables";
            if (!string.IsNullOrWhiteSpace(_scheduleClass1)) OnPropertyChanged(nameof(ScheduleClass1));
        }
        ReplaceChoices(ScheduleClass2Choices, FinancialStatementWorkbookRules.GetScheduleC2Choices(_scheduleClass1));
        if (!string.IsNullOrWhiteSpace(_scheduleClass2) &&
            !ScheduleClass2Choices.Contains(_scheduleClass2, StringComparer.OrdinalIgnoreCase))
        {
            _scheduleClass2 = string.Empty;
            OnPropertyChanged(nameof(ScheduleClass2));
        }
        OnPropertyChanged(nameof(ScheduleClass1Choices));
        OnPropertyChanged(nameof(ScheduleClass2Choices));
    }

    // Supplied trial workbook columns J/K. For Trade Receivables this is >6 months / <6 months;
    // for the other supported H3 groups it is Non-current / Current. K is always Closing - J.
    public decimal? NonCurrentAmount
    {
        get => _nonCurrentAmount;
        set
        {
            if (FinancialStatementClassificationService.IsCurrentOnlyLine(_classificationProfile, Classification4)) value = null;
            if (!FinancialStatementClassificationService.TryValidateBifurcation(Closing, value, out var error))
            {
                BifurcationValidation = error;
                OnPropertyChanged(nameof(NonCurrentAmount));
                return;
            }
            if (!SetProperty(ref _nonCurrentAmount, value)) return;
            BifurcationValidation = string.Empty;
            OnPropertyChanged(nameof(CurrentAmount));
            OnPropertyChanged(nameof(CurrentBifurcatedAmount));
            OnPropertyChanged(nameof(CanonicalMappingId));
        }
    }
    public decimal CurrentAmount => Closing - NonCurrentAmount.GetValueOrDefault();
    public decimal? CurrentBifurcatedAmount => BifurcationKind == FinancialStatementClassificationService.BifurcationKind.None ? null : CurrentAmount;
    public string BifurcationValidation { get => _bifurcationValidation; private set => SetProperty(ref _bifurcationValidation, value ?? string.Empty); }
    public FinancialStatementClassificationService.BifurcationKind BifurcationKind
        => FinancialStatementClassificationService.GetBifurcationKind(_classificationProfile, Classification3);
    public bool CanEditBifurcation => BifurcationKind != FinancialStatementClassificationService.BifurcationKind.None &&
                                      !FinancialStatementClassificationService.IsCurrentOnlyLine(_classificationProfile, Classification4);
    public string FirstBifurcationCaption => BifurcationKind == FinancialStatementClassificationService.BifurcationKind.ReceivableAge ? "> 6 Months" : "Non-current";
    public string SecondBifurcationCaption => BifurcationKind == FinancialStatementClassificationService.BifurcationKind.ReceivableAge ? "< 6 Months" : "Current";
    public string CanonicalMappingId => FinancialStatementClassificationService.ResolveCanonicalId(_classificationProfile, this);

    // R15 R14 R8: keep the statement/nature visible, but separate from the workbook
    // C1/C2/C3 mapping columns. The original TB places Nature of Group in the source
    // block and C1-C3 in Mapping Details; AAH shows Statement + Nature immediately
    // before those mapping columns so Balance Sheet / P&L and Assets/Liabilities remain
    // visible without being mistaken for workbook dropdown levels.
    public string StatementDisplay
    {
        get
        {
            // R9 FIX4: Statement follows the source Nature of Group first, exactly as the
            // preparation TB does. A statutory mapping selection must not visually move a
            // source Liability row to the Asset side (or Income to Expenses).
            var nature = NatureDisplay;
            if (nature.Equals("Assets", StringComparison.OrdinalIgnoreCase) || nature.Equals("Liabilities", StringComparison.OrdinalIgnoreCase)) return "Balance Sheet";
            if (nature.Equals("Income", StringComparison.OrdinalIgnoreCase) || nature.Equals("Expenses", StringComparison.OrdinalIgnoreCase)) return "P&L Account";
            return _class1;
        }
    }

    public ObservableCollection<string> NatureChoices { get; } = new(new[] { "Assets", "Liabilities", "Income", "Expenses" });
    public string SourceNatureDisplay => FinancialStatementTrialBalanceOrder.ResolveNatureOfGroup(Ledger, ParentGroup, PrimaryGroup);
    public string NatureOverride => _natureOverride;
    public string NatureDisplay
    {
        get => string.IsNullOrWhiteSpace(_natureOverride) ? (string.IsNullOrWhiteSpace(SourceNatureDisplay) ? _class2 : SourceNatureDisplay) : _natureOverride;
        set => ApplyNatureOverride(value, userInitiated: true);
    }

    public void RestoreNatureOverride(string? value) => ApplyNatureOverride(value, userInitiated: false);

    private void ApplyNatureOverride(string? value, bool userInitiated)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (!NatureChoices.Contains(normalized, StringComparer.OrdinalIgnoreCase)) normalized = string.Empty;
        if (normalized.Equals(SourceNatureDisplay, StringComparison.OrdinalIgnoreCase)) normalized = string.Empty;
        if (_natureOverride.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return;
        _natureOverride = normalized;

        var nature = NatureDisplay;
        _mappingCatalog = string.IsNullOrWhiteSpace(nature)
            ? _fullMappingCatalog
            : _fullMappingCatalog.Where(x => x.Class2.Equals(nature, StringComparison.OrdinalIgnoreCase)).ToArray();
        ReplaceChoices(FsHeadChoices, _mappingCatalog.Select(x => x.FsHead));
        ReplaceChoices(NoteHeadChoices, _mappingCatalog.Select(x => x.NoteHead));
        ReplaceChoices(NoteSubHeadChoices, _mappingCatalog.Select(x => x.NoteSubHead));

        if (userInitiated)
        {
            _suppressHierarchyChange = true;
            try
            {
                SetProperty(ref _class1, nature.Equals("Assets", StringComparison.OrdinalIgnoreCase) || nature.Equals("Liabilities", StringComparison.OrdinalIgnoreCase) ? "Balance Sheet" :
                    nature.Equals("Income", StringComparison.OrdinalIgnoreCase) || nature.Equals("Expenses", StringComparison.OrdinalIgnoreCase) ? "P&L Account" : string.Empty, nameof(Classification1));
                SetProperty(ref _class2, nature, nameof(Classification2));
                SetProperty(ref _class3, string.Empty, nameof(Classification3));
                SetProperty(ref _class4, string.Empty, nameof(Classification4));
                SetProperty(ref _class5, string.Empty, nameof(Classification5));
                ClearInternalMapping();
                _mappingOrigin = "Unmapped";
            }
            finally
            {
                _suppressHierarchyChange = false;
            }
        }

        RefreshHierarchyChoices();
        OnPropertyChanged(nameof(SourceNatureDisplay));
        OnPropertyChanged(nameof(NatureOverride));
        OnPropertyChanged(nameof(MappedStatus));
        NotifyStatementNatureDisplay();
    }

    private void NotifyStatementNatureDisplay()
    {
        OnPropertyChanged(nameof(StatementDisplay));
        OnPropertyChanged(nameof(NatureDisplay));
    }

    // Legacy/internal presentation authority retained for renderer and migration.
    public ObservableCollection<string> FsHeadChoices { get; }
    public ObservableCollection<string> NoteHeadChoices { get; }
    public ObservableCollection<string> NoteSubHeadChoices { get; }
    public string Mapping { get => _mapping; set { if (SetProperty(ref _mapping, value ?? string.Empty)) OnPropertyChanged(nameof(MappedStatus)); } }
    public string Note { get => _note; set => SetProperty(ref _note, value ?? string.Empty); }
    public string AmountSign { get => _amountSign; set => SetProperty(ref _amountSign, string.IsNullOrWhiteSpace(value) ? "Auto" : value); }
    public string FsHead { get => _fsHead; private set => SetProperty(ref _fsHead, value ?? string.Empty); }
    public string NoteHead { get => _noteHead; private set => SetProperty(ref _noteHead, value ?? string.Empty); }
    public string NoteSubHead { get => _noteSubHead; private set => SetProperty(ref _noteSubHead, value ?? string.Empty); }

    // R15 R10: exact trial-workbook H1-H5 hierarchy.
    public ObservableCollection<string> Class1Choices { get; }
    public ObservableCollection<string> Class2Choices { get; }
    public ObservableCollection<string> Class3Choices { get; }
    public ObservableCollection<string> Class4Choices { get; }
    public ObservableCollection<string> Class5Choices { get; }
    // R15 R14 R8: the supplied workbooks expose exactly three TB mapping columns:
    // C1 (FS Heads), C2 (Notes Heads), C3 (Notes Sub-Heads). AAH keeps Statement/Nature
    // and schedule-specific row identity internally, but they are not workbook dropdowns.
    public ObservableCollection<string> WorkbookClass3Choices { get; }
    public string WorkbookClassification3
    {
        get => UsesWorkbookThirdLevel() ? _class5 : string.Empty;
        set
        {
            if (!UsesWorkbookThirdLevel()) return;
            Classification5 = value ?? string.Empty;
        }
    }

    private bool UsesWorkbookThirdLevel()
        => FinancialStatementWorkbookRules.RequiresC3(_classificationProfile, _class3, _class4);


    public bool CanSpecifyClass5 => _mappingCatalog.Any(x =>
        x.Class1.Equals(Classification1, StringComparison.OrdinalIgnoreCase) &&
        x.Class2.Equals(Classification2, StringComparison.OrdinalIgnoreCase) &&
        x.Class3.Equals(Classification3, StringComparison.OrdinalIgnoreCase) &&
        x.Class4.Equals(Classification4, StringComparison.OrdinalIgnoreCase) &&
        x.Class5.Equals("Specify nature", StringComparison.OrdinalIgnoreCase));

    public string Classification1
    {
        get => _class1;
        set
        {
            if (!SetProperty(ref _class1, value ?? string.Empty)) return;
            NotifyStatementNatureDisplay();
            if (_suppressHierarchyChange) return;
            ClearHierarchyBelow(1);
            RefreshHierarchyChoices(2);
            ResolveMappingFromHierarchy(true);
        }
    }
    public string Classification2
    {
        get => _class2;
        set
        {
            if (!SetProperty(ref _class2, value ?? string.Empty)) return;
            NotifyStatementNatureDisplay();
            if (_suppressHierarchyChange) return;
            ClearHierarchyBelow(2);
            RefreshHierarchyChoices(3);
            ResolveMappingFromHierarchy(true);
        }
    }
    public string Classification3
    {
        get => _class3;
        set
        {
            if (!SetProperty(ref _class3, value ?? string.Empty)) return;
            if (_suppressHierarchyChange) return;
            ClearHierarchyBelow(3);
            // The original workbooks start with Tally Group -> first statutory group.
            // Statement and Nature stay read-only in AAH. Nature is a hard side boundary
            // (Assets/Liabilities/Income/Expenses); inside that side the full factory C1 list
            // remains available, with source-group workbook suggestions ordered first.
            DeriveStatementNatureFromClass3();
            RefreshHierarchyChoices(4);
            OnPropertyChanged(nameof(WorkbookClassification3));
            NotifyBifurcationState();
            ResolveMappingFromHierarchy(true);
        }
    }
    public string Classification4
    {
        get => _class4;
        set
        {
            if (!SetProperty(ref _class4, value ?? string.Empty)) return;
            if (_suppressHierarchyChange) return;
            ClearHierarchyBelow(4);
            RefreshHierarchyChoices(5);
            // Proprietor/partner identity is row-specific. When Basic Info / the Capital ledger
            // proves exactly one owner for this row, populate the mandatory workbook C3 now.
            // Multiple owners remain a deliberate user choice.
            if (FinancialStatementWorkbookRules.IsDynamicNceOwnerC3(_classificationProfile, Classification3, Classification4) &&
                string.IsNullOrWhiteSpace(_class5))
            {
                var owner = ResolveOwnerThirdLevelFromLedger(Classification3, Classification4);
                if (!string.IsNullOrWhiteSpace(owner)) SetProperty(ref _class5, owner, nameof(Classification5));
            }
            OnPropertyChanged(nameof(WorkbookClassification3));
            OnPropertyChanged(nameof(CanSpecifyClass5));
            NotifyBifurcationState();
            ResolveMappingFromHierarchy(true);
        }
    }
    public string Classification5
    {
        get => _class5;
        set
        {
            if (!SetProperty(ref _class5, value ?? string.Empty)) return;
            if (_suppressHierarchyChange) return;
            OnPropertyChanged(nameof(WorkbookClassification3));
            ResolveMappingFromHierarchy(true);
        }
    }

    public string MappedStatus => !HasResolvedMapping
        ? "Unmapped"
        : HasScheduleContext && !HasCompleteScheduleMapping ? "Schedule Pending" : _mappingOrigin;
    public string MappingOrigin => _mappingOrigin;
    public bool HasScheduleContext => !string.IsNullOrWhiteSpace(_scheduleContext);
    public bool HasCompleteScheduleMapping => !HasScheduleContext ||
        (!string.IsNullOrWhiteSpace(ScheduleClass1) && !string.IsNullOrWhiteSpace(ScheduleClass2) &&
         ScheduleClass2Choices.Contains(ScheduleClass2));
    public string ScheduleMappedStatus => HasCompleteScheduleMapping ? "Mapped" : "Unmapped";
    public string StableHierarchyId => _stableHierarchyId;

    public void ConfigureMappingCatalog(
        IReadOnlyList<FinancialStatementMappingOption> catalog,
        string? profile = null,
        IReadOnlyList<string>? ownerNames = null)
    {
        _classificationProfile = string.IsNullOrWhiteSpace(profile) ? "NCE" : profile.Trim();
        _ownerThirdLevelChoices = (ownerNames ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(11)
            .ToArray();

        var fullCatalog = catalog ?? [];
        _fullMappingCatalog = fullCatalog.ToArray();
        var effectiveNature = NatureDisplay;
        _mappingCatalog = string.IsNullOrWhiteSpace(effectiveNature)
            ? _fullMappingCatalog
            : _fullMappingCatalog.Where(x => x.Class2.Equals(effectiveNature, StringComparison.OrdinalIgnoreCase)).ToArray();

        ReplaceChoices(FsHeadChoices, _mappingCatalog.Select(x => x.FsHead));
        ReplaceChoices(NoteHeadChoices, _mappingCatalog.Select(x => x.NoteHead));
        ReplaceChoices(NoteSubHeadChoices, _mappingCatalog.Select(x => x.NoteSubHead));
        if (!string.IsNullOrWhiteSpace(_class3)) DeriveStatementNatureFromClass3();
        RefreshHierarchyChoices();
        NotifyStatementNatureDisplay();

        // R39: PopulateNcePeriod receives MappingHeadOne/MappingHeadTwo before the row has a
        // workbook catalog. Retain that partial Tally NCE hierarchy through the empty-catalog
        // phase and resolve it as soon as the real catalog is attached. Without this step the
        // first empty-catalog resolution changed the origin to Unmapped and ApplyMappings later
        // replaced the plug-in path with a generic Tally-group suggestion.
        if (Mapping.Length == 0 && _mappingOrigin.StartsWith("Tally NCE", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(_class3))
            ResolveMappingFromHierarchy(false, _mappingOrigin);

        if (Mapping.Length > 0 || !string.IsNullOrWhiteSpace(_stableHierarchyId))
        {
            FinancialStatementMappingOption? option = null;
            if (!string.IsNullOrWhiteSpace(_stableHierarchyId))
                option = _mappingCatalog.FirstOrDefault(x => x.StableHierarchyId.Equals(_stableHierarchyId, StringComparison.OrdinalIgnoreCase));

            option ??= _mappingCatalog.FirstOrDefault(x =>
                (string.IsNullOrWhiteSpace(Mapping) || x.MappingKey.Equals(Mapping, StringComparison.OrdinalIgnoreCase)) &&
                x.Class1.Equals(Classification1, StringComparison.OrdinalIgnoreCase) &&
                x.Class2.Equals(Classification2, StringComparison.OrdinalIgnoreCase) &&
                x.Class3.Equals(Classification3, StringComparison.OrdinalIgnoreCase) &&
                x.Class4.Equals(Classification4, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(Classification5) ||
                 x.Class5.Equals(Classification5, StringComparison.OrdinalIgnoreCase)));

            if (option is null && FinancialStatementWorkbookRules.IsDynamicNceOwnerC3(
                    _classificationProfile, Classification3, Classification4))
            {
                var owner = ResolveOwnerThirdLevelFromLedger(Classification3, Classification4);
                if (owner.Length == 0 && _ownerThirdLevelChoices.Count == 1) owner = _ownerThirdLevelChoices[0];
                if (owner.Length > 0)
                    option = _mappingCatalog.FirstOrDefault(x =>
                        x.Class3.Equals(Classification3, StringComparison.OrdinalIgnoreCase) &&
                        x.Class4.Equals(Classification4, StringComparison.OrdinalIgnoreCase) &&
                        x.Class5.Equals(owner, StringComparison.OrdinalIgnoreCase));
            }

            if (option is null && !string.IsNullOrWhiteSpace(Mapping))
            {
                var keyMatches = _mappingCatalog.Where(x => x.MappingKey.Equals(Mapping, StringComparison.OrdinalIgnoreCase)).ToArray();
                var oldSlot = TryLegacyOwnerSlot(Classification4);
                if (oldSlot > 0 && oldSlot <= keyMatches.Length) option = keyMatches[oldSlot - 1];
                option ??= keyMatches.Length == 1 ? keyMatches[0] : null;
            }

            if (option is null) ClearInternalMapping();
            else SetMappingOption(option, _mappingOrigin, Classification5);
        }
    }

    public bool HasResolvedMapping => !string.IsNullOrWhiteSpace(Mapping) &&
        FinancialStatementWorkbookRules.IsValidVisiblePath(_classificationProfile, Classification3, Classification4, WorkbookClassification3);

    public bool HasAnyMappingSelection => HasResolvedMapping ||
        !string.IsNullOrWhiteSpace(Mapping) || !string.IsNullOrWhiteSpace(StableHierarchyId) ||
        !string.IsNullOrWhiteSpace(Classification3) || !string.IsNullOrWhiteSpace(Classification4) ||
        !string.IsNullOrWhiteSpace(Classification5);

    public bool HasAnyScheduleSelection =>
        !string.IsNullOrWhiteSpace(ScheduleClass2) ||
        !string.Equals(ScheduleMappingOrigin, "Unmapped", StringComparison.OrdinalIgnoreCase);

    public static bool IsHierarchySequenceContiguous(string? class1, string? class2, string? class3, string? class4, string? class5)
    {
        // R15 R14 R8: internal Classification1/2 are Statement/Nature only. The supplied
        // workbook TB exposes Classification3/4/5 as C1/C2/C3, so saved workbook paths
        // remain valid even when the old internal Statement/Nature text was absent.
        var c3 = !string.IsNullOrWhiteSpace(class3);
        var c4 = !string.IsNullOrWhiteSpace(class4);
        var c5 = !string.IsNullOrWhiteSpace(class5);
        if (c4 && !c3) return false;
        if (c5 && (!c3 || !c4)) return false;
        return true;
    }

    public bool TryApplyResolvedHierarchyFrom(FinancialStatementLedgerRow source, string origin = "User Mapped")
    {
        if (source is null || !source.HasResolvedMapping) return false;

        // Same-Tally-Group copies the *visible workbook path only*. Party-schedule detail
        // (MSME/debtor status) is independent and is never copied. Owner/partner C3 is also
        // row-specific, so a proprietor/partner identity must never be propagated to another
        // capital ledger.
        if (FinancialStatementWorkbookRules.IsDynamicNceOwnerC3(
                _classificationProfile, source.Classification3, source.Classification4))
            return false;

        if (!FinancialStatementWorkbookRules.IsValidVisiblePath(
                _classificationProfile, source.Classification3, source.Classification4, source.WorkbookClassification3))
            return false;

        SetPartialHierarchy(
            source.Classification1,
            source.Classification2,
            source.Classification3,
            source.Classification4,
            source.WorkbookClassification3,
            origin);
        return HasResolvedMapping;
    }

    public bool TryApplyCrossYearMappingFrom(FinancialStatementLedgerRow source, string origin)
    {
        if (source is null || !source.HasResolvedMapping || HasResolvedMapping) return false;
        if (!FinancialStatementWorkbookRules.IsValidVisiblePath(
                _classificationProfile, source.Classification3, source.Classification4, source.WorkbookClassification3))
            return false;

        // FY/PY exact-ledger carry-forward is different from same-Tally-group propagation:
        // it is safe to carry the row-specific owner/partner C3 because the ledger identity
        // itself is the match. Schedules/balances are deliberately not copied.
        ApplyNatureOverride(source.NatureDisplay, userInitiated: false);
        SetPartialHierarchy(
            source.Classification1,
            source.Classification2,
            source.Classification3,
            source.Classification4,
            source.WorkbookClassification3,
            origin);
        return HasResolvedMapping;
    }

    public void SetMappingOption(FinancialStatementMappingOption option, string origin = "User Mapped", string? class5Override = null)
    {
        if (option is null) return;
        _suppressHierarchyChange = true;
        try
        {
            SetProperty(ref _class1, option.Class1 ?? string.Empty, nameof(Classification1));
            SetProperty(ref _class2, option.Class2 ?? string.Empty, nameof(Classification2));
            SetProperty(ref _class3, option.Class3 ?? string.Empty, nameof(Classification3));
            SetProperty(ref _class4, option.Class4 ?? string.Empty, nameof(Classification4));
            // Owner/Partner C3 is dynamic. StableHierarchyId identifies the owner slot; when
            // Basic Info changes, always display the catalog's current owner name instead of
            // resurrecting an older saved visible label through class5Override.
            var ownerDynamic = _classificationProfile.Equals("NCE", StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(option.Class3, "Owner/Partners Capital Account", StringComparison.OrdinalIgnoreCase) &&
                               (string.Equals(option.Class4, "Capital Account", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(option.Class4, "Current Account", StringComparison.OrdinalIgnoreCase));
            var resolvedClass5 = ownerDynamic || string.IsNullOrWhiteSpace(class5Override) ? option.Class5 : class5Override;
            SetProperty(ref _class5, resolvedClass5 ?? string.Empty, nameof(Classification5));
            FsHead = option.FsHead;
            NoteHead = option.NoteHead;
            NoteSubHead = option.NoteSubHead;
            Mapping = option.MappingKey;
            Note = option.Note;
            _stableHierarchyId = option.StableHierarchyId ?? string.Empty;
            _mappingOrigin = string.IsNullOrWhiteSpace(origin) ? "User Mapped" : origin;
            RefreshHierarchyChoices();
            NotifyStatementNatureDisplay();
            OnPropertyChanged(nameof(WorkbookClassification3));
            NotifyBifurcationState();
        }
        finally { _suppressHierarchyChange = false; }
        OnPropertyChanged(nameof(MappedStatus));
    }

    public void SetPartialHierarchy(string class1, string class2, string class3, string class4 = "", string class5 = "", string origin = "Unmapped")
    {
        _suppressHierarchyChange = true;
        try
        {
            ClearInternalMapping();
            SetProperty(ref _class1, class1 ?? string.Empty, nameof(Classification1));
            SetProperty(ref _class2, class2 ?? string.Empty, nameof(Classification2));
            SetProperty(ref _class3, class3 ?? string.Empty, nameof(Classification3));
            SetProperty(ref _class4, class4 ?? string.Empty, nameof(Classification4));
            SetProperty(ref _class5, class5 ?? string.Empty, nameof(Classification5));
            if (!string.IsNullOrWhiteSpace(_class3)) DeriveStatementNatureFromClass3();
            _mappingOrigin = origin;
            RefreshHierarchyChoices();
            NotifyStatementNatureDisplay();
            OnPropertyChanged(nameof(WorkbookClassification3));
            NotifyBifurcationState();
        }
        finally { _suppressHierarchyChange = false; }
        ResolveMappingFromHierarchy(false, origin);
    }

    public void ApplyTrialSuggestion(FinancialStatementPresentationExportService.HierarchySuggestion? suggestion)
    {
        if (suggestion is null) { ClearMappingPresentation(); return; }

        // Suggestions are only a convenience. The original workbook path is the final authority.
        // Sanitize every suggested C1/C2/C3 before it can reach the grid so an obsolete internal
        // name can never create a mapped-but-impossible row or cross the Asset/Liability boundary.
        var statement = StatementDisplay;
        var nature = NatureDisplay;
        var c1 = (suggestion.Class3 ?? string.Empty).Trim();
        var allowedC1 = FinancialStatementWorkbookRules.GetC1Choices(_classificationProfile, nature, ParentGroup, PrimaryGroup);
        if (!allowedC1.Contains(c1, StringComparer.OrdinalIgnoreCase)) c1 = string.Empty;

        var c2 = string.Empty;
        var c3 = string.Empty;
        if (!string.IsNullOrWhiteSpace(c1))
        {
            var c2Choices = FinancialStatementWorkbookRules.GetC2Choices(_classificationProfile, c1);
            var suggestedC2 = (suggestion.Class4 ?? string.Empty).Trim();
            if (c2Choices.Contains(suggestedC2, StringComparer.OrdinalIgnoreCase)) c2 = suggestedC2;

            if (FinancialStatementWorkbookRules.IsDynamicNceOwnerC3(_classificationProfile, c1, c2))
            {
                c3 = ResolveOwnerThirdLevelFromLedger(c1, c2);
            }
            else if (!string.IsNullOrWhiteSpace(c2))
            {
                var c3Choices = FinancialStatementWorkbookRules.GetC3Choices(_classificationProfile, c1, c2);
                var suggestedC3 = (suggestion.Class5 ?? string.Empty).Trim();
                if (c3Choices.Contains(suggestedC3, StringComparer.OrdinalIgnoreCase)) c3 = suggestedC3;
            }
        }

        SetPartialHierarchy(statement, nature, c1, c2, c3, "Auto Mapped");
    }

    private void NotifyBifurcationState()
    {
        if (FinancialStatementClassificationService.IsCurrentOnlyLine(_classificationProfile, Classification4) && _nonCurrentAmount.HasValue)
            _nonCurrentAmount = null;
        OnPropertyChanged(nameof(BifurcationKind));
        OnPropertyChanged(nameof(CanEditBifurcation));
        OnPropertyChanged(nameof(FirstBifurcationCaption));
        OnPropertyChanged(nameof(SecondBifurcationCaption));
        OnPropertyChanged(nameof(NonCurrentAmount));
        OnPropertyChanged(nameof(CurrentAmount));
        OnPropertyChanged(nameof(CurrentBifurcatedAmount));
        OnPropertyChanged(nameof(CanonicalMappingId));
    }

    private void ResolveMappingFromHierarchy(bool userInitiated, string? preferredOrigin = null)
    {
        var candidates = _mappingCatalog.Where(x =>
            x.Class1.Equals(Classification1, StringComparison.OrdinalIgnoreCase) &&
            x.Class2.Equals(Classification2, StringComparison.OrdinalIgnoreCase) &&
            x.Class3.Equals(Classification3, StringComparison.OrdinalIgnoreCase) &&
            x.Class4.Equals(Classification4, StringComparison.OrdinalIgnoreCase)).ToArray();
        FinancialStatementMappingOption? option = null;
        var workbookThirdLevelRequired = FinancialStatementWorkbookRules.RequiresC3(
            _classificationProfile, Classification3, Classification4);
        if (workbookThirdLevelRequired && string.IsNullOrWhiteSpace(WorkbookClassification3))
        {
            ClearInternalMapping();
            _mappingOrigin = PreserveUnresolvedNceOrigin(preferredOrigin);
            OnPropertyChanged(nameof(MappedStatus));
            return;
        }
        if (!string.IsNullOrWhiteSpace(Classification5))
        {
            option = candidates.FirstOrDefault(x => x.Class5.Equals(Classification5, StringComparison.OrdinalIgnoreCase));
            option ??= candidates.FirstOrDefault(x => x.Class5.Equals("Specify nature", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var distinctKeys = candidates.Select(x => x.MappingKey).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (distinctKeys.Length == 1) option = candidates.FirstOrDefault();
            else option = candidates.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Class5));
        }

        var branchRequiresC2 = _mappingCatalog.Any(x =>
            x.Class3.Equals(Classification3, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(x.Class4));
        if (option is null || string.IsNullOrWhiteSpace(Classification3) ||
            (branchRequiresC2 && string.IsNullOrWhiteSpace(Classification4)))
        {
            ClearInternalMapping();
            _mappingOrigin = PreserveUnresolvedNceOrigin(preferredOrigin);
            OnPropertyChanged(nameof(MappedStatus));
            return;
        }

        FsHead = option.FsHead;
        NoteHead = option.NoteHead;
        NoteSubHead = option.NoteSubHead;
        Mapping = option.MappingKey;
        Note = option.Note;
        _stableHierarchyId = option.StableHierarchyId ?? string.Empty;
        _mappingOrigin = userInitiated ? "User Mapped" : (string.IsNullOrWhiteSpace(preferredOrigin) ? "Auto Mapped" : preferredOrigin);
        OnPropertyChanged(nameof(MappedStatus));
    }

    private static string PreserveUnresolvedNceOrigin(string? preferredOrigin)
        => (preferredOrigin ?? string.Empty).StartsWith("Tally NCE", StringComparison.OrdinalIgnoreCase)
            ? preferredOrigin!.Trim()
            : "Unmapped";

    private void ClearInternalMapping()
    {
        Mapping = string.Empty;
        Note = string.Empty;
        _stableHierarchyId = string.Empty;
        FsHead = string.Empty;
        NoteHead = string.Empty;
        NoteSubHead = string.Empty;
    }

    private void ClearHierarchyBelow(int level)
    {
        _suppressHierarchyChange = true;
        try
        {
            if (level < 2) SetProperty(ref _class2, string.Empty, nameof(Classification2));
            if (level < 3) SetProperty(ref _class3, string.Empty, nameof(Classification3));
            if (level < 4) SetProperty(ref _class4, string.Empty, nameof(Classification4));
            if (level < 5) SetProperty(ref _class5, string.Empty, nameof(Classification5));
            ClearInternalMapping();
            _mappingOrigin = "Unmapped";
        }
        finally { _suppressHierarchyChange = false; }
        NotifyStatementNatureDisplay();
    }

    private void RefreshHierarchyChoices(int startLevel = 1)
    {
        // One visible authority only: exact workbook paths.  Tally group influences the
        // preferred order of C1, while Nature of Group is the hard side boundary.
        if (startLevel <= 1) ReplaceChoices(Class1Choices, string.IsNullOrWhiteSpace(Classification3)
            ? Array.Empty<string>()
            : _mappingCatalog.Where(x => x.Class3.Equals(Classification3, StringComparison.OrdinalIgnoreCase)).Select(x => x.Class1));
        if (startLevel <= 2) ReplaceChoices(Class2Choices, string.IsNullOrWhiteSpace(Classification3)
            ? Array.Empty<string>()
            : _mappingCatalog.Where(x => x.Class3.Equals(Classification3, StringComparison.OrdinalIgnoreCase)).Select(x => x.Class2));

        if (startLevel <= 3)
            ReplaceChoices(Class3Choices, FinancialStatementWorkbookRules.GetC1Choices(
                _classificationProfile, NatureDisplay, ParentGroup, PrimaryGroup));

        if (startLevel <= 4)
            ReplaceChoices(Class4Choices, FinancialStatementWorkbookRules.GetC2Choices(
                _classificationProfile, Classification3));

        if (startLevel <= 5)
        {
            var c3 = FinancialStatementWorkbookRules.IsDynamicNceOwnerC3(
                _classificationProfile, Classification3, Classification4)
                ? _ownerThirdLevelChoices
                : FinancialStatementWorkbookRules.GetC3Choices(
                    _classificationProfile, Classification3, Classification4);
            ReplaceChoices(Class5Choices, c3);
            ReplaceChoices(WorkbookClass3Choices, c3);
        }

        OnPropertyChanged(nameof(WorkbookClassification3));
        OnPropertyChanged(nameof(CanSpecifyClass5));
    }

    private void DeriveStatementNatureFromClass3()
    {
        if (string.IsNullOrWhiteSpace(_class3)) return;
        var pairs = _mappingCatalog
            .Where(x => x.Class3.Equals(_class3, StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.Class1, x.Class2))
            .Distinct()
            .ToArray();
        if (pairs.Length != 1)
        {
            SetProperty(ref _class1, string.Empty, nameof(Classification1));
            SetProperty(ref _class2, string.Empty, nameof(Classification2));
            NotifyStatementNatureDisplay();
            return;
        }
        SetProperty(ref _class1, pairs[0].Class1 ?? string.Empty, nameof(Classification1));
        SetProperty(ref _class2, pairs[0].Class2 ?? string.Empty, nameof(Classification2));
        NotifyStatementNatureDisplay();
    }

    private static int TryLegacyOwnerSlot(string class4)
    {
        var value = (class4 ?? string.Empty).Trim();
        foreach (var prefix in new[] { "Capital A/c", "Current A/c" })
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(value[prefix.Length..].Trim(), out var slot) && slot is >= 1 and <= 11) return slot;
        }
        return 0;
    }

    private static void ReplaceChoices(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (seen.Add(value)) target.Add(value);
    }

    public string ResolveOwnerThirdLevelFromLedger(string class3, string class4)
    {
        if (!FinancialStatementWorkbookRules.IsDynamicNceOwnerC3(_classificationProfile, class3, class4))
            return string.Empty;

        var choices = _ownerThirdLevelChoices
            .Where(IsNamedOwnerChoice)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (choices.Length == 0) return string.Empty;

        var candidate = FinancialStatementTrialBalanceOrder.OwnerNameCandidate(Ledger, ParentGroup, PrimaryGroup);
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var exact = choices.FirstOrDefault(x => x.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact)) return exact;
        }
        return choices.Length == 1 ? choices[0] : string.Empty;
    }

    private static bool IsNamedOwnerChoice(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return false;
        if (text.StartsWith("Capital A/c ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Current A/c ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Other Capital A/c", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Other Current A/c", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("specify nature", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public void SetMappingPresentation(string fsHead, string noteHead, string noteSubHead)
    {
        var option = _mappingCatalog.FirstOrDefault(x =>
            x.FsHead.Equals(fsHead ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            x.NoteHead.Equals(noteHead ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            x.NoteSubHead.Equals(noteSubHead ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (option is not null) SetMappingOption(option, _mappingOrigin);
    }

    public void ClearMappingPresentation()
    {
        _suppressHierarchyChange = true;
        try
        {
            ClearInternalMapping();
            SetProperty(ref _class1, string.Empty, nameof(Classification1));
            SetProperty(ref _class2, string.Empty, nameof(Classification2));
            SetProperty(ref _class3, string.Empty, nameof(Classification3));
            SetProperty(ref _class4, string.Empty, nameof(Classification4));
            SetProperty(ref _class5, string.Empty, nameof(Classification5));
            _mappingOrigin = "Unmapped";
            RefreshHierarchyChoices();
            OnPropertyChanged(nameof(WorkbookClassification3));
        }
        finally { _suppressHierarchyChange = false; }
        OnPropertyChanged(nameof(MappedStatus));
    }

    public FinancialStatementLedgerRow Clone()
    {
        var clone = new FinancialStatementLedgerRow
        {
            Ledger = Ledger, ParentGroup = ParentGroup, PrimaryGroup = PrimaryGroup, MasterId = MasterId,
            Opening = Opening, Debit = Debit, Credit = Credit, Closing = Closing, SourceOrder = SourceOrder, NonCurrentAmount = NonCurrentAmount,
            Mapping = Mapping, Note = Note, AmountSign = AmountSign
        };
        clone.ConfigureMappingCatalog(_fullMappingCatalog, _classificationProfile, _ownerThirdLevelChoices);
        clone.RestoreNatureOverride(NatureOverride);
        if (!string.IsNullOrWhiteSpace(Mapping))
        {
            var option = !string.IsNullOrWhiteSpace(_stableHierarchyId)
                ? _mappingCatalog.FirstOrDefault(x => x.StableHierarchyId.Equals(_stableHierarchyId, StringComparison.OrdinalIgnoreCase))
                : null;
            option ??= _mappingCatalog.FirstOrDefault(x =>
                x.MappingKey.Equals(Mapping, StringComparison.OrdinalIgnoreCase) &&
                x.Class1.Equals(Classification1, StringComparison.OrdinalIgnoreCase) &&
                x.Class2.Equals(Classification2, StringComparison.OrdinalIgnoreCase) &&
                x.Class3.Equals(Classification3, StringComparison.OrdinalIgnoreCase) &&
                x.Class4.Equals(Classification4, StringComparison.OrdinalIgnoreCase));
            option ??= _mappingCatalog.Where(x => x.MappingKey.Equals(Mapping, StringComparison.OrdinalIgnoreCase)).Take(2).Count() == 1
                ? _mappingCatalog.FirstOrDefault(x => x.MappingKey.Equals(Mapping, StringComparison.OrdinalIgnoreCase))
                : null;
            if (option is not null) clone.SetMappingOption(option, _mappingOrigin, Classification5);
            else clone.SetPartialHierarchy(Classification1, Classification2, Classification3, Classification4, Classification5, _mappingOrigin);
        }
        else clone.SetPartialHierarchy(Classification1, Classification2, Classification3, Classification4, Classification5, _mappingOrigin);
        if (HasScheduleContext)
        {
            clone.ConfigureScheduleContext(_scheduleContext);
            clone.RestoreScheduleMapping(ScheduleClass1, ScheduleClass2, ScheduleMappingOrigin);
        }
        return clone;
    }
}

public static class FinancialStatementTrialBalanceOrder
{
    public static string ResolvePrimaryGroup(string directParent, IReadOnlyDictionary<string, string>? groupParents)
    {
        var direct = (directParent ?? string.Empty).Trim();
        if (direct.Length == 0) return string.Empty;
        var current = direct;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (groupParents is not null && visited.Add(current) && groupParents.TryGetValue(current, out var parent))
        {
            parent = (parent ?? string.Empty).Trim();
            if (parent.Length == 0 || parent.Equals("Primary", StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        return CanonicalPrimaryGroup(current, direct);
    }

    public static IOrderedEnumerable<FinancialStatementLedgerRow> Order(IEnumerable<FinancialStatementLedgerRow> rows)
        => rows.OrderBy(x => GroupRank(x.Ledger, x.ParentGroup, x.PrimaryGroup))
            .ThenBy(x => ParentGroupRank(x.ParentGroup, x.PrimaryGroup))
            .ThenBy(x => SubGroupRank(x.ParentGroup, x.PrimaryGroup))
            .ThenBy(x => x.ParentGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SourceOrder)
            .ThenBy(x => x.Ledger, StringComparer.OrdinalIgnoreCase);

    public static bool IsProfitLossAccountLedger(string ledger)
    {
        var ledgerKey = Normalize(ledger);
        return ledgerKey is "PROFIT LOSS AC" or "PROFIT LOSS A C" or "PROFIT AND LOSS AC" or "PROFIT AND LOSS A C" or "PROFIT LOSS ACCOUNT" or "PROFIT AND LOSS ACCOUNT";
    }

    private static int GroupRank(string ledger, string parent, string primary)
    {
        var ledgerKey = Normalize(ledger);
        if (ledgerKey is "OPENINGSTOCK" or "OPENING STOCK") return 0;
        // The supplied TB example is the presentation authority, including the standard
        // Tally Profit & Loss A/c row between liabilities/suspense and the asset blocks.
        if (IsProfitLossAccountLedger(ledger)) return 50;
        return Normalize(primary) switch
        {
            "CAPITAL ACCOUNT" => 10,
            "LOANS LIABILITY" => 20,
            "CURRENT LIABILITIES" => 30,
            "SUSPENSE AC" => 40,
            "FIXED ASSETS" => 60,
            "INVESTMENTS" => 70,
            "CURRENT ASSETS" => 80,
            "MISC EXPENSES ASSET" => 85,
            "BRANCH DIVISIONS" => 86,
            "SALES ACCOUNTS" => 90,
            "PURCHASE ACCOUNTS" => 100,
            "DIRECT INCOMES" => 110,
            "DIRECT EXPENSES" => 120,
            "INDIRECT INCOMES" => 130,
            "INDIRECT EXPENSES" => 140,
            _ => FallbackRank(parent)
        };
    }

    private static int FallbackRank(string parent)
        => CanonicalPrimaryGroup(parent, parent) switch
        {
            "Capital Account" => 10,
            "Loans (Liability)" => 20,
            "Current Liabilities" => 30,
            "Suspense A/c" => 40,
            "Fixed Assets" => 60,
            "Investments" => 70,
            "Current Assets" => 80,
            "Misc. Expenses (ASSET)" => 85,
            "Branch / Divisions" => 86,
            "Sales Accounts" => 90,
            "Purchase Accounts" => 100,
            "Direct Incomes" => 110,
            "Direct Expenses" => 120,
            "Indirect Incomes" => 130,
            "Indirect Expenses" => 140,
            _ => 900
        };


    private static int ParentGroupRank(string parent, string primary)
    {
        var key = Normalize(parent);
        var primaryKey = Normalize(primary);
        if (primaryKey == "CAPITAL ACCOUNT")
        {
            if (key.Contains("CAPITAL ACCOUNT", StringComparison.Ordinal)) return 0;
            if (key.Contains("RESERVE", StringComparison.Ordinal) || key.Contains("SURPLUS", StringComparison.Ordinal)) return 1;
            if (key.Contains("DRAWING", StringComparison.Ordinal)) return 2;
        }
        if (primaryKey == "LOANS LIABILITY")
        {
            if (key.Contains("SECURED", StringComparison.Ordinal) && !key.Contains("UNSECURED", StringComparison.Ordinal)) return 0;
            if (key.Contains("UNSECURED", StringComparison.Ordinal)) return 1;
            if (key.Contains("BANK OD", StringComparison.Ordinal) || key.Contains("BANK OCC", StringComparison.Ordinal)) return 2;
            if (key.Contains("LOAN", StringComparison.Ordinal) || key.Contains("BORROW", StringComparison.Ordinal)) return 3;
        }
        if (primaryKey == "CURRENT LIABILITIES")
        {
            if (key.Contains("DUTIES", StringComparison.Ordinal) || key.Contains("TAX", StringComparison.Ordinal) || key.Contains("GST", StringComparison.Ordinal) || key.Contains("OUTPUT", StringComparison.Ordinal)) return 0;
            if (key.Contains("PROVISION", StringComparison.Ordinal)) return 1;
            if (key.Contains("SUNDRY CREDITOR", StringComparison.Ordinal) || key.Contains("TRADE PAYABLE", StringComparison.Ordinal) || key.Contains("CREDITOR", StringComparison.Ordinal)) return 2;
            if (key.Contains("ADVANCE", StringComparison.Ordinal) || key.Contains("CUSTOMER", StringComparison.Ordinal)) return 3;
        }
        if (primaryKey == "CURRENT ASSETS")
        {
            if (key == "CURRENT ASSETS") return 0;
            if (key.Contains("DEPOSIT", StringComparison.Ordinal)) return 1;
            if (key.Contains("LOANS", StringComparison.Ordinal) || key.Contains("ADVANCE", StringComparison.Ordinal)) return 2;
            if (key.Contains("TDS", StringComparison.Ordinal) || key.Contains("RECEIVABLE", StringComparison.Ordinal) || key.Contains("INPUT", StringComparison.Ordinal) || key.Contains("GST", StringComparison.Ordinal) || key.Contains("TAX", StringComparison.Ordinal)) return 3;
            if (key.Contains("SUNDRY DEBTOR", StringComparison.Ordinal) || key.Contains("TRADE RECEIV", StringComparison.Ordinal) || key.Contains("DEBTOR", StringComparison.Ordinal)) return 4;
            if (key.Contains("CASH", StringComparison.Ordinal)) return 5;
            if (key.Contains("BANK", StringComparison.Ordinal)) return 6;
            if (key.Contains("STOCK", StringComparison.Ordinal) || key.Contains("INVENTOR", StringComparison.Ordinal)) return 7;
        }
        if (primaryKey == "FIXED ASSETS")
        {
            if (key.Contains("LAND", StringComparison.Ordinal) || key.Contains("BUILDING", StringComparison.Ordinal)) return 0;
            if (key.Contains("PLANT", StringComparison.Ordinal) || key.Contains("MACHIN", StringComparison.Ordinal)) return 1;
            if (key.Contains("FURNITURE", StringComparison.Ordinal) || key.Contains("FIXTURE", StringComparison.Ordinal)) return 2;
            if (key.Contains("VEHICLE", StringComparison.Ordinal)) return 3;
            if (key.Contains("COMPUTER", StringComparison.Ordinal) || key.Contains("OFFICE EQUIP", StringComparison.Ordinal)) return 4;
        }
        return 50;
    }

    private static int SubGroupRank(string parent, string primary)
    {
        var key = Normalize(parent);
        if (Normalize(primary) == "CAPITAL ACCOUNT")
        {
            if (key.Contains("CAPITAL ACCOUNT", StringComparison.Ordinal)) return 0;
            if (key.Contains("RESERVE", StringComparison.Ordinal) || key.Contains("SURPLUS", StringComparison.Ordinal)) return 1;
            if (key.Contains("DRAWING", StringComparison.Ordinal)) return 2;
        }
        if (Normalize(primary) == "LOANS LIABILITY")
        {
            if (key.Contains("SECURED", StringComparison.Ordinal) && !key.Contains("UNSECURED", StringComparison.Ordinal)) return 0;
            if (key.Contains("UNSECURED", StringComparison.Ordinal)) return 1;
            if (key.Contains("BANK OD", StringComparison.Ordinal)) return 2;
        }
        if (Normalize(primary) == "CURRENT LIABILITIES")
        {
            if (key.Contains("DUTIES", StringComparison.Ordinal) || key.Contains("TAX", StringComparison.Ordinal) || key.Contains("GST", StringComparison.Ordinal)) return 0;
            if (key.Contains("PROVISION", StringComparison.Ordinal) || key.Contains("JOB WORKER", StringComparison.Ordinal)) return 1;
            if (key.Contains("SUNDRY CREDITOR", StringComparison.Ordinal) || key.Contains("TRADE PAYABLE", StringComparison.Ordinal)) return 2;
        }
        if (Normalize(primary) == "CURRENT ASSETS")
        {
            // Match the supplied TB: generic Current Assets first, then Loans/Advances
            // (including TDS/GST receivable subgroups), Debtors, Cash and Bank. OpeningStock
            // is handled as the explicit first-row exception in GroupRank.
            if (key == "CURRENT ASSETS") return 0;
            if (key.Contains("DEPOSIT", StringComparison.Ordinal)) return 1;
            if (key.Contains("LOANS", StringComparison.Ordinal) || key.Contains("ADVANCE", StringComparison.Ordinal) || key.Contains("TDS", StringComparison.Ordinal)) return 2;
            if (key.Contains("SUNDRY DEBTOR", StringComparison.Ordinal) || key.Contains("TRADE RECEIV", StringComparison.Ordinal)) return 3;
            if (key.Contains("CASH", StringComparison.Ordinal)) return 4;
            if (key.Contains("BANK", StringComparison.Ordinal)) return 5;
            if (key.Contains("STOCK", StringComparison.Ordinal) || key.Contains("INVENTOR", StringComparison.Ordinal)) return 6;
        }
        return 50;
    }

    public static string ResolveNatureOfGroup(string ledger, string parent, string primary)
    {
        var ledgerKey = Normalize(ledger);
        if (ledgerKey is "PROFIT LOSS AC" or "PROFIT LOSS A C" or "PROFIT AND LOSS AC" or "PROFIT AND LOSS A C" or "PROFIT LOSS ACCOUNT" or "PROFIT AND LOSS ACCOUNT") return "Liabilities";

        // Bank Accounts is normally an Asset group, but an explicitly named overdraft/cash-credit
        // ledger belongs on the Liability side without relying on the closing-balance sign.
        if (ledgerKey.Contains("OVERDRAFT", StringComparison.Ordinal) || ledgerKey.Contains("BANK OD", StringComparison.Ordinal) ||
            ledgerKey.Contains("CASH CREDIT", StringComparison.Ordinal)) return "Liabilities";

        // Narrow semantic overrides retained from the R15 R14 tax repair. Tally commonly keeps
        // both input and output taxes under Duties & Taxes; source-side filtering must therefore
        // recognise proved receivables before the parent group's normal Liability nature.
        if (ledgerKey.Contains("INPUT GST", StringComparison.Ordinal) || ledgerKey.Contains("INPUT CGST", StringComparison.Ordinal) ||
            ledgerKey.Contains("INPUT SGST", StringComparison.Ordinal) || ledgerKey.Contains("INPUT IGST", StringComparison.Ordinal) ||
            ledgerKey.Contains("NOT SHOWN IN 2B", StringComparison.Ordinal) || ledgerKey.Contains("GST RECEIVABLE", StringComparison.Ordinal) ||
            ledgerKey.Contains("TDS RECEIVABLE", StringComparison.Ordinal) || ledgerKey.Contains("TCS RECEIVABLE", StringComparison.Ordinal) ||
            ledgerKey.Contains("ADVANCE TAX", StringComparison.Ordinal)) return "Assets";
        if (ledgerKey.Contains("OUTPUT GST", StringComparison.Ordinal) || ledgerKey.Contains("OUTPUT CGST", StringComparison.Ordinal) ||
            ledgerKey.Contains("OUTPUT SGST", StringComparison.Ordinal) || ledgerKey.Contains("OUTPUT IGST", StringComparison.Ordinal) ||
            ledgerKey.Contains("GST PAYABLE", StringComparison.Ordinal) || ledgerKey.Contains("TDS PAYABLE", StringComparison.Ordinal) ||
            ledgerKey.Contains("TCS PAYABLE", StringComparison.Ordinal)) return "Liabilities";

        var group = CanonicalPrimaryGroup(string.IsNullOrWhiteSpace(primary) ? parent : primary, parent);
        return Normalize(group) switch
        {
            "CAPITAL ACCOUNT" => "Liabilities",
            "LOANS LIABILITY" => "Liabilities",
            "CURRENT LIABILITIES" => "Liabilities",
            "PRIMARY LIABILITIES" => "Liabilities",
            "SUSPENSE AC" => "Liabilities",
            "FIXED ASSETS" => "Assets",
            "INVESTMENTS" => "Assets",
            "CURRENT ASSETS" => "Assets",
            "PRIMARY ASSETS" => "Assets",
            "MISC EXPENSES ASSET" => "Assets",
            "SALES ACCOUNTS" => "Income",
            "DIRECT INCOMES" => "Income",
            "INDIRECT INCOMES" => "Income",
            "PRIMARY INCOME" => "Income",
            "PURCHASE ACCOUNTS" => "Expenses",
            "DIRECT EXPENSES" => "Expenses",
            "INDIRECT EXPENSES" => "Expenses",
            "PRIMARY EXPENSES" => "Expenses",
            _ => string.Empty
        };
    }

    public static string OwnerNameCandidate(string ledger, string parent, string primary)
    {
        var parentKey = Normalize(parent);
        if (parentKey.Contains("RESERVE", StringComparison.Ordinal) || parentKey.Contains("SURPLUS", StringComparison.Ordinal)) return string.Empty;
        var group = CanonicalPrimaryGroup(string.IsNullOrWhiteSpace(primary) ? parent : primary, parent);
        if (!Normalize(group).Equals("CAPITAL ACCOUNT", StringComparison.Ordinal)) return string.Empty;

        var text = (ledger ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        foreach (var suffix in new[] { " - Capital A/c", " Capital A/c", " - Capital Account", " Capital Account", " - Current A/c", " Current A/c", " - Current Account", " Current Account", " - A/c", " A/c" })
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^suffix.Length].Trim().Trim('-', ':');
                break;
            }

        var key = Normalize(text);
        if (key.Length == 0 || key is "DRAWINGS" or "DRAWING" or "CAPITAL" or "CAPITAL FUND" or "CAPITAL ACCOUNT" or
            "CURRENT ACCOUNT" or "CURRENT AC" or "OPENING CAPITAL" or "OPENING BALANCE" or "PROFIT LOSS AC" or "PROFIT LOSS A C" or
            "PROFIT AND LOSS AC" or "RESERVES SURPLUS") return string.Empty;
        return text;
    }

    private static string CanonicalPrimaryGroup(string candidate, string direct)
    {
        var key = Normalize(candidate);
        var directKey = Normalize(direct);
        if (key.Contains("CAPITAL ACCOUNT", StringComparison.Ordinal) || key.Contains("CAPITAL A C", StringComparison.Ordinal) ||
            key.Contains("PARTNER CAPITAL", StringComparison.Ordinal) || key.Contains("PROPRIETOR CAPITAL", StringComparison.Ordinal) ||
            key.Contains("OWNER CAPITAL", StringComparison.Ordinal) || key.Contains("RESERVE", StringComparison.Ordinal)) return "Capital Account";
        if (key.Contains("LOANS LIABILITY", StringComparison.Ordinal) || key.Contains("SECURED LOAN", StringComparison.Ordinal) || key.Contains("UNSECURED LOAN", StringComparison.Ordinal) || key.Contains("BANK OD", StringComparison.Ordinal)) return "Loans (Liability)";
        if (key.Contains("CURRENT LIABIL", StringComparison.Ordinal) || key.Contains("DUTIES", StringComparison.Ordinal) || key.Contains("SUNDRY CREDITOR", StringComparison.Ordinal) || key.Contains("TRADE PAYABLE", StringComparison.Ordinal) || key.Contains("PROVISION", StringComparison.Ordinal) || key.Contains("PAYABLE", StringComparison.Ordinal) || key.Contains("OUTPUT", StringComparison.Ordinal)) return "Current Liabilities";
        if (key.Contains("FIXED ASSET", StringComparison.Ordinal)) return "Fixed Assets";
        if (key.Contains("INVESTMENT", StringComparison.Ordinal)) return "Investments";
        if (key.Contains("CURRENT ASSET", StringComparison.Ordinal) || key.Contains("BANK ACCOUNT", StringComparison.Ordinal) || key.Contains("CASH IN HAND", StringComparison.Ordinal) || key.Contains("STOCK IN HAND", StringComparison.Ordinal) || key.Contains("SUNDRY DEBTOR", StringComparison.Ordinal) || key.Contains("TRADE RECEIV", StringComparison.Ordinal) || key.Contains("LOANS ADVANCE", StringComparison.Ordinal) || key.Contains("DEPOSIT", StringComparison.Ordinal) || key.Contains("TDS", StringComparison.Ordinal) || key.Contains("RECEIVABLE", StringComparison.Ordinal) || key.Contains("INPUT", StringComparison.Ordinal)) return "Current Assets";
        if (key.Contains("MISC EXPENSE", StringComparison.Ordinal)) return "Misc. Expenses (ASSET)";
        if (key.Contains("SUSPENSE", StringComparison.Ordinal)) return "Suspense A/c";
        if (key.Contains("SALES", StringComparison.Ordinal)) return "Sales Accounts";
        if (key.Contains("PURCHASE", StringComparison.Ordinal)) return "Purchase Accounts";
        if (key.Contains("DIRECT INCOME", StringComparison.Ordinal)) return "Direct Incomes";
        if (key.Contains("DIRECT EXPENSE", StringComparison.Ordinal)) return "Direct Expenses";
        if (key.Contains("INDIRECT INCOME", StringComparison.Ordinal)) return "Indirect Incomes";
        if (key.Contains("INDIRECT EXPENSE", StringComparison.Ordinal)) return "Indirect Expenses";
        if (key.Contains("BRANCH", StringComparison.Ordinal) || key.Contains("DIVISION", StringComparison.Ordinal)) return "Branch / Divisions";

        // When live PY authority exposes only the direct parent, use the same known Tally
        // parent aliases so FY and PY still follow one deterministic preparation order.
        if (directKey != key) return CanonicalPrimaryGroup(direct, direct);
        return candidate.Trim();
    }

    private static string Normalize(string value)
    {
        var chars = (value ?? string.Empty).ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}

public sealed class FinancialStatementStockRow : ViewModelBase
{
    private string _mapping = string.Empty;
    private string _mappingOrigin = "Unmapped";
    private bool _restoringMapping;

    public static IReadOnlyList<string> WorkbookMappingChoices => FinancialStatementWorkbookRules.StockChoices;
    public string Item { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string HsnSac { get; set; } = string.Empty;
    public decimal GstRate { get; set; }
    public string ValuationMethod { get; set; } = string.Empty;
    // Financial Statements keeps unknown stock balances visible. When authoritative
    // item-wise/ledger-wise stock balances are available, zero rows participate in the
    // shared Hide 0 Balance filter; null never means zero.
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }

    // R52 FIXED9: authoritative period totals from NCE XMLStatement. These are hidden accounting
    // metadata only; the user's item rows and Inventory Classification mappings remain untouched.
    public bool HasPeriodSourceAuthority { get; set; }
    public decimal PeriodSourceOpeningTotal { get; set; }
    public decimal PeriodSourceClosingTotal { get; set; }

    public string Mapping
    {
        get => _mapping;
        set
        {
            var normalized = WorkbookMappingChoices.FirstOrDefault(x => x.Equals((value ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            if (!SetProperty(ref _mapping, normalized)) return;
            if (!_restoringMapping) _mappingOrigin = normalized.Length == 0 ? "Unmapped" : "User Mapped";
            OnPropertyChanged(nameof(MappingOrigin));
            OnPropertyChanged(nameof(MappingStatus));
            OnPropertyChanged(nameof(HasResolvedMapping));
        }
    }
    public string MappingOrigin => _mappingOrigin;
    public string MappingStatus => HasResolvedMapping ? _mappingOrigin : "Unmapped";
    public bool HasResolvedMapping => !string.IsNullOrWhiteSpace(_mapping);
    public IReadOnlyList<string> MappingChoices => WorkbookMappingChoices;

    public void RestoreMapping(string? mapping, string? origin)
    {
        _restoringMapping = true;
        try
        {
            Mapping = mapping ?? string.Empty;
            _mappingOrigin = HasResolvedMapping ? (string.IsNullOrWhiteSpace(origin) ? "Mapped" : origin.Trim()) : "Unmapped";
        }
        finally { _restoringMapping = false; }
        OnPropertyChanged(nameof(MappingOrigin));
        OnPropertyChanged(nameof(MappingStatus));
        OnPropertyChanged(nameof(HasResolvedMapping));
    }

    public bool TryApplyMappingFrom(FinancialStatementStockRow source, string origin)
    {
        if (!source.HasResolvedMapping) return false;
        RestoreMapping(source.Mapping, origin);
        return HasResolvedMapping;
    }
}

public sealed class NceFinancialStatementFeedSnapshot
{
    public NceFinancialStatementFeed Feed { get; set; } = new();
    public DateTime SavedUtc { get; set; }
}

public sealed class FinancialStatementDataSnapshot
{
    public List<FinancialStatementDataLedger> FyTrialBalance { get; set; } = [];
    public List<FinancialStatementDataLedger> PyTrialBalance { get; set; } = [];
    public List<FinancialStatementDataLedger> FyDebtors { get; set; } = [];
    public List<FinancialStatementDataLedger> PyDebtors { get; set; } = [];
    public List<FinancialStatementDataLedger> FyCreditors { get; set; } = [];
    public List<FinancialStatementDataLedger> PyCreditors { get; set; } = [];
    public List<FinancialStatementDataStock> FyStock { get; set; } = [];
    public List<FinancialStatementDataStock> PyStock { get; set; } = [];
    public DateTime SavedUtc { get; set; }
}

public sealed class FinancialStatementDataLedger
{
    public string Ledger { get; set; } = string.Empty;
    public string ParentGroup { get; set; } = string.Empty;
    public string PrimaryGroup { get; set; } = string.Empty;
    public string MasterId { get; set; } = string.Empty;
    public decimal Opening { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Closing { get; set; }
    public int SourceOrder { get; set; }
    public string ScheduleClass1 { get; set; } = string.Empty;
    public string ScheduleClass2 { get; set; } = string.Empty;
    public string ScheduleMappingOrigin { get; set; } = string.Empty;
    public bool HasScheduleSourceAuthority { get; set; }
    public decimal ScheduleSourceAuthorityTotal { get; set; }

    public static FinancialStatementDataLedger From(FinancialStatementLedgerRow row) => new()
    {
        Ledger = row.Ledger, ParentGroup = row.ParentGroup, PrimaryGroup = row.PrimaryGroup, MasterId = row.MasterId,
        Opening = row.Opening, Debit = row.Debit, Credit = row.Credit, Closing = row.Closing, SourceOrder = row.SourceOrder,
        ScheduleClass1 = row.ScheduleClass1, ScheduleClass2 = row.ScheduleClass2, ScheduleMappingOrigin = row.ScheduleMappingOrigin,
        HasScheduleSourceAuthority = row.HasScheduleSourceAuthority,
        ScheduleSourceAuthorityTotal = row.ScheduleSourceAuthorityTotal
    };

    public FinancialStatementLedgerRow ToRow(string scheduleContext = "")
    {
        var row = new FinancialStatementLedgerRow
        {
            Ledger = Ledger, ParentGroup = ParentGroup, PrimaryGroup = PrimaryGroup, MasterId = MasterId,
            Opening = Opening, Debit = Debit, Credit = Credit, Closing = Closing, SourceOrder = SourceOrder,
            HasScheduleSourceAuthority = HasScheduleSourceAuthority,
            ScheduleSourceAuthorityTotal = ScheduleSourceAuthorityTotal
        };
        if (!string.IsNullOrWhiteSpace(scheduleContext))
        {
            row.ConfigureScheduleContext(scheduleContext);
            row.RestoreScheduleMapping(ScheduleClass1, ScheduleClass2, ScheduleMappingOrigin);
        }
        return row;
    }
}

public sealed class FinancialStatementDataStock
{
    public string Item { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string HsnSac { get; set; } = string.Empty;
    public decimal GstRate { get; set; }
    public string ValuationMethod { get; set; } = string.Empty;
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }
    public bool HasPeriodSourceAuthority { get; set; }
    public decimal PeriodSourceOpeningTotal { get; set; }
    public decimal PeriodSourceClosingTotal { get; set; }
    public string Mapping { get; set; } = string.Empty;
    public string MappingOrigin { get; set; } = string.Empty;

    public static FinancialStatementDataStock From(FinancialStatementStockRow row) => new()
    {
        Item = row.Item, Group = row.Group, Category = row.Category, Unit = row.Unit, HsnSac = row.HsnSac, GstRate = row.GstRate,
        ValuationMethod = row.ValuationMethod, OpeningBalance = row.OpeningBalance, ClosingBalance = row.ClosingBalance,
        HasPeriodSourceAuthority = row.HasPeriodSourceAuthority,
        PeriodSourceOpeningTotal = row.PeriodSourceOpeningTotal,
        PeriodSourceClosingTotal = row.PeriodSourceClosingTotal,
        Mapping = row.Mapping, MappingOrigin = row.MappingOrigin
    };

    public FinancialStatementStockRow ToRow()
    {
        var row = new FinancialStatementStockRow
        {
            Item = Item, Group = Group, Category = Category, Unit = Unit, HsnSac = HsnSac, GstRate = GstRate,
            ValuationMethod = ValuationMethod, OpeningBalance = OpeningBalance, ClosingBalance = ClosingBalance,
            HasPeriodSourceAuthority = HasPeriodSourceAuthority,
            PeriodSourceOpeningTotal = PeriodSourceOpeningTotal,
            PeriodSourceClosingTotal = PeriodSourceClosingTotal
        };
        row.RestoreMapping(Mapping, MappingOrigin);
        return row;
    }
}

public sealed class FinancialStatementBasicInfo
{
    public string EntityName { get; set; } = string.Empty;
    public string CurrentYearCompanyName { get; set; } = string.Empty;
    public string PreviousYearCompanyName { get; set; } = string.Empty;
    public string MailingName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Pan { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string LlpIn { get; set; } = string.Empty;
    public string EntityType { get; set; } = "Company - AS - Schedule III Division I";
    public string NceType { get; set; } = string.Empty;
    public string CurrentYearEnd { get; set; } = string.Empty;
    public string PreviousYearEnd { get; set; } = string.Empty;
    public string DebtorCreditorDetail { get; set; } = "Party-wise";
    public string StockDetail { get; set; } = "Item-wise";
    public string Rounding { get; set; } = "Default";
    public string PpeBlock { get; set; } = "Gross Block";
    public string PpeBlockSelectionOrigin { get; set; } = "Auto";
    public string SignatoryName { get; set; } = string.Empty;
    public string SignatoryDesignation { get; set; } = string.Empty;
    public string Din { get; set; } = string.Empty;
    public string SigningDate { get; set; } = string.Empty;
    public string SigningPlace { get; set; } = string.Empty;
    public string AuditFirmName { get; set; } = string.Empty;
    public string FirmRegistrationNumber { get; set; } = string.Empty;
    public string Udin { get; set; } = string.Empty;
    public string CaName { get; set; } = string.Empty;
    public string CaDesignation { get; set; } = "Chartered Accountant";
    public string CaMembershipNumber { get; set; } = string.Empty;
    public string AuditPlace { get; set; } = string.Empty;
    public string CaSigningDate { get; set; } = string.Empty;
    public string PresentationUnit { get; set; } = "₹";
    public string StandaloneConsolidated { get; set; } = "Standalone";
    public string PreviousYearTallyCompany { get; set; } = string.Empty;
    public string PreviousYearTallyCompanyKey { get; set; } = string.Empty;
    public string PreviousYearDataMode { get; set; } = "Actual Previous Year Data";
    public List<EntitySignatoryRow> Signatories { get; set; } = [];
    public List<OwnerPartnerRow> OwnerPartners { get; set; } = [];
    public List<ShareCapitalDetailRow> ShareCapitalDetails { get; set; } = [];
}

public sealed class EntitySignatoryRow
{
    public string Name { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
    public string Din { get; set; } = string.Empty;
    public string Place { get; set; } = string.Empty;
    public string SigningDate { get; set; } = string.Empty;
    public EntitySignatoryRow Clone() => new() { Name = Name, Designation = Designation, Din = Din, Place = Place, SigningDate = SigningDate };
}

public sealed class OwnerPartnerRow
{
    public string Name { get; set; } = string.Empty;
    public decimal ShareOfProfitLoss { get; set; }
    public string Designation { get; set; } = string.Empty;
    public OwnerPartnerRow Clone() => new() { Name = Name, ShareOfProfitLoss = ShareOfProfitLoss, Designation = Designation };
}

public sealed class ShareCapitalDetailRow
{
    public string TypeOfShares { get; set; } = string.Empty;
    public decimal FaceValue { get; set; }
    public ShareCapitalDetailRow Clone() => new() { TypeOfShares = TypeOfShares, FaceValue = FaceValue };
}

public sealed class FinancialStatementNotePreviewRow
{
    public string Note { get; set; } = string.Empty;
    public string NoteTitle { get; set; } = string.Empty;
    public string StatutoryLineItem { get; set; } = string.Empty;
    public string MappingKey { get; set; } = string.Empty;
    public decimal CurrentAmount { get; set; }
    public decimal PreviousAmount { get; set; }
    public int MappedLedgerCount { get; set; }
    public bool IsTotal { get; set; }
}

public sealed record FinancialStatementMappingPreference(string Kind, string Identity, string Name, string MappingKey, string Note, string AmountSign, string FsHead = "", string NoteHead = "", string NoteSubHead = "", string Class1 = "", string Class2 = "", string Class3 = "", string Class4 = "", string Class5 = "", string MappingOrigin = "", decimal? NonCurrentAmount = null, string CanonicalId = "", decimal? PyNonCurrentAmount = null, string StableHierarchyId = "", string Period = "", string ScheduleClass1 = "", string ScheduleClass2 = "", string NatureOverride = "");

public sealed class FirmProfileRow
{
    public string FirmId { get; set; } = Guid.NewGuid().ToString("N");
    public string FirmName { get; set; } = string.Empty;
    public string FirmRegistrationNumber { get; set; } = string.Empty;
    public string Place { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public sealed class CaProfileRow
{
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");
    public string FirmProfileId { get; set; } = string.Empty;
    public string CaName { get; set; } = string.Empty;
    public string Designation { get; set; } = "Chartered Accountant";
    public string MembershipNumber { get; set; } = string.Empty;
    public string FirmName { get; set; } = string.Empty;
    public string FirmRegistrationNumber { get; set; } = string.Empty;
    public string Place { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
