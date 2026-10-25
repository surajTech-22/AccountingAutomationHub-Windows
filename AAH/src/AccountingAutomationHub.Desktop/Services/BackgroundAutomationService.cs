using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Application.Imports;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Domain.Automation;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Imports;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Infrastructure.Automation;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Finance;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Imports;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Gst;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Persistence;
using AccountingAutomationHub.Infrastructure.Sales;
using AccountingAutomationHub.Infrastructure.Spreadsheets;
using AccountingAutomationHub.Infrastructure.Tally;
using AccountingAutomationHub.Infrastructure.Tax;

namespace AccountingAutomationHub.Desktop.Services;

public sealed class BackgroundAutomationService : IDisposable
{
    private const string MarkerFileName = ".aah-folder";
    // FIXED28: watcher/recovery work must stay bounded for long-running AAH sessions.
    private const int MaxAutomaticQueueDepth = 4096;
    private const int MaxSalesPurchaseLiveQueueDepth = 4096;
    private const int MaxRelocationDirectories = 500;
    private const int MaxRelocationDepth = 4;
    private static readonly TimeSpan FolderRelocationDiscoveryInterval = TimeSpan.FromMinutes(2);

    private readonly ApplicationPreferenceService _settings;
    private readonly ClientAutoImportPolicyService _clientAutoImportPolicies = new();
    private readonly SqliteAutomationRepository _automationRepository = new();
    private readonly SqliteClientRepository _clientRepository = new();
    private readonly SqliteBankingRepository _bankingRepository = new();
    private readonly SqliteMasterRepository _masterRepository = new();
    private TallyBankStatementReconciliationService? _tallyBankStatementReconciliationService;
    private Func<bool>? _isTallyConnectedProvider;
    private readonly SqliteStockMasterRepository _stockMasterRepository = new();
    private readonly SqliteActionRequiredRepository _actionRepository = new();
    private readonly SqliteSalesMappingRepository _salesMappingRepository = new();
    private readonly SqlitePurchaseMappingRepository _purchaseMappingRepository = new();
    private readonly SqliteSalesRecordRepository _salesRecordRepository = new();
    private readonly SqlitePurchaseRecordRepository _purchaseRecordRepository = new();
    private readonly SqliteJournalRepository _journalRepository = new();
    private readonly SqliteGstRepository _gstRepository = new();
    private readonly SqliteTaxCreditRepository _taxCreditRepository = new();
    private readonly Form26AsPdfImportService _form26AsImportService;
    private readonly GstinProfileLookupService _gstinProfileLookupService = new();
    private readonly Gstr3bPdfImportService _gstr3bPdfImportService;
    private readonly GstLedgerImportService _gstLedgerImportService;
    private readonly GstPortalAutoImportService _gstPortalAutoImportService;
    private readonly PurchaseAutoImportService _purchaseAutoImportService = new();
    private readonly SalesAutoImportService _salesAutoImportService = new();
    private AccountingRefreshCoordinator? _accountingRefreshCoordinator;
    private readonly AutomationFileClassifier _fileClassifier = new();
    private readonly SqliteImportActivityRepository _activityRepository = new();
    private readonly SqliteFinanceControlRepository _financeRepository = new();
    private readonly SqliteGovernanceRepository _governanceRepository = new();
    private readonly UniversalCashJournalImportService _universalCashJournalImporter;
    private readonly BankSuspenseWorkbookService _bankSuspenseWorkbookService;

    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _queuedKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingRegistrations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _dismissedRegistrations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _generatedWorkbookPaths =
        new(StringComparer.OrdinalIgnoreCase);
    // R52 FIXED26: a linked folder can be temporarily quiesced while its directory
    // is being renamed/rebased. New watcher observations are ignored during that
    // short window and a reconciliation scan resumes from the final path afterward.
    private readonly ConcurrentDictionary<string, byte> _folderMoveQuiesce =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _observedFileStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _reviewWorkbookSyncVersions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _reviewWorkbookObservedStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, QueuedAutomationFile> _activeFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AutomationActiveStage> _activeStages =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _gstBootstrapInFlight =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AutomationBatchCounter> _batchProgress =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<QueuedAutomationFile> _queue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly SemaphoreSlim _bankImportGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _bankAccountImportGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<BankCachedWorkItem>> _bankCachedQueues =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _bankCachedProcessors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _bankCachedLastEnqueueTicks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _pdfInteractiveGate = new(1, 1);
    private readonly SemaphoreSlim _spreadsheetPasswordGate = new(1, 1);
    private readonly SemaphoreSlim _bankOcrAutoBatchGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _bankOcrBatchHandled =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _clientImportGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _processedCustomerMasterHashes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lifecycleGate = new();

    // Fix38: one application-wide Sales/Purchase discovery coordinator watches shared
    // source locations once. It only indexes/classifies and routes to the existing Auto
    // Import queue; it never writes accounting rows itself.
    private readonly object _salesPurchaseWatcherGate = new();
    private readonly List<FileSystemWatcher> _salesPurchaseSourceWatchers = [];
    private readonly ConcurrentDictionary<string, string> _salesPurchaseObservedStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _salesPurchaseSourceInFlight =
        new(StringComparer.OrdinalIgnoreCase);
    // FIXED28: FileSystemWatcher bursts are queued into a small fixed worker pool instead
    // of creating one Task.Run per unique Sales/Purchase path. This keeps Downloads /
    // E-returns event storms bounded while retaining the same source-first index route.
    private readonly ConcurrentQueue<string> _salesPurchaseLiveQueue = new();
    private readonly ConcurrentDictionary<string, byte> _salesPurchaseLiveQueuedKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _salesPurchaseLiveQueueSignal = new(0);
    private Task[] _salesPurchaseLiveWorkerTasks = [];
    private readonly ConcurrentDictionary<string, long> _salesPurchaseSetVersions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _salesPurchaseSetProcessors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _salesPurchaseIndexGate = new(Math.Clamp(Environment.ProcessorCount / 4, 1, 3));
    private readonly object _salesPurchaseClientSnapshotGate = new();
    private ClientProfile[] _salesPurchaseActiveClientSnapshot = [];
    private DateTime _salesPurchaseActiveClientSnapshotUtc = DateTime.MinValue;
    private Func<bool>? _localPreparationReadyProvider;
    private Task? _salesPurchaseStartupScanTask;
    private volatile bool _centralSourceStartupComplete;
    private volatile bool _startupRecoveryComplete;
    private readonly ConcurrentDictionary<string, AutomationDashboardOutput> _dashboardOutputs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _dashboardOutputVersions =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _lifetime;
    private Task[] _workerTasks = [];
    private Task? _reconciliationTask;
    private Task? _downloadsMaintenanceTask;
    private Task? _startupRecoveryTask;
    private Thread? _explorerThread;
    private bool _started;
    private bool _globalPause;
    private string _lastActiveExplorerPath = string.Empty;
    private DateTime _lastStorageMaintenanceUtc = DateTime.MinValue;
    private DateTime _lastFolderRelocationDiscoveryUtc = DateTime.MinValue;
    private DateTime _lastObservedStatePruneUtc = DateTime.MinValue;
    private DateTime _lastSalesPurchaseObservedStatePruneUtc = DateTime.MinValue;
    private int _folderRelocationDiscoveryInFlight;

    public BackgroundAutomationService(ApplicationPreferenceService settings)
    {
        _settings = settings;
        _automationRepository.Initialize();
        _clientRepository.Initialize();
        _bankingRepository.Initialize();
        _masterRepository.Initialize();
        _stockMasterRepository.Initialize();
        _actionRepository.Initialize();
        _salesMappingRepository.Initialize();
        _purchaseMappingRepository.Initialize();
        _salesRecordRepository.Initialize();
        _purchaseRecordRepository.Initialize();
        _journalRepository.Initialize();
        _gstRepository.Initialize();
        _taxCreditRepository.Initialize();
        _form26AsImportService = new Form26AsPdfImportService(_taxCreditRepository);
        _gstinProfileLookupService.Initialize();
        _gstr3bPdfImportService = new Gstr3bPdfImportService(_gstRepository);
        _gstLedgerImportService = new GstLedgerImportService(_gstRepository);
        _gstPortalAutoImportService = new GstPortalAutoImportService(
            _gstRepository,
            _gstr3bPdfImportService,
            _gstLedgerImportService);
        _activityRepository.Initialize();
        _financeRepository.Initialize();
        _governanceRepository.Initialize();
        _universalCashJournalImporter = new UniversalCashJournalImportService(_bankingRepository, _masterRepository, _journalRepository);
        _bankSuspenseWorkbookService = new BankSuspenseWorkbookService(
            _bankingRepository,
            _masterRepository,
            new BankLedgerMappingService(_bankingRepository, _masterRepository, _governanceRepository));
        AutomationDiagnosticLog.Write("SERVICE", "Background Automation Service constructed and repositories initialized.");
    }

    public void AttachTallyBankStatementReconciliationService(TallyBankStatementReconciliationService service)
    {
        _tallyBankStatementReconciliationService = service ?? throw new ArgumentNullException(nameof(service));
    }

    public void AttachTallyConnectionStateProvider(Func<bool> provider)
    {
        _isTallyConnectedProvider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public void AttachLocalPreparationReadyProvider(Func<bool> provider)
    {
        _localPreparationReadyProvider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public void AttachAccountingRefreshCoordinator(AccountingRefreshCoordinator coordinator)
    {
        _accountingRefreshCoordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    private bool IsLocalPreparationReady()
    {
        // Fix38 startup order is explicit: until MainWindow attaches the workspace-cache
        // readiness provider, local preparation is NOT considered ready. This prevents the
        // Auto Import historical recovery/shared-source scan from racing the first cache pass.
        if (_localPreparationReadyProvider is null) return false;
        try { return _localPreparationReadyProvider.Invoke(); }
        catch { return false; }
    }

    private bool IsTallyCurrentlyConnected()
    {
        try { return _isTallyConnectedProvider?.Invoke() == true; }
        catch { return false; }
    }

    public event EventHandler<AutomationFolderRegistrationRequestEventArgs>? FolderRegistrationRequested;
    public event EventHandler<AutomationNotificationEventArgs>? NotificationRequested;
    public event EventHandler? FoldersChanged;
    public event EventHandler? ClientRegistryChanged;
    public event EventHandler? ActionRequiredChanged;
    public event EventHandler? AccountingDataChanged;
    public event EventHandler<AutomationAccountingDataChangedEventArgs>? AccountingDataChangedDetailed;
    public event EventHandler<ClientReviewWorkbookChangedEventArgs>? ClientReviewWorkbookChanged;
    public event EventHandler<PortalCredentialsChangedEventArgs>? PortalCredentialsChanged;
    public event EventHandler? ProgressChanged;

    public bool IsPaused => _globalPause;

    // Fix38 local phase completion authority used by the separate Tally scheduler.
    // Connection health may still pulse, but heavy Tally work waits until startup
    // source recovery has finished and the initial Auto Import queue is drained.
    // FIXED41 R5: this is the one-time startup boundary for Tally-last sequencing.
    // It deliberately excludes the continuously changing runtime Auto Import/Bank queues:
    // those queues may refill forever after startup and therefore cannot be the release condition.
    // Unlike the R3 shortcut, both central source discovery and startup recovery must actually
    // finish before the five-second Tally authority loop is released.
    public bool IsInitialLocalPreparationComplete =>
        _centralSourceStartupComplete &&
        _startupRecoveryComplete;

    public bool IsLocalPreparationComplete =>
        IsInitialLocalPreparationComplete &&
        _salesPurchaseSourceInFlight.IsEmpty &&
        _salesPurchaseSetProcessors.IsEmpty &&
        _gstBootstrapInFlight.IsEmpty &&
        _queue.IsEmpty &&
        _activeFiles.IsEmpty &&
        _bankCachedProcessors.IsEmpty &&
        _bankCachedQueues.Values.All(queue => queue.IsEmpty);

    public AutomationDashboardOutput GetDashboardOutput(long clientId, string financialYear) =>
        _dashboardOutputs.TryGetValue(ProgressKey(clientId, financialYear), out var output)
            ? output
            : AutomationDashboardOutput.Empty(clientId, financialYear);

    public IReadOnlyList<AutomationFolderRegistration> GetFolders() =>
        _automationRepository.GetFolders()
            .Where(folder => !folder.FolderId.StartsWith("SYSTEM-DUMP-", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public IReadOnlyList<AutomationFileHistoryRecord> GetClientHistory(long clientId, string financialYear, int limit = 100) =>
        _automationRepository.GetHistory(clientId, financialYear, limit);

    public AutomationProgressSnapshot GetClientProgress(long clientId, string financialYear)
    {
        var key = ProgressKey(clientId, financialYear);
        _batchProgress.TryGetValue(key, out var counter);
        var total = counter is null ? 0 : Volatile.Read(ref counter.Total);
        var completed = counter is null ? 0 : Math.Min(total, Volatile.Read(ref counter.Completed));
        var active = _activeFiles.Values.Count(item => item.ClientId == clientId && item.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase));
        var queuedItems = _queue
            .Where(item => item.ClientId == clientId && item.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var queued = queuedItems.Length;
        var pending = Math.Max(queued + active, total - completed);
        var percent = total <= 0 ? 0d : Math.Clamp(completed * 100d / total, 0d, 100d);

        var current = _activeStages.Values
            .Where(item => item.ClientId == clientId && item.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.UpdatedUtc)
            .FirstOrDefault();
        var currentStage = current?.Stage ?? (queued > 0 ? "Waiting for worker" : string.Empty);
        var currentFile = current?.FileName ?? (queuedItems.FirstOrDefault() is { } first ? Path.GetFileName(first.Path) : string.Empty);

        return new AutomationProgressSnapshot(
            total,
            completed,
            pending,
            active,
            queued,
            percent,
            pending > 0,
            currentStage,
            currentFile);
    }

    public IReadOnlyList<AutomationHealthSnapshot> GetHealthSnapshots()
    {
        var queuedByFolder = _queue
            .GroupBy(item => item.FolderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var activeByFolder = _activeFiles.Values
            .GroupBy(item => item.FolderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return _automationRepository.GetFolders()
            .Select(folder =>
            {
                var exists = Directory.Exists(folder.FolderPath);
                var watcher = _watchers.ContainsKey(folder.FolderId);
                queuedByFolder.TryGetValue(folder.FolderId, out var queued);
                activeByFolder.TryGetValue(folder.FolderId, out var active);
                var current = _activeStages.Values
                    .Where(item => item.FolderId.Equals(folder.FolderId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.UpdatedUtc)
                    .FirstOrDefault();
                var queuedFile = _queue.FirstOrDefault(item =>
                    item.FolderId.Equals(folder.FolderId, StringComparison.OrdinalIgnoreCase));
                var stage = current?.Stage ?? (queued > 0 ? "Waiting for worker" : string.Empty);
                var fileName = current?.FileName ?? (queuedFile is null ? string.Empty : Path.GetFileName(queuedFile.Path));
                var status = folder.IsPaused ? "Paused"
                    : !exists ? "Folder unavailable"
                    : !_settings.GetBool(ApplicationSettingKeys.AutomationEnabled) ? "Automation disabled"
                    : _globalPause ? "Globally paused"
                    : active > 0 ? "Processing"
                    : queued > 0 ? "Queued"
                    : watcher ? "Watching" : "Recovering watcher";
                return new AutomationHealthSnapshot
                {
                    FolderId = folder.FolderId,
                    ClientName = folder.ClientName,
                    FinancialYear = folder.FinancialYear,
                    FolderPath = folder.FolderPath,
                    FolderAvailable = exists,
                    WatcherActive = watcher,
                    FolderPaused = folder.IsPaused,
                    ActiveFileCount = active,
                    QueuedFileCount = queued,
                    CurrentStage = stage,
                    CurrentFileName = fileName,
                    Status = status,
                    CheckedUtc = DateTime.UtcNow
                };
            })
            .ToArray();
    }

    public AutomationRuntimeHealth GetRuntimeHealth()
    {
        var folders = _automationRepository.GetFolders();
        var available = folders.Count(folder => Directory.Exists(folder.FolderPath));
        var pdfPath = PdfBankStatementPipelineService.ProductionPipelinePath;
        var pdfAvailable = File.Exists(pdfPath);
        var summary = !_settings.GetBool(ApplicationSettingKeys.AutomationEnabled)
            ? "Automation is disabled in Settings."
            : _globalPause
                ? "Automation is globally paused."
                : folders.Count == 0
                    ? "No Automation Folder is registered."
                    : available == 0
                        ? "Registered Automation Folders are currently unavailable."
                        : $"{available:N0}/{folders.Count:N0} folder(s) available; {_watchers.Count:N0} watcher(s); {_queue.Count:N0} queued file(s).";
        return new AutomationRuntimeHealth(
            _settings.GetBool(ApplicationSettingKeys.AutomationEnabled),
            _globalPause,
            folders.Count,
            available,
            _watchers.Count,
            _queue.Count,
            pdfAvailable,
            pdfPath,
            summary);
    }

    public int RepairAutoImportAndRescan()
    {
        // Recovery helper for upgraded installations whose saved Settings may still
        // have one of the old monitor switches disabled. It deliberately does not
        // change linked clients/folders or accounting data.
        _settings.Set(ApplicationSettingKeys.AutomationEnabled, true);
        _settings.Set(ApplicationSettingKeys.AutomationAutoImportKnown, true);
        _settings.Set(ApplicationSettingKeys.AutomationMonitorExcelCsv, true);
        _settings.Set(ApplicationSettingKeys.AutomationMonitorPdf, true);
        _settings.Set(ApplicationSettingKeys.AutomationScanExisting, true);
        _globalPause = false;
        ReloadSettings();
        AutomationDiagnosticLog.Write("REPAIR", "Enabled Auto Import + spreadsheet/text + PDF monitoring and requested recursive recovery scan.");
        return ForceScanAllFolders();
    }

    public int ForceScanAllFolders()
    {
        if (!_settings.GetBool(ApplicationSettingKeys.AutomationEnabled)) return 0;
        var before = _queue.Count;
        foreach (var folder in _automationRepository.GetFolders())
        {
            if (folder.IsPaused || !Directory.Exists(folder.FolderPath)) continue;
            EnsureWatcher(folder);
            // Force-scan commands never pull GSTR-3B through the generic Auto Import queue.
            // GSTR-3B keeps its established GST portal/download + managed-evidence lifecycle.
            ScheduleRegisteredClientGstBootstrap(folder, folder.ClientId);
            QueueExistingFiles(folder, force: true, forceScanCommand: true);
        }
        return Math.Max(0, _queue.Count - before);
    }

    public UniversalDumpImportResult StageUniversalDump(long clientId, string financialYear, IEnumerable<string> selectedPaths)
    {
        if (!_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.Import))
            return new UniversalDumpImportResult(false, string.Empty, 0, 0, 0, "The current Windows user does not have Import permission.");

        var client = _clientRepository.GetById(clientId);
        if (client is null)
            return new UniversalDumpImportResult(false, string.Empty, 0, 0, 0, "The selected client is no longer available.");

        var candidates = new List<string>();
        foreach (var input in selectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                if (File.Exists(input)) candidates.Add(Path.GetFullPath(input));
                else if (Directory.Exists(input))
                    candidates.AddRange(Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Where(IsSupportedFile));
            }
            catch { }
        }
        candidates = candidates
            .Where(IsSupportedFile)
            .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.OrdinalIgnoreCase))
            .Where(path => !ClientDiagnosticExportService.IsHubGeneratedExportPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
            return new UniversalDumpImportResult(false, string.Empty, 0, 0, 0, "No supported PDF/image, Excel, CSV, bank exchange or text accounting files were found.");

        var run = _governanceRepository.CreateRun(clientId, financialYear, "Universal Dump", candidates.Count);
        var safeFy = new string(financialYear.Where(char.IsLetterOrDigit).ToArray());
        var vaultRoot = Path.Combine(ApplicationDataPaths.DocumentVaultFolder, clientId.ToString(), safeFy);
        var runFolder = Path.Combine(vaultRoot, run.RunId);
        Directory.CreateDirectory(runFolder);
        var folderId = $"SYSTEM-DUMP-{clientId}-{safeFy}";
        var registration = _automationRepository.GetByFolderId(folderId)
            ?? _automationRepository.Register(folderId, clientId, financialYear, vaultRoot, "Universal Dump / Document Vault");
        EnsureWatcher(registration);

        var staged = 0;
        var duplicateNames = 0;
        foreach (var source in candidates)
        {
            try
            {
                var hash = ComputeHash(source);
                var fileName = Path.GetFileName(source);
                var target = Path.Combine(runFolder, fileName);
                if (File.Exists(target))
                {
                    duplicateNames++;
                    target = Path.Combine(runFolder, $"{Path.GetFileNameWithoutExtension(fileName)}_{duplicateNames:000}{Path.GetExtension(fileName)}");
                }
                File.Copy(source, target, overwrite: false);
                var info = new FileInfo(target);
                var job = _governanceRepository.CreateJob(new ImportJobRecord
                {
                    RunId = run.RunId, FolderId = folderId, ClientId = clientId, FinancialYear = financialYear,
                    OriginalPath = source, WorkingPath = target, VaultPath = target, FileName = Path.GetFileName(target),
                    FileHash = hash, FileSize = info.Length, DetectedModule = "Pending Classification",
                    State = ImportJobStates.Detected, Detail = "Copied into the local Document Vault by Universal Dump.",
                    CreatedUtc = DateTime.UtcNow
                });
                _governanceRepository.AddDocument(new DocumentVaultRecord
                {
                    ClientId = clientId, FinancialYear = financialYear, RunId = run.RunId, JobId = job.JobId,
                    Module = "Pending Classification", FileName = Path.GetFileName(target), OriginalPath = source,
                    VaultPath = target, FileHash = hash, FileSize = info.Length, MimeHint = Path.GetExtension(target), AddedUtc = DateTime.UtcNow
                });
                if (QueueFile(folderId, target, force: true, manualReimport: true)) staged++;
            }
            catch (Exception exception)
            {
                AutomationDiagnosticLog.Write("UNIVERSAL-DUMP", $"Could not stage source: {exception.Message}", source);
            }
        }

        ProgressChanged?.Invoke(this, EventArgs.Empty);
        return new UniversalDumpImportResult(staged > 0, run.RunId, candidates.Count, staged, candidates.Count - staged,
            staged > 0
                ? $"Universal Dump run {run.RunId[..8].ToUpperInvariant()} staged {staged:N0}/{candidates.Count:N0} file(s). Classification/import continues through the common Import Job Center."
                : "The files were discovered but none could be queued. Review Import Job Center for details.");
    }

    public AutomationManualImportResult ManualImportClientFolders(long clientId, string financialYear)
    {
        var folders = _automationRepository.GetFolders()
            .Where(folder => !folder.FolderId.StartsWith("SYSTEM-DUMP-", StringComparison.OrdinalIgnoreCase))
            .Where(folder => folder.ClientId == clientId && folder.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(folder => folder.UpdatedUtc)
            .ToArray();
        if (folders.Length == 0)
            return new AutomationManualImportResult(false, 0, 0, 0, "No Automation Folder is linked to this client / Financial Year. Link a folder in Settings first.");
        if (folders.Length > 1)
            return new AutomationManualImportResult(false, folders.Length, 0, 0, "More than one folder registration exists for this Client/FY. AAH will not guess which one owns Auto Import. Open Settings → Auto Import & Folders → Linked Folders and Link / Relink the correct folder first.");

        var candidates = 0;
        var queued = 0;
        var customerNamesUpdated = 0;
        var unavailable = 0;
        var specialRouteSkipped = 0;
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder.FolderPath))
            {
                unavailable++;
                continue;
            }

            try
            {
                ClearBankOcrClaimsForFolder(folder.FolderPath);

                // Force scan is also the explicit Sales Customer Master refresh boundary.
                // Customer Master evidence stays out of the generic accounting queue, but
                // recognized GST Master Details must immediately backfill saved B2B/CDNR
                // fallback names for this exact linked Client/FY.
                customerNamesUpdated += ProcessDownloadedCustomerMasterAsync(folder, CancellationToken.None).GetAwaiter().GetResult();

                // Preserve the established special GSTR-3B path. The force-scan button
                // must not select/re-import GSTR-3B through the generic router.
                ScheduleRegisteredClientGstBootstrap(folder, folder.ClientId);
                foreach (var path in Directory.EnumerateFiles(folder.FolderPath, "*", SearchOption.AllDirectories))
                {
                    if (!IsSupportedFile(path) || Path.GetFileName(path).StartsWith("~$", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsManagedEvidencePath(folder, path)) continue;
                    if (IsGstr3bForceScanSource(path))
                    {
                        specialRouteSkipped++;
                        AutomationDiagnosticLog.Write("FORCE-SCAN-SPECIAL", "GSTR-3B excluded from generic force scan; existing GST special route remains authoritative.", path);
                        continue;
                    }
                    candidates++;
                    // Auto Import From Folder is ONLY a force scan. It uses the exact same
                    // unattended recognition, history, cache-handoff and module route as a
                    // FileSystemWatcher event; it never bypasses identity/settings or forces
                    // a destructive re-import of already-known source content.
                    if (QueueFile(folder.FolderId, path, force: true, manualReimport: false)) queued++;
                }
            }
            catch (IOException) { unavailable++; }
            catch (UnauthorizedAccessException) { unavailable++; }
        }

        var salesRefreshText = customerNamesUpdated > 0
            ? $" Sales Customer Master refreshed {customerNamesUpdated:N0} saved B2B/CDNR row(s)."
            : string.Empty;
        var message = unavailable == folders.Length
            ? "The linked Automation Folder is currently unavailable."
            : queued > 0
                ? $"Force scan queued {queued:N0} file(s) from the linked Client/FY folder. The normal unattended Auto Import route will process only new/changed content. GSTR-3B stays on its existing GST special route.{salesRefreshText}"
                : candidates == 0
                    ? specialRouteSkipped > 0
                        ? $"Force scan completed. No general Auto Import files were found; GSTR-3B remains on its existing GST special route.{salesRefreshText}"
                        : $"Force scan completed. No supported inbox files were found.{salesRefreshText}"
                    : $"Force scan completed. Files already queued or already known to Auto Import remain duplicate-protected; GSTR-3B stays on its existing GST special route.{salesRefreshText}";
        AutomationDiagnosticLog.Write("FORCE-SCAN", $"clientId={clientId} FY={financialYear} folders={folders.Length} candidates={candidates} queued={queued} customerNamesUpdated={customerNamesUpdated} specialRouteSkipped={specialRouteSkipped} unavailable={unavailable}");
        return new AutomationManualImportResult(unavailable < folders.Length, folders.Length, candidates, queued, message);
    }

    public void NotifyBankStatementDeleted(long clientId, string financialYear, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return;
        var removed = _automationRepository.DeleteHistoryForSource(clientId, financialYear, sourcePath);
        AutomationDiagnosticLog.Write("HISTORY-INVALIDATE", $"Bank Statement deleted from application; cleared {removed:N0} Auto Import history row(s) so a future re-paste can import again.", sourcePath);
    }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_started) return;
            _started = true;
            AutomationDiagnosticLog.Write("START", $"AutomationEnabled={_settings.GetBool(ApplicationSettingKeys.AutomationEnabled)} AutoImportKnown={_settings.GetBool(ApplicationSettingKeys.AutomationAutoImportKnown)} registeredFolders={_automationRepository.GetFolders().Count}");
            PipelineRuntimeDiagnostics.WriteReportIfDue(force: true);
            _lifetime = new CancellationTokenSource();
            _centralSourceStartupComplete = false;
            _startupRecoveryComplete = false;
            // BR4: source extraction is intentionally parallel. Accounting writes remain
            // gated by Client/module/bank authority, but one workbook no longer waits for
            // another workbook to finish its full accounting pipeline before being cached.
            var workerCount = Math.Clamp(Environment.ProcessorCount, 4, 12);
            _workerTasks = Enumerable.Range(0, workerCount)
                .Select(index => Task.Run(() => WorkerLoopAsync(_lifetime.Token)))
                .ToArray();
            var salesPurchaseWorkerCount = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);
            _salesPurchaseLiveWorkerTasks = Enumerable.Range(0, salesPurchaseWorkerCount)
                .Select(index => Task.Run(() => SalesPurchaseLiveWorkerLoopAsync(_lifetime.Token)))
                .ToArray();
            _reconciliationTask = Task.Run(() => FolderReconciliationLoopAsync(_lifetime.Token));
            _downloadsMaintenanceTask = Task.Run(() => DownloadsMaintenanceLoopAsync(_lifetime.Token));
            _explorerThread = new Thread(() => ExplorerLoop(_lifetime.Token))
            {
                IsBackground = true,
                Name = "AAH Explorer Automation Discovery"
            };
            if (OperatingSystem.IsWindows())
                _explorerThread.SetApartmentState(ApartmentState.STA);
            _explorerThread.Start();

            // Startup must attach watchers immediately, but it must not enumerate/hash/classify
            // every historical file while WPF is composing the shell. Reload Settings without
            // the legacy "scan existing now" side effect, then run one deferred recovery pass.
            ReloadSettings(scanExisting: false, scheduleGstBootstrap: false);
            ConfigureCentralSalesPurchaseWatcher();
            var startupToken = _lifetime.Token;
            _salesPurchaseStartupScanTask = Task.Run(() => CentralSalesPurchaseStartupScanAsync(startupToken), startupToken);
            _startupRecoveryTask = Task.Run(async () =>
            {
                try
                {
                    while (!IsLocalPreparationReady())
                    {
                        startupToken.ThrowIfCancellationRequested();
                        await Task.Delay(500, startupToken).ConfigureAwait(false);
                    }
                    if (!_settings.GetBool(ApplicationSettingKeys.AutomationEnabled) ||
                        !_settings.GetBool(ApplicationSettingKeys.AutomationAutoImportKnown))
                        return;

                    var repairedOcrClientYears = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var folder in _automationRepository.GetFolders())
                    {
                        startupToken.ThrowIfCancellationRequested();
                        if (folder.IsPaused || !Directory.Exists(folder.FolderPath)) continue;

                        var repairKey = $"{folder.ClientId}|{folder.FinancialYear}";
                        if (repairedOcrClientYears.Add(repairKey))
                        {
                            RepairLegacyGroupedOcrPageHistory(folder);
                            RepairSupersededOcrPartialStatements(folder, raiseDataChanged: false);
                        }

                        // QueueExistingFiles uses persistent Imported/Duplicate path watermarks,
                        // so unchanged historical sources never become fake "active/queued" work.
                        QueueExistingFiles(folder, force: false);

                        // GST profile/portal bootstrap can scan Downloads/e-Returns and is not
                        // part of shell startup. Run it only after the same quiet-start window.
                        ScheduleRegisteredClientGstBootstrap(folder, folder.ClientId);
                    }
                }
                catch (OperationCanceledException) when (startupToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    AutomationDiagnosticLog.Write("STARTUP-RECOVERY", $"Deferred recovery scan postponed: {exception.Message}");
                }
                finally
                {
                    _startupRecoveryComplete = true;
                    QueueAllActiveDashboardOutputs();
                }
            }, startupToken);
        }
    }

    public void ReloadSettings(bool scanExisting = true, bool scheduleGstBootstrap = true)
    {
        if (!_started) return;
        if (!_settings.GetBool(ApplicationSettingKeys.AutomationEnabled))
        {
            DisposeWatchers();
            DisposeCentralSalesPurchaseWatcher();
            return;
        }

        RefreshWatchers();
        ConfigureCentralSalesPurchaseWatcher();
        if (_settings.GetBool(ApplicationSettingKeys.AutomationAutoImportKnown))
        {
            foreach (var folder in _automationRepository.GetFolders())
            {
                if (folder.IsPaused || !Directory.Exists(folder.FolderPath)) continue;
                if (scanExisting && _settings.GetBool(ApplicationSettingKeys.AutomationScanExisting))
                    QueueExistingFiles(folder);
                if (scheduleGstBootstrap)
                    ScheduleRegisteredClientGstBootstrap(folder, folder.ClientId);
            }
        }
    }

    public void SetGlobalPause(bool paused)
    {
        _globalPause = paused;
        Notify(
            "Background automation",
            paused ? "Automatic file processing is paused." : "Automatic file processing has resumed.",
            false);
    }

    public string? GetActiveExplorerPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var foreground = EnumerateExplorerLocations(onlyForeground: true).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(foreground))
        {
            _lastActiveExplorerPath = foreground;
            return foreground;
        }

        if (!string.IsNullOrWhiteSpace(_lastActiveExplorerPath) && Directory.Exists(_lastActiveExplorerPath))
            return _lastActiveExplorerPath;

        var openExplorer = EnumerateExplorerLocations(onlyForeground: false).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(openExplorer)) _lastActiveExplorerPath = openExplorer;
        return openExplorer;
    }

    public AutomationCommandResult CreateAutomationFolderHere()
    {
        var currentPath = GetActiveExplorerPath();
        if (string.IsNullOrWhiteSpace(currentPath) || !Directory.Exists(currentPath))
            return AutomationCommandResult.Failed("Open the required location in File Explorer first.");

        if (!IsAllowedByRoot(currentPath))
            return AutomationCommandResult.Failed("The current Explorer location is outside the Auto Import Root Folder configured in Settings.");

        var folderName = GetConfiguredFolderName();
        var target = Path.Combine(currentPath, folderName);
        try
        {
            Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{target}\"",
                UseShellExecute = true
            });
            DiscoverFolder(target, forceRegistrationPrompt: true);
            return AutomationCommandResult.Success(target);
        }
        catch (Exception exception)
        {
            return AutomationCommandResult.Failed(exception.Message);
        }
    }

    public AutomationCommandResult RequestFolderLink(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return AutomationCommandResult.Failed("Choose the Automation Folder to link.");

        try
        {
            folderPath = NormalizePath(folderPath);
        }
        catch (Exception exception)
        {
            return AutomationCommandResult.Failed($"The selected folder path is invalid: {exception.Message}");
        }

        if (!Directory.Exists(folderPath))
            return AutomationCommandResult.Failed("The selected Automation Folder does not exist.");

        // An explicit Link command means the user intends this folder to be live.
        _settings.Set(ApplicationSettingKeys.AutomationEnabled, true);
        _settings.Set(ApplicationSettingKeys.AutomationAutoImportKnown, true);
        _settings.Set(ApplicationSettingKeys.AutomationMonitorExcelCsv, true);
        _settings.Set(ApplicationSettingKeys.AutomationMonitorPdf, true);
        _settings.Set(ApplicationSettingKeys.AutomationScanExisting, true);
        _globalPause = false;
        ReloadSettings();

        // IMPORTANT: explicit linking must ALWAYS open the existing-client chooser.
        // Older builds returned early when the folder already carried a Hub marker or
        // was registered to a wrong/duplicate client, which made it impossible to
        // re-link that folder to the user's real existing client.
        var existingAtPath = _automationRepository.GetByPath(folderPath);
        var markerId = ReadMarker(folderPath);
        AutomationFolderRegistration? existingById = null;
        if (markerId.Length > 0)
            existingById = _automationRepository.GetByFolderId(markerId);

        if (existingAtPath is null && existingById is not null && !PathsEqual(existingById.FolderPath, folderPath))
        {
            if (Directory.Exists(existingById.FolderPath))
                return AutomationCommandResult.Failed($"This folder carries a Hub Folder ID already linked at:\n{existingById.FolderPath}\n\nUnlink the original registration first if this is an intentional copy.");

            RebaseAutomationFolderPaths([
                (existingById.FolderId, existingById.FolderPath, folderPath, Path.GetFileName(folderPath))
            ]);
            existingAtPath = _automationRepository.GetByFolderId(existingById.FolderId);
        }

        var currentClient = _clientRepository.GetCurrent();
        var preferredClientId = existingAtPath?.ClientId
            ?? existingById?.ClientId
            ?? currentClient?.Id
            ?? 0;

        _dismissedRegistrations.TryRemove(folderPath, out _);
        _pendingRegistrations.TryRemove(folderPath, out _);
        _pendingRegistrations.TryAdd(folderPath, 0);
        AutomationDiagnosticLog.Write(
            "LINK-REQUEST",
            existingAtPath is null
                ? $"Explicit folder-link request opened the Existing Client/FY chooser. preferredClientId={preferredClientId}."
                : $"Explicit re-link request opened the Existing Client/FY chooser. Current link={existingAtPath.ClientName} FY={existingAtPath.FinancialYear}; preferredClientId={preferredClientId}.",
            folderPath);

        var handler = FolderRegistrationRequested;
        if (handler is null)
        {
            _pendingRegistrations.TryRemove(folderPath, out _);
            return AutomationCommandResult.Failed("The folder-link window is not available. Restart the Hub and try again.");
        }

        handler.Invoke(this, new AutomationFolderRegistrationRequestEventArgs(folderPath, preferredClientId));
        return new AutomationCommandResult(
            true,
            existingAtPath is null
                ? "Choose the existing Client / Financial Year and click Save & Link Folder."
                : $"This folder is currently linked to {existingAtPath.ClientName} • FY {existingAtPath.FinancialYear}. Choose the correct existing Client/FY and click Save & Link Folder to re-link it.",
            folderPath);
    }

    public IReadOnlyList<ClientProfile> GetLinkableClients() =>
        _clientRepository.GetAll(includeArchived: false)
            .OrderBy(client => client.ClientName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(client => client.FinancialYear, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<GstinProfileLookupResult> LookupClientProfileFromFolderAsync(
        string folderPath,
        string gstin,
        string financialYear,
        CancellationToken cancellationToken = default)
    {
        var normalizedGstin = NormalizeGstin(gstin);
        if (normalizedGstin.Length != 15)
            return new GstinProfileLookupResult { Gstin = normalizedGstin };

        _gstinProfileLookupService.Invalidate(normalizedGstin);
        var eReturnsRoot = _settings.Get(EReturnsRootContext.PreferenceKey).Trim();
        return await _gstinProfileLookupService.LookupAutomationLinkAsync(
            normalizedGstin,
            folderPath,
            eReturnsRoot,
            financialYear,
            cancellationToken);
    }

    public AutomationFolderRegistration LinkOrRecoverFolderToExistingClient(
        string folderPath,
        long clientId)
    {
        folderPath = NormalizePath(folderPath);
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("The selected Automation Folder no longer exists.");

        var client = _clientRepository.GetById(clientId)
            ?? throw new InvalidOperationException("The selected client no longer exists.");
        if (client.IsArchived)
            throw new InvalidOperationException("An archived client cannot be linked to a live Automation Folder.");

        var existingAtPath = _automationRepository.GetByPath(folderPath);
        if (existingAtPath is not null)
        {
            if (existingAtPath.ClientId == client.Id &&
                existingAtPath.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            {
                EnsureWatcher(existingAtPath);
                return existingAtPath;
            }

            throw new InvalidOperationException(
                $"This folder is already linked to {existingAtPath.ClientName} • FY {existingAtPath.FinancialYear}. " +
                "AAH will not move another Client/FY's folder ownership from the Dashboard.");
        }

        var markerId = ReadMarker(folderPath);
        if (markerId.Length > 0)
        {
            var marked = _automationRepository.GetByFolderId(markerId);
            if (marked is not null)
            {
                if (marked.ClientId != client.Id ||
                    !marked.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"This folder carries the durable FolderId for {marked.ClientName} • FY {marked.FinancialYear}. " +
                        "AAH will not reassign that ownership from the Dashboard.");
                }

                if (Directory.Exists(marked.FolderPath) && !PathsEqual(marked.FolderPath, folderPath))
                {
                    throw new InvalidOperationException(
                        $"The original linked folder still exists at:\n{marked.FolderPath}\n\n" +
                        "The selected folder appears to be a copy with the same .aah-folder identity. " +
                        "AAH will not silently move the link.");
                }

                _folderMoveQuiesce[marked.FolderId] = 0;
                RemoveWatcher(marked.FolderId);
                try
                {
                    RebaseAutomationFolderPaths([
                        (marked.FolderId, marked.FolderPath, folderPath, Path.GetFileName(folderPath))
                    ]);
                }
                finally
                {
                    _folderMoveQuiesce.TryRemove(marked.FolderId, out _);
                }

                var recovered = _automationRepository.GetByFolderId(marked.FolderId)
                    ?? throw new InvalidOperationException("The moved Automation Folder was found, but its registration could not be refreshed.");

                // The durable marker already exists in the moved folder. Do not rewrite it:
                // moved links may be on read-only/shared storage and path recovery must not
                // require write access to the source folder.
                PortalCredentialFileService.EnsureFile(recovered);
                EnsureWatcher(recovered);
                if (_settings.GetBool(ApplicationSettingKeys.AutomationEnabled) &&
                    _settings.GetBool(ApplicationSettingKeys.AutomationAutoImportKnown))
                    QueueExistingFiles(recovered, force: false);

                FoldersChanged?.Invoke(this, EventArgs.Empty);
                ClientRegistryChanged?.Invoke(this, EventArgs.Empty);
                AutomationDiagnosticLog.Write(
                    "DASHBOARD-FOLDER-RECOVER",
                    $"Dashboard recovered the moved linked Automation Folder without changing FolderId/client/FY ownership. Old={marked.FolderPath}; New={folderPath}",
                    folderPath);
                return recovered;
            }
        }

        // No durable moved-folder identity was present. The user explicitly selected
        // this path from the current Client/FY Dashboard, so use the normal existing-
        // client link path. That path still rejects live ownership collisions.
        return RegisterFolderToExistingClient(folderPath, clientId);
    }

    public AutomationFolderRegistration RegisterFolderToExistingClient(
        string folderPath,
        long clientId)
    {
        folderPath = NormalizePath(folderPath);
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("The Automation Folder no longer exists.");

        var client = _clientRepository.GetById(clientId)
            ?? throw new InvalidOperationException("The selected client no longer exists.");
        if (client.IsArchived)
            throw new InvalidOperationException("An archived client cannot be linked to a live Automation Folder.");

        var existingAtPath = _automationRepository.GetByPath(folderPath);
        // One Client + one Financial Year owns one Automation Folder. Relinking this
        // Client/FY removes only older folder registrations for the same ownership key;
        // accounting data and the physical folders are never deleted.
        foreach (var stale in _automationRepository.GetFolders()
                     .Where(item => item.ClientId == client.Id &&
                                    item.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
                     .Where(item => existingAtPath is null || !item.FolderId.Equals(existingAtPath.FolderId, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveWatcher(stale.FolderId);
            _automationRepository.Unlink(stale.FolderId);
        }

        if (existingAtPath is not null)
        {
            // Explicit existing-client linking is allowed to repair/reassign an old
            // wrong link. Keep the folder identity but move the live registration to
            // the selected Client/FY. File-history deduplication is Client-scoped, so
            // files previously seen under a wrong client do not block the corrected link.
            RemoveWatcher(existingAtPath.FolderId);
            var registration = _automationRepository.Relink(
                existingAtPath.FolderId,
                client.Id,
                client.FinancialYear,
                folderPath,
                Path.GetFileName(folderPath));
            WriteMarker(folderPath, registration.FolderId);
            _pendingRegistrations.TryRemove(folderPath, out _);
            _dismissedRegistrations.TryRemove(folderPath, out _);
            EnsureWatcher(registration);
            FoldersChanged?.Invoke(this, EventArgs.Empty);
            ClientRegistryChanged?.Invoke(this, EventArgs.Empty);
            if (_settings.GetBool(ApplicationSettingKeys.AutomationEnabled) &&
                _settings.GetBool(ApplicationSettingKeys.AutomationAutoImportKnown))
                QueueExistingFiles(registration, force: false);
            ScheduleRegisteredClientGstBootstrap(registration, client.Id);

            AutomationDiagnosticLog.Write(
                "RELINK",
                $"Automation Folder linked to existing client={client.ClientName} FY={client.FinancialYear}; previous client={existingAtPath.ClientName} FY={existingAtPath.FinancialYear}.",
                folderPath);
            Notify(
                "Automation Folder linked",
                $"{client.ClientName} • FY {client.FinancialYear}\n{folderPath}",
                false);
            return registration;
        }

        return RegisterFolderCore(folderPath, client);
    }

    public AutomationFolderRegistration RegisterFolder(
        string folderPath,
        string clientName,
        string gstin,
        string financialYear)
    {
        folderPath = NormalizePath(folderPath);
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("The Automation Folder no longer exists.");
        if (string.IsNullOrWhiteSpace(financialYear))
            throw new InvalidOperationException("Financial Year is required.");

        var normalizedGstin = NormalizeGstin(gstin);
        if (normalizedGstin.Length is > 0 and not 15)
            throw new InvalidOperationException("GSTIN must be blank or contain exactly 15 letters/numbers.");
        if (string.IsNullOrWhiteSpace(clientName) && normalizedGstin.Length == 0)
            throw new InvalidOperationException("Enter either Client Name or GSTIN.");

        var suppliedName = AccountingText.DisplayName(clientName);
        var displayName = suppliedName.Length > 0 ? suppliedName : normalizedGstin;
        var normalizedFinancialYear = financialYear.Trim();
        var duplicateNameKey = AccountingText.DuplicateKey(displayName);
        var existingClients = _clientRepository.GetAll(includeArchived: true);
        var duplicate = existingClients.FirstOrDefault(client =>
            client.FinancialYear.Equals(normalizedFinancialYear, StringComparison.OrdinalIgnoreCase) &&
            ((normalizedGstin.Length > 0 && NormalizeGstin(client.Gstin).Equals(normalizedGstin, StringComparison.OrdinalIgnoreCase)) ||
             (normalizedGstin.Length == 0 && AccountingText.DuplicateKey(client.ClientName).Equals(duplicateNameKey, StringComparison.OrdinalIgnoreCase))));
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"This client/FY already exists as '{duplicate.ClientName} • FY {duplicate.FinancialYear}'. " +
                "Choose Link to existing client instead of creating a duplicate client.");
        }

        var client = new ClientProfile
        {
            ClientCode = GenerateNextClientCode(),
            ClientName = displayName,
            // For GST clients, Client Name is only the display identity. Keep Legal/Trade
            // profile fields importable so the linked JSON/GSTR bootstrap can populate the
            // actual GST registration names instead of freezing the display name as Legal Name.
            LegalName = normalizedGstin.Length == 0 ? suppliedName : string.Empty,
            LegalNameIsManual = normalizedGstin.Length == 0 && suppliedName.Length > 0,
            Gstin = normalizedGstin,
            FinancialYear = normalizedFinancialYear,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        client = _clientRepository.Save(client);
        return RegisterFolderCore(folderPath, client);
    }

    private AutomationFolderRegistration RegisterFolderCore(string folderPath, ClientProfile client)
    {
        var existingAtPath = _automationRepository.GetByPath(folderPath);
        foreach (var stale in _automationRepository.GetFolders()
                     .Where(item => item.ClientId == client.Id &&
                                    item.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
                     .Where(item => existingAtPath is null || !item.FolderId.Equals(existingAtPath.FolderId, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveWatcher(stale.FolderId);
            _automationRepository.Unlink(stale.FolderId);
        }
        if (existingAtPath is not null)
        {
            if (existingAtPath.ClientId == client.Id &&
                existingAtPath.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            {
                PortalCredentialFileService.EnsureFile(existingAtPath);
                return existingAtPath;
            }
            throw new InvalidOperationException(
                $"This folder is already linked to {existingAtPath.ClientName} • FY {existingAtPath.FinancialYear}.");
        }

        var folderId = ReadMarker(folderPath);
        if (folderId.Length > 0)
        {
            var existingById = _automationRepository.GetByFolderId(folderId);
            if (existingById is not null && !PathsEqual(existingById.FolderPath, folderPath))
                throw new InvalidOperationException("This folder contains a Hub Folder ID that is already linked elsewhere. Use the registered folder or unlink it first.");
        }
        if (folderId.Length == 0) folderId = Guid.NewGuid().ToString("N").ToUpperInvariant();

        WriteMarker(folderPath, folderId);
        var registration = _automationRepository.Register(
            folderId,
            client.Id,
            client.FinancialYear,
            folderPath,
            Path.GetFileName(folderPath));

        _pendingRegistrations.TryRemove(folderPath, out _);
        _dismissedRegistrations.TryRemove(folderPath, out _);
        PortalCredentialFileService.EnsureFile(registration);
        EnsureWatcher(registration);
        FoldersChanged?.Invoke(this, EventArgs.Empty);
        ClientRegistryChanged?.Invoke(this, EventArgs.Empty);

        // A newly linked folder is an explicit Auto Import instruction. Always queue
        // files already present once; hash history prevents duplicate posting. This
        // removes the old state where linking succeeded but nothing happened because
        // the legacy Scan Existing preference happened to be disabled.
        if (_settings.GetBool(ApplicationSettingKeys.AutomationEnabled) &&
            _settings.GetBool(ApplicationSettingKeys.AutomationAutoImportKnown))
            QueueExistingFiles(registration, force: false);

        ScheduleRegisteredClientGstBootstrap(registration, client.Id);

        Notify(
            "Automation Folder linked",
            $"{client.ClientName} • FY {client.FinancialYear}\n{folderPath}",
            false);
        return registration;
    }

    private void ScheduleRegisteredClientGstBootstrap(
        AutomationFolderRegistration registration,
        long clientId,
        bool force = false)
    {
        // Completion 62: all folders for the same Client/FY share one GST discovery
        // operation. The 20-second maintenance cadence remains unchanged, but each
        // pass is only a metadata/cache probe unless a source version changed.
        var key = $"{clientId}|{registration.FinancialYear}";
        if (!_gstBootstrapInFlight.TryAdd(key, 0)) return;

        var cancellationToken = _lifetime?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                var client = _clientRepository.GetById(clientId);
                if (client is null || NormalizeGstin(client.Gstin).Length != 15) return;

                var eReturnsRoot = _settings.Get(EReturnsRootContext.PreferenceKey).Trim();
                if (!force && !_gstPortalAutoImportService.HasPotentialChanges(
                        client,
                        eReturnsRoot,
                        additionalRoots: [registration.FolderPath]))
                    return;

                await BootstrapRegisteredClientGstAsync(
                    registration,
                    clientId,
                    cancellationToken,
                    force);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal application shutdown.
            }
            catch (Exception exception)
            {
                AutomationDiagnosticLog.Write(
                    "GST-BOOTSTRAP-ERROR",
                    exception.ToString(),
                    registration.FolderPath);
            }
            finally
            {
                _gstBootstrapInFlight.TryRemove(key, out _);
            }
        }, cancellationToken);
    }

    private async Task BootstrapRegisteredClientGstAsync(
        AutomationFolderRegistration registration,
        long clientId,
        CancellationToken cancellationToken,
        bool forcePortalImport)
    {
        var client = _clientRepository.GetById(clientId);
        if (client is null || NormalizeGstin(client.Gstin).Length != 15) return;

        var eReturnsRoot = _settings.Get(EReturnsRootContext.PreferenceKey).Trim();
        AutomationDiagnosticLog.Write(
            "GST-BOOTSTRAP",
            $"GSTIN {client.Gstin} linked through Automation Folder. Starting client-independent GST profile/import discovery.",
            registration.FolderPath);

        // Profile lookup must happen before moving a Downloads GSTR-3B because the
        // existing profile service deliberately searches Downloads for authoritative 3B.
        _gstinProfileLookupService.Invalidate(client.Gstin);
        var profile = await _gstinProfileLookupService.LookupAsync(
            client.Gstin,
            eReturnsRoot,
            cancellationToken);

        var savedClient = _clientRepository.ApplyImportedGstProfile(client.Id, profile);
        ClientRegistryChanged?.Invoke(this, EventArgs.Empty);

        if (!_settings.GetBool(ApplicationSettingKeys.AutomationEnabled) ||
            !_settings.GetBool(ApplicationSettingKeys.AutomationAutoImportKnown))
        {
            AutomationDiagnosticLog.Write(
                "GST-BOOTSTRAP",
                "GST profile refreshed, but portal auto-import/movement was not started because Auto Import is disabled in Settings.",
                registration.FolderPath);
            return;
        }

        var movedFiles = await MoveValidatedGstr3bDownloadsAsync(
            registration,
            savedClient,
            profile.SourcePath,
            cancellationToken);

        var movedProfile = movedFiles.FirstOrDefault(item =>
            item.SourcePath.Equals(profile.SourcePath, StringComparison.OrdinalIgnoreCase));
        if (movedProfile is not null)
        {
            profile = CopyGstinProfileWithSourcePath(profile, movedProfile.TargetPath);
            savedClient = _clientRepository.ApplyImportedGstProfile(savedClient.Id, profile);
            ClientRegistryChanged?.Invoke(this, EventArgs.Empty);
        }

        // Moving and importing are intentionally separate. Every verified matching
        // Downloads PDF is relocated, including periods AAH already has. For a period
        // not yet saved, only the newest moved source is imported; older same-period
        // copies are retained in the linked client folder as duplicate source evidence.
        var existingPeriods = _gstRepository.GetReturns(savedClient.Id, savedClient.FinancialYear)
            .Where(row => row.ParseValid)
            .Select(row => row.PeriodKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var movedPeriodsImported = 0;
        foreach (var periodGroup in movedFiles
                     .GroupBy(item => item.PeriodKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = periodGroup.OrderByDescending(item => item.WrittenUtc).ToArray();
            var canonical = ordered[0];
            var canonicalHash = SafeHash(canonical.TargetPath);
            var latestState = _gstRepository.GetLatestPortalPeriodState(
                savedClient.Id,
                savedClient.FinancialYear,
                "GSTR-3B",
                periodGroup.Key);

            var shouldImport = forcePortalImport || !existingPeriods.Contains(periodGroup.Key);
            var skipReason = string.Empty;

            if (!shouldImport)
            {
                if (latestState is not null &&
                    latestState.ContentHash.Length > 0 &&
                    latestState.ContentHash.Equals(canonicalHash, StringComparison.OrdinalIgnoreCase))
                {
                    skipReason = "Same source version is already cached/imported.";
                }
                else if (latestState is not null &&
                         canonical.WrittenUtc < latestState.SourceLastWriteUtc)
                {
                    skipReason = "An older source version was detected; the newer imported version remains authoritative.";
                }
                else if (latestState is null)
                {
                    // Legacy imported periods predate the persistent source-state cache.
                    // Compare the actual saved source when it still exists. Same content
                    // is adopted into the cache without a re-import; different/new content
                    // is allowed through once and becomes the versioned baseline.
                    var current = _gstRepository.GetReturns(savedClient.Id, savedClient.FinancialYear)
                        .FirstOrDefault(row => row.PeriodKey.Equals(periodGroup.Key, StringComparison.OrdinalIgnoreCase));
                    var currentSource = current?.SourcePath
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault(path => File.Exists(path));
                    if (!string.IsNullOrWhiteSpace(currentSource) &&
                        SafeHash(currentSource).Equals(canonicalHash, StringComparison.OrdinalIgnoreCase))
                        skipReason = "Legacy period source matches the newly detected source; cache adopted without re-import.";
                    else
                        shouldImport = true;
                }
                else
                {
                    // Same period but a different source fingerprint at the same/newer
                    // source version is exactly the Completion 62 automatic-update case.
                    shouldImport = true;
                }
            }

            if (!shouldImport)
            {
                var info = new FileInfo(canonical.TargetPath);
                _gstRepository.UpsertPortalSourceState(new GstPortalSourceState(
                    savedClient.Id,
                    savedClient.FinancialYear,
                    NormalizeGstin(savedClient.Gstin),
                    "GSTR-3B",
                    canonical.TargetPath,
                    periodGroup.Key,
                    info.Exists ? info.Length : 0,
                    canonical.WrittenUtc,
                    $"{(info.Exists ? info.Length : 0)}|{canonical.WrittenUtc.Ticks}",
                    canonicalHash,
                    DateTime.UtcNow,
                    latestState?.LastImportedUtc ?? DateTime.MinValue,
                    latestState?.LastEditedUtc,
                    latestState is not null &&
                    canonicalHash.Length > 0 &&
                    latestState.ContentHash.Length > 0 &&
                    latestState.ContentHash.Equals(canonicalHash, StringComparison.OrdinalIgnoreCase)
                        ? "Duplicate"
                        : "Older Version"));

                foreach (var moved in ordered)
                {
                    var hash = SafeHash(moved.TargetPath);
                    RecordHistory(
                        registration,
                        moved.TargetPath,
                        hash,
                        "GSTR-3B PDF",
                        "Moved",
                        $"Verified GSTR-3B for {periodGroup.Key}: {skipReason}");
                }
                continue;
            }

            var result = await Task.Run(
                () => _gstr3bPdfImportService.Import(
                    [canonical.TargetPath],
                    savedClient.Id,
                    savedClient.FinancialYear,
                    savedClient.Gstin),
                cancellationToken);

            if (result.PeriodsImported > 0)
            {
                movedPeriodsImported += result.PeriodsImported;
                existingPeriods.Add(periodGroup.Key);
                var info = new FileInfo(canonical.TargetPath);
                _gstRepository.UpsertPortalSourceState(new GstPortalSourceState(
                    savedClient.Id,
                    savedClient.FinancialYear,
                    NormalizeGstin(savedClient.Gstin),
                    "GSTR-3B",
                    canonical.TargetPath,
                    periodGroup.Key,
                    info.Exists ? info.Length : 0,
                    canonical.WrittenUtc,
                    $"{(info.Exists ? info.Length : 0)}|{canonical.WrittenUtc.Ticks}",
                    canonicalHash,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    latestState?.LastEditedUtc,
                    "Imported"));

                var detail = result.Warnings.Count == 0
                    ? latestState is null
                        ? "Verified GSTR-3B source auto-imported and stored as the current cached version."
                        : "A newer/different GSTR-3B source version was detected and auto-imported for this period."
                    : $"Verified GSTR-3B was imported with review warning(s): {string.Join(" | ", result.Warnings.Take(3))}";
                RecordHistory(
                    registration,
                    canonical.TargetPath,
                    canonicalHash,
                    "GSTR-3B PDF",
                    result.Warnings.Count == 0 ? "Imported" : "Needs Action",
                    detail,
                    result.PeriodsImported);
                if (result.Warnings.Count == 0)
                    ResolveAutomationAction(registration, "GST", canonicalHash);
                else
                    AddAction(registration, "GST", "GSTR-3B Import Review", Path.GetFileName(canonical.TargetPath), detail, canonicalHash);
            }
            else
            {
                var detail = result.Warnings.Count > 0
                    ? string.Join(" | ", result.Warnings.Take(4))
                    : "The verified GSTR-3B file was moved, but no return period could be imported.";
                RecordHistory(registration, canonical.TargetPath, canonicalHash, "GSTR-3B PDF", "Needs Action", detail);
                AddAction(registration, "GST", "GSTR-3B Import Review", Path.GetFileName(canonical.TargetPath), detail, canonicalHash);
            }

            foreach (var duplicate in ordered.Skip(1))
            {
                var hash = SafeHash(duplicate.TargetPath);
                RecordHistory(
                    registration,
                    duplicate.TargetPath,
                    hash,
                    "GSTR-3B PDF",
                    "Moved",
                    $"Verified duplicate GSTR-3B source for {periodGroup.Key} was retained as evidence. Only the newest/different source version is eligible for import.");
            }
        }

        // Completion 41: repair an already-moved GSTR-3B that an older parser saved as
        // incomplete. Downloads movement is intentionally one-way, so without this pass a
        // fixed parser would never get another chance to read that linked evidence file.
        movedPeriodsImported += await ImportLinkedGstr3bRepairCandidatesAsync(
            registration,
            savedClient,
            existingPeriods,
            cancellationToken);

        // R22: Credit/Cash Ledger files now follow the same linked-folder ownership
        // contract as GSTR-3B. Validate GSTIN/FY with the real portal parser first, move
        // only matching evidence into the registered Client/FY folder, and include those
        // managed folders in the portal discovery pass. This prevents a validated ledger
        // from living forever in Downloads/Winman while AAH merely imports it in-place.
        var movedLedgerFiles = await MoveValidatedGstLedgerSourcesAsync(
            registration,
            savedClient,
            eReturnsRoot,
            cancellationToken);

        // This is the same portal import service used by the GST page, but it runs
        // against the linked client directly. The linked Automation Folder is supplied as
        // an additional authority root so moved Credit/Cash Ledger evidence is imported
        // from its durable managed path rather than from a stale pre-move path.
        var portalResult = await Task.Run(
            () => _gstPortalAutoImportService.ImportAvailable(
                savedClient,
                eReturnsRoot,
                forcePortalImport,
                refreshDiscovery: movedFiles.Count > 0 || movedLedgerFiles.Count > 0,
                additionalRoots: [registration.FolderPath]),
            cancellationToken);

        var totalPeriodsImported = movedPeriodsImported + portalResult.TotalPeriods;
        if (totalPeriodsImported > 0)
        {
            // Fix43: GSTR-3B/Nil recognition is an accounting input, not only a GST
            // screen notification. Regenerate Journal + GST reconciliation immediately
            // after the background import/repair, even when this client is not open.
            if (_accountingRefreshCoordinator is not null)
            {
                await _accountingRefreshCoordinator
                    .RefreshAsync(savedClient, cancellationToken)
                    .ConfigureAwait(false);
            }

            AutomationDiagnosticLog.Write(
                "GST-BOOTSTRAP",
                $"Background GST portal import + accounting refresh completed without opening the client. Imported/repaired periods={totalPeriodsImported:N0}; warnings={portalResult.Warnings.Count:N0}.",
                registration.FolderPath);
            RaiseAccountingDataChanged(
                registration,
                "GST",
                totalPeriodsImported,
                profile.SourcePath.Length > 0 ? profile.SourcePath : registration.FolderPath);
            RaiseAccountingDataChanged(
                registration,
                "Journal",
                0,
                profile.SourcePath.Length > 0 ? profile.SourcePath : registration.FolderPath);
        }

        ClientRegistryChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record MovedGstr3bDownload(
        string SourcePath,
        string TargetPath,
        string PeriodKey,
        DateTime WrittenUtc);

    private async Task<IReadOnlyList<MovedGstr3bDownload>> MoveValidatedGstr3bDownloadsAsync(
        AutomationFolderRegistration registration,
        ClientProfile client,
        string preferredProfileSourcePath,
        CancellationToken cancellationToken)
    {
        var moved = new List<MovedGstr3bDownload>();
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        if (!Directory.Exists(downloads) || !Directory.Exists(registration.FolderPath))
            return moved;

        string[] candidates;
        try
        {
            var discovered = Directory.EnumerateFiles(downloads, "*.pdf", SearchOption.AllDirectories)
                // If the linked Automation Folder itself lives under Downloads, do not
                // rediscover files that were already moved into that folder on an earlier
                // bootstrap; otherwise a later scan could keep renaming the same 3B.
                .Where(path => !IsPathInsideFolder(path, registration.FolderPath))
                .Select(path => new
                {
                    Path = path,
                    WrittenUtc = SafeGetLastWriteTimeUtc(path)
                })
                .OrderByDescending(item => item.WrittenUtc)
                .Take(250)
                .Select(item => item.Path)
                .ToList();

            // Profile lookup runs first and may already have found an authoritative
            // GSTR-3B in Downloads. Always include that exact file even when it falls
            // outside the recent-scan window so the already-found source is moved too.
            if (!string.IsNullOrWhiteSpace(preferredProfileSourcePath) &&
                File.Exists(preferredProfileSourcePath) &&
                Path.GetExtension(preferredProfileSourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
                IsPathInsideFolder(preferredProfileSourcePath, downloads) &&
                !IsPathInsideFolder(preferredProfileSourcePath, registration.FolderPath) &&
                !discovered.Contains(preferredProfileSourcePath, StringComparer.OrdinalIgnoreCase))
            {
                discovered.Insert(0, preferredProfileSourcePath);
            }

            candidates = discovered.ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AutomationDiagnosticLog.Write("GST-DOWNLOAD-SCAN", exception.Message, downloads);
            return moved;
        }

        var validCandidates = new List<(string Path, string PeriodKey, DateTime WrittenUtc)>();
        foreach (var source in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var classification = _fileClassifier.Classify(source);
                if (classification.Kind != AutomationFileKind.Gstr3bPdf) continue;

                // Use an empty fallback GSTIN here. A file is moved only when the PDF
                // itself contains the exact linked GSTIN; filename guesses are never enough.
                var record = _gstr3bPdfImportService.Parse(
                    source,
                    client.Id,
                    client.FinancialYear,
                    string.Empty);
                if (!NormalizeGstin(record.Gstin).Equals(NormalizeGstin(client.Gstin), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsPeriodInFinancialYear(record.PeriodKey, client.FinancialYear))
                    continue;

                var writtenUtc = SafeGetLastWriteTimeUtc(source);
                validCandidates.Add((source, record.PeriodKey, writtenUtc));
            }
            catch (Exception exception)
            {
                AutomationDiagnosticLog.Write(
                    "GST-DOWNLOAD-REVIEW",
                    $"GSTR-3B candidate was left in Downloads: {exception.Message}",
                    source);
            }
        }

        if (validCandidates.Count == 0) return moved;

        var destinationFolder = Path.Combine(registration.FolderPath, "GST", "GSTR-3B");
        Directory.CreateDirectory(destinationFolder);

        foreach (var candidate in validCandidates.OrderBy(item => item.WrittenUtc).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.GetFullPath(candidate.Path);
            if (!File.Exists(source)) continue;
            if (!await WaitForStableFileAsync(source, cancellationToken))
            {
                AutomationDiagnosticLog.Write(
                    "GST-DOWNLOAD-MOVE",
                    "Verified GSTR-3B remained in Downloads because the file was still being written/locked.",
                    source);
                continue;
            }

            var target = BuildCollisionSafeTarget(destinationFolder, Path.GetFileName(source));
            var normalizedTarget = Path.GetFullPath(target);
            _generatedWorkbookPaths.TryAdd(normalizedTarget, 0);
            try
            {
                File.Move(source, target);
                moved.Add(new MovedGstr3bDownload(source, target, candidate.PeriodKey, candidate.WrittenUtc));
                AutomationDiagnosticLog.Write(
                    "GST-DOWNLOAD-MOVE",
                    $"Verified GSTR-3B moved to linked client folder for immediate background import. GSTIN={client.Gstin}; FY={client.FinancialYear}.",
                    target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _generatedWorkbookPaths.TryRemove(normalizedTarget, out _);
                AutomationDiagnosticLog.Write(
                    "GST-DOWNLOAD-MOVE",
                    $"Verified GSTR-3B could not be moved and was left in Downloads: {exception.Message}",
                    source);
            }
        }

        return moved;
    }

    private sealed record MovedGstLedgerSource(
        string SourcePath,
        string TargetPath,
        string LedgerType,
        int PeriodCount);

    private async Task<IReadOnlyList<MovedGstLedgerSource>> MoveValidatedGstLedgerSourcesAsync(
        AutomationFolderRegistration registration,
        ClientProfile client,
        string eReturnsRoot,
        CancellationToken cancellationToken)
    {
        var moved = new List<MovedGstLedgerSource>();
        if (!Directory.Exists(registration.FolderPath)) return moved;

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(eReturnsRoot) && Directory.Exists(eReturnsRoot))
            roots.Add(Path.GetFullPath(eReturnsRoot));
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads) && !roots.Contains(downloads, StringComparer.OrdinalIgnoreCase))
            roots.Add(downloads);

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(6000))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsPathInsideFolder(path, registration.FolderPath)) continue;
                    var extension = Path.GetExtension(path).ToLowerInvariant();
                    if (extension is not (".csv" or ".xlsx" or ".xls" or ".xlsm")) continue;
                    var token = NormalizeIdentityToken(Path.GetFileName(path));
                    if (token.Contains("ELECTRONICCREDITLEDGER", StringComparison.OrdinalIgnoreCase) ||
                        token.Contains("ELECTRONICCASHLEDGER", StringComparison.OrdinalIgnoreCase) ||
                        token.Contains("CREDITLEDGER", StringComparison.OrdinalIgnoreCase) ||
                        token.Contains("CASHLEDGER", StringComparison.OrdinalIgnoreCase))
                        candidates.Add(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AutomationDiagnosticLog.Write("GST-LEDGER-SCAN", exception.Message, root);
            }
        }

        foreach (var sourcePath in candidates.OrderBy(SafeGetLastWriteTimeUtc).ThenBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = NormalizeIdentityToken(Path.GetFileName(sourcePath));
            var ledgerType = token.Contains("CASHLEDGER", StringComparison.OrdinalIgnoreCase)
                ? "Electronic Cash Ledger"
                : "Electronic Credit Ledger";
            try
            {
                // Validation is deliberately performed against the source path with the exact
                // Client GSTIN/FY and official From/To range. Filename matching alone can never
                // cause cross-client movement; a zero-transaction ledger is still valid evidence.
                if (!_gstLedgerImportService.IsValidSourceForClient(
                        sourcePath,
                        client.Gstin,
                        client.FinancialYear))
                    continue;

                // An official Electronic Credit Ledger can legitimately contain only
                // Opening/Closing Balance and no dated transaction rows. Such evidence
                // must still be linked/moved even though there is no monthly period to
                // import. Parse periods when available, but do not make transactions a
                // prerequisite for source ownership/movement.
                var periodCount = 0;
                try
                {
                    periodCount = _gstLedgerImportService.Parse(
                        sourcePath,
                        ledgerType,
                        client.Id,
                        client.FinancialYear,
                        client.Gstin).Count;
                }
                catch (InvalidDataException exception) when (exception.Message.Contains("No dated", StringComparison.OrdinalIgnoreCase))
                {
                    periodCount = 0;
                }
                if (!await WaitForStableFileAsync(sourcePath, cancellationToken).ConfigureAwait(false)) continue;

                var subFolder = ledgerType.Equals("Electronic Cash Ledger", StringComparison.OrdinalIgnoreCase)
                    ? "Electronic Cash Ledger"
                    : "Electronic Credit Ledger";
                var destinationFolder = Path.Combine(registration.FolderPath, "GST", subFolder);
                Directory.CreateDirectory(destinationFolder);
                var target = BuildCollisionSafeTarget(destinationFolder, Path.GetFileName(sourcePath));
                var normalizedTarget = Path.GetFullPath(target);
                _generatedWorkbookPaths.TryAdd(normalizedTarget, 0);
                try
                {
                    File.Move(sourcePath, target);
                    moved.Add(new MovedGstLedgerSource(sourcePath, target, ledgerType, periodCount));
                    RecordHistory(
                        registration,
                        target,
                        SafeHash(target),
                        ledgerType,
                        "Moved",
                        $"Verified {ledgerType} moved to linked Client/FY GST folder. GSTIN={client.Gstin}; FY={client.FinancialYear}.",
                        periodCount);
                    AutomationDiagnosticLog.Write(
                        "GST-LEDGER-MOVE",
                        $"Verified {ledgerType} moved to linked Client/FY folder. GSTIN={client.Gstin}; FY={client.FinancialYear}; periods={periodCount}.",
                        target);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _generatedWorkbookPaths.TryRemove(normalizedTarget, out _);
                    AutomationDiagnosticLog.Write("GST-LEDGER-MOVE", $"Validated ledger remained at source because move failed: {exception.Message}", sourcePath);
                }
            }
            catch (Exception exception)
            {
                AutomationDiagnosticLog.Write(
                    "GST-LEDGER-REVIEW",
                    $"Ledger candidate was not moved for this Client/FY: {exception.Message}",
                    sourcePath);
            }
        }

        return moved;
    }

    private async Task<int> ImportLinkedGstr3bRepairCandidatesAsync(
        AutomationFolderRegistration registration,
        ClientProfile client,
        ISet<string> validPeriods,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(registration.FolderPath, "GST", "GSTR-3B");
        if (!Directory.Exists(folder)) return 0;

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.pdf", SearchOption.TopDirectoryOnly)
                .OrderByDescending(SafeGetLastWriteTimeUtc)
                .Take(80)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AutomationDiagnosticLog.Write("GST-LINKED-REPAIR", exception.Message, folder);
            return 0;
        }

        var candidates = new List<(string Path, string PeriodKey, DateTime WrittenUtc)>();
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var record = _gstr3bPdfImportService.Parse(path, client.Id, client.FinancialYear, string.Empty);
                if (record.PeriodKey.Length == 0 || validPeriods.Contains(record.PeriodKey)) continue;
                if (!NormalizeGstin(record.Gstin).Equals(NormalizeGstin(client.Gstin), StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsPeriodInFinancialYear(record.PeriodKey, client.FinancialYear)) continue;
                candidates.Add((path, record.PeriodKey, SafeGetLastWriteTimeUtc(path)));
            }
            catch (Exception exception)
            {
                AutomationDiagnosticLog.Write("GST-LINKED-REPAIR", $"Linked GSTR-3B candidate deferred: {exception.Message}", path);
            }
        }

        var imported = 0;
        foreach (var group in candidates
                     .GroupBy(item => item.PeriodKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (validPeriods.Contains(group.Key)) continue;
            var candidate = group.OrderByDescending(item => item.WrittenUtc).First();
            var result = await Task.Run(
                () => _gstr3bPdfImportService.Import(
                    [candidate.Path],
                    client.Id,
                    client.FinancialYear,
                    client.Gstin),
                cancellationToken);

            if (result.PeriodsImported <= 0) continue;
            imported += result.PeriodsImported;
            var refreshed = _gstRepository.GetReturns(client.Id, client.FinancialYear)
                .FirstOrDefault(row => row.PeriodKey.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            if (refreshed?.ParseValid == true)
            {
                validPeriods.Add(group.Key);
                ResolveAutomationAction(registration, "GST", SafeHash(candidate.Path));
            }

            var detail = refreshed?.ParseValid == true
                ? "Re-read an already-linked GSTR-3B with the current parser and restored it as a valid GST period."
                : $"Re-read an already-linked GSTR-3B but it still requires review: {string.Join(" | ", result.Warnings.Take(3))}";
            RecordHistory(
                registration,
                candidate.Path,
                SafeHash(candidate.Path),
                "GSTR-3B PDF",
                refreshed?.ParseValid == true ? "Imported" : "Needs Action",
                detail,
                result.PeriodsImported);
            if (refreshed?.ParseValid != true)
                AddAction(registration, "GST", "GSTR-3B Import Review", Path.GetFileName(candidate.Path), detail, SafeHash(candidate.Path));
        }

        return imported;
    }

    private static bool IsPathInsideFolder(string path, string folder)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullFolder = Path.GetFullPath(folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildCollisionSafeTarget(string folder, string fileName)
    {
        var target = Path.Combine(folder, fileName);
        if (!File.Exists(target)) return target;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index <= 9999; index++)
        {
            target = Path.Combine(folder, $"{stem}_{index:000}{extension}");
            if (!File.Exists(target)) return target;
        }

        return Path.Combine(folder, $"{stem}_{Guid.NewGuid():N}{extension}");
    }

    private static GstinProfileLookupResult CopyGstinProfileWithSourcePath(
        GstinProfileLookupResult source,
        string sourcePath) =>
        new()
        {
            Gstin = source.Gstin,
            LegalName = source.LegalName,
            TradeName = source.TradeName,
            Pan = source.Pan,
            State = source.State,
            SourceType = source.SourceType,
            SourcePath = sourcePath,
            SourceModifiedUtc = source.SourceModifiedUtc,
            Gstr3bYear = source.Gstr3bYear,
            Gstr3bPeriod = source.Gstr3bPeriod,
            FilingFrequency = source.FilingFrequency,
            PeriodStartKey = source.PeriodStartKey,
            PeriodEndKey = source.PeriodEndKey,
            Arn = source.Arn,
            ArnDate = source.ArnDate
        };

    private static bool IsPeriodInFinancialYear(string periodKey, string financialYear)
    {
        if (periodKey.Length != 6 ||
            !int.TryParse(periodKey[..4], out var year) ||
            !int.TryParse(periodKey[4..], out var month) ||
            month is < 1 or > 12)
            return false;

        var startText = new string(financialYear
            .TakeWhile(character => character != '-')
            .Where(char.IsDigit)
            .ToArray());
        if (!int.TryParse(startText, out var startYear) || startYear < 2000)
            return false;

        return month >= 4 ? year == startYear : year == startYear + 1;
    }

    private static DateTime SafeGetLastWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    public void CancelRegistration(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        var path = NormalizePath(folderPath);
        _pendingRegistrations.TryRemove(path, out _);
        // Do not reopen the same first-time prompt every two seconds while the
        // folder is still open in Explorer. It becomes eligible again only after
        // the user closes that Explorer location and opens it later.
        _dismissedRegistrations.TryAdd(path, 0);
    }

    public void SetFolderPaused(AutomationFolderRegistration folder, bool paused)
    {
        _automationRepository.SetPaused(folder.FolderId, paused);
        if (paused)
            RemoveWatcher(folder.FolderId);
        else
        {
            var refreshed = _automationRepository.GetByFolderId(folder.FolderId);
            if (refreshed is not null) EnsureWatcher(refreshed);
        }
        FoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UnlinkFolder(AutomationFolderRegistration folder)
    {
        RemoveWatcher(folder.FolderId);
        _automationRepository.Unlink(folder.FolderId);
        FoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OpenFolder(AutomationFolderRegistration folder)
    {
        if (!Directory.Exists(folder.FolderPath)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{folder.FolderPath}\"",
            UseShellExecute = true
        });
    }

    public void ScanFolder(AutomationFolderRegistration folder)
    {
        // Settings -> selected-folder Force Scan has the same meaning as Dashboard ->
        // Auto Import From Folder: this exact Client/FY only. Managed Customer Master
        // evidence is refreshed separately and GSTR-3B stays on its established GST path.
        ProcessDownloadedCustomerMasterAsync(folder, CancellationToken.None).GetAwaiter().GetResult();
        ScheduleRegisteredClientGstBootstrap(folder, folder.ClientId);
        QueueExistingFiles(folder, force: true, forceScanCommand: true);
    }

    public AutomationRenameResult RenameLinkedFolders(string newFolderName)
    {
        newFolderName = SanitizeFolderName(newFolderName);
        if (newFolderName.Length == 0)
            return new AutomationRenameResult(0, 0, ["The Automation Folder Name is blank or invalid."]);

        var renamed = 0;
        var skipped = 0;
        var issues = new List<string>();

        foreach (var folder in _automationRepository.GetFolders())
        {
            if (!Directory.Exists(folder.FolderPath))
            {
                skipped++;
                issues.Add($"Missing: {folder.FolderPath}");
                continue;
            }
            if (Path.GetFileName(folder.FolderPath).Equals(newFolderName, StringComparison.OrdinalIgnoreCase))
                continue;

            var parent = Directory.GetParent(folder.FolderPath)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
            {
                skipped++;
                issues.Add($"Cannot rename root folder: {folder.FolderPath}");
                continue;
            }
            var target = Path.Combine(parent, newFolderName);
            if (Directory.Exists(target) || File.Exists(target))
            {
                skipped++;
                issues.Add($"Already exists: {target}");
                continue;
            }

            _folderMoveQuiesce[folder.FolderId] = 0;
            RemoveWatcher(folder.FolderId);
            var physicalMoveCompleted = false;
            try
            {
                MoveDirectoryWithRetry(folder.FolderPath, target, TimeSpan.FromSeconds(8));
                physicalMoveCompleted = true;
                RebaseAutomationFolderPaths([
                    (folder.FolderId, folder.FolderPath, target, newFolderName)
                ]);
                renamed++;
                AutomationDiagnosticLog.Write(
                    "FOLDER-REBASE",
                    $"Linked Automation Folder renamed without changing FolderId/client/FY ownership. Old={folder.FolderPath}; New={target}",
                    target);
            }
            catch (Exception exception)
            {
                if (physicalMoveCompleted && Directory.Exists(target) && !Directory.Exists(folder.FolderPath))
                {
                    try { MoveDirectoryWithRetry(target, folder.FolderPath, TimeSpan.FromSeconds(3)); }
                    catch (Exception rollbackException)
                    {
                        AutomationDiagnosticLog.Write(
                            "FOLDER-REBASE",
                            $"Folder path database update failed after the physical rename and rollback also failed: {rollbackException.Message}",
                            target);
                    }
                }
                skipped++;
                issues.Add($"{folder.FolderPath}: {exception.Message}");
            }
            finally
            {
                _folderMoveQuiesce.TryRemove(folder.FolderId, out _);
                var refreshed = _automationRepository.GetByFolderId(folder.FolderId);
                if (refreshed is not null && Directory.Exists(refreshed.FolderPath))
                {
                    EnsureWatcher(refreshed);
                    QueueExistingFiles(refreshed, force: false);
                }
            }
        }

        FoldersChanged?.Invoke(this, EventArgs.Empty);
        return new AutomationRenameResult(renamed, skipped, issues);
    }

    private void RebaseAutomationFolderPaths(
        IReadOnlyList<(string FolderId, string OldFolderPath, string NewFolderPath, string FolderName)> rebases)
    {
        if (rebases.Count == 0) return;

        // FIXED41 R8: AutomationFileHistory and the global Sales/Purchase source cache must
        // cross a physical folder move together. The latter is content authority for grouped
        // GSTR-1/2A/2B changed-only routing; leaving its path at the old root made an unchanged
        // moved Sales folder look like a growing new source set and repeatedly re-enter import.
        _automationRepository.RebaseFolderPaths(rebases);
        SalesPurchaseSourceCache.RebaseRootPaths(
            rebases.Select(item => (item.OldFolderPath, item.NewFolderPath)).ToArray());
    }

    private void QueueLinkedFolderObservation(string folderId, string path)
    {
        if (_folderMoveQuiesce.ContainsKey(folderId)) return;
        // FIXED28: Created/Changed/Renamed bursts from one Office/ZIP save must pass
        // through the normal size+mtime observation barrier. Force is reserved for
        // explicit/manual/recovery paths; using force here could enqueue every event.
        QueueFile(folderId, path, force: false);

        // Preserve Fix38 source-first Sales/Purchase indexing without attaching a
        // second watcher to the same linked Automation Folder. Respect the same
        // global/folder pause contract the old central linked-root watcher used.
        var folder = _automationRepository.GetByFolderId(folderId);
        if (folder is not null && !folder.IsPaused && _settings.GetBool(ApplicationSettingKeys.AutomationEnabled))
            QueueCentralSalesPurchaseSource(path, force: false);
    }

    private static void MoveDirectoryWithRetry(string source, string target, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        do
        {
            try
            {
                Directory.Move(source, target);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                last = exception;
                if (DateTime.UtcNow >= deadline) break;
                Thread.Sleep(200);
            }
        }
        while (DateTime.UtcNow < deadline);

        throw new IOException(
            $"The linked folder is still busy after AAH released its watcher. Close any external program that is actively using a file in this folder and retry. {last?.Message}",
            last);
    }

    private void DiscoverMovedLinkedFoldersUnder(string explorerRoot)
    {
        if (string.IsNullOrWhiteSpace(explorerRoot) || !Directory.Exists(explorerRoot)) return;

        // FIXED28: do not recursively touch Explorer trees unless a registered linked
        // folder is actually missing. Relocation recovery is single-flight and backed
        // off; the explicit Dashboard Locate/Link command remains immediate.
        var missing = _automationRepository.GetFolders()
            .Where(folder => !Directory.Exists(folder.FolderPath))
            .ToDictionary(folder => folder.FolderId, StringComparer.OrdinalIgnoreCase);
        if (missing.Count == 0) return;

        var now = DateTime.UtcNow;
        if (now - _lastFolderRelocationDiscoveryUtc < FolderRelocationDiscoveryInterval) return;
        if (Interlocked.CompareExchange(ref _folderRelocationDiscoveryInFlight, 1, 0) != 0) return;
        _lastFolderRelocationDiscoveryUtc = now;

        try
        {
            var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<(string Path, int Depth)>();
            pending.Enqueue((explorerRoot, 0));
            var visited = 0;

            while (pending.Count > 0 && visited < MaxRelocationDirectories)
            {
                var (current, depth) = pending.Dequeue();
                visited++;
                try
                {
                    var markerId = ReadMarker(current);
                    if (markerId.Length > 0 && missing.ContainsKey(markerId))
                    {
                        if (!found.TryGetValue(markerId, out var paths))
                        {
                            paths = [];
                            found[markerId] = paths;
                        }
                        paths.Add(NormalizePath(current));
                    }

                    if (depth >= MaxRelocationDepth) continue;
                    foreach (var child in Directory.EnumerateDirectories(current))
                    {
                        try
                        {
                            var attributes = File.GetAttributes(child);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if ((attributes & FileAttributes.System) != 0) continue;
                            pending.Enqueue((child, depth + 1));
                        }
                        catch { }
                    }
                }
                catch
                {
                    // Best-effort only. Explicit Locate/Link remains available.
                }
            }

            var rebases = new List<(string FolderId, string OldFolderPath, string NewFolderPath, string FolderName)>();
            foreach (var pair in found)
            {
                if (!missing.TryGetValue(pair.Key, out var registration)) continue;
                var unique = pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (unique.Length != 1)
                {
                    if (unique.Length > 1)
                    {
                        AutomationDiagnosticLog.Write(
                            "FOLDER-REBASE",
                            $"Automatic relocation refused because multiple folders carry the same durable FolderId {pair.Key}: {string.Join(" | ", unique)}");
                    }
                    continue;
                }

                var newPath = unique[0];
                var occupant = _automationRepository.GetByPath(newPath);
                if (occupant is not null && !occupant.FolderId.Equals(registration.FolderId, StringComparison.OrdinalIgnoreCase))
                    continue;

                rebases.Add((registration.FolderId, registration.FolderPath, newPath, Path.GetFileName(newPath)));
            }

            if (rebases.Count == 0) return;

            foreach (var rebase in rebases)
            {
                _folderMoveQuiesce[rebase.FolderId] = 0;
                RemoveWatcher(rebase.FolderId);
            }

            try
            {
                RebaseAutomationFolderPaths(rebases);
                foreach (var rebase in rebases)
                {
                    AutomationDiagnosticLog.Write(
                        "FOLDER-REBASE",
                        $"Detected moved base folder and rebound the existing linked Automation Folder by durable FolderId. Old={rebase.OldFolderPath}; New={rebase.NewFolderPath}",
                        rebase.NewFolderPath);
                }
            }
            finally
            {
                foreach (var rebase in rebases)
                {
                    _folderMoveQuiesce.TryRemove(rebase.FolderId, out _);
                    var refreshed = _automationRepository.GetByFolderId(rebase.FolderId);
                    if (refreshed is null || !Directory.Exists(refreshed.FolderPath)) continue;
                    EnsureWatcher(refreshed);
                    QueueExistingFiles(refreshed, force: false);
                }
            }

            FoldersChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            Volatile.Write(ref _folderRelocationDiscoveryInFlight, 0);
        }
    }

    private void ExplorerLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var foregroundExplorer = EnumerateExplorerLocations(onlyForeground: true).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(foregroundExplorer))
                {
                    _lastActiveExplorerPath = foregroundExplorer;
                    // R52 FIXED26: relocation repair is ownership maintenance, not an
                    // Auto Import action. Keep it active even when automatic importing is
                    // disabled/paused so a moved base folder can still rebind safely.
                    DiscoverMovedLinkedFoldersUnder(foregroundExplorer);
                }

                if (_settings.GetBool(ApplicationSettingKeys.AutomationEnabled))
                {
                    var openLocations = EnumerateExplorerLocations(onlyForeground: false)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var openSet = openLocations.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var dismissed in _dismissedRegistrations.Keys)
                    {
                        if (!openSet.Contains(dismissed))
                            _dismissedRegistrations.TryRemove(dismissed, out _);
                    }

                    foreach (var path in openLocations)
                        DiscoverFolder(path, forceRegistrationPrompt: false);
                }
            }
            catch
            {
                // Explorer discovery is best-effort. File watchers continue independently.
            }

            if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
                break;
        }
    }

    private IReadOnlyList<string> EnumerateExplorerLocations(bool onlyForeground)
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows()) return result;

        object? shellObject = null;
        object? windowsObject = null;
        var foreground = onlyForeground ? GetForegroundWindow() : IntPtr.Zero;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return result;
            shellObject = Activator.CreateInstance(shellType);
            if (shellObject is null) return result;
            dynamic shell = shellObject;
            windowsObject = shell.Windows();
            dynamic windows = windowsObject;
            var count = Convert.ToInt32(windows.Count);
            for (var index = 0; index < count; index++)
            {
                object? windowObject = null;
                try
                {
                    windowObject = windows.Item(index);
                    if (windowObject is null) continue;
                    dynamic window = windowObject;
                    if (onlyForeground)
                    {
                        var hwnd = new IntPtr(Convert.ToInt64(window.HWND));
                        if (hwnd != foreground) continue;
                    }
                    string locationUrl = Convert.ToString((object?)window.LocationURL) ?? string.Empty;
                    if (!Uri.TryCreate(locationUrl, UriKind.Absolute, out Uri? uri) || !uri.IsFile) continue;
                    var local = Uri.UnescapeDataString(uri.LocalPath);
                    if (!Directory.Exists(local)) continue;
                    result.Add(NormalizePath(local));
                }
                catch
                {
                    // Ignore non-file shell windows such as Control Panel.
                }
                finally
                {
                    if (windowObject is not null && Marshal.IsComObject(windowObject))
                        Marshal.FinalReleaseComObject(windowObject);
                }
            }
        }
        finally
        {
            if (windowsObject is not null && Marshal.IsComObject(windowsObject))
                Marshal.FinalReleaseComObject(windowsObject);
            if (shellObject is not null && Marshal.IsComObject(shellObject))
                Marshal.FinalReleaseComObject(shellObject);
        }
        return result;
    }

    private void DiscoverFolder(string path, bool forceRegistrationPrompt)
    {
        path = NormalizePath(path);
        if (!Directory.Exists(path) || !IsAllowedByRoot(path)) return;

        var markerId = ReadMarker(path);
        if (markerId.Length > 0)
        {
            var existingById = _automationRepository.GetByFolderId(markerId);
            if (existingById is not null)
            {
                if (!PathsEqual(existingById.FolderPath, path))
                {
                    if (Directory.Exists(existingById.FolderPath))
                    {
                        var detail = $"Two folders contain the same Hub Folder ID. The copy was NOT linked automatically.\nOriginal: {existingById.FolderPath}\nSource: {path}";
                        AddAction(
                            existingById,
                            "Automation",
                            "Duplicate Folder ID",
                            "Duplicate Automation Folder detected",
                            detail,
                            $"FOLDER-DUP:{markerId}:{path}");
                        Notify(
                            "Duplicate Automation Folder detected",
                            $"Two folders contain the same Hub Folder ID. Open Settings → Auto Import & Folders to review the copy at '{path}'.",
                            false);
                        return;
                    }
                    RebaseAutomationFolderPaths([
                        (existingById.FolderId, existingById.FolderPath, path, Path.GetFileName(path))
                    ]);
                    existingById = _automationRepository.GetByFolderId(markerId);
                    FoldersChanged?.Invoke(this, EventArgs.Empty);
                }
                if (existingById is not null) EnsureWatcher(existingById);
                return;
            }
        }

        var byPath = _automationRepository.GetByPath(path);
        if (byPath is not null)
        {
            if (markerId.Length == 0) WriteMarker(path, byPath.FolderId);
            EnsureWatcher(byPath);
            return;
        }

        var configuredName = GetConfiguredFolderName();
        if (!forceRegistrationPrompt &&
            !Path.GetFileName(path).Equals(configuredName, StringComparison.OrdinalIgnoreCase))
            return;

        if (forceRegistrationPrompt)
            _dismissedRegistrations.TryRemove(path, out _);
        else if (_dismissedRegistrations.ContainsKey(path))
            return;

        if (!_pendingRegistrations.TryAdd(path, 0)) return;
        if (_settings.GetBool(ApplicationSettingKeys.NotifyFolderRegistration))
            Notify("New Accounting Automation Folder", $"Complete Name, GSTIN and Financial Year for:\n{path}", false);
        FolderRegistrationRequested?.Invoke(
            this,
            new AutomationFolderRegistrationRequestEventArgs(path));
    }

    private void RefreshWatchers()
    {
        var folders = _automationRepository.GetFolders();
        // Keep a lightweight watcher on every valid linked folder even when normal
        // Auto Import is paused/disabled. Reserved AAH_REVIEW_ workbooks use this same
        // linked folder but have their own delta-sync route. Normal accounting files
        // are still blocked by the Auto Import enable/pause checks in QueueFile.
        var activeIds = folders.Where(folder => Directory.Exists(folder.FolderPath))
            .Select(folder => folder.FolderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _watchers.Keys)
            if (!activeIds.Contains(key)) RemoveWatcher(key);
        foreach (var folder in folders)
            if (Directory.Exists(folder.FolderPath)) EnsureWatcher(folder);
    }

    private void EnsureWatcher(AutomationFolderRegistration folder)
    {
        if (!Directory.Exists(folder.FolderPath)) return;
        PortalCredentialFileService.EnsureFile(folder);
        // Legacy migration only: EnsureFile already migrates/deletes an old plaintext file.
        // Keep this guarded read for read-only folders where deletion may not be possible.
        var portalCredentialPath = PortalCredentialFileService.PathFor(folder.FolderPath);
        if (File.Exists(portalCredentialPath) &&
            PortalCredentialFileService.TryImport(portalCredentialPath, folder.ClientId, folder.FinancialYear, out var startupPortalMessage))
        {
            AutomationDiagnosticLog.Write("PORTAL-CREDENTIALS", startupPortalMessage, portalCredentialPath);
        }
        if (_watchers.ContainsKey(folder.FolderId)) return;

        var watcher = new FileSystemWatcher(folder.FolderPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = false
        };
        watcher.Created += (_, args) => QueueLinkedFolderObservation(folder.FolderId, args.FullPath);
        watcher.Changed += (_, args) => QueueLinkedFolderObservation(folder.FolderId, args.FullPath);
        watcher.Renamed += (_, args) => QueueLinkedFolderObservation(folder.FolderId, args.FullPath);
        watcher.Error += (_, args) =>
        {
            AutomationDiagnosticLog.Write("WATCHER-ERROR", args.GetException()?.ToString() ?? "Unknown FileSystemWatcher error", folder.FolderPath);
            RemoveWatcher(folder.FolderId);
            var refreshed = _automationRepository.GetByFolderId(folder.FolderId);
            if (refreshed is null || !Directory.Exists(refreshed.FolderPath)) return;
            EnsureWatcher(refreshed);
            // FileSystemWatcher can lose events when its internal buffer overflows.
            // Reconciliation scanning is the source of truth, so immediately rescan
            // after watcher recovery instead of assuming no events were missed.
            QueueExistingFiles(refreshed, force: false);
            if (args.GetException() is InternalBufferOverflowException)
                Notify("Auto Import watcher recovered", $"A file-system event buffer overflow occurred for {refreshed.FolderPath}. The folder was rescanned automatically.", false);
        };

        if (_watchers.TryAdd(folder.FolderId, watcher))
        {
            watcher.EnableRaisingEvents = true;
            AutomationDiagnosticLog.Write("WATCHER", $"Watching client={folder.ClientName} FY={folder.FinancialYear} recursive=true", folder.FolderPath);
        }
        else
            watcher.Dispose();
    }

    private void RemoveWatcher(string folderId)
    {
        if (!_watchers.TryRemove(folderId, out var watcher)) return;
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    private void DisposeWatchers()
    {
        foreach (var key in _watchers.Keys.ToArray()) RemoveWatcher(key);
    }

    private void ConfigureCentralSalesPurchaseWatcher()
    {
        // FIXED28: watcher error-rebuild callbacks are delayed. Never recreate shared
        // watchers after shutdown has begun. Start() sets _started/lifetime before this
        // method is first called, and settings refreshes reuse that same lifetime.
        if (!_started || _lifetime?.IsCancellationRequested != false) return;

        lock (_salesPurchaseClientSnapshotGate)
        {
            _salesPurchaseActiveClientSnapshot = [];
            _salesPurchaseActiveClientSnapshotUtc = DateTime.MinValue;
        }
        lock (_salesPurchaseWatcherGate)
        {
            foreach (var watcher in _salesPurchaseSourceWatchers)
            {
                try { watcher.EnableRaisingEvents = false; watcher.Dispose(); }
                catch { }
            }
            _salesPurchaseSourceWatchers.Clear();

            if (!_settings.GetBool(ApplicationSettingKeys.AutomationEnabled)) return;

            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var eReturnsRoot = _settings.Get(EReturnsRootContext.PreferenceKey).Trim();
            if (Directory.Exists(eReturnsRoot)) roots.Add(Path.GetFullPath(eReturnsRoot));
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(downloads)) roots.Add(Path.GetFullPath(downloads));
            // R52 FIXED26: linked Automation Folders are already covered by their one
            // ownership watcher. Do NOT create a second long-lived FileSystemWatcher for
            // those same directories. QueueLinkedFolderObservation fans eligible files
            // into the shared Sales/Purchase source index without a duplicate directory
            // handle. Startup indexing still includes linked roots via GetCentralSalesPurchaseRoots.

            foreach (var root in roots)
            {
                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                        InternalBufferSize = 64 * 1024,
                        EnableRaisingEvents = false
                    };
                    watcher.Created += (_, args) => QueueCentralSalesPurchaseSource(args.FullPath, force: false);
                    watcher.Changed += (_, args) => QueueCentralSalesPurchaseSource(args.FullPath, force: false);
                    watcher.Renamed += (_, args) => QueueCentralSalesPurchaseSource(args.FullPath, force: false);
                    watcher.Error += (_, args) =>
                    {
                        AutomationDiagnosticLog.Write("SALES-PURCHASE-WATCHER", $"Shared source watcher will be rebuilt after error: {args.GetException()?.Message}", root);
                        var rebuildToken = _lifetime?.Token ?? CancellationToken.None;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(1500, rebuildToken).ConfigureAwait(false);
                                if (!_started || rebuildToken.IsCancellationRequested) return;
                                ConfigureCentralSalesPurchaseWatcher();
                            }
                            catch (OperationCanceledException) when (rebuildToken.IsCancellationRequested) { }
                            catch { }
                        }, rebuildToken);
                    };
                    watcher.EnableRaisingEvents = true;
                    _salesPurchaseSourceWatchers.Add(watcher);
                    AutomationDiagnosticLog.Write("SALES-PURCHASE-WATCHER", "Fix38 shared Sales/Purchase source watcher active.", root);
                }
                catch (Exception exception)
                {
                    AutomationDiagnosticLog.Write("SALES-PURCHASE-WATCHER", $"Shared watcher unavailable: {exception.Message}", root);
                }
            }
        }
    }

    private void DisposeCentralSalesPurchaseWatcher()
    {
        lock (_salesPurchaseWatcherGate)
        {
            foreach (var watcher in _salesPurchaseSourceWatchers)
            {
                try { watcher.EnableRaisingEvents = false; watcher.Dispose(); }
                catch { }
            }
            _salesPurchaseSourceWatchers.Clear();
        }
    }

    private async Task CentralSalesPurchaseStartupScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Fix38: file-system events are attached immediately, but the historical shared
            // source scan waits behind local paint-ready client cache preparation. This keeps
            // source discovery continuously available without competing with startup screen prep.
            while (!IsLocalPreparationReady())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }

            // Bound historical indexing so a very large E-returns/Downloads tree never
            // creates thousands of waiting Task objects. Live watcher events remain immediate;
            // startup indexing keeps only a small multiple of the parsing gate in flight.
            var pending = new List<Task>();
            foreach (var root in GetCentralSalesPurchaseRoots())
            {
                cancellationToken.ThrowIfCancellationRequested();
                IEnumerable<string> paths;
                try { paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var candidate in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCentralSalesPurchaseCandidate(candidate)) continue;
                    string path;
                    try { path = Path.GetFullPath(candidate); }
                    catch { continue; }
                    if (!_salesPurchaseSourceInFlight.TryAdd(path, 0)) continue;

                    pending.Add(ProcessCentralSalesPurchaseStartupCandidateAsync(path, cancellationToken));
                    if (pending.Count < 12) continue;
                    var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(completed);
                    await completed.ConfigureAwait(false);
                }
            }
            if (pending.Count > 0) await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AutomationDiagnosticLog.Write("SALES-PURCHASE-WATCHER", $"Shared startup source index deferred: {exception.Message}");
        }
        finally
        {
            _centralSourceStartupComplete = true;
            QueueAllActiveDashboardOutputs();
        }
    }

    private async Task ProcessCentralSalesPurchaseStartupCandidateAsync(string path, CancellationToken cancellationToken)
    {
        try { await ProcessCentralSalesPurchaseSourceAsync(path, cancellationToken, allowHandledMetadataFastSkip: true).ConfigureAwait(false); }
        finally { _salesPurchaseSourceInFlight.TryRemove(path, out _); }
    }

    private IReadOnlyList<string> GetCentralSalesPurchaseRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eReturnsRoot = _settings.Get(EReturnsRootContext.PreferenceKey).Trim();
        if (Directory.Exists(eReturnsRoot)) roots.Add(Path.GetFullPath(eReturnsRoot));
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) roots.Add(Path.GetFullPath(downloads));
        foreach (var folder in _automationRepository.GetFolders())
            if (!folder.IsPaused && Directory.Exists(folder.FolderPath)) roots.Add(Path.GetFullPath(folder.FolderPath));
        return roots.ToArray();
    }

    private void QueueCentralSalesPurchaseSource(string path, bool force = true)
    {
        if (_lifetime?.IsCancellationRequested != false) return;
        if (!IsCentralSalesPurchaseCandidate(path)) return;
        try { path = Path.GetFullPath(path); }
        catch { return; }

        if (!force)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return;
                var state = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
                if (_salesPurchaseObservedStates.TryGetValue(path, out var existing) && existing == state) return;
            }
            catch { return; }
        }

        PruneSalesPurchaseObservedStatesIfNeeded();
        if (!_salesPurchaseLiveQueuedKeys.TryAdd(path, 0)) return;
        if (_salesPurchaseLiveQueue.Count >= MaxSalesPurchaseLiveQueueDepth)
        {
            _salesPurchaseLiveQueuedKeys.TryRemove(path, out _);
            AutomationDiagnosticLog.Write(
                "SALES-PURCHASE-BACKPRESSURE",
                $"Live Sales/Purchase watcher queue is bounded at {MaxSalesPurchaseLiveQueueDepth:N0}; a later file event or startup/recovery index pass can offer this path again.",
                path);
            return;
        }

        _salesPurchaseLiveQueue.Enqueue(path);
        try { _salesPurchaseLiveQueueSignal.Release(); }
        catch (ObjectDisposedException) { _salesPurchaseLiveQueuedKeys.TryRemove(path, out _); }
    }

    private async Task SalesPurchaseLiveWorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _salesPurchaseLiveQueueSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!_salesPurchaseLiveQueue.TryDequeue(out var path)) continue;
            try
            {
                // Startup indexing and the live watcher can meet on the same path. One
                // source-first parse is enough; the stable-file + metadata/hash barriers
                // inside ProcessCentralSalesPurchaseSourceAsync remain authoritative.
                if (!_salesPurchaseSourceInFlight.TryAdd(path, 0)) continue;
                try
                {
                    await ProcessCentralSalesPurchaseSourceAsync(path, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _salesPurchaseSourceInFlight.TryRemove(path, out _);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AutomationDiagnosticLog.Write("SALES-PURCHASE-WATCHER", $"Live source processing deferred: {exception.Message}", path);
            }
            finally
            {
                _salesPurchaseLiveQueuedKeys.TryRemove(path, out _);
            }
        }
    }

    private void PruneSalesPurchaseObservedStatesIfNeeded()
    {
        if (_salesPurchaseObservedStates.Count < 8192) return;
        var now = DateTime.UtcNow;
        if (now - _lastSalesPurchaseObservedStatePruneUtc < TimeSpan.FromMinutes(10)) return;
        _lastSalesPurchaseObservedStatePruneUtc = now;

        var removed = 0;
        foreach (var key in _salesPurchaseObservedStates.Keys)
        {
            if (removed >= 1024) break;
            try
            {
                if (File.Exists(key)) continue;
            }
            catch { }
            if (_salesPurchaseObservedStates.TryRemove(key, out _)) removed++;
        }

        if (_salesPurchaseObservedStates.Count > 16384)
        {
            foreach (var key in _salesPurchaseObservedStates.Keys.Take(_salesPurchaseObservedStates.Count - 12288))
                _salesPurchaseObservedStates.TryRemove(key, out _);
        }
    }

    private async Task ProcessCentralSalesPurchaseSourceAsync(string path, CancellationToken cancellationToken, bool allowHandledMetadataFastSkip = false)
    {
        await _salesPurchaseIndexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        try
        {
            // Fix131: historical startup indexing must not reopen/reclassify/rehash every
            // unchanged Sales/Purchase source on every launch. The saved source cache can
            // prove an exact size+mtime path is already part of a handled source-set. Live
            // watcher events deliberately bypass this optimization and continue through the
            // normal stability/classification/hash path.
            if (allowHandledMetadataFastSkip)
            {
                try
                {
                    var cachedInfo = new FileInfo(path);
                    if (!cachedInfo.Exists) return;
                    if (SalesPurchaseSourceCache.IsPathMetadataCurrentAndHandled(
                            path, cachedInfo.Length, cachedInfo.LastWriteTimeUtc))
                    {
                        _salesPurchaseObservedStates[path] = $"{cachedInfo.Length}:{cachedInfo.LastWriteTimeUtc.Ticks}";
                        return;
                    }
                }
                catch
                {
                    // Optimization only; fall through to the normal source authority path.
                }
            }
            // R22 migration safety remains intact: an older workbook that was merely indexed
            // but never handled has no Current/Imported handled-set proof, so it cannot take
            // the fast skip above and is re-evaluated once through normal ownership/history.

            // FIXED41 R8 Repair 4 safety bar: a historical startup source that has already
            // been closed/stable for longer than the configured write-settle window does not
            // need another 1-second polling loop before classification. Live watcher events
            // still use WaitForStableFileAsync exactly as before, so active copies/downloads
            // retain the same protection and intended behavior.
            if (!(allowHandledMetadataFastSkip && IsHistoricalCentralSourceStable(path)) &&
                !await WaitForStableFileAsync(path, cancellationToken).ConfigureAwait(false)) return;
            var info = new FileInfo(path);
            if (!info.Exists) return;
            var state = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            if (_salesPurchaseObservedStates.TryGetValue(path, out var previous) && previous == state) return;
            _salesPurchaseObservedStates[path] = state;

            var kind = DetectCentralSalesPurchaseKind(path);
            if (kind is not (AutomationFileKind.Sales or AutomationFileKind.Purchase)) return;
            var isSourceSetCandidate = IsCentralSalesPurchaseSourceSetCandidate(path, kind);

            // R22: ordinary Winman/E-returns Sales/Purchase workbooks must not disappear
            // merely because they are not GSTR source-set components. They still go through
            // the source-first GSTIN/FY ownership proof below and are then handed to the
            // normal file-oriented Auto Import worker with an explicit module classification.
            // JSON/ZIP and Sales customer-master components retain grouped source-set routing.

            // Fix38 source-first identity authority: read/classify the source once, extract its
            // own GSTIN/FY without consulting the client registry, and persist that identity in
            // the shared source cache before any Client/FY routing decision is made.
            string identityText;
            try { identityText = _fileClassifier.GetIdentitySearchText(path); }
            catch { identityText = Path.GetFileName(path); }

            var sourceGstins = ExtractCentralSourceOwnerGstins(path, identityText);
            if (sourceGstins.Count == 0)
            {
                AutomationDiagnosticLog.Write(
                    "SALES-PURCHASE-WATCHER",
                    "Source-first GSTIN identity was not provable; source cached/routed only after a later identifiable change or explicit Auto Import classification.",
                    path);
                return;
            }

            var financialYearHint = DetectSourceFinancialYearHint(path, identityText);
            var fileHash = ComputeHash(path);
            var module = kind == AutomationFileKind.Sales ? "Sales" : "Purchase";

            // Cache source identity first. A blank FY is intentional when the source does not
            // prove its period; GetPaths can still use it as shared read-only source evidence.
            foreach (var sourceGstin in sourceGstins)
            {
                SalesPurchaseSourceCache.Record(
                    path,
                    module,
                    sourceGstin,
                    financialYearHint,
                    fileHash,
                    info.Length,
                    info.LastWriteTimeUtc);
            }

            // Only after the source owns its identity do we divide it to active Client/FY rows.
            var sourceGstinSet = sourceGstins.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matchingClients = GetCentralSalesPurchaseActiveClients()
                .Where(client => sourceGstinSet.Contains(NormalizeGstin(client.Gstin)))
                .Where(client => financialYearHint.Length == 0 ||
                                 NormalizeFinancialYearHint(client.FinancialYear).Equals(financialYearHint, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            // If FY is not source-provable and the same GSTIN owns multiple active FYs, do not
            // guess the destination. The source remains in the shared GSTIN cache and an
            // explicit/linked-folder Auto Import pass can resolve the Client+FY safely.
            if (financialYearHint.Length == 0)
            {
                matchingClients = matchingClients
                    .GroupBy(client => NormalizeGstin(client.Gstin), StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() == 1)
                    .SelectMany(group => group)
                    .ToArray();
            }

            // Fix39: GST-return Sales/Purchase sources are SOURCE-SET inputs, not standalone
            // Sales Register/Purchase files. Never push an individual GSTR-1/2A/2B component
            // through QueueFile: that file-oriented route invokes the manual Sales header parser
            // and made Purchase treat each component as a separate import attempt.
            //
            // A registered Automation Folder still owns Client+FY when the source lives inside
            // it. For shared E-returns/Downloads sources, source GSTIN/FY decides which ACTIVE
            // Client+FY source-set is scheduled. Auto Import remains the only automatic/background accounting writer; user-triggered Manual Import remains separate.
            var registeredOwner = _automationRepository.GetFolders()
                .Where(folder => !folder.IsPaused && Directory.Exists(folder.FolderPath))
                .FirstOrDefault(folder => IsPathInsideFolder(path, folder.FolderPath));

            if (registeredOwner is not null)
            {
                var ownerClient = GetCentralSalesPurchaseActiveClients()
                    .FirstOrDefault(client => client.Id == registeredOwner.ClientId &&
                                              client.FinancialYear.Equals(registeredOwner.FinancialYear, StringComparison.OrdinalIgnoreCase));
                if (ownerClient is not null)
                {
                    var ownerGstin = NormalizeGstin(ownerClient.Gstin);
                    if (ownerGstin.Length == 15)
                    {
                        SalesPurchaseSourceCache.Record(
                            path, module, ownerGstin, ownerClient.FinancialYear, fileHash, info.Length, info.LastWriteTimeUtc);
                        if (isSourceSetCandidate)
                            QueueSalesPurchaseSourceSet(ownerClient.Id, ownerClient.FinancialYear, module);
                        else
                            QueueFile(registeredOwner.FolderId, path, force: true, manualReimport: false, forcedKind: kind, precomputedHash: fileHash);
                    }
                }
                return;
            }

            if (!isSourceSetCandidate)
            {
                // Shared Winman/E-returns workbook: source evidence has already proved a
                // unique Client/FY above. Bind it to that Client/FY's registered Automation
                // Folder and use the exact same file importer as an in-folder workbook.
                // Processed-file hash history remains the changed-only barrier, so repeated
                // watcher bursts do not reimport unchanged Sales/Purchase data.
                foreach (var client in matchingClients)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var registration = _automationRepository.GetFolders()
                        .FirstOrDefault(folder => !folder.IsPaused &&
                                                  folder.ClientId == client.Id &&
                                                  folder.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase) &&
                                                  Directory.Exists(folder.FolderPath));
                    if (registration is null)
                    {
                        AutomationDiagnosticLog.Write(
                            "SALES-PURCHASE-WINMAN",
                            $"Source matched {client.ClientName} / {client.FinancialYear}, but no active linked Automation Folder is available; automatic file import deferred.",
                            path);
                        continue;
                    }

                    var queued = QueueFile(
                        registration.FolderId,
                        path,
                        force: true,
                        manualReimport: false,
                        forcedKind: kind,
                        precomputedHash: fileHash);
                    AutomationDiagnosticLog.Write(
                        "SALES-PURCHASE-WINMAN",
                        queued
                            ? $"Automatically routed Winman/E-returns {module} workbook to {client.ClientName} / {client.FinancialYear}."
                            : $"Winman/E-returns {module} workbook was already queued/handled for {client.ClientName} / {client.FinancialYear}.",
                        path);
                }
                return;
            }

            foreach (var client in matchingClients)
            {
                cancellationToken.ThrowIfCancellationRequested();
                QueueSalesPurchaseSourceSet(client.Id, client.FinancialYear, module);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AutomationDiagnosticLog.Write("SALES-PURCHASE-WATCHER", $"Shared source indexing deferred: {exception.Message}", path);
        }
    
        }
        finally
        {
            _salesPurchaseIndexGate.Release();
        }
    }

    private void QueueSalesPurchaseSourceSet(long clientId, string financialYear, string module)
    {
        var normalizedModule = module.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? "Sales"
            : module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) ? "Purchase"
            : string.Empty;
        var normalizedFy = NormalizeFinancialYearHint(financialYear);
        var lifetime = _lifetime;
        if (clientId <= 0 || normalizedModule.Length == 0 || normalizedFy.Length == 0 || lifetime is null || lifetime.IsCancellationRequested) return;

        var key = $"{clientId}|{normalizedFy}|{normalizedModule.ToUpperInvariant()}";
        _salesPurchaseSetVersions.AddOrUpdate(key, 1L, (_, value) => value + 1L);
        if (!_salesPurchaseSetProcessors.TryAdd(key, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var version = _salesPurchaseSetVersions.TryGetValue(key, out var currentVersion) ? currentVersion : 0L;
                    await Task.Delay(1500, lifetime.Token).ConfigureAwait(false);
                    if (_salesPurchaseSetVersions.TryGetValue(key, out var afterDelay) && afterDelay != version) continue;

                    await ProcessSalesPurchaseSourceSetAsync(clientId, normalizedFy, normalizedModule, lifetime.Token).ConfigureAwait(false);
                    if (!_salesPurchaseSetVersions.TryGetValue(key, out var afterRun) || afterRun == version) break;
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                AutomationDiagnosticLog.Write("SALES-PURCHASE-SET", $"Grouped {normalizedModule} Auto Import deferred: {exception.Message}", key);
            }
            finally
            {
                _salesPurchaseSetProcessors.TryRemove(key, out _);
                ProgressChanged?.Invoke(this, EventArgs.Empty);
            }
        }, CancellationToken.None);
    }

    private async Task ProcessSalesPurchaseSourceSetAsync(
        long clientId,
        string financialYear,
        string module,
        CancellationToken cancellationToken)
    {
        if (_globalPause || !_settings.GetBool(ApplicationSettingKeys.AutomationEnabled) ||
            !_settings.GetBool(ApplicationSettingKeys.AutomationAutoImportKnown) ||
            !_clientAutoImportPolicies.Get(clientId).Enabled)
            return;
        if (module.Equals("Purchase", StringComparison.OrdinalIgnoreCase) &&
            !_settings.GetBool(ApplicationSettingKeys.PurchaseAutoImportKnown))
            return;

        var client = _clientRepository.GetById(clientId);
        if (client is null || client.IsArchived || !client.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)) return;
        var gstin = NormalizeGstin(client.Gstin);
        if (gstin.Length != 15) return;

        var folders = _automationRepository.GetFolders()
            .Where(folder => folder.ClientId == clientId &&
                             folder.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase) &&
                             !folder.IsPaused && Directory.Exists(folder.FolderPath))
            .OrderByDescending(folder => folder.UpdatedUtc)
            .ToArray();
        if (folders.Length == 0) return;
        var folder = folders[0];

        var setFingerprint = SalesPurchaseSourceCache.GetSetFingerprint(module, gstin, financialYear);
        if (setFingerprint.Length == 0) return;

        // Fix39 durable source-set gate: after one successful/current pass, an unchanged
        // GSTIN+FY set does not even re-enter the Sales/Purchase parser on later watcher
        // events or app startups. Explicit module reimport remains independent of this state.
        if (SalesPurchaseSourceCache.IsSetHandled(module, gstin, financialYear, setFingerprint))
        {
            AutomationDiagnosticLog.Write("SALES-PURCHASE-SET", $"{module} GSTIN/FY source-set unchanged; grouped Auto Import skipped before parsing.", $"{client.ClientCode}|{financialYear}");
            return;
        }

        await RunCachedClientImportAsync(
            folder,
            async () =>
            {
                // Recheck after taking the Client/FY import gate because another source-set
                // worker may have completed while this one was waiting.
                var currentSetFingerprint = SalesPurchaseSourceCache.GetSetFingerprint(module, gstin, financialYear);
                if (currentSetFingerprint.Length == 0 ||
                    SalesPurchaseSourceCache.IsSetHandled(module, gstin, financialYear, currentSetFingerprint))
                    return;

                try
                {
                    if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase))
                        await ProcessSalesSourceSetAsync(client, folders, currentSetFingerprint, cancellationToken).ConfigureAwait(false);
                    else
                        await ProcessPurchaseSourceSetAsync(client, folder, currentSetFingerprint, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var sourceKey = $"SOURCESET:{module.ToUpperInvariant()}:{currentSetFingerprint}";
                    var actionType = module.Equals("Purchase", StringComparison.OrdinalIgnoreCase)
                        ? "Purchase Import Review"
                        : "Import Blocked";
                    var newAction = AddAction(
                        folder, module, actionType, $"{module} GST source set",
                        $"Grouped {module} Auto Import could not complete: {exception.Message}", sourceKey);
                    if (newAction && _settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                        Notify($"{module} source set needs review", $"{folder.ClientName}: {exception.Message}", true, folder.ClientName, module);
                    AutomationDiagnosticLog.Write("SALES-PURCHASE-SET", $"Grouped {module} Auto Import failed: {exception}", $"{client.ClientCode}|{financialYear}");
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessSalesSourceSetAsync(
        ClientProfile client,
        IReadOnlyList<AutomationFolderRegistration> folders,
        string setFingerprint,
        CancellationToken cancellationToken)
    {
        var folder = folders[0];
        var root = _settings.Get(EReturnsRootContext.PreferenceKey).Trim();
        var result = await _salesAutoImportService.ImportAsync(new SalesAutoImportRequest
        {
            ClientId = client.Id,
            ClientGstin = client.Gstin,
            FinancialYear = client.FinancialYear,
            CompanyName = string.IsNullOrWhiteSpace(client.LegalName) ? client.ClientName : client.LegalName,
            EReturnsRootPath = root,
            AutomationFolderPaths = folders.Select(item => item.FolderPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            SearchWindowsDownloads = true,
            SaveSourceFiles = false
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        result.SourceFingerprint = BuildSalesSourceFingerprint(result);
        var hasSalesSource = result.SourceManifest.Any(source =>
            source.FileType.Equals("GSTR1", StringComparison.OrdinalIgnoreCase));
        if (!hasSalesSource)
        {
            var detail = result.Issues.Count > 0
                ? string.Join(" | ", result.Issues.Select(issue => issue.Message).Where(message => !string.IsNullOrWhiteSpace(message)).Distinct(StringComparer.OrdinalIgnoreCase))
                : "No valid GSTR-1 payload was resolved from the indexed Sales source set.";
            if (detail.Length == 0) detail = "No valid GSTR-1 payload was resolved from the indexed Sales source set.";
            var actionKey = $"SOURCESET:SALES:{setFingerprint}";
            var newAction = AddAction(folder, "Sales", "Import Blocked", "Sales GST source set", detail, actionKey);
            if (newAction && _settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                Notify("Sales source set needs review", $"{folder.ClientName}: {detail}", true, folder.ClientName, "Sales");
            return;
        }

        var accountingContentFingerprint = SalesPurchaseAccountingContentFingerprint.BuildSales(result);
        var storedAccountingContentFingerprint = _salesRecordRepository.GetLatestAutomaticImportContentFingerprint(client.Id, client.FinancialYear);
        var current = _salesRecordRepository.IsAutomaticImportCurrent(client.Id, client.FinancialYear, result.SourceFingerprint) ||
                      (storedAccountingContentFingerprint.Length > 0 &&
                       storedAccountingContentFingerprint.Equals(accountingContentFingerprint, StringComparison.OrdinalIgnoreCase));
        if (!current)
        {
            var normalization = new SalesStockNormalizationService(_stockMasterRepository, _actionRepository);
            var stockSummary = normalization.Normalize(
                client.Id,
                client.FinancialYear,
                string.IsNullOrWhiteSpace(result.CompanyName) ? client.ClientName : result.CompanyName,
                result.Records);
            result.PersistedRows = _salesRecordRepository.ReplaceAutomaticImport(client.Id, client.FinancialYear, result);
            if (_accountingRefreshCoordinator is not null)
                await _accountingRefreshCoordinator.RefreshAsync(client, cancellationToken).ConfigureAwait(false);

            _activityRepository.Record(new ImportActivity
            {
                ClientId = client.Id,
                ImportType = "Sales",
                RecordCount = result.PersistedRows,
                SourceSummary = "Grouped GSTIN/FY GSTR-1 source set",
                Status = "Auto Imported",
                Details = $"Sales {result.Summary.SalesRows:N0}; Credit Notes {result.Summary.CreditNoteRows:N0}; stock review {stockSummary.ActionsRequired:N0}.",
                ImportedUtc = DateTime.UtcNow
            });
            if (_settings.GetBool(ApplicationSettingKeys.NotifyImportComplete))
                Notify("Sales auto-imported", $"{client.ClientName}: {result.PersistedRows:N0} saved row(s)", false, client.ClientName, "Sales");
        }
        else
        {
            result.PersistedRows = _salesRecordRepository.GetAutomaticRecords(client.Id, client.FinancialYear).Count;
        }

        RecordSalesSourceSetHistory(folder, result, current ? "Duplicate" : "Imported");
        ResolveCachedSalesPurchaseActions(folder, "Sales", client.Gstin, client.FinancialYear);
        ResolveAutomationAction(folder, "Sales", $"SOURCESET:SALES:{setFingerprint}");
        SalesPurchaseSourceCache.MarkSetHandled(
            "Sales", client.Gstin, client.FinancialYear, setFingerprint, accountingContentFingerprint, current ? "Current" : "Imported");
        if (!current)
            RaiseAccountingDataChanged(folder, "Sales", result.PersistedRows, "GSTIN/FY GSTR-1 source set");
        else
            QueueDashboardOutputRefresh(client.Id, client.FinancialYear);
    }

    private async Task ProcessPurchaseSourceSetAsync(
        ClientProfile client,
        AutomationFolderRegistration folder,
        string setFingerprint,
        CancellationToken cancellationToken)
    {
        var root = _settings.Get(EReturnsRootContext.PreferenceKey).Trim();
        var result = await _purchaseAutoImportService.ImportAsync(new PurchaseAutoImportRequest
        {
            ClientId = client.Id,
            ClientGstin = client.Gstin,
            FinancialYear = client.FinancialYear,
            CompanyName = string.IsNullOrWhiteSpace(client.LegalName) ? client.ClientName : client.LegalName,
            EReturnsRootPath = root,
            SearchWindowsDownloads = true,
            SaveSourceFiles = false
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        result.SourceFingerprint = BuildPurchaseSourceFingerprint(result);
        var hasTwoA = result.SourceManifest.Any(source => source.FileType.Equals("2A", StringComparison.OrdinalIgnoreCase));
        if (result.WasBlocked || !hasTwoA)
        {
            var detail = result.Issues.Count == 0
                ? "No valid GSTR-2A source was resolved for this Client/FY source set."
                : string.Join(" | ", result.Issues.Select(item => item.Message).Where(message => !string.IsNullOrWhiteSpace(message)).Distinct(StringComparer.OrdinalIgnoreCase));
            var actionKey = $"SOURCESET:PURCHASE:{setFingerprint}";
            var newAction = AddAction(folder, "Purchase", "Purchase Import Review", "Purchase GST source set", detail, actionKey);
            if (newAction && _settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                Notify("Purchase source set needs review", $"{folder.ClientName}: {detail}", true, folder.ClientName, "Purchase");
            return;
        }

        var accountingContentFingerprint = SalesPurchaseAccountingContentFingerprint.BuildPurchase(result);
        var storedAccountingContentFingerprint = _purchaseRecordRepository.GetLatestAutomaticImportContentFingerprint(client.Id, client.FinancialYear);
        var current = _purchaseRecordRepository.IsAutomaticImportCurrent(client.Id, client.FinancialYear, result.SourceFingerprint) ||
                      (storedAccountingContentFingerprint.Length > 0 &&
                       storedAccountingContentFingerprint.Equals(accountingContentFingerprint, StringComparison.OrdinalIgnoreCase));
        var persisted = current
            ? _purchaseRecordRepository.GetAutomaticRecords(client.Id, client.FinancialYear).Count
            : _purchaseRecordRepository.ReplaceAutomaticImport(client.Id, client.FinancialYear, result);
        result.PersistedRows = persisted;

        if (!current)
        {
            if (_accountingRefreshCoordinator is not null)
                await _accountingRefreshCoordinator.RefreshAsync(client, cancellationToken).ConfigureAwait(false);
            _activityRepository.Record(new ImportActivity
            {
                ClientId = client.Id,
                ImportType = "Purchase",
                RecordCount = persisted,
                SourceSummary = "Grouped GSTIN/FY GSTR-2A/2B source set",
                Status = "Auto Imported",
                Details = $"Purchase {result.Summary.PurchaseRows:N0}; RCM {result.Summary.RcmRows:N0}; Debit Note {result.Summary.DebitNoteRows:N0}; Debit Note Reverse {result.Summary.DebitNoteReverseRows:N0}.",
                ImportedUtc = DateTime.UtcNow
            });
            if (_settings.GetBool(ApplicationSettingKeys.NotifyImportComplete))
                Notify("Purchase auto-imported", $"{folder.ClientName}: {persisted:N0} saved row(s)", false, folder.ClientName, "Purchase");
        }

        RecordPurchaseSourceSetHistory(folder, result, current ? "Duplicate" : "Imported");
        ResolveCachedSalesPurchaseActions(folder, "Purchase", client.Gstin, client.FinancialYear);
        ResolveAutomationAction(folder, "Purchase", $"SOURCESET:PURCHASE:{setFingerprint}");
        SalesPurchaseSourceCache.MarkSetHandled(
            "Purchase", client.Gstin, client.FinancialYear, setFingerprint, accountingContentFingerprint, current ? "Current" : "Imported");
        if (!current)
            RaiseAccountingDataChanged(folder, "Purchase", persisted, "GSTIN/FY GSTR-2A/2B source set");
        else
            QueueDashboardOutputRefresh(client.Id, client.FinancialYear);
    }

    private void RecordSalesSourceSetHistory(AutomationFolderRegistration folder, SalesAutoImportResult result, string status)
    {
        foreach (var sourcePath in result.SourceManifest
                     .Select(source => source.SourcePath)
                     .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hash = SafeHash(sourcePath);
            if (hash.Length == 0) continue;
            RecordHistory(folder, sourcePath, hash, "Sales", status,
                status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase)
                    ? "Grouped GSTR-1 source set is already current; no Sales rows were rewritten."
                    : "Grouped GSTR-1 source was consumed by the verified GSTIN/FY automatic Sales import.");
            ResolveAutomationAction(folder, "Sales", hash);
        }
    }

    private void RecordPurchaseSourceSetHistory(AutomationFolderRegistration folder, PurchaseAutoImportResult result, string status)
    {
        foreach (var sourcePath in result.SourceManifest
                     .Select(source => source.SourcePath)
                     .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hash = SafeHash(sourcePath);
            if (hash.Length == 0) continue;
            RecordHistory(folder, sourcePath, hash, "Purchase", status,
                status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase)
                    ? "Grouped GSTR-2A/2B source set is already current; no Purchase rows were rewritten."
                    : "Grouped GSTR-2A/2B source was consumed by the verified GSTIN/FY automatic Purchase import.");
            ResolveAutomationAction(folder, "Purchase", hash);
        }
    }

    private void ResolveCachedSalesPurchaseActions(
        AutomationFolderRegistration folder,
        string module,
        string gstin,
        string financialYear)
    {
        foreach (var path in SalesPurchaseSourceCache.GetPaths(module, gstin, financialYear))
        {
            var hash = SafeHash(path);
            if (hash.Length == 0) continue;
            ResolveAutomationAction(folder, module, hash);

            // Repair Fix38's old component-level Needs Action history too. A component that
            // was indexed but not selected by the verified aggregate source set is not an
            // import failure. Preserve any real Imported/Duplicate history; otherwise close
            // the stale review row as a harmless source-set component.
            if (_automationRepository.GetProcessedFile(folder.FolderId, folder.ClientId, hash) is null)
            {
                RecordHistory(folder, path, hash, module, "Skipped",
                    $"{module} GST return component was indexed as part of the verified GSTIN/FY source set; standalone reimport/review is not required.");
            }
        }
    }

    private static string BuildSalesSourceFingerprint(SalesAutoImportResult result)
    {
        var builder = new StringBuilder();
        builder.Append(result.ClientGstin).Append('|')
            .Append(result.FinancialYear).Append('|')
            .Append(result.Verification.Status).Append('|')
            .Append(result.Verification.CustomerMasterSourceHash).AppendLine();
        foreach (var source in result.SourceManifest
                     .OrderBy(item => item.FileType, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.ReturnPeriod, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.InternalJsonName, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(source.FileType).Append('|')
                .Append(source.SourceBranch).Append('|')
                .Append(source.SourceScope).Append('|')
                .Append(source.ReturnPeriod).Append('|')
                .Append(source.SourcePath).Append('|')
                .Append(source.InternalJsonName).Append('|')
                .Append(source.EmbeddedTimestamp).Append('|')
                .Append(source.PayloadHash).AppendLine();
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private IReadOnlyList<ClientProfile> GetCentralSalesPurchaseActiveClients()
    {
        lock (_salesPurchaseClientSnapshotGate)
        {
            if (_salesPurchaseActiveClientSnapshotUtc != DateTime.MinValue &&
                DateTime.UtcNow - _salesPurchaseActiveClientSnapshotUtc < TimeSpan.FromSeconds(30))
                return _salesPurchaseActiveClientSnapshot;

            var now = DateTime.UtcNow;
            _salesPurchaseActiveClientSnapshot = _clientRepository.GetAll(false)
                .Where(client => !client.IsArchived && NormalizeIdentityToken(client.Gstin).Length == 15)
                .Where(client =>
                {
                    var opened = _clientRepository.GetWorkspaceLastOpenedUtc(client.Id, client.FinancialYear);
                    var activity = opened != DateTime.MinValue ? opened : client.UpdatedUtc;
                    return activity == DateTime.MinValue || now - activity <= TimeSpan.FromDays(183);
                })
                .ToArray();
            _salesPurchaseActiveClientSnapshotUtc = now;
            return _salesPurchaseActiveClientSnapshot;
        }
    }

    private AutomationFileKind DetectCentralSalesPurchaseKind(string path)
    {
        var token = NormalizeIdentityToken(path);
        if (token.Contains("GSTR1", StringComparison.OrdinalIgnoreCase) || token.Contains("SALES", StringComparison.OrdinalIgnoreCase))
            return AutomationFileKind.Sales;
        if (token.Contains("GSTR2A", StringComparison.OrdinalIgnoreCase) ||
            token.Contains("GSTR2B", StringComparison.OrdinalIgnoreCase) ||
            token.Contains("PURCHASE", StringComparison.OrdinalIgnoreCase))
            return AutomationFileKind.Purchase;
        try
        {
            var classification = _fileClassifier.Classify(path);
            return classification.Kind is AutomationFileKind.Sales or AutomationFileKind.Purchase
                ? classification.Kind
                : AutomationFileKind.Unknown;
        }
        catch { return AutomationFileKind.Unknown; }
    }

    private static bool IsCentralSalesPurchaseSourceSetCandidate(string path, AutomationFileKind kind)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".json" or ".zip") return true;
        if (kind != AutomationFileKind.Sales || extension is not (".xlsx" or ".xlsm" or ".xls")) return false;

        // Sales customer/recipient master workbooks belong to the automatic GSTR-1 source set
        // because a master change must refresh customer names even when GSTR-1 payloads did not
        // change. Ordinary Sales Register spreadsheets remain on the normal file importer.
        var token = NormalizeIdentityToken(Path.GetFileName(path));
        return token.Contains("MASTER", StringComparison.OrdinalIgnoreCase) &&
               (token.Contains("CUSTOMER", StringComparison.OrdinalIgnoreCase) ||
                token.Contains("RECIPIENT", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCentralSalesPurchaseCandidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        if (Path.GetFileName(path).StartsWith("~$", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsKnownNonAccountingBuildArtifact(path)) return false;
        return Path.GetExtension(path).ToLowerInvariant() is ".json" or ".zip" or ".xlsx" or ".xls" or ".xlsm" or ".csv";
    }

    private static bool IsKnownNonAccountingBuildArtifact(string path)
    {
        // Safety bar only: reject files that are unambiguously application/compiler metadata
        // before the generic Sales/Purchase classifier opens them. Business filenames and
        // arbitrary Winman/GST workbooks remain eligible exactly as before.
        var fileName = Path.GetFileName(path);
        if (fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) ||
            (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
             (fileName.StartsWith("AAH_", StringComparison.OrdinalIgnoreCase) ||
              fileName.Contains("AccountingAutomationHub", StringComparison.OrdinalIgnoreCase))))
            return true;

        string normalized;
        try { normalized = Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar); }
        catch { normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar); }
        var wrapped = $"{Path.DirectorySeparatorChar}{normalized.Trim(Path.DirectorySeparatorChar)}{Path.DirectorySeparatorChar}";
        var blockedSegments = new[]
        {
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}TestResults{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}AAH."
        };
        if (blockedSegments.Any(segment => wrapped.Contains(segment, StringComparison.OrdinalIgnoreCase)))
            return true;

        return wrapped.Contains(
            $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}AccountingAutomationHub.",
            StringComparison.OrdinalIgnoreCase);
    }

    private bool IsHistoricalCentralSourceStable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0) return false;
            var stableSeconds = Math.Clamp(_settings.GetInt(ApplicationSettingKeys.AutomationFileStableSeconds, 3), 1, 30);
            if (DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromSeconds(stableSeconds + 2)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length == info.Length;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ExtractCentralSourceOwnerGstins(string path, string identityText)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var combined = $"{Path.GetFileName(path)}\n{identityText}";

        // GST-return sources identify the taxpayer/owner with GSTIN/gstin. Counterparty fields
        // such as ctin/supplier GSTIN are deliberately not accepted here; "GSTIN of recipient" is
        // accepted because official inward-supply exports use it for the taxpayer owning the return.
        foreach (Match match in Regex.Matches(
                     combined,
                     @"(?ix)(?:[""']gstin[""']\s*:\s*[""']?|gstin\s*/\s*uin\s*(?:[:=,\-]|\bis\b)?\s*[""']?|gstin\s+of\s+(?:taxpayer|recipient)\s*(?:[:=,\-]|\bis\b)?\s*[""']?)(?<gstin>[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][A-Z0-9]Z[A-Z0-9])"))
        {
            var candidate = NormalizeGstin(match.Groups["gstin"].Value);
            if (candidate.Length == 15) results.Add(candidate);
        }

        // Many official GST exports include the owner GSTIN in the file/archive name. This is
        // also source evidence and does not depend on any Client record.
        foreach (Match match in Regex.Matches(
                     Path.GetFileName(path),
                     @"(?i)(?<![A-Z0-9])(?<gstin>[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][A-Z0-9]Z[A-Z0-9])(?![A-Z0-9])"))
        {
            var candidate = NormalizeGstin(match.Groups["gstin"].Value);
            if (candidate.Length == 15) results.Add(candidate);
        }

        // If there is exactly one valid GSTIN anywhere in the bounded source identity sample,
        // it is safe to treat it as the owner. Multiple unlabelled GSTINs are intentionally not
        // guessed because they are usually customer/supplier counterparties.
        var all = Regex.Matches(
                combined,
                @"(?i)(?<![A-Z0-9])(?<gstin>[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][A-Z0-9]Z[A-Z0-9])(?![A-Z0-9])")
            .Select(match => NormalizeGstin(match.Groups["gstin"].Value))
            .Where(candidate => candidate.Length == 15)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (results.Count == 0 && all.Length == 1) results.Add(all[0]);

        return results.ToArray();
    }

    private static string DetectSourceFinancialYearHint(string path, string identityText)
    {
        var combined = $"{path} {identityText}";
        var normalized = combined.Replace('_', '-').Replace('/', '-');
        var fy = Regex.Match(normalized, @"(?<!\d)(?<start>20\d{2})\s*-\s*(?<end>\d{2}|20\d{2})(?!\d)");
        if (fy.Success)
        {
            var end = fy.Groups["end"].Value;
            if (end.Length == 4) end = end[^2..];
            return $"{fy.Groups["start"].Value}-{end}";
        }

        // Prefer an explicitly labelled GST return period. Do not use an arbitrary six-digit
        // number because invoice/reference values could otherwise assign the source to a wrong FY.
        var period = Regex.Match(
            combined,
            @"(?ix)(?:[""']?(?:fp|rtnprd|retnprd)[""']?|return\s*period|tax\s*period)\s*(?:[:=,\-]|\bis\b)?\s*[""']?(?<period>(?:0[1-9]|1[0-2])20\d{2}|20\d{2}(?:0[1-9]|1[0-2]))");
        if (!period.Success) return string.Empty;

        var token = period.Groups["period"].Value;
        int month;
        int year;
        if (token.StartsWith("20", StringComparison.Ordinal))
        {
            year = int.Parse(token[..4], System.Globalization.CultureInfo.InvariantCulture);
            month = int.Parse(token[4..], System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            month = int.Parse(token[..2], System.Globalization.CultureInfo.InvariantCulture);
            year = int.Parse(token[2..], System.Globalization.CultureInfo.InvariantCulture);
        }
        var start = month >= 4 ? year : year - 1;
        return $"{start}-{(start + 1) % 100:00}";
    }

    private static string NormalizeFinancialYearHint(string? financialYear)
    {
        var text = (financialYear ?? string.Empty).Trim().Replace('_', '-').Replace('/', '-');
        var match = Regex.Match(text, @"(?<!\d)(?<start>20\d{2})\s*-\s*(?<end>\d{2}|20\d{2})(?!\d)");
        if (!match.Success) return text;
        var end = match.Groups["end"].Value;
        if (end.Length == 4) end = end[^2..];
        return $"{match.Groups["start"].Value}-{end}";
    }

    private void QueueExistingFiles(AutomationFolderRegistration folder, bool force = false, bool forceScanCommand = false)
    {
        if (!Directory.Exists(folder.FolderPath)) return;
        IReadOnlyDictionary<string, AutomationFileHistoryRecord>? fileRegistry = null;
        if (!force)
        {
            try { fileRegistry = _automationRepository.GetFileRegistry(folder.FolderId, folder.ClientId); }
            catch { fileRegistry = null; }
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(folder.FolderPath, "*", SearchOption.AllDirectories))
            {
                if (forceScanCommand && IsGstr3bForceScanSource(path))
                {
                    AutomationDiagnosticLog.Write("FORCE-SCAN-SPECIAL", "GSTR-3B excluded from generic force scan; existing GST special route remains authoritative.", path);
                    continue;
                }
                if (!force && IsUnchangedRegisteredSource(folder, path, fileRegistry))
                {
                    RememberObservedFileState(path);
                    continue;
                }
                QueueFile(folder.FolderId, path, force);
            }
        }
        catch (IOException)
        {
            // Watcher/recovery will retry transient directory failures.
        }
        catch (UnauthorizedAccessException)
        {
            // Do not terminate the background service because one subfolder is unavailable.
        }
    }

    private bool IsUnchangedRegisteredSource(
        AutomationFolderRegistration folder,
        string path,
        IReadOnlyDictionary<string, AutomationFileHistoryRecord>? fileRegistry)
    {
        if (fileRegistry is null || fileRegistry.Count == 0) return false;
        try
        {
            var normalized = Path.GetFullPath(path);
            if (!fileRegistry.TryGetValue(normalized, out var record)) return false;
            var info = new FileInfo(normalized);
            if (!info.Exists) return false;

            // R9 FIXED16 R17: R14/R16 can leave an unchanged Crystal/Tally Sales Register
            // recorded as Imported/Duplicate before the direct Sales-summary migration has
            // actually created rows. Do not let the BR4 path/size/mtime shortcut hide that
            // one-time replay. Look through newer Duplicate observations to the last successful
            // Sales import for this exact path, then let the normal hash barrier clear/replay it.
            if (NeedsTallySalesRegisterSummaryReplayForPath(folder, normalized))
                return false;

            // BR4: startup/client-open recovery is FILE LEVEL ONLY. Exact size + last-write
            // equality means the source is unchanged, so no hash, workbook read, transaction
            // duplicate check, balance verification or post-import work is allowed to run.
            if (record.FileSize > 0 && record.LastWriteUtc != DateTime.MinValue)
                return info.Length == record.FileSize && info.LastWriteTimeUtc == record.LastWriteUtc;

            // One-time upgrade fallback for pre-BR4 history that has no stored source
            // metadata. Upgrade the registry row immediately so every later app/client
            // opening becomes an exact path/size/mtime comparison only.
            var unchangedLegacy = record.ProcessedUtc != DateTime.MinValue &&
                                  info.LastWriteTimeUtc <= record.ProcessedUtc.AddSeconds(2);
            if (unchangedLegacy)
            {
                try
                {
                    _automationRepository.UpdateFileRegistryMetadata(
                        record.PersistentId,
                        normalized,
                        info.Length,
                        info.LastWriteTimeUtc);
                }
                catch
                {
                    // The source is still known unchanged from the legacy watermark; a later
                    // recovery pass can retry the metadata upgrade without opening Excel.
                }
            }
            return unchangedLegacy;
        }
        catch
        {
            return false;
        }
    }

    private void RememberObservedFileState(string path)
    {
        try
        {
            var normalized = Path.GetFullPath(path);
            var info = new FileInfo(normalized);
            if (!info.Exists) return;
            _observedFileStates[normalized] = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
        }
    }

    private bool IsGstr3bForceScanSource(string path)
    {
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return _fileClassifier.Classify(path).Kind == AutomationFileKind.Gstr3bPdf;
        }
        catch
        {
            // A force scan must never hide an unreadable Bank/other PDF just because
            // GSTR-3B detection could not complete. Let the normal router review it.
            return false;
        }
    }

    private static bool IsManagedEvidencePath(AutomationFolderRegistration folder, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(folder.FolderPath, path)
                .Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative)) return false;

            return relative.StartsWith("GST/GSTR-3B/", StringComparison.OrdinalIgnoreCase) ||
                   relative.StartsWith("GST/Electronic Credit Ledger/", StringComparison.OrdinalIgnoreCase) ||
                   relative.StartsWith("GST/Electronic Cash Ledger/", StringComparison.OrdinalIgnoreCase) ||
                   relative.StartsWith("Sales/Customer Master/", StringComparison.OrdinalIgnoreCase) ||
                   // Fix137 R41: mobile invoice photos/OCR evidence are managed scanner
                   // artifacts. They must never be recursively classified as Bank/Sales/
                   // Purchase source files by the ordinary Automation Folder watcher.
                   relative.StartsWith("Mobile Scans/", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool QueueFile(
        string folderId,
        string path,
        bool force = false,
        bool manualReimport = false,
        AutomationFileKind? forcedKind = null,
        string precomputedHash = "")
    {
        if (Path.GetFileName(path).StartsWith("~$", StringComparison.OrdinalIgnoreCase)) return false;
        try { path = Path.GetFullPath(path); }
        catch { return false; }
        if (!manualReimport && _folderMoveQuiesce.ContainsKey(folderId)) return false;

        // FIXED22: the linked folder now carries a shortcut to AAH's secure password-entry
        // prompt. Ignore that shortcut completely. The old text file is accepted only long
        // enough to migrate legacy credentials into Windows Credential Manager and is deleted.
        if (PortalCredentialFileService.IsPromptShortcut(path)) return false;
        if (PortalCredentialFileService.IsCredentialFile(path))
        {
            var credentialFolder = _automationRepository.GetByFolderId(folderId);
            if (credentialFolder is not null &&
                PortalCredentialFileService.TryImport(path, credentialFolder.ClientId, credentialFolder.FinancialYear, out var credentialMessage))
            {
                AutomationDiagnosticLog.Write("PORTAL-CREDENTIALS", credentialMessage, path);
                PortalCredentialsChanged?.Invoke(this, new PortalCredentialsChangedEventArgs(credentialFolder.ClientId, credentialFolder.FinancialYear, path));
            }
            return false;
        }

        // Review workbooks live inside the linked client folder by design. They are
        // never accounting-import candidates. An .xlsx review save is instead routed
        // to the delta review-sync event, independently of Auto Import pause state.
        if (ClientDiagnosticExportService.IsReservedReviewExportPath(path))
        {
            if (Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                // Keep generated review files only long enough for the user to edit them.
                // If the workbook is still effectively unchanged after the grace
                // period (binary-identical or accounting-row-identical after a no-op
                // Excel save), delete the file but keep its name +
                // delta baseline in LocalAppData. A later copied-back edited workbook
                // with the same name is therefore recognized and only changed rows sync.
                if (ClientDiagnosticExportService.TryDeleteUnchangedReviewWorkbook(path, TimeSpan.FromMinutes(10)))
                    return false;

                var reviewFolder = _automationRepository.GetByFolderId(folderId);
                if (reviewFolder is not null)
                    QueueClientReviewWorkbookSync(reviewFolder, path);
            }
            return false;
        }

        // Tally diagnostic packs are generated inside the linked client folder.
        // They contain XML/CSV/TXT report artifacts and must never be interpreted as
        // accounting input or create Action Required rows.
        var reportSegment = Path.DirectorySeparatorChar + "AAH_TALLY_REPORTS" + Path.DirectorySeparatorChar;
        var altReportSegment = Path.AltDirectorySeparatorChar + "AAH_TALLY_REPORTS" + Path.AltDirectorySeparatorChar;
        if (path.Contains(reportSegment, StringComparison.OrdinalIgnoreCase) ||
            path.Contains(altReportSegment, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).StartsWith("AAH_TALLY_", StringComparison.OrdinalIgnoreCase))
            return false;

        // Older diagnostic exports (for example AAH_CL001_2025-26_Diagnostic.xlsx)
        // may already exist in a linked client folder. They are Hub output, never
        // accounting input, and must not be classified or sent to Action Required.
        if (ClientDiagnosticExportService.IsHubGeneratedExportPath(path)) return false;

        var folder = _automationRepository.GetByFolderId(folderId);
        if (folder is null) return false;

        // Managed evidence/output folders have their own authoritative reprocessing paths.
        // They must never be fed back into the general recursive Auto Import queue. In
        // particular, Force Auto Import must not re-queue GST\GSTR-3B PDFs that were already
        // moved/validated, and Customer Master evidence is handled by the Sales backfill path.
        if (IsManagedEvidencePath(folder, path)) return false;

        // Per-client Auto Import policy is an additional silent gate. Existing clients default ON
        // so this cumulative update never disables a previously working Automation Folder; an
        // Administrator/mobile policy change can explicitly turn that Client OFF.
        if (!manualReimport && !_clientAutoImportPolicies.Get(folder.ClientId).Enabled) return false;
        // Do not run the strict Client identity check from the FileSystemWatcher callback.
        // A newly-created file may still be changing, and an encrypted Office workbook must be
        // decrypted before its GSTIN/account evidence can be read. The worker performs the same
        // strict check after WaitForStableFileAsync + password-aware classification.
        if (!manualReimport && (folder.IsPaused || _globalPause || !_settings.GetBool(ApplicationSettingKeys.AutomationEnabled))) return false;
        if (!IsSupportedFile(path)) return false;
        if (_generatedWorkbookPaths.ContainsKey(path)) return false;

        if (!force)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var info = new FileInfo(path);
                var state = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
                if (_observedFileStates.TryGetValue(path, out var previous) && previous == state) return false;
                _observedFileStates[path] = state;
            }
            catch
            {
                return false;
            }
        }

        // FIXED28: normal watcher/reconciliation work is bounded. Manual reimport and
        // explicit force/recovery paths retain their existing authority and are never
        // silently dropped by the background-pressure guard.
        if (!manualReimport && !force && _queue.Count >= MaxAutomaticQueueDepth)
        {
            // Do not leave the just-observed size/mtime as a completed observation when
            // backpressure rejects the enqueue. The next bounded reconciliation pass must
            // be able to offer the still-unprocessed file again without requiring a new
            // filesystem event.
            _observedFileStates.TryRemove(path, out _);
            AutomationDiagnosticLog.Write("QUEUE-BACKPRESSURE", $"Automatic queue is bounded at {MaxAutomaticQueueDepth:N0}; reconciliation will retry unchanged-safe work later.", path);
            return false;
        }

        PruneObservedFileStatesIfNeeded();

        var key = $"{folderId}|{path}";
        if (!_queuedKeys.TryAdd(key, 0)) return false;
        var queued = new QueuedAutomationFile(
            folderId,
            folder.ClientId,
            folder.FinancialYear,
            path,
            key,
            manualReimport,
            forcedKind,
            precomputedHash ?? string.Empty);
        _queue.Enqueue(queued);
        RegisterQueuedProgress(queued);
        AutomationDiagnosticLog.Write("QUEUE", $"Queued force={force} manual={manualReimport}; queueDepth={_queue.Count}", path);
        ProgressChanged?.Invoke(this, EventArgs.Empty);
        _queueSignal.Release();
        return true;
    }

    private void PruneObservedFileStatesIfNeeded()
    {
        if (_observedFileStates.Count < 8192) return;
        var now = DateTime.UtcNow;
        if (now - _lastObservedStatePruneUtc < TimeSpan.FromMinutes(10)) return;
        _lastObservedStatePruneUtc = now;

        var removed = 0;
        foreach (var key in _observedFileStates.Keys)
        {
            if (removed >= 1024) break;
            try
            {
                if (File.Exists(key)) continue;
            }
            catch { }
            if (_observedFileStates.TryRemove(key, out _)) removed++;
        }

        // Observation state is only an event-burst optimization; durable import history
        // remains authoritative. Keep memory bounded even after very long sessions that
        // have seen many temporary Office/browser files.
        if (_observedFileStates.Count > 16384)
        {
            foreach (var key in _observedFileStates.Keys.Take(_observedFileStates.Count - 12288))
                _observedFileStates.TryRemove(key, out _);
        }
    }

    private void QueueNearbyProtectedWorkbooksAfterPasswordHelper(
        AutomationFolderRegistration folder,
        string helperPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(helperPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
            foreach (var candidate in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (candidate.Equals(helperPath, StringComparison.OrdinalIgnoreCase)) continue;
                var extension = Path.GetExtension(candidate).ToLowerInvariant();
                if (extension is not (".xlsx" or ".xlsm" or ".xltx" or ".xltm")) continue;
                SpreadsheetSourceInspection inspection;
                try { inspection = SpreadsheetSourceInspector.Inspect(candidate); }
                catch { continue; }
                if (inspection.Kind != SpreadsheetSourceKind.LegacyOleWorkbook) continue;
                QueueFile(folder.FolderId, candidate, force: true, manualReimport: false);
            }
        }
        catch
        {
            // Best-effort wake-up only. The normal Recovery Scan remains available.
        }
    }

    private static string NormalizeIdentityToken(string? value) =>
        new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string DigitsOnly(string? value) =>
        new string((value ?? string.Empty).Where(char.IsDigit).ToArray());

    private void QueueClientReviewWorkbookSync(AutomationFolderRegistration folder, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            var state = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            if (_reviewWorkbookObservedStates.TryGetValue(path, out var previous) && previous == state) return;
            _reviewWorkbookObservedStates[path] = state;
        }
        catch
        {
            return;
        }

        var version = DateTime.UtcNow.Ticks;
        _reviewWorkbookSyncVersions[path] = version;
        _ = Task.Run(async () =>
        {
            try
            {
                // Excel commonly raises several Created/Changed/Renamed events for
                // one save. Wait for the write burst to settle and keep only the
                // newest version for this workbook path.
                await Task.Delay(900).ConfigureAwait(false);
                if (!_reviewWorkbookSyncVersions.TryGetValue(path, out var current) || current != version) return;
                if (!File.Exists(path)) return;

                // Do not hold the file open; the importer has its own retry/error path.
                ClientReviewWorkbookChanged?.Invoke(
                    this,
                    new ClientReviewWorkbookChangedEventArgs(
                        folder.ClientId,
                        folder.FinancialYear,
                        path));
            }
            catch
            {
                // The next file-system change or reconciliation pass can retry.
            }
        });
    }

    private async Task FolderReconciliationLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);

                // FileSystemWatcher handles normal changes immediately. This recovery
                // pass exists only for missed events/unavailable folders and must not compete
                // with foreground workspace/cache work every ten seconds.
                // Reconciliation always walks linked folders so reserved AAH_REVIEW_
                // workbooks can be delta-synced or aged-out even when normal Auto
                // Import is paused/disabled. QueueFile itself still blocks ordinary
                // accounting files according to the global/folder Auto Import state.
                foreach (var folder in _automationRepository.GetFolders())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(folder.FolderPath))
                    {
                        RemoveWatcher(folder.FolderId);
                        continue;
                    }
                    EnsureWatcher(folder);
                    QueueExistingFiles(folder, force: false);
                }

                // AAH is commonly left running in the tray for days. Re-run the bounded
                // AAH-owned storage cleanup hourly so a crash/locked temporary file does not
                // accumulate until the next application restart. This never performs a general
                // Windows TEMP cleanup and never touches Automation Folder/accounting data.
                if (DateTime.UtcNow - _lastStorageMaintenanceUtc >= TimeSpan.FromHours(1))
                {
                    _lastStorageMaintenanceUtc = DateTime.UtcNow;
                    ApplicationStorageMaintenanceService.RunSafeMaintenance();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A failed recovery pass never stops the next periodic scan.
            }
        }
    }

    /// <summary>
    /// Executes an import chosen explicitly by an AAH Windows/Android controller. This path never
    /// auto-detects the accounting module; only file subtype handling inside the chosen module is allowed.
    /// </summary>
    public async Task<AutomationCommandResult> ProcessExplicitImportAsync(
        long clientId,
        string financialYear,
        string module,
        string sourcePath,
        IReadOnlyList<string>? passwordCandidates = null,
        CancellationToken cancellationToken = default)
    {
        if (!DistributedImportModules.IsAllowed(module))
            return AutomationCommandResult.Failed("Choose a supported AAH import module before processing the file.");
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return AutomationCommandResult.Failed("The selected import source is unavailable.");
        var client = _clientRepository.GetById(clientId);
        if (client is null || client.IsArchived)
            return AutomationCommandResult.Failed("The selected Client is unavailable or archived.");
        if (!client.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
            return AutomationCommandResult.Failed("The selected Financial Year no longer matches the Client workspace.");

        var normalized = Path.GetFullPath(sourcePath);
        var fileHash = ComputeHash(normalized);
        var folder = new AutomationFolderRegistration
        {
            FolderId = $"AAH-EXPLICIT-{clientId}-{SanitizeFolderName(financialYear)}",
            ClientId = clientId,
            ClientName = client.ClientName,
            Gstin = client.Gstin,
            FinancialYear = financialYear,
            FolderPath = Path.GetDirectoryName(normalized) ?? AppPrivateImportRuntimePaths.Staging,
            FolderName = "AAH Explicit Import",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        var passwords = (passwordCandidates ?? Array.Empty<string>()).Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length is > 0 and <= 64).Distinct(StringComparer.Ordinal).Take(12).ToArray();
        var initialPassword = passwords.FirstOrDefault();
        var selected = DistributedImportModules.Allowed.First(x => x.Equals(module.Trim(), StringComparison.OrdinalIgnoreCase));
        var preExistingBankCommitCount = selected.Equals(DistributedImportModules.Banking, StringComparison.OrdinalIgnoreCase)
            ? CountCommittedBankTransactions(clientId, financialYear, fileHash)
            : 0;
        try
        {
            switch (selected)
            {
                case DistributedImportModules.Banking:
                {
                    var ext = Path.GetExtension(normalized).ToLowerInvariant();
                    if (ext == ".pdf") await ProcessPdfAsync(folder, normalized, fileHash, cancellationToken, passwords);
                    else if (ext is ".jpg" or ".jpeg" or ".png" or ".webp") await ProcessBankOcrSourceAsync(folder, normalized, fileHash, manualReimport: true, cancellationToken);
                    else if (ext == ".mt940") await ProcessMt940Async(folder, normalized, fileHash, cancellationToken);
                    else if (ext is ".ofx" or ".qbo") await ProcessExchangeStatementAsync(folder, normalized, fileHash, "OFX/QBO", FinancialExchangeStatementConverters.ConvertOfx, cancellationToken);
                    else if (ext == ".qif") await ProcessExchangeStatementAsync(folder, normalized, fileHash, "QIF", FinancialExchangeStatementConverters.ConvertQif, cancellationToken);
                    else if (ext == ".xml") await ProcessExchangeStatementAsync(folder, normalized, fileHash, "ISO 20022 camt.053", FinancialExchangeStatementConverters.ConvertCamt053, cancellationToken);
                    else await ProcessBankWorkbookAsync(folder, normalized, normalized, Path.GetFileName(normalized), fileHash, cancellationToken, initialPassword, passwords);
                    break;
                }
                case DistributedImportModules.Purchase:
                    await ProcessPurchaseAsync(folder, normalized, fileHash, cancellationToken);
                    break;
                case DistributedImportModules.Sales:
                    await ProcessSalesAsync(folder, normalized, fileHash, cancellationToken, initialPassword, passwords);
                    break;
                case DistributedImportModules.Journal:
                    await ProcessUniversalJournalAsync(folder, normalized, fileHash, cancellationToken);
                    break;
                case DistributedImportModules.CashEntry:
                    await ProcessUniversalCashAsync(folder, normalized, fileHash, cancellationToken);
                    break;
                case DistributedImportModules.TallyBackup:
                    return new AutomationCommandResult(true, "Tally backup retained. Automatic restore/link execution is foundation-only in this release.", normalized);
                case DistributedImportModules.DocumentsOnly:
                    return new AutomationCommandResult(true, "Document retained without accounting import.", normalized);
            }

            var result = _automationRepository.GetHistory(clientId, financialYear, 100)
                .Where(x => x.FileHash.Equals(fileHash, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.ProcessedUtc).FirstOrDefault();

            if (selected.Equals(DistributedImportModules.Banking, StringComparison.OrdinalIgnoreCase))
            {
                var committedCount = CountCommittedBankTransactions(clientId, financialYear, fileHash);
                if (committedCount <= 0)
                {
                    var detail = result?.Detail;
                    var reason = string.IsNullOrWhiteSpace(detail)
                        ? "The Banking importer returned without authoritative Bank Statement rows. The Main Server did not mark this upload Completed."
                        : $"The Banking importer did not produce authoritative Bank Statement rows. {detail}";
                    return new AutomationCommandResult(false, reason, normalized, false, 0, "BANK_COMMIT_NOT_VERIFIED");
                }

                var historyRejected = result is not null &&
                    !result.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase) &&
                    !result.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) &&
                    !result.Status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase);
                if (historyRejected)
                    return new AutomationCommandResult(false, string.IsNullOrWhiteSpace(result!.Detail) ? "Banking import requires review." : result.Detail, normalized, true, committedCount, "BANK_IMPORT_REVIEW_REQUIRED");

                var committedMessage = preExistingBankCommitCount > 0 && committedCount == preExistingBankCommitCount
                    ? $"Banking source is already committed on the Main Server: {committedCount:N0} transaction(s) verified."
                    : $"Banking commit verified on the Main Server: {committedCount:N0} transaction(s) are authoritative for this source.";
                if (!string.IsNullOrWhiteSpace(result?.Detail)) committedMessage += $" {result.Detail}";
                return new AutomationCommandResult(true, committedMessage, normalized, true, committedCount, string.Empty);
            }

            if (result is null)
                return new AutomationCommandResult(false, $"{selected} processing returned without authoritative import-history evidence.", normalized, false, 0, "IMPORT_RESULT_NOT_VERIFIED");
            var success = result.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase) || result.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase);
            return success
                ? new AutomationCommandResult(true, string.IsNullOrWhiteSpace(result.Detail) ? $"{selected} import completed." : result.Detail, normalized, true, Math.Max(0, result.RecordCount), string.Empty)
                : new AutomationCommandResult(false, string.IsNullOrWhiteSpace(result.Detail) ? $"{selected} import requires review." : result.Detail, normalized, false, 0, "IMPORT_REVIEW_REQUIRED");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AutomationCommandResult.Failed("The explicit import was cancelled.");
        }
        catch (Exception exception)
        {
            AutomationDiagnosticLog.Write("EXPLICIT-IMPORT-ERROR", $"Module={selected}; {exception}", normalized);
            return AutomationCommandResult.Failed(exception.Message);
        }
    }

    private int CountCommittedBankTransactions(long clientId, string financialYear, string fileHash)
    {
        if (string.IsNullOrWhiteSpace(fileHash)) return 0;
        var statementIds = _bankingRepository.GetStatements(clientId, financialYear)
            .Where(x => x.FileHash.Equals(fileHash, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id)
            .ToHashSet();
        if (statementIds.Count == 0) return 0;
        return _bankingRepository.GetTransactions(clientId, financialYear, includeArchived: false)
            .Count(x => statementIds.Contains(x.StatementId));
    }

    public async Task<AutomationCommandResult> ResolvePdfAndImportInteractivelyAsync(
        long clientId,
        string financialYear,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return AutomationCommandResult.Failed("The PDF source no longer exists.");
        if (!Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return AutomationCommandResult.Failed("Interactive PDF resolution is only available for PDF bank statements.");

        var normalized = Path.GetFullPath(sourcePath);
        var folder = _automationRepository.GetFolders()
            .Where(item => item.ClientId == clientId && item.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
            .Where(item => normalized.Equals(item.FolderPath, StringComparison.OrdinalIgnoreCase) ||
                           normalized.StartsWith(item.FolderPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.FolderPath.Length)
            .FirstOrDefault();
        if (folder is null)
            return AutomationCommandResult.Failed("The PDF is not inside an Automation Folder linked to this client/FY.");

        var fileHash = ComputeHash(normalized);
        // An Action Center / manual Auto Import retry must use the SAME scan
        // classification as the first background import. No interactive Python
        // PDF mapping/password dialog for an image-based bank statement.
        var sourceKind = BankPdfDocumentClassifier.Classify(normalized);
        if (sourceKind is BankPdfDocumentKind.Scanned or BankPdfDocumentKind.Mixed)
        {
            AutomationDiagnosticLog.Write("PDF-SCANNED-OCR-RETRY",
                $"Interactive PDF retry detected {sourceKind}; routing to production Bank OCR, not digital PDF Python.", normalized);
            await ProcessBankOcrSourceAsync(folder, normalized, fileHash, manualReimport: true, cancellationToken);
            var checkedResult = _automationRepository.GetProcessedFile(folder.FolderId, folder.ClientId, fileHash);
            return checkedResult?.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase) == true
                ? new AutomationCommandResult(true, "The scanned statement was handled by the existing Bank OCR verifier/importer.", normalized)
                : AutomationCommandResult.Failed("The scanned statement was routed to Bank OCR, but is not confirmed imported. Check Bank OCR review and Action Center.");
        }

        var expectedWorkbook = Path.ChangeExtension(normalized, ".xlsx");
        _generatedWorkbookPaths.TryAdd(Path.GetFullPath(expectedWorkbook), 0);
        var pipeline = new PdfBankStatementPipelineService(_settings);
        PdfPipelineFileResult? pipelineResult = null;
        try
        {
            AutomationDiagnosticLog.Write(
                "PDF-INTERACTIVE",
                "Action Center requested the same interactive external-Python mode used by a normal command-line run. A Python mapping/password/line-confirmation window may appear. No Python file is copied, changed, backed up, restored, or deleted by the Hub.",
                normalized);

            // IMPORTANT: automationMode=false is intentional here. Background Auto Import
            // remains headless, but an explicit Action Center Retry must behave like a
            // user's direct command-line execution so one-time Python mapping/password/
            // line-confirmation UI can complete and create the learned local mapping.
            pipelineResult = await pipeline.ProcessAsync(normalized, cancellationToken, automationMode: false);
            if (!pipelineResult.WasSuccessful || pipelineResult.Issue is not null)
            {
                var message = pipelineResult.Issue?.FriendlyMessage ?? "The external Python pipeline did not produce an importable workbook.";
                RecordHistory(folder, normalized, fileHash, "Bank PDF", "Needs Action", message);
                AddAction(folder, "Banking", "PDF Import Review", Path.GetFileName(normalized), $"{message}\nSource: {normalized}\nLog: {pipelineResult.LogPath}", fileHash);
                return AutomationCommandResult.Failed(message + (string.IsNullOrWhiteSpace(pipelineResult.LogPath) ? string.Empty : $" Log: {pipelineResult.LogPath}"));
            }

            await ProcessBankWorkbookAsync(
                folder,
                pipelineResult.GeneratedWorkbookPath,
                normalized,
                Path.GetFileName(normalized),
                fileHash,
                cancellationToken);

            var processed = _automationRepository.GetProcessedFile(folder.FolderId, folder.ClientId, fileHash);
            return processed?.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase) == true
                ? new AutomationCommandResult(true, "The PDF was resolved through the external Python pipeline and imported successfully.", normalized)
                : AutomationCommandResult.Failed("The Python pipeline created the Excel workbook, but the generated workbook still needs accounting review. Check Action Center and the PDF import log.");
        }
        catch (OperationCanceledException)
        {
            return AutomationCommandResult.Failed("Interactive PDF resolution was cancelled.");
        }
        catch (Exception exception)
        {
            AutomationDiagnosticLog.Write("PDF-INTERACTIVE-ERROR", exception.ToString(), normalized);
            return AutomationCommandResult.Failed(exception.Message);
        }
        finally
        {
            _generatedWorkbookPaths.TryAdd(Path.GetFullPath(expectedWorkbook), 0);
            if (pipelineResult is not null && pipelineResult.WasSuccessful)
            {
                try { await pipeline.CleanupAsync(pipelineResult); }
                catch { /* cleanup never changes the accounting result */ }
            }
        }
    }

    public bool RetryFile(long clientId, string financialYear, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return false;
        var normalized = Path.GetFullPath(sourcePath);
        var folder = _automationRepository.GetFolders()
            .Where(item => item.ClientId == clientId && item.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
            .Where(item => normalized.Equals(item.FolderPath, StringComparison.OrdinalIgnoreCase) ||
                           normalized.StartsWith(item.FolderPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.FolderPath.Length)
            .FirstOrDefault();
        if (folder is null) return false;
        _observedFileStates.TryRemove(normalized, out _);
        QueueFile(folder.FolderId, normalized, force: true);
        return true;
    }

    public bool RetryFileAs(long clientId, string financialYear, string sourcePath, string accountingType)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return false;
        var kind = accountingType.Trim().ToUpperInvariant() switch
        {
            "BANK" => AutomationFileKind.BankSpreadsheet,
            "SALES" => AutomationFileKind.Sales,
            "PURCHASE" => AutomationFileKind.Purchase,
            _ => AutomationFileKind.Unknown
        };
        if (kind == AutomationFileKind.Unknown) return false;
        var signature = _fileClassifier.GetLearningSignature(sourcePath);
        _settings.Set($"Automation.Classifier.Override.{signature}", kind.ToString());
        return RetryFile(clientId, financialYear, sourcePath);
    }

    public bool RetryFileWithTemporaryHeaders(long clientId, string financialYear, string sourcePath, string module, IReadOnlyDictionary<string, string> sourceToCanonical)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return false;
        var normalized = Path.GetFullPath(sourcePath);
        var normalizedModule = ImportHeaderMappingCatalog.NormalizeModule(module);
        var retryKind = normalizedModule switch
        {
            ImportHeaderMappingCatalog.Banking => AutomationFileKind.BankSpreadsheet,
            ImportHeaderMappingCatalog.Sales => AutomationFileKind.Sales,
            ImportHeaderMappingCatalog.Purchase => AutomationFileKind.Purchase,
            ImportHeaderMappingCatalog.Cash => AutomationFileKind.Cash,
            ImportHeaderMappingCatalog.Journal => AutomationFileKind.Journal,
            ImportHeaderMappingCatalog.Suspense => AutomationFileKind.BankSuspenseMapping,
            _ => AutomationFileKind.Unknown
        };
        if (retryKind == AutomationFileKind.Unknown) return false;

        var folder = _automationRepository.GetFolders()
            .Where(item => item.ClientId == clientId && item.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
            .Where(item => normalized.Equals(item.FolderPath, StringComparison.OrdinalIgnoreCase) ||
                           normalized.StartsWith(item.FolderPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.FolderPath.Length)
            .FirstOrDefault();
        if (folder is null) return false;

        ImportHeaderMappingRuntime.SetOneShotMapping(normalized, normalizedModule, sourceToCanonical);
        _observedFileStates.TryRemove(normalized, out _);
        if (QueueFile(folder.FolderId, normalized, force: true, manualReimport: true, forcedKind: retryKind)) return true;
        ImportHeaderMappingRuntime.ClearOneShotMapping(normalized);
        return false;
    }


    private AutomationFileClassification ApplyClassificationOverride(string path, AutomationFileClassification detected)
    {
        if (detected.Kind is AutomationFileKind.BankPdf or AutomationFileKind.BankOcrSource or AutomationFileKind.Gstr3bPdf or AutomationFileKind.Form26AsPdf or AutomationFileKind.Mt940 or AutomationFileKind.Camt053 or AutomationFileKind.Ofx or AutomationFileKind.Qif)
            return detected;
        var signature = _fileClassifier.GetLearningSignature(path);
        var saved = _settings.Get($"Automation.Classifier.Override.{signature}");
        if (Enum.TryParse<AutomationFileKind>(saved, ignoreCase: true, out var kind) &&
            kind is AutomationFileKind.BankSpreadsheet or AutomationFileKind.Sales or AutomationFileKind.Purchase)
            return new AutomationFileClassification(kind, 100, $"Saved file-type learning selected {kind}.", detected.BankScore, detected.SalesScore, detected.PurchaseScore);
        return detected;
    }

    private void SetAutomationStage(QueuedAutomationFile item, string stage)
    {
        _activeStages[item.QueueKey] = new AutomationActiveStage(
            item.QueueKey,
            item.FolderId,
            item.ClientId,
            item.FinancialYear,
            stage,
            Path.GetFileName(item.Path),
            item.Path,
            DateTime.UtcNow);
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetAutomationStageForPaths(IEnumerable<string> paths, string stage)
    {
        var normalized = new HashSet<string>(
            paths.Select(path =>
            {
                try { return Path.GetFullPath(path); }
                catch { return path; }
            }),
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in _activeFiles.Values)
        {
            string path;
            try { path = Path.GetFullPath(item.Path); }
            catch { path = item.Path; }
            if (normalized.Contains(path)) SetAutomationStage(item, stage);
        }
    }

    private static string StageForClassification(AutomationFileKind kind) => kind switch
    {
        AutomationFileKind.BankPdf => "Bank PDF • extracting",
        AutomationFileKind.BankOcrSource => "Bank OCR • preparing document batch",
        AutomationFileKind.Gstr3bPdf => "GSTR-3B • importing",
        AutomationFileKind.Form26AsPdf => "26AS • importing tax-credit authority",
        AutomationFileKind.BankSpreadsheet => "Bank workbook • validating/importing",
        AutomationFileKind.AccountingWorkbook => "Accounting workbook • Master → Bank → Mapping → Sales/Purchase/Journal",
        AutomationFileKind.BankSuspenseMapping => "Bank Suspense • importing ledger mappings",
        AutomationFileKind.Mt940 => "Bank MT940 • converting/importing",
        AutomationFileKind.Camt053 => "Bank camt.053 • converting/importing",
        AutomationFileKind.Ofx => "Bank OFX/QBO • converting/importing",
        AutomationFileKind.Qif => "Bank QIF • converting/importing",
        AutomationFileKind.Purchase => "Purchase • resolving/importing",
        AutomationFileKind.Sales => "Sales • validating/importing",
        AutomationFileKind.Cash => "Cash • validating/importing",
        AutomationFileKind.Journal => "Journal • validating/importing",
        AutomationFileKind.Unsupported => "Unsupported • Action Required",
        AutomationFileKind.Unknown => "Review Required • classifying",
        _ => "Processing"
    };

    private async Task DownloadsMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
                if (!_settings.GetBool(ApplicationSettingKeys.AutomationEnabled) ||
                    !_settings.GetBool(ApplicationSettingKeys.AutomationAutoImportKnown)) continue;

                foreach (var folder in _automationRepository.GetFolders())
                {
                    if (folder.IsPaused || folder.ClientId <= 0 || !Directory.Exists(folder.FolderPath)) continue;
                    ScheduleRegisteredClientGstBootstrap(folder, folder.ClientId);
                    await ProcessDownloadedCustomerMasterAsync(folder, cancellationToken);
                }
                PipelineRuntimeDiagnostics.WriteReportIfDue();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AutomationDiagnosticLog.Write("DOWNLOADS-MAINTENANCE", exception.Message);
            }
        }
    }

    private async Task<int> ProcessDownloadedCustomerMasterAsync(AutomationFolderRegistration folder, CancellationToken cancellationToken)
    {
        var client = _clientRepository.GetById(folder.ClientId);
        if (client is null) return 0;
        var gstin = NormalizeGstin(client.Gstin);
        if (gstin.Length != 15) return 0;

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var targetFolder = Path.Combine(folder.FolderPath, "Sales", "Customer Master");
        Directory.CreateDirectory(targetFolder);

        var candidates = new List<(string Path, bool FromDownloads, DateTime WrittenUtc)>();
        try
        {
            if (Directory.Exists(downloads))
            {
                candidates.AddRange(Directory.EnumerateFiles(downloads, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsCustomerMasterWorkbookPath)
                    .Where(path => Path.GetFileName(path).Contains("MASTER", StringComparison.OrdinalIgnoreCase))
                    .Select(path => (path, true, SafeGetLastWriteTimeUtc(path)))
                    .OrderByDescending(item => item.Item3)
                    .Take(20));
            }

            // Completion 41: a Master Details workbook may already have been moved on a
            // previous pass before any Sales fallback rows existed, or before the current
            // format was understood. Re-scan the linked evidence folder whenever registered
            // B2B/CDNR fallback names still exist so movement and name resolution cannot get
            // out of sync.
            if (_salesRecordRepository.HasAutomaticCustomerFallbacks(folder.ClientId, folder.FinancialYear))
            {
                candidates.AddRange(Directory.EnumerateFiles(targetFolder, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsCustomerMasterWorkbookPath)
                    .Select(path => (path, false, SafeGetLastWriteTimeUtc(path)))
                    .OrderByDescending(item => item.Item3)
                    .Take(30));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AutomationDiagnosticLog.Write("SALES-CUSTOMER-MASTER", $"Master scan deferred: {exception.Message}", targetFolder);
            return 0;
        }

        if (candidates.Count == 0) return 0;

        var mergedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var evidencePaths = new List<string>();
        var recognizedFiles = 0;

        foreach (var candidate in candidates
                     .GroupBy(item => Path.GetFullPath(item.Path), StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.OrderByDescending(item => item.WrittenUtc).First())
                     .OrderByDescending(item => item.WrittenUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = candidate.Path;
            try
            {
                var fileNameGstin = NormalizeGstin(Path.GetFileName(source));
                if (fileNameGstin.Length >= 15 && !fileNameGstin.Contains(gstin, StringComparison.OrdinalIgnoreCase)) continue;

                var names = SalesAutoImportService.ReadCustomerMasterNamesForBackfill(source, cancellationToken);
                if (names.Count == 0) continue;
                recognizedFiles++;
                foreach (var pair in names)
                {
                    // Candidates are newest-first. Preserve the newest valid name for a GSTIN.
                    if (!mergedNames.ContainsKey(pair.Key)) mergedNames[pair.Key] = pair.Value;
                }

                var evidencePath = source;
                if (candidate.FromDownloads)
                {
                    var hash = SafeHash(source);
                    var target = Path.Combine(targetFolder, Path.GetFileName(source));
                    if (File.Exists(target))
                    {
                        var existingHash = SafeHash(target);
                        if (existingHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(source); } catch { }
                            evidencePath = target;
                        }
                        else
                        {
                            target = Path.Combine(targetFolder, $"{Path.GetFileNameWithoutExtension(source)}_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(source)}");
                        }
                    }

                    if (File.Exists(source))
                    {
                        try
                        {
                            File.Move(source, target);
                        }
                        catch (IOException)
                        {
                            File.Copy(source, target, overwrite: false);
                            File.Delete(source);
                        }
                        evidencePath = target;
                        AutomationDiagnosticLog.Write(
                            "SALES-CUSTOMER-MASTER",
                            $"Moved recognized GST Master Details workbook into linked Sales\\Customer Master. Names available={names.Count:N0}.",
                            evidencePath);
                    }
                }

                evidencePaths.Add(evidencePath);
            }
            catch (Exception exception)
            {
                AutomationDiagnosticLog.Write("SALES-CUSTOMER-MASTER", $"Candidate deferred: {exception.Message}", source);
            }
        }

        if (mergedNames.Count == 0) return 0;

        var updated = _salesRecordRepository.BackfillAutomaticCustomerNames(
            folder.ClientId,
            folder.FinancialYear,
            mergedNames);

        var key = $"{folder.ClientId}|{folder.FinancialYear}";
        var aggregateHash = string.Join("|", evidencePaths
            .Where(File.Exists)
            .Select(SafeHash)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        if (aggregateHash.Length > 0) _processedCustomerMasterHashes[key] = aggregateHash;

        AutomationDiagnosticLog.Write(
            "SALES-CUSTOMER-MASTER",
            $"Customer Master backfill evaluated {recognizedFiles:N0} recognized workbook(s), {mergedNames.Count:N0} recipient GSTIN name(s), and updated {updated:N0} registered B2B/CDNR row(s).",
            evidencePaths.FirstOrDefault() ?? targetFolder);

        if (updated > 0)
        {
            RaiseAccountingDataChanged(
                folder,
                "Sales",
                updated,
                evidencePaths.FirstOrDefault() ?? targetFolder);
        }

        await Task.CompletedTask;
        return updated;
    }

    private static bool IsCustomerMasterWorkbookPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryProcessBankSpreadsheetCacheFirstAsync(
        QueuedAutomationFile item,
        CancellationToken cancellationToken)
    {
        // Fix38 shared Sales/Purchase sources are already classified once by the global
        // source watcher. Never route them through Bank workbook probing first.
        if (item.ForcedKind is { } forcedKind && forcedKind != AutomationFileKind.BankSpreadsheet) return false;
        if (!IsSpreadsheetSourcePath(item.Path)) return false;
        var folder = _automationRepository.GetByFolderId(item.FolderId);
        if (folder is null || folder.IsPaused || !File.Exists(item.Path)) return false;

        // BR4 fast reopen path: a file that has the exact same path/size/mtime as its
        // persistent registry entry never enters transaction-level validation again.
        try
        {
            var registry = _automationRepository.GetFileRegistryByPath(folder.FolderId, folder.ClientId, item.Path);
            if (!item.ManualReimport && registry is not null)
            {
                var info = new FileInfo(item.Path);
                if (info.Exists && registry.FileSize > 0 && registry.LastWriteUtc != DateTime.MinValue &&
                    info.Length == registry.FileSize && info.LastWriteTimeUtc == registry.LastWriteUtc &&
                    (registry.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase) ||
                     registry.Status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!NeedsTallySalesRegisterSummaryReplayForPath(folder, item.Path))
                    {
                        RememberObservedFileState(item.Path);
                        SetAutomationStage(item, "Verified unchanged • file-level only");
                        AutomationDiagnosticLog.Write(
                            "AUTOIMPORT-FILE-REGISTRY",
                            $"Unchanged source skipped before hash/workbook/transaction processing. size={info.Length}; lastWriteUtc={info.LastWriteTimeUtc:O}; status={registry.Status}",
                            item.Path);
                        return true;
                    }

                    AutomationDiagnosticLog.Write(
                        "R17-SALES-SUMMARY-REPLAY",
                        "Bypassed the earliest path/size/mtime registry barrier because this unchanged Tally Sales Register still has only pre-R15 evidence history and no persisted Sales summary rows.",
                        item.Path);
                }
            }
        }
        catch (Exception exception)
        {
            AutomationDiagnosticLog.Write("AUTOIMPORT-FILE-REGISTRY", $"File-level registry lookup deferred: {exception.Message}", item.Path);
        }

        // Do not make a non-Bank spreadsheet wait on the cache path when it is still
        // actively being written. The established generic worker can handle that source.
        SetAutomationStage(item, "Spreadsheet source • checking file history");
        if (!await WaitForBankSourceReadyAsync(item.Path, cancellationToken).ConfigureAwait(false))
            return false;

        // Durable Auto Import history is the first duplicate authority. Hash the stable
        // source before opening Excel/workbook content. A previously successful Auto Import
        // is skipped here without classification or transaction-level duplicate checking.
        var fileHash = ComputeHash(item.Path);
        var earlyProcessedHistory = _automationRepository.GetProcessedFile(folder.FolderId, folder.ClientId, fileHash);
        if (!item.ManualReimport && earlyProcessedHistory is not null)
        {
            if (NeedsTallySalesRegisterSummaryReplay(folder, earlyProcessedHistory, fileHash))
            {
                _automationRepository.DeleteProcessedFile(folder.FolderId, folder.ClientId, fileHash);
                AutomationDiagnosticLog.Write("R15-SALES-SUMMARY-REPLAY", "Cleared pre-R15 evidence-only Tally Sales Register history so the unchanged source can be imported once as direct Sales statement summaries.", item.Path);
                earlyProcessedHistory = null;
            }
        }
        if (!item.ManualReimport && earlyProcessedHistory is not null)
        {
            var historyIsBank = IsProcessedBankHistory(earlyProcessedHistory);
            if (!historyIsBank || BankStatementSourceStillExists(earlyProcessedHistory))
            {
                RecordHistory(folder, item.Path, BuildDuplicateObservationKey(fileHash, item.Path), "Duplicate", "Duplicate",
                    "The same source content was already successfully processed through Auto Import for this Client/FY. File-level history stopped processing before workbook/classification/transaction checks.");
                RememberObservedFileState(item.Path);
                return true;
            }

            _automationRepository.DeleteProcessedFile(folder.FolderId, folder.ClientId, fileHash);
            AutomationDiagnosticLog.Write("STALE-HISTORY", "Previous Auto Import Bank history was cleared because its Bank Statement no longer exists in the application.", item.Path);
        }

        SetAutomationStage(item, "Spreadsheet source • caching");

        // Start the source-read immediately for normal OpenXML workbooks. Password-candidate
        // discovery is performed by BankStatementImportService only if the workbook actually
        // requires Excel/password recovery, so ordinary Bank files do not pay that pre-read cost.
        var importService = new BankStatementImportService(_bankingRepository, _masterRepository);
        BankStatementImportService.BankStatementSourceCache sourceCache;
        try
        {
            sourceCache = await importService.ExtractSourceAsync(
                item.Path,
                item.Path,
                Path.GetFileName(item.Path),
                fileHash,
                initialPassword: null,
                recoveryMode: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Protected/legacy/non-Bank workbook compatibility remains on the established
            // path. The important invariant is that a normal readable Bank workbook never
            // waits for another file's accounting work before entering RAM.
            AutomationDiagnosticLog.Write("BANK-SOURCE-CACHE", $"Cache-first extraction deferred to compatibility path: {exception.Message}", item.Path);
            return false;
        }

        var accessMs = sourceCache.SourceAccessMilliseconds;
        AutomationDiagnosticLog.Write(
            accessMs > 1000d ? "BANK-SOURCE-ACCESS-SLOW" : "BANK-SOURCE-ACCESS",
            $"Workbook data copied to RAM and source closed in {accessMs:N0} ms; target=1000 ms; bytes={sourceCache.FileSize:N0}.",
            item.Path);

        var classification = _fileClassifier.ClassifyWorkbookData(sourceCache.Workbook, Path.GetFileName(item.Path));
        TryUpdateImportJobClassification(folder, item.Path, classification);
        SetAutomationStage(item, StageForClassification(classification.Kind));

        // One-read Auto Import contract: classification and the target module consume the
        // same workbook held in RAM. The linked Automation Folder is the authoritative
        // Client/FY owner; the file is not required to repeat GSTIN/account identity.

        if (!item.ManualReimport && !_settings.GetBool(ApplicationSettingKeys.AutomationAutoImportKnown))
        {
            RecordHistory(folder, item.Path, sourceCache.FileHash, classification.Kind.ToString(), "Skipped", "Automatic import of known formats is disabled in Settings.");
            return true;
        }

        switch (classification.Kind)
        {
            case AutomationFileKind.AccountingWorkbook:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, sourceCache.FileHash, item.ManualReimport)) return true;
                await RunCachedClientImportAsync(
                    folder,
                    () => ProcessLinkedAccountingWorkbookAsync(folder, item.Path, sourceCache.FileHash, sourceCache.Workbook, sourceCache, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                return true;

            case AutomationFileKind.Sales:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, sourceCache.FileHash, item.ManualReimport)) return true;
                await RunCachedClientImportAsync(folder, () => ProcessSalesCachedAsync(folder, item.Path, sourceCache.FileHash, sourceCache.Workbook, cancellationToken), cancellationToken).ConfigureAwait(false);
                return true;

            case AutomationFileKind.Cash:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, sourceCache.FileHash, item.ManualReimport)) return true;
                await RunCachedClientImportAsync(folder, () => ProcessUniversalCashCachedAsync(folder, item.Path, sourceCache.FileHash, sourceCache.Workbook, cancellationToken), cancellationToken).ConfigureAwait(false);
                return true;

            case AutomationFileKind.Journal:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, sourceCache.FileHash, item.ManualReimport)) return true;
                await RunCachedClientImportAsync(folder, () => ProcessUniversalJournalCachedAsync(folder, item.Path, sourceCache.FileHash, sourceCache.Workbook, cancellationToken), cancellationToken).ConfigureAwait(false);
                return true;

            case AutomationFileKind.Purchase:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, sourceCache.FileHash, item.ManualReimport)) return true;
                if (!item.ManualReimport && !_settings.GetBool(ApplicationSettingKeys.PurchaseAutoImportKnown))
                {
                    RecordHistory(folder, item.Path, sourceCache.FileHash, "Purchase", "Skipped", "Automatic Purchase import is disabled in Settings.");
                    return true;
                }
                await RunCachedClientImportAsync(folder, () => ProcessPurchaseWorkbookAsync(folder, item.Path, sourceCache.FileHash, sourceCache.Workbook, cancellationToken), cancellationToken).ConfigureAwait(false);
                return true;

            case AutomationFileKind.Unknown:
            case AutomationFileKind.Unsupported:
                var cachedDetail = $"{classification.Reason} Confidence: {classification.Confidence}. Scores: Bank {classification.BankScore}, Sales {classification.SalesScore}, Purchase {classification.PurchaseScore}, Cash {classification.CashScore}, Journal {classification.JournalScore}.";
                RecordHistory(folder, item.Path, sourceCache.FileHash, classification.Kind.ToString(), "Needs Action", cachedDetail);
                AddAction(folder, "Automation", classification.Kind == AutomationFileKind.Unsupported ? "Unsupported Format" : "Unknown Format", Path.GetFileName(item.Path), $"{cachedDetail}\nSource: {item.Path}", sourceCache.FileHash);
                return true;

            case AutomationFileKind.BankSpreadsheet:
                break;

            default:
                // Specialized spreadsheet types (for example the Bank Suspense mapping
                // workbook) deliberately retain their established module handler.
                return false;
        }

        var bankKey = $"{folder.ClientId}|{folder.FinancialYear}|UNKNOWN|{sourceCache.FileHash}";
        if (importService.TryGetBankIdentity(sourceCache, out var bankName, out var accountNumber, out var accountType))
        {
            bankKey = $"{folder.ClientId}|{folder.FinancialYear}|{bankName.Trim().ToUpperInvariant()}|{DigitsOnly(accountNumber)}|{accountType.Trim().ToUpperInvariant()}";
        }
        else
        {
            // Some bank formats expose the account identity only through the already
            // trusted Client/FY Bank Account fallback. Use that same authority for the
            // queue key so files from one account are still finalized as one bank batch.
            var fallback = SelectTrustedBankAccountFallback(folder, sourceCache.IdentityPath);
            if (fallback is not null)
                bankKey = $"{folder.ClientId}|{folder.FinancialYear}|{fallback.BankName.Trim().ToUpperInvariant()}|{DigitsOnly(fallback.AccountNumber)}|{fallback.AccountType.Trim().ToUpperInvariant()}";
        }

        PipelineRuntimeDiagnostics.ImportStarted(item.ClientId, item.FinancialYear, item.Path);
        SetAutomationStage(item, "Bank cache • queued bank-wise");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = _bankCachedQueues.GetOrAdd(bankKey, _ => new ConcurrentQueue<BankCachedWorkItem>());
        queue.Enqueue(new BankCachedWorkItem(item, folder, sourceCache, completion));
        _bankCachedLastEnqueueTicks[bankKey] = Stopwatch.GetTimestamp();
        if (_bankCachedProcessors.TryAdd(bankKey, 0))
            _ = Task.Run(() => ProcessBankCachedQueueAsync(bankKey, cancellationToken), CancellationToken.None);

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task RunCachedClientImportAsync(AutomationFolderRegistration folder, Func<Task> action, CancellationToken cancellationToken)
    {
        var clientGateKey = $"{folder.ClientId}|{folder.FinancialYear}";
        var clientGate = _clientImportGates.GetOrAdd(clientGateKey, _ => new SemaphoreSlim(1, 1));
        await clientGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            clientGate.Release();
        }
    }

    private static bool IsGstReturnSourceSetComponent(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".json" or ".zip";
    }

    private void IndexLinkedSalesPurchaseSourceSetComponent(
        AutomationFolderRegistration folder,
        string path,
        string fileHash,
        string module)
    {
        var client = _clientRepository.GetById(folder.ClientId);
        if (client is null) return;
        var gstin = NormalizeGstin(client.Gstin);
        if (gstin.Length != 15 || !File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            SalesPurchaseSourceCache.Record(
                path, module, gstin, folder.FinancialYear, fileHash, info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception exception)
        {
            AutomationDiagnosticLog.Write("SALES-PURCHASE-SET", $"Linked source indexing deferred: {exception.Message}", path);
        }
    }

    private static bool IsSpreadsheetSourcePath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".xlsx" or ".xlsm" or ".xls" or ".xltx" or ".xltm" or ".csv" or ".txt" or ".tsv";
    }

    private static async Task<bool> WaitForBankSourceReadyAsync(string path, CancellationToken cancellationToken)
    {
        long previousLength = -1;
        DateTime previousWrite = DateTime.MinValue;
        var unchangedSamples = 0;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0)
                {
                    unchangedSamples = 0;
                }
                else
                {
                    // A successful read-open plus two unchanged 150ms samples is enough for
                    // cache extraction. We no longer impose a fixed three-second delay.
                    // Exclude an existing writer: if another process still has write
                    // access, opening without FileShare.Write fails and we retry. Once this
                    // succeeds, one short unchanged sample is enough to begin RAM extraction.
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    if (info.Length == previousLength && info.LastWriteTimeUtc == previousWrite)
                        unchangedSamples++;
                    else
                        unchangedSamples = 0;
                    previousLength = info.Length;
                    previousWrite = info.LastWriteTimeUtc;
                    if (unchangedSamples >= 1) return true;
                }
            }
            catch (IOException)
            {
                unchangedSamples = 0;
            }
            catch (UnauthorizedAccessException)
            {
                unchangedSamples = 0;
            }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private async Task WaitForBankCacheBurstToSettleAsync(string bankKey, CancellationToken cancellationToken)
    {
        var maximumWait = Stopwatch.StartNew();
        while (maximumWait.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_bankCachedLastEnqueueTicks.TryGetValue(bankKey, out var lastEnqueue))
                return;
            if (Stopwatch.GetElapsedTime(lastEnqueue) >= TimeSpan.FromMilliseconds(300))
                return;
            await Task.Delay(75, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessBankCachedQueueAsync(string bankKey, CancellationToken cancellationToken)
    {
        try
        {
            var gate = _bankAccountImportGates.GetOrAdd(bankKey, _ => new SemaphoreSlim(1, 1));
            while (!cancellationToken.IsCancellationRequested)
            {
                // Wait until the parallel source-extraction burst is quiet. This lets fast
                // and slightly slower workbooks from the same Bank/account join one finalized
                // cache batch instead of starting reconciliation after the first file alone.
                await WaitForBankCacheBurstToSettleAsync(bankKey, cancellationToken).ConfigureAwait(false);
                if (!_bankCachedQueues.TryGetValue(bankKey, out var queue)) return;
                var batch = new List<BankCachedWorkItem>();
                while (queue.TryDequeue(out var work)) batch.Add(work);
                if (batch.Count == 0) return;

                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    foreach (var work in batch)
                        SetAutomationStage(work.Item, $"Bank cache • processing {batch.Count:N0} file(s) bank-wise");
                    await ProcessBankWorkbookCacheBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    foreach (var work in batch)
                        work.Completion.TrySetException(exception);
                }
                finally
                {
                    gate.Release();
                }

                if (queue.IsEmpty) return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_bankCachedQueues.TryGetValue(bankKey, out var queue))
                while (queue.TryDequeue(out var work)) work.Completion.TrySetCanceled(cancellationToken);
        }
        finally
        {
            _bankCachedProcessors.TryRemove(bankKey, out _);
            if (_bankCachedQueues.TryGetValue(bankKey, out var queue) && queue.IsEmpty)
                _bankCachedLastEnqueueTicks.TryRemove(bankKey, out _);
            if (_bankCachedQueues.TryGetValue(bankKey, out queue) && !queue.IsEmpty &&
                _bankCachedProcessors.TryAdd(bankKey, 0))
            {
                var token = _lifetime?.Token ?? CancellationToken.None;
                _ = Task.Run(() => ProcessBankCachedQueueAsync(bankKey, token), CancellationToken.None);
            }
        }
    }

    private async Task ProcessBankWorkbookCacheBatchAsync(
        IReadOnlyList<BankCachedWorkItem> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;
        var primary = batch[0];
        var sources = batch.Select(work => work.Source).ToArray();
        var clientId = primary.Folder.ClientId;
        var financialYear = primary.Folder.FinancialYear;
        var importService = new BankStatementImportService(_bankingRepository, _masterRepository);
        var linkedClient = _clientRepository.GetById(clientId)?.Clone();
        if (linkedClient is not null) linkedClient.FinancialYear = financialYear;
        var useTallyPreImport = _tallyBankStatementReconciliationService is not null &&
            linkedClient is not null && IsTallyCurrentlyConnected();

        var sourceDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sourceIdentityPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fallbackBankNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fallbackAccountNumbers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fallbackAccountTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fallbackBranchNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fallbackIfscCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var work in batch)
        {
            var path = work.Source.OriginalPath;
            sourceDisplayNames[path] = work.Source.DisplayName;
            sourceIdentityPaths[path] = work.Source.IdentityPath;
            if (!string.IsNullOrWhiteSpace(work.Source.ResolvedPassword)) passwords[path] = work.Source.ResolvedPassword;
            var fallback = SelectTrustedBankAccountFallback(work.Folder, work.Source.IdentityPath);
            if (fallback is null) continue;
            fallbackBankNames[path] = fallback.BankName;
            fallbackAccountNumbers[path] = fallback.AccountNumber;
            fallbackAccountTypes[path] = fallback.AccountType;
            fallbackBranchNames[path] = fallback.BranchName;
            fallbackIfscCodes[path] = fallback.IfscCode;
        }

        var useTransactionFallback = batch.All(work => !work.Item.ManualReimport);

        // The file-level Auto Import history gate already removed sources that Auto Import
        // successfully handled before. Only the remaining (history-unknown) Bank sources use
        // the transaction-level fallback. Fix24 first-import-wins now removes strongly proven
        // later physical duplicates regardless of Ledger or source-file ownership; the first
        // imported Bank row remains authoritative and its Ledger is never replaced here.
        if (useTransactionFallback)
        {
            try
            {
            var cleanup = _bankingRepository.CleanupExactTransactionDuplicates(clientId, financialYear);
            if (cleanup.TransactionsRemoved > 0 || cleanup.ConflictingGroupsSkipped > 0 || cleanup.ProtectedGroupsSkipped > 0)
            {
                AutomationDiagnosticLog.Write(
                    "BANK-TRANSACTION-FALLBACK-CLEANUP",
                    $"groups={cleanup.DuplicateGroupsFound}; removed={cleanup.TransactionsRemoved}; protectedSkipped={cleanup.ProtectedGroupsSkipped}; conflictsSkipped={cleanup.ConflictingGroupsSkipped}; emptyStatementsRemoved={cleanup.EmptyStatementsRemoved}",
                    primary.Source.IdentityPath);
            }
            }
            catch (Exception exception)
            {
                // Cleanup is a fallback safety net, never an excuse to block a new statement.
                AutomationDiagnosticLog.Write("BANK-TRANSACTION-FALLBACK-CLEANUP", $"Cleanup deferred safely: {exception.Message}", primary.Source.IdentityPath);
            }
        }

        var request = new BankStatementImportRequest
        {
            ClientId = clientId,
            FinancialYear = financialYear,
            FilePaths = sources.Select(source => source.OriginalPath).ToArray(),
            ClearExisting = false,
            StrictMetadata = true,
            // Changed files are whole-file replacements in BR4; transaction-delta import
            // is deliberately disabled on the cache-first Auto Import path.
            AllowSourceDelta = false,
            ForceSourceReplacement = batch.Any(work => work.Item.ManualReimport),
            AutoImportTransactionDuplicateFallback = useTransactionFallback,
            Passwords = passwords,
            Attempt = 1,
            RecoveryMode = false,
            SourceDisplayNames = sourceDisplayNames,
            SourceIdentityPaths = sourceIdentityPaths,
            MetadataFallbackBankNames = fallbackBankNames,
            MetadataFallbackAccountNumbers = fallbackAccountNumbers,
            MetadataFallbackAccountTypes = fallbackAccountTypes,
            MetadataFallbackBranchNames = fallbackBranchNames,
            MetadataFallbackIfscCodes = fallbackIfscCodes,
            PreImportReconciliationAsync = !useTallyPreImport
                ? null
                : ct => _tallyBankStatementReconciliationService!.ReconcileBeforeImportAsync(linkedClient!, ct),
            PostImportCheckpointAsync = !useTallyPreImport
                ? null
                : ct => _tallyBankStatementReconciliationService!.CheckpointAfterBankImportAsync(linkedClient!, ct)
        };

        var batchWatch = Stopwatch.StartNew();
        var result = await importService.ImportCachedAsync(request, sources, cancellationToken).ConfigureAwait(false);

        if (result.FilesImported > 0)
        {
            // Other report modules will be moved to cache-first separately. For Banking BR4,
            // run each established post-import authority ONCE for the entire bank batch rather
            // than once per source workbook.
            try
            {
                new BankTransferReconciliationService(_bankingRepository, _masterRepository)
                    .Reconcile(clientId, financialYear);
                var ruleCount = _financeRepository.ApplyBankRules(clientId, financialYear, onlyUnmapped: true);
                new BankLedgerMappingService(_bankingRepository, _masterRepository).Rebuild(clientId, financialYear);
                if (ruleCount > 0)
                    RecordHistory(primary.Folder, primary.Source.IdentityPath, primary.Source.FileHash + ":rules", "Bank Rules", "Applied",
                        $"{ruleCount:N0} transaction(s) mapped once after the finalized bank batch.");
            }
            catch (Exception exception)
            {
                AddAction(primary.Folder, "Banking", "Post Import Review", primary.Source.DisplayName,
                    $"{exception.Message}\nBank batch contained {batch.Count:N0} source file(s).", primary.Source.FileHash + ":post");
            }
        }

        var importedCount = 0;
        foreach (var work in batch)
        {
            var imported = _bankingRepository.StatementHashExists(clientId, financialYear, work.Source.FileHash);
            if (imported)
            {
                importedCount++;
                var statement = _bankingRepository.GetStatements(clientId, financialYear)
                    .Where(row => row.FileHash.Equals(work.Source.FileHash, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(row => row.ImportedUtc)
                    .FirstOrDefault();
                var count = statement?.TransactionCount ?? 0;
                RecordHistory(work.Folder, work.Source.IdentityPath, work.Source.FileHash, "Bank", "Imported",
                    $"Cache-first Bank import finalized. Source access {work.Source.SourceAccessMilliseconds:N0} ms; bank-wise processing completed in {batchWatch.Elapsed.TotalMilliseconds:N0} ms.",
                    count);
                ResolveAutomationAction(work.Folder, "Banking", work.Source.FileHash);
                work.Completion.TrySetResult("Imported");
            }
            else
            {
                var issue = result.Issues.FirstOrDefault(row => row.SourceFile.Equals(work.Source.DisplayName, StringComparison.OrdinalIgnoreCase));
                var detail = issue is null
                    ? result.DuplicateStatementsSkipped > 0
                        ? "The Bank Statement was already authoritative for this Client/FY."
                        : "The cached Bank Statement could not be finalized safely."
                    : $"{issue.ErrorCodeText}: {issue.FriendlyMessage}";
                var status = result.DuplicateStatementsSkipped > 0 && issue is null ? "Duplicate" : "Needs Action";
                RecordHistory(work.Folder, work.Source.IdentityPath, work.Source.FileHash, "Bank", status, detail);
                if (status.Equals("Needs Action", StringComparison.OrdinalIgnoreCase))
                    AddAction(work.Folder, "Banking", "Bank Import Review", work.Source.DisplayName, $"{detail}\nSource: {work.Source.IdentityPath}", work.Source.FileHash);
                work.Completion.TrySetResult(status);
            }
        }

        if (result.FilesImported > 0)
        {
            _activityRepository.Record(new ImportActivity
            {
                ClientId = clientId,
                ImportType = "Bank",
                PeriodFrom = result.PeriodFrom,
                PeriodTo = result.PeriodTo,
                RecordCount = result.TransactionsImported,
                BankCount = _bankingRepository.GetAccounts(clientId, financialYear).Count,
                SourceSummary = batch.Count == 1 ? primary.Source.DisplayName : $"{batch.Count:N0} cached Bank Statement files",
                Status = result.Issues.Count == 0 ? "Auto Imported" : "Review Required",
                Details = result.Summary,
                ImportedUtc = DateTime.UtcNow
            });
            RaiseAccountingDataChanged(primary.Folder, "Banking", result.TransactionsImported, primary.Source.IdentityPath);
            if (_settings.GetBool(ApplicationSettingKeys.NotifyImportComplete))
                Notify("Bank Statements auto-imported",
                    $"{primary.Folder.ClientName}: {importedCount:N0} file(s) finalized bank-wise • {result.TransactionsImported:N0} transaction(s)", false);
        }
    }

    private sealed record BankCachedWorkItem(
        QueuedAutomationFile Item,
        AutomationFolderRegistration Folder,
        BankStatementImportService.BankStatementSourceCache Source,
        TaskCompletionSource<string> Completion);

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!_queue.TryDequeue(out var item)) continue;
            if (_folderMoveQuiesce.ContainsKey(item.FolderId))
            {
                // The physical directory is being renamed/rebased. Drop this old-path
                // observation; the post-move reconciliation scan will enqueue only the
                // final path and unchanged content will still stop at file history.
                _queuedKeys.TryRemove(item.QueueKey, out _);
                RegisterCompletedProgress(item);
                ProgressChanged?.Invoke(this, EventArgs.Empty);
                continue;
            }
            _activeFiles[item.QueueKey] = item;
            SetAutomationStage(item, "Starting");
            try
            {
                while (_globalPause && !item.ManualReimport && !cancellationToken.IsCancellationRequested)
                {
                    SetAutomationStage(item, "Paused - waiting to resume");
                    await Task.Delay(500, cancellationToken);
                }
                PipelineRuntimeDiagnostics.ImportWaiting(item.ClientId, item.FinancialYear, item.Path);
                if (await TryProcessBankSpreadsheetCacheFirstAsync(item, cancellationToken))
                {
                    PipelineRuntimeDiagnostics.ImportFinished(item.ClientId, item.FinancialYear, item.Path);
                }
                else
                {
                    var clientGateKey = $"{item.ClientId}|{item.FinancialYear}";
                    var clientGate = _clientImportGates.GetOrAdd(clientGateKey, _ => new SemaphoreSlim(1, 1));
                    await clientGate.WaitAsync(cancellationToken);
                    try
                    {
                        PipelineRuntimeDiagnostics.ImportStarted(item.ClientId, item.FinancialYear, item.Path);
                        await ProcessQueuedFileAsync(item, cancellationToken);
                        PipelineRuntimeDiagnostics.ImportFinished(item.ClientId, item.FinancialYear, item.Path);
                    }
                    finally
                    {
                        clientGate.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AutomationDiagnosticLog.Write("WORKER-ERROR", exception.ToString(), item.Path);
                var folder = _automationRepository.GetByFolderId(item.FolderId);
                if (folder is not null)
                {
                    var locked = exception.Message.Contains("ACCOUNTING_PERIOD_LOCKED", StringComparison.OrdinalIgnoreCase);
                    var actionType = locked ? "Period Locked" : "Import Failed";
                    var friendly = locked
                        ? "This source belongs to an accounting period that is locked in Finance Center. Unlock the required module/period, then Retry Import."
                        : exception.Message;
                    RecordHistory(folder, item.Path, SafeHash(item.Path), "Unknown", locked ? "Needs Action" : "Failed", friendly);
                    AddAction(folder, "Automation", actionType, Path.GetFileName(item.Path), $"{friendly}\nSource: {item.Path}", SafeHash(item.Path));
                }
                if (_settings.GetBool(ApplicationSettingKeys.NotifyImportFailed))
                    Notify("Automatic import failed", $"{Path.GetFileName(item.Path)}\n{exception.Message}", true);
            }
            finally
            {
                ImportHeaderMappingRuntime.ClearOneShotMapping(item.Path);
                _activeStages.TryRemove(item.QueueKey, out _);
                _activeFiles.TryRemove(item.QueueKey, out _);
                _queuedKeys.TryRemove(item.QueueKey, out _);
                RegisterCompletedProgress(item);
                ProgressChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async Task ProcessQueuedFileAsync(QueuedAutomationFile item, CancellationToken cancellationToken)
    {
        SetAutomationStage(item, "Checking linked folder");
        var folder = _automationRepository.GetByFolderId(item.FolderId);
        if (folder is null)
        {
            AutomationDiagnosticLog.Write("PROCESS", $"Folder registration {item.FolderId} no longer exists; dropping queued observation.", item.Path);
            return;
        }
        if (folder.IsPaused && !item.ManualReimport)
        {
            var pausedHash = File.Exists(item.Path) ? SafeHash(item.Path) : BuildDuplicateObservationKey("PAUSED", item.Path);
            RecordHistory(folder, item.Path, pausedHash, "Automation", "Skipped", "Automation Folder is paused. Resume it and run Recovery Scan to import this source.");
            AutomationDiagnosticLog.Write("PROCESS", "Skipped because Automation Folder is paused.", item.Path);
            return;
        }
        if (!Directory.Exists(folder.FolderPath))
        {
            AutomationDiagnosticLog.Write("PROCESS", $"Registered folder is currently unavailable: {folder.FolderPath}", item.Path);
            return;
        }
        if (!File.Exists(item.Path))
        {
            AutomationDiagnosticLog.Write("PROCESS", "Queued source no longer exists. A reconciliation scan will pick it up if it reappears.", item.Path);
            return;
        }
        SetAutomationStage(item, "Waiting for file write to finish");
        if (!await WaitForStableFileAsync(item.Path, cancellationToken))
        {
            var hash = SafeHash(item.Path);
            RecordHistory(folder, item.Path, hash, "Unknown", "Needs Action", "The file did not become stable/unlocked within the waiting period.");
            AddAction(folder, "Automation", "File Locked", Path.GetFileName(item.Path), $"The file stayed locked or continued changing. Retry after the source application finishes writing it.\nSource: {item.Path}", hash);
            if (_settings.GetBool(ApplicationSettingKeys.NotifyImportFailed))
                Notify("Automatic import needs attention", $"{Path.GetFileName(item.Path)} stayed locked or incomplete.", true);
            return;
        }

        // Password companion notes/workbooks are local helper inputs for protected
        // statements, not accounting sources. Never classify them as Unknown/Unsupported
        // and never create an Action Center item for them. Their contents are not logged.
        if (LocalPasswordCandidateDiscovery.IsLikelyPasswordCompanionFile(item.Path))
        {
            var helperHash = SafeHash(item.Path);
            RecordHistory(folder, item.Path, helperHash, "Password Helper", "Skipped",
                "Local password companion file retained for protected-workbook recovery; it is not an accounting import source.");
            AutomationDiagnosticLog.Write("PASSWORD-HELPER", "Skipped local password companion file from accounting classification and rechecking nearby protected workbooks.", item.Path);
            QueueNearbyProtectedWorkbooksAfterPasswordHelper(folder, item.Path);
            return;
        }

        SetAutomationStage(item, "Hashing source");
        var fileHash = string.IsNullOrWhiteSpace(item.PrecomputedHash)
            ? ComputeHash(item.Path)
            : item.PrecomputedHash;

        // Two-tier duplicate rule: successful Auto Import history is authoritative and
        // must be checked before classification/import. Transaction-level duplicate
        // comparison is only a fallback for history-unknown Bank sources.
        var processedHistory = _automationRepository.GetProcessedFile(folder.FolderId, folder.ClientId, fileHash);
        if (!item.ManualReimport && processedHistory is not null)
        {
            if (NeedsTallySalesRegisterSummaryReplay(folder, processedHistory, fileHash))
            {
                _automationRepository.DeleteProcessedFile(folder.FolderId, folder.ClientId, fileHash);
                AutomationDiagnosticLog.Write("R15-SALES-SUMMARY-REPLAY", "Cleared pre-R15 evidence-only Tally Sales Register history at the normal duplicate barrier.", item.Path);
                processedHistory = null;
            }
        }
        if (!item.ManualReimport && processedHistory is not null)
        {
            var historyIsBank = IsProcessedBankHistory(processedHistory);
            if (!historyIsBank || BankStatementSourceStillExists(processedHistory))
            {
                RecordHistory(folder, item.Path, BuildDuplicateObservationKey(fileHash, item.Path), "Duplicate", "Duplicate",
                    "The same source content was already successfully processed through Auto Import for this Client/FY. File-level history stopped processing before classification/transaction checks.");
                RememberObservedFileState(item.Path);
                return;
            }

            _automationRepository.DeleteProcessedFile(folder.FolderId, folder.ClientId, fileHash);
            AutomationDiagnosticLog.Write("STALE-HISTORY", "Previous Auto Import Bank history was cleared because its Bank Statement no longer exists in the application.", item.Path);
        }

        var extension = Path.GetExtension(item.Path).ToLowerInvariant();
        string? classificationPassword = null;
        IReadOnlyList<string> spreadsheetPasswordCandidates = Array.Empty<string>();
        if (extension is ".xls" or ".xlsx" or ".xlsm" or ".xltx" or ".xltm")
        {
            // Auto Import is non-interactive. Keep the complete deterministic candidate list.
            // The classifier may unlock the workbook with a later candidate; the same list
            // is passed onward to the normal module importer without re-deciding Client ownership.
            spreadsheetPasswordCandidates = LocalPasswordCandidateDiscovery.Discover(item.Path);
            classificationPassword = spreadsheetPasswordCandidates.FirstOrDefault();
        }

        SetAutomationStage(item, "Classifying source");
        var classification = item.ForcedKind is { } centralKind
            ? new AutomationFileClassification(
                centralKind,
                100,
                "AAH supplied an explicit module classification before normal Auto Import routing.",
                SalesScore: centralKind == AutomationFileKind.Sales ? 100 : 0,
                PurchaseScore: centralKind == AutomationFileKind.Purchase ? 100 : 0)
            : ApplyClassificationOverride(item.Path, _fileClassifier.Classify(item.Path, classificationPassword ?? string.Empty));
        if (item.ForcedKind is null && classification.Kind == AutomationFileKind.Unknown && spreadsheetPasswordCandidates.Count > 1 &&
            (SpreadsheetSourceInspector.IsEncryptedOpenXmlContainer(item.Path) ||
             classification.Reason.Contains("password", StringComparison.OrdinalIgnoreCase) ||
             classification.Reason.Contains("encrypted", StringComparison.OrdinalIgnoreCase)))
        {
            // The correct secret may live in a nearby password helper rather than the workbook
            // name. Auto Import must exhaust the same bounded candidate list used by manual import
            // before deciding that the encrypted workbook requires user intervention.
            foreach (var candidate in spreadsheetPasswordCandidates.Skip(1))
            {
                var retryClassification = ApplyClassificationOverride(item.Path, _fileClassifier.Classify(item.Path, candidate));
                if (retryClassification.Kind == AutomationFileKind.Unknown) continue;
                classificationPassword = candidate;
                classification = retryClassification;
                break;
            }
        }
        SetAutomationStage(item, StageForClassification(classification.Kind));
        TryUpdateImportJobClassification(folder, item.Path, classification);

        if (classification.Kind == AutomationFileKind.Unknown &&
            (extension is ".xls" or ".xlsx" or ".xlsm" or ".xltx" or ".xltm") &&
            (SpreadsheetSourceInspector.IsEncryptedOpenXmlContainer(item.Path) ||
             classification.Reason.Contains("password", StringComparison.OrdinalIgnoreCase) ||
             classification.Reason.Contains("encrypted", StringComparison.OrdinalIgnoreCase)))
        {
            RecordHistory(folder, item.Path, fileHash, "Protected Spreadsheet", "Needs Action",
                "Automatic filename/nearby password candidates could not unlock this workbook.");
            AddAction(folder, "Banking", "Password Required", Path.GetFileName(item.Path),
                $"AAH could not unlock this protected workbook with automatic filename/nearby helper candidates. Open Action Center to retry it manually.\nSource: {item.Path}", fileHash);
            if (_settings.GetBool(ApplicationSettingKeys.NotifyImportFailed))
                Notify("Protected workbook needs attention", $"{Path.GetFileName(item.Path)} needs a password that AAH could not resolve automatically.", true);
            return;
        }

        // Folder ownership is authoritative: once a stable supported file is observed
        // under this linked Client/FY Automation Folder, classification selects only the
        // accounting module. It must not re-decide the Client from GSTIN/account text.

        RecordHistory(folder, item.Path, fileHash, "Pending Classification", "Processing", item.ManualReimport ? "Manual Auto Import From Folder found new/changed content." : "Automatic import worker accepted the file.");
        AutomationDiagnosticLog.Write("PROCESS", $"Accepted client={folder.ClientName} FY={folder.FinancialYear} manualScan={item.ManualReimport} hash={fileHash[..Math.Min(12,fileHash.Length)]}", item.Path);

        if (!item.ManualReimport && !_settings.GetBool(ApplicationSettingKeys.AutomationAutoImportKnown))
        {
            RecordHistory(folder, item.Path, fileHash, "Unknown", "Skipped", "Automatic import of known formats is disabled in Settings.");
            return;
        }

        AutomationDiagnosticLog.Write("CLASSIFY", $"kind={classification.Kind} confidence={classification.Confidence} bank={classification.BankScore} sales={classification.SalesScore} purchase={classification.PurchaseScore} cash={classification.CashScore} journal={classification.JournalScore}; {classification.Reason}", item.Path);
        switch (classification.Kind)
        {
            case AutomationFileKind.BankPdf:
                if (!item.ManualReimport && !_settings.GetBool(ApplicationSettingKeys.AutomationMonitorPdf))
                {
                    RecordHistory(folder, item.Path, fileHash, "Bank PDF", "Skipped", "PDF monitoring is disabled in Settings.");
                    return;
                }
                if (item.ManualReimport)
                {
                    // Dashboard -> Auto Import From Folder is an explicit user action.
                    // For PDFs it therefore uses the normal interactive external-Python
                    // call, matching a direct command-line run. Background watcher imports
                    // remain headless and never open surprise dialogs.
                    await ResolvePdfAndImportInteractivelyAsync(
                        folder.ClientId,
                        folder.FinancialYear,
                        item.Path,
                        cancellationToken);
                    return;
                }
                await ProcessPdfAsync(folder, item.Path, fileHash, cancellationToken);
                return;

            case AutomationFileKind.BankOcrSource:
                if (!item.ManualReimport && !_settings.GetBool(ApplicationSettingKeys.AutomationMonitorPdf))
                {
                    const string monitorMessage = "Bank OCR source detected, but Monitor PDF / Bank Images is disabled in Settings. Enable it and use Recovery Scan / Auto Import From Folder to retry.";
                    RecordHistory(folder, item.Path, fileHash, "Bank OCR", "Needs Action", monitorMessage);
                    AddAction(
                        folder,
                        "Banking",
                        "Bank OCR Monitoring Disabled",
                        Path.GetFileName(item.Path),
                        $"{monitorMessage}\nSource: {item.Path}",
                        fileHash);
                    if (_settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                        Notify("Bank OCR needs attention", $"{folder.ClientName}: {Path.GetFileName(item.Path)}", true);
                    return;
                }
                await ProcessBankOcrSourceAsync(folder, item.Path, fileHash, item.ManualReimport, cancellationToken);
                return;

            case AutomationFileKind.Gstr3bPdf:
                await ProcessGstr3bPdfAsync(folder, item.Path, fileHash, cancellationToken);
                break;

            case AutomationFileKind.Form26AsPdf:
                await ProcessForm26AsPdfAsync(folder, item.Path, fileHash, cancellationToken);
                return;

            case AutomationFileKind.AccountingWorkbook:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, fileHash, item.ManualReimport)) return;
                {
                    var workbook = new FlexibleWorkbookReader().Read(item.Path, cancellationToken);
                    await ProcessLinkedAccountingWorkbookAsync(folder, item.Path, fileHash, workbook, null, cancellationToken).ConfigureAwait(false);
                }
                return;

            case AutomationFileKind.BankSpreadsheet:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, fileHash, item.ManualReimport)) return;
                await ProcessBankWorkbookAsync(folder, item.Path, item.Path, Path.GetFileName(item.Path), fileHash, cancellationToken, classificationPassword);
                return;

            case AutomationFileKind.BankSuspenseMapping:
                await ProcessBankSuspenseMappingAsync(folder, item.Path, fileHash, cancellationToken);
                return;

            case AutomationFileKind.Mt940:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, fileHash, item.ManualReimport)) return;
                await ProcessMt940Async(folder, item.Path, fileHash, cancellationToken);
                return;

            case AutomationFileKind.Sales:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, fileHash, item.ManualReimport)) return;
                if (IsGstReturnSourceSetComponent(item.Path))
                {
                    IndexLinkedSalesPurchaseSourceSetComponent(folder, item.Path, fileHash, "Sales");
                    RecordHistory(folder, item.Path, fileHash, "Sales", "Skipped",
                        "GSTR-1 source component indexed into the grouped GSTIN/FY Sales source set; Auto Import will evaluate the complete set once instead of using the manual Sales header parser.");
                    QueueSalesPurchaseSourceSet(folder.ClientId, folder.FinancialYear, "Sales");
                    return;
                }
                await ProcessSalesAsync(folder, item.Path, fileHash, cancellationToken, classificationPassword);
                return;

            case AutomationFileKind.Purchase:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, fileHash, item.ManualReimport)) return;
                if (!item.ManualReimport && !_settings.GetBool(ApplicationSettingKeys.PurchaseAutoImportKnown))
                {
                    RecordHistory(folder, item.Path, fileHash, "Purchase", "Skipped", "Automatic Purchase import is disabled in Settings.");
                    return;
                }
                if (IsGstReturnSourceSetComponent(item.Path))
                {
                    IndexLinkedSalesPurchaseSourceSetComponent(folder, item.Path, fileHash, "Purchase");
                    RecordHistory(folder, item.Path, fileHash, "Purchase", "Skipped",
                        "GSTR-2A/2B source component indexed into the grouped GSTIN/FY Purchase source set; Auto Import will evaluate the complete set once instead of treating this component as a separate import.");
                    QueueSalesPurchaseSourceSet(folder.ClientId, folder.FinancialYear, "Purchase");
                    return;
                }
                await ProcessPurchaseAsync(folder, item.Path, fileHash, cancellationToken, classificationPassword);
                return;

            case AutomationFileKind.Cash:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, fileHash, item.ManualReimport)) return;
                await ProcessUniversalCashAsync(folder, item.Path, fileHash, cancellationToken);
                return;

            case AutomationFileKind.Journal:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, fileHash, item.ManualReimport)) return;
                await ProcessUniversalJournalAsync(folder, item.Path, fileHash, cancellationToken);
                return;

            case AutomationFileKind.Camt053:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, fileHash, item.ManualReimport)) return;
                await ProcessExchangeStatementAsync(folder, item.Path, fileHash, "ISO 20022 camt.053", FinancialExchangeStatementConverters.ConvertCamt053, cancellationToken);
                return;

            case AutomationFileKind.Ofx:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, fileHash, item.ManualReimport)) return;
                await ProcessExchangeStatementAsync(folder, item.Path, fileHash, "OFX/QBO", FinancialExchangeStatementConverters.ConvertOfx, cancellationToken);
                return;

            case AutomationFileKind.Qif:
                if (!IsDataFileMonitoringEnabled(folder, item.Path, fileHash, item.ManualReimport)) return;
                await ProcessExchangeStatementAsync(folder, item.Path, fileHash, "QIF", FinancialExchangeStatementConverters.ConvertQif, cancellationToken);
                return;

            case AutomationFileKind.Unsupported:
                RecordHistory(folder, item.Path, fileHash, "Unsupported", "Needs Action", classification.Reason);
                AddAction(folder, "Automation", "Unsupported Format", Path.GetFileName(item.Path), $"{classification.Reason}\nSource: {item.Path}", fileHash);
                if (_settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                    Notify("Unsupported accounting source", $"{folder.ClientName}: {Path.GetFileName(item.Path)}", true);
                return;

            default:
                var detail = $"{classification.Reason} Confidence: {classification.Confidence}. Scores: Bank {classification.BankScore}, Sales {classification.SalesScore}, Purchase {classification.PurchaseScore}, Cash {classification.CashScore}, Journal {classification.JournalScore}.";
                RecordHistory(folder, item.Path, fileHash, "Unknown", "Needs Action", detail);
                AddAction(folder, "Automation", "Unknown Format", Path.GetFileName(item.Path), $"{detail}\nSource: {item.Path}", fileHash);
                if (_settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                    Notify("File type confirmation required", $"{folder.ClientName}: {Path.GetFileName(item.Path)}", true);
                return;
        }
    }

    private static bool IsProcessedBankHistory(AutomationFileHistoryRecord history)
    {
        if (history is null) return false;
        var type = history.FileType?.Trim() ?? string.Empty;
        return type.Contains("Bank", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("Accounting Workbook", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("MT940", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("OFX", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("QBO", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("QIF", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("camt", StringComparison.OrdinalIgnoreCase);
    }

    private void TryUpdateImportJobClassification(AutomationFolderRegistration folder, string path, AutomationFileClassification classification)
    {
        try
        {
            var job = _governanceRepository.FindJobByWorkingPath(folder.ClientId, folder.FinancialYear, path);
            if (job is null) return;
            var module = classification.Kind switch
            {
                AutomationFileKind.BankPdf or AutomationFileKind.BankOcrSource or AutomationFileKind.BankSpreadsheet or AutomationFileKind.BankSuspenseMapping or AutomationFileKind.Mt940 or AutomationFileKind.Camt053 or AutomationFileKind.Ofx or AutomationFileKind.Qif => "Banking",
                AutomationFileKind.AccountingWorkbook => "Accounting Workbook",
                AutomationFileKind.Gstr3bPdf => "GST",
                AutomationFileKind.Purchase => "Purchase",
                AutomationFileKind.Sales => "Sales",
                AutomationFileKind.Cash => "Cash",
                AutomationFileKind.Journal => "Journal",
                AutomationFileKind.Unsupported => "Unsupported",
                _ => "Review Required"
            };
            _governanceRepository.SetJobClassification(job.JobId, module, classification.Confidence / 100d, classification.Reason);
        }
        catch (Exception exception)
        {
            AutomationDiagnosticLog.Write("IMPORT-JOB", $"Classification synchronization deferred: {exception.Message}", path);
        }
    }

    private Task ProcessBankSuspenseMappingAsync(
        AutomationFolderRegistration folder,
        string path,
        string fileHash,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = _bankSuspenseWorkbookService.Import(path, folder.ClientId, folder.FinancialYear);
                var detail = result.Summary;
                if (result.Errors.Count > 0) detail += $" First error: {result.Errors[0]}";

                RecordHistory(
                    folder,
                    path,
                    fileHash,
                    "Bank Suspense Mapping",
                    result.HasReview ? "Imported with Review" : "Imported",
                    detail,
                    result.Updated);

                if (result.Updated > 0)
                    RaiseAccountingDataChanged(folder, "Banking", result.Updated, path);

                // Conflicts/errors are normal mapping review unless the workbook itself
                // was invalid. They remain visible in Auto Import history rather than
                // becoming a global Action Required item. Once the metadata proves this
                // is an AAH Suspense workbook, also clear stale Unknown/Import Failed
                // actions left by older classifiers for this exact file.
                ResolveAutomationAction(folder, "Banking", fileHash);
                ResolveGeneratedWorkbookActionsForPath(folder, path, "Banking", "Automation");
                AutomationDiagnosticLog.Write("BANK-SUSPENSE-AUTOIMPORT", detail, path);
            }
            catch (Exception exception)
            {
                var detail = $"AAH Suspense workbook could not be imported: {exception.Message}";
                RecordHistory(folder, path, fileHash, "Bank Suspense Mapping", "Needs Action", detail);
                AddAction(folder, "Banking", "Import Failed", Path.GetFileName(path), $"{detail}\nSource: {path}", fileHash);
                AutomationDiagnosticLog.Write("BANK-SUSPENSE-AUTOIMPORT-ERROR", detail, path);
            }
        }, cancellationToken);

    private async Task ProcessLinkedAccountingWorkbookAsync(
        AutomationFolderRegistration folder,
        string sourcePath,
        string fileHash,
        OpenXmlWorkbookData workbook,
        BankStatementImportService.BankStatementSourceCache? sourceCache,
        CancellationToken cancellationToken)
    {
        if (!AutomationFileClassifier.IsLinkedAccountingWorkbook(workbook))
            throw new InvalidDataException("The selected workbook no longer matches the linked Accounting Workbook contract.");

        var client = _clientRepository.GetById(folder.ClientId)
            ?? throw new InvalidOperationException("The client linked to this Accounting Workbook no longer exists.");
        var company = string.IsNullOrWhiteSpace(client.LegalName) ? client.ClientName : client.LegalName;
        var details = new List<string>();
        var importedTotal = 0;

        // 1. Master is first authority. This workbook's Master_DB sheet is a clean
        // Name/Parent/Type/Company table and must be loaded before Bank/Sales/Purchase
        // so downstream ledger resolution never creates avoidable duplicate Masters.
        var masterCount = ImportLinkedAccountingWorkbookMasters(folder, workbook, company);
        importedTotal += masterCount;
        details.Add($"Master {masterCount:N0}");
        if (masterCount > 0)
            RaiseAccountingDataChanged(folder, "Masters", masterCount, sourcePath);

        // 2. Bank Statement uses the already-read cache whenever Auto Import reached
        // this method through the cache-first route. It therefore does not reopen the
        // workbook and still retains all normal duplicate/inter-bank/balance controls.
        var bankStatus = "Template/empty";
        if (HasLinkedWorkbookDataRows(workbook, "Bank Statement", 1, 1, 7))
        {
            bankStatus = await ProcessBankWorkbookAsync(
                folder,
                sourcePath,
                sourcePath,
                Path.GetFileName(sourcePath),
                fileHash,
                cancellationToken,
                sourceCache: sourceCache).ConfigureAwait(false);
            details.Add($"Bank {bankStatus}");

            // 3. The supplied workbook carries row-level Ledger Mapping authority in
            // hidden AA:AG columns. Apply only explicit FinalLedger values to the exact
            // imported Bank Statement source row, then rebuild the normal AAH mapping
            // workbench. Blank workbook mappings remain pending; AAH never guesses them.
            var mapped = ApplyLinkedAccountingWorkbookLedgerMappings(folder, workbook, sourcePath);
            importedTotal += mapped;
            details.Add($"Ledger Mapping {mapped:N0}");
            if (mapped > 0)
                RaiseAccountingDataChanged(folder, "Banking", mapped, sourcePath);
        }
        else
        {
            details.Add("Bank 0");
        }

        // 4. Sales + Credit Note share one deterministic Sales template. A linked
        // accounting workbook deliberately ignores historical learned mapping profiles:
        // its built-in template is authoritative so H/I/J tax amounts cannot be confused
        // with N/O/P tax-ledger names.
        var salesWorkbook = SelectLinkedWorkbookSheets(workbook, "Sales", "Credit Note");
        if (HasLinkedWorkbookDataRows(salesWorkbook, "Sales", 1, 1, 53) ||
            HasLinkedWorkbookDataRows(salesWorkbook, "Credit Note", 1, 1, 53))
        {
            var salesImported = await ProcessLinkedAccountingWorkbookSalesAsync(folder, sourcePath, fileHash, salesWorkbook, client, cancellationToken).ConfigureAwait(false);
            importedTotal += salesImported;
            details.Add($"Sales/Credit Note {salesImported:N0}");
        }
        else details.Add("Sales/Credit Note 0");

        // 5. Purchase + Debit Note use the same shared Purchase parser/profile path,
        // but are isolated from the Sales/Bank sheets. Blank templates are not errors.
        var purchaseWorkbook = SelectLinkedWorkbookSheets(workbook, "Purchase", "Debit note", "Debit Note");
        if (HasAnyLinkedWorkbookDataRows(purchaseWorkbook, 1, 1, 31))
        {
            var before = _purchaseRecordRepository.GetRecords(folder.ClientId, folder.FinancialYear).Count;
            await ProcessPurchaseWorkbookAsync(folder, sourcePath, fileHash, purchaseWorkbook, cancellationToken).ConfigureAwait(false);
            var after = _purchaseRecordRepository.GetRecords(folder.ClientId, folder.FinancialYear).Count;
            var purchaseImported = Math.Max(0, after - before);
            importedTotal += purchaseImported;
            details.Add($"Purchase/Debit Note {purchaseImported:N0}");
        }
        else details.Add("Purchase/Debit Note 0");

        // 6. Only the actual A:F Journal entry table is routed to Journal. The GST
        // calculator/control area on the same worksheet is intentionally ignored.
        var journalWorkbook = SelectLinkedWorkbookSheets(workbook, "Journal");
        if (HasLinkedJournalVoucherRows(journalWorkbook))
        {
            var job = _governanceRepository.FindJobByWorkingPath(folder.ClientId, folder.FinancialYear, sourcePath);
            var journalResult = _universalCashJournalImporter.ImportJournal(
                folder.ClientId,
                folder.FinancialYear,
                journalWorkbook,
                sourcePath,
                job?.RunId ?? string.Empty,
                job?.JobId ?? string.Empty,
                cancellationToken);
            importedTotal += journalResult.ImportedCount;
            details.Add($"Journal {journalResult.ImportedCount:N0}");
            if (journalResult.ImportedCount > 0)
                RaiseAccountingDataChanged(folder, "Journal", journalResult.ImportedCount, sourcePath);
            if (journalResult.ReviewCount > 0)
                AddAction(folder, "Journal", "Review Imported Journal", Path.GetFileName(sourcePath), $"{journalResult.Detail}\nSource: {sourcePath}", fileHash + ":journal");
        }
        else details.Add("Journal 0");

        var detail = $"Linked Accounting Workbook imported in dependency order: {string.Join(" • ", details)}. Empty module templates were retained as templates and did not create rows.";
        RecordHistory(folder, sourcePath, fileHash, "Accounting Workbook", "Imported", detail, importedTotal);
        ResolveAutomationAction(folder, "Automation", fileHash);
        _activityRepository.Record(new ImportActivity
        {
            ClientId = folder.ClientId,
            ImportType = "Accounting Workbook",
            RecordCount = importedTotal,
            SourceSummary = Path.GetFileName(sourcePath),
            Status = "Imported",
            Details = detail,
            ImportedUtc = DateTime.UtcNow
        });
        if (_settings.GetBool(ApplicationSettingKeys.NotifyImportComplete))
            Notify("Accounting Workbook imported", $"{folder.ClientName}: {Path.GetFileName(sourcePath)} • {detail}", false);
    }

    private int ImportLinkedAccountingWorkbookMasters(
        AutomationFolderRegistration folder,
        OpenXmlWorkbookData workbook,
        string company)
    {
        if (!workbook.TryGetWorksheet("Master_DB", out var sheet) || sheet.MaxRow < 2) return 0;
        var records = new List<MasterRecord>();
        for (var row = 2; row <= sheet.MaxRow; row++)
        {
            var name = sheet.GetCell(row, 1).Trim();
            var parent = sheet.GetCell(row, 2).Trim();
            var type = sheet.GetCell(row, 3).Trim();
            var sourceCompany = sheet.GetCell(row, 4).Trim();
            if (name.Length == 0 || type.Length == 0) continue;
            if (!type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                !type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)) continue;
            records.Add(new MasterRecord
            {
                ClientId = folder.ClientId,
                FinancialYear = folder.FinancialYear,
                Name = name,
                Parent = parent.Length > 0 ? parent : "Primary",
                Type = type.Equals("Group", StringComparison.OrdinalIgnoreCase) ? "Group" : "Ledger",
                Company = sourceCompany.Length > 0 ? sourceCompany : company,
                Source = "Linked Accounting Workbook"
            });
        }
        if (records.Count == 0) return 0;
        return _masterRepository.Upsert(folder.ClientId, folder.FinancialYear, records, markUserEdited: false);
    }

    private int ApplyLinkedAccountingWorkbookLedgerMappings(
        AutomationFolderRegistration folder,
        OpenXmlWorkbookData workbook,
        string sourcePath)
    {
        if (!workbook.TryGetWorksheet("Ledger Mapping", out var sheet) || sheet.MaxRow < 2) return 0;
        var transactions = _bankingRepository.GetTransactions(folder.ClientId, folder.FinancialYear, true);
        if (transactions.Count == 0) return 0;
        var accounts = _bankingRepository.GetAccounts(folder.ClientId, folder.FinancialYear)
            .ToDictionary(account => account.Id, account => account.AuthorityName, EqualityComparer<long>.Default);
        var masterGroups = _masterRepository.Get(folder.ClientId, folder.FinancialYear, false, string.Empty)
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !master.IsArchived)
            .GroupBy(master => AccountingText.DuplicateKey(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Parent, StringComparer.OrdinalIgnoreCase);

        var changed = 0;
        for (var row = 2; row <= sheet.MaxRow; row++)
        {
            // Hidden cache: AA Summary Name, AB SourceRow, AC CurrentGroup,
            // AD Bank, AE Raw Summary, AF FinalLedger, AG VoucherType.
            if (!int.TryParse(sheet.GetCell(row, 28).Trim(), out var sourceRow) || sourceRow <= 1) continue;
            var bank = sheet.GetCell(row, 30).Trim();
            var finalLedger = sheet.GetCell(row, 32).Trim();
            if (finalLedger.Length == 0) continue;

            var candidates = transactions.Where(transaction =>
                    transaction.SourceRow == sourceRow &&
                    (transaction.SourceSheet.Length == 0 || transaction.SourceSheet.Equals("Bank Statement", StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (bank.Length > 0)
            {
                candidates = candidates.Where(transaction =>
                {
                    var authority = accounts.TryGetValue(transaction.BankAccountId, out var accountName) ? accountName : transaction.BankAccountName;
                    return authority.Equals(bank, StringComparison.OrdinalIgnoreCase) ||
                           transaction.BankAccountName.Equals(bank, StringComparison.OrdinalIgnoreCase);
                }).ToArray();
            }
            if (candidates.Length != 1) continue; // fail closed on ambiguous source-row identity

            var transaction = candidates[0];
            if (transaction.LedgerName.Equals(finalLedger, StringComparison.OrdinalIgnoreCase) &&
                transaction.MappingStatus.Equals("Mapped", StringComparison.OrdinalIgnoreCase)) continue;
            transaction.LedgerName = finalLedger;
            transaction.MappingStatus = "Mapped";
            _bankingRepository.SaveTransaction(transaction);
            changed++;

            // The Master sheet should already contain the ledger. If an explicit Final
            // Ledger was entered but is absent from Master_DB, retain it as Action Required
            // rather than inventing a group.
            var key = AccountingText.DuplicateKey(finalLedger);
            if (!masterGroups.ContainsKey(key))
                AddAction(folder, "Masters", "Linked Workbook Ledger Missing", finalLedger,
                    $"Ledger Mapping row {row:N0} selected '{finalLedger}', but Master_DB does not contain that Ledger. Add/confirm its Master group before Tally export.\nSource: {sourcePath}",
                    $"{Path.GetFileName(sourcePath)}:ledger:{key}");
        }

        // Rebuild after direct source-row adoption so the normal AAH workbench and all
        // frozen membership counts reflect the same mapped transaction authority.
        new BankLedgerMappingService(_bankingRepository, _masterRepository, _governanceRepository)
            .Rebuild(folder.ClientId, folder.FinancialYear);
        return changed;
    }

    private async Task<int> ProcessLinkedAccountingWorkbookSalesAsync(
        AutomationFolderRegistration folder,
        string sourcePath,
        string fileHash,
        OpenXmlWorkbookData salesWorkbook,
        ClientProfile client,
        CancellationToken cancellationToken)
    {
        var service = new SalesManualImportService();
        // Do NOT load a historical saved/global mapping for this fixed template. The
        // workbook's built-in header contract is first authority on every import.
        var inspection = service.Inspect(salesWorkbook, sourcePath);
        if (!inspection.IsComplete)
        {
            var detail = $"Linked Accounting Workbook Sales template could not map its required fields. Signature: {inspection.HeaderSignature}.";
            RecordHistory(folder, sourcePath, fileHash + ":sales", "Sales", "Needs Action", detail);
            AddAction(folder, "Sales", "Header Mapping", Path.GetFileName(sourcePath), $"{detail}\nSource: {sourcePath}", fileHash + ":sales");
            return 0;
        }

        var result = await Task.Run(() => service.Import(
            salesWorkbook,
            sourcePath,
            folder.ClientId,
            client.Gstin,
            string.IsNullOrWhiteSpace(client.LegalName) ? client.ClientName : client.LegalName,
            folder.FinancialYear,
            inspection.SuggestedMapping,
            string.Empty,
            "SINGLE",
            fileHash), cancellationToken).ConfigureAwait(false);

        var masterSummary = new SalesAccountingMasterSyncService(_masterRepository).Ensure(
            folder.ClientId, folder.FinancialYear, client.ClientName, result.Records);
        var normalization = new SalesStockNormalizationService(_stockMasterRepository, _actionRepository);
        var stockSummary = normalization.Normalize(folder.ClientId, folder.FinancialYear, client.ClientName, result.Records);
        var inserted = _salesRecordRepository.SaveManualImport(folder.ClientId, folder.FinancialYear, result, replaceExisting: false);
        RecordHistory(folder, sourcePath, fileHash + ":sales", "Sales", "Imported",
            $"{inserted:N0} linked-workbook Sales/Credit Note row(s) imported with deterministic built-in mapping; {stockSummary.ActionsRequired:N0} stock review item(s).", inserted);
        ResolveAutomationAction(folder, "Sales", fileHash + ":sales");
        if (inserted > 0) RaiseAccountingDataChanged(folder, "Sales", inserted, sourcePath);
        if (masterSummary.CreatedOrUpdated > 0) RaiseAccountingDataChanged(folder, "Masters", masterSummary.CreatedOrUpdated, sourcePath);
        if (stockSummary.InventoryRows > 0) RaiseAccountingDataChanged(folder, "Stock", stockSummary.InventoryRows, sourcePath);
        return inserted;
    }

    private static OpenXmlWorkbookData SelectLinkedWorkbookSheets(OpenXmlWorkbookData workbook, params string[] names)
    {
        var selected = new Dictionary<string, OpenXmlWorksheetData>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            if (workbook.Worksheets.TryGetValue(name, out var sheet)) selected[sheet.Name] = sheet;
        return new OpenXmlWorkbookData
        {
            FilePath = workbook.FilePath,
            Uses1904DateSystem = workbook.Uses1904DateSystem,
            Worksheets = selected
        };
    }

    private static bool HasAnyLinkedWorkbookDataRows(OpenXmlWorkbookData workbook, int headerRow, int firstColumn, int lastColumn) =>
        workbook.Worksheets.Values.Any(sheet => HasLinkedWorkbookDataRows(sheet, headerRow, firstColumn, lastColumn));

    private static bool HasLinkedWorkbookDataRows(OpenXmlWorkbookData workbook, string sheetName, int headerRow, int firstColumn, int lastColumn) =>
        workbook.TryGetWorksheet(sheetName, out var sheet) && HasLinkedWorkbookDataRows(sheet, headerRow, firstColumn, lastColumn);

    private static bool HasLinkedWorkbookDataRows(OpenXmlWorksheetData sheet, int headerRow, int firstColumn, int lastColumn)
    {
        for (var row = headerRow + 1; row <= sheet.MaxRow; row++)
            for (var column = firstColumn; column <= lastColumn; column++)
                if (!string.IsNullOrWhiteSpace(sheet.GetCell(row, column))) return true;
        return false;
    }

    private static bool HasLinkedJournalVoucherRows(OpenXmlWorkbookData workbook)
    {
        if (!workbook.TryGetWorksheet("Journal", out var sheet)) return false;
        // Only A:F is the actual voucher-entry table. H onward is GST control/reporting.
        for (var row = 2; row <= sheet.MaxRow; row++)
        {
            var hasDate = !string.IsNullOrWhiteSpace(sheet.GetCell(row, 2));
            var hasLedger = !string.IsNullOrWhiteSpace(sheet.GetCell(row, 3));
            var hasAmount = !string.IsNullOrWhiteSpace(sheet.GetCell(row, 4));
            if (hasDate && hasLedger && hasAmount) return true;
        }
        return false;
    }

    private Task ProcessUniversalCashCachedAsync(AutomationFolderRegistration folder, string path, string fileHash, OpenXmlWorkbookData workbook, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                var job = _governanceRepository.FindJobByWorkingPath(folder.ClientId, folder.FinancialYear, path);
                var result = _universalCashJournalImporter.ImportCash(folder.ClientId, folder.FinancialYear, workbook, path, job?.RunId ?? string.Empty, job?.JobId ?? string.Empty, cancellationToken);
                var generatedTemplate = GeneratedAccountingWorkbookIdentity.IsCashWorkbook(workbook);
                if (!result.WasSuccessful && result.ImportedCount == 0 && result.ReviewCount == 0 && generatedTemplate)
                {
                    var detail = "AAH Cash Entry format is correctly classified and is still blank; waiting for completed rows. A blank generated format is not Action Required.";
                    RecordHistory(folder, path, fileHash, "Cash Format", "Template Ready", detail);
                    ResolveAutomationAction(folder, "Cash", fileHash);
                    ResolveGeneratedWorkbookActionsForPath(folder, path, "Cash", "Automation");
                    return;
                }

                var status = result.WasSuccessful ? (result.ReviewCount > 0 ? "Needs Action" : "Imported") : "Needs Action";
                RecordHistory(folder, path, fileHash, "Cash", status, result.Detail, result.ImportedCount);
                if (result.ReviewCount > 0)
                    AddAction(folder, "Cash", "Review Imported Cash", Path.GetFileName(path), $"{result.Detail}\nSource: {path}", fileHash);
                if (result.WasSuccessful)
                {
                    if (result.ReviewCount == 0)
                    {
                        ResolveAutomationAction(folder, "Cash", fileHash);
                        if (generatedTemplate) ResolveGeneratedWorkbookActionsForPath(folder, path, "Cash", "Automation");
                    }
                    RaiseAccountingDataChanged(folder, "Cash", result.ImportedCount, path);
                    _financeRepository.RecordAudit(folder.ClientId, folder.FinancialYear, "Universal Dump", "Cash Import", Path.GetFileName(path), "Import", string.Empty, result.Detail);
                }
            }
            catch (Exception exception)
            {
                RecordHistory(folder, path, fileHash, "Cash", "Failed", exception.Message);
                AddAction(folder, "Cash", "Cash Import Failed", Path.GetFileName(path), $"{exception.Message}\nSource: {path}", fileHash);
            }
        }, cancellationToken);

    private Task ProcessUniversalJournalCachedAsync(AutomationFolderRegistration folder, string path, string fileHash, OpenXmlWorkbookData workbook, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                var job = _governanceRepository.FindJobByWorkingPath(folder.ClientId, folder.FinancialYear, path);
                var result = _universalCashJournalImporter.ImportJournal(folder.ClientId, folder.FinancialYear, workbook, path, job?.RunId ?? string.Empty, job?.JobId ?? string.Empty, cancellationToken);
                var generatedTemplate = GeneratedAccountingWorkbookIdentity.IsJournalWorkbook(workbook);
                if (!result.WasSuccessful && result.ImportedCount == 0 && result.ReviewCount == 0 && generatedTemplate)
                {
                    var detail = "AAH Journal format is correctly classified and is still blank; waiting for completed rows. A blank generated format is not Action Required.";
                    RecordHistory(folder, path, fileHash, "Journal Format", "Template Ready", detail);
                    ResolveAutomationAction(folder, "Journal", fileHash);
                    ResolveGeneratedWorkbookActionsForPath(folder, path, "Journal", "Automation");
                    return;
                }

                var status = result.WasSuccessful ? (result.ReviewCount > 0 ? "Needs Action" : "Imported") : "Needs Action";
                RecordHistory(folder, path, fileHash, "Journal", status, result.Detail, result.ImportedCount);
                if (result.ReviewCount > 0)
                    AddAction(folder, "Journal", "Review Imported Journal", Path.GetFileName(path), $"{result.Detail}\nSource: {path}", fileHash);
                if (result.WasSuccessful)
                {
                    if (result.ReviewCount == 0)
                    {
                        ResolveAutomationAction(folder, "Journal", fileHash);
                        if (generatedTemplate) ResolveGeneratedWorkbookActionsForPath(folder, path, "Journal", "Automation");
                    }
                    RaiseAccountingDataChanged(folder, "Journal", result.ImportedCount, path);
                    _financeRepository.RecordAudit(folder.ClientId, folder.FinancialYear, "Universal Dump", "Journal Import", Path.GetFileName(path), "Import", string.Empty, result.Detail);
                }
            }
            catch (Exception exception)
            {
                RecordHistory(folder, path, fileHash, "Journal", "Failed", exception.Message);
                AddAction(folder, "Journal", "Journal Import Failed", Path.GetFileName(path), $"{exception.Message}\nSource: {path}", fileHash);
            }
        }, cancellationToken);

    private Task ProcessUniversalCashAsync(AutomationFolderRegistration folder, string path, string fileHash, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                var job = _governanceRepository.FindJobByWorkingPath(folder.ClientId, folder.FinancialYear, path);
                var workbook = new FlexibleWorkbookReader().Read(path, cancellationToken);
                var result = _universalCashJournalImporter.ImportCash(folder.ClientId, folder.FinancialYear, workbook, path, job?.RunId ?? string.Empty, job?.JobId ?? string.Empty, cancellationToken);
                var generatedTemplate = GeneratedAccountingWorkbookIdentity.IsCashWorkbook(workbook);
                if (!result.WasSuccessful && result.ImportedCount == 0 && result.ReviewCount == 0 && generatedTemplate)
                {
                    var detail = "AAH Cash Entry format is correctly classified and is still blank; waiting for completed rows. A blank generated format is not Action Required.";
                    RecordHistory(folder, path, fileHash, "Cash Format", "Template Ready", detail);
                    ResolveAutomationAction(folder, "Cash", fileHash);
                    ResolveGeneratedWorkbookActionsForPath(folder, path, "Cash", "Automation");
                    return;
                }
                var status = result.WasSuccessful ? (result.ReviewCount > 0 ? "Needs Action" : "Imported") : "Needs Action";
                RecordHistory(folder, path, fileHash, "Cash", status, result.Detail, result.ImportedCount);
                if (result.ReviewCount > 0)
                    AddAction(folder, "Cash", "Review Imported Cash", Path.GetFileName(path), $"{result.Detail}\nSource: {path}", fileHash);
                if (result.WasSuccessful)
                {
                    if (result.ReviewCount == 0)
                    {
                        ResolveAutomationAction(folder, "Cash", fileHash);
                        if (generatedTemplate) ResolveGeneratedWorkbookActionsForPath(folder, path, "Cash", "Automation");
                    }
                    RaiseAccountingDataChanged(folder, "Cash", result.ImportedCount, path);
                    _financeRepository.RecordAudit(folder.ClientId, folder.FinancialYear, "Universal Dump", "Cash Import", Path.GetFileName(path), "Import", string.Empty, result.Detail);
                }
            }
            catch (Exception exception)
            {
                RecordHistory(folder, path, fileHash, "Cash", "Failed", exception.Message);
                AddAction(folder, "Cash", "Cash Import Failed", Path.GetFileName(path), $"{exception.Message}\nSource: {path}", fileHash);
            }
        }, cancellationToken);

    private Task ProcessUniversalJournalAsync(AutomationFolderRegistration folder, string path, string fileHash, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                var job = _governanceRepository.FindJobByWorkingPath(folder.ClientId, folder.FinancialYear, path);
                var workbook = new FlexibleWorkbookReader().Read(path, cancellationToken);
                var result = _universalCashJournalImporter.ImportJournal(folder.ClientId, folder.FinancialYear, workbook, path, job?.RunId ?? string.Empty, job?.JobId ?? string.Empty, cancellationToken);
                var generatedTemplate = GeneratedAccountingWorkbookIdentity.IsJournalWorkbook(workbook);
                if (!result.WasSuccessful && result.ImportedCount == 0 && result.ReviewCount == 0 && generatedTemplate)
                {
                    var detail = "AAH Journal format is correctly classified and is still blank; waiting for completed rows. A blank generated format is not Action Required.";
                    RecordHistory(folder, path, fileHash, "Journal Format", "Template Ready", detail);
                    ResolveAutomationAction(folder, "Journal", fileHash);
                    ResolveGeneratedWorkbookActionsForPath(folder, path, "Journal", "Automation");
                    return;
                }
                var status = result.WasSuccessful ? (result.ReviewCount > 0 ? "Needs Action" : "Imported") : "Needs Action";
                RecordHistory(folder, path, fileHash, "Journal", status, result.Detail, result.ImportedCount);
                if (result.ReviewCount > 0)
                    AddAction(folder, "Journal", "Review Imported Journal", Path.GetFileName(path), $"{result.Detail}\nSource: {path}", fileHash);
                if (result.WasSuccessful)
                {
                    if (result.ReviewCount == 0)
                    {
                        ResolveAutomationAction(folder, "Journal", fileHash);
                        if (generatedTemplate) ResolveGeneratedWorkbookActionsForPath(folder, path, "Journal", "Automation");
                    }
                    RaiseAccountingDataChanged(folder, "Journal", result.ImportedCount, path);
                    _financeRepository.RecordAudit(folder.ClientId, folder.FinancialYear, "Universal Dump", "Journal Import", Path.GetFileName(path), "Import", string.Empty, result.Detail);
                }
            }
            catch (Exception exception)
            {
                RecordHistory(folder, path, fileHash, "Journal", "Failed", exception.Message);
                AddAction(folder, "Journal", "Journal Import Failed", Path.GetFileName(path), $"{exception.Message}\nSource: {path}", fileHash);
            }
        }, cancellationToken);

    private bool IsDataFileMonitoringEnabled(AutomationFolderRegistration folder, string path, string fileHash, bool manualReimport = false)
    {
        if (manualReimport || _settings.GetBool(ApplicationSettingKeys.AutomationMonitorExcelCsv)) return true;
        RecordHistory(folder, path, fileHash, "Accounting Data", "Skipped", "Spreadsheet/text/data-file monitoring is disabled in Settings.");
        return false;
    }

    private void RecordRecognizedUnsupported(
        AutomationFolderRegistration folder,
        string path,
        string fileHash,
        string fileType,
        string reason,
        string message)
    {
        var detail = $"{message} {reason}".Trim();
        RecordHistory(folder, path, fileHash, fileType, "Needs Action", detail);
        AddAction(folder, "Automation", "Unsupported Format", Path.GetFileName(path), $"{detail}\nSource: {path}", fileHash);
        if (_settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
            Notify("Recognized source needs support", $"{folder.ClientName}: {Path.GetFileName(path)}", true);
    }

    private Task ProcessGstr3bPdfAsync(AutomationFolderRegistration folder, string pdfPath, string pdfHash, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = _clientRepository.GetById(folder.ClientId);
            if (client is null)
            {
                RecordHistory(folder, pdfPath, pdfHash, "GSTR-3B PDF", "Failed", "The linked client no longer exists.");
                return;
            }

            var result = _gstr3bPdfImportService.Import([pdfPath], folder.ClientId, folder.FinancialYear, client.Gstin);
            if (result.PeriodsImported > 0)
            {
                var detail = result.Warnings.Count > 0
                    ? $"Imported {result.PeriodsImported:N0} GSTR-3B period(s) with review warning(s): {string.Join(" | ", result.Warnings.Take(3))}"
                    : $"Imported and verified {result.PeriodsImported:N0} GSTR-3B period(s).";
                RecordHistory(folder, pdfPath, pdfHash, "GSTR-3B PDF", result.Warnings.Count > 0 ? "Needs Action" : "Imported", detail, result.PeriodsImported);
                if (result.Warnings.Count > 0)
                    AddAction(folder, "GST", "GSTR-3B Import Review", Path.GetFileName(pdfPath), detail, pdfHash);
                RaiseAccountingDataChanged(folder, "GST", result.PeriodsImported, pdfPath);
            }
            else
            {
                var detail = result.Warnings.Count > 0 ? string.Join(" | ", result.Warnings.Take(4)) : "No GSTR-3B return period could be imported.";
                RecordHistory(folder, pdfPath, pdfHash, "GSTR-3B PDF", "Needs Action", detail);
                AddAction(folder, "GST", "GSTR-3B Import Review", Path.GetFileName(pdfPath), detail, pdfHash);
            }
        }, cancellationToken);

    private async Task ProcessBankOcrSourceAsync(
        AutomationFolderRegistration folder,
        string sourcePath,
        string sourceHash,
        bool manualReimport,
        CancellationToken cancellationToken)
    {
        // Recognition is only the first stage. Raster photos are collected as one statement
        // job; after raw PaddleOCR completes, AAH itself groups/orders/verifies the pages.
        // Manual retry claims are cleared once per folder scan before the queue is populated.
        // Do not clear them here: several worker threads can enter this method concurrently.
        if (!BankOcrExcelConversionService.IsRasterImage(sourcePath))
        {
            var single = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(sourcePath)] = sourceHash
            };
            await ProcessBankOcrBatchCoreAsync(folder, single, cancellationToken);
            return;
        }

        var currentKey = BuildBankOcrBatchHandledKey(sourcePath, sourceHash);
        if (_bankOcrBatchHandled.ContainsKey(currentKey))
        {
            AutomationDiagnosticLog.Write(
                "BANK-OCR-BATCH-SKIP",
                "This raster page was already handled by the current Auto Folder OCR batch.",
                sourcePath);
            return;
        }

        await _bankOcrAutoBatchGate.WaitAsync(cancellationToken);
        try
        {
            // Another worker may have included this image while this worker was waiting.
            if (_bankOcrBatchHandled.ContainsKey(currentKey))
            {
                AutomationDiagnosticLog.Write(
                    "BANK-OCR-BATCH-SKIP",
                    "This raster page was already handled by another Auto Folder OCR worker.",
                    sourcePath);
                return;
            }

            // FileSystemWatcher emits one event per pasted photo. Give the burst a short
            // debounce window so the remaining photos reach the active/queued sets.
            await Task.Delay(TimeSpan.FromSeconds(2.5), cancellationToken);

            var batchSources = await CollectPendingBankOcrRasterSourcesAsync(
                folder,
                sourcePath,
                sourceHash,
                cancellationToken);

            if (batchSources.Count == 0)
                batchSources[Path.GetFullPath(sourcePath)] = sourceHash;

            // Claim the complete burst before OCR starts. Other workers that already
            // dequeued one of these pages will return without starting a second recognition run.
            foreach (var pair in batchSources)
                _bankOcrBatchHandled.TryAdd(BuildBankOcrBatchHandledKey(pair.Key, pair.Value), 0);

            AutomationDiagnosticLog.Write(
                "BANK-OCR-BATCH",
                $"Auto Folder grouped {batchSources.Count:N0} pending raster page(s) into one PaddleOCR-VL 1.6 production recognition run.",
                sourcePath);

            await ProcessBankOcrBatchCoreAsync(folder, batchSources, cancellationToken);
        }
        finally
        {
            _bankOcrAutoBatchGate.Release();
        }
    }

    private async Task<Dictionary<string, string>> CollectPendingBankOcrRasterSourcesAsync(
        AutomationFolderRegistration folder,
        string currentPath,
        string currentHash,
        CancellationToken cancellationToken)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { path = Path.GetFullPath(path); }
            catch { return; }
            if (!BankOcrExcelConversionService.IsRasterImage(path)) return;
            if (!File.Exists(path)) return;
            candidates.Add(path);
        }

        AddCandidate(currentPath);

        // Up to three Auto Import workers may already have dequeued pages from the same
        // paste operation, while the rest remain in the queue. Include both sets.
        foreach (var active in _activeFiles.Values)
        {
            if (active.FolderId.Equals(folder.FolderId, StringComparison.OrdinalIgnoreCase))
                AddCandidate(active.Path);
        }

        foreach (var queued in _queue.ToArray())
        {
            if (queued.FolderId.Equals(folder.FolderId, StringComparison.OrdinalIgnoreCase))
                AddCandidate(queued.Path);
        }

        var ordered = candidates
            .OrderBy(path => Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Check the copied files in parallel so six images do not incur six sequential
        // stable-file delays.
        var stability = await Task.WhenAll(
            ordered.Select(async path =>
            {
                if (PathsEqual(path, currentPath)) return (Path: path, Stable: true);
                return (Path: path, Stable: await WaitForStableFileAsync(path, cancellationToken));
            }));

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in stability.Where(item => item.Stable))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = PathsEqual(item.Path, currentPath) ? currentHash : SafeHash(item.Path);
            if (hash.Length == 0) continue;

            var handledKey = BuildBankOcrBatchHandledKey(item.Path, hash);
            if (_bankOcrBatchHandled.ContainsKey(handledKey)) continue;

            var processed = _automationRepository.GetProcessedFile(folder.FolderId, folder.ClientId, hash);
            if (processed is not null && BankStatementSourceStillExists(processed)) continue;

            result[item.Path] = hash;
        }

        return result;
    }

    private async Task ProcessBankOcrBatchCoreAsync(
        AutomationFolderRegistration folder,
        IReadOnlyDictionary<string, string> batchSources,
        CancellationToken cancellationToken)
    {
        if (batchSources.Count == 0) return;

        var orderedSources = batchSources.Keys
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (orderedSources.Length == 0) return;

        var ocr = new BankOcrExcelConversionService();
        BankOcrBatchConversionResult? batch = null;

        try
        {
            AutomationDiagnosticLog.Write(
                "BANK-OCR",
                orderedSources.Length == 1
                    ? "PaddleOCR-VL 1.6 production recognition started for one scanned source; AAH verification follows."
                    : $"PaddleOCR-VL 1.6 production recognition started once for an Auto Folder burst of {orderedSources.Length:N0} image page(s); AAH verification follows.",
                orderedSources[0]);

            batch = await ocr.ProcessBatchAsync(
                orderedSources,
                message =>
                {
                    var stage = message.StartsWith("Bank OCR", StringComparison.OrdinalIgnoreCase)
                        ? message
                        : $"Bank OCR • {message}";
                    SetAutomationStageForPaths(orderedSources, stage);
                    AutomationDiagnosticLog.Write("BANK-OCR-PROGRESS", message, orderedSources[0]);
                },
                cancellationToken);

            if (!batch.WasSuccessful || batch.Issue is not null)
            {
                var message = batch.Issue?.FriendlyMessage ??
                              "The scanned Bank Statement batch could not be converted to a verified Excel workbook.";

                foreach (var source in orderedSources)
                {
                    var hash = batchSources.TryGetValue(source, out var savedHash) ? savedHash : SafeHash(source);
                    RecordHistory(folder, source, hash, "Bank OCR", "Needs Action", message);
                }

                var primary = orderedSources[0];
                var primaryHash = batchSources.TryGetValue(primary, out var firstHash) ? firstHash : SafeHash(primary);
                AddAction(
                    folder,
                    "Banking",
                    orderedSources.Length == 1 ? "Bank OCR Review" : "Bank OCR Batch Review",
                    Path.GetFileName(primary),
                    $"{message}\\nBatch files: {orderedSources.Length:N0}\\nSource: {primary}\\nOCR Log: {batch.LogPath}",
                    primaryHash);

                if (_settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                    Notify("Bank OCR needs attention", $"{folder.ClientName}: {orderedSources.Length:N0} scanned page(s)", true);
                return;
            }

            var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var document in batch.Documents.OrderBy(item => item.DocumentNumber))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var documentSources = document.SourcePaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .Where(path => batchSources.ContainsKey(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (documentSources.Length == 0)
                    documentSources = [document.PrimarySourcePath];

                foreach (var source in documentSources)
                    assigned.Add(source);

                var verified =
                    document.VerificationStatus.Equals("VERIFIED", StringComparison.OrdinalIgnoreCase) ||
                    document.VerificationStatus.Equals("VERIFIED_WITH_CORRECTIONS", StringComparison.OrdinalIgnoreCase);

                var primary = documentSources.FirstOrDefault(File.Exists) ??
                              document.PrimarySourcePath;
                primary = Path.GetFullPath(primary);

                var primaryHash = batchSources.TryGetValue(primary, out var savedPrimaryHash)
                    ? savedPrimaryHash
                    : SafeHash(primary);

                if (!verified)
                {
                    var reviewMessage =
                        $"AAH grouped the raw OCR page(s), but document verification is '{document.VerificationStatus}'. " +
                        $"Page status: {document.PageStatus}; summary status: {document.SummaryStatus}. " +
                        "The document was not passed into accounting automatically.";

                    foreach (var source in documentSources)
                    {
                        var hash = batchSources.TryGetValue(source, out var sourceHash) ? sourceHash : SafeHash(source);
                        RecordHistory(folder, source, hash, "Bank OCR", "Needs Action", reviewMessage);
                    }

                    AddAction(
                        folder,
                        "Banking",
                        "Bank OCR Document Review",
                        Path.GetFileName(primary),
                        $"{reviewMessage}\nOCR pages in this document: {document.PageCount:N0}\nSource files: {documentSources.Length:N0}\nTransaction rows detected: {document.TransactionRowsPrepared:N0}\nVerification issues: {document.VerificationIssueCount:N0}\nSource: {primary}\nVerification audit: {document.VerificationAuditPath}\nOCR Log: {batch.LogPath}",
                        primaryHash);

                    continue;
                }

                var workbookStatus = await ProcessBankWorkbookAsync(
                    folder,
                    document.GeneratedWorkbookPath,
                    primary,
                    documentSources.Length == 1
                        ? Path.GetFileName(primary)
                        : $"{Path.GetFileName(primary)} + {documentSources.Length - 1:N0} page(s)",
                    primaryHash,
                    cancellationToken);

                // Earlier OCR builds could import isolated page subsets before a later
                // complete grouped document. Now that a verified grouped statement exists,
                // remove only exact raster-source statement subsets and preserve any
                // non-conflicting mapping decision on the canonical transaction.
                RepairSupersededOcrPartialStatements(folder, raiseDataChanged: true);

                // ProcessBankWorkbookAsync is the Bank accounting authority and now
                // returns its terminal source status directly. Mirror that exact result
                // to every additional image page in the verified document. Do not infer
                // success from a later history query: that race left grouped pages as
                // Needs Action even when the statement itself imported successfully.
                var groupedImported =
                    workbookStatus.Equals("Imported", StringComparison.OrdinalIgnoreCase) ||
                    workbookStatus.Equals("Duplicate", StringComparison.OrdinalIgnoreCase);

                foreach (var source in documentSources)
                {
                    if (PathsEqual(source, primary)) continue;
                    var hash = batchSources.TryGetValue(source, out var sourceHash) ? sourceHash : SafeHash(source);

                    if (groupedImported)
                    {
                        RecordHistory(
                            folder,
                            source,
                            hash,
                            "Bank OCR Page",
                            workbookStatus,
                            $"OCR_GROUP_SOURCE={primary}\nGrouped into verified AAH OCR document {document.DocumentNumber:N0}; the grouped Bank Statement finished with status {workbookStatus}.");
                        ResolveAutomationAction(folder, "Banking", hash);
                    }
                    else
                    {
                        var detail =
                            $"This page was grouped into AAH OCR document {document.DocumentNumber:N0}, but the normal Bank Statement importer did not complete the grouped statement. Review the primary source/action: {primary}";
                        RecordHistory(folder, source, hash, "Bank OCR Page", "Needs Action", detail);
                    }
                }
            }

            // If recognition/AAH verification left a page outside every document,
            // surface that fact instead of silently losing the page.
            foreach (var source in orderedSources.Where(path => !assigned.Contains(path)))
            {
                var hash = batchSources.TryGetValue(source, out var sourceHash) ? sourceHash : SafeHash(source);
                const string message = "AAH document verification did not assign this scanned page to a completed statement document. The page was not imported.";
                RecordHistory(folder, source, hash, "Bank OCR", "Needs Action", message);
                AddAction(
                    folder,
                    "Banking",
                    "Unassigned Bank OCR Page",
                    Path.GetFileName(source),
                    $"{message}\\nSource: {source}\\nOCR Log: {batch.LogPath}",
                    hash);
            }

            if (batch.SourceErrorCount > 0 && _settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                Notify("Bank OCR source review", $"{folder.ClientName}: {batch.SourceErrorCount:N0} source page(s) need attention.", true);
        }
        catch (Exception exception)
        {
            AutomationDiagnosticLog.Write("BANK-OCR-ERROR", exception.ToString(), orderedSources[0]);

            foreach (var source in orderedSources)
            {
                var hash = batchSources.TryGetValue(source, out var sourceHash) ? sourceHash : SafeHash(source);
                RecordHistory(folder, source, hash, "Bank OCR", "Needs Action", exception.Message);
            }

            var primary = orderedSources[0];
            var primaryHash = batchSources.TryGetValue(primary, out var firstHash) ? firstHash : SafeHash(primary);
            AddAction(
                folder,
                "Banking",
                "Bank OCR Batch Failed",
                Path.GetFileName(primary),
                $"{exception.Message}\\nBatch files: {orderedSources.Length:N0}\\nSource: {primary}\\nOCR Log: {batch?.LogPath ?? string.Empty}",
                primaryHash);

            if (_settings.GetBool(ApplicationSettingKeys.NotifyImportFailed))
                Notify("Bank OCR batch failed", $"{folder.ClientName}: {exception.Message}", true);
        }
        finally
        {
            if (batch is not null)
            {
                try { await ocr.CleanupAsync(batch); }
                catch { /* OCR cleanup never changes accounting persistence. */ }
            }
        }
    }

    private int RepairSupersededOcrPartialStatements(
        AutomationFolderRegistration folder,
        bool raiseDataChanged)
    {
        try
        {
            var statements = _bankingRepository.GetStatements(folder.ClientId, folder.FinancialYear).ToList();
            if (statements.Count < 2) return 0;

            var groupedStatements = statements
                .Where(IsGroupedOcrStatement)
                .OrderByDescending(statement => statement.TransactionCount)
                .ThenByDescending(statement => statement.ImportedUtc)
                .ThenByDescending(statement => statement.Id)
                .ToList();
            if (groupedStatements.Count == 0) return 0;

            var accounts = _bankingRepository.GetAccounts(folder.ClientId, folder.FinancialYear)
                .ToDictionary(account => account.Id);
            var transactionsByStatement = _bankingRepository
                .GetTransactions(folder.ClientId, folder.FinancialYear, includeArchived: true)
                .GroupBy(transaction => transaction.StatementId)
                .ToDictionary(group => group.Key, group => group.ToList());

            var deleted = new HashSet<long>();
            var repaired = 0;

            foreach (var canonical in groupedStatements)
            {
                if (deleted.Contains(canonical.Id)) continue;
                if (!transactionsByStatement.TryGetValue(canonical.Id, out var canonicalRows) || canonicalRows.Count == 0)
                    continue;

                var canonicalByKey = canonicalRows
                    .GroupBy(OcrExactTransactionKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                accounts.TryGetValue(canonical.BankAccountId, out var canonicalAccount);

                foreach (var candidate in statements)
                {
                    if (candidate.Id == canonical.Id || deleted.Contains(candidate.Id)) continue;
                    if (!BankOcrExcelConversionService.IsRasterImage(candidate.SourcePath)) continue;
                    if (!PeriodIsInside(candidate, canonical)) continue;
                    if (!transactionsByStatement.TryGetValue(candidate.Id, out var candidateRows) || candidateRows.Count == 0) continue;
                    if (candidateRows.Count > canonicalRows.Count) continue;

                    accounts.TryGetValue(candidate.BankAccountId, out var candidateAccount);
                    if (!SameBankFamily(candidateAccount?.BankName, canonicalAccount?.BankName)) continue;

                    var matches = new List<(BankTransactionRecord Candidate, BankTransactionRecord Canonical)>();
                    var completeSubset = true;
                    foreach (var candidateRow in candidateRows)
                    {
                        if (!canonicalByKey.TryGetValue(OcrExactTransactionKey(candidateRow), out var canonicalRow))
                        {
                            completeSubset = false;
                            break;
                        }
                        matches.Add((candidateRow, canonicalRow));
                    }
                    if (!completeSubset) continue;

                    var conflictingLedger = matches.Any(pair =>
                        pair.Candidate.LedgerName.Trim().Length > 0 &&
                        pair.Canonical.LedgerName.Trim().Length > 0 &&
                        !pair.Candidate.LedgerName.Trim().Equals(pair.Canonical.LedgerName.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (conflictingLedger)
                    {
                        AutomationDiagnosticLog.Write(
                            "BANK-OCR-DEDUP-REVIEW",
                            $"Exact OCR duplicate statement {candidate.Id} was retained because saved Ledger decisions conflict with canonical statement {canonical.Id}.",
                            candidate.SourcePath);
                        continue;
                    }

                    foreach (var pair in matches)
                    {
                        if (pair.Canonical.LedgerName.Trim().Length == 0 && pair.Candidate.LedgerName.Trim().Length > 0)
                        {
                            pair.Canonical.LedgerName = pair.Candidate.LedgerName;
                            pair.Canonical.MappingStatus = pair.Candidate.MappingStatus;
                            _bankingRepository.SaveTransaction(pair.Canonical);
                        }
                    }

                    _bankingRepository.DeleteStatement(candidate.Id);
                    deleted.Add(candidate.Id);
                    repaired++;
                    AutomationDiagnosticLog.Write(
                        "BANK-OCR-DEDUP",
                        $"Removed superseded OCR partial/duplicate statement {candidate.Id}; {candidateRows.Count:N0} exact transaction(s) are already present in verified grouped statement {canonical.Id}.",
                        candidate.SourcePath);
                }
            }

            if (repaired > 0)
            {
                new BankReconciliationService(_bankingRepository).Reconcile(folder.ClientId, folder.FinancialYear);
                new BankTransferReconciliationService(_bankingRepository, _masterRepository).Reconcile(folder.ClientId, folder.FinancialYear);
                new BankLedgerMappingService(_bankingRepository, _masterRepository).Rebuild(folder.ClientId, folder.FinancialYear);

                if (raiseDataChanged)
                    RaiseAccountingDataChanged(folder, "Banking", 0, folder.FolderPath);
            }

            return repaired;
        }
        catch (Exception exception)
        {
            AutomationDiagnosticLog.Write(
                "BANK-OCR-DEDUP-ERROR",
                $"Exact OCR duplicate repair was skipped safely: {exception}",
                folder.FolderPath);
            return 0;
        }
    }

    private static bool IsGroupedOcrStatement(BankStatementRecord statement) =>
        BankOcrExcelConversionService.IsRasterImage(statement.SourcePath) &&
        statement.SourceFile.Contains("+", StringComparison.Ordinal) &&
        statement.SourceFile.Contains("page(s)", StringComparison.OrdinalIgnoreCase);

    private static bool PeriodIsInside(BankStatementRecord candidate, BankStatementRecord canonical)
    {
        if (canonical.PeriodFrom.HasValue && candidate.PeriodFrom.HasValue &&
            candidate.PeriodFrom.Value < canonical.PeriodFrom.Value) return false;
        if (canonical.PeriodTo.HasValue && candidate.PeriodTo.HasValue &&
            candidate.PeriodTo.Value > canonical.PeriodTo.Value) return false;
        return true;
    }

    private static bool SameBankFamily(string? left, string? right)
    {
        var a = (left ?? string.Empty).Trim();
        var b = (right ?? string.Empty).Trim();
        return a.Length > 0 && b.Length > 0 && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    private static string OcrExactTransactionKey(BankTransactionRecord transaction) =>
        string.Join("|",
            transaction.TransactionDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
            transaction.Narration.Trim().ToUpperInvariant(),
            transaction.Debit.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            transaction.Credit.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            transaction.RunningBalance?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);

    private void ClearBankOcrClaimsForFolder(string folderPath)
    {
        string root;
        try
        {
            root = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        catch
        {
            root = folderPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        }

        foreach (var key in _bankOcrBatchHandled.Keys.ToArray())
        {
            var separator = key.LastIndexOf('|');
            var path = separator > 0 ? key[..separator] : key;
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                _bankOcrBatchHandled.TryRemove(key, out _);
        }
    }

    private static string BuildBankOcrBatchHandledKey(string path, string hash)
    {
        string normalized;
        try { normalized = Path.GetFullPath(path); }
        catch { normalized = path; }
        return $"{normalized}|{hash}";
    }

    private async Task ProcessForm26AsPdfAsync(
        AutomationFolderRegistration folder,
        string pdfPath,
        string pdfHash,
        CancellationToken cancellationToken)
    {
        var client = _clientRepository.GetById(folder.ClientId);
        if (client is null || client.IsArchived)
        {
            RecordHistory(folder, pdfPath, pdfHash, "26AS PDF", "Needs Action", "The linked Client is unavailable or archived.");
            AddAction(folder, "26AS", "26AS Client Review", Path.GetFileName(pdfPath), "The linked Client is unavailable or archived.", pdfHash);
            return;
        }

        var expectedPan = string.IsNullOrWhiteSpace(client.Pan) && !string.IsNullOrWhiteSpace(client.Gstin) && client.Gstin.Trim().Length == 15
            ? client.Gstin.Trim().Substring(2, 10)
            : client.Pan;
        var result = await Task.Run(
            () => _form26AsImportService.Import(pdfPath, folder.ClientId, folder.FinancialYear, expectedPan),
            cancellationToken);

        if (result.Imported)
        {
            var detail = result.Warnings.Count == 0
                ? result.Message
                : $"{result.Message} Review warning(s): {string.Join(" | ", result.Warnings.Take(3))}";
            RecordHistory(folder, pdfPath, pdfHash, "26AS PDF", result.Warnings.Count == 0 ? "Imported" : "Needs Action", detail, result.Rows.Count);
            if (result.Warnings.Count == 0) ResolveAutomationAction(folder, "26AS", pdfHash);
            else AddAction(folder, "26AS", "26AS Import Review", Path.GetFileName(pdfPath), detail, pdfHash);
            PublishCommittedAccountingImport(folder.ClientId, folder.FinancialYear, "26AS", result.Rows.Count, pdfPath, "Auto Import");
            return;
        }

        if (result.Duplicate)
        {
            RecordHistory(folder, pdfPath, pdfHash, "26AS PDF", "Duplicate", result.Message, result.Rows.Count);
            ResolveAutomationAction(folder, "26AS", pdfHash);
            return;
        }

        RecordHistory(folder, pdfPath, pdfHash, "26AS PDF", "Needs Action", result.Message);
        AddAction(folder, "26AS", "26AS Import Review", Path.GetFileName(pdfPath), result.Message, pdfHash);
        if (_settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
            Notify("26AS needs attention", $"{folder.ClientName}: {Path.GetFileName(pdfPath)}", true);
    }

    private async Task ProcessPdfAsync(
        AutomationFolderRegistration folder,
        string pdfPath,
        string pdfHash,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? passwordCandidates = null)
    {
        // All automatic and distributed Bank PDF paths share this final routing
        // decision, even if a caller incorrectly labels a scan as BankPdf.
        // A scanned or mixed statement must not start the normal PDF Python
        // process or touch any existing source-side generated XLSX.
        var sourceKind = BankPdfDocumentClassifier.Classify(pdfPath);
        if (sourceKind is BankPdfDocumentKind.Scanned or BankPdfDocumentKind.Mixed)
        {
            AutomationDiagnosticLog.Write("PDF-SCANNED-OCR-ROUTE",
                $"Physical PDF preflight detected {sourceKind}; using existing PaddleOCR-VL Bank scan route instead of digital Python.", pdfPath);
            await ProcessBankOcrSourceAsync(folder, pdfPath, pdfHash, manualReimport: false, cancellationToken);
            return;
        }

        var expectedWorkbook = Path.ChangeExtension(pdfPath, ".xlsx");
        _generatedWorkbookPaths.TryAdd(Path.GetFullPath(expectedWorkbook), 0);
        var pipeline = new PdfBankStatementPipelineService(_settings);
        PdfPipelineFileResult? pipelineResult = null;
        try
        {
            // Pass 1 is headless so already-learned PDFs remain fully automatic and
            // multiple extraction workers can still run in parallel.
            pipelineResult = await pipeline.ProcessAsync(pdfPath, cancellationToken, automationMode: true, passwordCandidates: passwordCandidates);

            // If the only blocker is a one-time Python decision (mapping/password/line
            // confirmation), native Python UI is allowed only on the Main Server/Admin
            // system or on a Windows node granted Import.NativePythonMappingUi. Other
            // nodes stay paused for AAH/Android presentation instead of opening Python UI.
            var nativePythonUiAllowed = !NetworkClientRuntime.IsNetworkClient ||
                                        NetworkClientRuntime.HasPermission(GovernancePermissions.NativePythonMappingUi);
            if ((!pipelineResult.WasSuccessful || pipelineResult.Issue is not null) &&
                (pipelineResult.Issue?.ErrorCode is BankImportErrorCode.P004_PdfPasswordFailure or BankImportErrorCode.P008_MappingWizardFailure) &&
                nativePythonUiAllowed)
            {
                await _pdfInteractiveGate.WaitAsync(cancellationToken);
                try
                {
                    AutomationDiagnosticLog.Write(
                        "PDF-INTERACTIVE-FALLBACK",
                        "Headless import needs one-time Python input and this Windows node is authorised for the native Python UI. Rerunning the live external pipeline interactively.",
                        pdfPath);
                    pipelineResult = await pipeline.ProcessAsync(pdfPath, cancellationToken, automationMode: false, passwordCandidates: passwordCandidates);
                }
                finally
                {
                    _pdfInteractiveGate.Release();
                }
            }
            else if ((!pipelineResult.WasSuccessful || pipelineResult.Issue is not null) &&
                     (pipelineResult.Issue?.ErrorCode is BankImportErrorCode.P004_PdfPasswordFailure or BankImportErrorCode.P008_MappingWizardFailure) &&
                     !nativePythonUiAllowed)
            {
                AutomationDiagnosticLog.Write(
                    "PDF-NATIVE-UI-BLOCKED",
                    "Python mapping/password UI is not permitted on this Windows node. The import remains paused for AAH/Android mapping-session presentation or Administrator handling.",
                    pdfPath);
            }

            if (!pipelineResult.WasSuccessful || pipelineResult.Issue is not null)
            {
                var message = pipelineResult.Issue?.FriendlyMessage ?? "The PDF pipeline did not produce an importable workbook.";
                var actionType = pipelineResult.Issue?.ErrorCode switch
                {
                    BankImportErrorCode.P004_PdfPasswordFailure => "Password Required",
                    BankImportErrorCode.P008_MappingWizardFailure => "Unknown Format",
                    _ => "PDF Import Review"
                };
                RecordHistory(folder, pdfPath, pdfHash, "Bank PDF", "Needs Action", message);

                // R32: once a PDF has been routed into Banking, Banking owns the one
                // actionable row for this source hash. Resolve a stale generic Automation
                // Unknown Format row first so a password/header failure is not shown twice.
                ResolveAutomationAction(folder, "Banking", pdfHash);
                AddAction(folder, "Banking", actionType, Path.GetFileName(pdfPath), $"{message}\nSource: {pdfPath}\nLog: {pipelineResult.LogPath}", pdfHash);
                if (_settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                    Notify("PDF Bank Statement needs attention", $"{folder.ClientName}: {Path.GetFileName(pdfPath)}", true);
                return;
            }

            await ProcessBankWorkbookAsync(
                folder,
                pipelineResult.GeneratedWorkbookPath,
                pdfPath,
                Path.GetFileName(pdfPath),
                pdfHash,
                cancellationToken);
        }
        finally
        {
            // Keep this path suppressed for the lifetime of the process; generated
            // PDF workbooks must never be picked up again by FileSystemWatcher.
            _generatedWorkbookPaths.TryAdd(Path.GetFullPath(expectedWorkbook), 0);
            if (pipelineResult is not null && pipelineResult.WasSuccessful)
            {
                try { await pipeline.CleanupAsync(pipelineResult); }
                catch { /* cleanup never changes the accounting result */ }
            }
        }
    }

    private async Task<string> ProcessBankWorkbookAsync(
        AutomationFolderRegistration folder,
        string workbookPath,
        string identityPath,
        string displayName,
        string fileHash,
        CancellationToken cancellationToken,
        string? initialPassword = null,
        IReadOnlyList<string>? suppliedPasswordCandidates = null,
        BankStatementImportService.BankStatementSourceCache? sourceCache = null,
        bool bankGateAlreadyHeld = false)
    {
        // Normal/manual/PDF paths still use the global compatibility gate. BR4's cached
        // Bank Auto Import path already holds a bank-account-specific gate, allowing
        // different bank accounts to process independently after source extraction.
        if (!bankGateAlreadyHeld)
            await _bankImportGate.WaitAsync(cancellationToken);
        try
        {
            var importService = new BankStatementImportService(_bankingRepository, _masterRepository);
            var fallback = SelectTrustedBankAccountFallback(folder, identityPath);
            var linkedClientForPreImport = _clientRepository.GetById(folder.ClientId)?.Clone();
            if (linkedClientForPreImport is not null)
                linkedClientForPreImport.FinancialYear = folder.FinancialYear;
            // Auto Import must never wait for a disconnected Tally host. Use the coordinator's
            // already-known live state to decide whether hidden pre-import reconciliation is even
            // enabled. A false/offline state imports immediately; the later connected edge performs
            // the one-time catch-up from the durable Bank/Tally checkpoint.
            var useTallyPreImport = _tallyBankStatementReconciliationService is not null &&
                linkedClientForPreImport is not null && IsTallyCurrentlyConnected();

            BankStatementImportRequest BuildRequest(
                IReadOnlyDictionary<string, string>? passwords = null,
                bool recoveryMode = false,
                int attempt = 1) => new()
            {
                ClientId = folder.ClientId,
                FinancialYear = folder.FinancialYear,
                FilePaths = [workbookPath],
                ClearExisting = false,
                StrictMetadata = true,
                AllowSourceDelta = sourceCache is null,
                Passwords = passwords ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Attempt = attempt,
                RecoveryMode = recoveryMode,
                SourceDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [workbookPath] = displayName },
                SourceIdentityPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [workbookPath] = identityPath },
                MetadataFallbackBankNames = fallback is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [workbookPath] = fallback.BankName },
                MetadataFallbackAccountNumbers = fallback is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [workbookPath] = fallback.AccountNumber },
                MetadataFallbackAccountTypes = fallback is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [workbookPath] = fallback.AccountType },
                MetadataFallbackBranchNames = fallback is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [workbookPath] = fallback.BranchName },
                MetadataFallbackIfscCodes = fallback is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [workbookPath] = fallback.IfscCode },
                PreImportReconciliationAsync = !useTallyPreImport
                    ? null
                    : ct => _tallyBankStatementReconciliationService!.ReconcileBeforeImportAsync(linkedClientForPreImport!, ct),
                PostImportCheckpointAsync = !useTallyPreImport
                    ? null
                    : ct => _tallyBankStatementReconciliationService!.CheckpointAfterBankImportAsync(linkedClientForPreImport!, ct)
            };

            var explicitCandidates = (suppliedPasswordCandidates ?? Array.Empty<string>())
                .Select(candidate => (candidate ?? string.Empty).Trim())
                .Where(candidate => candidate.Length is > 0 and <= 64)
                .Distinct(StringComparer.Ordinal)
                .Take(12)
                .ToList();
            if (!string.IsNullOrWhiteSpace(initialPassword) && !explicitCandidates.Contains(initialPassword, StringComparer.Ordinal))
                explicitCandidates.Insert(0, initialPassword);

            IReadOnlyDictionary<string, string>? CandidatePassword(string? candidate) => string.IsNullOrWhiteSpace(candidate)
                ? null
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [workbookPath] = candidate };

            var result = sourceCache is null
                ? await importService.ImportAsync(BuildRequest(CandidatePassword(explicitCandidates.FirstOrDefault())), cancellationToken)
                : await importService.ImportCachedAsync(BuildRequest(CandidatePassword(sourceCache.ResolvedPassword)), [sourceCache], cancellationToken);

            // Android shared-text/file-name candidates are tried before any prompt.
            // The normal workbook importer still performs its own local filename/small-file
            // discovery for every attempt, so this augments rather than replaces it.
            if (sourceCache is null && result.FilesImported == 0 &&
                result.Issues.Any(issue => issue.ErrorCode == BankImportErrorCode.E002_PasswordFailure))
            {
                foreach (var candidate in explicitCandidates.Skip(1))
                {
                    result = await importService.ImportAsync(BuildRequest(CandidatePassword(candidate), attempt: 2), cancellationToken);
                    if (result.FilesImported > 0 || !result.Issues.Any(issue => issue.ErrorCode == BankImportErrorCode.E002_PasswordFailure))
                        break;
                }
            }

            // Match the PDF workflow: after explicit/shared candidates and local discovery,
            // perform the Excel repair/extract-data recovery pass before manual input.
            if (sourceCache is null && result.FilesImported == 0 &&
                result.Issues.Any(issue => issue.ErrorCode == BankImportErrorCode.E002_PasswordFailure))
            {
                var recoveryPassword = explicitCandidates.LastOrDefault();
                result = await importService.ImportAsync(
                    BuildRequest(CandidatePassword(recoveryPassword), recoveryMode: true, attempt: 3),
                    cancellationToken);
            }

            // Manual password entry is the final fallback only after both the normal and
            // recovery passes have exhausted automatic local password discovery.
            if (sourceCache is null && result.FilesImported == 0 &&
                result.Issues.Any(issue => issue.ErrorCode == BankImportErrorCode.E002_PasswordFailure))
            {
                await _spreadsheetPasswordGate.WaitAsync(cancellationToken);
                try
                {
                    string? manualPassword = null;
                    var app = System.Windows.Application.Current;
                    if (app is not null)
                        manualPassword = await app.Dispatcher.InvokeAsync(() => PasswordPromptService.Prompt(displayName));

                    if (manualPassword is not null)
                    {
                        var passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [workbookPath] = manualPassword
                        };
                        result = await importService.ImportAsync(
                            BuildRequest(passwords, recoveryMode: true, attempt: 4),
                            cancellationToken);
                    }
                }
                finally
                {
                    _spreadsheetPasswordGate.Release();
                }
            }

            if (result.FilesImported > 0)
            {
                try
                {
                    // BankStatementImportService already completed transaction/file/inter-file/
                    // account balance control for the committed batch. Do not immediately run
                    // the same whole-bank reconciliation a second time in the Auto Import shell.

                    // Completion 36: linked Tally has already been offered the hidden
                    // pre-import staging rows by BankStatementImportService. Do not replay the
                    // same Tally workflow after commit. The remaining Active Bank Statement rows
                    // still need the ordinary imported-to-imported Inter-Bank pass whether Tally
                    // was online, offline or unlinked at intake time.
                    new BankTransferReconciliationService(_bankingRepository, _masterRepository)
                        .Reconcile(folder.ClientId, folder.FinancialYear);
                    if (result.TransactionsMatchedBeforeImport > 0)
                    {
                        RecordHistory(folder, identityPath, fileHash + ":tally-bank", "Tally Bank Pre-Import", "Processed",
                            $"{result.TransactionsMatchedBeforeImport:N0} staged Bank Statement row(s) were resolved against linked Tally before active import.");
                    }

                    var ruleCount = _financeRepository.ApplyBankRules(folder.ClientId, folder.FinancialYear, onlyUnmapped: true);
                    new BankLedgerMappingService(_bankingRepository, _masterRepository).Rebuild(folder.ClientId, folder.FinancialYear);
                    if (ruleCount > 0)
                        RecordHistory(folder, identityPath, fileHash + ":rules", "Bank Rules", "Applied", $"{ruleCount:N0} transaction(s) mapped by active Bank Rules before Ledger Mapping review.");
                }
                catch (Exception exception)
                {
                    AddAction(folder, "Banking", "Post Import Review", displayName, $"{exception.Message}\nSource: {identityPath}", fileHash + ":post");
                }

            // Ledger Mapping/Suspense are normal post-import accounting review.
            // A successful Bank import must not create a global Action Required item.
            RecordHistory(folder, identityPath, fileHash, "Bank", "Imported", result.Summary, result.TransactionsImported);
            ResolveAutomationAction(folder, "Banking", fileHash);
            _activityRepository.Record(new ImportActivity
            {
                ClientId = folder.ClientId,
                ImportType = "Bank",
                PeriodFrom = result.PeriodFrom,
                PeriodTo = result.PeriodTo,
                RecordCount = result.TransactionsImported,
                BankCount = _bankingRepository.GetAccounts(folder.ClientId, folder.FinancialYear).Count,
                SourceSummary = displayName,
                Status = result.Issues.Count == 0 ? "Auto Imported" : "Review Required",
                Details = result.Summary,
                ImportedUtc = DateTime.UtcNow
            });
            // Publish after ImportActivity is committed so a visible Dashboard can
            // refresh the cards/recent activity and the new transactions atomically.
            RaiseAccountingDataChanged(folder, "Banking", result.TransactionsImported, identityPath);
            if (_settings.GetBool(ApplicationSettingKeys.NotifyImportComplete))
                Notify("Bank Statement auto-imported", $"{folder.ClientName}: {displayName} • {result.TransactionsImported:N0} transaction(s)", false);
            return "Imported";
        }

        if (result.DuplicateStatementsSkipped > 0)
        {
            RecordHistory(folder, identityPath, fileHash, "Bank", "Duplicate", "The statement was already imported for this client/FY.");
            return "Duplicate";
        }

        var issueText = result.Issues.Count == 0
            ? "The workbook could not be classified/imported safely as a Bank Statement."
            : string.Join(" | ", result.Issues.Select(issue => $"{issue.ErrorCodeText}: {issue.FriendlyMessage}"));
        RecordHistory(folder, identityPath, fileHash, "Bank", "Needs Action", issueText);
        AddAction(folder, "Banking", "Bank Import Review", displayName, $"{issueText}\nSource: {identityPath}", fileHash);
        if (_settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
            Notify("Bank Statement needs review", $"{folder.ClientName}: {displayName}", true);
        return "Needs Action";
        }
        finally
        {
            if (!bankGateAlreadyHeld)
                _bankImportGate.Release();
        }
    }

    private async Task ProcessExchangeStatementAsync(
        AutomationFolderRegistration folder,
        string sourcePath,
        string fileHash,
        string formatName,
        Func<string, BankExchangeConversionResult> converter,
        CancellationToken cancellationToken)
    {
        BankExchangeConversionResult? conversion = null;
        try
        {
            conversion = converter(sourcePath);
            if (conversion.TransactionCount <= 0)
            {
                var message = $"{formatName} was recognized but contained no importable transactions.";
                RecordHistory(folder, sourcePath, fileHash, formatName, "Needs Action", message);
                AddAction(folder, "Banking", "Bank Import Review", Path.GetFileName(sourcePath), $"{message}\nSource: {sourcePath}", fileHash);
                return;
            }
            await ProcessBankWorkbookAsync(folder, conversion.CsvPath, sourcePath, Path.GetFileName(sourcePath), fileHash, cancellationToken);
        }
        catch (Exception exception)
        {
            RecordHistory(folder, sourcePath, fileHash, formatName, "Needs Action", exception.Message);
            AddAction(folder, "Banking", "Bank Import Review", Path.GetFileName(sourcePath), $"{formatName} conversion failed: {exception.Message}\nSource: {sourcePath}", fileHash);
        }
        finally
        {
            if (conversion is not null)
            {
                try { if (File.Exists(conversion.CsvPath)) File.Delete(conversion.CsvPath); } catch { }
            }
        }
    }

    private async Task ProcessMt940Async(
        AutomationFolderRegistration folder,
        string sourcePath,
        string fileHash,
        CancellationToken cancellationToken)
    {
        Mt940ConversionResult? conversion = null;
        try
        {
            conversion = Mt940StatementConverter.Convert(sourcePath);
            if (conversion.TransactionCount <= 0)
            {
                RecordHistory(folder, sourcePath, fileHash, "MT940", "Needs Action", "MT940 was recognized but contained no importable :61: transaction records.");
                AddAction(folder, "Banking", "Bank Import Review", Path.GetFileName(sourcePath), $"MT940 was recognized but contained no importable transactions.\nSource: {sourcePath}", fileHash);
                return;
            }
            await ProcessBankWorkbookAsync(
                folder,
                conversion.CsvPath,
                sourcePath,
                Path.GetFileName(sourcePath),
                fileHash,
                cancellationToken);
        }
        catch (Exception exception)
        {
            RecordHistory(folder, sourcePath, fileHash, "MT940", "Needs Action", exception.Message);
            AddAction(folder, "Banking", "Bank Import Review", Path.GetFileName(sourcePath), $"MT940 conversion failed: {exception.Message}\nSource: {sourcePath}", fileHash);
        }
        finally
        {
            if (conversion is not null)
            {
                try { if (File.Exists(conversion.CsvPath)) File.Delete(conversion.CsvPath); } catch { }
            }
        }
    }

    private async Task ProcessSalesCachedAsync(
        AutomationFolderRegistration folder,
        string path,
        string fileHash,
        OpenXmlWorkbookData workbook,
        CancellationToken cancellationToken)
    {
        var service = new SalesManualImportService();
        var first = service.Inspect(workbook, path);
        var saved = _settings.GetBool(ApplicationSettingKeys.SalesUseSavedMappings)
            ? _salesMappingRepository.Get(folder.ClientId, folder.FinancialYear, first.HeaderSignature)
            : null;
        var inspection = service.Inspect(workbook, path, saved);
        var client = _clientRepository.GetById(folder.ClientId)
            ?? throw new InvalidOperationException("The client linked to this Automation Folder no longer exists.");

        // R9 FIXED16 R13: a printed Crystal/Tally Sales Register is companion GST-rate
        // evidence for Tally Voucher XML, not a second Sales Register. Apply it directly
        // by date + printed voucher range and never route it through Header Mapping or
        // SaveManualImport, which would either block the file or create duplicate vouchers.
        if (inspection.IsTallyRateSummaryMapping)
        {
            var rateResult = await Task.Run(() => service.Import(
                workbook,
                path,
                folder.ClientId,
                client.Gstin,
                client.ClientName,
                folder.FinancialYear,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                "LOCAL",
                "SINGLE",
                fileHash), cancellationToken).ConfigureAwait(false);
            ApplyTallySalesRegisterRateSummary(folder, path, fileHash, rateResult);
            return;
        }

        if (!inspection.IsComplete)
        {
            RecordHistory(folder, path, fileHash, "Sales", "Needs Action", $"Sales header mapping is required. Signature: {inspection.HeaderSignature}");
            var newAction = AddAction(folder, "Sales", "Header Mapping", Path.GetFileName(path), $"A Sales Register was detected but its header mapping is incomplete. Open Sales and map this format once.\nSource: {path}\nHeader signature: {inspection.HeaderSignature}", fileHash);
            if (newAction && _settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                Notify("Sales mapping required", $"{folder.ClientName}: {Path.GetFileName(path)}", true);
            return;
        }

        var options = _salesMappingRepository.GetOptions(folder.ClientId, folder.FinancialYear, inspection.HeaderSignature);
        var result = await Task.Run(() => service.Import(
            workbook,
            path,
            folder.ClientId,
            client.Gstin,
            client.ClientName,
            folder.FinancialYear,
            inspection.SuggestedMapping,
            options.GstTreatment,
            options.TaxColumnMode,
            fileHash), cancellationToken).ConfigureAwait(false);

        var masterSummary = new SalesAccountingMasterSyncService(_masterRepository).Ensure(
            folder.ClientId, folder.FinancialYear, client.ClientName, result.Records);
        var normalization = new SalesStockNormalizationService(_stockMasterRepository, _actionRepository);
        var stockSummary = normalization.Normalize(folder.ClientId, folder.FinancialYear, client.ClientName, result.Records);
        var inserted = _salesRecordRepository.SaveManualImport(folder.ClientId, folder.FinancialYear, result, replaceExisting: false);
        RecordHistory(folder, path, fileHash, "Sales", "Imported", $"{inserted:N0} row(s) imported; {stockSummary.ActionsRequired:N0} stock review item(s).", inserted);
        ResolveAutomationAction(folder, "Sales", fileHash);
        var dates = result.Records.Where(row => row.DocumentDate.HasValue).Select(row => row.DocumentDate!.Value).ToArray();
        _activityRepository.Record(new ImportActivity
        {
            ClientId = folder.ClientId,
            ImportType = "Sales",
            PeriodFrom = dates.Length == 0 ? null : dates.Min(),
            PeriodTo = dates.Length == 0 ? null : dates.Max(),
            RecordCount = inserted,
            SourceSummary = Path.GetFileName(path),
            Status = "Auto Imported",
            Details = $"Cached source; saved header signature {inspection.HeaderSignature}; {stockSummary.ActionsRequired:N0} stock review item(s).",
            ImportedUtc = DateTime.UtcNow
        });
        RaiseAccountingDataChanged(folder, "Sales", inserted, path);
        if (masterSummary.CreatedOrUpdated > 0) RaiseAccountingDataChanged(folder, "Masters", masterSummary.CreatedOrUpdated, path);
        if (stockSummary.InventoryRows > 0) RaiseAccountingDataChanged(folder, "Stock", stockSummary.InventoryRows, path);
        if (_settings.GetBool(ApplicationSettingKeys.NotifyImportComplete))
            Notify("Sales Register auto-imported", $"{folder.ClientName}: {Path.GetFileName(path)} • {inserted:N0} row(s)", false);
    }

    private async Task ProcessSalesAsync(
        AutomationFolderRegistration folder,
        string path,
        string fileHash,
        CancellationToken cancellationToken,
        string? initialPassword = null,
        IReadOnlyList<string>? suppliedPasswordCandidates = null)
    {
        string? convertedPath = null;
        try
        {
            var importPath = path;
            var sourceExtension = Path.GetExtension(path).ToLowerInvariant();
            if (sourceExtension is ".xls" or ".xlsx" or ".xlsm")
            {
                var sourceInspection = SpreadsheetSourceInspector.Inspect(path);
                if (sourceInspection.Kind is SpreadsheetSourceKind.LegacyOleWorkbook or
                    SpreadsheetSourceKind.XmlSpreadsheet or
                    SpreadsheetSourceKind.UnknownBinary)
                {
                    var candidates = (suppliedPasswordCandidates ?? Array.Empty<string>())
                        .Select(candidate => (candidate ?? string.Empty).Trim())
                        .Where(candidate => candidate.Length is > 0 and <= 64)
                        .Distinct(StringComparer.Ordinal)
                        .Take(12)
                        .ToList();
                    if (!string.IsNullOrWhiteSpace(initialPassword) && !candidates.Contains(initialPassword, StringComparer.Ordinal))
                        candidates.Insert(0, initialPassword);
                    if (candidates.Count == 0) candidates.Add(string.Empty);

                    Exception? lastPasswordFailure = null;
                    foreach (var candidate in candidates)
                    {
                        try
                        {
                            convertedPath = AutomationFileClassifier.ConvertLegacyXlsForInspection(path, candidate);
                            importPath = convertedPath;
                            lastPasswordFailure = null;
                            break;
                        }
                        catch (Exception exception)
                        {
                            lastPasswordFailure = exception;
                        }
                    }
                    if (lastPasswordFailure is not null) throw lastPasswordFailure;
                }
            }

            var service = new SalesManualImportService();
            var first = service.Inspect(importPath);
            var saved = _settings.GetBool(ApplicationSettingKeys.SalesUseSavedMappings)
                ? _salesMappingRepository.Get(folder.ClientId, folder.FinancialYear, first.HeaderSignature)
                : null;
            var inspection = service.Inspect(importPath, saved);
            var client = _clientRepository.GetById(folder.ClientId)
                ?? throw new InvalidOperationException("The client linked to this Automation Folder no longer exists.");

            // R9 FIXED16 R13: direct-map printed Tally Sales Register rate evidence.
            // Keep the original source path/hash as durable evidence even when a legacy
            // .xls was temporarily converted to Open XML for inspection.
            if (inspection.IsTallyRateSummaryMapping)
            {
                var rateResult = await Task.Run(() => service.Import(
                    importPath,
                    folder.ClientId,
                    client.Gstin,
                    client.ClientName,
                    folder.FinancialYear,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    "LOCAL",
                    "SINGLE"), cancellationToken).ConfigureAwait(false);
                ApplyTallySalesRegisterRateSummary(folder, path, fileHash, rateResult);
                return;
            }

            if (!inspection.IsComplete)
            {
                RecordHistory(folder, path, fileHash, "Sales", "Needs Action", $"Sales header mapping is required. Signature: {inspection.HeaderSignature}");
                var newAction = AddAction(folder, "Sales", "Header Mapping", Path.GetFileName(path), $"A Sales Register was detected but its header mapping is incomplete. Open Sales and map this format once.\nSource: {path}\nHeader signature: {inspection.HeaderSignature}", fileHash);
                if (newAction && _settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                    Notify("Sales mapping required", $"{folder.ClientName}: {Path.GetFileName(path)}", true);
                return;
            }

            var options = _salesMappingRepository.GetOptions(folder.ClientId, folder.FinancialYear, inspection.HeaderSignature);
            var result = await Task.Run(() => service.Import(
                importPath,
                folder.ClientId,
                client.Gstin,
                client.ClientName,
                folder.FinancialYear,
                inspection.SuggestedMapping,
                options.GstTreatment,
                options.TaxColumnMode), cancellationToken);

            var embeddedTallyMasters = 0;
            if (Path.GetExtension(importPath).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                var parsedMasters = new TallyMasterImportService().Parse(
                    importPath, folder.ClientId, folder.FinancialYear, client.ClientName);
                var accountingMasters = parsedMasters.Records
                    .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ||
                                     master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                embeddedTallyMasters = _masterRepository.Upsert(folder.ClientId, folder.FinancialYear, accountingMasters, markUserEdited: false);
            }

            var masterSummary = new SalesAccountingMasterSyncService(_masterRepository).Ensure(
                folder.ClientId, folder.FinancialYear, client.ClientName, result.Records);
            var normalization = new SalesStockNormalizationService(_stockMasterRepository, _actionRepository);
            var stockSummary = normalization.Normalize(folder.ClientId, folder.FinancialYear, client.ClientName, result.Records);
            var inserted = _salesRecordRepository.SaveManualImport(folder.ClientId, folder.FinancialYear, result, replaceExisting: false);
            RecordHistory(folder, path, fileHash, "Sales", "Imported", $"{inserted:N0} row(s) imported; {embeddedTallyMasters + masterSummary.CreatedOrUpdated:N0} accounting Master(s); {stockSummary.ActionsRequired:N0} stock review item(s).", inserted);
            ResolveAutomationAction(folder, "Sales", fileHash);
            var dates = result.Records.Where(row => row.DocumentDate.HasValue).Select(row => row.DocumentDate!.Value).ToArray();
            _activityRepository.Record(new ImportActivity
            {
                ClientId = folder.ClientId,
                ImportType = "Sales",
                PeriodFrom = dates.Length == 0 ? null : dates.Min(),
                PeriodTo = dates.Length == 0 ? null : dates.Max(),
                RecordCount = inserted,
                SourceSummary = Path.GetFileName(path),
                Status = "Auto Imported",
                Details = $"Saved header signature {inspection.HeaderSignature}; {stockSummary.ActionsRequired:N0} stock review item(s).",
                ImportedUtc = DateTime.UtcNow
            });
            RaiseAccountingDataChanged(folder, "Sales", inserted, path);
            if (embeddedTallyMasters + masterSummary.CreatedOrUpdated > 0) RaiseAccountingDataChanged(folder, "Masters", embeddedTallyMasters + masterSummary.CreatedOrUpdated, path);
            if (stockSummary.InventoryRows > 0) RaiseAccountingDataChanged(folder, "Stock", stockSummary.InventoryRows, path);
            if (_settings.GetBool(ApplicationSettingKeys.NotifyImportComplete))
                Notify("Sales Register auto-imported", $"{folder.ClientName}: {Path.GetFileName(path)} • {inserted:N0} row(s)", false);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(convertedPath))
            {
                try { if (File.Exists(convertedPath)) File.Delete(convertedPath); } catch { }
            }
        }
    }

    private bool NeedsTallySalesRegisterSummaryReplayForPath(
        AutomationFolderRegistration folder,
        string sourcePath)
    {
        try
        {
            var imported = _automationRepository.GetLatestImportedFileByPath(
                folder.FolderId, folder.ClientId, sourcePath, "Sales");
            if (imported is null) return false;
            return NeedsTallySalesRegisterSummaryReplay(folder, imported, imported.FileHash);
        }
        catch
        {
            return false;
        }
    }

    private bool NeedsTallySalesRegisterSummaryReplay(
        AutomationFolderRegistration folder,
        AutomationFileHistoryRecord history,
        string fileHash)
    {
        if (!history.FileType.Equals("Sales", StringComparison.OrdinalIgnoreCase) ||
            !history.Detail.Contains("Tally Sales Register mapped directly", StringComparison.OrdinalIgnoreCase) ||
            history.Detail.Contains("Sales statement summary row", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            return !_salesRecordRepository.GetRecords(folder.ClientId, folder.FinancialYear).Any(row =>
                row.SourceOrigin.Equals("Tally Sales Register Summary", StringComparison.OrdinalIgnoreCase) &&
                row.SourceHash.Equals(fileHash, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private int ApplyTallySalesRegisterRateSummary(
        AutomationFolderRegistration folder,
        string sourcePath,
        string fileHash,
        SalesManualImportResult result)
    {
        if (!result.IsTallyRateSummaryMapping || result.RateMappings.Count == 0)
            throw new InvalidDataException("The Tally Sales Register was recognized as rate evidence but no date/voucher-range GST mapping rows were produced.");

        var updatedRows = _salesRecordRepository.ApplyTallySalesRegisterRateMappings(
            folder.ClientId, folder.FinancialYear, result.RateMappings, sourcePath, fileHash);
        var summaryRows = 0;
        if (updatedRows == 0 && result.Records.Count > 0)
        {
            // R9 FIXED16 R15: when the supplied Crystal/Tally Sales Register is the only
            // available Sales source, persist one non-exportable daily summary row for each
            // printed date/voucher range. This makes the statement visible in Sales/Dashboard
            // without inventing individual voucher amounts. A later complete Tally XML import
            // remains voucher authority and can supersede these statement summaries.
            summaryRows = _salesRecordRepository.SaveManualImport(
                folder.ClientId,
                folder.FinancialYear,
                result,
                replaceExisting: false,
                replaceSourcePath: sourcePath);
        }
        var dates = result.RateMappings.Select(mapping => mapping.SalesDate.Date).ToArray();
        var detail =
            $"Tally Sales Register mapped directly: {result.RateMappings.Count:N0} day/voucher-range GST evidence row(s); " +
            $"{updatedRows:N0} existing Tally XML Sales/Credit Note row(s) enriched; " +
            $"{summaryRows:N0} non-exportable Sales statement summary row(s) saved. " +
            "No individual voucher amounts were fabricated; exact XML vouchers remain authoritative when available.";

        RecordHistory(folder, sourcePath, fileHash, "Sales", "Imported", detail, summaryRows > 0 ? summaryRows : result.RateMappings.Count);
        ResolveAutomationAction(folder, "Sales", fileHash);
        _activityRepository.Record(new ImportActivity
        {
            ClientId = folder.ClientId,
            ImportType = "Sales",
            PeriodFrom = dates.Length == 0 ? null : dates.Min(),
            PeriodTo = dates.Length == 0 ? null : dates.Max(),
            RecordCount = result.RateMappings.Count,
            SourceSummary = Path.GetFileName(sourcePath),
            Status = "Auto Imported",
            Details = detail,
            ImportedUtc = DateTime.UtcNow
        });

        if (updatedRows + summaryRows > 0) RaiseAccountingDataChanged(folder, "Sales", updatedRows + summaryRows, sourcePath);
        if (_settings.GetBool(ApplicationSettingKeys.NotifyImportComplete))
            Notify(
                "Tally Sales Register imported",
                $"{folder.ClientName}: {Path.GetFileName(sourcePath)} • {result.RateMappings.Count:N0} mapping(s), {updatedRows:N0} Tally XML row(s) enriched, {summaryRows:N0} Sales summary row(s) saved",
                false);
        return updatedRows + summaryRows;
    }

    private (PurchaseMappingProfile? Profile, PurchaseManualInspection Inspection) ResolvePurchaseMappingProfile(
        AutomationFolderRegistration folder,
        PurchaseManualImportService service,
        OpenXmlWorkbookData workbook,
        string sourcePath)
    {
        var first = service.Inspect(workbook, sourcePath);
        if (!_settings.GetBool(ApplicationSettingKeys.PurchaseUseSavedMappings))
            return (null, first);

        var profiles = new List<PurchaseMappingProfile>();
        var exact = _purchaseMappingRepository.Get(folder.ClientId, folder.FinancialYear, first.HeaderSignature);
        if (exact is not null) profiles.Add(exact);
        foreach (var profile in _purchaseMappingRepository.GetAll(folder.ClientId, folder.FinancialYear))
        {
            if (profiles.Any(existing => existing.HeaderSignature.Equals(profile.HeaderSignature, StringComparison.OrdinalIgnoreCase)))
                continue;
            profiles.Add(profile);
        }

        foreach (var profile in profiles)
        {
            try
            {
                var profiled = service.Inspect(
                    workbook,
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
                // Profiles are format-specific. A profile whose sheet/layout is absent in
                // this workbook is simply not a match; retain it for its own workbook type.
            }
        }

        return (null, first);
    }

    private async Task ProcessPurchaseWorkbookAsync(
        AutomationFolderRegistration folder,
        string sourcePath,
        string fileHash,
        OpenXmlWorkbookData workbook,
        CancellationToken cancellationToken)
    {
        var client = _clientRepository.GetById(folder.ClientId)
            ?? throw new InvalidOperationException("The client linked to this Automation Folder no longer exists.");
        var service = new PurchaseManualImportService();
        var (profile, inspection) = ResolvePurchaseMappingProfile(folder, service, workbook, sourcePath);

        if (!inspection.HeaderDetected || !inspection.IsComplete)
        {
            var detail = $"Purchase header mapping is required. Signature: {inspection.HeaderSignature}. Open Purchase and map this workbook format once; Manual Import and Auto Import will then use the same Purchase profile.";
            RecordHistory(folder, sourcePath, fileHash, "Purchase", "Needs Action", detail);
            var newAction = AddAction(folder, "Purchase", "Header Mapping", Path.GetFileName(sourcePath), $"{detail}\nSource: {sourcePath}", fileHash);
            if (newAction && _settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                Notify("Purchase mapping required", $"{folder.ClientName}: {Path.GetFileName(sourcePath)}", true);
            return;
        }

        var result = await Task.Run(() => service.Import(
            workbook,
            sourcePath,
            folder.ClientId,
            client.Gstin,
            string.IsNullOrWhiteSpace(client.LegalName) ? client.ClientName : client.LegalName,
            folder.FinancialYear,
            inspection.SuggestedMapping,
            profile?.SheetName,
            profile?.HeaderRow,
            profile?.DataStartRow,
            profile?.GstTreatment ?? PurchaseTreatmentPolicy.NotShownInTwoB), cancellationToken).ConfigureAwait(false);

        var normalization = new PurchaseStockNormalizationService(_stockMasterRepository, _actionRepository);
        var stockSummary = normalization.Normalize(folder.ClientId, folder.FinancialYear, client.ClientName, result.Records);
        var inserted = _purchaseRecordRepository.SaveManualImport(folder.ClientId, folder.FinancialYear, result, replaceExisting: false);
        RecordHistory(folder, sourcePath, fileHash, "Purchase", "Imported", $"{inserted:N0} row(s) imported through the shared Purchase profile process; {stockSummary.ActionsRequired:N0} stock review item(s).", inserted);
        ResolveAutomationAction(folder, "Purchase", fileHash);

        var dates = result.Records.Where(row => row.DocumentDate.HasValue).Select(row => row.DocumentDate!.Value).ToArray();
        _activityRepository.Record(new ImportActivity
        {
            ClientId = folder.ClientId,
            ImportType = "Purchase",
            PeriodFrom = dates.Length == 0 ? null : dates.Min(),
            PeriodTo = dates.Length == 0 ? null : dates.Max(),
            RecordCount = inserted,
            SourceSummary = Path.GetFileName(sourcePath),
            Status = "Auto Imported",
            Details = $"Shared Purchase profile process; header signature {inspection.HeaderSignature}; saved profile={(profile is null ? "built-in/global" : "yes")}; {stockSummary.ActionsRequired:N0} stock review item(s).",
            ImportedUtc = DateTime.UtcNow
        });
        RaiseAccountingDataChanged(folder, "Purchase", inserted, sourcePath);
        if (_settings.GetBool(ApplicationSettingKeys.NotifyImportComplete))
            Notify("Purchase Register auto-imported", $"{folder.ClientName}: {Path.GetFileName(sourcePath)} • {inserted:N0} row(s)", false);
    }

    private async Task ProcessPurchaseAsync(
        AutomationFolderRegistration folder,
        string sourcePath,
        string fileHash,
        CancellationToken cancellationToken,
        string? initialPassword = null,
        IReadOnlyList<string>? suppliedPasswordCandidates = null)
    {
        var client = _clientRepository.GetById(folder.ClientId)
            ?? throw new InvalidOperationException("The client linked to this Automation Folder no longer exists.");

        var sourceExtension = Path.GetExtension(sourcePath).ToLowerInvariant();

        // Fix114: Purchase workbook/text Auto Import now follows the same cache-first,
        // saved-header profile process as Sales. JSON/ZIP remains the separate statutory
        // GSTR-2A/2B source-set route below.
        if (sourceExtension is ".xls" or ".xlsx" or ".xlsm" or ".csv" or ".txt" or ".tsv")
        {
            string? convertedPath = null;
            try
            {
                var importPath = sourcePath;
                if (sourceExtension is ".xls" or ".xlsx" or ".xlsm")
                {
                    var sourceInspection = SpreadsheetSourceInspector.Inspect(sourcePath);
                    if (sourceInspection.Kind is SpreadsheetSourceKind.LegacyOleWorkbook or
                        SpreadsheetSourceKind.XmlSpreadsheet or
                        SpreadsheetSourceKind.UnknownBinary)
                    {
                        var candidates = (suppliedPasswordCandidates ?? Array.Empty<string>())
                            .Select(candidate => (candidate ?? string.Empty).Trim())
                            .Where(candidate => candidate.Length is > 0 and <= 64)
                            .Distinct(StringComparer.Ordinal)
                            .Take(12)
                            .ToList();
                        if (!string.IsNullOrWhiteSpace(initialPassword) && !candidates.Contains(initialPassword, StringComparer.Ordinal))
                            candidates.Insert(0, initialPassword);
                        if (candidates.Count == 0) candidates.Add(string.Empty);

                        Exception? lastPasswordFailure = null;
                        foreach (var candidate in candidates)
                        {
                            try
                            {
                                convertedPath = AutomationFileClassifier.ConvertLegacyXlsForInspection(sourcePath, candidate);
                                importPath = convertedPath;
                                lastPasswordFailure = null;
                                break;
                            }
                            catch (Exception exception)
                            {
                                lastPasswordFailure = exception;
                            }
                        }
                        if (lastPasswordFailure is not null) throw lastPasswordFailure;
                    }
                }

                var workbook = new FlexibleWorkbookReader().Read(importPath, cancellationToken);
                await ProcessPurchaseWorkbookAsync(folder, sourcePath, fileHash, workbook, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(convertedPath))
                {
                    try { if (File.Exists(convertedPath)) File.Delete(convertedPath); } catch { }
                }
            }
            return;
        }

        if (sourceExtension is not (".json" or ".zip"))
        {
            var message = "Purchase source type is not supported by the shared Purchase Register profile or the verified GSTR-2A/2B JSON/ZIP source-set workflow.";
            RecordHistory(folder, sourcePath, fileHash, "Purchase", "Needs Action", message);
            var newAction = AddAction(folder, "Purchase", "Purchase Import Review", Path.GetFileName(sourcePath), $"{message}\nSource: {sourcePath}", fileHash);
            if (newAction && _settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                Notify("Purchase source needs review", $"{folder.ClientName}: {Path.GetFileName(sourcePath)}", true);
            return;
        }

        if (NormalizeGstin(client.Gstin).Length != 15)
        {
            const string message = "Purchase Auto Import requires a valid 15-character client GSTIN.";
            RecordHistory(folder, sourcePath, fileHash, "Purchase", "Needs Action", message);
            AddAction(folder, "Purchase", "Purchase Import Review", Path.GetFileName(sourcePath), $"{message}\nSource: {sourcePath}", fileHash);
            return;
        }

        var root = _settings.Get(EReturnsRootContext.PreferenceKey).Trim();
        if (root.Length == 0 || !Directory.Exists(root))
        {
            const string message = "Purchase source was detected, but the E-returns root folder is not configured or is unavailable.";
            RecordHistory(folder, sourcePath, fileHash, "Purchase", "Needs Action", message);
            AddAction(folder, "Purchase", "Purchase Import Review", Path.GetFileName(sourcePath), $"{message} Open Purchase/Settings, select the E-returns root, then Retry Import.\nSource: {sourcePath}", fileHash);
            return;
        }

        var result = await _purchaseAutoImportService.ImportAsync(new PurchaseAutoImportRequest
        {
            ClientId = client.Id,
            ClientGstin = client.Gstin,
            FinancialYear = folder.FinancialYear,
            CompanyName = string.IsNullOrWhiteSpace(client.LegalName) ? client.ClientName : client.LegalName,
            EReturnsRootPath = root,
            SearchWindowsDownloads = true,
            SaveSourceFiles = false
        }, cancellationToken: cancellationToken);

        result.SourceFingerprint = BuildPurchaseSourceFingerprint(result);
        var hasTwoA = result.SourceManifest.Any(source => source.FileType.Equals("2A", StringComparison.OrdinalIgnoreCase));
        var triggerSourceWasUsed = result.SourceManifest.Any(source => PathsEqual(source.SourcePath, sourcePath));
        if (!triggerSourceWasUsed)
        {
            var message = "The detected Purchase file is not part of the verified E-returns/Downloads source set resolved for this client and FY. Nothing was posted from an unrelated source set. Place/link the file in the configured E-returns structure or resolve it from Purchase Import, then Retry Import.";
            RecordHistory(folder, sourcePath, fileHash, "Purchase", "Needs Action", message);
            var newAction = AddAction(folder, "Purchase", "Purchase Import Review", Path.GetFileName(sourcePath), $"{message}\nSource: {sourcePath}", fileHash);
            if (newAction && _settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                Notify("Purchase source needs review", $"{folder.ClientName}: {Path.GetFileName(sourcePath)}", true);
            return;
        }
        if (result.WasBlocked || !hasTwoA)
        {
            var issue = result.Issues.Count == 0
                ? "No valid GSTR-2A source was resolved for this client/FY."
                : string.Join(" | ", result.Issues.Select(item => item.Message));
            RecordHistory(folder, sourcePath, fileHash, "Purchase", "Needs Action", issue);
            var newAction = AddAction(folder, "Purchase", "Purchase Import Review", Path.GetFileName(sourcePath), $"{issue}\nSource: {sourcePath}", fileHash);
            if (newAction && _settings.GetBool(ApplicationSettingKeys.NotifyActionRequired))
                Notify("Purchase import needs review", $"{folder.ClientName}: {Path.GetFileName(sourcePath)}", true);
            return;
        }

        var current = _purchaseRecordRepository.IsAutomaticImportCurrent(client.Id, folder.FinancialYear, result.SourceFingerprint);
        var persisted = current
            ? _purchaseRecordRepository.GetAutomaticRecords(client.Id, folder.FinancialYear).Count
            : _purchaseRecordRepository.ReplaceAutomaticImport(client.Id, folder.FinancialYear, result);
        result.PersistedRows = persisted;
        RecordHistory(folder, sourcePath, fileHash, "Purchase", current ? "Duplicate" : "Imported",
            current ? $"Purchase is already current ({persisted:N0} saved rows)." : $"{persisted:N0} Purchase/RCM/Debit Note/Debit Note Reverse row(s) saved from the current verified E-returns source set.", current ? 0 : persisted);
        ResolveAutomationAction(folder, "Purchase", fileHash);

        if (!current)
        {
            _activityRepository.Record(new ImportActivity
            {
                ClientId = folder.ClientId,
                ImportType = "Purchase",
                RecordCount = persisted,
                SourceSummary = Path.GetFileName(sourcePath),
                Status = "Auto Imported",
                Details = $"Purchase {result.Summary.PurchaseRows:N0}; RCM {result.Summary.RcmRows:N0}; Debit Note {result.Summary.DebitNoteRows:N0}; Debit Note Reverse {result.Summary.DebitNoteReverseRows:N0}.",
                ImportedUtc = DateTime.UtcNow
            });
            if (_settings.GetBool(ApplicationSettingKeys.NotifyImportComplete))
                Notify("Purchase auto-imported", $"{folder.ClientName}: {persisted:N0} saved row(s)", false);
        }
        RaiseAccountingDataChanged(folder, "Purchase", current ? 0 : persisted, sourcePath);
    }

    private static string BuildPurchaseSourceFingerprint(PurchaseAutoImportResult result)
    {
        var builder = new StringBuilder();
        builder.Append(result.ClientGstin).Append('|')
            .Append(result.FinancialYear).Append('|')
            .Append(result.Verification.TwoA.Status).Append('|')
            .Append(result.Verification.TwoB.Status).AppendLine();
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
                .Append(source.PayloadHash).AppendLine();
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private BankAccountRecord? SelectTrustedBankAccountFallback(AutomationFolderRegistration folder, string sourcePath)
    {
        var accounts = _bankingRepository.GetAccounts(folder.ClientId, folder.FinancialYear)
            .Where(account => !string.IsNullOrWhiteSpace(account.BankName) && !string.IsNullOrWhiteSpace(account.AccountNumber))
            .ToArray();
        if (accounts.Length == 0) return null;
        if (accounts.Length == 1) return accounts[0];

        var file = NormalizeFileMatchText(Path.GetFileNameWithoutExtension(sourcePath));
        var ranked = accounts
            .Select(account => (Account: account, Score: ScoreBankAccountMatch(account, file)))
            .OrderByDescending(item => item.Score)
            .ToArray();
        if (ranked.Length == 0 || ranked[0].Score <= 0) return null;
        if (ranked.Length > 1 && ranked[0].Score == ranked[1].Score) return null;
        return ranked[0].Account;
    }

    private static int ScoreBankAccountMatch(BankAccountRecord account, string normalizedFile)
    {
        var score = 0;
        var digits = new string((account.AccountNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length >= 4 && normalizedFile.Contains(digits[^4..], StringComparison.OrdinalIgnoreCase)) score += 20;
        if (digits.Length >= 6 && normalizedFile.Contains(digits[^6..], StringComparison.OrdinalIgnoreCase)) score += 20;

        var bank = NormalizeFileMatchText(account.BankName);
        bank = bank.Replace("LIMITED", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("LTD", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("BANK", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (bank.Length >= 3 && normalizedFile.Contains(bank, StringComparison.OrdinalIgnoreCase)) score += 8;

        var type = NormalizeFileMatchText(account.AccountType);
        if (type.Length >= 2 && normalizedFile.Contains(type, StringComparison.OrdinalIgnoreCase)) score += 2;
        return score;
    }

    private static string NormalizeFileMatchText(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool IsBankStatementKind(AutomationFileKind kind) =>
        kind is AutomationFileKind.BankPdf
            or AutomationFileKind.BankOcrSource
            or AutomationFileKind.BankSpreadsheet
            or AutomationFileKind.Mt940
            or AutomationFileKind.Camt053
            or AutomationFileKind.Ofx
            or AutomationFileKind.Qif;

    private bool BankStatementSourceStillExists(AutomationFileHistoryRecord history)
    {
        var statements = _bankingRepository.GetStatements(history.ClientId, history.FinancialYear);

        // Secondary photos in an AAH production OCR batch deliberately retain their own
        // content hashes, while the Bank Statement itself uses one canonical/primary
        // source path. Keep the grouped-page history valid only while that canonical
        // statement still exists.
        var groupedSource = ExtractGroupedOcrStatementSource(history.Detail);
        if (groupedSource.Length > 0)
        {
            foreach (var statement in statements)
            {
                if (!string.IsNullOrWhiteSpace(statement.SourcePath) &&
                    PathsEqual(statement.SourcePath, groupedSource))
                    return true;
                if (string.IsNullOrWhiteSpace(statement.SourcePath) &&
                    Path.GetFileName(groupedSource).Equals(statement.SourceFile, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        foreach (var statement in statements)
        {
            if (!string.IsNullOrWhiteSpace(statement.SourcePath) && PathsEqual(statement.SourcePath, history.FilePath))
                return true;
            if (string.IsNullOrWhiteSpace(statement.SourcePath) &&
                Path.GetFileName(history.FilePath).Equals(statement.SourceFile, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ExtractGroupedOcrStatementSource(string detail)
    {
        const string marker = "OCR_GROUP_SOURCE=";
        if (string.IsNullOrWhiteSpace(detail)) return string.Empty;
        var start = detail.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return string.Empty;
        start += marker.Length;

        // Fix 30 repair: older grouped-OCR history accidentally persisted the separator
        // as the two literal characters "\\n" instead of an actual line break. That
        // made the stored primary path include the following detail text, so every
        // 10-second reconciliation scan concluded the Bank Statement no longer existed,
        // deleted the successful page history and started PaddleOCR again. Accept both
        // legacy literal separators and the corrected real newline format.
        var ends = new List<int>();
        var actualLineBreak = detail.IndexOfAny(['\r', '\n'], start);
        if (actualLineBreak >= 0) ends.Add(actualLineBreak);
        var literalNewLine = detail.IndexOf("\\n", start, StringComparison.Ordinal);
        if (literalNewLine >= 0) ends.Add(literalNewLine);
        var literalCarriageReturn = detail.IndexOf("\\r", start, StringComparison.Ordinal);
        if (literalCarriageReturn >= 0) ends.Add(literalCarriageReturn);
        var end = ends.Count == 0 ? detail.Length : ends.Min();
        return detail[start..end].Trim();
    }

    private async Task<bool> WaitForStableFileAsync(string path, CancellationToken cancellationToken)
    {
        var stableSeconds = Math.Clamp(_settings.GetInt(ApplicationSettingKeys.AutomationFileStableSeconds, 3), 1, 30);
        long lastLength = -1;
        DateTime lastWrite = DateTime.MinValue;
        var stableFor = 0;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return false;
            try
            {
                var info = new FileInfo(path);
                // A zero-byte placeholder can be created several seconds before a copy/download
                // starts writing.  It must never be accepted merely because its size is stable.
                if (info.Length <= 0)
                {
                    stableFor = 0;
                    lastLength = 0;
                    lastWrite = info.LastWriteTimeUtc;
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }
                var unchanged = info.Length == lastLength && info.LastWriteTimeUtc == lastWrite;
                // R52 FIXED26: stability probing must never itself prevent Windows
                // from renaming/moving the containing folder. We only need a readable
                // snapshot check, so share read/write/delete while the short probe is open.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (unchanged) stableFor++;
                else stableFor = 0;
                lastLength = info.Length;
                lastWrite = info.LastWriteTimeUtc;
                if (stableFor >= stableSeconds) return true;
            }
            catch (IOException)
            {
                stableFor = 0;
            }
            catch (UnauthorizedAccessException)
            {
                stableFor = 0;
            }
            await Task.Delay(1000, cancellationToken);
        }
        return false;
    }

    private void RepairLegacyGroupedOcrPageHistory(AutomationFolderRegistration folder)
    {
        try
        {
            var history = _automationRepository.GetHistory(folder.ClientId, folder.FinancialYear, 5000);
            if (history.Count == 0) return;

            // Current Bank Statements are the accounting authority. Include their source
            // paths as well as successful Auto Import history so a grouped page can be
            // recovered even if an older reconciliation scan already overwrote its own
            // status after mis-parsing the legacy OCR_GROUP_SOURCE separator.
            var successfulPaths = history
                .Where(item =>
                    item.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase) ||
                    item.Status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase))
                .Select(item => NormalizeAutomationPath(item.FilePath))
                .Where(path => path.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var statement in _bankingRepository.GetStatements(folder.ClientId, folder.FinancialYear))
            {
                var source = NormalizeAutomationPath(statement.SourcePath);
                if (source.Length > 0) successfulPaths.Add(source);
            }

            if (successfulPaths.Count == 0) return;

            const string legacyMarker = "Review the primary source/action: ";
            var repaired = 0;
            var repairedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in history)
            {
                if (item.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase) ||
                    item.Status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase)) continue;
                if (!item.FileType.Equals("Bank OCR Page", StringComparison.OrdinalIgnoreCase) &&
                    !item.FileType.Equals("Bank OCR", StringComparison.OrdinalIgnoreCase)) continue;

                // Newer broken builds already wrote OCR_GROUP_SOURCE= but used a literal
                // "\\n" separator. ExtractGroupedOcrStatementSource now understands that
                // legacy representation, so repair those rows directly first.
                var primary = ExtractGroupedOcrStatementSource(item.Detail);
                if (primary.Length == 0)
                {
                    var markerIndex = item.Detail.LastIndexOf(legacyMarker, StringComparison.OrdinalIgnoreCase);
                    if (markerIndex < 0) continue;
                    primary = item.Detail[(markerIndex + legacyMarker.Length)..].Trim();
                    var newline = primary.IndexOfAny(new[] { '\r', '\n' });
                    if (newline >= 0) primary = primary[..newline].Trim();
                }

                var normalizedPrimary = NormalizeAutomationPath(primary);
                if (normalizedPrimary.Length == 0 || !successfulPaths.Contains(normalizedPrimary)) continue;

                RecordHistory(
                    folder,
                    item.FilePath,
                    item.FileHash,
                    "Bank OCR Page",
                    "Imported",
                    $"OCR_GROUP_SOURCE={primary}\nSelf-healed grouped OCR page status because the primary grouped Bank Statement is already present in accounting data.",
                    item.RecordCount);
                ResolveAutomationAction(folder, "Banking", item.FileHash);
                repairedHashes.Add(item.FileHash);
                repaired++;
            }

            // Final recovery for the exact failure created by Source Status Repair 2:
            // the broken separator could cause a later scan to overwrite the page row
            // entirely, so no OCR_GROUP_SOURCE marker remains to parse. Infer only when
            // there is exactly ONE existing grouped statement with a "+ N page(s)" label
            // and exactly N unresolved raster sources from the same directory that were
            // already present when that statement was imported. Any ambiguity is left
            // untouched and will require a normal explicit retry instead of guessing.
            var groupedStatements = _bankingRepository.GetStatements(folder.ClientId, folder.FinancialYear)
                .Select(statement => new
                {
                    Statement = statement,
                    AdditionalPages = ParseGroupedOcrAdditionalPageCount(statement.SourceFile),
                    Primary = NormalizeAutomationPath(statement.SourcePath)
                })
                .Where(item => item.AdditionalPages > 0 &&
                               item.Primary.Length > 0 &&
                               BankOcrExcelConversionService.IsRasterImage(item.Primary))
                .ToArray();

            if (groupedStatements.Length == 1)
            {
                var grouped = groupedStatements[0];
                var primaryDirectory = Path.GetDirectoryName(grouped.Primary) ?? string.Empty;
                var alreadyLinked = history.Count(item =>
                {
                    if (!item.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase) &&
                        !item.Status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase) &&
                        !repairedHashes.Contains(item.FileHash)) return false;
                    return NormalizeAutomationPath(ExtractGroupedOcrStatementSource(item.Detail))
                        .Equals(grouped.Primary, StringComparison.OrdinalIgnoreCase);
                });
                var missingPages = Math.Max(0, grouped.AdditionalPages - alreadyLinked);

                if (missingPages > 0)
                {
                    var candidates = history
                        .Where(item =>
                            !item.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase) &&
                            !item.Status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase) &&
                            !repairedHashes.Contains(item.FileHash))
                        .Where(item => File.Exists(item.FilePath) && BankOcrExcelConversionService.IsRasterImage(item.FilePath))
                        .Where(item => string.Equals(
                            Path.GetDirectoryName(NormalizeAutomationPath(item.FilePath)) ?? string.Empty,
                            primaryDirectory,
                            StringComparison.OrdinalIgnoreCase))
                        .Where(item =>
                        {
                            try
                            {
                                return File.GetLastWriteTimeUtc(item.FilePath) <= grouped.Statement.ImportedUtc.AddMinutes(2);
                            }
                            catch
                            {
                                return false;
                            }
                        })
                        .GroupBy(item => item.FileHash, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.OrderByDescending(item => item.ProcessedUtc).First())
                        .ToArray();

                    if (candidates.Length == missingPages)
                    {
                        foreach (var item in candidates)
                        {
                            RecordHistory(
                                folder,
                                item.FilePath,
                                item.FileHash,
                                "Bank OCR Page",
                                "Imported",
                                $"OCR_GROUP_SOURCE={grouped.Statement.SourcePath}\nSelf-healed from the existing grouped Bank Statement '{grouped.Statement.SourceFile}' before OCR recovery scanning.",
                                item.RecordCount);
                            ResolveAutomationAction(folder, "Banking", item.FileHash);
                            repairedHashes.Add(item.FileHash);
                            repaired++;
                        }
                    }
                }
            }

            if (repaired > 0)
                AutomationDiagnosticLog.Write("BANK-OCR-HISTORY", $"Self-healed {repaired:N0} grouped OCR page status row(s) for client={folder.ClientName} FY={folder.FinancialYear} before the recovery scan.", folder.FolderPath);
        }
        catch (Exception exception)
        {
            AutomationDiagnosticLog.Write("BANK-OCR-HISTORY", $"Grouped OCR page history self-heal deferred: {exception.Message}", folder.FolderPath);
        }
    }

    private static int ParseGroupedOcrAdditionalPageCount(string? sourceFile)
    {
        var value = (sourceFile ?? string.Empty).Trim();
        var plus = value.LastIndexOf(" + ", StringComparison.Ordinal);
        if (plus < 0) return 0;
        var tail = value[(plus + 3)..].Trim();
        var suffix = tail.IndexOf(" page(s)", StringComparison.OrdinalIgnoreCase);
        if (suffix <= 0) return 0;
        return int.TryParse(tail[..suffix].Trim(), out var count) && count > 0 ? count : 0;
    }

    private static string NormalizeAutomationPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path).Trim(); }
        catch { return path.Trim(); }
    }

    private void QueueAllActiveDashboardOutputs()
    {
        foreach (var client in _clientRepository.GetAll(false).Where(client => !client.IsArchived))
        {
            var lastOpened = _clientRepository.GetWorkspaceLastOpenedUtc(client.Id, client.FinancialYear);
            if (lastOpened != DateTime.MinValue && DateTime.UtcNow - lastOpened > TimeSpan.FromDays(183)) continue;
            QueueDashboardOutputRefresh(client.Id, client.FinancialYear);
        }
    }

    private void QueueDashboardOutputRefresh(long clientId, string financialYear)
    {
        var key = ProgressKey(clientId, financialYear);
        var version = _dashboardOutputVersions.AddOrUpdate(key, 1, (_, current) => current + 1);
        _ = Task.Run(() =>
        {
            try
            {
                var output = BuildDashboardOutput(clientId, financialYear);
                if (_dashboardOutputVersions.TryGetValue(key, out var current) && current == version)
                    _dashboardOutputs[key] = output;
            }
            catch (Exception exception)
            {
                AutomationDiagnosticLog.Write("AUTOIMPORT-DASHBOARD", $"Output refresh deferred: {exception.Message}");
            }
        });
    }

    private AutomationDashboardOutput BuildDashboardOutput(long clientId, string financialYear)
    {
        var history = _automationRepository.GetHistory(clientId, financialYear, 5000)
            .Where(row => DashboardAutomationModule(row.FileType).Length > 0)
            .Where(row => !ClientDiagnosticExportService.IsHubGeneratedExportPath(row.FilePath ?? string.Empty))
            .ToArray();

        var latest = history
            .GroupBy(row => DashboardSourceKey(row.FilePath, row.FileHash), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderByDescending(row => row.ProcessedUtc).ThenByDescending(row => row.PersistentId).ToArray();
                var newest = ordered[0];
                if (newest.Status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase))
                    return ordered.FirstOrDefault(row => !row.Status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase)) ?? newest;
                return newest;
            })
            .ToArray();
        var successful = latest.Where(row => row.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase)).ToArray();
        var modules = successful
            .GroupBy(row => DashboardAutomationModule(row.FileType), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var metrics = new List<AutomationDashboardMetric>();
        foreach (var module in new[] { "Banking", "Purchase", "Sales" })
        {
            var sources = modules.GetValueOrDefault(module, []);
            var count = DashboardAutomaticTransactionCount(clientId, financialYear, module, sources);
            var latestUtc = sources.Length == 0 ? DateTime.MinValue : sources.Max(row => row.ProcessedUtc);
            var detail = count == 0
                ? "0 automatic transactions"
                : $"{count:N0} automatic transaction(s)" +
                  (latestUtc == DateTime.MinValue ? string.Empty : $" • last {latestUtc.ToLocalTime():dd.MM HH:mm}");
            metrics.Add(new AutomationDashboardMetric(module, count, detail));
        }

        var failed = latest.Count(row =>
            !row.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase) &&
            (row.Status.Contains("Fail", StringComparison.OrdinalIgnoreCase) ||
             row.Status.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
             row.Status.Contains("Review", StringComparison.OrdinalIgnoreCase) ||
             row.Status.Contains("Action", StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(row.FilePath) || File.Exists(row.FilePath)));

        var recent = latest.OrderByDescending(row => row.ProcessedUtc).Take(8).ToArray();
        return new AutomationDashboardOutput(
            clientId,
            financialYear,
            DateTime.UtcNow,
            metrics,
            recent,
            successful.Length,
            metrics.Sum(metric => metric.Value),
            failed);
    }

    private int DashboardAutomaticTransactionCount(
        long clientId,
        string financialYear,
        string module,
        IReadOnlyList<AutomationFileHistoryRecord> sources)
    {
        if (sources.Count == 0) return 0;
        var hashes = sources.Select(row => (row.FileHash ?? string.Empty).Trim()).Where(value => value.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = sources.Select(row => NormalizeDashboardSource(row.FilePath)).Where(value => value.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = paths.Select(Path.GetFileName).Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (module.Equals("Banking", StringComparison.OrdinalIgnoreCase))
        {
            var statementIds = _bankingRepository.GetStatements(clientId, financialYear)
                .Where(statement => hashes.Contains((statement.FileHash ?? string.Empty).Trim()) ||
                                    paths.Contains(NormalizeDashboardSource(statement.SourcePath)) ||
                                    names.Contains((statement.SourceFile ?? string.Empty).Trim()))
                .Select(statement => statement.Id)
                .ToHashSet();
            return _bankingRepository.GetTransactions(clientId, financialYear, includeArchived: true)
                .Count(row => statementIds.Contains(row.StatementId) &&
                              !row.RecordStatus.Equals("Generated Transfer Charge", StringComparison.OrdinalIgnoreCase) &&
                              !row.RecordStatus.StartsWith("Duplicate", StringComparison.OrdinalIgnoreCase));
        }
        if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase))
            return _salesRecordRepository.GetRecords(clientId, financialYear).Count(row =>
                hashes.Contains((row.SourceHash ?? string.Empty).Trim()) ||
                paths.Contains(NormalizeDashboardSource(row.SourcePath)) ||
                names.Contains((row.SourceFile ?? string.Empty).Trim()));
        if (module.Equals("Purchase", StringComparison.OrdinalIgnoreCase))
            return _purchaseRecordRepository.GetAutomaticRecords(clientId, financialYear).Count(row =>
                paths.Contains(NormalizeDashboardSource(row.Source2APath)) ||
                paths.Contains(NormalizeDashboardSource(row.Source2BPath)) ||
                names.Contains((row.Source2AFile ?? string.Empty).Trim()) ||
                names.Contains((row.Source2BFile ?? string.Empty).Trim()));
        return 0;
    }

    private static string DashboardAutomationModule(string? fileType)
    {
        var value = (fileType ?? string.Empty).Trim();
        if (value.Equals("Bank", StringComparison.OrdinalIgnoreCase) || value.Equals("Bank PDF", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Bank OCR", StringComparison.OrdinalIgnoreCase) || value.Equals("Bank OCR Page", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("MT940", StringComparison.OrdinalIgnoreCase) || value.Equals("CAMT.053", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("OFX", StringComparison.OrdinalIgnoreCase) || value.Equals("QIF", StringComparison.OrdinalIgnoreCase)) return "Banking";
        if (value.Equals("Purchase", StringComparison.OrdinalIgnoreCase)) return "Purchase";
        if (value.Equals("Sales", StringComparison.OrdinalIgnoreCase)) return "Sales";
        return string.Empty;
    }

    private static string DashboardSourceKey(string? path, string? hash)
    {
        var normalized = NormalizeDashboardSource(path);
        return normalized.Length > 0 ? normalized : "HASH:" + (hash ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string NormalizeDashboardSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); }
        catch { return path.Trim().ToUpperInvariant(); }
    }

    private void RecordHistory(
        AutomationFolderRegistration folder,
        string path,
        string hash,
        string fileType,
        string status,
        string detail,
        int recordCount = 0)
    {
        if (hash.Length == 0) hash = $"NOHASH-{Guid.NewGuid():N}";
        long fileSize = 0;
        var lastWriteUtc = DateTime.MinValue;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                fileSize = info.Length;
                lastWriteUtc = info.LastWriteTimeUtc;
            }
        }
        catch
        {
            // File metadata is an acceleration hint only; accounting history remains authoritative.
        }
        _automationRepository.RecordFile(new AutomationFileHistoryRecord
        {
            FolderId = folder.FolderId,
            ClientId = folder.ClientId,
            FinancialYear = folder.FinancialYear,
            FilePath = path,
            FileHash = hash,
            FileType = fileType,
            Status = status,
            Detail = detail,
            RecordCount = Math.Max(0, recordCount),
            FileSize = fileSize,
            LastWriteUtc = lastWriteUtc,
            ProcessedUtc = DateTime.UtcNow
        });
        try
        {
            _governanceRepository.UpdateJobFromAutomation(folder.ClientId, folder.FinancialYear, folder.FolderId, path, hash, fileType, status, detail, recordCount);
        }
        catch (Exception exception)
        {
            AutomationDiagnosticLog.Write("IMPORT-JOB", $"Import Job synchronization deferred: {exception.Message}", path);
        }
        AutomationDiagnosticLog.Write("HISTORY", $"type={fileType} status={status}; {detail}", path);
        QueueDashboardOutputRefresh(folder.ClientId, folder.FinancialYear);
        FoldersChanged?.Invoke(this, EventArgs.Empty);
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Fix127: one post-import completion bridge for every intake method. Manual Import,
    /// Auto Import, linked-workbook, Universal Dump and future source-discovery routes
    /// all publish the same committed-accounting event after their module repository
    /// transaction succeeds. Discovery/parsing stays method-specific; durable cache,
    /// Dashboard, LAN/distributed sync and live module hydration do not.
    /// </summary>
    public void PublishCommittedAccountingImport(
        long clientId,
        string financialYear,
        string module,
        int recordCount,
        string sourcePath,
        string importMethod = "Manual Import")
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear) || string.IsNullOrWhiteSpace(module)) return;
        var fy = financialYear.Trim();
        var cleanModule = module.Trim();

        // Fix128: a UI/cache notification is allowed only after the same durable SQLite
        // authority can read the committed module back. This closes the gap where an import
        // dialog could report success and publish cache work even though the authoritative
        // module read was unavailable. Manual/Auto/Workbook/Folder are still intake methods
        // only; this verification is common to every committed-import caller.
        if (recordCount > 0)
        {
            var authorityCount = ReadCommittedAuthorityCount(clientId, fy, cleanModule);
            if (authorityCount <= 0)
            {
                var detail =
                    $"{importMethod} committed {recordCount:N0} {cleanModule} record(s), but the durable SQLite authority read-back returned 0. " +
                    "AAH will not publish an empty prepared-cache success for this import.";
                AutomationDiagnosticLog.Write("IMPORT-COMMIT-VERIFY", detail, sourcePath);
                throw new InvalidOperationException(detail);
            }
        }

        QueueDashboardOutputRefresh(clientId, financialYear);
        AccountingDataChanged?.Invoke(this, EventArgs.Empty);
        AccountingDataChangedDetailed?.Invoke(this, new AutomationAccountingDataChangedEventArgs(
            clientId,
            fy,
            cleanModule,
            Math.Max(0, recordCount),
            sourcePath ?? string.Empty,
            string.IsNullOrWhiteSpace(importMethod) ? "Import" : importMethod.Trim()));
        AutomationDiagnosticLog.Write(
            "IMPORT-COMMIT",
            $"method={importMethod}; client={clientId}; fy={financialYear}; module={module}; records={Math.Max(0, recordCount):N0}",
            sourcePath);
    }

    private int ReadCommittedAuthorityCount(long clientId, string financialYear, string module)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return ReadCommittedAuthorityCountCore(clientId, financialYear, module);
            }
            catch (Exception exception)
            {
                lastError = exception;
                if (attempt < 3)
                    System.Threading.Thread.Sleep(90 * attempt);
            }
        }

        throw new InvalidOperationException(
            $"The durable {module} authority could not be read back after commit for Client {clientId} / FY {financialYear}.",
            lastError);
    }

    private int ReadCommittedAuthorityCountCore(long clientId, string financialYear, string module)
    {
        if (module.Equals("Banking", StringComparison.OrdinalIgnoreCase))
            return _bankingRepository.GetTransactions(clientId, financialYear, includeArchived: true).Count;
        if (module.Equals("Cash", StringComparison.OrdinalIgnoreCase))
            return _bankingRepository.GetCashEntries(clientId, financialYear).Count;
        if (module.Equals("Purchase", StringComparison.OrdinalIgnoreCase))
            return _purchaseRecordRepository.GetRecords(clientId, financialYear).Count;
        if (module.Equals("Sales", StringComparison.OrdinalIgnoreCase))
            return _salesRecordRepository.GetRecords(clientId, financialYear).Count;
        if (module.Equals("Journal", StringComparison.OrdinalIgnoreCase))
            return _journalRepository.GetVouchers(clientId, financialYear).Count;
        if (module.Equals("Masters", StringComparison.OrdinalIgnoreCase))
            return _masterRepository.Get(clientId, financialYear, includeArchived: true, searchText: string.Empty).Count;
        if (module.Equals("Stock", StringComparison.OrdinalIgnoreCase))
            return _stockMasterRepository.GetStockItems(clientId, financialYear, includeArchived: true, searchText: string.Empty).Count;
        if (module.Equals("GST", StringComparison.OrdinalIgnoreCase))
            return _gstRepository.GetReturns(clientId, financialYear).Count +
                   _gstRepository.GetLedgerPeriods(clientId, financialYear).Count +
                   _gstRepository.GetReconciliation(clientId, financialYear).Count;
        if (module.Equals("26AS", StringComparison.OrdinalIgnoreCase))
            return _taxCreditRepository.GetCurrentRows(clientId, financialYear, "26AS").Count;
        return 1;
    }

    private void RaiseAccountingDataChanged(AutomationFolderRegistration folder, string module, int recordCount, string sourcePath) =>
        PublishCommittedAccountingImport(
            folder.ClientId,
            folder.FinancialYear,
            module,
            recordCount,
            sourcePath,
            folder.FolderId.StartsWith("SYSTEM-DUMP-", StringComparison.OrdinalIgnoreCase) ? "Universal Dump" : "Auto Import");

    private static string ProgressKey(long clientId, string financialYear) => $"{clientId}|{financialYear.Trim().ToUpperInvariant()}";

    private void RegisterQueuedProgress(QueuedAutomationFile item)
    {
        var counter = _batchProgress.GetOrAdd(ProgressKey(item.ClientId, item.FinancialYear), _ => new AutomationBatchCounter());
        lock (counter)
        {
            if (counter.Completed >= counter.Total)
            {
                counter.Total = 0;
                counter.Completed = 0;
            }
            counter.Total++;
        }
    }

    private void RegisterCompletedProgress(QueuedAutomationFile item)
    {
        var counter = _batchProgress.GetOrAdd(ProgressKey(item.ClientId, item.FinancialYear), _ => new AutomationBatchCounter());
        lock (counter)
        {
            if (counter.Total <= 0) counter.Total = 1;
            counter.Completed = Math.Min(counter.Total, counter.Completed + 1);
        }
    }

    private bool AddAction(
        AutomationFolderRegistration folder,
        string module,
        string actionType,
        string title,
        string detail,
        string sourceKey)
    {
        // Global Action Required is strictly for files/imports that did not
        // complete successfully. Normal review stays in the owning module.
        if (!ActionRequiredPolicy.IsImportBlockingType(actionType)) return false;

        // Hub-generated review/diagnostic outputs can sit inside the same linked
        // Automation Folder. They are never accounting source files, so even an old
        // classifier/error path must not create an Action Required item for them.
        var actionSourcePath = ExtractSourcePathForAction(detail);
        if (ClientDiagnosticExportService.IsHubGeneratedExportPath(title) ||
            (actionSourcePath.Length > 0 && ClientDiagnosticExportService.IsHubGeneratedExportPath(actionSourcePath)))
            return false;

        var durableSourceKey = $"AUTOMATION:{sourceKey}";
        var alreadyOpen = _actionRepository
            .GetOpen(folder.ClientId, folder.FinancialYear, module)
            .Any(item => item.SourceKey.Equals(durableSourceKey, StringComparison.OrdinalIgnoreCase));

        AutomationDiagnosticLog.Write("ACTION", $"module={module} type={actionType}; {detail}", title);
        _actionRepository.Upsert(new ActionRequiredItem
        {
            ClientId = folder.ClientId,
            FinancialYear = folder.FinancialYear,
            Module = module,
            ActionType = actionType,
            Title = title,
            Detail = detail,
            SourceKey = durableSourceKey
        });
        ActionRequiredChanged?.Invoke(this, EventArgs.Empty);
        return !alreadyOpen;
    }

    private static string ExtractSourcePathForAction(string detail)
    {
        foreach (var line in (detail ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Source:", StringComparison.OrdinalIgnoreCase))
                return trimmed["Source:".Length..].Trim();
        }
        return string.Empty;
    }

    private void ResolveAutomationAction(
        AutomationFolderRegistration folder,
        string module,
        string sourceKey)
    {
        foreach (var candidateModule in new[] { module, "Automation", "Banking", "Sales", "Purchase" }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _actionRepository.ResolveBySourceKey(
                folder.ClientId,
                folder.FinancialYear,
                candidateModule,
                $"AUTOMATION:{sourceKey}");
        }
        ActionRequiredChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResolveGeneratedWorkbookActionsForPath(
        AutomationFolderRegistration folder,
        string path,
        params string[] modules)
    {
        var resolved = false;
        foreach (var module in modules.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var action in _actionRepository.GetOpen(folder.ClientId, folder.FinancialYear, module))
            {
                // Generated Journal/Suspense/Cash workbooks can intentionally share the
                // same Client Name filename in different subfolders. Resolve only an
                // Action Required row whose persisted Source path is this exact workbook;
                // filename-only cleanup could hide an unrelated format/action.
                var actionPath = ExtractSourcePathForAction(action.Detail);
                if (actionPath.Length == 0 || !PathsEqual(actionPath, path)) continue;

                _actionRepository.Resolve(action.PersistentId);
                resolved = true;
            }
        }
        if (resolved) ActionRequiredChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Notify(string title, string message, bool isActionRequired, string clientName = "", string module = "") =>
        NotificationRequested?.Invoke(
            this,
            new AutomationNotificationEventArgs(title, message, isActionRequired, clientName, module));

    private bool IsAllowedByRoot(string path)
    {
        var mode = _settings.Get(ApplicationSettingKeys.AutomationRootMode);
        if (!mode.Equals("Selected Root", StringComparison.OrdinalIgnoreCase)) return true;
        var root = _settings.Get(ApplicationSettingKeys.AutomationRootFolder);
        if (string.IsNullOrWhiteSpace(root)) return true;
        try
        {
            root = NormalizePath(root);
            path = NormalizePath(path);
            return path.Equals(root, StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string GetConfiguredFolderName() =>
        SanitizeFolderName(_settings.Get(ApplicationSettingKeys.AutomationFolderName)) is { Length: > 0 } name
            ? name
            : "XYZ";

    private string GenerateNextClientCode()
    {
        var used = _clientRepository.GetAll(includeArchived: true)
            .Select(client => client.ClientCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = 1; number <= 99999; number++)
        {
            var candidate = $"CL{number:000}";
            if (!used.Contains(candidate)) return candidate;
        }
        return $"CL{DateTime.Now:HHmmss}";
    }

    private static void WriteMarker(string folderPath, string folderId)
    {
        var marker = Path.Combine(folderPath, MarkerFileName);
        File.WriteAllText(marker, JsonSerializer.Serialize(new FolderMarker { FolderId = folderId, Version = 1 }));
        if (OperatingSystem.IsWindows())
        {
            try { File.SetAttributes(marker, FileAttributes.Hidden | FileAttributes.System); } catch { }
        }
    }

    private static string ReadMarker(string folderPath)
    {
        var marker = Path.Combine(folderPath, MarkerFileName);
        if (!File.Exists(marker)) return string.Empty;
        try
        {
            var payload = JsonSerializer.Deserialize<FolderMarker>(File.ReadAllText(marker));
            return payload?.FolderId?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsSupportedFile(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.Equals(MarkerFileName, StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase)) return false;
        // Hub-generated client review exports are round-trip review artifacts, not
        // Bank/Sales/Purchase source files. They are imported only through the
        // the dedicated automatic review-workbook delta-sync path and must never enter Auto Import.
        if (ClientDiagnosticExportService.IsHubGeneratedExportPath(path)) return false;

        // The external PDF pipeline can create helper/confirmation PDFs next to the
        // source while a user resolves an unknown layout. Those are pipeline artifacts,
        // not new bank statements, and must never be recursively auto-imported.
        var stem = Path.GetFileNameWithoutExtension(path);
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
            (stem.EndsWith("_CONFIRMED_LINED", StringComparison.OrdinalIgnoreCase) ||
             stem.EndsWith("_CONFIRMED_LINE", StringComparison.OrdinalIgnoreCase) ||
             stem.EndsWith("_CONFIRMED", StringComparison.OrdinalIgnoreCase)))
            return false;

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".pdf" or ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp" or ".webp"
            or ".xlsx" or ".xlsm" or ".xls" or ".csv" or ".txt" or ".tsv"
            or ".mt940" or ".940" or ".sta" or ".json" or ".zip" or ".xml" or ".ofx" or ".qif"
            or ".ods" or ".qbo" or ".iif";
    }

    private static string BuildDuplicateObservationKey(string contentHash, string path)
    {
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))));
        return $"DUP-{contentHash}-{pathHash[..12]}";
    }

    private static string ComputeHash(string path)
    {
        // R52 FIXED26: hashing is read-only and must not pin the source against a
        // parent/base-folder move. Preserve move/delete sharing for the full hash read.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string SafeHash(string path)
    {
        try { return File.Exists(path) ? ComputeHash(path) : string.Empty; }
        catch { return string.Empty; }
    }

    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full) ?? string.Empty;
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return full;
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return left.Equals(right, StringComparison.OrdinalIgnoreCase); }
    }

    private static string NormalizeGstin(string value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string SanitizeFolderName(string value)
    {
        var trimmed = (value ?? string.Empty).Trim().TrimEnd('.', ' ');
        if (trimmed.Length == 0) return string.Empty;
        return string.Concat(trimmed.Where(character => !Path.GetInvalidFileNameChars().Contains(character))).Trim();
    }

    // Release updater may request a graceful application exit only when no
    // queued/in-flight automation source, import or batch worker remains.
    // This is a read-only snapshot, not a signal to cancel active accounting.
    public bool HasPendingWorkForUpdate =>
        !_queue.IsEmpty ||
        !_queuedKeys.IsEmpty ||
        !_activeFiles.IsEmpty ||
        !_activeStages.IsEmpty ||
        !_gstBootstrapInFlight.IsEmpty ||
        !_bankCachedProcessors.IsEmpty ||
        !_salesPurchaseLiveQueue.IsEmpty ||
        !_salesPurchaseLiveQueuedKeys.IsEmpty ||
        !_salesPurchaseSourceInFlight.IsEmpty ||
        !_salesPurchaseSetProcessors.IsEmpty;

    public void Dispose()
    {
        Task[] pendingTasks;
        CancellationTokenSource? lifetime;
        lock (_lifecycleGate)
        {
            if (!_started) return;
            _started = false;
            lifetime = _lifetime;
            lifetime?.Cancel();

            // FIXED28: release every FileSystemWatcher before waiting on workers. This
            // prevents late events from extending process lifetime after the window closes.
            DisposeWatchers();
            DisposeCentralSalesPurchaseWatcher();
            try { _queueSignal.Release(Math.Max(1, _workerTasks.Length)); } catch { }
            try { _salesPurchaseLiveQueueSignal.Release(Math.Max(1, _salesPurchaseLiveWorkerTasks.Length)); } catch { }

            pendingTasks = _workerTasks
                .Concat(_salesPurchaseLiveWorkerTasks)
                .Concat(new[] { _reconciliationTask, _downloadsMaintenanceTask, _startupRecoveryTask, _salesPurchaseStartupScanTask }
                    .Where(task => task is not null)
                    .Select(task => task!))
                .Distinct()
                .ToArray();
        }

        if (_explorerThread is not null && _explorerThread.IsAlive)
            _explorerThread.Join(TimeSpan.FromSeconds(3));
        _explorerThread = null;

        try
        {
            if (pendingTasks.Length > 0) Task.WaitAll(pendingTasks, TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Cancellation/fault during shutdown must never keep the desktop process alive.
        }

        lock (_lifecycleGate)
        {
            _workerTasks = [];
            _salesPurchaseLiveWorkerTasks = [];
            _downloadsMaintenanceTask = null;
            _startupRecoveryTask = null;
            _reconciliationTask = null;
            _salesPurchaseStartupScanTask = null;
            _queue.Clear();
            _queuedKeys.Clear();
            _activeFiles.Clear();
            _activeStages.Clear();
            _observedFileStates.Clear();
            _salesPurchaseObservedStates.Clear();
            _salesPurchaseLiveQueue.Clear();
            _salesPurchaseLiveQueuedKeys.Clear();
            _salesPurchaseSourceInFlight.Clear();
            _lifetime = null;
        }

        lifetime?.Dispose();
        _queueSignal.Dispose();
        _salesPurchaseLiveQueueSignal.Dispose();
        _bankImportGate.Dispose();
        _bankOcrAutoBatchGate.Dispose();
        _salesPurchaseIndexGate.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private sealed record AutomationActiveStage(
        string QueueKey,
        string FolderId,
        long ClientId,
        string FinancialYear,
        string Stage,
        string FileName,
        string Path,
        DateTime UpdatedUtc);
    private sealed record QueuedAutomationFile(
        string FolderId,
        long ClientId,
        string FinancialYear,
        string Path,
        string QueueKey,
        bool ManualReimport,
        AutomationFileKind? ForcedKind = null,
        string PrecomputedHash = "");
    private sealed class AutomationBatchCounter
    {
        public int Total;
        public int Completed;
    }
    private sealed class FolderMarker
    {
        public string FolderId { get; set; } = string.Empty;
        public int Version { get; set; }
    }
}

public sealed record AutomationDashboardMetric(string Label, int Value, string Detail);

public sealed record AutomationDashboardOutput(
    long ClientId,
    string FinancialYear,
    DateTime PreparedUtc,
    IReadOnlyList<AutomationDashboardMetric> Metrics,
    IReadOnlyList<AutomationFileHistoryRecord> RecentFiles,
    int SuccessfulFiles,
    int TransactionCount,
    int ErrorCount)
{
    public static AutomationDashboardOutput Empty(long clientId, string financialYear) =>
        new(clientId, financialYear ?? string.Empty, DateTime.MinValue, [], [], 0, 0, 0);
}

public sealed class PortalCredentialsChangedEventArgs(long clientId, string financialYear, string sourcePath) : EventArgs
{
    public long ClientId { get; } = clientId;
    public string FinancialYear { get; } = financialYear ?? string.Empty;
    public string SourcePath { get; } = sourcePath ?? string.Empty;
}

public sealed class AutomationFolderRegistrationRequestEventArgs(string folderPath, long preferredClientId = 0) : EventArgs
{
    public string FolderPath { get; } = folderPath;
    public long PreferredClientId { get; } = preferredClientId;
}

public sealed class AutomationNotificationEventArgs(string title, string message, bool isActionRequired, string clientName = "", string module = "") : EventArgs
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public bool IsActionRequired { get; } = isActionRequired;
    public string ClientName { get; } = clientName;
    public string Module { get; } = module;
}

public sealed record AutomationCommandResult(
    bool WasSuccessful,
    string Message,
    string Path,
    bool CommitVerified = false,
    int CommittedRecordCount = 0,
    string IssueCode = "")
{
    public static AutomationCommandResult Success(string path) => new(true, string.Empty, path);
    public static AutomationCommandResult Failed(string message, string issueCode = "") => new(false, message, string.Empty, false, 0, issueCode);
}

public sealed record AutomationManualImportResult(
    bool WasSuccessful,
    int FolderCount,
    int CandidateCount,
    int QueuedCount,
    string Message);

public sealed record UniversalDumpImportResult(
    bool WasSuccessful,
    string RunId,
    int CandidateCount,
    int QueuedCount,
    int SkippedCount,
    string Message);

public sealed record AutomationRenameResult(int Renamed, int Skipped, IReadOnlyList<string> Issues);


public sealed record AutomationAccountingDataChangedEventArgs(
    long ClientId,
    string FinancialYear,
    string Module,
    int RecordCount,
    string SourcePath,
    string ImportMethod = "Auto Import");

public sealed class ClientReviewWorkbookChangedEventArgs : EventArgs
{
    public ClientReviewWorkbookChangedEventArgs(long clientId, string financialYear, string workbookPath)
    {
        ClientId = clientId;
        FinancialYear = financialYear;
        WorkbookPath = workbookPath;
    }

    public long ClientId { get; }
    public string FinancialYear { get; }
    public string WorkbookPath { get; }
}

public sealed record AutomationProgressSnapshot(
    int Total,
    int Completed,
    int Pending,
    int Active,
    int Queued,
    double Percent,
    bool IsActive,
    string CurrentStage,
    string CurrentFileName);
