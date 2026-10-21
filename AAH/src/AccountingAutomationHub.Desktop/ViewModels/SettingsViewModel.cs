using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Desktop.Views;
using AccountingAutomationHub.Domain.Automation;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Imports;
using AccountingAutomationHub.Infrastructure.Persistence;
using AccountingAutomationHub.Infrastructure.Tally;
using Microsoft.Win32;

using System.Windows.Threading;
namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class SettingsViewModel : PageViewModel, IDisposable
{
    public const string CaProfilesPreferenceKey = "FinancialStatements.CaProfiles";
    public const string FirmProfilesPreferenceKey = "FinancialStatements.FirmProfiles";
    private readonly ApplicationPreferenceService _settings;
    private readonly BackgroundAutomationService _automation;
    private readonly ActiveClientContext _clientContext;
    private readonly ClientSchedulePreferenceService _clientSchedulePreferences;
    private readonly SecureLanServerService? _secureLanServer;
    private readonly SqliteClientPreferenceRepository _clientPreferenceRepository = new();
    private int _automationHealthRefreshPending;

    private bool _startWithWindows;
    private bool _closeToTray;
    private bool _notificationsEnabled;
    private bool _openLastClient;
    private bool _automationEnabled;
    private string _automationFolderName = "XYZ";
    private string _automationRootMode = "Anywhere";
    private string _automationRootFolder = string.Empty;
    private bool _scanExistingFiles;
    private bool _monitorExcelCsv;
    private bool _monitorPdf;
    private bool _autoImportKnownFormats;
    private int _fileStableSeconds = 3;
    private bool _notifyImportComplete;
    private bool _notifyImportFailed;
    private bool _notifyActionRequired;
    private bool _notifyFolderRegistration;
    private bool _notifyTallyDisconnected;
    private string _pdfPythonExecutable = string.Empty;
    private int _pdfTimeoutMinutes = 30;
    private bool _pdfDeleteGeneratedWorkbook;
    private string _bankOcrExternalRoot = string.Empty;
    private int _bankOcrTimeoutMinutes = 120;
    private bool _reviewExportOverwriteExisting;
    private bool _bankingAutoApplyExactMappings;
    private bool _bankingShowMappedDefault;
    private string _bankingOpeningBalanceMode = "Review";
    private bool _salesUseSavedMappings;
    private bool _salesSaveConfirmedMappings;
    private bool _purchaseUseSavedMappings;
    private bool _purchaseSaveConfirmedMappings;
    private bool _purchaseAutoImportKnown;
    private string _tallyHost = "127.0.0.1";
    private int _tallyPort = 9000;
    private bool _tallyAutoConnect;
    private bool _tallySelectAllDefault;
    private bool _secureLanEnabled;
    private string _secureLanBindAddress = "127.0.0.1";
    private int _secureLanPort = 47831;
    private bool _serverMode;
    private string _serverRootFolder = string.Empty;
    private int _pcExpiryDays = 30;
    private int _androidExpiryDays = 15;
    private int _folderLinkExpiryDays = 15;
    private string _externalBackupFolder = string.Empty;
    private string _networkClientPackagePath = string.Empty;
    private string _androidPackagePath = string.Empty;
    private string _serverDistributionText = "No server download ticket generated.";
    private string _securityDiagnosticsText = "Security diagnostics have not been refreshed yet.";
    private string _mainServerRuntimeText = "Main Server runtime has not been inspected yet.";
    private string _snapshotDiagnosticText = "No snapshot diagnostic has run in this Main Server session yet.";
    private string _lanControlPanelStatus = "LAN control panel has not been refreshed yet.";
    private LanUserActivityRow? _selectedLanUser;
    private LanSystemActivityRow? _selectedLanSystem;
    private string _lanPairingCodeText = "No active pairing code.";
    private System.Windows.Media.ImageSource? _mobileEnrollmentQrImage;
    private string _mobileEnrollmentQrText = "Select an active AAH user, then generate a user-bound Android QR.";
    private string _pendingMfaText = "No pending network login OTP.";
    private readonly DispatcherTimer? _pendingOtpRefreshTimer;
    private readonly DispatcherTimer? _networkMobileQrRefreshTimer;
    private string _lanUpdatePolicyText = "LAN update policy has not been published yet.";
    private string _theme = "Light";
    private string _accentColor = "#2563EB";
    private string _actionRequiredColor = "#EF4444";
    private string _tallySelectedPeriodColor = "#16A34A";
    private string _gridDensity = "Comfortable";
    private bool _rememberWindowPlacement;
    private string _shortcutHelp = "Ctrl+H";
    private string _shortcutRefresh = "Ctrl+R";
    private string _shortcutSave = "Ctrl+S";
    private string _shortcutClientSearch = "Ctrl+K";
    private string _shortcutNewClient = "Ctrl+N";
    private AutomationFolderRegistration? _selectedAutomationFolder;
    private AutomationFolderLinkRow? _selectedLinkedFolderRow;
    private string _automationHealthText = "Auto Import health has not been checked yet.";
    private string _statusText = "Settings ready.";
    private string _networkUpdateStatusText = "Update status has not been checked yet.";
    private string _networkConnectionStatusText = "Main Server connection has not been tested yet.";
    private System.Windows.Media.ImageSource? _networkMobileQrImage;
    private string _networkMobileQrText = "Live mobile code has not been loaded yet.";
    private bool _networkMessageNotificationsEnabled = true;
    private bool _networkWorkNotificationsEnabled = true;
    private DateTime _newManualHoliday = DateTime.Today;
    private DateTime? _selectedManualHoliday;
    private NotificationHistoryRow? _selectedNotificationHistory;
    private string _selectedMappingModule = ImportHeaderMappingCatalog.Banking;
    private string _mappingHeaderSearch = string.Empty;
    private string _mappingStatus = "Global header mapping ready. Existing aliases stay protected and are not displayed here.";
    private readonly List<SettingsHeaderFieldRow> _selectedMappingFields = [];
    private CaProfileRow? _selectedCaProfile;
    private FirmProfileRow? _selectedFirmProfile;

    public SettingsViewModel(ApplicationPreferenceService settings, BackgroundAutomationService automation, ActiveClientContext clientContext, SecureLanServerService? secureLanServer = null)
        : base("Settings", NetworkClientRuntime.IsNetworkClient
            ? "AAH user, registered-device session, Main Server connection and application update"
            : "Application, automation folders, PDF, Tally, Security & LAN, shortcuts, appearance and update preferences")
    {
        _settings = settings;
        _automation = automation;
        _clientContext = clientContext;
        _clientSchedulePreferences = new ClientSchedulePreferenceService(settings);
        _secureLanServer = secureLanServer;
        _clientPreferenceRepository.Initialize();
        AutomationFolders = [];
        LinkedFolderRows = [];
        NotificationHistory = [];
        ManualHolidays = [];
        AutomationHealth = [];
        AutoTallyImportClients = [];
        LanUsers = [];
        LanSystems = [];
        LanSecurityActivity = [];
        RootModeOptions = ["Anywhere", "Selected Root"];
        ThemeOptions = ["Light", "Dark", "System"];
        GridDensityOptions = ["Compact", "Comfortable"];
        OpeningBalanceModeOptions = ["Review", "Prompt", "Skip"];
        LanBindAddressOptions = new ObservableCollection<string>(SecurityNetworkPolicy.GetPrivateBindAddresses());
        MappingModuleOptions = new ObservableCollection<string>(ImportHeaderMappingCatalog.Modules);
        MappingHeaderFields = [];
        PendingHeaderMappings = [];
        CaProfiles = [];
        FirmProfiles = [];
        ImportHeaderMappingRuntime.Initialize();
        RebuildMappingHeaderFields();

        SaveCommand = new RelayCommand(Save, () => !NetworkClientRuntime.IsNetworkClient);
        AddFirmProfileCommand = new RelayCommand(AddFirmProfile, () => !NetworkClientRuntime.IsNetworkClient);
        DeleteFirmProfileCommand = new RelayCommand(DeleteFirmProfile, () => !NetworkClientRuntime.IsNetworkClient && SelectedFirmProfile is not null);
        SaveFirmProfilesCommand = new RelayCommand(SaveFirmProfiles, () => !NetworkClientRuntime.IsNetworkClient);
        AddCaProfileCommand = new RelayCommand(AddCaProfile, () => !NetworkClientRuntime.IsNetworkClient);
        DeleteCaProfileCommand = new RelayCommand(DeleteCaProfile, () => !NetworkClientRuntime.IsNetworkClient && SelectedCaProfile is not null);
        SaveCaProfilesCommand = new RelayCommand(SaveCaProfiles, () => !NetworkClientRuntime.IsNetworkClient);
        AddSelectedMappingHeadersCommand = new RelayCommand(AddSelectedMappingHeaders, () => _selectedMappingFields.Count > 0);
        SaveGlobalHeaderMappingsCommand = new RelayCommand(SaveGlobalHeaderMappings);
        SaveLanLocalSettingsCommand = new RelayCommand(SaveNetworkLocalSettings, () => NetworkClientRuntime.IsNetworkClient);
        BrowseRootFolderCommand = new RelayCommand(BrowseRootFolder);
        CreateFolderHereCommand = new RelayCommand(CreateFolderHere);
        LinkExistingFolderCommand = new RelayCommand(LinkExistingFolder);
        LinkRelinkSelectedFolderCommand = new RelayCommand(LinkRelinkSelectedFolder, () => SelectedLinkedFolderRow is not null);
        ChangeSelectedFolderFyCommand = new RelayCommand(ChangeSelectedFolderFy, () => SelectedLinkedFolderRow?.Registration is not null);
        RefreshFoldersCommand = new RelayCommand(LoadFolders);
        RefreshAutoTallyImportClientsCommand = new RelayCommand(LoadAutoTallyImportClients);
        SaveAutoTallyImportClientsCommand = new RelayCommand(SaveAutoTallyImportClients);
        ForceScanAllFoldersCommand = new RelayCommand(ForceScanAllFolders);
        RepairAutoImportCommand = new RelayCommand(RepairAutoImport);
        RunAutoImportHealthCheckCommand = new RelayCommand(LoadAutomationHealth);
        OpenFolderCommand = new RelayCommand(OpenSelectedFolder, () => SelectedAutomationFolder is not null);
        PauseResumeFolderCommand = new RelayCommand(PauseResumeSelectedFolder, () => SelectedAutomationFolder is not null);
        ScanFolderCommand = new RelayCommand(ScanSelectedFolder, () => SelectedAutomationFolder is not null);
        UnlinkFolderCommand = new RelayCommand(UnlinkSelectedFolder, () => SelectedAutomationFolder is not null);
        RenameLinkedFoldersCommand = new RelayCommand(RenameLinkedFolders);
        ExportSettingsCommand = new RelayCommand(ExportSettings);
        ImportSettingsCommand = new RelayCommand(ImportSettings);
        RestoreDefaultsCommand = new RelayCommand(RestoreDefaults);
        OpenDataFolderCommand = new RelayCommand(() => OpenPath(ApplicationDataPaths.RootFolder));
        OpenLogsFolderCommand = new RelayCommand(() => OpenPath(LogsFolder));
        OpenPipelineDiagnosticsCommand = new RelayCommand(() =>
        {
            PipelineRuntimeDiagnostics.WriteReportIfDue(force: true);
            OpenPath(PipelineRuntimeDiagnostics.ReportPath);
        });
        OpenPdfPipelineFolderCommand = new RelayCommand(() => OpenPath(PdfManagedPythonFolder));
        BrowseBankOcrFolderCommand = new RelayCommand(BrowseBankOcrFolder);
        OpenBankOcrFolderCommand = new RelayCommand(() =>
        {
            var root = ResolvedBankOcrRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                StatusText = "Advanced OCR folder is not configured or could not be found.";
                return;
            }
            OpenPath(root);
        });

        RefreshNotificationHistoryCommand = new RelayCommand(LoadNotificationHistory);
        ClearNotificationHistoryCommand = new RelayCommand(ClearNotificationHistory);
        AddManualHolidayCommand = new RelayCommand(AddManualHoliday, () => _clientContext.CurrentClient is not null);
        RemoveManualHolidayCommand = new RelayCommand(RemoveManualHoliday, () => SelectedManualHoliday.HasValue && _clientContext.CurrentClient is not null);
        RefreshSecurityDiagnosticsCommand = new RelayCommand(RefreshSecurityDiagnostics);
        RefreshLanControlPanelCommand = new RelayCommand(RefreshLanControlPanel);
        RunSnapshotDiagnosticCommand = new AsyncRelayCommand(RunSnapshotDiagnosticAsync, () => _secureLanServer is not null);
        RequireSelectedLanRegistrationOtpCommand = new RelayCommand(RequireSelectedLanRegistrationOtp, () => SelectedLanSystem is not null && SelectedLanSystem.DeviceType.Equals(ServerDeviceTypes.Windows, StringComparison.OrdinalIgnoreCase) && !NetworkClientRuntime.IsNetworkClient);
        GenerateLanPairingCodeCommand = new RelayCommand(GenerateLanPairingCode, () => _secureLanServer is not null);
        GenerateMobileEnrollmentQrCommand = new RelayCommand(GenerateMobileEnrollmentQr, () => _secureLanServer is not null && SelectedLanUser is not null);
        GenerateMobileLoginQrCommand = new RelayCommand(GenerateMobileLoginQr, () => _secureLanServer is not null && SelectedLanUser is not null);
        CreateLanUserCommand = new RelayCommand(CreateLanUser);
        OpenLanCertificateFolderCommand = new RelayCommand(OpenLanCertificateFolder);
        BrowseServerRootFolderCommand = new RelayCommand(BrowseServerRootFolder);
        BrowseExternalBackupFolderCommand = new RelayCommand(BrowseExternalBackupFolder);
        BrowseNetworkClientPackageCommand = new RelayCommand(() => BrowsePackageFile(false));
        BrowseAndroidPackageCommand = new RelayCommand(() => BrowsePackageFile(true));
        RunServerReplicationCommand = new RelayCommand(RunServerReplication);
        CreateWindowsDownloadTicketCommand = new RelayCommand(() => CreateDistributionTicket("windows"));
        PublishLanUpdateCommand = new RelayCommand(() => PublishLanUpdate(force: false));
        ForceLanUpdateCommand = new RelayCommand(() => PublishLanUpdate(force: true));
        CreateAndroidDownloadTicketCommand = new RelayCommand(() => CreateDistributionTicket("android"));
        OpenNetworkClientPackageFolderCommand = new RelayCommand(OpenNetworkClientPackageFolder);
        OpenServerRootFolderCommand = new RelayCommand(OpenServerRootFolder);
        CheckNetworkUpdateCommand = new AsyncRelayCommand(CheckNetworkUpdateAsync, () => NetworkClientRuntime.IsNetworkClient);
        InstallNetworkUpdateCommand = new AsyncRelayCommand(InstallNetworkUpdateAsync, () => NetworkClientRuntime.IsNetworkClient);
        SignOutNetworkSessionCommand = new AsyncRelayCommand(SignOutNetworkSessionAsync, () => NetworkClientRuntime.IsNetworkClient);
        RefreshNetworkIdentityCommand = new AsyncRelayCommand(RefreshNetworkIdentityAsync, () => NetworkClientRuntime.IsNetworkClient);
        TestNetworkConnectionCommand = new AsyncRelayCommand(TestNetworkConnectionAsync, () => NetworkClientRuntime.IsNetworkClient);
        RefreshNetworkDataCommand = new AsyncRelayCommand(RefreshNetworkDataAsync, () => NetworkClientRuntime.IsNetworkClient);
        RefreshNetworkMobileQrCommand = new AsyncRelayCommand(RefreshNetworkMobileQrAsync, () => NetworkClientRuntime.IsNetworkClient);
        OpenNetworkInstallFolderCommand = new RelayCommand(() => OpenPath(AccountingAutomationHub.Desktop.Services.NetworkClientInstallManager.CanonicalInstallPath), () => NetworkClientRuntime.IsNetworkClient);
        OpenNetworkLogsFolderCommand = new RelayCommand(() => OpenPath(Path.Combine(NetworkClientRuntime.RootFolder, "Logs")), () => NetworkClientRuntime.IsNetworkClient);
        OpenRemoteAssistWorkspaceCommand = new RelayCommand(OpenRemoteAssistWorkspace, () => NetworkClientRuntime.IsNetworkClient);
        OpenNetworkWorkspaceCommand = new ParameterRelayCommand(OpenNetworkWorkspace, parameter => NetworkClientRuntime.IsNetworkClient && !string.IsNullOrWhiteSpace(parameter?.ToString()));

        LoadFirmProfiles();
        LoadCaProfiles();

        if (NetworkClientRuntime.IsNetworkClient)
        {
            LoadNetworkLocalSettings();
            LoadNotificationHistory();
            RemoteAssistConsentCenter.Changed += OnRemoteAssistConsentChanged;
            StatusText = "LAN settings loaded. Main Server authority controls role, Client/FY access, Remote Assist and updates; this page controls only safe workstation preferences.";
            _networkMobileQrRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
            _networkMobileQrRefreshTimer.Tick += OnNetworkMobileQrRefreshTick;
            _networkMobileQrRefreshTimer.Start();
            _ = RefreshNetworkIdentityAsync();
            _ = RefreshNetworkMobileQrAsync();
            _ = CheckNetworkUpdateAsync();
            return;
        }

        LoadSettings();
        LoadFolders();
        LoadAutoTallyImportClients();
        LoadNotificationHistory();
        LoadManualHolidays();
        RefreshSecurityDiagnostics();
        RefreshLanControlPanel();
        _automation.FoldersChanged += OnFoldersChanged;
        _automation.ProgressChanged += OnAutomationProgressChanged;
        _clientContext.PropertyChanged += OnClientContextPropertyChanged;
        if (_secureLanServer is not null)
        {
            _secureLanServer.MfaChallengeCreated += OnMfaChallengeCreated;
            _pendingOtpRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _pendingOtpRefreshTimer.Tick += OnPendingOtpRefreshTick;
            _pendingOtpRefreshTimer.Start();
        }
    }

    public ObservableCollection<LanUserActivityRow> LanUsers { get; }
    public ObservableCollection<LanSystemActivityRow> LanSystems { get; }
    public ObservableCollection<LanSecurityActivityRow> LanSecurityActivity { get; }

    public ObservableCollection<AutomationFolderRegistration> AutomationFolders { get; }
    public ObservableCollection<AutomationFolderLinkRow> LinkedFolderRows { get; }
    public ObservableCollection<AutomationHealthSnapshot> AutomationHealth { get; }
    public ObservableCollection<AutoTallyImportClientRow> AutoTallyImportClients { get; }
    public ObservableCollection<NotificationHistoryRow> NotificationHistory { get; }
    public ObservableCollection<DateTime> ManualHolidays { get; }
    public IReadOnlyList<string> RootModeOptions { get; }
    public IReadOnlyList<string> ThemeOptions { get; }
    public IReadOnlyList<string> GridDensityOptions { get; }
    public IReadOnlyList<string> OpeningBalanceModeOptions { get; }
    public ObservableCollection<string> LanBindAddressOptions { get; }
    public ObservableCollection<string> MappingModuleOptions { get; }
    public ObservableCollection<SettingsHeaderFieldRow> MappingHeaderFields { get; }
    public ObservableCollection<PendingGlobalHeaderMappingRow> PendingHeaderMappings { get; }

    public ObservableCollection<FirmProfileRow> FirmProfiles { get; }
    public FirmProfileRow? SelectedFirmProfile
    {
        get => _selectedFirmProfile;
        set
        {
            if (SetProperty(ref _selectedFirmProfile, value))
                DeleteFirmProfileCommand?.RaiseCanExecuteChanged();
        }
    }
    public RelayCommand AddFirmProfileCommand { get; }
    public RelayCommand DeleteFirmProfileCommand { get; }
    public RelayCommand SaveFirmProfilesCommand { get; }

    public ObservableCollection<CaProfileRow> CaProfiles { get; }
    public CaProfileRow? SelectedCaProfile
    {
        get => _selectedCaProfile;
        set
        {
            if (SetProperty(ref _selectedCaProfile, value))
                DeleteCaProfileCommand?.RaiseCanExecuteChanged();
        }
    }
    public RelayCommand AddCaProfileCommand { get; }
    public RelayCommand DeleteCaProfileCommand { get; }
    public RelayCommand SaveCaProfilesCommand { get; }

    public RelayCommand SaveCommand { get; }
    public RelayCommand AddSelectedMappingHeadersCommand { get; }
    public RelayCommand SaveGlobalHeaderMappingsCommand { get; }
    public RelayCommand SaveLanLocalSettingsCommand { get; }
    public RelayCommand BrowseRootFolderCommand { get; }
    public RelayCommand CreateFolderHereCommand { get; }
    public RelayCommand LinkExistingFolderCommand { get; }
    public RelayCommand LinkRelinkSelectedFolderCommand { get; }
    public RelayCommand ChangeSelectedFolderFyCommand { get; }
    public RelayCommand RefreshFoldersCommand { get; }
    public RelayCommand RefreshAutoTallyImportClientsCommand { get; }
    public RelayCommand SaveAutoTallyImportClientsCommand { get; }
    public RelayCommand ForceScanAllFoldersCommand { get; }
    public RelayCommand RepairAutoImportCommand { get; }
    public RelayCommand RunAutoImportHealthCheckCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand PauseResumeFolderCommand { get; }
    public RelayCommand ScanFolderCommand { get; }
    public RelayCommand UnlinkFolderCommand { get; }
    public RelayCommand RenameLinkedFoldersCommand { get; }
    public RelayCommand ExportSettingsCommand { get; }
    public RelayCommand ImportSettingsCommand { get; }
    public RelayCommand RestoreDefaultsCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand OpenPipelineDiagnosticsCommand { get; }
    public RelayCommand OpenPdfPipelineFolderCommand { get; }
    public RelayCommand BrowseBankOcrFolderCommand { get; }
    public RelayCommand OpenBankOcrFolderCommand { get; }
    public RelayCommand RefreshNotificationHistoryCommand { get; }
    public RelayCommand ClearNotificationHistoryCommand { get; }
    public RelayCommand AddManualHolidayCommand { get; }
    public RelayCommand RemoveManualHolidayCommand { get; }
    public RelayCommand RefreshSecurityDiagnosticsCommand { get; }
    public RelayCommand RefreshLanControlPanelCommand { get; }
    public AsyncRelayCommand RunSnapshotDiagnosticCommand { get; }
    public RelayCommand RequireSelectedLanRegistrationOtpCommand { get; }
    public RelayCommand GenerateLanPairingCodeCommand { get; }
    public RelayCommand GenerateMobileEnrollmentQrCommand { get; }
    public RelayCommand GenerateMobileLoginQrCommand { get; }
    public RelayCommand CreateLanUserCommand { get; }
    public RelayCommand OpenLanCertificateFolderCommand { get; }
    public RelayCommand BrowseServerRootFolderCommand { get; }
    public RelayCommand BrowseExternalBackupFolderCommand { get; }
    public RelayCommand BrowseNetworkClientPackageCommand { get; }
    public RelayCommand BrowseAndroidPackageCommand { get; }
    public RelayCommand RunServerReplicationCommand { get; }
    public RelayCommand CreateWindowsDownloadTicketCommand { get; }
    public RelayCommand PublishLanUpdateCommand { get; }
    public RelayCommand ForceLanUpdateCommand { get; }
    public RelayCommand CreateAndroidDownloadTicketCommand { get; }
    public RelayCommand OpenNetworkClientPackageFolderCommand { get; }
    public RelayCommand OpenServerRootFolderCommand { get; }
    public AsyncRelayCommand CheckNetworkUpdateCommand { get; }
    public AsyncRelayCommand InstallNetworkUpdateCommand { get; }
    public AsyncRelayCommand SignOutNetworkSessionCommand { get; }
    public AsyncRelayCommand RefreshNetworkIdentityCommand { get; }
    public AsyncRelayCommand TestNetworkConnectionCommand { get; }
    public AsyncRelayCommand RefreshNetworkDataCommand { get; }
    public AsyncRelayCommand RefreshNetworkMobileQrCommand { get; }
    public RelayCommand OpenNetworkInstallFolderCommand { get; }
    public RelayCommand OpenNetworkLogsFolderCommand { get; }
    public ParameterRelayCommand OpenNetworkWorkspaceCommand { get; }
    public RelayCommand OpenRemoteAssistWorkspaceCommand { get; }

    public DateTime NewManualHoliday { get => _newManualHoliday; set => SetProperty(ref _newManualHoliday, value.Date); }
    public DateTime? SelectedManualHoliday
    {
        get => _selectedManualHoliday;
        set { if (SetProperty(ref _selectedManualHoliday, value)) RemoveManualHolidayCommand.RaiseCanExecuteChanged(); }
    }
    public NotificationHistoryRow? SelectedNotificationHistory { get => _selectedNotificationHistory; set => SetProperty(ref _selectedNotificationHistory, value); }
    public string HolidayClientCaption => _clientContext.CurrentClient is { } client
        ? $"{client.ClientName} • FY {client.FinancialYear} (client-specific)"
        : "Open a client to manage client-specific holidays.";

    public bool StartWithWindows { get => _startWithWindows; set => SetProperty(ref _startWithWindows, value); }
    public bool CloseToTray { get => _closeToTray; set => SetProperty(ref _closeToTray, value); }
    public bool NotificationsEnabled { get => _notificationsEnabled; set => SetProperty(ref _notificationsEnabled, value); }
    public bool OpenLastClient { get => _openLastClient; set => SetProperty(ref _openLastClient, value); }
    public bool AutomationEnabled { get => _automationEnabled; set => SetProperty(ref _automationEnabled, value); }
    public string AutomationFolderName
    {
        get => _automationFolderName;
        set
        {
            if (SetProperty(ref _automationFolderName, value)) OnPropertyChanged(nameof(CreateFolderHereText));
        }
    }
    public string CreateFolderHereText => $"Create {(string.IsNullOrWhiteSpace(AutomationFolderName) ? "XYZ" : AutomationFolderName.Trim())} Here";
    public string AutomationRootMode { get => _automationRootMode; set { if (SetProperty(ref _automationRootMode, value)) OnPropertyChanged(nameof(IsSelectedRootMode)); } }
    public string AutomationRootFolder { get => _automationRootFolder; set => SetProperty(ref _automationRootFolder, value); }
    public bool IsSelectedRootMode => AutomationRootMode.Equals("Selected Root", StringComparison.OrdinalIgnoreCase);
    public bool ScanExistingFiles { get => _scanExistingFiles; set => SetProperty(ref _scanExistingFiles, value); }
    public bool MonitorExcelCsv { get => _monitorExcelCsv; set => SetProperty(ref _monitorExcelCsv, value); }
    public bool MonitorPdf { get => _monitorPdf; set => SetProperty(ref _monitorPdf, value); }
    public bool AutoImportKnownFormats { get => _autoImportKnownFormats; set => SetProperty(ref _autoImportKnownFormats, value); }
    public int FileStableSeconds { get => _fileStableSeconds; set => SetProperty(ref _fileStableSeconds, Math.Clamp(value, 1, 30)); }
    public bool NotifyImportComplete { get => _notifyImportComplete; set => SetProperty(ref _notifyImportComplete, value); }
    public bool NotifyImportFailed { get => _notifyImportFailed; set => SetProperty(ref _notifyImportFailed, value); }
    public bool NotifyActionRequired { get => _notifyActionRequired; set => SetProperty(ref _notifyActionRequired, value); }
    public bool NotifyFolderRegistration { get => _notifyFolderRegistration; set => SetProperty(ref _notifyFolderRegistration, value); }
    public bool NotifyTallyDisconnected { get => _notifyTallyDisconnected; set => SetProperty(ref _notifyTallyDisconnected, value); }
    public string PdfPythonExecutable { get => _pdfPythonExecutable; set => SetProperty(ref _pdfPythonExecutable, value); }
    public int PdfTimeoutMinutes { get => _pdfTimeoutMinutes; set => SetProperty(ref _pdfTimeoutMinutes, Math.Clamp(value, 1, 240)); }
    public bool PdfDeleteGeneratedWorkbook { get => _pdfDeleteGeneratedWorkbook; set => SetProperty(ref _pdfDeleteGeneratedWorkbook, value); }
    public string BankOcrExternalRoot
    {
        get => _bankOcrExternalRoot;
        set
        {
            if (!SetProperty(ref _bankOcrExternalRoot, value)) return;
            OnPropertyChanged(nameof(ResolvedBankOcrRoot));
            OnPropertyChanged(nameof(BankOcrRuntimeStatus));
        }
    }
    public int BankOcrTimeoutMinutes { get => _bankOcrTimeoutMinutes; set => SetProperty(ref _bankOcrTimeoutMinutes, Math.Clamp(value, 5, 720)); }
    public bool ReviewExportOverwriteExisting { get => _reviewExportOverwriteExisting; set => SetProperty(ref _reviewExportOverwriteExisting, value); }
    public bool BankingAutoApplyExactMappings { get => _bankingAutoApplyExactMappings; set => SetProperty(ref _bankingAutoApplyExactMappings, value); }
    public bool BankingShowMappedDefault { get => _bankingShowMappedDefault; set => SetProperty(ref _bankingShowMappedDefault, value); }
    public string BankingOpeningBalanceMode { get => _bankingOpeningBalanceMode; set => SetProperty(ref _bankingOpeningBalanceMode, value); }
    public bool SalesUseSavedMappings { get => _salesUseSavedMappings; set => SetProperty(ref _salesUseSavedMappings, value); }
    public bool SalesSaveConfirmedMappings { get => _salesSaveConfirmedMappings; set => SetProperty(ref _salesSaveConfirmedMappings, value); }
    public bool PurchaseUseSavedMappings { get => _purchaseUseSavedMappings; set => SetProperty(ref _purchaseUseSavedMappings, value); }
    public bool PurchaseSaveConfirmedMappings { get => _purchaseSaveConfirmedMappings; set => SetProperty(ref _purchaseSaveConfirmedMappings, value); }
    public bool PurchaseAutoImportKnown { get => _purchaseAutoImportKnown; set => SetProperty(ref _purchaseAutoImportKnown, value); }
    public string TallyHost { get => _tallyHost; set => SetProperty(ref _tallyHost, value); }
    public int TallyPort { get => _tallyPort; set => SetProperty(ref _tallyPort, Math.Clamp(value, 1, 65535)); }
    public bool TallyAutoConnect { get => _tallyAutoConnect; set => SetProperty(ref _tallyAutoConnect, value); }
    public bool TallySelectAllDefault { get => _tallySelectAllDefault; set => SetProperty(ref _tallySelectAllDefault, value); }
    public bool SecureLanEnabled { get => _secureLanEnabled; set => SetProperty(ref _secureLanEnabled, value); }
    public string SecureLanBindAddress { get => _secureLanBindAddress; set => SetProperty(ref _secureLanBindAddress, value); }
    public int SecureLanPort { get => _secureLanPort; set => SetProperty(ref _secureLanPort, Math.Clamp(value, 1024, 65535)); }
    public bool ServerMode { get => _serverMode; set => SetProperty(ref _serverMode, value); }
    public string ServerRootFolder { get => _serverRootFolder; set => SetProperty(ref _serverRootFolder, value); }
    public int PcExpiryDays { get => _pcExpiryDays; set => SetProperty(ref _pcExpiryDays, Math.Clamp(value, 1, 365)); }
    public int AndroidExpiryDays { get => _androidExpiryDays; set => SetProperty(ref _androidExpiryDays, Math.Clamp(value, 1, 365)); }
    public int FolderLinkExpiryDays { get => _folderLinkExpiryDays; set => SetProperty(ref _folderLinkExpiryDays, Math.Clamp(value, 1, 365)); }
    public string ExternalBackupFolder { get => _externalBackupFolder; set => SetProperty(ref _externalBackupFolder, value); }
    public string NetworkClientPackagePath { get => _networkClientPackagePath; set => SetProperty(ref _networkClientPackagePath, value); }
    public string AndroidPackagePath { get => _androidPackagePath; set => SetProperty(ref _androidPackagePath, value); }
    public string ServerDistributionText { get => _serverDistributionText; private set => SetProperty(ref _serverDistributionText, value); }
    public string SecurityDiagnosticsText { get => _securityDiagnosticsText; private set => SetProperty(ref _securityDiagnosticsText, value); }
    public string MainServerRuntimeText { get => _mainServerRuntimeText; private set => SetProperty(ref _mainServerRuntimeText, value); }
    public string RemoteAssistInternetRouteText => _secureLanServer is null ? "Remote Assist Internet route: Main Server runtime unavailable." : string.IsNullOrWhiteSpace(_secureLanServer.RemoteAssistInternetEndpoint) ? $"Remote Assist Internet route: no private VPN/overlay interface detected. Current private route: {_secureLanServer.Endpoint}." : $"Remote Assist Internet route: {_secureLanServer.RemoteAssistInternetEndpoint} (preferred for Remote Assist). LAN/private endpoint remains {_secureLanServer.Endpoint}.";
    public string SnapshotDiagnosticText { get => _snapshotDiagnosticText; private set => SetProperty(ref _snapshotDiagnosticText, value); }
    public string LanControlPanelStatus { get => _lanControlPanelStatus; private set => SetProperty(ref _lanControlPanelStatus, value); }
    public string SelectedMappingModule
    {
        get => _selectedMappingModule;
        set
        {
            var normalized = ImportHeaderMappingCatalog.NormalizeModule(value);
            if (SetProperty(ref _selectedMappingModule, normalized.Length == 0 ? ImportHeaderMappingCatalog.Banking : normalized))
            {
                _selectedMappingFields.Clear();
                RebuildMappingHeaderFields();
                AddSelectedMappingHeadersCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string MappingHeaderSearch
    {
        get => _mappingHeaderSearch;
        set
        {
            if (SetProperty(ref _mappingHeaderSearch, value ?? string.Empty)) RebuildMappingHeaderFields();
        }
    }

    public string MappingStatus
    {
        get => _mappingStatus;
        private set => SetProperty(ref _mappingStatus, value ?? string.Empty);
    }

    public void SetMappingFieldSelection(IEnumerable<SettingsHeaderFieldRow> rows)
    {
        _selectedMappingFields.Clear();
        _selectedMappingFields.AddRange(rows.Where(item => item is not null));
        AddSelectedMappingHeadersCommand.RaiseCanExecuteChanged();
    }

    public LanUserActivityRow? SelectedLanUser { get => _selectedLanUser; set { if(SetProperty(ref _selectedLanUser, value)) { GenerateMobileEnrollmentQrCommand.RaiseCanExecuteChanged(); GenerateMobileLoginQrCommand.RaiseCanExecuteChanged(); } } }
    public LanSystemActivityRow? SelectedLanSystem
    {
        get => _selectedLanSystem;
        set
        {
            if (!SetProperty(ref _selectedLanSystem, value)) return;
            RequireSelectedLanRegistrationOtpCommand.RaiseCanExecuteChanged();
        }
    }
    public string LanPairingCodeText { get => _lanPairingCodeText; private set => SetProperty(ref _lanPairingCodeText, value); }
    public System.Windows.Media.ImageSource? MobileEnrollmentQrImage { get => _mobileEnrollmentQrImage; private set => SetProperty(ref _mobileEnrollmentQrImage, value); }
    public string MobileEnrollmentQrText { get => _mobileEnrollmentQrText; private set => SetProperty(ref _mobileEnrollmentQrText, value); }
    public string PendingMfaText { get => _pendingMfaText; private set => SetProperty(ref _pendingMfaText, value); }
    public string LanUpdatePolicyText { get => _lanUpdatePolicyText; private set => SetProperty(ref _lanUpdatePolicyText, value); }
    public string SecureLanEndpoint => _secureLanServer?.Endpoint ?? string.Empty;
    public string SecureLanServerStatus => _secureLanServer?.StatusText ?? "Secure LAN runtime is unavailable in this application session.";
    public string Theme { get => _theme; set => SetProperty(ref _theme, value); }
    public string AccentColor { get => _accentColor; set => SetProperty(ref _accentColor, value); }
    public string ActionRequiredColor { get => _actionRequiredColor; set => SetProperty(ref _actionRequiredColor, value); }
    public string TallySelectedPeriodColor { get => _tallySelectedPeriodColor; set => SetProperty(ref _tallySelectedPeriodColor, value); }
    public string GridDensity { get => _gridDensity; set => SetProperty(ref _gridDensity, value); }
    public bool RememberWindowPlacement { get => _rememberWindowPlacement; set => SetProperty(ref _rememberWindowPlacement, value); }
    public string ShortcutHelp { get => _shortcutHelp; set => SetProperty(ref _shortcutHelp, value); }
    public string ShortcutRefresh { get => _shortcutRefresh; set => SetProperty(ref _shortcutRefresh, value); }
    public string ShortcutSave { get => _shortcutSave; set => SetProperty(ref _shortcutSave, value); }
    public string ShortcutClientSearch { get => _shortcutClientSearch; set => SetProperty(ref _shortcutClientSearch, value); }
    public string ShortcutNewClient { get => _shortcutNewClient; set => SetProperty(ref _shortcutNewClient, value); }

    public AutomationFolderLinkRow? SelectedLinkedFolderRow
    {
        get => _selectedLinkedFolderRow;
        set
        {
            if (!SetProperty(ref _selectedLinkedFolderRow, value)) return;
            SelectedAutomationFolder = value?.Registration;
            LinkRelinkSelectedFolderCommand.RaiseCanExecuteChanged();
            ChangeSelectedFolderFyCommand.RaiseCanExecuteChanged();
        }
    }

    public AutomationFolderRegistration? SelectedAutomationFolder
    {
        get => _selectedAutomationFolder;
        set
        {
            if (!SetProperty(ref _selectedAutomationFolder, value)) return;
            OpenFolderCommand.RaiseCanExecuteChanged();
            PauseResumeFolderCommand.RaiseCanExecuteChanged();
            ScanFolderCommand.RaiseCanExecuteChanged();
            UnlinkFolderCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(PauseResumeText));
        }
    }

    public string PauseResumeText => SelectedAutomationFolder?.IsPaused == true ? "Resume" : "Pause";
    public string AutomationHealthText
    {
        get => _automationHealthText;
        private set => SetProperty(ref _automationHealthText, value);
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsNetworkClient => NetworkClientRuntime.IsNetworkClient;
    public bool IsMainApplicationSettings => !NetworkClientRuntime.IsNetworkClient;
    public string NetworkUserSummary => $"{(string.IsNullOrWhiteSpace(NetworkClientRuntime.DisplayName) ? NetworkClientRuntime.UserName : NetworkClientRuntime.DisplayName)} · User ID {NetworkClientRuntime.UserName} · {NetworkClientRuntime.RoleName}";
    public string NetworkPermissionSummary => NetworkClientRuntime.Permissions.Count == 0 ? "No effective LAN permissions." : string.Join(" · ", NetworkClientRuntime.Permissions.OrderBy(x => x));
    public string NetworkSessionSummary => NetworkClientRuntime.SessionExpiresUtc == default ? "No active login session. Registration OTP remains device-level and is not used for routine login." : $"Registered-device login active until {NetworkClientRuntime.SessionExpiresUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}. Session renewal uses User ID/password without OTP; Registration OTP is required again only after 30 days of inactivity or an Administrator reset.";
    public string NetworkServerSummary
    {
        get
        {
            var endpoint = NetworkClientRuntime.ResolveServerEndpoint();
            return $"Main Server: {(endpoint.Length > 0 ? endpoint : "Endpoint unavailable")} · Device: {Environment.MachineName} · Device ID: {NetworkClientRuntime.DeviceId}";
        }
    }
    public string NetworkUpdateStatusText { get => _networkUpdateStatusText; private set => SetProperty(ref _networkUpdateStatusText, value); }
    public string NetworkConnectionStatusText { get => _networkConnectionStatusText; private set => SetProperty(ref _networkConnectionStatusText, value); }
    public System.Windows.Media.ImageSource? NetworkMobileQrImage { get => _networkMobileQrImage; private set => SetProperty(ref _networkMobileQrImage, value); }
    public string NetworkMobileQrText { get => _networkMobileQrText; private set => SetProperty(ref _networkMobileQrText, value); }
    public bool NetworkMessageNotificationsEnabled { get => _networkMessageNotificationsEnabled; set => SetProperty(ref _networkMessageNotificationsEnabled, value); }
    public bool NetworkWorkNotificationsEnabled { get => _networkWorkNotificationsEnabled; set => SetProperty(ref _networkWorkNotificationsEnabled, value); }
    public bool CanNetworkReadData => NetworkClientRuntime.HasPermission(GovernancePermissions.ReadData);
    public bool CanNetworkUpload => NetworkClientRuntime.HasAnyPermission(GovernancePermissions.UploadFiles, GovernancePermissions.Import);
    public bool CanNetworkExport => NetworkClientRuntime.HasPermission(GovernancePermissions.ExportData);
    public string NetworkAccessSummary => $"Accounting: {(CanNetworkReadData ? "View allowed" : "No Read Data")} · Upload/Import: {(CanNetworkUpload ? "Allowed" : "Denied")} · Export: {(CanNetworkExport ? "Allowed" : "Denied")} · LAN accounting mutation: Always blocked on this workstation";
    public string NetworkWorkspaceSummary => "Always available after sign-in: Client Directory · Team · Work · Notifications · Remote Assist · Settings. Accounting modules appear only for an authorised Client/FY and the current effective view permissions.";
    public string NetworkPolicySummary => $"Device health {NetworkClientRuntime.DeviceHealthState} · Risk {NetworkClientRuntime.DeviceRiskScore} · Policy revisions A:{NetworkClientRuntime.AuthorizationRevision} D:{NetworkClientRuntime.DevicePolicyRevision} C:{NetworkClientRuntime.ClientAccessRevision} S:{NetworkClientRuntime.SecurityPolicyRevision} · Live data rev {NetworkClientRuntime.AccountingDataRevision}";
    public string NetworkInstallPath => AccountingAutomationHub.Desktop.Services.NetworkClientInstallManager.CanonicalInstallPath;
    public string NetworkDataRoot => NetworkClientRuntime.RootFolder;
    public string NetworkRemoteAssistSummary
    {
        get
        {
            var pending = RemoteAssistConsentCenter.Pending;
            if (pending is not null) return $"REQUEST WAITING · {pending.HelperUser} · {pending.PermissionMode} · {pending.Scope} · expires {pending.ExpiresUtc.ToLocalTime():g}";
            var active = RemoteAssistConsentCenter.Active;
            if (active is not null) return $"ACTIVE · {active.HelperUser} · {active.PermissionMode} · {active.Scope} · until {active.ExpiresUtc.ToLocalTime():g}";
            return "No Remote Assist request is waiting and no Remote Assist session is active.";
        }
    }
    public string DataFolder => ApplicationDataPaths.RootFolder;
    public string DatabasePath => ApplicationDataPaths.DatabasePath;
    public string LogsFolder => Path.Combine(ApplicationDataPaths.RootFolder, "Logs");
    public string AutoImportLogPath => AutomationDiagnosticLog.LogPath;
    public string PdfManagedPythonFolder => @"C:\BankPDFImporter\Python";
    public string PdfPipelineScriptPath => Path.Combine(PdfManagedPythonFolder, "pdf_pipeline_v4_merged.py");
    public string PdfRegistryFolder => @"C:\BankPDFImporter\Registry";
    public string ResolvedBankOcrRoot
    {
        get
        {
            var configured = BankOcrExternalRoot?.Trim() ?? string.Empty;
            var info = BankAdvancedOcrRuntimeLocator.Resolve(configured);
            return info.IsReady ? info.RootFolder : configured;
        }
    }
    public string BankOcrRuntimeStatus
    {
        get
        {
            var info = BankAdvancedOcrRuntimeLocator.Resolve(BankOcrExternalRoot);
            return info.IsReady
                ? $"Ready: Bank OCR direct on-demand PaddleOCR-VL 1.6 runtime at {info.RootFolder}"
                : info.FailureMessage;
        }
    }
    public string InstallationFolder => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    private void LoadSettings()
    {
        StartWithWindows = _settings.GetBool(ApplicationSettingKeys.StartWithWindows);
        CloseToTray = _settings.GetBool(ApplicationSettingKeys.CloseToTray);
        NotificationsEnabled = _settings.GetBool(ApplicationSettingKeys.NotificationsEnabled);
        OpenLastClient = _settings.GetBool(ApplicationSettingKeys.OpenLastClient);
        AutomationEnabled = _settings.GetBool(ApplicationSettingKeys.AutomationEnabled);
        AutomationFolderName = _settings.Get(ApplicationSettingKeys.AutomationFolderName);
        AutomationRootMode = _settings.Get(ApplicationSettingKeys.AutomationRootMode);
        AutomationRootFolder = _settings.Get(ApplicationSettingKeys.AutomationRootFolder);
        ScanExistingFiles = _settings.GetBool(ApplicationSettingKeys.AutomationScanExisting);
        MonitorExcelCsv = _settings.GetBool(ApplicationSettingKeys.AutomationMonitorExcelCsv);
        MonitorPdf = _settings.GetBool(ApplicationSettingKeys.AutomationMonitorPdf);
        AutoImportKnownFormats = _settings.GetBool(ApplicationSettingKeys.AutomationAutoImportKnown);
        FileStableSeconds = _settings.GetInt(ApplicationSettingKeys.AutomationFileStableSeconds, 3);
        NotifyImportComplete = _settings.GetBool(ApplicationSettingKeys.NotifyImportComplete);
        NotifyImportFailed = _settings.GetBool(ApplicationSettingKeys.NotifyImportFailed);
        NotifyActionRequired = _settings.GetBool(ApplicationSettingKeys.NotifyActionRequired);
        NotifyFolderRegistration = _settings.GetBool(ApplicationSettingKeys.NotifyFolderRegistration);
        NotifyTallyDisconnected = _settings.GetBool(ApplicationSettingKeys.NotifyTallyDisconnected);
        PdfPythonExecutable = _settings.Get(ApplicationSettingKeys.PdfPythonExecutable);
        PdfTimeoutMinutes = _settings.GetInt(ApplicationSettingKeys.PdfTimeoutMinutes, 30);
        PdfDeleteGeneratedWorkbook = _settings.GetBool(ApplicationSettingKeys.PdfDeleteGeneratedWorkbook);
        BankOcrExternalRoot = _settings.Get(ApplicationSettingKeys.BankOcrExternalRoot);
        BankOcrTimeoutMinutes = _settings.GetInt(ApplicationSettingKeys.BankOcrTimeoutMinutes, 120);
        if (string.IsNullOrWhiteSpace(BankOcrExternalRoot))
        {
            var detected = BankAdvancedOcrRuntimeLocator.Resolve();
            if (detected.IsReady) BankOcrExternalRoot = detected.RootFolder;
        }
        ReviewExportOverwriteExisting = _settings.GetBool(ApplicationSettingKeys.ReviewExportOverwriteExisting);
        BankingAutoApplyExactMappings = _settings.GetBool(ApplicationSettingKeys.BankingAutoApplyExactMappings);
        BankingShowMappedDefault = _settings.GetBool(ApplicationSettingKeys.BankingShowMappedDefault);
        BankingOpeningBalanceMode = _settings.Get(ApplicationSettingKeys.BankingOpeningBalanceMode);
        SalesUseSavedMappings = _settings.GetBool(ApplicationSettingKeys.SalesUseSavedMappings);
        SalesSaveConfirmedMappings = _settings.GetBool(ApplicationSettingKeys.SalesSaveConfirmedMappings);
        PurchaseUseSavedMappings = _settings.GetBool(ApplicationSettingKeys.PurchaseUseSavedMappings);
        PurchaseSaveConfirmedMappings = _settings.GetBool(ApplicationSettingKeys.PurchaseSaveConfirmedMappings);
        PurchaseAutoImportKnown = _settings.GetBool(ApplicationSettingKeys.PurchaseAutoImportKnown);
        TallyHost = _settings.Get(ApplicationSettingKeys.TallyHost);
        TallyPort = _settings.GetInt(ApplicationSettingKeys.TallyPort, 9000);
        TallyAutoConnect = _settings.GetBool(ApplicationSettingKeys.TallyAutoConnect);
        TallySelectAllDefault = _settings.GetBool(ApplicationSettingKeys.TallySelectAllDefault);
        SecureLanEnabled = _settings.GetBool(ApplicationSettingKeys.SecurityLanEnabled);
        SecureLanBindAddress = _settings.Get(ApplicationSettingKeys.SecurityLanBindAddress);
        SecureLanPort = _settings.GetInt(ApplicationSettingKeys.SecurityLanPort, 47831);
        ServerMode = _settings.GetBool(ApplicationSettingKeys.SecurityServerMode);
        ServerRootFolder = _settings.Get(ApplicationSettingKeys.SecurityServerRootFolder);
        PcExpiryDays = _settings.GetInt(ApplicationSettingKeys.SecurityPcExpiryDays, 30);
        AndroidExpiryDays = _settings.GetInt(ApplicationSettingKeys.SecurityAndroidExpiryDays, 15);
        FolderLinkExpiryDays = _settings.GetInt(ApplicationSettingKeys.SecurityFolderLinkExpiryDays, 15);
        ExternalBackupFolder = _settings.Get(ApplicationSettingKeys.SecurityExternalBackupFolder);
        NetworkClientPackagePath = _settings.Get(ApplicationSettingKeys.SecurityNetworkClientPackagePath);
        if (string.IsNullOrWhiteSpace(NetworkClientPackagePath))
        {
            var bundledNetworkClient = Path.Combine(AppContext.BaseDirectory, "NetworkClient", "AAHNetworkClient.zip");
            if (File.Exists(bundledNetworkClient)) NetworkClientPackagePath = bundledNetworkClient;
        }
        AndroidPackagePath = _settings.Get(ApplicationSettingKeys.SecurityAndroidPackagePath);
        if (string.IsNullOrWhiteSpace(SecureLanBindAddress)) SecureLanBindAddress = LanBindAddressOptions.FirstOrDefault() ?? "127.0.0.1";
        Theme = _settings.Get(ApplicationSettingKeys.Theme);
        AccentColor = _settings.Get(ApplicationSettingKeys.AccentColor);
        ActionRequiredColor = _settings.Get(ApplicationSettingKeys.ActionRequiredColor);
        TallySelectedPeriodColor = _settings.Get(ApplicationSettingKeys.TallySelectedPeriodColor);
        GridDensity = _settings.Get(ApplicationSettingKeys.GridDensity);
        RememberWindowPlacement = _settings.GetBool(ApplicationSettingKeys.RememberWindowPlacement);
        ShortcutHelp = _settings.Get(ApplicationSettingKeys.ShortcutHelp);
        ShortcutRefresh = _settings.Get(ApplicationSettingKeys.ShortcutRefresh);
        ShortcutSave = _settings.Get(ApplicationSettingKeys.ShortcutSave);
        ShortcutClientSearch = _settings.Get(ApplicationSettingKeys.ShortcutClientSearch);
        ShortcutNewClient = _settings.Get(ApplicationSettingKeys.ShortcutNewClient);
        OnPropertyChanged(nameof(IsSelectedRootMode));
    }

    private void RebuildMappingHeaderFields()
    {
        var search = MappingHeaderSearch.Trim();
        MappingHeaderFields.Clear();
        foreach (var field in ImportHeaderMappingCatalog.GetFields(SelectedMappingModule)
                     .Where(field => search.Length == 0 || field.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) || field.Key.Contains(search, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(field => field.Required)
                     .ThenBy(field => field.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            MappingHeaderFields.Add(new SettingsHeaderFieldRow
            {
                Module = field.Module,
                Key = field.Key,
                DisplayName = field.DisplayName,
                Required = field.Required
            });
        }
    }

    private void AddSelectedMappingHeaders()
    {
        var added = 0;
        foreach (var field in _selectedMappingFields)
        {
            PendingHeaderMappings.Add(new PendingGlobalHeaderMappingRow
            {
                Module = field.Module,
                CanonicalField = field.Key,
                CanonicalDisplay = field.DisplayName
            });
            added++;
        }
        MappingStatus = $"Added {added:N0} AAH header row(s) to the pending global mapping table. Select the same AAH header again whenever you want to add another alias.";
        SaveGlobalHeaderMappingsCommand.RaiseCanExecuteChanged();
    }

    private void SaveGlobalHeaderMappings()
    {
        var candidates = PendingHeaderMappings.Where(item => !string.IsNullOrWhiteSpace(item.SourceHeader)).ToArray();
        if (candidates.Length == 0)
        {
            MappingStatus = "Enter at least one new external header before saving.";
            return;
        }

        var added = 0;
        var already = 0;
        var conflicts = 0;
        foreach (var row in candidates)
        {
            var result = ImportHeaderMappingRuntime.AddGlobalMapping(row.Module, row.CanonicalField, row.SourceHeader);
            row.Status = result.Message;
            if (result.Added) added++;
            else if (result.AlreadyExists) already++;
            else if (result.Conflict) conflicts++;
        }

        foreach (var row in PendingHeaderMappings.Where(row => candidates.Contains(row) && !row.Status.Contains("cannot", StringComparison.OrdinalIgnoreCase) && !row.Status.Contains("already mapped to", StringComparison.OrdinalIgnoreCase)).ToArray())
            PendingHeaderMappings.Remove(row);

        MappingStatus = $"Global mapping update: {added:N0} added · {already:N0} already recognized · {conflicts:N0} conflict(s). Existing mappings remain append-only and hidden.";
        SaveGlobalHeaderMappingsCommand.RaiseCanExecuteChanged();
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(AutomationFolderName))
        {
            StatusText = "Automation Folder Name cannot be blank.";
            return;
        }
        if (IsSelectedRootMode && !string.IsNullOrWhiteSpace(AutomationRootFolder) && !Directory.Exists(AutomationRootFolder))
        {
            StatusText = "The selected Auto Import Root Folder does not exist.";
            return;
        }
        if (SecureLanEnabled && !SecurityNetworkPolicy.TryResolvePrivateBindAddress(SecureLanBindAddress, out _, out var lanValidationError))
        {
            StatusText = lanValidationError;
            return;
        }
        if (ServerMode && !string.IsNullOrWhiteSpace(ServerRootFolder))
        {
            try
            {
                var root = Path.GetFullPath(ServerRootFolder.Trim());
                if (root.StartsWith("\\", StringComparison.Ordinal))
                {
                    StatusText = "The authoritative Main Server root must be on a local disk. Use External Backup Folder for NAS/network backup storage.";
                    return;
                }
                Directory.CreateDirectory(root);
            }
            catch (Exception exception)
            {
                StatusText = $"The Main Server root is invalid or cannot be created: {exception.Message}";
                return;
            }
        }
        if (PcExpiryDays is < 1 or > 365 || AndroidExpiryDays is < 1 or > 365 || FolderLinkExpiryDays is < 1 or > 365)
        {
            StatusText = "Device and folder inactivity expiry must be between 1 and 365 days.";
            return;
        }
        if (ServerMode && !string.IsNullOrWhiteSpace(ExternalBackupFolder))
        {
            try
            {
                var root = Path.GetFullPath(string.IsNullOrWhiteSpace(ServerRootFolder) ? Path.Combine(ApplicationDataPaths.RootFolder, "ServerAuthority") : ServerRootFolder.Trim());
                var backup = Path.GetFullPath(ExternalBackupFolder.Trim());
                if (root.Equals(backup, StringComparison.OrdinalIgnoreCase))
                {
                    StatusText = "External Backup Folder must be different from the Main Server root.";
                    return;
                }
            }
            catch (Exception exception)
            {
                StatusText = $"The external backup path is invalid: {exception.Message}";
                return;
            }
        }

        _settings.Set(ApplicationSettingKeys.StartWithWindows, StartWithWindows);
        _settings.Set(ApplicationSettingKeys.CloseToTray, CloseToTray);
        _settings.Set(ApplicationSettingKeys.NotificationsEnabled, NotificationsEnabled);
        _settings.Set(ApplicationSettingKeys.OpenLastClient, OpenLastClient);
        _settings.Set(ApplicationSettingKeys.AutomationEnabled, AutomationEnabled);
        _settings.Set(ApplicationSettingKeys.AutomationFolderName, AutomationFolderName.Trim());
        _settings.Set(ApplicationSettingKeys.AutomationRootMode, AutomationRootMode);
        _settings.Set(ApplicationSettingKeys.AutomationRootFolder, AutomationRootFolder.Trim());
        _settings.Set(ApplicationSettingKeys.AutomationScanExisting, ScanExistingFiles);
        _settings.Set(ApplicationSettingKeys.AutomationMonitorExcelCsv, MonitorExcelCsv);
        _settings.Set(ApplicationSettingKeys.AutomationMonitorPdf, MonitorPdf);
        _settings.Set(ApplicationSettingKeys.AutomationAutoImportKnown, AutoImportKnownFormats);
        _settings.Set(ApplicationSettingKeys.AutomationFileStableSeconds, FileStableSeconds);
        _settings.Set(ApplicationSettingKeys.NotifyImportComplete, NotifyImportComplete);
        _settings.Set(ApplicationSettingKeys.NotifyImportFailed, NotifyImportFailed);
        _settings.Set(ApplicationSettingKeys.NotifyActionRequired, NotifyActionRequired);
        _settings.Set(ApplicationSettingKeys.NotifyFolderRegistration, NotifyFolderRegistration);
        _settings.Set(ApplicationSettingKeys.NotifyTallyDisconnected, NotifyTallyDisconnected);
        _settings.Set(ApplicationSettingKeys.PdfPythonExecutable, PdfPythonExecutable.Trim());
        _settings.Set(ApplicationSettingKeys.PdfTimeoutMinutes, PdfTimeoutMinutes);
        _settings.Set(ApplicationSettingKeys.PdfDeleteGeneratedWorkbook, PdfDeleteGeneratedWorkbook);
        _settings.Set(ApplicationSettingKeys.BankOcrExternalRoot, BankOcrExternalRoot.Trim());
        _settings.Set(ApplicationSettingKeys.BankOcrTimeoutMinutes, BankOcrTimeoutMinutes);
        _settings.Set(ApplicationSettingKeys.ReviewExportOverwriteExisting, ReviewExportOverwriteExisting);
        _settings.Set(ApplicationSettingKeys.BankingAutoApplyExactMappings, BankingAutoApplyExactMappings);
        _settings.Set(ApplicationSettingKeys.BankingShowMappedDefault, BankingShowMappedDefault);
        _settings.Set(ApplicationSettingKeys.BankingOpeningBalanceMode, BankingOpeningBalanceMode);
        _settings.Set(ApplicationSettingKeys.SalesUseSavedMappings, SalesUseSavedMappings);
        _settings.Set(ApplicationSettingKeys.SalesSaveConfirmedMappings, SalesSaveConfirmedMappings);
        _settings.Set(ApplicationSettingKeys.PurchaseUseSavedMappings, PurchaseUseSavedMappings);
        _settings.Set(ApplicationSettingKeys.PurchaseSaveConfirmedMappings, PurchaseSaveConfirmedMappings);
        _settings.Set(ApplicationSettingKeys.PurchaseAutoImportKnown, PurchaseAutoImportKnown);
        _settings.Set(ApplicationSettingKeys.TallyHost, string.IsNullOrWhiteSpace(TallyHost) ? "127.0.0.1" : TallyHost.Trim());
        _settings.Set(ApplicationSettingKeys.TallyPort, TallyPort);
        _settings.Set(ApplicationSettingKeys.TallyAutoConnect, TallyAutoConnect);
        _settings.Set(ApplicationSettingKeys.TallySelectAllDefault, TallySelectAllDefault);
        _settings.Set(ApplicationSettingKeys.SecurityLanEnabled, SecureLanEnabled);
        _settings.Set(ApplicationSettingKeys.SecurityLanBindAddress, SecureLanBindAddress.Trim());
        _settings.Set(ApplicationSettingKeys.SecurityLanPort, SecureLanPort);
        _settings.Set(ApplicationSettingKeys.SecurityServerMode, ServerMode);
        _settings.Set(ApplicationSettingKeys.SecurityServerRootFolder, ServerRootFolder.Trim());
        _settings.Set(ApplicationSettingKeys.SecurityPcExpiryDays, PcExpiryDays);
        _settings.Set(ApplicationSettingKeys.SecurityAndroidExpiryDays, AndroidExpiryDays);
        _settings.Set(ApplicationSettingKeys.SecurityFolderLinkExpiryDays, FolderLinkExpiryDays);
        _settings.Set(ApplicationSettingKeys.SecurityRequireMfa, true);
        _settings.Set(ApplicationSettingKeys.SecurityExternalBackupFolder, ExternalBackupFolder.Trim());
        _settings.Set(ApplicationSettingKeys.SecurityNetworkClientPackagePath, NetworkClientPackagePath.Trim());
        _settings.Set(ApplicationSettingKeys.SecurityAndroidPackagePath, AndroidPackagePath.Trim());
        _settings.Set(ApplicationSettingKeys.Theme, Theme);
        _settings.Set(ApplicationSettingKeys.AccentColor, AccentColor.Trim());
        _settings.Set(ApplicationSettingKeys.ActionRequiredColor, ActionRequiredColor.Trim());
        _settings.Set(ApplicationSettingKeys.TallySelectedPeriodColor, TallySelectedPeriodColor.Trim());
        _settings.Set(ApplicationSettingKeys.GridDensity, GridDensity);
        _settings.Set(ApplicationSettingKeys.RememberWindowPlacement, RememberWindowPlacement);
        _settings.Set(ApplicationSettingKeys.ShortcutHelp, ShortcutHelp.Trim());
        _settings.Set(ApplicationSettingKeys.ShortcutRefresh, ShortcutRefresh.Trim());
        _settings.Set(ApplicationSettingKeys.ShortcutSave, ShortcutSave.Trim());
        _settings.Set(ApplicationSettingKeys.ShortcutClientSearch, ShortcutClientSearch.Trim());
        _settings.Set(ApplicationSettingKeys.ShortcutNewClient, ShortcutNewClient.Trim());

        StartupRegistrationService.Apply(StartWithWindows);
        AppearanceService.Apply(_settings);
        _automation.ReloadSettings();
        _ = ApplySecureLanSettingsAsync();
        StatusText = "Settings saved. Tally host/port changes apply fully on the next app start; appearance, folder automation and Main Server/LAN policy are applied now.";
    }

    private void SaveNetworkLocalSettings()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        try
        {
            var local = new NetworkLocalPreferences
            {
                StartWithWindows=StartWithWindows,CloseToTray=CloseToTray,NotificationsEnabled=NotificationsEnabled,
                MessageNotificationsEnabled=NetworkMessageNotificationsEnabled,WorkNotificationsEnabled=NetworkWorkNotificationsEnabled,
                Theme=Theme,AccentColor=AccentColor.Trim(),GridDensity=GridDensity,RememberWindowPlacement=RememberWindowPlacement,
                ShortcutHelp=ShortcutHelp.Trim(),ShortcutRefresh=ShortcutRefresh.Trim(),ShortcutSave=ShortcutSave.Trim(),
                ShortcutClientSearch=ShortcutClientSearch.Trim(),ShortcutNewClient=ShortcutNewClient.Trim()
            };
            NetworkLocalPreferenceService.Save(local);
            NetworkLocalPreferenceService.ApplyTo(_settings);
            StartupRegistrationService.Apply(StartWithWindows);
            AppearanceService.Apply(_settings);
            StatusText = "LAN local settings saved. These preferences are stored outside the disposable Main Server snapshot and survive data refreshes.";
        }
        catch(Exception exception){StatusText="LAN local settings could not be saved: "+exception.Message;}
    }

    private void CreateFolderHere()
    {
        // Apply the currently typed folder/root values first so this command and
        // the tray command always use the same Settings state.
        if (!string.IsNullOrWhiteSpace(AutomationFolderName))
            _settings.Set(ApplicationSettingKeys.AutomationFolderName, AutomationFolderName.Trim());
        _settings.Set(ApplicationSettingKeys.AutomationRootMode, AutomationRootMode);
        _settings.Set(ApplicationSettingKeys.AutomationRootFolder, AutomationRootFolder.Trim());
        _settings.Set(ApplicationSettingKeys.AutomationEnabled, AutomationEnabled);
        _automation.ReloadSettings();

        var result = _automation.CreateAutomationFolderHere();
        StatusText = result.WasSuccessful
            ? $"Automation Folder ready: {result.Path}"
            : result.Message;
    }

    private void LinkExistingFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the Automation Folder to link",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        // Explicit linking is intentionally independent of Explorer-window detection.
        // The service opens the Client/FY chooser through the normal registration event.
        var result = _automation.RequestFolderLink(dialog.FolderName);
        if (!result.WasSuccessful)
        {
            StatusText = result.Message;
            return;
        }

        AutomationEnabled = true;
        AutoImportKnownFormats = true;
        MonitorExcelCsv = true;
        MonitorPdf = true;
        ScanExistingFiles = true;
        StatusText = string.IsNullOrWhiteSpace(result.Message)
            ? $"Link window opened for: {result.Path}. Choose the existing Client/FY and click Save & Link Folder."
            : result.Message;
    }

    private void LinkRelinkSelectedFolder()
    {
        var row = SelectedLinkedFolderRow;
        if (row is null) return;

        var dialog = new OpenFolderDialog
        {
            Title = $"Select XYZ / Automation Folder for {row.ClientName} • FY {row.FinancialYear}",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            // Linking a Client/FY folder is an explicit unattended Auto Import instruction.
            // Persist the watcher/import switches immediately rather than waiting for a
            // separate Save click on the Settings page.
            _settings.Set(ApplicationSettingKeys.AutomationEnabled, true);
            _settings.Set(ApplicationSettingKeys.AutomationAutoImportKnown, true);
            _settings.Set(ApplicationSettingKeys.AutomationMonitorExcelCsv, true);
            _settings.Set(ApplicationSettingKeys.AutomationMonitorPdf, true);
            _settings.Set(ApplicationSettingKeys.AutomationScanExisting, true);
            _automation.ReloadSettings();
            var linked = _automation.RegisterFolderToExistingClient(dialog.FolderName, row.ClientId);
            AutomationEnabled = true;
            AutoImportKnownFormats = true;
            MonitorExcelCsv = true;
            MonitorPdf = true;
            ScanExistingFiles = true;
            LoadFolders();
            SelectedLinkedFolderRow = LinkedFolderRows.FirstOrDefault(item => item.ClientId == linked.ClientId && item.FinancialYear.Equals(linked.FinancialYear, StringComparison.OrdinalIgnoreCase));
            StatusText = $"Linked {linked.ClientName} • FY {linked.FinancialYear} to {linked.FolderPath}. The watcher is live and existing files are checked through the same Auto Import route.";
        }
        catch (Exception exception)
        {
            StatusText = $"Folder could not be linked: {exception.Message}";
        }
    }

    private void ChangeSelectedFolderFy()
    {
        var registration = SelectedLinkedFolderRow?.Registration;
        if (registration is null) return;
        var result = _automation.RequestFolderLink(registration.FolderPath);
        StatusText = result.WasSuccessful
            ? "Choose the correct existing Client / Financial Year for this folder and click Save & Link Folder. No accounting data is moved or deleted."
            : result.Message;
    }

    private void BrowseRootFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Select Auto Import Root Folder", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        AutomationRootFolder = dialog.FolderName;
        AutomationRootMode = "Selected Root";
    }

    private void BrowseBankOcrFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the existing PaddleOCR-VL 1.6 runtime/model folder",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        var info = BankAdvancedOcrRuntimeLocator.Inspect(dialog.FolderName);
        BankOcrExternalRoot = dialog.FolderName;
        if (!info.IsReady)
        {
            StatusText = info.FailureMessage;
            return;
        }

        BankOcrExternalRoot = info.RootFolder;
        OnPropertyChanged(nameof(ResolvedBankOcrRoot));
        OnPropertyChanged(nameof(BankOcrRuntimeStatus));
        StatusText = "Advanced Bank OCR engine folder selected. Click Save Settings to keep it.";
    }

    private void LoadNotificationHistory()
    {
        NotificationHistory.Clear();
        foreach (var row in NotificationHistoryService.Read()) NotificationHistory.Add(row);
    }

    private void ClearNotificationHistory()
    {
        if (MessageBox.Show("Clear Notification History? This does not change accounting data.", "Notification History", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        NotificationHistoryService.Clear();
        LoadNotificationHistory();
        StatusText = "Notification History cleared.";
    }

    private void LoadManualHolidays()
    {
        ManualHolidays.Clear();
        if (_clientContext.CurrentClient is { } client)
            foreach (var date in _clientSchedulePreferences.GetManualHolidays(client.Id, client.FinancialYear)) ManualHolidays.Add(date);
        OnPropertyChanged(nameof(HolidayClientCaption));
        AddManualHolidayCommand?.RaiseCanExecuteChanged();
        RemoveManualHolidayCommand?.RaiseCanExecuteChanged();
    }

    private void AddManualHoliday()
    {
        if (_clientContext.CurrentClient is not { } client) return;
        _clientSchedulePreferences.AddManualHoliday(client.Id, client.FinancialYear, NewManualHoliday);
        LoadManualHolidays();
        StatusText = $"Added {NewManualHoliday:dd-MMM-yyyy} to {client.ClientName}'s client-specific holiday calendar.";
    }

    private void RemoveManualHoliday()
    {
        if (_clientContext.CurrentClient is not { } client || SelectedManualHoliday is not DateTime date) return;
        _clientSchedulePreferences.RemoveManualHoliday(client.Id, client.FinancialYear, date);
        LoadManualHolidays();
        StatusText = $"Removed {date:dd-MMM-yyyy} from the client holiday calendar.";
    }

    private void OnPendingOtpRefreshTick(object? sender, EventArgs e) => RefreshPendingOtpText();

    private void RefreshPendingOtpText()
    {
        if (_secureLanServer is null) return;
        var pending = _secureLanServer.GetPendingMfaChallenges();
        var lastRequest = _secureLanServer.GetLatestMfaRequestStatus();
        PendingMfaText = pending.Count == 0
            ? $"No pending network login OTP.{Environment.NewLine}{lastRequest}"
            : string.Join(Environment.NewLine, pending.Select(x => $"{SecurityNetworkPolicy.GetOtpLabel(x.NetworkScope)} · {x.UserName} · {x.DeviceName} · OTP {x.Code} · expires {x.ExpiresUtc.ToLocalTime():HH:mm:ss}")) + Environment.NewLine + lastRequest;
    }

    private void RefreshSecurityDiagnostics()
    {
        LanBindAddressOptions.Clear();
        foreach (var address in SecurityNetworkPolicy.GetPrivateBindAddresses()) LanBindAddressOptions.Add(address);
        if (!string.IsNullOrWhiteSpace(SecureLanBindAddress) && !LanBindAddressOptions.Contains(SecureLanBindAddress)) LanBindAddressOptions.Add(SecureLanBindAddress);
        OnPropertyChanged(nameof(RemoteAssistInternetRouteText));
        SecurityDiagnosticsText = SecurityNetworkPolicy.BuildDiagnosticText(_settings) + Environment.NewLine +
                                  $"Runtime: {SecureLanServerStatus}";
        RefreshPendingOtpText();
        var forcedVersion = _settings.Get(ApplicationSettingKeys.SecurityForceWindowsUpdateVersion).Trim();
        if (forcedVersion.Length > 0) LanUpdatePolicyText = $"FORCED LAN update policy is active through Windows version {forcedVersion}. Outdated enrolled LAN applications cannot continue until updated.";
        OnPropertyChanged(nameof(SecureLanEndpoint));
        OnPropertyChanged(nameof(SecureLanServerStatus));
    }

    private void RefreshLanControlPanel()
    {
        try
        {
            var governance = new SqliteGovernanceRepository();
            governance.Initialize();
            var security = new ServerSecurityRepository();
            security.Initialize();
            var clientRepository = new SqliteClientRepository();
            clientRepository.Initialize();

            var now = DateTime.UtcNow;
            var users = governance.GetUsers();
            var credentials = security.GetCredentials().ToDictionary(x => x.UserName, StringComparer.OrdinalIgnoreCase);
            var devices = security.GetServerDevices();
            var sessions = security.GetSessions(1000);
            var activeSessions = sessions.Where(x => x.ExpiresUtc > now).ToList();
            var events = security.GetRecentSecurityEvents(500);
            var clients = clientRepository.GetAll(includeArchived: false);
            var deviceNames = devices.ToDictionary(x => x.DeviceId, x => x.DeviceName, StringComparer.OrdinalIgnoreCase);

            LanUsers.Clear();
            foreach (var user in users.OrderByDescending(x => x.IsActive).ThenBy(x => x.WindowsUserName, StringComparer.OrdinalIgnoreCase))
            {
                credentials.TryGetValue(user.WindowsUserName, out var credential);
                var userDevices = devices.Where(x => x.UserName.Equals(user.WindowsUserName, StringComparison.OrdinalIgnoreCase)).ToList();
                var userSessions = activeSessions.Where(x => x.UserName.Equals(user.WindowsUserName, StringComparison.OrdinalIgnoreCase)).ToList();
                var userEvents = events.Where(x => x.UserName.Equals(user.WindowsUserName, StringComparison.OrdinalIgnoreCase)).ToList();
                var lastSuccess = userEvents.FirstOrDefault(x => x.EventType.Equals("NetworkLogin", StringComparison.OrdinalIgnoreCase) && x.Success)?.CreatedUtc;
                var lastFailure = userEvents.FirstOrDefault(x => x.EventType.Equals("NetworkLogin", StringComparison.OrdinalIgnoreCase) && !x.Success)?.CreatedUtc;
                var latestEvent = userEvents.Select(x => (DateTime?)x.CreatedUtc).FirstOrDefault();
                var latestDevice = userDevices.Select(x => x.LastSeenUtc).Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(DateTime.MinValue).Max();
                var latestSession = userSessions.Select(x => x.LastSeenUtc).DefaultIfEmpty(DateTime.MinValue).Max();
                var lastActivity = new[] { user.LastSeenUtc ?? DateTime.MinValue, latestEvent ?? DateTime.MinValue, latestDevice, latestSession }.Max();

                LanUsers.Add(new LanUserActivityRow
                {
                    UserId = user.WindowsUserName,
                    DisplayName = user.DisplayName,
                    Role = user.RoleName,
                    Status = user.IsActive ? "Active" : "Disabled",
                    PasswordStatus = credential is not null && !string.IsNullOrWhiteSpace(credential.PasswordHash) && credential.PasswordIterations > 0 ? "Configured" : "Not configured",
                    FailedAttempts = credential?.FailedAttempts ?? 0,
                    LockoutUntil = FormatLocal(credential?.LockoutUntilUtc),
                    Created = FormatLocal(user.CreatedUtc),
                    LastLogin = FormatLocal(lastSuccess),
                    LastFailedLogin = FormatLocal(lastFailure),
                    LastActivity = FormatLocal(lastActivity == DateTime.MinValue ? null : lastActivity),
                    ActiveSessions = userSessions.Count,
                    EnrolledSystems = userDevices.Count,
                    Mfa = "Registration OTP once"
                });
            }

            LanSystems.Clear();
            foreach (var device in devices.OrderByDescending(x => x.LastSeenUtc ?? x.UpdatedUtc))
            {
                var assigned = new List<string>();
                var effective = new List<string>();
                foreach (var client in clients)
                {
                    if (!governance.HasExplicitClientAccess(device.UserName, client.Id, client.FinancialYear)) continue;
                    var lanOn = governance.IsClientLanAvailable(client.Id);
                    assigned.Add($"{client.ClientName} · FY {client.FinancialYear} · {(lanOn ? "LAN On" : "LAN Off")}");
                    if (lanOn) effective.Add(client.ClientName);
                }

                var deviceSessions = activeSessions.Where(x => x.DeviceId.Equals(device.DeviceId, StringComparison.OrdinalIgnoreCase)).ToList();
                var expiryDays = device.DeviceType.Equals(ServerDeviceTypes.Android, StringComparison.OrdinalIgnoreCase) ? AndroidExpiryDays : PcExpiryDays;
                var expiry = (device.LastSeenUtc ?? device.CreatedUtc).AddDays(expiryDays);
                LanSystems.Add(new LanSystemActivityRow
                {
                    DeviceName = device.DeviceName,
                    DeviceType = device.DeviceType,
                    UserId = device.UserName,
                    DeviceId = device.DeviceId,
                    Status = device.IsActive ? device.EnrollmentState : string.IsNullOrWhiteSpace(device.EnrollmentState) ? "Inactive" : device.EnrollmentState,
                    AppVersion = device.AppVersion,
                    Enrolled = FormatLocal(device.CreatedUtc),
                    LastSeen = FormatLocal(device.LastSeenUtc),
                    LastValidated = FormatLocal(device.LastValidatedUtc),
                    Expires = FormatLocal(expiry),
                    ActiveSessions = deviceSessions.Count,
                    AssignedClients = assigned.Count == 0 ? "None" : string.Join(Environment.NewLine, assigned),
                    EffectiveClientCount = effective.Count,
                    SnapshotBranch = effective.Count == 0 ? "Zero-client bootstrap" : $"Filtered snapshot ({effective.Count})",
                    RegistrationTrust = device.RegistrationOtpVerifiedUtc.HasValue ? $"Verified {FormatLocal(device.RegistrationOtpVerifiedUtc)}" : "OTP required"
                });
            }

            LanSecurityActivity.Clear();
            foreach (var activity in events.Take(250))
            {
                LanSecurityActivity.Add(new LanSecurityActivityRow
                {
                    Time = FormatLocal(activity.CreatedUtc),
                    UserId = activity.UserName,
                    System = deviceNames.TryGetValue(activity.DeviceId, out var deviceName) ? deviceName : activity.DeviceId,
                    Operation = activity.EventType,
                    Result = activity.Success ? "PASS" : "FAIL",
                    Details = activity.Detail
                });
            }

            var package = security.GetPackage("Windows");
            var processPath = _secureLanServer?.RuntimeExecutablePath ?? Environment.ProcessPath ?? AppContext.BaseDirectory;
            var runtimeVersion = _secureLanServer?.RuntimeVersion ?? typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(4) ?? "Unknown";
            var started = _secureLanServer?.RuntimeProcessStartedLocal ?? Process.GetCurrentProcess().StartTime;
            var dbPath = ApplicationDataPaths.DatabasePath;
            var dbExists = File.Exists(dbPath);
            var dbSize = dbExists ? new FileInfo(dbPath).Length : 0L;
            var walPath = dbPath + "-wal";
            var walSize = File.Exists(walPath) ? new FileInfo(walPath).Length : 0L;
            MainServerRuntimeText =
                $"Actual Main Server runtime version: {runtimeVersion}{Environment.NewLine}" +
                $"Process ID: {Process.GetCurrentProcess().Id}{Environment.NewLine}" +
                $"Executable hosting this Settings page: {processPath}{Environment.NewLine}" +
                $"Process started: {started:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                $"HTTPS endpoint: {SecureLanEndpoint}{Environment.NewLine}" +
                $"Runtime status: {SecureLanServerStatus}{Environment.NewLine}" +
                $"Authoritative database: {dbPath}{Environment.NewLine}" +
                $"Database exists: {dbExists} · Size: {dbSize:N0} bytes · WAL: {walSize:N0} bytes{Environment.NewLine}" +
                $"Server root: {_secureLanServer?.ServerRootFolder ?? ServerRootFolder}{Environment.NewLine}" +
                $"Approved Windows LAN package: {(package is null ? "Not registered" : $"{package.Version} · SHA-256 {package.Sha256}")}";

            var lastDiagnostic = _secureLanServer?.LastSnapshotDiagnostic;
            if (lastDiagnostic is not null)
                SnapshotDiagnosticText = lastDiagnostic.ToDisplayText();
            else
            {
                var lastSnapshotEvent = events.FirstOrDefault(x => x.EventType.Equals("NetworkReadOnlySnapshot", StringComparison.OrdinalIgnoreCase) || x.EventType.Equals("NetworkSnapshotDiagnostic", StringComparison.OrdinalIgnoreCase));
                SnapshotDiagnosticText = lastSnapshotEvent is null
                    ? "No snapshot request or manual snapshot diagnostic has been recorded in this Main Server database yet."
                    : $"Last recorded snapshot activity: {FormatLocal(lastSnapshotEvent.CreatedUtc)} · {(lastSnapshotEvent.Success ? "PASS" : "FAIL")}{Environment.NewLine}{lastSnapshotEvent.Detail}";
            }

            LanControlPanelStatus = $"Loaded {LanUsers.Count:N0} user(s), {LanSystems.Count:N0} enrolled system(s), {activeSessions.Count:N0} active network session(s) and {LanSecurityActivity.Count:N0} recent security event(s).";
        }
        catch (Exception exception)
        {
            LanControlPanelStatus = $"Control panel refresh failed: {exception.GetType().Name}: {exception.Message}";
            MainServerRuntimeText = LanControlPanelStatus;
        }
    }

    private void RequireSelectedLanRegistrationOtp()
    {
        if (SelectedLanSystem is null) return;
        try
        {
            var security = new ServerSecurityRepository();
            security.Initialize();
            var device = security.FindDeviceById(SelectedLanSystem.DeviceId);
            if (device is null)
            {
                StatusText = "The selected enrolled system was not found.";
                return;
            }
            security.RequireRegistrationOtp(device.Id);
            security.LogEvent("AdminDeviceLogout", Environment.UserName, device.DeviceId, "Administrator cleared trusted-device registration verification and all active sessions. The next LAN login requires one Registration OTP.", true);
            StatusText = $"{device.DeviceName}: signed out by Administrator. The next LAN login requires one Registration OTP; routine logins after that will not use OTP.";
            RefreshLanControlPanel();
        }
        catch (Exception exception)
        {
            StatusText = $"Could not reset registration verification: {exception.Message}";
        }
    }

    private async Task RunSnapshotDiagnosticAsync()
    {
        if (_secureLanServer is null)
        {
            SnapshotDiagnosticText = "The Main Server runtime is not available in this application session.";
            return;
        }

        try
        {
            SnapshotDiagnosticText = "Running the same server-side snapshot pipeline used by LAN Open AAH...";
            var result = await Task.Run(_secureLanServer.RunSnapshotDiagnostic).ConfigureAwait(true);
            SnapshotDiagnosticText = result.ToDisplayText();
            StatusText = result.Success
                ? $"Snapshot diagnostic passed through stage: {result.LastStage}."
                : $"Snapshot diagnostic failed at stage: {result.LastStage}. See Main Server Control Panel for the exact exception.";
            RefreshLanControlPanel();
        }
        catch (Exception exception)
        {
            SnapshotDiagnosticText = $"Snapshot diagnostic could not run: {exception.GetType().Name}: {exception.Message}";
            StatusText = "Snapshot diagnostic failed before completion.";
        }
    }

    private static string FormatLocal(DateTime? utc)
    {
        if (!utc.HasValue || utc.Value == DateTime.MinValue) return "—";
        var value = utc.Value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc) : utc.Value;
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    public void RefreshNetworkIdentityPresentation()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        OnPropertyChanged(nameof(NetworkUserSummary));
        OnPropertyChanged(nameof(NetworkPermissionSummary));
        OnPropertyChanged(nameof(NetworkSessionSummary));
        OnPropertyChanged(nameof(NetworkServerSummary));
        OnPropertyChanged(nameof(CanNetworkReadData));
        OnPropertyChanged(nameof(CanNetworkUpload));
        OnPropertyChanged(nameof(CanNetworkExport));
        OnPropertyChanged(nameof(NetworkAccessSummary));
        OnPropertyChanged(nameof(NetworkWorkspaceSummary));
        OnPropertyChanged(nameof(NetworkPolicySummary));
        OnPropertyChanged(nameof(NetworkRemoteAssistSummary));
    }

    private void LoadNetworkLocalSettings()
    {
        var local = NetworkLocalPreferenceService.Load();
        StartWithWindows = local.StartWithWindows;
        CloseToTray = local.CloseToTray;
        NotificationsEnabled = local.NotificationsEnabled;
        NetworkMessageNotificationsEnabled = local.MessageNotificationsEnabled;
        NetworkWorkNotificationsEnabled = local.WorkNotificationsEnabled;
        Theme = local.Theme;
        AccentColor = local.AccentColor;
        GridDensity = local.GridDensity;
        RememberWindowPlacement = local.RememberWindowPlacement;
        ShortcutHelp = local.ShortcutHelp;
        ShortcutRefresh = local.ShortcutRefresh;
        ShortcutSave = local.ShortcutSave;
        ShortcutClientSearch = local.ShortcutClientSearch;
        ShortcutNewClient = local.ShortcutNewClient;
    }

    private void OnNetworkMobileQrRefreshTick(object? sender, EventArgs e) => _ = RefreshNetworkMobileQrAsync();

    private void OnRemoteAssistConsentChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(NetworkRemoteAssistSummary));

    private void OpenRemoteAssistWorkspace()
    {
        OpenNetworkWorkspace("Remote Assist");
    }

    private void OpenNetworkWorkspace(object? parameter)
    {
        var title = parameter?.ToString()?.Trim() ?? string.Empty;
        if (title.Length > 0 && System.Windows.Application.Current?.MainWindow?.DataContext is MainWindowViewModel vm && vm.NavigateTo(title))
            StatusText = $"{title} workspace opened.";
        else
            StatusText = title.Length == 0 ? "A LAN workspace name is required." : $"{title} workspace is not available in this application mode.";
    }

    private async Task RefreshNetworkIdentityAsync()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        try
        {
            using var api = CreateNetworkApi();
            var status = await api.GetEnterpriseStatusAsync().ConfigureAwait(true);
            NetworkClientRuntime.ApplyIdentity(status.UserId, status.DisplayName, status.RoleName, NetworkClientRuntime.SessionExpiresUtc, status.Permissions);
            NetworkClientRuntime.AuthorizationRevision = status.Revisions.AuthorizationRevision;
            NetworkClientRuntime.DevicePolicyRevision = status.Revisions.DevicePolicyRevision;
            NetworkClientRuntime.ClientAccessRevision = status.Revisions.ClientAccessRevision;
            NetworkClientRuntime.SecurityPolicyRevision = status.Revisions.SecurityPolicyRevision;
            NetworkClientRuntime.DeviceHealthState = status.Posture.HealthState;
            NetworkClientRuntime.DeviceRiskScore = status.Posture.RiskScore;
            NetworkLocalIdentityService.ApplyToDisposableSnapshot();
            RefreshNetworkIdentityPresentation();
            if (System.Windows.Application.Current?.MainWindow?.DataContext is MainWindowViewModel vm) vm.ApplyNetworkIdentityRefresh();
            NetworkConnectionStatusText = $"Connected to {api.Server} · {NetworkClientRuntime.RoleName} · device health {status.Posture.HealthState} · risk {status.Posture.RiskScore}.";
            await RefreshNetworkMobileQrAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            NetworkConnectionStatusText = "Could not refresh Main Server identity: " + exception.Message;
        }
    }

    private async Task RefreshNetworkMobileQrAsync()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        try
        {
            using var api = CreateNetworkApi();
            var qr = await api.GetCurrentUserMobileCodeAsync().ConfigureAwait(true);
            NetworkMobileQrImage = LocalQrCodeService.CreateImage(qr.Payload);
            NetworkMobileQrText = $"{NetworkClientRuntime.DisplayName} · User ID {qr.UserId} · live code {qr.Code} · refreshes by {qr.ExpiresUtc.ToLocalTime():HH:mm:ss} or immediately after use. New phone: Main Server approval is required. Enrolled matching phone: scanning signs in automatically.";
        }
        catch (Exception exception)
        {
            NetworkMobileQrImage = null;
            NetworkMobileQrText = "Could not load the Main Server mobile code: " + exception.Message;
        }
    }

    private async Task TestNetworkConnectionAsync()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        try
        {
            using var api = CreateNetworkApi();
            var status = await api.GetEnterpriseStatusAsync().ConfigureAwait(true);
            NetworkConnectionStatusText = $"Main Server reachable at {api.Server} · signed in as {status.UserId} · role {status.RoleName} · health {status.Posture.HealthState}.";
            OnPropertyChanged(nameof(NetworkServerSummary));
        }
        catch (Exception exception) { NetworkConnectionStatusText = "Main Server connection failed: " + exception.Message; }
    }

    private async Task RefreshNetworkDataAsync()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        try
        {
            using var api = CreateNetworkApi();
            await api.DownloadReadOnlySnapshotAsync().ConfigureAwait(true);
            if (System.Windows.Application.Current?.MainWindow?.DataContext is MainWindowViewModel vm) vm.ApplyNetworkLiveSnapshotRefresh();
            StatusText = "Authorised Client/FY snapshot refreshed from the Main Server.";
        }
        catch (Exception exception) { StatusText = "Could not refresh authorised Main Server data: " + exception.Message; }
    }

    private FullUiNetworkClientApi CreateNetworkApi() => new()
    {
        Server = NetworkClientRuntime.ResolveServerEndpoint(),
        DeviceId = NetworkClientRuntime.DeviceId,
        DeviceToken = NetworkClientRuntime.DeviceToken,
        SessionToken = NetworkClientRuntime.SessionToken
    };

    private async Task CheckNetworkUpdateAsync()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        try
        {
            using var api = CreateNetworkApi();
            var status = await api.GetUpdateStatusAsync().ConfigureAwait(true);
            var ringText=$"Ring {status.UpdateRing} · {(status.UpdateRingEnabled?"Active":"Paused")}";
            var forceAfterText=status.UpdateForceAfterUtc.HasValue?$" · Force after {status.UpdateForceAfterUtc.Value.ToLocalTime():g}":string.Empty;
            NetworkUpdateStatusText = status.UpdateRequired
                ? $"Update available · Installed {status.CurrentVersion} · Main Server {status.ApprovedVersion} · {ringText}{forceAfterText}" + (status.ForceUpdate ? " · REQUIRED" : string.Empty)
                : $"LAN application is current · {status.CurrentVersion} · {ringText}{forceAfterText}.";
        }
        catch (Exception exception)
        {
            NetworkUpdateStatusText = $"Could not check Main Server update: {exception.Message}";
        }
    }

    private async Task InstallNetworkUpdateAsync()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        try
        {
            using var api = CreateNetworkApi();
            var status = await api.GetUpdateStatusAsync().ConfigureAwait(true);
            if (!status.UpdateRequired)
            {
                var forceAfterText=status.UpdateForceAfterUtc.HasValue?$" · Force after {status.UpdateForceAfterUtc.Value.ToLocalTime():g}":string.Empty;
                NetworkUpdateStatusText = $"LAN application is current · {status.CurrentVersion} · Ring {status.UpdateRing} · {(status.UpdateRingEnabled?"Active":"Paused")}{forceAfterText}.";
                return;
            }
            await NetworkClientSelfUpdateService.DownloadVerifyAndScheduleAsync(api, System.Windows.Application.Current?.MainWindow, status.ForceUpdate).ConfigureAwait(true);
            if (System.Windows.Application.Current is AccountingAutomationHub.Desktop.App app) app.ExitApplication(); else System.Windows.Application.Current?.Shutdown();
        }
        catch (Exception exception)
        {
            NetworkUpdateStatusText = $"LAN update failed: {exception.Message}";
        }
    }

    private async Task SignOutNetworkSessionAsync()
    {
        if (!NetworkClientRuntime.IsNetworkClient) return;
        try
        {
            using var api = CreateNetworkApi();
            await api.LogoutAsync().ConfigureAwait(true);
        }
        catch { }
        RemoteAssistPrivilegeContext.End();
        RemoteAssistConsentCenter.ClearAll();
        NetworkClientCredentialStore.DeleteSession();
        NetworkClientRuntime.ClearSession();
        if (System.Windows.Application.Current is AccountingAutomationHub.Desktop.App app) app.ExitApplication(); else System.Windows.Application.Current?.Shutdown();
    }

    private async Task ApplySecureLanSettingsAsync()
    {
        if (_secureLanServer is null)
        {
            RefreshSecurityDiagnostics();
            return;
        }
        await _secureLanServer.ApplySettingsAsync().ConfigureAwait(true);
        RefreshSecurityDiagnostics();
        RefreshLanControlPanel();
        StatusText = _secureLanServer.StatusText;
    }

    private void CreateLanUser()
    {
        try
        {
            var governance = new AccountingAutomationHub.Infrastructure.Governance.SqliteGovernanceRepository(); governance.Initialize();
            if (!governance.HasPermission(Environment.UserName, AccountingAutomationHub.Domain.Governance.GovernancePermissions.ManageUsers))
                throw new InvalidOperationException("Only an Administrator/Manage Users account can create or reset AAH users.");
            var dialog = new QuickLanUserWindow(_settings) { Owner = System.Windows.Application.Current?.MainWindow };
            if (dialog.ShowDialog() == true)
            {
                StatusText = "AAH User ID/password saved on the Main Server. Enroll trusted devices for this user, then grant Client/FY access once from Client Directory → User Access.";
                RefreshLanControlPanel();
            }
        }
        catch (Exception exception) { StatusText = $"Could not create AAH user: {exception.Message}"; }
    }

    private async void GenerateMobileEnrollmentQr()
    {
        try
        {
            if (_secureLanServer is null || SelectedLanUser is null) return;
            var selectedUser = SelectedLanUser;
            await _secureLanServer.EnsureRunningAsync();
            if (!ReferenceEquals(selectedUser, SelectedLanUser)) return;
            var result = _secureLanServer.GeneratePairingCode(SelectedLanUser.UserId, ServerDeviceTypes.Android);
            var payload = BuildMobileQrPayload("E", result.Code);
            MobileEnrollmentQrImage = LocalQrCodeService.CreateImage(payload);
            MobileEnrollmentQrText = $"FIRST-TIME ENROLLMENT QR · {result.BoundUserName} · role {SelectedLanUser.Role} · expires {result.ExpiresUtc.ToLocalTime():HH:mm:ss}. Scan once: the Android app verifies this Main Server certificate pin, enrolls the phone, consumes the QR and opens the signed-in session. No server address, enrollment code, password, CA installation or OTP entry is required on the phone.";
            LanPairingCodeText = $"One-time Android enrollment QR · {result.BoundUserName} · expires {result.ExpiresUtc.ToLocalTime():HH:mm:ss}.";
            StatusText = "One-time Android first-enrollment QR generated. It is invalid immediately after successful use.";
        }
        catch(Exception ex)
        {
            MobileEnrollmentQrImage=null; MobileEnrollmentQrText="Could not create mobile enrollment QR: "+ex.Message; StatusText=MobileEnrollmentQrText;
        }
    }

    private async void GenerateLanPairingCode()
    {
        if (_secureLanServer is null) return;
        try
        {
            await _secureLanServer.EnsureRunningAsync();
            var result = _secureLanServer.GeneratePairingCode();
            LanPairingCodeText = $"System enrollment code {result.Code} · expires {result.ExpiresUtc.ToLocalTime():HH:mm:ss}. One use only. After enrollment, complete one Registration OTP. OTP is not used for routine login; it is required again only after 30 days of inactivity or an Administrator registration reset.";
            StatusText = "LAN/Android system enrollment code created. Enrolling a system does not grant any client access; client availability and user access are controlled only from Client Directory.";
        }
        catch (Exception exception) { StatusText = $"Could not create LAN system enrollment code: {exception.Message}"; }
    }

    private void BrowseServerRootFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Select AAH Main Server root folder", Multiselect = false };
        if (dialog.ShowDialog() == true) ServerRootFolder = dialog.FolderName;
    }

    private void BrowseExternalBackupFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Select second/off-machine backup destination", Multiselect = false };
        if (dialog.ShowDialog() == true) ExternalBackupFolder = dialog.FolderName;
    }

    private void BrowsePackageFile(bool android)
    {
        var dialog = new OpenFileDialog
        {
            Title = android ? "Select approved Android APK" : "Select approved AAH Network Client package",
            Filter = android ? "Android APK (*.apk)|*.apk|All files (*.*)|*.*" : "AAH package (*.zip;*.exe;*.msi)|*.zip;*.exe;*.msi|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        if (android) AndroidPackagePath = dialog.FileName; else NetworkClientPackagePath = dialog.FileName;
    }

    private async void RunServerReplication()
    {
        if (_secureLanServer is null) return;
        try { StatusText = await _secureLanServer.RunServerReplicationNowAsync().ConfigureAwait(true); }
        catch (Exception exception) { StatusText = $"Server replication failed: {exception.Message}"; }
        RefreshSecurityDiagnostics();
    }

    private void CreateDistributionTicket(string kind)
    {
        if (_secureLanServer is null) return;
        try
        {
            var result = _secureLanServer.GenerateDistributionLink(kind);
            ServerDistributionText = $"{result.PackageKind} package · expires {result.ExpiresUtc.ToLocalTime():g} · SHA-256 {result.Sha256} · {result.Url}";
            Clipboard.SetText(result.Url);
            StatusText = $"One-use {kind} package download ticket created by the Main Server and copied to the clipboard.";
        }
        catch (Exception exception) { StatusText = $"Could not create {kind} package ticket: {exception.Message}"; }
    }

    private void PublishLanUpdate(bool force)
    {
        if (_secureLanServer is null) return;
        try
        {
            var result = _secureLanServer.PublishWindowsUpdate(force);
            LanUpdatePolicyText = force
                ? $"FORCED LAN update: Windows version {result.Version} · SHA-256 {result.Sha256}. Enrolled Windows LAN applications below this version are blocked until they update."
                : $"Optional LAN update published: Windows version {result.Version} · SHA-256 {result.Sha256}. AAH users on trusted Windows devices may install it from Update from Main Server.";
            StatusText = force ? "Forced LAN application update published. Outdated LAN PCs must update on next launch/heartbeat." : "LAN application update published as optional.";
        }
        catch (Exception exception) { StatusText = $"Could not publish LAN update: {exception.Message}"; }
    }

    private void OnMfaChallengeCreated(object? sender, SecureLanServerService.NetworkMfaChallengeEventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            var otpLabel = SecurityNetworkPolicy.GetOtpLabel(e.NetworkScope);
            PendingMfaText = $"{otpLabel} · {e.UserName} · {e.DeviceName} · OTP {e.Code} · expires {e.ExpiresUtc.ToLocalTime():HH:mm:ss}";
            StatusText = $"{otpLabel} {e.Code} requested by {e.UserName} on {e.DeviceName}.";
        }));
    }

    private void OpenNetworkClientPackageFolder()
    {
        var path = NetworkClientPackagePath.Trim();
        if (path.Length == 0)
            path = Path.Combine(AppContext.BaseDirectory, "NetworkClient", "AAHNetworkClient.zip");

        if (!File.Exists(path))
        {
            StatusText = "The bundled AAH Network Client package is not available yet. Run the latest cumulative update on the Main Server first.";
            return;
        }

        var folder = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        OpenPath(folder);
        StatusText = "Opened the Main Server LAN application package folder.";
    }

    private void OpenServerRootFolder()
    {
        var path = _secureLanServer?.ServerRootFolder ?? (ServerRootFolder.Trim().Length > 0 ? ServerRootFolder.Trim() : Path.Combine(ApplicationDataPaths.RootFolder, "ServerAuthority"));
        Directory.CreateDirectory(path); OpenPath(path);
    }


    private async void GenerateMobileLoginQr()
    {
        try
        {
            if (_secureLanServer is null || SelectedLanUser is null) return;
            var selectedUser = SelectedLanUser;
            await _secureLanServer.EnsureRunningAsync();
            if (!ReferenceEquals(selectedUser, SelectedLanUser)) return;
            var result = _secureLanServer.GenerateAndroidLoginQr(SelectedLanUser.UserId);
            var payload = BuildMobileQrPayload("L", result.Code);
            MobileEnrollmentQrImage = LocalQrCodeService.CreateImage(payload);
            MobileEnrollmentQrText = $"RETURNING LOGIN QR · {result.BoundUserName} · expires {result.ExpiresUtc.ToLocalTime():HH:mm:ss}. Only an already-enrolled Android device bound to this user can consume it. The QR is one-time and signs the phone in without password/OTP entry.";
            LanPairingCodeText = $"One-time Android login QR · {result.BoundUserName} · expires {result.ExpiresUtc.ToLocalTime():HH:mm:ss}.";
            StatusText = "One-time Android login QR generated. It expires in two minutes and is invalid immediately after successful use.";
        }
        catch (Exception ex)
        {
            MobileEnrollmentQrImage = null;
            MobileEnrollmentQrText = "Could not create Android login QR: " + ex.Message;
            StatusText = MobileEnrollmentQrText;
        }
    }

    private string BuildMobileQrPayload(string purpose, string code)
    {
        if (_secureLanServer is null) throw new InvalidOperationException("AAH Main Server is not available.");
        var endpoint = _secureLanServer.Endpoint.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        var pin = _secureLanServer.ServerCertificateQrPin;
        if (string.IsNullOrWhiteSpace(pin)) throw new InvalidOperationException("The Main Server TLS certificate pin is not available. Restart the Main Server and retry.");
        return $"AAH2|{endpoint}|{purpose}|{code}|{pin}";
    }

    private void OpenLanCertificateFolder()
    {
        var path = _secureLanServer?.PublicCertificatePath ?? Path.Combine(ApplicationDataPaths.RootFolder, "Security", "AAH-LAN-Root-CA.cer");
        var folder = Path.GetDirectoryName(path) ?? ApplicationDataPaths.RootFolder;
        OpenPath(folder);
        StatusText = "Opened the Security folder. Install/trust only the AAH Local LAN Root CA certificate on devices that you control.";
    }

    private void OnClientContextPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveClientContext.CurrentClient))
        {
            LoadManualHolidays();
            GenerateLanPairingCodeCommand.RaiseCanExecuteChanged();
        }
    }

    private void LoadAutoTallyImportClients()
    {
        AutoTallyImportClients.Clear();
        foreach (var client in _automation.GetLinkableClients().OrderBy(x => x.ClientName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.FinancialYear))
        {
            var row = new AutoTallyImportClientRow
            {
                ClientId = client.Id,
                ClientCode = client.ClientCode,
                ClientName = client.ClientName,
                FinancialYear = client.FinancialYear,
                IsEnabled = AutoTallyImportPreference.IsEnabled(_clientPreferenceRepository, client),
                AutoOpen = AutoTallyImportPreference.AutoOpen(_clientPreferenceRepository, client),
                AutoClose = AutoTallyImportPreference.AutoClose(_clientPreferenceRepository, client),
                ExactCompanyFolder = AutoTallyImportPreference.ExactCompanyFolder(_clientPreferenceRepository, client)
            };
            // Persist only on Save. An unfinished folder path must never become launch authority.
            row.Changed = null;
            AutoTallyImportClients.Add(row);
        }
        StatusText = $"Auto Tally Import settings loaded for {AutoTallyImportClients.Count:N0} Client/FY row(s).";
    }

    private void SaveAutoTallyImportClients()
    {
        var tallyLinks = new SqliteTallyRepository();
        tallyLinks.Initialize();
        // Fix10: Auto Open can be requested before the exact folder is learned.
        // Launch remains fail-closed until the live Tally path/CompanyKey is verified.
        // An old blank FY / company number must not cancel Save for ALL clients.
        foreach (var row in AutoTallyImportClients)
        {
            AutoTallyImportPreference.SetEnabled(_clientPreferenceRepository, row.ClientId, row.FinancialYear, row.IsEnabled);
            // Auto Open/Close belong only to outbound Automatic Tally Sync and
            // save immediately in the Dashboard. Never overwrite them from a stale
            // inbound Auto Tally Import settings grid.
            var confirmed = tallyLinks.GetLinkForClient(row.ClientId);
            var currentFolder = _clientPreferenceRepository.Get(row.ClientId,
                "Tally.AutoImport.ExactCompanyFolder." + row.FinancialYear);
            // The settings page may have been open BEFORE live Tally learned a new
            // path. Do not allow its stale empty editor to erase live authority.
            if (!string.IsNullOrWhiteSpace(row.ExactCompanyFolder) &&
                !row.ExactCompanyFolder.Equals(currentFolder, StringComparison.OrdinalIgnoreCase) &&
                confirmed is not null && confirmed.CompanyNumber.Length > 0)
            {
                try
                {
                    TallyPrivateCompanySessionService.VerifyCompanyFolder(row.ExactCompanyFolder, confirmed.CompanyNumber);
                    AutoTallyImportPreference.SetExactCompanyKey(_clientPreferenceRepository, row.ClientId, row.FinancialYear, string.Empty);
                    AutoTallyImportPreference.SetExactCompanyFolder(_clientPreferenceRepository, row.ClientId, row.FinancialYear, row.ExactCompanyFolder);
                    AutoTallyImportPreference.SetExactCompanyKey(_clientPreferenceRepository, row.ClientId, row.FinancialYear, confirmed.CompanyKey);
                }
                catch (Exception ex)
                {
                    StatusText = $"Saved other preferences; {row.ClientName} folder was not changed: {ex.Message}";
                }
            }
        }
        StatusText = $"Inbound Auto Tally Import settings saved for {AutoTallyImportClients.Count:N0} Client/FY row(s). Outbound Auto Open/Close are configured and saved immediately in Dashboard Automatic Tally Sync.";
    }

    private void LoadFolders()
    {
        var selectedClientId = SelectedLinkedFolderRow?.ClientId;
        var selectedFy = SelectedLinkedFolderRow?.FinancialYear ?? string.Empty;
        var registrations = _automation.GetFolders()
            .OrderByDescending(folder => folder.UpdatedUtc)
            .ToArray();

        AutomationFolders.Clear();
        foreach (var folder in registrations) AutomationFolders.Add(folder);

        LinkedFolderRows.Clear();
        foreach (var client in _automation.GetLinkableClients())
        {
            var matches = registrations
                .Where(folder => folder.ClientId == client.Id && folder.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(folder => folder.UpdatedUtc)
                .ToArray();
            var registration = matches.FirstOrDefault();
            LinkedFolderRows.Add(new AutomationFolderLinkRow
            {
                ClientId = client.Id,
                ClientCode = client.ClientCode,
                ClientName = client.ClientName,
                Gstin = client.Gstin,
                FinancialYear = client.FinancialYear,
                Registration = registration,
                RegistrationCount = matches.Length
            });
        }

        SelectedLinkedFolderRow = LinkedFolderRows.FirstOrDefault(row =>
                                      selectedClientId.HasValue && row.ClientId == selectedClientId.Value &&
                                      row.FinancialYear.Equals(selectedFy, StringComparison.OrdinalIgnoreCase))
                                  ?? LinkedFolderRows.FirstOrDefault();
        LoadAutomationHealth(updateStatus: false);
        var linked = LinkedFolderRows.Count(row => row.IsLinked);
        StatusText = $"{LinkedFolderRows.Count:N0} Client/FY row(s) shown • {linked:N0} linked • {LinkedFolderRows.Count - linked:N0} available to link.";
    }

    private void LoadAutomationHealth() => LoadAutomationHealth(updateStatus: true);

    private void LoadAutomationHealth(bool updateStatus)
    {
        AutomationHealth.Clear();
        foreach (var item in _automation.GetHealthSnapshots()) AutomationHealth.Add(item);
        var runtime = _automation.GetRuntimeHealth();
        AutomationHealthText = runtime.Summary +
                               (runtime.PdfRuntimeAvailable
                                   ? " PDF runtime is available."
                                   : $" PDF runtime is missing: {runtime.PdfRuntimePath}") +
                               $" Diagnostic log: {AutoImportLogPath}";
        if (updateStatus) StatusText = AutomationHealthText;
    }

    private void RepairAutoImport()
    {
        var queued = _automation.RepairAutoImportAndRescan();
        AutomationEnabled = true;
        AutoImportKnownFormats = true;
        MonitorExcelCsv = true;
        MonitorPdf = true;
        ScanExistingFiles = true;
        LoadAutomationHealth(updateStatus: false);
        StatusText = $"Auto Import repair completed and queued {queued:N0} file(s). Diagnostic log: {AutoImportLogPath}";
    }

    private void ForceScanAllFolders()
    {
        var queued = _automation.ForceScanAllFolders();
        LoadAutomationHealth(updateStatus: false);
        StatusText = queued == 0
            ? "Recovery scan completed. No new queue entries were needed; already imported file hashes remain protected."
            : $"Recovery scan queued {queued:N0} file(s) for classification/import.";
    }

    private void OpenSelectedFolder()
    {
        if (SelectedAutomationFolder is null) return;
        _automation.OpenFolder(SelectedAutomationFolder);
    }

    private void PauseResumeSelectedFolder()
    {
        if (SelectedAutomationFolder is null) return;
        _automation.SetFolderPaused(SelectedAutomationFolder, !SelectedAutomationFolder.IsPaused);
        LoadFolders();
    }

    private void ScanSelectedFolder()
    {
        if (SelectedAutomationFolder is null) return;
        _automation.ScanFolder(SelectedAutomationFolder);
        StatusText = $"Force scan queued for {SelectedAutomationFolder.ClientName} • FY {SelectedAutomationFolder.FinancialYear}. It uses the same watcher recognition/import path.";
    }

    private void UnlinkSelectedFolder()
    {
        if (SelectedAutomationFolder is null) return;
        var answer = MessageBox.Show(
            $"Unlink this Automation Folder from {SelectedAutomationFolder.ClientName}?\n\nThe Windows folder and accounting data will NOT be deleted.",
            "Unlink Automation Folder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _automation.UnlinkFolder(SelectedAutomationFolder);
        LoadFolders();
        StatusText = "Automation Folder unlinked. The physical folder and accounting data were preserved.";
    }

    private void RenameLinkedFolders()
    {
        var newName = AutomationFolderName.Trim();
        if (newName.Length == 0)
        {
            StatusText = "Enter the new Automation Folder Name first.";
            return;
        }
        var answer = MessageBox.Show(
            $"Rename only the registered Automation Folders to '{newName}'?\n\nThis does not rename unrelated folders with the same old name. Folder IDs and client/FY links are preserved.",
            "Rename Linked Automation Folders",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _settings.Set(ApplicationSettingKeys.AutomationFolderName, newName);
        var result = _automation.RenameLinkedFolders(newName);
        LoadFolders();
        StatusText = $"Renamed {result.Renamed:N0} linked folder(s); {result.Skipped:N0} skipped." +
                     (result.Issues.Count == 0 ? string.Empty : " " + string.Join(" | ", result.Issues.Take(3)));
    }

    private void ExportSettings()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Accounting Automation Hub Settings",
            Filter = "JSON settings (*.json)|*.json",
            FileName = $"Accounting_Automation_Hub_Settings_{DateTime.Now:yyyyMMdd}.json"
        };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_settings.ExportKnownSettings(), new JsonSerializerOptions { WriteIndented = true }));
        StatusText = $"Settings exported to {dialog.FileName}.";
    }

    private void ImportSettings()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Accounting Automation Hub Settings",
            Filter = "JSON settings (*.json)|*.json",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        var imported = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(dialog.FileName))
            ?? new Dictionary<string, string>();
        _settings.ImportKnownSettings(imported);
        LoadSettings();
        StartupRegistrationService.Apply(StartWithWindows);
        AppearanceService.Apply(_settings);
        _automation.ReloadSettings();
        _ = ApplySecureLanSettingsAsync();
        StatusText = "Settings imported and applied.";
    }

    private void RestoreDefaults()
    {
        if (MessageBox.Show("Restore all application settings to v4.0.0 defaults? Client/accounting data and folder registrations are not deleted.",
                "Restore Settings Defaults", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _settings.RestoreDefaults();
        LoadSettings();
        StartupRegistrationService.Apply(StartWithWindows);
        AppearanceService.Apply(_settings);
        _automation.ReloadSettings();
        _ = ApplySecureLanSettingsAsync();
        StatusText = "Default settings restored. Accounting data and Automation Folder links were preserved.";
    }

    private static void OpenPath(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
    }

    private void OnFoldersChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) LoadFolders();
        else dispatcher.BeginInvoke(new Action(LoadFolders));
    }

    private void OnAutomationProgressChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (System.Threading.Interlocked.Exchange(ref _automationHealthRefreshPending, 1) == 1) return;

        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                LoadAutomationHealth(updateStatus: false);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _automationHealthRefreshPending, 0);
            }
        }));
    }

    private void LoadFirmProfiles()
    {
        FirmProfiles.Clear();
        try
        {
            var rows = JsonSerializer.Deserialize<List<FirmProfileRow>>(_settings.Get(FirmProfilesPreferenceKey)) ?? [];
            var migratedIdentity = false;
            foreach (var row in rows.Where(x => !string.IsNullOrWhiteSpace(x.FirmName)).OrderBy(x => x.FirmName, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(row.FirmId)) { row.FirmId = Guid.NewGuid().ToString("N"); migratedIdentity = true; }
                FirmProfiles.Add(row);
            }
            if (migratedIdentity) _settings.Set(FirmProfilesPreferenceKey, JsonSerializer.Serialize(rows));
        }
        catch { }
        SelectedFirmProfile = FirmProfiles.FirstOrDefault();
    }

    private void AddFirmProfile()
    {
        var row = new FirmProfileRow { FirmId = Guid.NewGuid().ToString("N"), FirmName = "New Firm" };
        FirmProfiles.Add(row);
        SelectedFirmProfile = row;
        StatusText = "Enter the firm details, then click Save Firm Details.";
    }

    private void DeleteFirmProfile()
    {
        if (SelectedFirmProfile is null) return;
        var firmId = SelectedFirmProfile.FirmId;
        FirmProfiles.Remove(SelectedFirmProfile);
        foreach (var ca in CaProfiles.Where(x => x.FirmProfileId.Equals(firmId, StringComparison.OrdinalIgnoreCase)))
            ca.FirmProfileId = string.Empty;
        SelectedFirmProfile = FirmProfiles.FirstOrDefault();
        SaveFirmProfiles();
        SaveCaProfiles();
    }

    private void SaveFirmProfiles()
    {
        var cleaned = FirmProfiles.Where(x => !string.IsNullOrWhiteSpace(x.FirmName)).ToList();
        foreach (var row in cleaned) if (string.IsNullOrWhiteSpace(row.FirmId)) row.FirmId = Guid.NewGuid().ToString("N");
        _settings.Set(FirmProfilesPreferenceKey, JsonSerializer.Serialize(cleaned));
        ApplyFirmDetailsToCaProfiles();
        _settings.Set(CaProfilesPreferenceKey, JsonSerializer.Serialize(CaProfiles.Where(x => !string.IsNullOrWhiteSpace(x.CaName)).ToList()));
        StatusText = $"Saved {cleaned.Count} firm profile(s). Linked CA profiles inherit the saved firm details.";
    }

    private void ApplyFirmDetailsToCaProfiles()
    {
        var byId = FirmProfiles.Where(x => !string.IsNullOrWhiteSpace(x.FirmId)).ToDictionary(x => x.FirmId, StringComparer.OrdinalIgnoreCase);
        foreach (var ca in CaProfiles)
        {
            if (!byId.TryGetValue(ca.FirmProfileId ?? string.Empty, out var firm)) continue;
            ca.FirmName = firm.FirmName;
            ca.FirmRegistrationNumber = firm.FirmRegistrationNumber;
            ca.Place = firm.Place;
            ca.Address = firm.Address;
            ca.Phone = firm.Phone;
            ca.Email = firm.Email;
        }
    }

    private void LoadCaProfiles()
    {
        CaProfiles.Clear();
        try
        {
            var rows = JsonSerializer.Deserialize<List<CaProfileRow>>(_settings.Get(CaProfilesPreferenceKey)) ?? [];
            var migratedIdentity = false;
            foreach (var row in rows.OrderBy(x => x.FirmName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.CaName, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(row.ProfileId)) { row.ProfileId = Guid.NewGuid().ToString("N"); migratedIdentity = true; }
                CaProfiles.Add(row);
            }
            ApplyFirmDetailsToCaProfiles();
            if (migratedIdentity) _settings.Set(CaProfilesPreferenceKey, JsonSerializer.Serialize(rows));
        }
        catch { }
        SelectedCaProfile = CaProfiles.FirstOrDefault();
    }

    private void AddCaProfile()
    {
        var row = new CaProfileRow { ProfileId = Guid.NewGuid().ToString("N"), CaName = "New CA", Designation = "Chartered Accountant", FirmProfileId = SelectedFirmProfile?.FirmId ?? string.Empty };
        CaProfiles.Add(row);
        SelectedCaProfile = row;
        StatusText = "Enter the CA / audit firm details, then click Save CA Details.";
    }

    private void DeleteCaProfile()
    {
        if (SelectedCaProfile is null) return;
        CaProfiles.Remove(SelectedCaProfile);
        SelectedCaProfile = CaProfiles.FirstOrDefault();
        SaveCaProfiles();
    }

    private void SaveCaProfiles()
    {
        ApplyFirmDetailsToCaProfiles();
        var cleaned = CaProfiles.Where(x => !string.IsNullOrWhiteSpace(x.CaName)).ToList();
        foreach (var row in cleaned) if (string.IsNullOrWhiteSpace(row.ProfileId)) row.ProfileId = Guid.NewGuid().ToString("N");
        _settings.Set(CaProfilesPreferenceKey, JsonSerializer.Serialize(cleaned));
        StatusText = $"Saved {cleaned.Count} CA profile(s), grouped by their linked Firm Details. Financial Statements and CA client folders can reuse them.";
    }

    public void Dispose()
    {
        if (NetworkClientRuntime.IsNetworkClient) RemoteAssistConsentCenter.Changed -= OnRemoteAssistConsentChanged;
        _automation.FoldersChanged -= OnFoldersChanged;
        _automation.ProgressChanged -= OnAutomationProgressChanged;
        _clientContext.PropertyChanged -= OnClientContextPropertyChanged;
        if (_pendingOtpRefreshTimer is not null)
        {
            _pendingOtpRefreshTimer.Stop();
            _pendingOtpRefreshTimer.Tick -= OnPendingOtpRefreshTick;
        }
        if (_networkMobileQrRefreshTimer is not null)
        {
            _networkMobileQrRefreshTimer.Stop();
            _networkMobileQrRefreshTimer.Tick -= OnNetworkMobileQrRefreshTick;
        }
        if (_secureLanServer is not null) _secureLanServer.MfaChallengeCreated -= OnMfaChallengeCreated;
    }
}

public sealed class AutomationFolderLinkRow
{
    public long ClientId { get; set; }
    public string ClientCode { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public AutomationFolderRegistration? Registration { get; set; }
    public int RegistrationCount { get; set; }
    public bool IsLinked => Registration is not null;
    public string LinkStatus => Registration is null ? "Not linked" : RegistrationCount > 1 ? $"Linked • {RegistrationCount} registrations" : "Linked";
    public string FolderPath => Registration?.FolderPath ?? string.Empty;
    public bool IsPaused => Registration?.IsPaused ?? false;
    public string LastStatus => Registration?.LastStatus ?? string.Empty;
    public DateTime? LastImportUtc => Registration is null || Registration.LastImportUtc == default ? null : Registration.LastImportUtc;
}

public sealed class LanUserActivityRow
{
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string PasswordStatus { get; set; } = string.Empty;
    public string Mfa { get; set; } = string.Empty;
    public int FailedAttempts { get; set; }
    public string LockoutUntil { get; set; } = string.Empty;
    public string Created { get; set; } = string.Empty;
    public string LastLogin { get; set; } = string.Empty;
    public string LastFailedLogin { get; set; } = string.Empty;
    public string LastActivity { get; set; } = string.Empty;
    public int ActiveSessions { get; set; }
    public int EnrolledSystems { get; set; }
}

public sealed class LanSystemActivityRow
{
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string Enrolled { get; set; } = string.Empty;
    public string LastSeen { get; set; } = string.Empty;
    public string LastValidated { get; set; } = string.Empty;
    public string Expires { get; set; } = string.Empty;
    public int ActiveSessions { get; set; }
    public string AssignedClients { get; set; } = string.Empty;
    public int EffectiveClientCount { get; set; }
    public string SnapshotBranch { get; set; } = string.Empty;
    public string RegistrationTrust { get; set; } = string.Empty;
}

public sealed class LanSecurityActivityRow
{
    public string Time { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}



public sealed class AutoTallyImportClientRow : ViewModelBase
{
    private bool _isEnabled = true;
    private bool _autoOpen;
    private bool _autoClose;
    private string _exactCompanyFolder = string.Empty;
    public bool AutoOpen { get => _autoOpen; set => SetProperty(ref _autoOpen, value); }
    public bool AutoClose { get => _autoClose; set => SetProperty(ref _autoClose, value); }
    public string ExactCompanyFolder { get => _exactCompanyFolder; set => SetProperty(ref _exactCompanyFolder, value); }
    public long ClientId { get; init; }
    public string ClientCode { get; init; } = string.Empty;
    public string ClientName { get; init; } = string.Empty;
    public string FinancialYear { get; init; } = string.Empty;
    public Action<AutoTallyImportClientRow>? Changed { get; set; }
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value)) return;
            Changed?.Invoke(this);
        }
    }
}
