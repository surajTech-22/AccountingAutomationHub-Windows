using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using AccountingAutomationHub.Application.Clients;
using AccountingAutomationHub.Application.Imports;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Application.Settings;
using System.ComponentModel;
using AccountingAutomationHub.Desktop.Collections;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Desktop.Views;
using AccountingAutomationHub.Domain.Imports;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Infrastructure.Gst;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Accounting;
using AccountingAutomationHub.Infrastructure.Governance;
using Microsoft.Win32;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class PurchaseImportViewModel :
    PageViewModel
{
    private const string EReturnsRootPreference = "EReturnsRoot";

    private readonly IPurchaseImportService _service;
    private readonly IPurchaseManualImportService _manualImportService;
    private readonly IPurchaseAutoImportService _autoImportService;
    private readonly IPurchaseRecordRepository _purchaseRecordRepository;
    private readonly IPurchaseMappingRepository _purchaseMappingRepository;
    private IMasterRepository? _masterRepository;
    private IStockMasterRepository? _stockRepository;
    private PurchaseStockNormalizationService? _stockNormalizationService;
    private readonly IClientPreferenceRepository _clientPreferenceRepository;
    private readonly IApplicationPreferenceRepository _applicationPreferenceRepository;
    private readonly EReturnsRootContext _eReturnsRootContext;
    private readonly ActiveClientContext _clientContext;
    private readonly IImportActivityRepository _activityRepository;
    private readonly ImportActivityContext _activityContext;
    private readonly SqliteGovernanceRepository _governanceRepository;
    private AccountingRefreshCoordinator? _refreshCoordinator;
    private LedgerTotalService? _ledgerTotalService;
    private AccountingLiveChangeHub? _liveChangeHub;
    private BackgroundAutomationService? _automationService;
    private CancellationTokenSource? _cancellationSource;
    private long _automaticImportGeneration;
    private bool _rootPromptAttempted;
    private readonly DispatcherTimer _liveEditSaveTimer;
    private readonly HashSet<long> _pendingLiveEditIds = [];
    private bool _liveEditSaveRunning;
    private bool _isPageActive;
    private long _preparedClientId;
    private string _preparedFinancialYear = string.Empty;

    private string _gstr2AFilePath = string.Empty;
    private string _gstr2BFilePath = string.Empty;
    private string _eReturnsRootPath = string.Empty;
    private string _sourceVerificationText = string.Empty;
    private string _searchText = string.Empty;
    private bool _saveSourceFiles;
    private string _companyName = string.Empty;
    private string _clientGstin = string.Empty;
    private string _financialYear = string.Empty;
    private string _statusText =
        "Select GSTR-2A and optional GSTR-2B workbooks.";
    private string _warningText = string.Empty;
    private string _errorText = string.Empty;
    private int _progressPercent;
    private bool _isBusy;
    private bool _isImportPanelExpanded;
    private string _editStatusText = string.Empty;
    private PurchaseImportSummary _summary = new();

    public PurchaseImportViewModel(
        IPurchaseImportService service,
        IPurchaseManualImportService manualImportService,
        IPurchaseAutoImportService autoImportService,
        IPurchaseRecordRepository purchaseRecordRepository,
        IPurchaseMappingRepository purchaseMappingRepository,
        IClientPreferenceRepository clientPreferenceRepository,
        IApplicationPreferenceRepository applicationPreferenceRepository,
        EReturnsRootContext eReturnsRootContext,
        ActiveClientContext clientContext,
        IImportActivityRepository activityRepository,
        ImportActivityContext activityContext)
        : base(
            "Purchase",
            "Automatic GST purchase records and manual Winman workbook support")
    {
        _service = service;
        _manualImportService = manualImportService;
        _autoImportService = autoImportService;
        _purchaseRecordRepository = purchaseRecordRepository;
        _purchaseMappingRepository = purchaseMappingRepository;
        _clientPreferenceRepository = clientPreferenceRepository;
        _applicationPreferenceRepository = applicationPreferenceRepository;
        _eReturnsRootContext = eReturnsRootContext;
        _clientContext = clientContext;
        _activityRepository = activityRepository;
        _activityContext = activityContext;
        _governanceRepository = new SqliteGovernanceRepository();
        _governanceRepository.Initialize();

        _liveEditSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _liveEditSaveTimer.Tick += OnLiveEditSaveTimerTick;

        _clientContext.PropertyChanged +=
            OnClientContextPropertyChanged;

        _eReturnsRootContext.PropertyChanged +=
            OnEReturnsRootPropertyChanged;

        Browse2ACommand = new RelayCommand(
            Browse2A,
            () => !IsBusy && CanImportPurchase);

        Browse2BCommand = new RelayCommand(
            Browse2B,
            () => !IsBusy && CanImportPurchase);

        BrowseEReturnsRootCommand = new RelayCommand(
            BrowseEReturnsRoot,
            () => !IsBusy && CanImportPurchase);

        ManualPurchaseSheetImportCommand = new RelayCommand(
            ManualImportPurchaseSheet,
            () => !IsBusy && CanImportPurchase && _clientContext.CurrentClient is not null);

        RemapPurchaseSheetCommand = new RelayCommand(
            RemapPurchaseSheet,
            () => !IsBusy && CanImportPurchase && _clientContext.CurrentClient is not null);

        RunImportCommand = new AsyncRelayCommand(
            RunImportAsync,
            CanRunImport);

        CancelCommand = new RelayCommand(
            Cancel,
            () => IsBusy);

        ClearCommand = new RelayCommand(
            Clear,
            () => !IsBusy);

        ClearModuleCommand = new RelayCommand(
            ClearPurchaseModule,
            () => !IsBusy && _clientContext.CurrentClient is not null && CanDeletePurchase);

        ItemNameMappingCommand = new RelayCommand(
            OpenItemNameMapping,
            () => !IsBusy && CanEditPurchase && AllRows.Any(HasInventoryEvidenceForItemName));

        UnitMappingCommand = new RelayCommand(
            OpenUnitMapping,
            () => !IsBusy && CanEditPurchase && AllRows.Any(row => !string.IsNullOrWhiteSpace(row.Unit)));

        QuantityConversionCommand = new RelayCommand(
            OpenQuantityConversion,
            () => !IsBusy && CanEditPurchase && AllRows.Any(row => !string.IsNullOrWhiteSpace(row.ItemName) && !string.IsNullOrWhiteSpace(row.Unit)));

        SaveChangesCommand = new RelayCommand(
            SaveChanges,
            CanSaveChanges);

        RefreshCommand = new RelayCommand(
            () => ScheduleAutomaticImport(promptForRoot: false),
            () => !IsBusy && CanImportPurchase);

        ReimportCommand = new AsyncRelayCommand(
            ReimportAutomaticSourcesAsync,
            () => !IsBusy && _clientContext.CurrentClient is not null && CanImportPurchase);

        // Module construction must stay passive. BackgroundAutomationService owns
        // folder Auto Import; merely constructing a hidden Purchase page must not
        // start a second Purchase scan/accounting refresh.
        ApplyClientContext(loadRows: false);
    }

    public void AttachParityServices(
        IMasterRepository masterRepository,
        AccountingRefreshCoordinator refreshCoordinator,
        LedgerTotalService? ledgerTotalService = null,
        AccountingLiveChangeHub? liveChangeHub = null,
        IStockMasterRepository? stockRepository = null,
        PurchaseStockNormalizationService? stockNormalizationService = null,
        BackgroundAutomationService? automationService = null)
    {
        _masterRepository = masterRepository ?? throw new ArgumentNullException(nameof(masterRepository));
        _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
        _ledgerTotalService = ledgerTotalService;
        _liveChangeHub = liveChangeHub;
        _stockRepository = stockRepository;
        _stockNormalizationService = stockNormalizationService;
        _automationService = automationService;
        RefreshIndirectExpenseLedgerOptions();
        RefreshCommands();
    }

    public string Gstr2AFilePath
    {
        get => _gstr2AFilePath;
        set
        {
            if (SetProperty(
                    ref _gstr2AFilePath,
                    value))
            {
                RefreshCommands();
            }
        }
    }

    public string Gstr2BFilePath
    {
        get => _gstr2BFilePath;
        set => SetProperty(
            ref _gstr2BFilePath,
            value);
    }

    public string EReturnsRootPath
    {
        get => _eReturnsRootPath;
        set
        {
            if (SetProperty(
                    ref _eReturnsRootPath,
                    value))
            {
                RefreshCommands();
            }
        }
    }

    public string SourceVerificationText
    {
        get => _sourceVerificationText;
        private set => SetProperty(
            ref _sourceVerificationText,
            value);
    }

    /// <summary>
    /// One shared Purchase search string for every accounting/reconciliation tab.
    /// The grid controls are recreated/loaded independently by WPF TabControl, so
    /// keeping the text on the page ViewModel prevents a tab switch or refresh from
    /// silently clearing the user's active search.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value ?? string.Empty);
    }

    public bool SaveSourceFiles
    {
        get => _saveSourceFiles;
        set => SetProperty(
            ref _saveSourceFiles,
            value);
    }

    public string CompanyName
    {
        get => _companyName;
        set => SetProperty(
            ref _companyName,
            value);
    }

    public string ClientGstin
    {
        get => _clientGstin;
        set => SetProperty(
            ref _clientGstin,
            value);
    }

    public string FinancialYear
    {
        get => _financialYear;
        private set
        {
            if (!SetProperty(ref _financialYear, value)) return;
            OnPropertyChanged(nameof(FinancialYearStart));
            OnPropertyChanged(nameof(FinancialYearEnd));
        }
    }

    public DateTime? FinancialYearStart => TryFinancialYearRange(FinancialYear, out var start, out _) ? start : null;
    public DateTime? FinancialYearEnd => TryFinancialYearRange(FinancialYear, out _, out var end) ? end : null;

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

    public string EditStatusText
    {
        get => _editStatusText;
        private set => SetProperty(
            ref _editStatusText,
            value);
    }

    public bool HasPersistedRows =>
        AllRows.Any(record => record.PersistentId > 0);

    public bool HasWarning =>
        WarningText.Length > 0;

    public bool HasError =>
        ErrorText.Length > 0;

    public PurchaseImportSummary Summary
    {
        get => _summary;
        private set => SetProperty(
            ref _summary,
            value);
    }

    public ObservableCollection<PurchaseImportRecord>
        PurchaseRows { get; } = new BulkObservableCollection<PurchaseImportRecord>();

    public ObservableCollection<PurchaseImportRecord>
        RcmRows { get; } = new BulkObservableCollection<PurchaseImportRecord>();

    public ObservableCollection<PurchaseImportRecord>
        DebitNoteRows { get; } = new BulkObservableCollection<PurchaseImportRecord>();

    public ObservableCollection<PurchaseImportRecord>
        DebitNoteReverseRows { get; } = new BulkObservableCollection<PurchaseImportRecord>();

    public ObservableCollection<PurchaseImportRecord>
        AllRows { get; } = new BulkObservableCollection<PurchaseImportRecord>();

    public ObservableCollection<PurchaseImportRecord>
        ReconciliationRows { get; } = new BulkObservableCollection<PurchaseImportRecord>();

    public ObservableCollection<PurchaseImportIssue>
        Issues { get; } = new BulkObservableCollection<PurchaseImportIssue>();

    /// <summary>
    /// Existing Ledger Masters whose parent hierarchy is Indirect Expenses.
    /// GST-included-in-cost rows use only this selector; a new custom name is
    /// created as a Manual Ledger directly under Indirect Expenses.
    /// </summary>
    public ObservableCollection<string> IndirectExpenseLedgerNames { get; } = new BulkObservableCollection<string>();

    public bool CanImportPurchase => DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.Import) &&
        (!NetworkClientRuntime.IsNetworkClient || !NetworkClientRuntime.AccountingAccessSuspended);

    public bool CanReimportPurchase =>
        !IsBusy &&
        _clientContext.CurrentClient is not null &&
        CanImportPurchase;

    public bool CanEditPurchase => (!NetworkClientRuntime.IsNetworkClient || !NetworkClientRuntime.AccountingAccessSuspended) &&
        (DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.PurchaseEdit) ||
         DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.EditAccounting));

    public bool CanDeletePurchase => (!NetworkClientRuntime.IsNetworkClient || !NetworkClientRuntime.AccountingAccessSuspended) &&
        (DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.PurchaseDelete) ||
         DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.DeleteAccounting));

    public bool IsPurchaseReadOnly => !CanEditPurchase;

    public string PurchasePermissionSummary => $"Role: {DesktopPermissionService.EffectiveRoleName(_governanceRepository)} • {(NetworkClientRuntime.IsNetworkClient ? "Windows processing node • " : string.Empty)}Import {(CanImportPurchase ? "allowed" : "denied")} • Edit {(CanEditPurchase ? "allowed" : "denied")} • Delete {(CanDeletePurchase ? "allowed" : "denied")}";

    public RelayCommand Browse2ACommand { get; }
    public RelayCommand Browse2BCommand { get; }
    public RelayCommand BrowseEReturnsRootCommand { get; }
    public RelayCommand ManualPurchaseSheetImportCommand { get; }
    public RelayCommand RemapPurchaseSheetCommand { get; }
    public AsyncRelayCommand RunImportCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand ClearModuleCommand { get; }
    public RelayCommand ItemNameMappingCommand { get; }
    public RelayCommand UnitMappingCommand { get; }
    public RelayCommand QuantityConversionCommand { get; }
    public RelayCommand SaveChangesCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ReimportCommand { get; }

    private bool CanRunImport() =>
        CanImportPurchase &&
        !IsBusy &&
        !string.IsNullOrWhiteSpace(
            Gstr2AFilePath);

    private void Browse2A()
    {
        var selected = BrowseWorkbook(
            "Select GSTR-2A purchase workbook");

        if (selected is not null)
        {
            Gstr2AFilePath = selected;
            ErrorText = string.Empty;
            NotifyStatusVisibility();
        }
    }

    private void Browse2B()
    {
        var selected = BrowseWorkbook(
            "Select matching GSTR-2B workbook");

        if (selected is not null)
        {
            Gstr2BFilePath = selected;
            ErrorText = string.Empty;
            NotifyStatusVisibility();
        }
    }

    private void BrowseEReturnsRoot()
    {
        if (!SelectAndSaveEReturnsRoot())
        {
            return;
        }

        ScheduleAutomaticImport(promptForRoot: false);
    }

    private bool SelectAndSaveEReturnsRoot()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the E-returns root folder",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        _rootPromptAttempted = true;
        EReturnsRootPath = dialog.FolderName;
        ErrorText = string.Empty;
        IsImportPanelExpanded = false;

        _applicationPreferenceRepository.Set(
            EReturnsRootPreference,
            EReturnsRootPath);

        _eReturnsRootContext.SetRoot(
            EReturnsRootPath);

        NotifyStatusVisibility();
        return true;
    }

    private static string? BrowseWorkbook(
        string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter =
                "Excel workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }

    private async Task RunAutoImportAsync(
        bool promptForRoot,
        long generation,
        bool forceReimport = false,
        DateTime? reimportMonth = null)
    {
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
                "Select an active client. Automatic Purchase import is waiting.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ClientGstin) ||
            ClientGstin.Length != 15)
        {
            StatusText =
                "Automatic Purchase import is waiting for a valid client GSTIN.";
            return;
        }

        if (string.IsNullOrWhiteSpace(FinancialYear))
        {
            StatusText =
                "Automatic Purchase import is waiting for the Financial Year.";
            return;
        }

        if (string.IsNullOrWhiteSpace(EReturnsRootPath) &&
            promptForRoot &&
            !_rootPromptAttempted)
        {
            _rootPromptAttempted = true;

            if (!SelectAndSaveEReturnsRoot())
            {
                IsImportPanelExpanded = true;
                StatusText =
                    "E-returns root is not configured. Open Import setup to select it.";
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(EReturnsRootPath))
        {
            IsImportPanelExpanded = true;
            StatusText =
                "E-returns root is not configured. Open Import setup to select it.";
            return;
        }

        if (!Directory.Exists(EReturnsRootPath))
        {
            IsImportPanelExpanded = true;
            StatusText =
                "The saved E-returns root is not available. Use Change Root in Import setup.";
            WarningText =
                EReturnsRootPath;
            NotifyStatusVisibility();
            return;
        }

        IsBusy = true;
        EditStatusText = string.Empty;
        ErrorText = string.Empty;
        WarningText = string.Empty;
        SourceVerificationText = string.Empty;
        ProgressPercent = 0;
        StatusText = reimportMonth.HasValue
            ? $"Checking Purchase sources for {reimportMonth.Value:MMMM yyyy} reimport..."
            : "Checking Purchase sources automatically...";
        NotifyStatusVisibility();

        using var cancellationSource =
            new CancellationTokenSource();

        _cancellationSource = cancellationSource;

        try
        {
            var progress =
                new Progress<PurchaseImportProgress>(
                    update =>
                    {
                        ProgressPercent = update.Percent;
                        StatusText = update.Stage;
                    });

            var result = await _autoImportService.ImportAsync(
                new PurchaseAutoImportRequest
                {
                    ClientId = client.Id,
                    ClientGstin = ClientGstin,
                    FinancialYear = FinancialYear,
                    CompanyName = CompanyName,
                    EReturnsRootPath = EReturnsRootPath,
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

            CompanyName = result.CompanyName;
            ClientGstin = result.ClientGstin;
            FinancialYear = result.FinancialYear;
            Summary = result.Summary;
            WarningText = string.Join(
                Environment.NewLine,
                result.Warnings);
            SourceVerificationText = BuildSourceVerificationText(
                result.Verification);
            if (reimportMonth.HasValue)
            {
                var annualForms = new List<string>();
                if (UsesAnnualSourceForMonth(result.Verification.TwoA, reimportMonth.Value))
                    annualForms.Add("2A");
                if (UsesAnnualSourceForMonth(result.Verification.TwoB, reimportMonth.Value))
                    annualForms.Add("2B");
                if (annualForms.Count > 0)
                {
                    SourceVerificationText +=
                        $" | {reimportMonth.Value:MMM yyyy}: annual {string.Join("/", annualForms)} selected-month extraction";
                }
            }
            result.SourceFingerprint = BuildSourceFingerprint(result);

            if (result.WasBlocked)
            {
                LoadSavedAutomaticRows(
                    client.Id,
                    result.FinancialYear,
                    result.Issues);

                StatusText =
                    "Automatic Purchase import blocked. Existing saved records were retained.";
                return;
            }

            var hasTwoASource = result.SourceManifest.Any(
                source => string.Equals(
                    source.FileType,
                    "2A",
                    StringComparison.OrdinalIgnoreCase));

            if (!hasTwoASource)
            {
                LoadSavedAutomaticRows(
                    client.Id,
                    result.FinancialYear,
                    result.Issues);

                StatusText =
                    "No valid GSTR-2A source was found. Existing saved records were retained.";
                return;
            }

            if (!forceReimport &&
                _purchaseRecordRepository.IsAutomaticImportCurrent(
                    client.Id,
                    result.FinancialYear,
                    result.SourceFingerprint))
            {
                LoadSavedAutomaticRows(
                    client.Id,
                    result.FinancialYear,
                    result.Issues);

                ProgressPercent = 100;
                StatusText =
                    $"Purchase is up to date: {AllRows.Count:N0} saved rows.";
                return;
            }

            if (reimportMonth.HasValue)
            {
                if (!HasSourceCoverageForMonth(result.Verification.TwoA, reimportMonth.Value))
                {
                    LoadSavedAutomaticRows(
                        client.Id,
                        result.FinancialYear,
                        result.Issues);

                    StatusText =
                        $"{reimportMonth.Value:MMMM yyyy} was not reimported because no verified GSTR-2A annual/monthly source covers that month. Existing saved rows were retained.";
                    return;
                }
            }

            // Fix127: selected-month source coverage is validated before Stock normalization.
            // A rejected import cannot mutate Stock authority or prepared cache state.
            var stockSummary = _stockNormalizationService is not null && result.Records.Any(row => !string.IsNullOrWhiteSpace(row.ItemName))
                ? _stockNormalizationService.Normalize(client.Id, result.FinancialYear, CompanyName, result.Records)
                : null;

            if (reimportMonth.HasValue)
            {
                result.PersistedRows =
                    _purchaseRecordRepository.ReplaceAutomaticImportMonth(
                        client.Id,
                        result.FinancialYear,
                        result,
                        reimportMonth.Value);
            }
            else
            {
                result.PersistedRows =
                    _purchaseRecordRepository.ReplaceAutomaticImport(
                        client.Id,
                        result.FinancialYear,
                        result);
            }

            var automaticSourcePath = result.SourceManifest
                .Select(source => source.SourcePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
                ?? EReturnsRootPath;
            if (stockSummary is not null && stockSummary.InventoryRows > 0)
                PublishCommittedImport(client.Id, result.FinancialYear, "Stock", stockSummary.InventoryRows, automaticSourcePath, "Automatic GST Import");

            if (_refreshCoordinator is not null)
                await _refreshCoordinator.RefreshAsync(client, cancellationSource.Token);

            _ledgerTotalService?.Invalidate(client.Id, result.FinancialYear);
            PublishCommittedImport(
                client.Id,
                result.FinancialYear,
                "Purchase",
                result.PersistedRows,
                automaticSourcePath,
                reimportMonth.HasValue ? "Automatic GST Reimport" : "Automatic GST Import");

            if (reimportMonth.HasValue)
            {
                // Fix12: selected-month replacement already rebuilds the authoritative
                // Journal/GSTR-3B books data through AccountingRefreshCoordinator above.
                // The common committed-import bridge invalidates/prepares Purchase + dependencies.

                LoadSavedAutomaticRows(
                    client.Id,
                    result.FinancialYear,
                    result.Issues);
            }
            else if (_isPageActive)
            {
                ReplaceResultRows(
                    result.Records,
                    result.Issues);
            }

            if (!reimportMonth.HasValue)
            {
                if (result.Summary.TotalOutputRows > 0)
                {
                    RecordDashboardActivity(result);
                }
                else
                {
                    WarningText = AppendWarning(
                        WarningText,
                        "No Purchase, RCM or supplier Credit Note rows were produced, so the Dashboard was not marked as imported.");
                }
            }

            ProgressPercent = 100;
            StatusText = reimportMonth.HasValue
                ? $"Purchase reimport completed for {reimportMonth.Value:MMMM yyyy}: {result.PersistedRows:N0} automatic row(s) rebuilt; Journal and GSTR-3B books auto-rebuilt and refreshed; all other Purchase months were retained unchanged."
                : $"Automatic import completed: {result.PersistedRows:N0} saved rows, " +
                  $"{result.Summary.PurchaseRows:N0} Purchase, " +
                  $"{result.Summary.RcmRows:N0} RCM, " +
                  $"{result.Summary.DebitNoteRows:N0} Debit Note; " +
                  $"{result.Summary.DebitNoteReverseRows:N0} Debit Note Reverse.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Automatic Purchase import cancelled.";
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            StatusText = "Automatic Purchase import failed.";
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
        if (!CanImportPurchase)
        {
            EditStatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "reimport Purchase", accountingMutation: true);
            return;
        }

        _cancellationSource?.Cancel();

        var generation = Interlocked.Increment(
            ref _automaticImportGeneration);

        StatusText = "Reimporting Purchase from verified GSTR-2A / GSTR-2B sources...";

        await RunAutoImportAsync(
            promptForRoot: true,
            generation,
            forceReimport: true);
    }

    public async Task ReimportAutomaticSourcesForMonthAsync(int month)
    {
        if (!CanReimportPurchase)
        {
            EditStatusText = CanImportPurchase
                ? "Select an active client and wait for the current Purchase operation to finish."
                : DesktopPermissionService.DeniedMessage(_governanceRepository, "reimport Purchase", accountingMutation: true);
            return;
        }

        if (month is < 1 or > 12 ||
            !TryFinancialYearRange(FinancialYear, out var fyStart, out _))
        {
            EditStatusText = "Select a valid Financial Year before choosing a Purchase reimport month.";
            return;
        }

        var year = month >= 4
            ? fyStart.Year
            : fyStart.Year + 1;
        var selectedMonth = new DateTime(year, month, 1);

        _cancellationSource?.Cancel();

        var generation = Interlocked.Increment(
            ref _automaticImportGeneration);

        StatusText = $"Reimporting Purchase for {selectedMonth:MMMM yyyy} from verified GSTR-2A / GSTR-2B sources...";

        await RunAutoImportAsync(
            promptForRoot: true,
            generation,
            forceReimport: true,
            reimportMonth: selectedMonth);
    }

    private static bool HasSourceCoverageForMonth(
        PurchaseFormVerification verification,
        DateTime month)
    {
        if (!string.IsNullOrWhiteSpace(verification.AnnualSource))
        {
            return true;
        }

        var monthKey = month.ToString("yyyyMM", CultureInfo.InvariantCulture);
        if (verification.MonthlyMonthKeys.Any(key =>
                string.Equals(
                    new string((key ?? string.Empty).Where(char.IsDigit).ToArray()),
                    monthKey,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Windows Downloads fallback does not expose reliable month-coverage metadata.
        // Month replacement therefore fails closed there; the normal Reimport Purchase
        // button remains available for a complete verified-FY rebuild.
        return false;
    }

    private static bool UsesAnnualSourceForMonth(
        PurchaseFormVerification verification,
        DateTime month)
    {
        if (string.IsNullOrWhiteSpace(verification.AnnualSource))
        {
            return false;
        }

        var monthKey = month.ToString("yyyyMM", CultureInfo.InvariantCulture);
        return !verification.MonthlyMonthKeys.Any(key =>
            string.Equals(
                new string((key ?? string.Empty).Where(char.IsDigit).ToArray()),
                monthKey,
                StringComparison.OrdinalIgnoreCase));
    }

    private void ManualImportPurchaseSheet()
    {
        if (!CanImportPurchase)
        {
            EditStatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "import Purchase", accountingMutation: true);
            return;
        }

        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Select Purchase Register / Purchase Sheet",
            Filter = "Purchase sheets (*.xlsx;*.xlsm;*.csv;*.txt;*.tsv)|*.xlsx;*.xlsm;*.csv;*.txt;*.tsv|Excel workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|Delimited text (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|All files (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            IsBusy = true;
            ErrorText = string.Empty;
            WarningText = string.Empty;
            EditStatusText = string.Empty;
            StatusText = "Inspecting the Purchase Register and applying the shared saved-format profile...";
            NotifyStatusVisibility();

            // Fix114: one saved Purchase profile is shared by Manual Import and Auto Import.
            // Start with the current workbook's best signature, then (only when needed) try
            // the small set of saved Purchase layouts for this Client/FY. The fallback is
            // important for hidden-sheet templates: a saved B2B sheet/header remains usable
            // even when another visible sheet becomes the automatic inspection candidate.
            var firstInspection = _manualImportService.Inspect(dialog.FileName);
            var (savedProfile, inspection) = ResolveSavedPurchaseProfile(
                client.Id,
                FinancialYear,
                dialog.FileName,
                firstInspection);

            IReadOnlyDictionary<string, string> mapping = inspection.SuggestedMapping;
            string gstTreatment = savedProfile?.GstTreatment ?? PurchaseTreatmentPolicy.NotShownInTwoB;
            string? selectedSheetName = savedProfile?.SheetName;
            int? selectedHeaderRow = savedProfile?.HeaderRow;
            int? selectedDataStartRow = savedProfile?.DataStartRow;

            if (!inspection.HeaderDetected || !inspection.IsComplete)
            {
                StatusText = "This Purchase workbook format is not yet mapped. Map it once; the same profile will be used by Manual Import and Auto Import.";

                // Never feed legacy Purchase.ManualSheetMapping preferences into this window.
                // Fix114 starts from the exact source header row and saves one clean profile.
                var mappingWindow = new PurchaseHeaderMappingWindow(
                    _manualImportService,
                    dialog.FileName,
                    firstInspection,
                    gstTreatment)
                {
                    Owner = System.Windows.Application.Current?.MainWindow
                };
                if (mappingWindow.ShowDialog() != true)
                {
                    StatusText = "Manual Purchase sheet import cancelled before any saved Purchase data was changed.";
                    return;
                }

                mapping = mappingWindow.Mapping;
                selectedSheetName = mappingWindow.SelectedSheetName;
                selectedHeaderRow = mappingWindow.HeaderRow;
                selectedDataStartRow = mappingWindow.DataStartRow;
                gstTreatment = mappingWindow.GstTreatment;

                if (PreferenceEnabled(ApplicationSettingKeys.PurchaseSaveConfirmedMappings))
                {
                    _purchaseMappingRepository.Save(client.Id, FinancialYear, new PurchaseMappingProfile
                    {
                        HeaderSignature = mappingWindow.HeaderSignature,
                        SheetName = mappingWindow.SelectedSheetName,
                        HeaderRow = mappingWindow.HeaderRow,
                        DataStartRow = mappingWindow.DataStartRow,
                        GstTreatment = gstTreatment,
                        Mapping = mapping
                    });
                }
            }
            else if (savedProfile is not null)
            {
                // Same behavior as Sales: a known complete format does not reopen mapping.
                mapping = inspection.SuggestedMapping;
                selectedSheetName = savedProfile.SheetName;
                selectedHeaderRow = savedProfile.HeaderRow;
                selectedDataStartRow = savedProfile.DataStartRow;
            }

            var replaceChoice = MessageBox.Show(
                "Replace existing Manual Purchase rows?\n\nYes = Replace Manual Purchase rows\nNo = Append new non-duplicate rows\nCancel = Do not import",
                "Manual Purchase Sheet Import",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (replaceChoice == MessageBoxResult.Cancel)
            {
                StatusText = "Manual Purchase sheet import cancelled.";
                return;
            }

            var result = _manualImportService.Import(
                dialog.FileName,
                client.Id,
                ClientGstin,
                CompanyName,
                FinancialYear,
                mapping,
                selectedSheetName,
                selectedHeaderRow,
                selectedDataStartRow,
                gstTreatment);

            var stockSummary = _stockNormalizationService?.Normalize(
                client.Id,
                FinancialYear,
                CompanyName,
                result.Records);

            var inserted = _purchaseRecordRepository.SaveManualImport(
                client.Id,
                FinancialYear,
                result,
                replaceChoice == MessageBoxResult.Yes);

            _ledgerTotalService?.Invalidate(client.Id, client.FinancialYear);
            PublishCommittedImport(client.Id, FinancialYear, "Purchase", inserted, dialog.FileName, "Manual Import");
            if (stockSummary is not null && stockSummary.InventoryRows > 0)
                PublishCommittedImport(client.Id, FinancialYear, "Stock", stockSummary.InventoryRows, dialog.FileName, "Manual Import");

            LoadSavedAutomaticRows(client.Id, FinancialYear, result.Issues);
            RecordManualPurchaseDashboardActivity(client.Id, result, inserted);
            _ = RefreshAccountingAfterManualEditAsync(client);

            var stockText = stockSummary is null
                ? string.Empty
                : $", {stockSummary.InventoryRows:N0} inventory row(s), {stockSummary.ActionsRequired:N0} stock review item(s)";
            StatusText =
                $"Manual Purchase sheet import completed: {inserted:N0} new row(s) saved" + stockText +
                $"; {result.Issues.Count:N0} issue(s). Existing automatic GSTR-2A/2B rows were preserved.";
            ProgressPercent = 100;
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            StatusText = "Manual Purchase sheet import failed.";
        }
        finally
        {
            IsBusy = false;
            NotifyStatusVisibility();
            RefreshCommands();
        }
    }

    private void RemapPurchaseSheet()
    {
        if (!CanImportPurchase)
        {
            EditStatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "remap Purchase", accountingMutation: true);
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Select Purchase Register to Remap",
            Filter = "Purchase sheets (*.xlsx;*.xlsm;*.csv;*.txt;*.tsv)|*.xlsx;*.xlsm;*.csv;*.txt;*.tsv|Excel workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|Delimited text (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|All files (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            IsBusy = true;
            ErrorText = string.Empty;
            StatusText = "Opening the saved Purchase mapping profile for remap...";
            var firstInspection = _manualImportService.Inspect(dialog.FileName);
            var (profile, inspection) = ResolveSavedPurchaseProfile(client.Id, FinancialYear, dialog.FileName, firstInspection);
            var initial = profile is null ? firstInspection : inspection;
            var window = new PurchaseHeaderMappingWindow(
                _manualImportService,
                dialog.FileName,
                initial,
                profile?.GstTreatment ?? PurchaseTreatmentPolicy.NotShownInTwoB,
                preserveInitialMapping: profile is not null)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (window.ShowDialog() != true)
            {
                StatusText = "Purchase remap cancelled; the saved profile was not changed.";
                return;
            }

            // Fix116 R1: Remap is an immediate source reimport, matching the user's
            // expectation that corrected headers take effect now rather than only on a
            // future Manual/Auto Import. Parse first so a bad remap cannot delete the
            // previously imported rows.
            var result = _manualImportService.Import(
                dialog.FileName,
                client.Id,
                ClientGstin,
                CompanyName,
                FinancialYear,
                window.Mapping,
                window.SelectedSheetName,
                window.HeaderRow,
                window.DataStartRow,
                window.GstTreatment);

            _purchaseMappingRepository.Save(client.Id, FinancialYear, new PurchaseMappingProfile
            {
                HeaderSignature = window.HeaderSignature,
                SheetName = window.SelectedSheetName,
                HeaderRow = window.HeaderRow,
                DataStartRow = window.DataStartRow,
                GstTreatment = window.GstTreatment,
                Mapping = window.Mapping
            });

            var stockSummary = _stockNormalizationService?.Normalize(
                client.Id,
                FinancialYear,
                CompanyName,
                result.Records);
            var inserted = _purchaseRecordRepository.SaveManualImport(
                client.Id,
                FinancialYear,
                result,
                replaceExisting: false,
                replaceSourcePath: result.SourcePath);

            _ledgerTotalService?.Invalidate(client.Id, client.FinancialYear);
            PublishCommittedImport(client.Id, FinancialYear, "Purchase", inserted, dialog.FileName, "Manual Remap");
            if (stockSummary is not null && stockSummary.InventoryRows > 0)
                PublishCommittedImport(client.Id, FinancialYear, "Stock", stockSummary.InventoryRows, dialog.FileName, "Manual Remap");

            LoadSavedAutomaticRows(client.Id, FinancialYear, result.Issues);
            RecordManualPurchaseDashboardActivity(client.Id, result, inserted);
            _ = RefreshAccountingAfterManualEditAsync(client);

            var stockText = stockSummary is null
                ? string.Empty
                : $", {stockSummary.InventoryRows:N0} inventory row(s), {stockSummary.ActionsRequired:N0} stock review item(s)";
            StatusText =
                $"Purchase remap + source reimport completed for '{Path.GetFileName(dialog.FileName)}': {inserted:N0} row(s) saved" +
                stockText + $"; {result.Issues.Count:N0} issue(s). Other Manual Purchase sources and automatic GSTR-2A/2B rows were preserved.";
            ProgressPercent = 100;
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            StatusText = "Purchase remap failed.";
        }
        finally
        {
            IsBusy = false;
            NotifyStatusVisibility();
            RefreshCommands();
        }
    }

    private (PurchaseMappingProfile? Profile, PurchaseManualInspection Inspection) ResolveSavedPurchaseProfile(
        long clientId,
        string financialYear,
        string sourcePath,
        PurchaseManualInspection firstInspection)
    {
        // Manual Import mirrors Sales: always honor a saved profile for the current
        // Client/FY. The "Use saved mappings automatically" setting controls background
        // Auto Import, not whether Manual Import forgets an already-confirmed mapping.
        var profiles = new List<PurchaseMappingProfile>();
        var exact = _purchaseMappingRepository.Get(clientId, financialYear, firstInspection.HeaderSignature);
        if (exact is not null) profiles.Add(exact);
        foreach (var profile in _purchaseMappingRepository.GetAll(clientId, financialYear))
        {
            if (profiles.Any(existing => existing.HeaderSignature.Equals(profile.HeaderSignature, StringComparison.OrdinalIgnoreCase)))
                continue;
            profiles.Add(profile);
        }

        foreach (var profile in profiles)
        {
            try
            {
                var profiled = _manualImportService.Inspect(
                    sourcePath,
                    profile.Mapping,
                    profile.SheetName,
                    profile.HeaderRow,
                    profile.DataStartRow);
                if (!profiled.IsComplete ||
                    !profiled.HeaderSignature.Equals(profile.HeaderSignature, StringComparison.OrdinalIgnoreCase))
                    continue;
                return (profile, profiled);
            }
            catch
            {
                // A profile for another workbook format may reference a sheet that does not
                // exist here. Keep it for its own format and continue looking for a match.
            }
        }

        return (null, firstInspection);
    }

    private bool PreferenceEnabled(string key)
    {
        var value = _applicationPreferenceRepository.Get(key);
        return !value.Equals("False", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("0", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("No", StringComparison.OrdinalIgnoreCase);
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

        var area = module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.Purchase
            : module.Equals("Stock", StringComparison.OrdinalIgnoreCase) ? AccountingLiveArea.StockMasters
            : (AccountingLiveArea?)null;
        if (area.HasValue)
            _liveChangeHub?.Publish(clientId, financialYear, area.Value, $"{importMethod} committed {module}");
    }

    private void RecordManualPurchaseDashboardActivity(long clientId, PurchaseManualImportResult result, int inserted)
    {
        if (inserted <= 0) return;
        var dates = result.Records.Where(row => row.DocumentDate.HasValue).Select(row => row.DocumentDate!.Value.Date).ToArray();
        var periodFrom = dates.Length == 0 ? (DateTime?)null : dates.Min();
        var periodTo = dates.Length == 0 ? (DateTime?)null : dates.Max();
        _activityRepository.Record(new ImportActivity
        {
            ClientId = clientId,
            ImportType = "Purchase",
            PeriodFrom = periodFrom,
            PeriodTo = periodTo,
            PeriodLabel = BuildPeriodLabel(periodFrom, periodTo, FinancialYear),
            FilingStatus = "Manual Purchase Register",
            RecordCount = inserted,
            BankCount = 0,
            SourceSummary = Path.GetFileName(result.SourcePath),
            Status = "Manual Imported",
            Details = $"Purchase sheet manual import; {result.PurchaseRows:N0} Purchase, {result.RcmRows:N0} RCM, {result.DebitNoteRows:N0} Debit Note row(s) parsed; header signature {result.HeaderSignature}.",
            ImportedUtc = DateTime.UtcNow
        });
        _activityContext.NotifyChanged();
    }

    private async Task RunImportAsync()
    {
        if (!CanImportPurchase)
        {
            ErrorText = DesktopPermissionService.DeniedMessage(_governanceRepository, "import Purchase", accountingMutation: true);
            NotifyStatusVisibility();
            return;
        }

        IsBusy = true;
        ErrorText = string.Empty;
        WarningText = string.Empty;
        ProgressPercent = 0;
        NotifyStatusVisibility();

        using var cancellationSource =
            new CancellationTokenSource();

        _cancellationSource =
            cancellationSource;

        try
        {
            var progress =
                new Progress<PurchaseImportProgress>(
                    update =>
                    {
                        ProgressPercent =
                            update.Percent;

                        StatusText =
                            update.Stage;
                    });

            var result = await _service.ImportAsync(
                new PurchaseImportRequest
                {
                    Gstr2AFilePath =
                        Gstr2AFilePath,
                    Gstr2BFilePath =
                        string.IsNullOrWhiteSpace(
                            Gstr2BFilePath)
                            ? null
                            : Gstr2BFilePath,
                    CompanyName =
                        string.IsNullOrWhiteSpace(
                            CompanyName)
                            ? null
                            : CompanyName,
                    ClientGstin =
                        string.IsNullOrWhiteSpace(
                            ClientGstin)
                            ? null
                            : ClientGstin
                },
                progress,
                cancellationSource.Token);

            CompanyName =
                result.CompanyName;

            ClientGstin =
                result.ClientGstin;

            FinancialYear =
                result.FinancialYear;

            Summary =
                result.Summary;

            ReplaceResultRows(
                result.Records,
                result.Issues);

            WarningText = string.Join(
                Environment.NewLine,
                result.Warnings);

            RecordDashboardActivity(
                result);

            StatusText =
                $"Completed: {result.Summary.TotalOutputRows:N0} preview rows, " +
                $"{result.Summary.MatchedRows:N0} matched, " +
                $"{result.Summary.TwoAOnlyRows:N0} 2A only, " +
                $"{result.Summary.TwoBOnlyRows:N0} 2B only, " +
                $"{result.Summary.DifferenceRows:N0} differences.";
        }
        catch (OperationCanceledException)
        {
            StatusText =
                "Purchase import cancelled.";
        }
        catch (Exception exception)
        {
            ErrorText =
                exception.Message;

            StatusText =
                "Purchase import failed.";
        }
        finally
        {
            IsBusy = false;
            _cancellationSource = null;
            NotifyStatusVisibility();
        }
    }

    private void Cancel() =>
        _cancellationSource?.Cancel();

    private void Clear()
    {
        ResetAutomaticSaveQueue();
        Gstr2AFilePath = string.Empty;
        Gstr2BFilePath = string.Empty;
        SourceVerificationText = string.Empty;
        SaveSourceFiles = false;
        EditStatusText = string.Empty;
        ApplyClientContext();
        StatusText =
            "Select GSTR-2A and optional GSTR-2B workbooks.";
        WarningText = string.Empty;
        ErrorText = string.Empty;
        ProgressPercent = 0;
        Summary = new PurchaseImportSummary();

        PurchaseRows.Clear();
        RcmRows.Clear();
        DebitNoteRows.Clear();
        DebitNoteReverseRows.Clear();
        AllRows.Clear();
        ReconciliationRows.Clear();
        Issues.Clear();

        NotifyStatusVisibility();
    }

    private void ClearPurchaseModule()
    {
        var client = _clientContext.CurrentClient;
        if (client is null || !CanDeletePurchase) return;

        var answer = MessageBox.Show(
            $"Clear all Purchase module transactions for {client.ClientName} / FY {client.FinancialYear}?\n\n" +
            "This removes Purchase/RCM/Debit Note records and their import batches from AAH only. " +
            "Stock Masters, Unit/Item mappings, global/built-in mappings, Automation Folder links and Tally vouchers are preserved.",
            "Clear Purchase Module",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        ResetAutomaticSaveQueue();
        var removed = _purchaseRecordRepository.ClearModule(client.Id, client.FinancialYear);
        PurchaseRows.Clear();
        RcmRows.Clear();
        DebitNoteRows.Clear();
        DebitNoteReverseRows.Clear();
        AllRows.Clear();
        ReconciliationRows.Clear();
        Issues.Clear();
        Summary = new PurchaseImportSummary();
        _ledgerTotalService?.Invalidate(client.Id, client.FinancialYear);
        _liveChangeHub?.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Purchase, "Purchase module cleared");
        _activityContext.NotifyChanged();
        EditStatusText = $"Cleared {removed:N0} Purchase module row(s). Masters, mappings and Tally vouchers were preserved.";
        StatusText = "Purchase module cleared for the selected Client + FY.";
        _ = RefreshAccountingAfterManualEditAsync(client);
        RefreshCommands();
    }

    private bool TryGetStockServices(out IStockMasterRepository stockRepository, out PurchaseStockNormalizationService normalization)
    {
        stockRepository = _stockRepository!;
        normalization = _stockNormalizationService!;
        if (_stockRepository is not null && _stockNormalizationService is not null) return true;
        EditStatusText = "Purchase Stock/Unit services are not initialized.";
        return false;
    }

    private void OpenItemNameMapping()
    {
        var client = _clientContext.CurrentClient;
        if (client is null || !TryGetStockServices(out var stockRepository, out _)) return;
        var clientId = client.Id;
        var fy = FinancialYear;
        var company = CompanyName;
        // Capture the actual persisted revisions, not an old presentation cache.
        // The transaction refuses to overwrite any import/edit made after this read.
        var snapshots = _purchaseRecordRepository.GetStockProjectionSnapshots(clientId, fy).ToArray();
        if (snapshots.Any(snapshot => snapshot.Record is null))
        {
            EditStatusText = "A saved Purchase row is unreadable. Repair the source before changing Stock Item mappings.";
            return;
        }
        var savedRows = snapshots.Select(snapshot => snapshot.Record!).ToArray();
        var revisions = snapshots.ToDictionary(snapshot => snapshot.Record!.PersistentId,
            snapshot => new StockItemMappingSourceRevision(snapshot.OriginalRecordJson,
                snapshot.OriginalUpdatedUtc, snapshot.OriginalBatchId, snapshot.OriginalIsUserEdited));
        var stockItems = stockRepository.GetStockItems(clientId, fy).ToArray();
        var aliases = stockRepository.GetItemAliases(clientId, fy).ToArray();
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
            var result = await Task.Run(() => StockItemMappingCommitService.ApplyPurchase(
                clientId, fy, company, selections));
            var warnings = new List<string>();
            try
            {
                if (_clientContext.CurrentClient?.Id == clientId && FinancialYear == fy)
                {
                    LoadSavedAutomaticRows(clientId, fy, []);
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
                _liveChangeHub?.Publish(clientId, fy, AccountingLiveArea.StockMasters, "Purchase Stock mapping committed and verified");
                _liveChangeHub?.Publish(clientId, fy, AccountingLiveArea.Purchase, "Purchase Stock mapping committed and verified");
            }
            catch (Exception exception)
            {
                warnings.Add("Background refresh notification: " + exception.Message);
            }
            if (_clientContext.CurrentClient?.Id == clientId && FinancialYear == fy)
                _ = RefreshAccountingAfterManualEditAsync(client);
            if (warnings.Count > 0)
                result = result with { Warning = result.Warning + " " + string.Join(" ", warnings) };
            EditStatusText = result.Message;
            return result;
        };
        window.ShowDialog();
    }

    private StockItemRecord EnsureAssignedPurchaseStockItem(
        long clientId,
        string requestedName,
        string currentStockItemName,
        bool sourceOwnsCurrentMaster,
        IReadOnlyCollection<PurchaseImportRecord> targets,
        IReadOnlyCollection<StockItemRecord> existingItems,
        IStockMasterRepository stockRepository)
    {
        var name = requestedName.Trim();
        var existing = existingItems.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        // Fix123: a NEW Final Stock Item renames the current Master in place only when
        // this imported source exclusively owns that Master. Shared Masters are split
        // safely by creating a new target so unrelated imported aliases are untouched.
        var currentMaster = existingItems.FirstOrDefault(item =>
            item.Name.Equals(currentStockItemName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (sourceOwnsCurrentMaster && currentMaster is not null)
        {
            currentMaster.Name = name;
            currentMaster.Source = "Manual Purchase Item Rename";
            currentMaster.IsUserEdited = true;
            stockRepository.SaveStockItem(currentMaster);
            // Keep the just-saved durable Stock Item instead of using a fragile First()
            // readback while background Stock authority can refresh concurrently.
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
        var saved = stockRepository.UpsertStockItemAndGet(clientId, FinancialYear,
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
                Source = "Manual Purchase Item Name Assignment",
                IsUserEdited = true
            }, markUserEdited: true);
        return saved;
    }

    private static bool HasInventoryEvidenceForItemName(PurchaseImportRecord row) =>
        row.ItemName.Trim().Length > 0 || row.Unit.Trim().Length > 0 ||
        Math.Abs(row.ActualQuantity) > 0.000001m || Math.Abs(row.BilledQuantity) > 0.000001m ||
        Math.Abs(row.ItemRate) > 0.000001m || Math.Abs(row.ItemAmount) > 0.005m;

    private void OpenUnitMapping()
    {
        var client = _clientContext.CurrentClient;
        if (client is null || !TryGetStockServices(out var stockRepository, out var normalization)) return;
        var stockItems = stockRepository.GetStockItems(client.Id, FinancialYear).ToArray();
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

                stockRepository.ChangeStockItemInventoryUnit(
                    client.Id, FinancialYear, mapping.StockItemName, finalUnit, mapping.SourceValue,
                    deterministicFactor, "Manual Purchase Unit Mapping");
                normalization.SaveUnitMapping(client.Id, FinancialYear, mapping.StockItemName, mapping.SourceValue, finalUnit);
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
        ReapplyPurchaseStockRulesAndSave(client, normalization,
            $"Purchase Unit Mapping Apply All: {mappingsApplied:N0} common Stock Unit mapping(s), {applied:N0} Purchase/Debit Note row(s) re-evaluated live. Sales uses the same Stock Item Unit. Any real Unit difference requires Quantity Conversion confirmation.");
    }

    private void OpenQuantityConversion()
    {
        var client = _clientContext.CurrentClient;
        if (client is null || !TryGetStockServices(out var stockRepository, out var normalization)) return;
        var stockItems = stockRepository.GetStockItems(client.Id, FinancialYear).ToArray();
        var units = AahUnitCatalog.CanonicalUnitCodes;
        var window = new SalesStockBulkReviewWindow(SalesStockToolsWindow.QuantityConversionMode, AllRows, stockItems, units)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (window.Rows.Count == 0)
        {
            EditStatusText = "No Quantity Conversion is required. Every imported Purchase/Debit Note Unit already matches its Stock Master Inventory/Transaction Unit.";
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
                stockRepository.ChangeStockItemInventoryUnit(
                    client.Id, FinancialYear, mapping.StockItemName, finalUnit, mapping.SourceValue, mapping.Factor,
                    "Manual Purchase Quantity Conversion");

                foreach (var row in AllRows.Where(row => row.ItemName.Equals(mapping.StockItemName, StringComparison.OrdinalIgnoreCase)))
                {
                    var imported = string.IsNullOrWhiteSpace(row.OriginalUnit) ? row.Unit : row.OriginalUnit;
                    if (!imported.Equals(mapping.SourceValue, StringComparison.OrdinalIgnoreCase)) continue;
                    normalization.ApplyAndRememberConversion(client.Id, FinancialYear, row, finalUnit, mapping.Factor, true);
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
        _purchaseRecordRepository.UpdateStockIdentityReferences(client.Id, AllRows);
        LoadSavedAutomaticRows(client.Id, FinancialYear, []);
        EditStatusText = $"Purchase Quantity Conversion Apply All: {conversions:N0} common Stock Unit change(s), {affected:N0} Purchase/Debit Note row(s) recalculated from original values. Sales uses the same Stock Item Unit and is reprojected live.";
        _activityContext.NotifyChanged();
        _liveChangeHub?.Publish(client.Id, FinancialYear, AccountingLiveArea.StockMasters, "Purchase quantity conversion changed Stock rules");
        _ = RefreshAccountingAfterManualEditAsync(client);
    }

    private void ReapplyPurchaseStockRulesAndSave(
        AccountingAutomationHub.Domain.Clients.ClientProfile client,
        PurchaseStockNormalizationService normalization,
        string successMessage)
    {
        normalization.Normalize(client.Id, FinancialYear, CompanyName, AllRows);
        _purchaseRecordRepository.UpdateRecords(client.Id, AllRows);
        LoadSavedAutomaticRows(client.Id, FinancialYear, []);
        EditStatusText = successMessage;
        _activityContext.NotifyChanged();
        _liveChangeHub?.Publish(client.Id, FinancialYear, AccountingLiveArea.StockMasters, "Purchase Stock mapping Apply All");
        _ = RefreshAccountingAfterManualEditAsync(client);
    }

    private void RecordDashboardActivity(
        PurchaseImportResult result)
    {
        var client =
            _clientContext.CurrentClient;

        if (client is null)
        {
            WarningText =
                AppendWarning(
                    WarningText,
                    "No active client is selected. The Purchase result was not added to the Dashboard.");
            return;
        }

        var dates =
            result.Records
                .Where(
                    record =>
                        record.GstDate.HasValue)
                .Select(
                    record =>
                        record.GstDate!.Value.Date)
                .ToArray();

        DateTime? periodFrom =
            dates.Length == 0
                ? null
                : dates.Min();

        DateTime? periodTo =
            dates.Length == 0
                ? null
                : dates.Max();

        var periodLabel =
            BuildPeriodLabel(
                periodFrom,
                periodTo,
                client.FinancialYear);

        var sourceFiles =
            new[]
            {
                Path.GetFileName(
                    Gstr2AFilePath),
                string.IsNullOrWhiteSpace(
                    Gstr2BFilePath)
                    ? string.Empty
                    : Path.GetFileName(
                        Gstr2BFilePath)
            }
            .Where(
                value =>
                    !string.IsNullOrWhiteSpace(
                        value));

        _activityRepository.Record(
            new ImportActivity
            {
                ClientId =
                    client.Id,
                ImportType =
                    "Purchase",
                PeriodFrom =
                    periodFrom,
                PeriodTo =
                    periodTo,
                PeriodLabel =
                    periodLabel,
                FilingStatus =
                    "GST3B status not checked",
                RecordCount =
                    result.Summary.TotalOutputRows,
                BankCount =
                    0,
                SourceSummary =
                    string.Join(
                        "; ",
                        sourceFiles),
                Status =
                    "Imported",
                Details =
                    $"{result.Summary.MatchedRows:N0} matched, " +
                    $"{result.Summary.TwoAOnlyRows:N0} 2A only, " +
                    $"{result.Summary.TwoBOnlyRows:N0} 2B only, " +
                    $"{result.Summary.DifferenceRows:N0} differences.",
                ImportedUtc =
                    DateTime.UtcNow
            });

        _activityContext.NotifyChanged();
    }

    private void RecordDashboardActivity(
        PurchaseAutoImportResult result)
    {
        var client = _clientContext.CurrentClient;

        if (client is null)
        {
            return;
        }

        var dates = result.Records
            .Where(record =>
                record.Destination != "Reconciliation Only" &&
                record.GstDate.HasValue)
            .Select(record =>
                record.GstDate!.Value.Date)
            .ToArray();

        DateTime? periodFrom = dates.Length == 0
            ? null
            : dates.Min();

        DateTime? periodTo = dates.Length == 0
            ? null
            : dates.Max();

        var filingStatus =
            $"2A: {result.Verification.TwoA.Status}; " +
            $"2B: {result.Verification.TwoB.Status}";

        _activityRepository.Record(
            new ImportActivity
            {
                ClientId = client.Id,
                ImportType = "Purchase",
                PeriodFrom = periodFrom,
                PeriodTo = periodTo,
                PeriodLabel = BuildPeriodLabel(
                    periodFrom,
                    periodTo,
                    result.FinancialYear),
                FilingStatus = filingStatus,
                RecordCount = result.Summary.TotalOutputRows,
                BankCount = 0,
                SourceSummary = string.Join(
                    "; ",
                    result.SourceManifest
                        .Select(source => source.SourceFile)
                        .Distinct(StringComparer.OrdinalIgnoreCase)),
                Status = "Auto Imported",
                Details =
                    $"{result.Summary.MatchedRows:N0} matched, " +
                    $"{result.Summary.TwoAOnlyRows:N0} 2A only, " +
                    $"{result.Summary.TwoBOnlyRows:N0} 2B only, " +
                    $"{result.Summary.DifferenceRows:N0} differences.",
                ImportedUtc = DateTime.UtcNow
            });

        _activityContext.NotifyChanged();
    }

    private static string BuildSourceVerificationText(
        PurchaseSourceVerification verification)
    {
        var blocked = verification.Blocked
            ? "BLOCKED"
            : "READY";

        return
            $"Source verification: {blocked} | " +
            $"2A {verification.TwoA.SourceBranch} - {verification.TwoA.Status} | " +
            $"2B {verification.TwoB.SourceBranch} - {verification.TwoB.Status}";
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
                $"{periodFrom.Value:MMM yyyy} – {periodTo.Value:MMM yyyy}";
        }

        return (periodFrom ?? periodTo)!.Value.ToString(
            "MMM yyyy",
            CultureInfo.InvariantCulture);
    }

    private static string AppendWarning(
        string existing,
        string warning) =>
        string.IsNullOrWhiteSpace(
            existing)
            ? warning
            : existing +
              Environment.NewLine +
              warning;

    private void OnClientContextPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveClientContext.CurrentClient))
        {
            return;
        }

        ApplyClientContext(loadRows: false);
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

        // Root changes are configuration changes. The background automation owner
        // will process linked files; a hidden Purchase ViewModel must stay passive.
    }

    private void ApplyClientContext(bool loadRows = true)
    {
        ResetAutomaticSaveQueue();

        var client =
            _clientContext.CurrentClient;

        var globalRoot =
            _eReturnsRootContext.RootPath;

        if (string.IsNullOrWhiteSpace(
                globalRoot))
        {
            globalRoot =
                _applicationPreferenceRepository.Get(
                    EReturnsRootPreference);
        }

        if (string.IsNullOrWhiteSpace(globalRoot) &&
            client is not null)
        {
            var legacyClientRoot =
                _clientPreferenceRepository.Get(
                    client.Id,
                    EReturnsRootPreference);

            if (!string.IsNullOrWhiteSpace(legacyClientRoot))
            {
                globalRoot = legacyClientRoot;
                _applicationPreferenceRepository.Set(
                    EReturnsRootPreference,
                    globalRoot);

                _eReturnsRootContext.SetRoot(
                    globalRoot);
            }
        }

        EReturnsRootPath = globalRoot;
        IsImportPanelExpanded =
            string.IsNullOrWhiteSpace(globalRoot);

        if (client is null)
        {
            CompanyName = string.Empty;
            ClientGstin = string.Empty;
            FinancialYear = string.Empty;
            _preparedClientId = 0;
            _preparedFinancialYear = string.Empty;
            ReplaceResultRows([], []);
            RefreshIndirectExpenseLedgerOptions();
            RefreshCommands();
            return;
        }

        CompanyName =
            string.IsNullOrWhiteSpace(client.LegalName)
                ? client.ClientName
                : client.LegalName;

        ClientGstin = client.Gstin;
        FinancialYear = client.FinancialYear;
        EditStatusText = string.Empty;
        if (loadRows) RefreshIndirectExpenseLedgerOptions();
        else ReplaceRows(IndirectExpenseLedgerNames, []);

        if (loadRows)
        {
            var savedRows =
                _purchaseRecordRepository.GetRecords(
                    client.Id,
                    FinancialYear);

            ReplaceResultRows(savedRows, []);
            Summary = savedRows.Count > 0
                ? BuildSummaryFromRecords(savedRows)
                : new PurchaseImportSummary();
            _preparedClientId = client.Id;
            _preparedFinancialYear = FinancialYear;
        }
        else
        {
            _preparedClientId = 0;
            _preparedFinancialYear = string.Empty;
            ReplaceResultRows([], []);
            Summary = new PurchaseImportSummary();
            StatusText = "Purchase is preparing in the background.";
        }

        RefreshCommands();
    }

    public void PrepareForDisplay()
    {
        // Fix132: restore the proven Fix118 passive page-open contract. Opening Purchase
        // only consumes the already prepared Client/FY snapshot; it must never start
        // Stock reprojection, workbook/GSTR-2A/2B parsing or cache invalidation. Automatic
        // GST purchase import and Manual Purchase Register import remain independent.
        _isPageActive = true;
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        if (_preparedClientId != client.Id ||
            !_preparedFinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            StatusText = "Purchase stream is preparing in the background…";
    }

    public void WarmFromPreparedSnapshot(ClientWorkspaceSnapshot snapshot)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != snapshot.ClientId ||
            !client.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase))
            return;

        CompanyName = string.IsNullOrWhiteSpace(client.LegalName) ? client.ClientName : client.LegalName;
        ClientGstin = client.Gstin;
        FinancialYear = client.FinancialYear;
        ReplaceResultRows(snapshot.Purchases, []);
        Summary = snapshot.Purchases.Count > 0
            ? BuildSummaryFromRecords(snapshot.Purchases)
            : new PurchaseImportSummary();
        _preparedClientId = client.Id;
        _preparedFinancialYear = client.FinancialYear;
        StatusText = $"Purchase ready • {snapshot.Purchases.Count:N0} saved row(s).";
        RefreshCommands();
    }

    public void WarmMasterDependenciesFromPreparedSnapshot(ClientWorkspaceSnapshot snapshot)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != snapshot.ClientId ||
            !client.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;
        var allowedGroupKeys = BuildIndirectExpenseGroupKeys(snapshot.Masters);
        var ledgers = snapshot.Masters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !master.IsArchived &&
                             allowedGroupKeys.Contains(TallyMasterIdentity.Key(master.Parent)))
            .Select(master => master.Name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ReplaceRows(IndirectExpenseLedgerNames, ledgers);
    }

    public void ReleaseForNavigation()
    {
        _isPageActive = false;
        // Completion 59: keep the committed Purchase snapshot warm. Navigation must
        // never blank the grid and visibly rebuild it in front of the user. The repository
        // remains authoritative; live-change notifications refresh this cache when needed.
    }

    private void ReleaseLoadedRowsIfSafe()
    {
        // Completion 61 keeps the last prepared Purchase view resident. Hiding a module
        // must not destroy its UI working set and force first-open hydration later.
        if (_isPageActive || IsBusy || _liveEditSaveRunning || _pendingLiveEditIds.Count > 0) return;
    }

    public void RefreshSavedRowsAfterExternalImport()
    {
        // Completion 61: external-import notifications are reconciled by MainWindow's
        // module-scoped cache invalidation. Never fall back to a direct Purchase DB load.
        if (_isPageActive)
            StatusText = "Purchase stream is synchronizing in the background…";
    }

    private void ScheduleAutomaticImport(bool promptForRoot)
    {
        if (!CanImportPurchase)
        {
            if (_clientContext.CurrentClient is not null && _isPageActive)
                StatusText = "Purchase stream is preparing in the background…";
            return;
        }

        _cancellationSource?.Cancel();

        var generation = Interlocked.Increment(
            ref _automaticImportGeneration);

        System.Windows.Application.Current?.Dispatcher.InvokeAsync(
            () =>
            {
                _ = RunAutoImportAsync(
                    promptForRoot,
                    generation);
            },
            DispatcherPriority.ApplicationIdle);
    }

    private void LoadSavedAutomaticRows(
        long clientId,
        string financialYear,
        IEnumerable<PurchaseImportIssue> issues)
    {
        if (!_isPageActive) return;
        var savedRows =
            _purchaseRecordRepository.GetRecords(
                clientId,
                financialYear);

        ReplaceResultRows(savedRows, issues);

        if (savedRows.Count > 0)
        {
            Summary = BuildSummaryFromRecords(savedRows);
        }
    }

    public async void DeleteRows(IReadOnlyCollection<PurchaseImportRecord>? rows)
    {
        if (!CanDeletePurchase)
        {
            EditStatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "delete Purchase rows", accountingMutation: true);
            return;
        }

        var client = _clientContext.CurrentClient;
        if (client is null || rows is null || rows.Count == 0 || IsBusy) return;

        var persisted = rows
            .Where(row => row.PersistentId > 0)
            .GroupBy(row => row.PersistentId)
            .Select(group => group.First())
            .ToArray();
        if (persisted.Length == 0)
        {
            EditStatusText = "Select one or more saved Purchase rows to delete.";
            return;
        }

        var answer = MessageBox.Show(
            $"Delete {persisted.Length:N0} selected Purchase row(s) from AAH?\n\nThis does not delete anything from Tally or the original GSTR source files.",
            "Delete Purchase Rows",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var ids = persisted.Select(row => row.PersistentId).ToArray();
            foreach (var id in ids) _pendingLiveEditIds.Remove(id);
            var deleted = _purchaseRecordRepository.DeleteRecords(client.Id, client.FinancialYear, ids);
            if (deleted <= 0)
            {
                EditStatusText = "No selected Purchase rows were deleted.";
                return;
            }

            _ledgerTotalService?.Invalidate(client.Id, client.FinancialYear);
            if (_refreshCoordinator is not null)
                await _refreshCoordinator.RefreshAsync(client);

            LoadSavedAutomaticRows(client.Id, client.FinancialYear, Issues.ToArray());
            _liveChangeHub?.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Purchase, "Purchase rows deleted");
            RefreshIndirectExpenseLedgerOptions();
            var affectedGstDates = persisted
                .Where(row => row.GstDate.HasValue)
                .Select(row => row.GstDate!.Value.ToString("MMM yyyy", CultureInfo.InvariantCulture))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var periodText = affectedGstDates.Length == 0
                ? "GST Date missing"
                : string.Join(", ", affectedGstDates);
            EditStatusText = $"Deleted {deleted:N0} Purchase row(s); GST-Date period(s) refreshed: {periodText}.";
        }
        catch (Exception exception)
        {
            EditStatusText = $"Purchase row deletion failed: {exception.Message}";
            MessageBox.Show(exception.Message, "Delete Purchase Rows", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void RecalculateTwoBRateLine(PurchaseImportRecord? row)
    {
        if (row is null || !row.IsTwoBOnlyEvidence || !row.RateKnown) return;
        if (row.Rate < 0m || row.Rate > 100m) return;

        var group = AllRows
            .Where(item => item.IsTwoBOnlyEvidence &&
                           BuildTwoBOnlyDocumentIdentity(item).Equals(
                               BuildTwoBOnlyDocumentIdentity(row),
                               StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (group.Length == 0) group = [row];

        var evidence = group.First();
        var useIgst = Math.Abs(evidence.TwoBIgst ?? 0m) > 0.005m ||
                      (Math.Abs(evidence.TwoBCgst ?? 0m) <= 0.005m &&
                       Math.Abs(evidence.TwoBSgst ?? 0m) <= 0.005m &&
                       group.Sum(item => Math.Abs(item.Igst)) >
                       group.Sum(item => Math.Abs(item.Cgst) + Math.Abs(item.Sgst)));

        var totalTax = decimal.Round(
            row.TaxableValue * row.Rate / 100m,
            2,
            MidpointRounding.AwayFromZero);
        if (row.Rate == 0m) totalTax = 0m;

        if (useIgst)
        {
            row.Igst = totalTax;
            row.Cgst = 0m;
            row.Sgst = 0m;
        }
        else
        {
            var half = decimal.Round(totalTax / 2m, 2, MidpointRounding.AwayFromZero);
            row.Igst = 0m;
            row.Cgst = half;
            row.Sgst = totalTax - half;
        }

        // Round-off is invoice-level. Recalculate it only when the residual is a
        // genuine rounding amount; never use round-off to hide an incomplete split.
        foreach (var item in group) item.RoundOff = 0m;
        var invoiceValue = evidence.TwoBInvoiceValue ?? evidence.SourceDocumentValue;
        if (invoiceValue != 0m)
        {
            var calculated = group.Sum(item =>
                item.TaxableValue + item.Igst + item.Cgst + item.Sgst + item.Cess);
            var residual = decimal.Round(invoiceValue - calculated, 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(residual) <= 2m)
                group[0].RoundOff = residual;
        }
    }

    public void AddTwoBRateLine(PurchaseImportRecord? selected)
    {
        if (!CanEditPurchase)
        {
            EditStatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "add a 2B GST rate line", accountingMutation: true);
            return;
        }

        var client = _clientContext.CurrentClient;
        if (client is null || selected is null || !selected.IsTwoBOnlyEvidence || selected.PersistentId <= 0)
        {
            EditStatusText = "Select a saved 2B-only invoice before adding another GST rate line.";
            return;
        }

        try
        {
            var key = BuildTwoBOnlyDocumentIdentity(selected);
            var invoiceRows = AllRows
                .Where(row => row.IsTwoBOnlyEvidence &&
                              BuildTwoBOnlyDocumentIdentity(row).Equals(key, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var clone = JsonSerializer.Deserialize<PurchaseImportRecord>(JsonSerializer.Serialize(selected))
                        ?? throw new InvalidOperationException("Could not prepare the additional GST rate line.");
            clone.PersistentId = 0;
            clone.BatchId = selected.BatchId;
            clone.IsUserEdited = true;
            clone.Destination = "2B Only";
            clone.GstApplicability = PurchaseTreatmentPolicy.NotIncludedInPurchase;
            clone.RateKnown = false;
            clone.Rate = 0m;
            clone.TaxableValue = 0m;
            clone.Igst = 0m;
            clone.Cgst = 0m;
            clone.Sgst = 0m;
            clone.Cess = 0m;
            clone.RoundOff = 0m;
            clone.PurchaseLedger = string.Empty;
            clone.IgstLedger = string.Empty;
            clone.CgstLedger = string.Empty;
            clone.SgstLedger = string.Empty;
            clone.ItemSequence = invoiceRows.Select(row => row.ItemSequence).DefaultIfEmpty(0).Max() + 1;
            clone.DifferenceDetails = string.IsNullOrWhiteSpace(clone.DifferenceDetails)
                ? "Manual additional GST rate line - enter Taxable and Rate %."
                : clone.DifferenceDetails + " | Manual additional GST rate line.";

            var id = _purchaseRecordRepository.InsertManualRateLine(client.Id, client.FinancialYear, clone);
            if (id <= 0)
            {
                EditStatusText = "The additional GST rate line could not be saved.";
                return;
            }

            LoadSavedAutomaticRows(client.Id, client.FinancialYear, Issues.ToArray());
            ApplyReconciliationFilter("2B ONLY");
            EditStatusText = "Added a blank GST rate line. Enter Taxable and Rate %; tax will be calculated automatically.";
        }
        catch (Exception exception)
        {
            EditStatusText = $"Adding GST rate line failed: {exception.Message}";
            MessageBox.Show(exception.Message, "Add GST Rate Line", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async void IncludeTwoBOnlyRows(IReadOnlyCollection<PurchaseImportRecord>? rows)
    {
        await IncludeTwoBOnlyRowsCore(rows, confirm: true);
    }

    public async void SetTwoBInclusionState(PurchaseImportRecord? row, string requestedState)
    {
        if (!CanEditPurchase)
        {
            EditStatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "change 2B-only Purchase inclusion", accountingMutation: true);
            return;
        }

        if (row is null || !row.IsTwoBOnlyEvidence || row.PersistentId <= 0)
            return;

        if (PurchaseTreatmentPolicy.IsTwoBIncluded(requestedState))
        {
            var included = await IncludeTwoBOnlyRowsCore([row], confirm: false);
            if (!included)
            {
                // The selector is optimistic so the user sees the clicked value
                // immediately. If validation/persistence does not complete, restore the
                // authoritative reconciliation state instead of leaving a false Included
                // label on retained evidence.
                var identity = BuildTwoBOnlyDocumentIdentity(row);
                foreach (var item in AllRows.Where(item =>
                             item.IsTwoBOnlyEvidence &&
                             BuildTwoBOnlyDocumentIdentity(item).Equals(identity, StringComparison.OrdinalIgnoreCase) &&
                             item.Destination.Equals("2B Only", StringComparison.OrdinalIgnoreCase)))
                {
                    item.GstApplicability = PurchaseTreatmentPolicy.NotIncludedInPurchase;
                }
            }
            return;
        }

        if (PurchaseTreatmentPolicy.IsTwoBNotIncluded(requestedState))
            await ExcludeTwoBOnlyRowsCore([row]);
    }

    private async Task<bool> IncludeTwoBOnlyRowsCore(
        IReadOnlyCollection<PurchaseImportRecord>? rows,
        bool confirm)
    {
        if (!CanEditPurchase)
        {
            EditStatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "include 2B-only Purchase rows", accountingMutation: true);
            return false;
        }

        var client = _clientContext.CurrentClient;
        if (client is null || rows is null || rows.Count == 0 || IsBusy)
        {
            EditStatusText = "Select one or more retained 2B-only invoices first.";
            return false;
        }

        var selectedKeys = rows
            .Where(row => row.ReconciliationStatus.Equals("2B Only", StringComparison.OrdinalIgnoreCase))
            .Select(BuildTwoBOnlyDocumentIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedKeys.Count == 0)
        {
            EditStatusText = "The selection does not contain a retained 2B-only invoice.";
            return false;
        }

        var promoted = AllRows
            .Where(row => row.PersistentId > 0 && row.IsTwoBOnlyEvidence &&
                          selectedKeys.Contains(BuildTwoBOnlyDocumentIdentity(row)))
            .ToArray();
        if (promoted.Length == 0)
        {
            EditStatusText = "No saved 2B-only rows were available to include.";
            return false;
        }

        var validationErrors = ValidateTwoBInclusion(promoted);
        if (validationErrors.Count > 0)
        {
            var detail = string.Join(Environment.NewLine, validationErrors.Take(12).Select(error => "• " + error));
            if (validationErrors.Count > 12)
                detail += $"{Environment.NewLine}• …and {validationErrors.Count - 12:N0} more issue(s).";
            EditStatusText = "2B-only inclusion blocked until Purchase validation passes.";
            MessageBox.Show(
                "The selected invoice(s) cannot be included yet. Correct every condition below first:\n\n" + detail,
                "2B Only - Purchase Validation",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var invoiceCount = promoted
            .Select(BuildTwoBOnlyDocumentIdentity)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (confirm)
        {
            var answer = MessageBox.Show(
                $"Include {invoiceCount:N0} validated GSTR-2B-only invoice(s) in Purchase accounting?\n\n" +
                "The retained 2B evidence stays in All / Recon. The complete document is promoted to Purchase/RCM/Debit Notes/Debit Note Reverse only after FY, rate, tax arithmetic and duplicate/cross-match validation pass.",
                "Include 2B Only",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return false;
        }

        try
        {
            foreach (var item in promoted)
            {
                item.Destination = ResolveTwoBOnlyAccountingDestination(item);
                item.GstApplicability = item.Destination.Equals("RCM", StringComparison.OrdinalIgnoreCase)
                    ? PurchaseTreatmentPolicy.RcmSameMonth
                    : PurchaseTreatmentPolicy.IncludedInPurchase;
                PurchaseTreatmentPolicy.ApplyAccountingShape(item);
            }

            var updated = _purchaseRecordRepository.UpdateRecords(client.Id, promoted);
            if (updated <= 0)
            {
                EditStatusText = "The selected 2B-only invoices could not be included.";
                return false;
            }

            _ledgerTotalService?.Invalidate(client.Id, client.FinancialYear);
            if (_refreshCoordinator is not null)
                await _refreshCoordinator.RefreshAsync(client);

            LoadSavedAutomaticRows(client.Id, client.FinancialYear, Issues.ToArray());
            _liveChangeHub?.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Purchase, "Validated 2B-only invoice included in Purchase accounting");
            RefreshIndirectExpenseLedgerOptions();
            EditStatusText = $"Included {invoiceCount:N0} validated 2B-only invoice(s) ({updated:N0} row(s)) in Purchase accounting; 2B evidence retained.";
            return true;
        }
        catch (Exception exception)
        {
            EditStatusText = $"Including 2B-only invoices failed: {exception.Message}";
            MessageBox.Show(exception.Message, "Include 2B Only", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async Task<bool> ExcludeTwoBOnlyRowsCore(IReadOnlyCollection<PurchaseImportRecord>? rows)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || rows is null || rows.Count == 0 || IsBusy)
            return false;

        var selectedKeys = rows
            .Where(row => row.IsTwoBOnlyEvidence)
            .Select(BuildTwoBOnlyDocumentIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedKeys.Count == 0) return false;

        var excluded = AllRows
            .Where(row => row.PersistentId > 0 && row.IsTwoBOnlyEvidence &&
                          selectedKeys.Contains(BuildTwoBOnlyDocumentIdentity(row)))
            .ToArray();
        if (excluded.Length == 0) return false;

        try
        {
            foreach (var item in excluded)
            {
                item.Destination = "2B Only";
                item.GstApplicability = PurchaseTreatmentPolicy.NotIncludedInPurchase;
                PurchaseTreatmentPolicy.ApplyAccountingShape(item);
            }

            var updated = _purchaseRecordRepository.UpdateRecords(client.Id, excluded);
            if (updated <= 0)
            {
                EditStatusText = "The selected 2B-only invoice could not be removed from Purchase accounting.";
                return false;
            }

            _ledgerTotalService?.Invalidate(client.Id, client.FinancialYear);
            if (_refreshCoordinator is not null)
                await _refreshCoordinator.RefreshAsync(client);

            LoadSavedAutomaticRows(client.Id, client.FinancialYear, Issues.ToArray());
            _liveChangeHub?.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Purchase, "2B-only invoice returned to reconciliation evidence");
            RefreshIndirectExpenseLedgerOptions();
            EditStatusText = "2B-only invoice marked Not Included in Purchase; reconciliation evidence retained.";
            return true;
        }
        catch (Exception exception)
        {
            EditStatusText = $"Changing 2B-only inclusion failed: {exception.Message}";
            MessageBox.Show(exception.Message, "2B Only - GST Treatment", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private IReadOnlyList<string> ValidateTwoBInclusion(IReadOnlyCollection<PurchaseImportRecord> rows)
    {
        var errors = new List<string>();
        if (!TryFinancialYearRange(FinancialYear, out var fyStart, out var fyEnd))
        {
            errors.Add($"Financial year '{FinancialYear}' is not valid.");
            return errors;
        }

        var supportedRates = new[] { 0m, 0.1m, 0.25m, 1m, 1.5m, 3m, 5m, 6m, 7.5m, 12m, 18m, 28m };
        const decimal taxTolerance = 0.06m;
        const decimal invoiceTolerance = 1.00m;

        foreach (var group in rows.GroupBy(BuildTwoBOnlyDocumentIdentity, StringComparer.OrdinalIgnoreCase))
        {
            var invoiceRows = group.OrderBy(row => row.ItemSequence).ToArray();
            var first = invoiceRows[0];
            var label = $"{first.SupplierGstin} / {first.DocumentNumber}";

            if (string.IsNullOrWhiteSpace(first.DocumentNumber))
                errors.Add($"{label}: Document Number is required.");
            if (first.DocumentDate is null)
                errors.Add($"{label}: Document Date is required.");
            if (first.GstDate is null)
                errors.Add($"{label}: GST Date is required.");
            else if (first.GstDate.Value.Date < fyStart || first.GstDate.Value.Date > fyEnd)
                errors.Add($"{label}: GST Date {first.GstDate.Value:dd-MM-yyyy} is outside FY {FinancialYear}.");

            var gstin = Regex.Replace((first.SupplierGstin ?? string.Empty).ToUpperInvariant(), "[^A-Z0-9]", string.Empty);
            if (!Regex.IsMatch(gstin, "^[0-9]{2}[A-Z0-9]{13}$"))
                errors.Add($"{label}: Supplier GSTIN is not a valid 15-character GSTIN.");

            foreach (var row in invoiceRows)
            {
                if (!row.RateKnown)
                {
                    errors.Add($"{label}: GST Rate % is blank on rate line {row.ItemSequence}. Enter a valid rate first.");
                    continue;
                }
                if (!supportedRates.Any(rate => Math.Abs(rate - row.Rate) <= 0.001m))
                    errors.Add($"{label}: {row.Rate:0.####}% is not a supported GST rate on line {row.ItemSequence}.");
                if (row.TaxableValue < 0m && !first.DocumentType.Contains("NOTE", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{label}: Taxable value cannot be negative for a normal Purchase invoice.");

                var hasIgst = Math.Abs(row.Igst) > 0.005m;
                var hasCgst = Math.Abs(row.Cgst) > 0.005m;
                var hasSgst = Math.Abs(row.Sgst) > 0.005m;
                if (hasIgst && (hasCgst || hasSgst))
                {
                    errors.Add($"{label}: line {row.ItemSequence} mixes IGST with CGST/SGST.");
                    continue;
                }

                var expectedTax = decimal.Round(row.TaxableValue * row.Rate / 100m, 2, MidpointRounding.AwayFromZero);
                if (hasIgst || Math.Abs(first.TwoBIgst ?? 0m) > 0.005m)
                {
                    if (Math.Abs(row.Igst - expectedTax) > taxTolerance || hasCgst || hasSgst)
                        errors.Add($"{label}: IGST does not cross-match Taxable × Rate on line {row.ItemSequence}.");
                }
                else
                {
                    var expectedHalf = decimal.Round(expectedTax / 2m, 2, MidpointRounding.AwayFromZero);
                    if (Math.Abs(row.Cgst - expectedHalf) > taxTolerance ||
                        Math.Abs(row.Sgst - (expectedTax - expectedHalf)) > taxTolerance || hasIgst)
                        errors.Add($"{label}: CGST/SGST does not cross-match Taxable × Rate on line {row.ItemSequence}.");
                }
            }

            var taxable = invoiceRows.Sum(row => row.TaxableValue);
            var igst = invoiceRows.Sum(row => row.Igst);
            var cgst = invoiceRows.Sum(row => row.Cgst);
            var sgst = invoiceRows.Sum(row => row.Sgst);
            var cess = invoiceRows.Sum(row => row.Cess);
            var roundOff = invoiceRows.Sum(row => row.RoundOff);
            var calculatedInvoice = taxable + igst + cgst + sgst + cess + roundOff;

            ValidateTwoBAmount(first.TwoBTaxableValue, taxable, "Taxable", label, taxTolerance, errors);
            ValidateTwoBAmount(first.TwoBIgst, igst, "IGST", label, taxTolerance, errors);
            ValidateTwoBAmount(first.TwoBCgst, cgst, "CGST", label, taxTolerance, errors);
            ValidateTwoBAmount(first.TwoBSgst, sgst, "SGST", label, taxTolerance, errors);
            var invoiceEvidence = first.TwoBInvoiceValue ?? (first.SourceDocumentValue != 0m ? first.SourceDocumentValue : (decimal?)null);
            if (invoiceEvidence.HasValue &&
                Math.Abs(Math.Abs(invoiceEvidence.Value) - Math.Abs(calculatedInvoice)) > invoiceTolerance)
                errors.Add($"{label}: calculated invoice value {calculatedInvoice:N2} does not cross-match 2B value {invoiceEvidence.Value:N2}.");

            var duplicateExists = AllRows.Any(existing =>
                !existing.IsTwoBOnlyEvidence &&
                (existing.Destination.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ||
                 existing.Destination.Equals("RCM", StringComparison.OrdinalIgnoreCase) ||
                 existing.Destination.Equals(PurchaseTreatmentPolicy.DebitNoteDestination, StringComparison.OrdinalIgnoreCase) ||
                 existing.Destination.Equals(PurchaseTreatmentPolicy.DebitNoteReverseDestination, StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(existing.SupplierGstin?.Trim(), first.SupplierGstin?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.DocumentNumber?.Trim(), first.DocumentNumber?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                existing.DocumentDate?.Date == first.DocumentDate?.Date);
            if (duplicateExists)
                errors.Add($"{label}: a Purchase/RCM/Debit Note/Debit Note Reverse row already exists for this supplier, document number and date.");
        }

        return errors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ValidateTwoBAmount(
        decimal? expected,
        decimal actual,
        string field,
        string label,
        decimal tolerance,
        ICollection<string> errors)
    {
        if (!expected.HasValue) return;
        if (Math.Abs(Math.Abs(expected.Value) - Math.Abs(actual)) > tolerance)
            errors.Add($"{label}: {field} total {actual:N2} does not cross-match 2B {expected.Value:N2}.");
    }

    private static string ResolveTwoBOnlyAccountingDestination(PurchaseImportRecord row)
    {
        var reverse = Regex.Replace((row.ReverseCharge ?? string.Empty).ToUpperInvariant(), "[^A-Z0-9]", string.Empty);
        if (reverse is "Y" or "YES" or "TRUE" or "1") return "RCM";

        var type = (row.DocumentType ?? string.Empty).Trim();
        var invoiceType = Regex.Replace((row.InvoiceType ?? string.Empty).ToUpperInvariant(), "[^A-Z0-9]", string.Empty);
        if (invoiceType is "D" or "DN" or "DR" or "DEBIT" or "DEBITNOTE")
            return PurchaseTreatmentPolicy.DebitNoteReverseDestination;

        if (type.Contains("NOTE", StringComparison.OrdinalIgnoreCase) ||
            invoiceType is "C" or "CN" or "CR" or "CDN" or "CREDIT" or "CREDITNOTE")
            return PurchaseTreatmentPolicy.DebitNoteDestination;

        return "Purchase";
    }

    private static string BuildTwoBOnlyDocumentIdentity(PurchaseImportRecord row) =>
        string.Join(
            "\u001f",
            (row.SupplierGstin ?? string.Empty).Trim().ToUpperInvariant(),
            (row.DocumentNumber ?? string.Empty).Trim().ToUpperInvariant(),
            row.DocumentDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty,
            (row.Source2BPath ?? row.Source2BFile ?? string.Empty).Trim().ToUpperInvariant(),
            (row.OriginalDocumentNumber ?? string.Empty).Trim().ToUpperInvariant());

    public void QueueAutomaticSave(PurchaseImportRecord? row)
    {
        if (!CanEditPurchase) return;
        if (row is null || row.PersistentId <= 0)
            return;

        _pendingLiveEditIds.Add(row.PersistentId);
        EditStatusText = "Saving changes automatically…";
        RestartAutomaticSaveTimer();
    }

    private void SaveChanges()
    {
        if (!CanEditPurchase)
        {
            EditStatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "edit Purchase", accountingMutation: true);
            return;
        }
        foreach (var row in AllRows.Where(row => row.PersistentId > 0))
            _pendingLiveEditIds.Add(row.PersistentId);

        if (_pendingLiveEditIds.Count == 0)
        {
            EditStatusText = "No saved automatic rows were available to update.";
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
            : TimeSpan.FromMilliseconds(250);
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
        if (!CanEditPurchase)
        {
            _pendingLiveEditIds.Clear();
            return;
        }
        _liveEditSaveTimer.Interval = TimeSpan.FromMilliseconds(250);

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
            EditStatusText = "Select an active client before editing Purchase rows.";
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
            foreach (var row in rows)
                PurchaseTreatmentPolicy.ApplyAccountingShape(row);

            EnsureSelectedPurchaseLedgerMasters(client, rows);
            NormalizeRowsForSave(client.Id, client.FinancialYear, rows);

            var updated = _purchaseRecordRepository.UpdateRecords(client.Id, rows);

            if (updated > 0)
            {
                _ledgerTotalService?.Invalidate(client.Id, client.FinancialYear);
                if (_refreshCoordinator is not null)
                    await _refreshCoordinator.RefreshAsync(client);

                _liveChangeHub?.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Purchase, "Purchase rows saved");
                RefreshIndirectExpenseLedgerOptions();
                EditStatusText =
                    $"Auto-saved {updated:N0} Purchase/RCM/Debit Note row(s); Journal and GST refreshed.";
            }
            else
            {
                EditStatusText = "No saved automatic Purchase rows were available to update.";
            }
        }
        catch (Exception exception)
        {
            EditStatusText =
                "Automatic Purchase save/refresh needs attention: " +
                exception.Message;
        }
        finally
        {
            _liveEditSaveRunning = false;
            OnPropertyChanged(nameof(HasPersistedRows));
            SaveChangesCommand.RaiseCanExecuteChanged();
        ClearModuleCommand.RaiseCanExecuteChanged();
        ItemNameMappingCommand.RaiseCanExecuteChanged();
        UnitMappingCommand.RaiseCanExecuteChanged();
        QuantityConversionCommand.RaiseCanExecuteChanged();

            if (_pendingLiveEditIds.Count > 0)
                RestartAutomaticSaveTimer();
            else
                ReleaseLoadedRowsIfSafe();
        }
    }

    private void EnsureSelectedPurchaseLedgerMasters(
        AccountingAutomationHub.Domain.Clients.ClientProfile client,
        IEnumerable<PurchaseImportRecord> rows)
    {
        if (_masterRepository is null)
            throw new InvalidOperationException("Purchase parity services are not initialized.");

        var masters = _masterRepository
            .Get(client.Id, client.FinancialYear, includeArchived: false)
            .ToArray();
        var allowedGroupKeys = BuildIndirectExpenseGroupKeys(masters);

        foreach (var row in rows)
        {
            var requested = AccountingText.CleanWhitespace(row.PurchaseLedger);
            if (requested.Length == 0 || PurchaseTreatmentPolicy.IsAutomaticPurchaseLedger(requested))
                continue;

            var key = TallyMasterIdentity.Key(requested);
            var matches = masters
                .Where(master =>
                    master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                    TallyMasterIdentity.Key(master.Name).Equals(key, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Duplicate Ledger Master detected for '{requested}'. Resolve the duplicate in Master Centre before using it as the Purchase / Cost Ledger.");
            }

            if (matches.Length == 1)
            {
                var existing = matches[0];
                row.PurchaseLedger = existing.Name.Trim();

                if (allowedGroupKeys.Contains(TallyMasterIdentity.Key(existing.Parent)))
                    continue;

                // Normal Shown-in-2B Purchase rows may keep an already-existing
                // Ledger in its current Tally/local group. The dropdown itself offers
                // the Indirect Expenses hierarchy, and only a genuinely new custom
                // name is auto-created under Indirect Expenses. GST Included in Cost
                // remains stricter because its posting destination must be an expense.
                if (!row.IsGstIncludedInCost)
                    continue;

                if (existing.Source.Contains("Tally", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"'{existing.Name}' is a Tally/imported Ledger under '{existing.Parent}'. " +
                        "Tally/imported Masters remain read-only in this phase. Select an Indirect Expenses ledger or enter a new Purchase / Cost Ledger name.");
                }

                existing.Parent = "Indirect Expenses";
                existing.Company = CompanyName;
                existing.Source = "Manual Purchase / Cost Ledger";
                existing.IsUserEdited = true;
                _masterRepository.Save(existing);
                continue;
            }

            var displayName = AccountingText.DisplayName(requested);
            _masterRepository.Upsert(
                client.Id,
                client.FinancialYear,
                [new AccountingAutomationHub.Domain.Masters.MasterRecord
                {
                    Name = displayName,
                    Parent = "Indirect Expenses",
                    Type = "Ledger",
                    Company = CompanyName,
                    Source = "Manual Purchase / Cost Ledger",
                    IsUserEdited = true
                }],
                markUserEdited: true);
            row.PurchaseLedger = displayName;

            // Include a newly-created name for subsequent rows in this same save.
            masters = _masterRepository
                .Get(client.Id, client.FinancialYear, includeArchived: false)
                .ToArray();
            allowedGroupKeys = BuildIndirectExpenseGroupKeys(masters);
        }
    }

    private void RefreshIndirectExpenseLedgerOptions()
    {
        if (_masterRepository is null || _clientContext.CurrentClient is not { } client)
        {
            ReplaceRows(IndirectExpenseLedgerNames, []);
            return;
        }

        var masters = _masterRepository
            .Get(client.Id, client.FinancialYear, includeArchived: false)
            .ToArray();
        var allowedGroupKeys = BuildIndirectExpenseGroupKeys(masters);
        var ledgers = masters
            .Where(master =>
                master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                !master.IsArchived &&
                allowedGroupKeys.Contains(TallyMasterIdentity.Key(master.Parent)))
            .Select(master => master.Name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ReplaceRows(IndirectExpenseLedgerNames, ledgers);
    }

    private static HashSet<string> BuildIndirectExpenseGroupKeys(
        IReadOnlyCollection<AccountingAutomationHub.Domain.Masters.MasterRecord> masters)
    {
        var groupRows = masters
            .Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && !master.IsArchived)
            .ToArray();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            TallyMasterIdentity.Key("Indirect Expenses")
        };

        bool changed;
        do
        {
            changed = false;
            foreach (var group in groupRows)
            {
                var groupKey = TallyMasterIdentity.Key(group.Name);
                if (allowed.Contains(groupKey)) continue;
                if (!allowed.Contains(TallyMasterIdentity.Key(group.Parent))) continue;
                if (allowed.Add(groupKey)) changed = true;
            }
        }
        while (changed);

        return allowed;
    }

    private void NormalizeRowsForSave(
        long clientId,
        string financialYear,
        IEnumerable<PurchaseImportRecord> rows)
    {
        var ledgerMasters = BuildLedgerMasterLookup(clientId, financialYear);
        var stockItems = _stockRepository?.GetStockItems(clientId, financialYear)
            .Where(item => !item.IsArchived && !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => AccountingText.DuplicateKey(item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var units = _stockRepository?.GetUnits(clientId, financialYear)
            .Where(unit => !unit.IsArchived && !string.IsNullOrWhiteSpace(unit.Name))
            .GroupBy(unit => AccountingText.DuplicateKey(unit.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            row.SupplierName = ResolveLedgerEntry(row.SupplierName, ledgerMasters, "Supplier");
            row.PurchaseLedger = ResolveLedgerEntry(row.PurchaseLedger, ledgerMasters, "Purchase Ledger");
            if (string.IsNullOrWhiteSpace(row.ItemPurchaseLedger)) row.ItemPurchaseLedger = row.PurchaseLedger;
            row.ItemPurchaseLedger = ResolveLedgerEntry(row.ItemPurchaseLedger, ledgerMasters, "Item Purchase Ledger");
            row.IgstLedger = ResolveLedgerEntry(row.IgstLedger, ledgerMasters, "IGST Ledger");
            row.CgstLedger = ResolveLedgerEntry(row.CgstLedger, ledgerMasters, "CGST Ledger");
            row.SgstLedger = ResolveLedgerEntry(row.SgstLedger, ledgerMasters, "SGST Ledger");
            row.PlaceOfSupply = AccountingText.DisplayName(row.PlaceOfSupply);

            if (row.BilledQuantity == 0m && row.ActualQuantity != 0m) row.BilledQuantity = row.ActualQuantity;
            if (row.ItemAmount == 0m && row.TaxableValue != 0m) row.ItemAmount = row.TaxableValue;
            var itemRateQuantity = row.ActualQuantity != 0m ? row.ActualQuantity : row.BilledQuantity;
            if (itemRateQuantity != 0m && row.TaxableValue != 0m)
                row.ItemRate = Math.Round(row.TaxableValue / itemRateQuantity, 6, MidpointRounding.AwayFromZero);
            if (row.AccountingAmount == 0m && row.ItemAmount != 0m) row.AccountingAmount = row.ItemAmount;

            if (stockItems is not null) row.ItemName = ResolvePurchaseStockEntry(row.ItemName, stockItems, "Stock Item");
            if (units is not null)
            {
                row.Unit = ResolvePurchaseStockEntry(row.Unit, units, "Unit", useUnitCase: true);
                row.AlternateUnit = ResolvePurchaseStockEntry(row.AlternateUnit, units, "Alternate Unit", useUnitCase: true);
            }
        }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<AccountingAutomationHub.Domain.Masters.MasterRecord>> BuildLedgerMasterLookup(
        long clientId,
        string financialYear) =>
        (_masterRepository ?? throw new InvalidOperationException("Purchase parity services are not initialized."))
            .Get(clientId, financialYear, includeArchived: false)
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

    private static string ResolvePurchaseStockEntry<T>(
        string value,
        IReadOnlyDictionary<string, T[]> lookup,
        string fieldName,
        bool useUnitCase = false)
        where T : class
    {
        var clean = AccountingText.CleanWhitespace(value);
        if (clean.Length == 0) return string.Empty;
        var key = AccountingText.DuplicateKey(clean);
        if (!lookup.TryGetValue(key, out var matches))
            return useUnitCase ? AccountingText.UnitName(clean) : AccountingText.DisplayName(clean);
        if (matches.Length > 1)
            throw new InvalidOperationException($"Duplicate {fieldName} Master detected for '{clean}'. Resolve the duplicate in Stock Master before saving this Purchase entry.");
        var name = matches[0]?.GetType().GetProperty("Name")?.GetValue(matches[0])?.ToString() ?? clean;
        return name.Trim();
    }

    private async Task RefreshAccountingAfterManualEditAsync(AccountingAutomationHub.Domain.Clients.ClientProfile client)
    {
        try
        {
            if (_refreshCoordinator is not null)
                await _refreshCoordinator.RefreshAsync(client);
        }
        catch (Exception exception)
        {
            EditStatusText = $"Saved, but live accounting refresh needs attention: {exception.Message}";
        }
    }

    private bool CanSaveChanges() =>
        CanEditPurchase &&
        !IsBusy &&
        HasPersistedRows;

    public void ApplyReconciliationFilter(string? filter)
    {
        var normalized = (filter ?? string.Empty).Trim();
        IEnumerable<PurchaseImportRecord> rows = AllRows;
        rows = normalized.ToUpperInvariant() switch
        {
            "MATCHED" => rows.Where(record => record.ReconciliationStatus.Contains("Matched", StringComparison.OrdinalIgnoreCase) &&
                                                !record.ReconciliationStatus.Contains("Difference", StringComparison.OrdinalIgnoreCase)),
            "2A ONLY" => rows.Where(record => record.ReconciliationStatus.Equals("Not Found in 2B", StringComparison.OrdinalIgnoreCase) ||
                                              record.ReconciliationStatus.Equals("2A Only", StringComparison.OrdinalIgnoreCase)),
            "2B ONLY" => rows.Where(record => record.ReconciliationStatus.Equals("2B Only", StringComparison.OrdinalIgnoreCase)),
            "DIFFERENCES" => rows.Where(record => record.ReconciliationStatus.Contains("Difference", StringComparison.OrdinalIgnoreCase) ||
                                                   record.ReconciliationStatus.Equals("Rejected in 2B", StringComparison.OrdinalIgnoreCase)),
            _ => rows
        };
        ReplaceRows(ReconciliationRows, rows);
    }

    private static bool TryFinancialYearRange(string? financialYear, out DateTime start, out DateTime end)
    {
        start = default;
        end = default;
        var first = (financialYear ?? string.Empty).Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (!int.TryParse(first, out var year)) return false;
        start = new DateTime(year, 4, 1);
        end = new DateTime(year + 1, 3, 31);
        return true;
    }

    private static PurchaseImportSummary BuildSummaryFromRecords(
        IReadOnlyCollection<PurchaseImportRecord> records) =>
        new()
        {
            PurchaseRows = records.Count(record =>
                record.Destination == "Purchase"),
            RcmRows = records.Count(record =>
                record.Destination == "RCM"),
            DebitNoteRows = records.Count(record =>
                record.Destination == PurchaseTreatmentPolicy.DebitNoteDestination),
            DebitNoteReverseRows = records.Count(record =>
                record.Destination == PurchaseTreatmentPolicy.DebitNoteReverseDestination),
            MatchedRows = records.Count(record =>
                record.ReconciliationStatus.Contains(
                    "Matched",
                    StringComparison.OrdinalIgnoreCase) &&
                !record.ReconciliationStatus.Contains(
                    "Difference",
                    StringComparison.OrdinalIgnoreCase)),
            TwoAOnlyRows = records.Count(record =>
                record.ReconciliationStatus.Equals("Not Found in 2B", StringComparison.OrdinalIgnoreCase) ||
                record.ReconciliationStatus.Equals("2A Only", StringComparison.OrdinalIgnoreCase)),
            TwoBOnlyRows = records.Count(record =>
                record.ReconciliationStatus == "2B Only"),
            DifferenceRows = records.Count(record =>
                record.ReconciliationStatus.Contains(
                    "Difference",
                    StringComparison.OrdinalIgnoreCase) ||
                record.ReconciliationStatus == "Rejected in 2B")
        };

    private static string BuildSourceFingerprint(
        PurchaseAutoImportResult result)
    {
        var builder = new StringBuilder();

        builder.Append(result.ClientGstin)
            .Append('|')
            .Append(result.FinancialYear)
            .Append('|')
            .Append(result.Verification.TwoA.Status)
            .Append('|')
            .Append(result.Verification.TwoB.Status)
            .AppendLine();

        foreach (var source in result.SourceManifest
                     .OrderBy(item => item.FileType, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.InternalJsonName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.PayloadHash, StringComparer.OrdinalIgnoreCase))
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

        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(builder.ToString()));

        return Convert.ToHexString(bytes);
    }

    private void ReplaceResultRows(
        IEnumerable<PurchaseImportRecord> records,
        IEnumerable<PurchaseImportIssue> issues)
    {
        var materialized = records.ToArray();
        foreach (var row in materialized)
            PurchaseTreatmentPolicy.ApplyAccountingShape(row);

        ReplaceRows(
            AllRows,
            materialized);

        ReplaceRows(
            ReconciliationRows,
            materialized);

        ReplaceRows(
            PurchaseRows,
            materialized.Where(record =>
                record.Destination == "Purchase"));

        ReplaceRows(
            RcmRows,
            materialized.Where(record =>
                record.Destination == "RCM"));

        ReplaceRows(
            DebitNoteRows,
            materialized.Where(record =>
                record.Destination == PurchaseTreatmentPolicy.DebitNoteDestination));

        ReplaceRows(
            DebitNoteReverseRows,
            materialized.Where(record =>
                record.Destination == PurchaseTreatmentPolicy.DebitNoteReverseDestination));

        ReplaceRows(
            Issues,
            issues);

        OnPropertyChanged(nameof(HasPersistedRows));
        SaveChangesCommand.RaiseCanExecuteChanged();
        ClearModuleCommand.RaiseCanExecuteChanged();
        ItemNameMappingCommand.RaiseCanExecuteChanged();
        UnitMappingCommand.RaiseCanExecuteChanged();
        QuantityConversionCommand.RaiseCanExecuteChanged();
    }

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
        OnPropertyChanged(nameof(CanImportPurchase));
        OnPropertyChanged(nameof(CanReimportPurchase));
        OnPropertyChanged(nameof(CanEditPurchase));
        OnPropertyChanged(nameof(CanDeletePurchase));
        OnPropertyChanged(nameof(IsPurchaseReadOnly));
        OnPropertyChanged(nameof(PurchasePermissionSummary));
        Browse2ACommand.RaiseCanExecuteChanged();
        Browse2BCommand.RaiseCanExecuteChanged();
        BrowseEReturnsRootCommand.RaiseCanExecuteChanged();
        ManualPurchaseSheetImportCommand.RaiseCanExecuteChanged();
        RemapPurchaseSheetCommand.RaiseCanExecuteChanged();
        RunImportCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        SaveChangesCommand.RaiseCanExecuteChanged();
        ClearModuleCommand.RaiseCanExecuteChanged();
        ItemNameMappingCommand.RaiseCanExecuteChanged();
        UnitMappingCommand.RaiseCanExecuteChanged();
        QuantityConversionCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        ReimportCommand.RaiseCanExecuteChanged();
    }

    private void NotifyStatusVisibility()
    {
        OnPropertyChanged(
            nameof(HasWarning));

        OnPropertyChanged(
            nameof(HasError));
    }
}
