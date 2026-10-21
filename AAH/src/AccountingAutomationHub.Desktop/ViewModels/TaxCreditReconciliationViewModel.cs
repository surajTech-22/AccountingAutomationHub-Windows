using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Tax;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Sales;
using AccountingAutomationHub.Infrastructure.Tax;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class TaxCreditReconciliationViewModel : PageViewModel
{
    private readonly ActiveClientContext _clientContext;
    private readonly SqliteTaxCreditRepository _repository;
    private readonly Form26AsPdfImportService _importService;
    private readonly TaxCreditReconciliationService _reconciliationService;
    private readonly SqliteMasterRepository _masterRepository;
    private readonly BackgroundAutomationService? _automationService;
    private readonly List<TaxCreditReconciliationRow> _allRows = [];
    private readonly List<TaxCreditPartyUiRow> _allParties = [];
    private readonly List<MasterRecord> _currentLedgerMasters = [];
    private ClientWorkspaceStateCache? _workspaceCache;
    private bool _ledgerMastersPrepared;

    public void AttachWorkspaceCache(ClientWorkspaceStateCache cache) => _workspaceCache = cache;

    private string _statusText = "26AS setup UI ready. Source intake and preliminary suggestions are separate from final automatch and reconciliation.";
    private bool _isBusy;
    private string _statementPan = string.Empty;
    private string _statementAssessee = string.Empty;
    private string _statementAssessmentYear = string.Empty;
    private string _statementUpdatedTill = string.Empty;
    private string _sourcePath = string.Empty;
    private string _sourceType = "Not imported";
    private string _sourceCompleteness = "WAITING";
    private string _sourceComparisonStatus = "Awaiting PDF/TXT/ZIP corroboration";
    private string _clientMatchStatus = "WAITING";
    private string _partySetupStatus = "WAITING";
    private string _tdsLedgerSetupStatus = "NOT SET";
    private string _accountingRulesStatus = "PENDING";
    private string _reconciliationStatus = "NOT STARTED";
    private string _downloadsIntakeStatus = "Downloads intake UI ready · no file will move unless Client PAN + FY can be proved.";
    private string _winmanFilePath = string.Empty;
    private string _winmanStatus = "Foundation only · Winman reader not active";
    private string _passwordStatus = "Manual TRACES password fallback is available. Winman password reading will be linked later.";
    private string _pendingTracesPassword = string.Empty;
    private bool _watchDownloads = true;
    private bool _moveVerifiedDownloads = true;
    private bool _hideCompletedParties = true;
    private string _partySearchText = string.Empty;
    private string _booksPartySearchText = string.Empty;
    private TaxCreditPartyFilterOption? _selectedPartyFilter;
    private TaxCreditPartyUiRow? _selectedPartyMapping;
    private TaxCreditBooksPartySuggestionUiRow? _selectedBooksPartySuggestion;
    private string _partyMappingStatus = "Open Client Matching to review 26AS deductor/client links and Books-side suggestions.";
    private string _partySelectionStatus = "No client/deductor link selection prepared.";
    private string _partyDetectedMatchShape = "Passive match shape: select rows on both sides";
    private string _entryMatchStatus = "No entry link draft prepared.";
    private string _entrySelectionStatus = "No entry link selection prepared.";
    private string _entryDetectedMatchShape = "Passive match shape: select rows on both sides";
    private int _selectedPartySideCount;
    private int _selectedBooksPartySideCount;
    private int _selectedEntrySideCount;
    private int _selectedBooksEntrySideCount;
    private TaxCreditPartTabUi? _selectedPartTab;
    private string _activePanel = string.Empty;
    private bool _useExistingTdsLedger = true;
    private bool _createNewTdsLedger;
    private MasterLedgerUiOption? _selectedExistingTdsLedger;
    private string _newTdsLedgerName = string.Empty;
    private string _newTdsLedgerParent = "Loans & Advances (Asset)";
    private string _tdsLedgerMasterId = string.Empty;
    private string _tdsLedgerParent = string.Empty;
    private string _tdsLedgerName = string.Empty;
    private int _statementRows;
    private int _matched;
    private int _probable;
    private int _missingInBooks;
    private int _review;
    private decimal _statementGross;
    private decimal _statementTds;
    private decimal _statementTcs;
    private decimal _statementDeposited;
    private decimal _matchedGross;

    public TaxCreditReconciliationViewModel(
        ActiveClientContext clientContext,
        SqliteTaxCreditRepository repository,
        SqliteSalesRecordRepository salesRepository,
        SqliteJournalRepository journalRepository,
        SqliteMasterRepository masterRepository,
        BackgroundAutomationService? automationService = null)
        : base("26AS Reconciliation", "26AS source, party mapping, TDS Receivable setup and staged reconciliation workspace")
    {
        _clientContext = clientContext;
        _repository = repository;
        _masterRepository = masterRepository;
        _automationService = automationService;
        _importService = new Form26AsPdfImportService(repository);
        _reconciliationService = new TaxCreditReconciliationService(repository, salesRepository, journalRepository);
        _clientContext.PropertyChanged += OnClientContextPropertyChanged;

        Import26AsCommand = new AsyncRelayCommand(Import26AsAsync, () => CurrentClientId > 0 && !IsBusy);
        ImportStructuredSourceCommand = new RelayCommand(ChooseStructuredSource, () => CurrentClientId > 0 && !IsBusy);
        RefreshCommand = new RelayCommand(Refresh, () => CurrentClientId > 0 && !IsBusy);
        ScanDownloadsCommand = new RelayCommand(ShowDownloadsUiStatus, () => CurrentClientId > 0 && !IsBusy);
        LinkWinmanCommand = new RelayCommand(ChooseWinmanFile, () => CurrentClientId > 0 && !IsBusy);
        DetectWinmanPasswordCommand = new RelayCommand(ShowWinmanDetectionUiStatus, () => CurrentClientId > 0 && !IsBusy);
        SavePasswordCommand = new RelayCommand(SavePasswordUiState, () => CurrentClientId > 0 && !IsBusy);
        ForgetPasswordCommand = new RelayCommand(ForgetPasswordUiState, () => CurrentClientId > 0 && !IsBusy);
        AcceptPartySuggestionCommand = new RelayCommand(AcceptPartySuggestionUiState, () => SelectedPartyMapping is not null);
        LinkSelectedPartyCommand = new RelayCommand(LinkSelectedPartyUiState, () => SelectedPartyMapping is not null && SelectedBooksPartySuggestion is not null);
        PreparePartyMatchCommand = new RelayCommand(PreparePartyMatchUiState, () => PartyMappings.Count > 0 && BooksPartySuggestions.Count > 0);
        ClearPartyLinkCommand = new RelayCommand(ClearPartyLinkUiState, () => SelectedPartyMapping is not null);
        HoldPartyCommand = new RelayCommand(HoldPartyUiState, () => SelectedPartyMapping is not null);
        PrepareEntryMatchCommand = new RelayCommand(PrepareEntryMatchUiState, () => Rows.Count > 0);
        ClearEntryMatchCommand = new RelayCommand(() => EntryMatchStatus = "No entry link draft prepared.");
        LinkExistingTdsLedgerCommand = new RelayCommand(LinkExistingTdsLedgerUiState, () => SelectedExistingTdsLedger is not null);
        CreateTdsLedgerDraftCommand = new RelayCommand(CreateTdsLedgerDraftUiState, () => CurrentClientId > 0);
        ClearPartyFilterCommand = new RelayCommand(ClearPartyFilter);
        OpenSourcePanelCommand = new RelayCommand(() => SetActivePanel("Source"));
        OpenClientMatchPanelCommand = new RelayCommand(() => SetActivePanel("ClientMatch"));
        OpenTdsLedgerPanelCommand = new RelayCommand(() => SetActivePanel("TdsLedger"));
        OpenEntryMatchPanelCommand = new RelayCommand(() => SetActivePanel("EntryMatch"), () => SelectedPartTab is not null);
        CloseWorkspacePanelCommand = new RelayCommand(() => SetActivePanel(string.Empty));
        RebuildFinancialYearDefaults();
    }

    public ObservableCollection<TaxCreditReconciliationRow> Rows { get; } = [];
    public ObservableCollection<TaxCreditReconciliationRow> GroupStatementRows { get; } = [];
    public ObservableCollection<TaxCreditReconciliationRow> GroupBooksRows { get; } = [];
    public ObservableCollection<TaxCreditPartyFilterOption> PartyFilterOptions { get; } = [];
    public ObservableCollection<TaxCreditPartyUiRow> PartyMappings { get; } = [];
    public ObservableCollection<TaxCreditBooksPartySuggestionUiRow> BooksPartySuggestions { get; } = [];
    public ObservableCollection<TaxCreditReconciliationRow> EntryBooksSuggestions { get; } = [];
    public ObservableCollection<TaxCreditSourceValidationUiRow> SourceValidationRows { get; } = [];
    public ObservableCollection<MasterLedgerUiOption> ExistingTdsLedgerOptions { get; } = [];
    public ObservableCollection<TaxCreditPartTabUi> AvailablePartTabs { get; } = [];

    public AsyncRelayCommand Import26AsCommand { get; }
    public RelayCommand ImportStructuredSourceCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ScanDownloadsCommand { get; }
    public RelayCommand LinkWinmanCommand { get; }
    public RelayCommand DetectWinmanPasswordCommand { get; }
    public RelayCommand SavePasswordCommand { get; }
    public RelayCommand ForgetPasswordCommand { get; }
    public RelayCommand AcceptPartySuggestionCommand { get; }
    public RelayCommand LinkSelectedPartyCommand { get; }
    public RelayCommand PreparePartyMatchCommand { get; }
    public RelayCommand ClearPartyLinkCommand { get; }
    public RelayCommand HoldPartyCommand { get; }
    public RelayCommand PrepareEntryMatchCommand { get; }
    public RelayCommand ClearEntryMatchCommand { get; }
    public RelayCommand LinkExistingTdsLedgerCommand { get; }
    public RelayCommand CreateTdsLedgerDraftCommand { get; }
    public RelayCommand ClearPartyFilterCommand { get; }
    public RelayCommand OpenSourcePanelCommand { get; }
    public RelayCommand OpenClientMatchPanelCommand { get; }
    public RelayCommand OpenTdsLedgerPanelCommand { get; }
    public RelayCommand OpenEntryMatchPanelCommand { get; }
    public RelayCommand CloseWorkspacePanelCommand { get; }

    public long CurrentClientId => _clientContext.CurrentClient?.Id ?? 0;
    public string CurrentFinancialYear => _clientContext.CurrentClient?.FinancialYear ?? string.Empty;
    public string CurrentClientName => _clientContext.CurrentClient?.ClientName ?? string.Empty;
    public string ClientPan => _clientContext.CurrentClient?.Pan ?? string.Empty;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanImport));
            Import26AsCommand.RaiseCanExecuteChanged();
            ImportStructuredSourceCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
            ScanDownloadsCommand.RaiseCanExecuteChanged();
            LinkWinmanCommand.RaiseCanExecuteChanged();
            DetectWinmanPasswordCommand.RaiseCanExecuteChanged();
            SavePasswordCommand.RaiseCanExecuteChanged();
            ForgetPasswordCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanImport => CurrentClientId > 0 && !IsBusy;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string StatementPan { get => _statementPan; private set => SetProperty(ref _statementPan, value); }
    public string StatementAssessee { get => _statementAssessee; private set => SetProperty(ref _statementAssessee, value); }
    public string StatementAssessmentYear { get => _statementAssessmentYear; private set => SetProperty(ref _statementAssessmentYear, value); }
    public string StatementUpdatedTill { get => _statementUpdatedTill; private set => SetProperty(ref _statementUpdatedTill, value); }
    public string SourcePath { get => _sourcePath; private set => SetProperty(ref _sourcePath, value); }
    public string SourceType { get => _sourceType; private set => SetProperty(ref _sourceType, value); }
    public string SourceCompleteness { get => _sourceCompleteness; private set => SetProperty(ref _sourceCompleteness, value); }
    public string SourceComparisonStatus { get => _sourceComparisonStatus; private set => SetProperty(ref _sourceComparisonStatus, value); }
    public string ClientMatchStatus { get => _clientMatchStatus; private set => SetProperty(ref _clientMatchStatus, value); }
    public string PartySetupStatus { get => _partySetupStatus; private set => SetProperty(ref _partySetupStatus, value); }
    public string TdsLedgerSetupStatus { get => _tdsLedgerSetupStatus; private set => SetProperty(ref _tdsLedgerSetupStatus, value); }
    public string AccountingRulesStatus { get => _accountingRulesStatus; private set => SetProperty(ref _accountingRulesStatus, value); }
    public string ReconciliationStatus { get => _reconciliationStatus; private set => SetProperty(ref _reconciliationStatus, value); }
    public string DownloadsIntakeStatus { get => _downloadsIntakeStatus; private set => SetProperty(ref _downloadsIntakeStatus, value); }
    public string WinmanFilePath { get => _winmanFilePath; private set => SetProperty(ref _winmanFilePath, value); }
    public string WinmanStatus { get => _winmanStatus; private set => SetProperty(ref _winmanStatus, value); }
    public string PasswordStatus { get => _passwordStatus; private set => SetProperty(ref _passwordStatus, value); }
    public string PendingTracesPassword { get => _pendingTracesPassword; set => SetProperty(ref _pendingTracesPassword, value ?? string.Empty); }
    public bool WatchDownloads { get => _watchDownloads; set => SetProperty(ref _watchDownloads, value); }
    public bool MoveVerifiedDownloads { get => _moveVerifiedDownloads; set => SetProperty(ref _moveVerifiedDownloads, value); }

    public bool HideCompletedParties
    {
        get => _hideCompletedParties;
        set
        {
            if (!SetProperty(ref _hideCompletedParties, value)) return;
            RebuildPartyFilterAndRows();
        }
    }

    public string PartySearchText
    {
        get => _partySearchText;
        set
        {
            if (!SetProperty(ref _partySearchText, value ?? string.Empty)) return;
            RebuildPartyFilterAndRows();
        }
    }


    public string BooksPartySearchText
    {
        get => _booksPartySearchText;
        set
        {
            if (!SetProperty(ref _booksPartySearchText, value ?? string.Empty)) return;
            RebuildBooksPartySuggestions();
        }
    }

    public TaxCreditPartyFilterOption? SelectedPartyFilter
    {
        get => _selectedPartyFilter;
        set
        {
            if (!SetProperty(ref _selectedPartyFilter, value)) return;
            RebuildVisibleRows();
        }
    }

    public TaxCreditPartyUiRow? SelectedPartyMapping
    {
        get => _selectedPartyMapping;
        set
        {
            if (!SetProperty(ref _selectedPartyMapping, value)) return;
            RebuildBooksPartySuggestions();
            AcceptPartySuggestionCommand.RaiseCanExecuteChanged();
            LinkSelectedPartyCommand.RaiseCanExecuteChanged();
            ClearPartyLinkCommand.RaiseCanExecuteChanged();
            HoldPartyCommand.RaiseCanExecuteChanged();
        }
    }

    public TaxCreditBooksPartySuggestionUiRow? SelectedBooksPartySuggestion
    {
        get => _selectedBooksPartySuggestion;
        set
        {
            if (!SetProperty(ref _selectedBooksPartySuggestion, value)) return;
            LinkSelectedPartyCommand.RaiseCanExecuteChanged();
        }
    }

    public string PartyMappingStatus { get => _partyMappingStatus; private set => SetProperty(ref _partyMappingStatus, value); }
    public string PartySelectionStatus { get => _partySelectionStatus; private set => SetProperty(ref _partySelectionStatus, value); }
    public string PartyDetectedMatchShape { get => _partyDetectedMatchShape; private set => SetProperty(ref _partyDetectedMatchShape, value); }
    public string EntryMatchStatus { get => _entryMatchStatus; private set => SetProperty(ref _entryMatchStatus, value); }
    public string EntrySelectionStatus { get => _entrySelectionStatus; private set => SetProperty(ref _entrySelectionStatus, value); }
    public string EntryDetectedMatchShape { get => _entryDetectedMatchShape; private set => SetProperty(ref _entryDetectedMatchShape, value); }

    public TaxCreditPartTabUi? SelectedPartTab
    {
        get => _selectedPartTab;
        set
        {
            if (!SetProperty(ref _selectedPartTab, value)) return;
            RebuildPartyFilterAndRows();
            OpenEntryMatchPanelCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsSourcePanelOpen => _activePanel.Equals("Source", StringComparison.Ordinal);
    public bool IsClientMatchPanelOpen => _activePanel.Equals("ClientMatch", StringComparison.Ordinal);
    public bool IsTdsLedgerPanelOpen => _activePanel.Equals("TdsLedger", StringComparison.Ordinal);
    public bool IsEntryMatchPanelOpen => _activePanel.Equals("EntryMatch", StringComparison.Ordinal);

    private void SetActivePanel(string panel)
    {
        _activePanel = panel ?? string.Empty;
        OnPropertyChanged(nameof(IsSourcePanelOpen));
        OnPropertyChanged(nameof(IsClientMatchPanelOpen));
        OnPropertyChanged(nameof(IsTdsLedgerPanelOpen));
        OnPropertyChanged(nameof(IsEntryMatchPanelOpen));
    }

    public bool UseExistingTdsLedger
    {
        get => _useExistingTdsLedger;
        set
        {
            if (!SetProperty(ref _useExistingTdsLedger, value)) return;
            if (value && _createNewTdsLedger)
            {
                _createNewTdsLedger = false;
                OnPropertyChanged(nameof(CreateNewTdsLedger));
            }
        }
    }

    public bool CreateNewTdsLedger
    {
        get => _createNewTdsLedger;
        set
        {
            if (!SetProperty(ref _createNewTdsLedger, value)) return;
            if (value && _useExistingTdsLedger)
            {
                _useExistingTdsLedger = false;
                OnPropertyChanged(nameof(UseExistingTdsLedger));
            }
        }
    }

    public MasterLedgerUiOption? SelectedExistingTdsLedger
    {
        get => _selectedExistingTdsLedger;
        set
        {
            if (!SetProperty(ref _selectedExistingTdsLedger, value)) return;
            LinkExistingTdsLedgerCommand.RaiseCanExecuteChanged();
        }
    }

    public string NewTdsLedgerName { get => _newTdsLedgerName; set => SetProperty(ref _newTdsLedgerName, value ?? string.Empty); }
    public string NewTdsLedgerParent { get => _newTdsLedgerParent; set => SetProperty(ref _newTdsLedgerParent, value ?? string.Empty); }
    public string TdsLedgerMasterId { get => _tdsLedgerMasterId; private set => SetProperty(ref _tdsLedgerMasterId, value); }
    public string TdsLedgerParent { get => _tdsLedgerParent; private set => SetProperty(ref _tdsLedgerParent, value); }
    public string TdsLedgerName { get => _tdsLedgerName; private set => SetProperty(ref _tdsLedgerName, value); }

    public int StatementRows { get => _statementRows; private set => SetProperty(ref _statementRows, value); }
    public int Matched { get => _matched; private set => SetProperty(ref _matched, value); }
    public int Probable { get => _probable; private set => SetProperty(ref _probable, value); }
    public int MissingInBooks { get => _missingInBooks; private set => SetProperty(ref _missingInBooks, value); }
    public int Review { get => _review; private set => SetProperty(ref _review, value); }
    public decimal StatementGross { get => _statementGross; private set => SetProperty(ref _statementGross, value); }
    public decimal StatementTds { get => _statementTds; private set => SetProperty(ref _statementTds, value); }
    public decimal StatementTcs { get => _statementTcs; private set => SetProperty(ref _statementTcs, value); }
    public decimal StatementDeposited { get => _statementDeposited; private set => SetProperty(ref _statementDeposited, value); }
    public decimal MatchedGross { get => _matchedGross; private set => SetProperty(ref _matchedGross, value); }

    public void PrepareForDisplay()
    {
        if (_workspaceCache is null) Refresh();
        else RefreshFromWorkspaceCache(force: false);
    }

    private void RefreshFromWorkspaceCache(bool force)
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        if (_workspaceCache is null) return;
        if (_workspaceCache.TryGet(client.Id, client.FinancialYear, out var snapshot) &&
            _workspaceCache.IsModuleReady(client.Id, client.FinancialYear, ClientWorkspaceModule.TaxCredit))
            WarmFromPreparedSnapshot(snapshot);
        if (force) _workspaceCache.QueuePriorityModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.TaxCredit);
        else _workspaceCache.EnsureModulePrepared(client.Id, client.FinancialYear, ClientWorkspaceModule.TaxCredit);
    }

    public void WarmFromPreparedSnapshot(ClientWorkspaceSnapshot snapshot)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != snapshot.ClientId ||
            !client.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;
        var statement = snapshot.TaxCreditImport;
        RebuildFinancialYearDefaults();
        StatementPan = statement?.Pan ?? string.Empty;
        StatementAssessee = statement?.AssesseeName ?? string.Empty;
        StatementAssessmentYear = statement?.AssessmentYear ?? string.Empty;
        StatementUpdatedTill = statement?.DataUpdatedTill?.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture) ?? string.Empty;
        SourcePath = statement?.SourcePath ?? string.Empty;
        SourceType = statement is null ? "Not imported" : GetSourceType(statement.SourcePath);
        SourceCompleteness = statement is null ? "WAITING" : statement.IsCompleteAuthority ? "COMPLETE" : "INCOMPLETE";
        SourceComparisonStatus = statement is null ? "Awaiting source" : $"{SourceType} current authority · add another source format to corroborate";
        ClientMatchStatus = statement is null ? "WAITING" : ResolveClientMatchStatus(client.Pan, client.Gstin, statement.Pan);
        _allRows.Clear();
        _allRows.AddRange(snapshot.TaxCreditReconciliationRows);
        _currentLedgerMasters.Clear();
        _currentLedgerMasters.AddRange(snapshot.TaxCreditLedgerMasters.Where(item =>
            item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)));
        _ledgerMastersPrepared = true;
        RebuildAvailableParts(statement);
        RebuildParties();
        RebuildPartyFilterAndRows();
        RebuildSourceValidation(statement);
        RebuildMasterLedgerOptions(client.Id, client.FinancialYear);
        RefreshSetupStatuses();
        StatusText = statement is null
            ? $"No Form 26AS has been imported for {client.ClientName} · FY {client.FinancialYear}."
            : "26AS source is loaded. Client Matching, Source, TDS Receivable and Entry Match remain separate panels; final accounting/reconciliation rules remain pending.";
    }

    private async Task Import26AsAsync()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var dialog = new OpenFileDialog
        {
            Title = $"Import Form 26AS PDF · {client.ClientName} · FY {client.FinancialYear}",
            Filter = "Form 26AS PDF (*.pdf)|*.pdf|All files (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        StatusText = "Reading Form 26AS and validating Client PAN/FY…";
        try
        {
            var expectedPan = ResolveClientPan(client.Pan, client.Gstin);
            var result = await Task.Run(() => _importService.Import(dialog.FileName, client.Id, client.FinancialYear, expectedPan));
            if (!result.Imported && !result.Duplicate)
            {
                StatusText = result.Message;
                MessageBox.Show(result.Message, "26AS Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (result.Imported && _automationService is not null)
                _automationService.PublishCommittedAccountingImport(
                    client.Id, client.FinancialYear, "26AS", result.Rows.Count, dialog.FileName, "Manual Import");

            Refresh();
            StatusText = result.Duplicate
                ? result.Message
                : result.Warnings.Count == 0
                    ? $"{result.Message} Preliminary automatch panels refreshed; final reconciliation remains NOT STARTED."
                    : $"{result.Message} Review: {string.Join(" | ", result.Warnings.Take(3))}";
        }
        catch (Exception exception)
        {
            StatusText = $"26AS import failed: {exception.Message}";
            MessageBox.Show(StatusText, "26AS Import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ChooseStructuredSource()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var dialog = new OpenFileDialog
        {
            Title = $"Choose 26AS structured source · {client.ClientName} · FY {client.FinancialYear}",
            Filter = "26AS structured files (*.zip;*.txt;*.csv)|*.zip;*.txt;*.csv|TRACES ZIP (*.zip)|*.zip|TRACES TXT (*.txt)|*.txt|CSV (*.csv)|*.csv|All files (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        StatusText = $"Structured source selected: {Path.GetFileName(dialog.FileName)}. UI is ready; structured-source parser/intake wiring remains separate from the accounting-rules phase.";
    }

    private void Refresh()
    {
        if (_workspaceCache is not null)
        {
            RefreshFromWorkspaceCache(force: true);
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            _allRows.Clear();
            _allParties.Clear();
            Rows.Clear();
            PartyFilterOptions.Clear();
            PartyMappings.Clear();
            AvailablePartTabs.Clear();
            SelectedPartTab = null;
            SourceValidationRows.Clear();
            ExistingTdsLedgerOptions.Clear();
            ResetSummary();
            ResetSetupStates();
            StatusText = "Open a Client workspace to use 26AS setup.";
            return;
        }

        RebuildFinancialYearDefaults();
        var statement = _repository.GetCurrentImport(client.Id, client.FinancialYear);
        StatementPan = statement?.Pan ?? string.Empty;
        StatementAssessee = statement?.AssesseeName ?? string.Empty;
        StatementAssessmentYear = statement?.AssessmentYear ?? string.Empty;
        StatementUpdatedTill = statement?.DataUpdatedTill?.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture) ?? string.Empty;
        SourcePath = statement?.SourcePath ?? string.Empty;
        SourceType = statement is null ? "Not imported" : GetSourceType(statement.SourcePath);
        SourceCompleteness = statement is null ? "WAITING" : statement.IsCompleteAuthority ? "COMPLETE" : "INCOMPLETE";
        SourceComparisonStatus = statement is null ? "Awaiting source" : $"{SourceType} current authority · add another source format to corroborate";
        ClientMatchStatus = statement is null ? "WAITING" : ResolveClientMatchStatus(client.Pan, client.Gstin, statement.Pan);

        var reconciliation = _reconciliationService.Build(client.Id, client.FinancialYear);
        _allRows.Clear();
        _allRows.AddRange(reconciliation.Rows);
        RebuildAvailableParts(statement);
        LoadLedgerMasters(client.Id, client.FinancialYear);
        RebuildParties();
        RebuildPartyFilterAndRows();
        RebuildSourceValidation(statement);
        RebuildMasterLedgerOptions(client.Id, client.FinancialYear);
        RefreshSetupStatuses();

        StatusText = statement is null
            ? $"No Form 26AS has been imported for {client.ClientName} · FY {client.FinancialYear}."
            : $"26AS source is loaded. Part tabs show only the selected 26AS client/deductor. Client Matching, Source, TDS Receivable and Entry Match open as separate panels; final accounting/reconciliation rules remain pending.";
    }

    private void RebuildAvailableParts(TaxCreditStatementImport? statement)
    {
        var previous = SelectedPartTab?.PartCode ?? string.Empty;
        AvailablePartTabs.Clear();
        var raw = statement?.RawText ?? string.Empty;
        foreach (var definition in TaxCreditPartTabUi.Definitions)
        {
            var hasRows = _allRows.Any(row => row.Part.Equals(definition.PartCode, StringComparison.OrdinalIgnoreCase));
            var marker = $@"(?im)^\s*PART[-\s]*{Regex.Escape(definition.Roman)}\b";
            var sourceHasPart = hasRows || (!string.IsNullOrWhiteSpace(raw) && Regex.IsMatch(raw, marker, RegexOptions.CultureInvariant));
            if (!sourceHasPart) continue;
            AvailablePartTabs.Add(new TaxCreditPartTabUi
            {
                PartCode = definition.PartCode,
                Roman = definition.Roman,
                Header = definition.Header,
                Description = definition.Description,
                ParsedRowCount = _allRows.Count(row => row.Part.Equals(definition.PartCode, StringComparison.OrdinalIgnoreCase))
            });
        }

        if (AvailablePartTabs.Count == 0)
        {
            foreach (var part in _allRows.Select(row => row.Part).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
                AvailablePartTabs.Add(new TaxCreditPartTabUi { PartCode = part, Roman = part, Header = part, Description = "26AS source part", ParsedRowCount = _allRows.Count(row => row.Part.Equals(part, StringComparison.OrdinalIgnoreCase)) });
        }

        var selected = AvailablePartTabs.FirstOrDefault(item => item.PartCode.Equals(previous, StringComparison.OrdinalIgnoreCase))
                       ?? AvailablePartTabs.FirstOrDefault();
        if (!ReferenceEquals(_selectedPartTab, selected))
        {
            _selectedPartTab = selected;
            OnPropertyChanged(nameof(SelectedPartTab));
            OpenEntryMatchPanelCommand.RaiseCanExecuteChanged();
        }
    }

    private void RebuildParties()
    {
        _allParties.Clear();
        foreach (var group in _allRows.GroupBy(PartyKey).OrderBy(group => group.First().DeductorName, StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.ToArray();
            var candidateNames = rows
                .Where(row => !string.IsNullOrWhiteSpace(row.BooksParty))
                .GroupBy(row => row.BooksParty.Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ToArray();
            var isTdsParty = rows.Any(item => item.TaxKind.Equals("TDS", StringComparison.OrdinalIgnoreCase));
            var strongestCandidate = candidateNames.FirstOrDefault(group =>
            {
                var candidateMaster = FindMasterByName(group.Key);
                return !isTdsParty || (candidateMaster is not null && IsSundryDebtor(candidateMaster));
            });
            var booksName = strongestCandidate?.Key ?? string.Empty;
            var master = FindMasterByName(booksName);
            var rejectedCreditorSuggestion = isTdsParty && strongestCandidate is null && candidateNames.Length > 0;
            var fullyMatched = rows.Length > 0 &&
                               rows.All(item => item.Status.Equals("Matched", StringComparison.OrdinalIgnoreCase)) &&
                               rows.Where(item => item.TaxKind.Equals("TDS", StringComparison.OrdinalIgnoreCase))
                                   .All(item => !string.IsNullOrWhiteSpace(item.BooksParty) &&
                                                FindMasterByName(item.BooksParty) is { } tdsMaster && IsSundryDebtor(tdsMaster));
            var row = new TaxCreditPartyUiRow
            {
                Key = group.Key,
                DeductorName = rows[0].DeductorName,
                Tan = rows[0].DeductorTan,
                TransactionCount = rows.Length,
                Gross = rows.Sum(item => item.StatementAmount),
                Tax = rows.Sum(item => item.StatementTds),
                SuggestedBooksLedger = booksName,
                SuggestedMasterId = master?.AahMasterId ?? string.Empty,
                SuggestedParent = master?.Parent ?? string.Empty,
                Confidence = string.IsNullOrWhiteSpace(booksName) || rows.Length == 0 ? 0 : (int)Math.Round(rows.Average(item => item.Confidence), MidpointRounding.AwayFromZero),
                IsFullyMatched = fullyMatched,
                MatchState = fullyMatched
                    ? "Complete · auto-hide"
                    : rejectedCreditorSuggestion
                        ? "Creditor suggestion excluded"
                        : string.IsNullOrWhiteSpace(booksName) ? "Unmapped" : "Suggestion ready"
            };
            _allParties.Add(row);
        }
    }

    private MasterRecord? FindMasterByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return _currentLedgerMasters.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadLedgerMasters(long clientId, string financialYear)
    {
        _currentLedgerMasters.Clear();
        var ledgers = _masterRepository.Get(clientId, financialYear, false)
            .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _masterRepository.PopulateUsageCounts(clientId, financialYear, ledgers);
        _currentLedgerMasters.AddRange(ledgers);
        _ledgerMastersPrepared = true;
    }

    private void RebuildPartyFilterAndRows()
    {
        var selectedKey = SelectedPartyFilter?.Key ?? string.Empty;
        PartyFilterOptions.Clear();

        var selectedPart = SelectedPartTab?.PartCode ?? string.Empty;
        var partKeys = _allRows
            .Where(row => string.IsNullOrWhiteSpace(selectedPart) || row.Part.Equals(selectedPart, StringComparison.OrdinalIgnoreCase))
            .Select(PartyKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var visibleParties = _allParties
            .Where(item => !_hideCompletedParties || !item.IsFullyMatched)
            .Where(item => partKeys.Contains(item.Key))
            .OrderBy(item => item.DeductorName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var party in visibleParties)
        {
            PartyFilterOptions.Add(new TaxCreditPartyFilterOption
            {
                Key = party.Key,
                DisplayName = party.DeductorName,
                Tan = party.Tan,
                IsFullyMatched = party.IsFullyMatched
            });
        }

        var desired = PartyFilterOptions.FirstOrDefault(item => item.Key.Equals(selectedKey, StringComparison.OrdinalIgnoreCase))
                      ?? PartyFilterOptions.FirstOrDefault();
        if (!ReferenceEquals(_selectedPartyFilter, desired))
        {
            _selectedPartyFilter = desired;
            OnPropertyChanged(nameof(SelectedPartyFilter));
        }

        RebuildVisibleRows();
    }

    private void RebuildVisibleRows()
    {
        Rows.Clear();
        GroupStatementRows.Clear();
        GroupBooksRows.Clear();
        PartyMappings.Clear();
        EntryBooksSuggestions.Clear();

        // Client Matching is a separate panel and intentionally sees every active/unmatched 26AS party.
        foreach (var party in _allParties
                     .Where(item => !_hideCompletedParties || !item.IsFullyMatched)
                     .OrderBy(item => item.DeductorName, StringComparer.OrdinalIgnoreCase))
            PartyMappings.Add(party);

        var selectedKey = SelectedPartyFilter?.Key ?? string.Empty;
        var selectedPart = SelectedPartTab?.PartCode ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selectedKey) || string.IsNullOrWhiteSpace(selectedPart))
        {
            ApplyVisibleSummary(Array.Empty<TaxCreditReconciliationRow>());
            RebuildBooksPartySuggestions();
            return;
        }

        var search = (PartySearchText ?? string.Empty).Trim();
        var visible = _allRows
            .Where(row => row.Part.Equals(selectedPart, StringComparison.OrdinalIgnoreCase))
            .Where(row => PartyKey(row).Equals(selectedKey, StringComparison.OrdinalIgnoreCase))
            .Where(row => string.IsNullOrWhiteSpace(search) || RowMatchesTabSearch(row, search))
            .ToArray();

        foreach (var row in visible)
        {
            Rows.Add(row);
            GroupStatementRows.Add(row);
            if (row.BooksSourceId.HasValue || !string.IsNullOrWhiteSpace(row.BooksParty) || !string.IsNullOrWhiteSpace(row.BooksReference))
            {
                GroupBooksRows.Add(row);
                if (IsEligibleEntrySuggestion(row)) EntryBooksSuggestions.Add(row);
            }
        }

        ApplyVisibleSummary(visible);
        PartySetupStatus = _allParties.Count == 0
            ? "WAITING"
            : $"{_allParties.Count(item => item.IsFullyMatched):N0}/{_allParties.Count:N0} FULLY MATCHED";
        RebuildBooksPartySuggestions();
        PreparePartyMatchCommand.RaiseCanExecuteChanged();
        PrepareEntryMatchCommand.RaiseCanExecuteChanged();
    }

    private static bool RowMatchesTabSearch(TaxCreditReconciliationRow row, string search)
    {
        return row.DeductorName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.DeductorTan.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.Section.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.Status.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.BookingStatus.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.BooksSourceType.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.BooksReference.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.BooksParty.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.StatementAmount.ToString("N2", CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.StatementTds.ToString("N2", CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyVisibleSummary(IReadOnlyCollection<TaxCreditReconciliationRow> visible)
    {
        StatementRows = visible.Count;
        Matched = visible.Count(row => row.Status.Equals("Matched", StringComparison.OrdinalIgnoreCase));
        Probable = visible.Count(row => row.Status.Equals("Probable", StringComparison.OrdinalIgnoreCase));
        MissingInBooks = visible.Count(row => row.Status.Equals("Missing in Books", StringComparison.OrdinalIgnoreCase));
        Review = visible.Count(row => row.Status.Equals("Review", StringComparison.OrdinalIgnoreCase));
        StatementGross = visible.Sum(row => row.StatementAmount);
        StatementTds = visible.Where(row => row.TaxKind.Equals("TDS", StringComparison.OrdinalIgnoreCase)).Sum(row => row.StatementTds);
        StatementTcs = visible.Where(row => row.TaxKind.Equals("TCS", StringComparison.OrdinalIgnoreCase)).Sum(row => row.StatementTds);
        StatementDeposited = visible.Sum(row => row.StatementDeposited);
        MatchedGross = visible.Where(row => row.Status.Equals("Matched", StringComparison.OrdinalIgnoreCase)).Sum(row => row.StatementAmount);
    }

    private void RebuildSourceValidation(TaxCreditStatementImport? statement)
    {
        SourceValidationRows.Clear();
        if (statement is null)
        {
            SourceValidationRows.Add(new TaxCreditSourceValidationUiRow("Source", "No current 26AS authority", "WAITING"));
            return;
        }

        SourceValidationRows.Add(new TaxCreditSourceValidationUiRow("PAN", statement.Pan, IsValidPan(statement.Pan) ? "PASS" : "REVIEW"));
        SourceValidationRows.Add(new TaxCreditSourceValidationUiRow("Assessee", statement.AssesseeName, string.IsNullOrWhiteSpace(statement.AssesseeName) ? "REVIEW" : "PASS"));
        SourceValidationRows.Add(new TaxCreditSourceValidationUiRow("Financial Year", statement.FinancialYear, string.IsNullOrWhiteSpace(statement.FinancialYear) ? "REVIEW" : "PASS"));
        SourceValidationRows.Add(new TaxCreditSourceValidationUiRow("Assessment Year", statement.AssessmentYear, string.IsNullOrWhiteSpace(statement.AssessmentYear) ? "REVIEW" : "PASS"));
        SourceValidationRows.Add(new TaxCreditSourceValidationUiRow("Source completeness", statement.IsCompleteAuthority ? "All supported populated sections understood" : "Unsupported / incomplete populated section", statement.IsCompleteAuthority ? "PASS" : "BLOCK"));
        SourceValidationRows.Add(new TaxCreditSourceValidationUiRow("Statement rows", _allRows.Count.ToString("N0", CultureInfo.InvariantCulture), _allRows.Count > 0 ? "PASS" : "REVIEW"));
        SourceValidationRows.Add(new TaxCreditSourceValidationUiRow("Gross total", _allRows.Sum(row => row.StatementAmount).ToString("N2", CultureInfo.InvariantCulture), "INFO"));
        SourceValidationRows.Add(new TaxCreditSourceValidationUiRow("Tax total", _allRows.Sum(row => row.StatementTds).ToString("N2", CultureInfo.InvariantCulture), "INFO"));
        SourceValidationRows.Add(new TaxCreditSourceValidationUiRow("Deposited total", _allRows.Sum(row => row.StatementDeposited).ToString("N2", CultureInfo.InvariantCulture), "INFO"));
    }

    private void RebuildMasterLedgerOptions(long clientId, string financialYear)
    {
        ExistingTdsLedgerOptions.Clear();
        if (!_ledgerMastersPrepared) LoadLedgerMasters(clientId, financialYear);
        foreach (var master in _currentLedgerMasters
                     .Where(IsLoansAndAdvancesLedger)
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            ExistingTdsLedgerOptions.Add(new MasterLedgerUiOption(master));
        }
        SelectedExistingTdsLedger = ExistingTdsLedgerOptions.FirstOrDefault(item =>
            item.Name.Contains("TDS", StringComparison.OrdinalIgnoreCase) &&
            item.Name.Contains("Receiv", StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshSetupStatuses()
    {
        var statementReady = !string.IsNullOrWhiteSpace(StatementPan);
        SourceCompleteness = !statementReady ? "WAITING" : SourceCompleteness;
        AccountingRulesStatus = "PENDING";
        ReconciliationStatus = "NOT STARTED";
        if (string.IsNullOrWhiteSpace(TdsLedgerName)) TdsLedgerSetupStatus = "NOT SET";
    }

    private void ResetSetupStates()
    {
        StatementPan = string.Empty;
        StatementAssessee = string.Empty;
        StatementAssessmentYear = string.Empty;
        StatementUpdatedTill = string.Empty;
        SourcePath = string.Empty;
        SourceType = "Not imported";
        SourceCompleteness = "WAITING";
        SourceComparisonStatus = "Awaiting PDF/TXT/ZIP corroboration";
        ClientMatchStatus = "WAITING";
        PartySetupStatus = "WAITING";
        TdsLedgerSetupStatus = "NOT SET";
        AccountingRulesStatus = "PENDING";
        ReconciliationStatus = "NOT STARTED";
        PartyMappingStatus = "Select a 26AS party/client and review debtor-ledger suggestions on the Books side.";
        PartySelectionStatus = "No client/deductor link selection prepared.";
        EntryMatchStatus = "No entry link draft prepared.";
        EntrySelectionStatus = "No entry link selection prepared.";
    }

    private void RebuildFinancialYearDefaults()
    {
        var fy = (CurrentFinancialYear ?? string.Empty).Trim();
        NewTdsLedgerName = string.IsNullOrWhiteSpace(fy)
            ? "TDS Receivable FY - XXXX-XXXX"
            : $"TDS Receivable FY - {ExpandFinancialYear(fy)}";
        NewTdsLedgerParent = "Loans & Advances (Asset)";
    }

    private void ChooseWinmanFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Link Winman source · {CurrentClientName}",
            Filter = "Winman / client data files (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        WinmanFilePath = dialog.FileName;
        WinmanStatus = $"Foundation link selected · {Path.GetFileName(dialog.FileName)} · no Winman data is read in this update.";
        PasswordStatus = "Winman link foundation is stored only in the current UI state. Use the manual TRACES password fallback until the full Winman reader is implemented.";
    }

    private void ShowWinmanDetectionUiStatus()
    {
        WinmanStatus = string.IsNullOrWhiteSpace(WinmanFilePath)
            ? "Winman reader is intentionally deferred. You may select a file only to lay the link foundation."
            : "Winman file foundation is linked, but AAH intentionally does not read it or derive a password in this update.";
    }

    private void SavePasswordUiState()
    {
        PasswordStatus = string.IsNullOrWhiteSpace(PendingTracesPassword)
            ? "Enter the TRACES ZIP password first."
            : $"Password entered for {CurrentClientName}. Secure client-specific persistence is represented in the UI; final secret-store wiring is separate.";
    }

    private void ForgetPasswordUiState()
    {
        PendingTracesPassword = string.Empty;
        PasswordStatus = "Saved-password UI cleared for the current Client.";
    }

    private void ShowDownloadsUiStatus()
    {
        DownloadsIntakeStatus = WatchDownloads
            ? "Downloads scan requested. Only files whose PAN + FY resolve to the current Client are eligible to move to the linked Automation Folder."
            : "Downloads watcher is off. Manual scan control remains available when enabled.";
    }

    private void AcceptPartySuggestionUiState()
    {
        var party = SelectedPartyMapping;
        if (party is null) return;
        if (string.IsNullOrWhiteSpace(party.SuggestedBooksLedger))
        {
            PartyMappingStatus = $"{party.DeductorName}: no debtor-side suggestion is currently available. Choose a Sundry Debtors ledger manually on the Books side.";
            return;
        }
        var master = FindMasterByName(party.SuggestedBooksLedger);
        if (master is null || !IsSundryDebtor(master))
        {
            PartyMappingStatus = $"{party.DeductorName}: the preliminary suggestion is not a verified Sundry Debtors ledger and cannot be accepted for TDS.";
            return;
        }
        party.MatchState = "Draft link accepted";
        PartyMappingStatus = $"Draft link: {party.DeductorName} → {party.SuggestedBooksLedger} / MASTERID {DisplayOrDash(party.SuggestedMasterId)}. TDS debtor barrier passed; no accounting entry was changed.";
    }

    private void LinkSelectedPartyUiState()
    {
        var party = SelectedPartyMapping;
        var books = SelectedBooksPartySuggestion;
        if (party is null || books is null) return;
        if (!books.Parent.Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase))
        {
            PartyMappingStatus = "TDS party links are debtors-only. Sundry Creditors and other parent groups cannot be selected.";
            return;
        }
        party.SuggestedBooksLedger = books.Name;
        party.SuggestedMasterId = books.MasterId;
        party.SuggestedParent = books.Parent;
        var passiveShape = DetectPassiveMatchShape(_selectedPartySideCount, _selectedBooksPartySideCount);
        party.MatchState = passiveShape == "One-to-One" ? "Manual draft link" : $"{passiveShape} draft member";
        PartyMappingStatus = $"Manual draft link: {party.DeductorName} → {books.Name} / MASTERID {DisplayOrDash(books.MasterId)}. No final automatch or accounting rule has been applied.";
        RebuildBooksPartySuggestions();
    }

    private void PreparePartyMatchUiState()
    {
        var shape = DetectPassiveMatchShape(_selectedPartySideCount, _selectedBooksPartySideCount);
        if (_selectedPartySideCount == 0 || _selectedBooksPartySideCount == 0)
        {
            PartyMappingStatus = "Select at least one 26AS client/deductor and one Books ledger. AAH determines one-to-one / one-to-many / many-to-one / many-to-many automatically from the selection.";
            return;
        }
        PartyMappingStatus = $"{shape} client/deductor draft prepared from {_selectedPartySideCount:N0} 26AS selection(s) and {_selectedBooksPartySideCount:N0} Books ledger selection(s). The relationship shape is passive and cannot be selected manually.";
    }

    private void PrepareEntryMatchUiState()
    {
        var shape = DetectPassiveMatchShape(_selectedEntrySideCount, _selectedBooksEntrySideCount);
        if (_selectedEntrySideCount == 0 || _selectedBooksEntrySideCount == 0)
        {
            EntryMatchStatus = "Select at least one 26AS entry and one Books candidate. AAH determines the relationship shape automatically from the selection.";
            return;
        }
        EntryMatchStatus = $"{shape} entry draft prepared from {_selectedEntrySideCount:N0} 26AS entry selection(s) and {_selectedBooksEntrySideCount:N0} Books selection(s). No accounting/reconciliation is committed.";
    }

    private static string DetectPassiveMatchShape(int leftCount, int rightCount)
    {
        if (leftCount <= 0 || rightCount <= 0) return "Waiting for both sides";
        if (leftCount == 1 && rightCount == 1) return "One-to-One";
        if (leftCount == 1) return "One-to-Many";
        if (rightCount == 1) return "Many-to-One";
        return "Many-to-Many";
    }

    private void ClearPartyLinkUiState()
    {
        var party = SelectedPartyMapping;
        if (party is null) return;
        party.MatchState = "Unmapped";
        PartyMappingStatus = $"Draft Books-side link cleared for {party.DeductorName}.";
    }

    private void HoldPartyUiState()
    {
        var party = SelectedPartyMapping;
        if (party is null) return;
        party.MatchState = "Hold / review";
        PartyMappingStatus = $"{party.DeductorName} is marked Hold / review in the UI workspace.";
    }

    private void LinkExistingTdsLedgerUiState()
    {
        var option = SelectedExistingTdsLedger;
        if (option is null) return;
        if (!option.Parent.Equals("Loans & Advances (Asset)", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "TDS Receivable must be under Loans & Advances (Asset). The selected ledger was rejected.";
            return;
        }
        TdsLedgerName = option.Name;
        TdsLedgerMasterId = option.MasterId;
        TdsLedgerParent = "Loans & Advances (Asset)";
        TdsLedgerSetupStatus = "DRAFT LINK READY";
        StatusText = $"TDS Receivable draft link: {option.Name} · MASTERID {DisplayOrDash(option.MasterId)}. Final accounting rules remain pending.";
    }

    private void CreateTdsLedgerDraftUiState()
    {
        UseExistingTdsLedger = false;
        CreateNewTdsLedger = true;
        TdsLedgerName = NewTdsLedgerName;
        TdsLedgerMasterId = "Pending Tally CREATE";
        NewTdsLedgerParent = "Loans & Advances (Asset)";
        TdsLedgerParent = "Loans & Advances (Asset)";
        TdsLedgerSetupStatus = "CREATE DRAFT READY";
        StatusText = $"New-ledger UI draft prepared: {TdsLedgerName}. No Master has been created yet.";
    }

    public void UpdatePartySelectionCounts(int statementSideCount, int booksSideCount)
    {
        _selectedPartySideCount = Math.Max(0, statementSideCount);
        _selectedBooksPartySideCount = Math.Max(0, booksSideCount);
        var shape = DetectPassiveMatchShape(_selectedPartySideCount, _selectedBooksPartySideCount);
        PartyDetectedMatchShape = $"Passive relationship: {shape}";
        PartySelectionStatus = $"Selected: {_selectedPartySideCount:N0} 26AS client/deductor row(s) ↔ {_selectedBooksPartySideCount:N0} Books ledger row(s).";
    }

    public void UpdateEntrySelectionCounts(int statementSideCount, int booksSideCount)
    {
        _selectedEntrySideCount = Math.Max(0, statementSideCount);
        _selectedBooksEntrySideCount = Math.Max(0, booksSideCount);
        var shape = DetectPassiveMatchShape(_selectedEntrySideCount, _selectedBooksEntrySideCount);
        EntryDetectedMatchShape = $"Passive relationship: {shape}";
        EntrySelectionStatus = $"Selected: {_selectedEntrySideCount:N0} 26AS entry row(s) ↔ {_selectedBooksEntrySideCount:N0} Books suggestion row(s).";
    }

    private void RebuildBooksPartySuggestions()
    {
        var suggestedName = SelectedPartyMapping?.SuggestedBooksLedger ?? string.Empty;
        var suggestedConfidence = SelectedPartyMapping?.Confidence ?? 0;
        BooksPartySuggestions.Clear();
        var search = BooksPartySearchText.Trim();
        foreach (var master in _currentLedgerMasters
                     .Where(IsSundryDebtor)
                     .Where(item => search.Length > 0
                         ? item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || item.AahMasterId.Contains(search, StringComparison.OrdinalIgnoreCase)
                         : item.UsageCount > 0)
                     .OrderBy(item => !string.IsNullOrWhiteSpace(suggestedName) && item.Name.Equals(suggestedName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenByDescending(item => item.UsageCount)
                     .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var isSuggested = !string.IsNullOrWhiteSpace(suggestedName) && master.Name.Equals(suggestedName, StringComparison.OrdinalIgnoreCase);
            BooksPartySuggestions.Add(new TaxCreditBooksPartySuggestionUiRow
            {
                Name = master.Name,
                MasterId = master.AahMasterId,
                Parent = master.Parent,
                SuggestionState = isSuggested ? "Suggested" : "Available",
                Confidence = isSuggested ? suggestedConfidence : 0,
                EntryCount = master.UsageCount
            });
        }
        SelectedBooksPartySuggestion = BooksPartySuggestions.FirstOrDefault(item => item.SuggestionState.Equals("Suggested", StringComparison.OrdinalIgnoreCase))
                                       ?? BooksPartySuggestions.FirstOrDefault();
    }

    private bool IsEligibleEntrySuggestion(TaxCreditReconciliationRow row)
    {
        if (!row.TaxKind.Equals("TDS", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(row.BooksParty)) return false;
        var master = FindMasterByName(row.BooksParty);
        return master is not null && IsSundryDebtor(master);
    }

    private static bool IsSundryDebtor(MasterRecord master) =>
        master.Parent.Trim().Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoansAndAdvancesLedger(MasterRecord master) =>
        master.Parent.Trim().Equals("Loans & Advances (Asset)", StringComparison.OrdinalIgnoreCase);

    private void ClearPartyFilter()
    {
        PartySearchText = string.Empty;
        SelectedPartyFilter = PartyFilterOptions.FirstOrDefault();
    }

    private static string PartyKey(TaxCreditReconciliationRow row)
    {
        var tan = (row.DeductorTan ?? string.Empty).Trim();
        return tan.Length > 0 ? tan.ToUpperInvariant() : (row.DeductorName ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string ResolveClientMatchStatus(string pan, string gstin, string statementPan)
    {
        var expected = ResolveClientPan(pan, gstin);
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(statementPan)) return "REVIEW";
        return expected.Equals(statementPan.Trim(), StringComparison.OrdinalIgnoreCase) ? "MATCHED" : "PAN MISMATCH";
    }

    private static string GetSourceType(string path)
    {
        var extension = Path.GetExtension(path ?? string.Empty).ToUpperInvariant();
        return extension switch
        {
            ".PDF" => "PDF",
            ".TXT" => "TRACES TXT",
            ".ZIP" => "TRACES ZIP",
            ".CSV" => "CSV",
            _ => string.IsNullOrWhiteSpace(extension) ? "Unknown" : extension.TrimStart('.')
        };
    }

    private static bool IsValidPan(string value)
    {
        var pan = (value ?? string.Empty).Trim().ToUpperInvariant();
        return pan.Length == 10 && pan.Take(5).All(char.IsLetter) && pan.Skip(5).Take(4).All(char.IsDigit) && char.IsLetter(pan[9]);
    }

    private static string ExpandFinancialYear(string value)
    {
        var normalized = value.Replace("/", "-").Trim();
        var parts = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return normalized;
        if (parts[0].Length == 4 && parts[1].Length == 2 && int.TryParse(parts[0], out var startYear) && int.TryParse(parts[1], out var shortEnd))
        {
            var century = (startYear / 100) * 100;
            var endYear = century + shortEnd;
            if (endYear < startYear) endYear += 100;
            return $"{startYear:0000}-{endYear:0000}";
        }
        return normalized;
    }

    private static string DisplayOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void ResetSummary()
    {
        StatementRows = 0;
        Matched = 0;
        Probable = 0;
        MissingInBooks = 0;
        Review = 0;
        StatementGross = 0m;
        StatementTds = 0m;
        StatementTcs = 0m;
        StatementDeposited = 0m;
        MatchedGross = 0m;
    }

    private void OnClientContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ActiveClientContext.CurrentClient), StringComparison.Ordinal)) return;
        OnPropertyChanged(nameof(CurrentClientId));
        OnPropertyChanged(nameof(CurrentFinancialYear));
        OnPropertyChanged(nameof(CurrentClientName));
        OnPropertyChanged(nameof(ClientPan));
        OnPropertyChanged(nameof(CanImport));
        Import26AsCommand.RaiseCanExecuteChanged();
        ImportStructuredSourceCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        ScanDownloadsCommand.RaiseCanExecuteChanged();
        LinkWinmanCommand.RaiseCanExecuteChanged();
        DetectWinmanPasswordCommand.RaiseCanExecuteChanged();
        SavePasswordCommand.RaiseCanExecuteChanged();
        ForgetPasswordCommand.RaiseCanExecuteChanged();
        _ledgerMastersPrepared = false;
        if (_workspaceCache is null) Refresh();
        else RefreshFromWorkspaceCache(force: false);
    }

    private static string ResolveClientPan(string pan, string gstin)
    {
        var direct = new string((pan ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (direct.Length == 10) return direct;
        var normalizedGstin = new string((gstin ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return normalizedGstin.Length == 15 ? normalizedGstin.Substring(2, 10) : string.Empty;
    }
}

public sealed class TaxCreditPartyFilterOption
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Tan { get; set; } = string.Empty;
    public bool IsFullyMatched { get; set; }
    public string Display => string.IsNullOrWhiteSpace(Tan) ? DisplayName : $"{DisplayName} · {Tan}";
}

public sealed class TaxCreditPartyUiRow : ViewModelBase
{
    private string _matchState = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string DeductorName { get; set; } = string.Empty;
    public string Tan { get; set; } = string.Empty;
    public int TransactionCount { get; set; }
    public decimal Gross { get; set; }
    public decimal Tax { get; set; }
    public string SuggestedBooksLedger { get; set; } = string.Empty;
    public string SuggestedMasterId { get; set; } = string.Empty;
    public string SuggestedParent { get; set; } = string.Empty;
    public int Confidence { get; set; }
    public bool IsFullyMatched { get; set; }
    public string MatchState { get => _matchState; set => SetProperty(ref _matchState, value ?? string.Empty); }
}

public sealed class TaxCreditBooksPartySuggestionUiRow
{
    public string Name { get; set; } = string.Empty;
    public string MasterId { get; set; } = string.Empty;
    public string Parent { get; set; } = string.Empty;
    public string SuggestionState { get; set; } = string.Empty;
    public int Confidence { get; set; }
    public int EntryCount { get; set; }
}

public sealed record TaxCreditSourceValidationUiRow(string Check, string Value, string State);

public sealed class MasterLedgerUiOption
{
    public MasterLedgerUiOption(MasterRecord master)
    {
        Name = master.Name;
        Parent = master.Parent;
        MasterId = master.AahMasterId;
        Source = master.Source;
        OpeningBalance = master.OpeningBalance;
    }

    public string Name { get; }
    public string Parent { get; }
    public string MasterId { get; }
    public string Source { get; }
    public decimal? OpeningBalance { get; }
    public string Display => $"{Name} · MASTERID {MasterId} · {Parent}";
}

public sealed class TaxCreditPartTabUi
{
    public static IReadOnlyList<TaxCreditPartTabUi> Definitions { get; } = new[]
    {
        new TaxCreditPartTabUi { PartCode = "I", Roman = "I", Header = "Part I · TDS", Description = "Details of Tax Deducted at Source" },
        new TaxCreditPartTabUi { PartCode = "II", Roman = "II", Header = "Part II · 15G / 15H", Description = "Tax Deducted at Source for Form 15G / 15H" },
        new TaxCreditPartTabUi { PartCode = "III", Roman = "III", Header = "Part III · Special benefit/kind", Description = "Transactions under the special provisos to sections 194B / 194R / 194S / 194BA" },
        new TaxCreditPartTabUi { PartCode = "IV", Roman = "IV", Header = "Part IV · 194IA / 194IB / 194M / 194S", Description = "Specified seller / landlord / contractor / professional / VDA tax-credit transactions" },
        new TaxCreditPartTabUi { PartCode = "V", Roman = "V", Header = "Part V · Form 26QE", Description = "Specified VDA transactions under section 194S" },
        new TaxCreditPartTabUi { PartCode = "VI", Roman = "VI", Header = "Part VI · TCS", Description = "Details of Tax Collected at Source" },
        new TaxCreditPartTabUi { PartCode = "VII", Roman = "VII", Header = "Part VII · Refunds", Description = "Details of paid refund" },
        new TaxCreditPartTabUi { PartCode = "VIII", Roman = "VIII", Header = "Part VIII · Buyer / Tenant TDS", Description = "Specified TDS transactions for buyer / tenant / payer" },
        new TaxCreditPartTabUi { PartCode = "IX", Roman = "IX", Header = "Part IX · 26QE Buyer", Description = "Specified VDA transaction / demand-payment details" },
        new TaxCreditPartTabUi { PartCode = "X", Roman = "X", Header = "Part X · Defaults", Description = "TDS/TCS defaults from statement processing" }
    };

    public string PartCode { get; set; } = string.Empty;
    public string Roman { get; set; } = string.Empty;
    public string Header { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int ParsedRowCount { get; set; }
    public string TabHeader => ParsedRowCount > 0 ? $"{Header} ({ParsedRowCount:N0})" : Header;
}
