using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Gst;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Persistence;
using AccountingAutomationHub.Infrastructure.Tally;

namespace AccountingAutomationHub.Desktop.Services;

public sealed class TallyLiveConnectionCoordinator :
    ViewModelBase,
    IDisposable
{
    private readonly ActiveClientContext _clientContext;
    private readonly SqliteClientRepository _clientRepository;
    private readonly SqliteTallyRepository _repository;
    private readonly SqliteMasterRepository _masterRepository = new();
    private readonly SqliteStockMasterRepository _stockMasterRepository;
    private readonly SqliteClientPreferenceRepository _clientPreferenceRepository = new();
    private readonly TallyHttpService _httpService;
    private readonly TallyLiveMasterSynchronizationService _masterSyncService;
    private readonly TallyBankVoucherImportService _bankImportService;
    private readonly TallyAccountingVoucherExportService _accountingExportService;
    private readonly TallyBankStatementReconciliationService _bankStatementReconciliationService;
    private readonly AccountingRefreshCoordinator _accountingRefreshCoordinator;
    private readonly BackgroundAutomationService _automationService;
    private readonly AccountingLiveChangeHub _liveChangeHub;
    private readonly TallyApplicationLaunchService _applicationLaunchService = new();
    private readonly TallyPrivateCompanySessionService _privateSessions = TallyPrivateCompanySessionService.Shared;
    private int _autoSessionScheduled;
    private DateTime _lastAutomaticSessionAttemptUtc = DateTime.MinValue;
    // Only failed launch attempts are backed off; successful sessions never delay
    // genuinely new prepared work after Tally is closed.
    private DateTime _nextFailedAutoOpenRetryUtc = DateTime.MinValue;
    private long _confirmedIdlePulseClientId;
    private string _confirmedIdlePulseFy = string.Empty;
    private string _confirmedIdlePulseCompanyKey = string.Empty;
    private DateTime _confirmedIdlePulseUtc = DateTime.MinValue;
    private long _confirmedIdlePulseAlterId = -1;
    private long _outboundReadyClientId;
    private string _outboundReadyFy = string.Empty;
    // Fix27: a successful automatic Process.Start consumes this READY edge.
    // If Tally crashes or is closed before the common exporter finishes, a
    // repeated five-second heartbeat must not relaunch the same work forever.
    // The next completed export/new explicit READY edge resets this guard.
    private int _automaticLaunchConsumedForReady;
    private string _automaticReadySourceRevision = string.Empty;
    private long _outboundModeClientId;
    private string _outboundModeFy = string.Empty;
    private bool _outboundModeEnabled;
    private Func<ClientProfile, bool>? _outboundAutomaticWorkPending;
    public int PreparedAccountingXmlReadyCount => _lastLiveXmlReadyVouchers;

    // Signalled only by automatic outbound selection once local preparation is ready.
    // Manual Export never calls this, and no XML is sent here.
    public void SetOutboundAutoExportReady(ClientProfile client, bool ready, string? desiredSourceRevision = null)
    {
        if (ready)
        {
            // A stale callback from a switched-away client cannot re-arm launch.
            if (_clientContext.CurrentClient is not { } active || active.Id != client.Id ||
                !active.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;
            // Reset the consumed-launch barrier ONLY for a genuinely different
            // locally verified voucher/source revision, not for repeated 5s pulses.
            var revision = desiredSourceRevision ?? string.Empty;
            if (_outboundReadyClientId != client.Id ||
                !_outboundReadyFy.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase) ||
                !_automaticReadySourceRevision.Equals(revision, StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _automaticLaunchConsumedForReady, 0);
                _automaticReadySourceRevision = revision;
            }
            _outboundReadyClientId = client.Id;
            _outboundReadyFy = client.FinancialYear;
            MaybeScheduleAutomaticTallySession();
        }
        else if (_outboundReadyClientId == client.Id &&
                 _outboundReadyFy.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
        {
            _outboundReadyClientId = 0;
            _outboundReadyFy = string.Empty;
            _automaticReadySourceRevision = string.Empty;
            Interlocked.Exchange(ref _automaticLaunchConsumedForReady, 0);
        }
    }

    // Immediate in-memory state closes the race with the asynchronously persisted
    // Dashboard selection when Auto Sync is toggled ON/OFF.
    public void SetOutboundAutoSyncEnabled(ClientProfile client, bool enabled)
    {
        _outboundModeClientId = client.Id;
        _outboundModeFy = client.FinancialYear;
        _outboundModeEnabled = enabled;
        if (!enabled) SetOutboundAutoExportReady(client, false);
    }

    // The same per-Client/FY outbound toggle governs both session actions.
    // Do not infer session permission from the separate inbound Auto Tally Import flag.
    private bool IsOutboundAutoSyncEnabled(ClientProfile client)
    {
        if (_outboundModeClientId == client.Id &&
            _outboundModeFy.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            return _outboundModeEnabled;
        var mode = _clientPreferenceRepository.Get(client.Id,
            $"TallyExport.{client.FinancialYear.Trim()}.AutoSync.Mode").Trim();
        return mode.Equals("Selected", StringComparison.OrdinalIgnoreCase) ||
               mode.Equals("Full Auto", StringComparison.OrdinalIgnoreCase);
    }

    public void AttachOutboundAutomaticWorkProvider(Func<ClientProfile, bool> provider) =>
        _outboundAutomaticWorkPending = provider;

    private bool HasPendingOutboundWork(ClientProfile client)
    {
        try { return _outboundAutomaticWorkPending?.Invoke(client) == true; }
        catch { return true; } // Unknown state cannot authorize Auto Close.
    }

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _liveXmlTimer;
    private readonly SemaphoreSlim _tallyOperationGate = new(1, 1);
    private readonly SemaphoreSlim _liveXmlGate = new(1, 1);
    // FIX4 CORE: connection-cycle full snapshots are scheduled independently from the
    // foreground connection probe. This keeps the desktop responsive while Tally builds
    // the one complete snapshot response. Only one background synchronization may exist.
    private int _backgroundTallySyncScheduled;
    private int _backgroundReportAuthorityScheduled;
    // FIXED41 R8 Repair 4 safety bars: failures in low-priority background XML must not
    // immediately create another expensive timeout. Healthy Tally keeps the normal 5-second
    // authority pulse; only failed background transport/report attempts receive a short retry
    // cooldown, while manual import/export/refresh paths remain available immediately.
    private DateTime _backgroundXmlRetryNotBeforeUtc = DateTime.MinValue;
    private DateTime _reportWarmupRetryNotBeforeUtc = DateTime.MinValue;
    private long _liveXmlGeneration;
    private int _consecutiveConnectionFailures;
    private int _consecutiveEmptyDiscoveries;
    private DateTime _lastSuccessfulConnectionUtc = DateTime.MinValue;
    private readonly TallyLiveCompanyLocationReader _liveCompanyLocationReader = new();
    private DateTime _lastLiveCompanyFolderCheckUtc = DateTime.MinValue;
    private string _lastLiveCompanyFolderScope = string.Empty;
    private DateTime _lastFullDiscoveryUtc = DateTime.MinValue;
    private string _lastAvailableLinkedCompanyKey = string.Empty;
    private string _lastErp9CoreMasterFingerprint = string.Empty;
    private string _lastErp9CoreMasterScope = string.Empty;
    // Fix131: ERP9 lightweight stock counts are not reliable enough to reuse a previous
    // application's zero-stock proof forever. Every app session verifies Stock authority
    // once per Client/FY/company with a real Master read.
    private readonly ConcurrentDictionary<string, byte> _erp9StockAuthorityVerifiedThisSession = new(StringComparer.OrdinalIgnoreCase);
    private Func<bool>? _localPreparationReadyProvider;
    // FIXED41: startup keeps the Tally-last ordering, but once local preparation has
    // completed once, ordinary Auto Import/local cache work must never suppress the
    // five-second ALTERID authority loop.
    private bool _initialLocalPreparationCompleted;

    private TallyCompanyInfo? _selectedCompany;
    private string _connectionStatus = "Tally: checking…";
    private string _lastOperationStatus = "Waiting for Tally.";
    private bool _isConnected;
    private bool _isBusy;
    private bool _manualSelectionPending;
    private bool _disposed;
    private string _lastLiveXmlPath = string.Empty;
    private string _liveXmlStatus = "Live Tally XML waiting for accounting data.";
    private int _lastLiveXmlReadyVouchers;
    private int _lastLiveXmlNewMasters;

    public TallyLiveConnectionCoordinator(
        ActiveClientContext clientContext,
        SqliteClientRepository clientRepository,
        SqliteTallyRepository repository,
        SqliteStockMasterRepository stockMasterRepository,
        TallyHttpService httpService,
        TallyLiveMasterSynchronizationService masterSyncService,
        TallyBankVoucherImportService bankImportService,
        TallyAccountingVoucherExportService accountingExportService,
        TallyBankStatementReconciliationService bankStatementReconciliationService,
        AccountingRefreshCoordinator accountingRefreshCoordinator,
        BackgroundAutomationService automationService,
        AccountingLiveChangeHub liveChangeHub)
    {
        _clientContext = clientContext;
        _clientRepository = clientRepository;
        _repository = repository;
        _stockMasterRepository = stockMasterRepository;
        _masterRepository.Initialize();
        _stockMasterRepository.Initialize();
        _clientPreferenceRepository.Initialize();
        _httpService = httpService;
        _masterSyncService = masterSyncService;
        _bankImportService = bankImportService;
        _accountingExportService = accountingExportService;
        _bankStatementReconciliationService = bankStatementReconciliationService;
        _accountingRefreshCoordinator = accountingRefreshCoordinator;
        _automationService = automationService;
        _liveChangeHub = liveChangeHub;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _timer.Tick += OnTimerTick;

        _liveXmlTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(2)
        };
        _liveXmlTimer.Tick += OnLiveXmlTimerTick;

        _clientContext.PropertyChanged += OnClientContextChanged;
        _accountingRefreshCoordinator.Refreshed += OnAccountingRefreshed;
    }

    public ObservableCollection<TallyCompanyInfo> OpenCompanies { get; } = [];

    public event EventHandler? Changed;

    public TallyCompanyInfo? SelectedCompany
    {
        get => _selectedCompany;
        set
        {
            if (!SetProperty(ref _selectedCompany, value))
            {
                return;
            }

            _manualSelectionPending = value is not null;
            OnPropertyChanged(nameof(HasSelectedCompany));
            Changed?.Invoke(this, EventArgs.Empty);
            QueueLiveXmlGeneration();
        }
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public string LastOperationStatus
    {
        get => _lastOperationStatus;
        private set => SetProperty(ref _lastOperationStatus, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool HasSelectedCompany => SelectedCompany is not null;
    public bool IsErp9CompatibilityMode => _httpService.IsLocalErp9Runtime;
    public string LastDiagnosticReportPath { get; private set; } = string.Empty;

    public int LastLiveXmlReadyVouchers
    {
        get => _lastLiveXmlReadyVouchers;
        private set => SetProperty(ref _lastLiveXmlReadyVouchers, value);
    }

    public int LastLiveXmlNewMasters
    {
        get => _lastLiveXmlNewMasters;
        private set => SetProperty(ref _lastLiveXmlNewMasters, value);
    }

    public string LastLiveXmlPath
    {
        get => _lastLiveXmlPath;
        private set => SetProperty(ref _lastLiveXmlPath, value);
    }

    public string LiveXmlStatus
    {
        get => _liveXmlStatus;
        private set => SetProperty(ref _liveXmlStatus, value);
    }

    public void AttachLocalPreparationReadyProvider(Func<bool> provider)
    {
        _localPreparationReadyProvider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    private bool IsLocalPreparationReady()
    {
        try { return _localPreparationReadyProvider?.Invoke() != false; }
        catch { return true; }
    }

    private bool IsInitialLocalPreparationReady()
    {
        if (_initialLocalPreparationCompleted) return true;
        if (!IsLocalPreparationReady()) return false;
        _initialLocalPreparationCompleted = true;
        return true;
    }

    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Start();
        _ = RefreshNowAsync(forceLinkedSynchronization: false);
    }

    public async Task RefreshNowAsync(
        bool forceLinkedSynchronization,
        CancellationToken cancellationToken = default,
        bool isFiveSecondBackgroundPulse = false)
    {
        if (_disposed) return;
        cancellationToken.ThrowIfCancellationRequested();

        // FIX4 CORE: an intentional AAH -> Tally IMPORT is not a failed health probe.
        // Do not queue a competing request while Tally is processing the import and do
        // not turn the last confirmed connection into "offline/not responding".
        if (_httpService.IsImportInProgress || _httpService.IsExportInProgress)
        {
            IsConnected = true;
            if (_httpService.IsLocalErp9Runtime)
            {
                ConnectionStatus = _httpService.IsImportInProgress
                    ? "Tally.ERP 9 linked • waiting for import response"
                    : "Tally.ERP 9 linked • waiting for response";
                LastOperationStatus = "ERP 9 request is still in progress. AAH will wait for Tally to return success or an actual failure before any retry; the 5-second pulse is skipped and no overlapping XML request is sent.";
            }
            else
            {
                ConnectionStatus = "Tally connected • busy";
                LastOperationStatus = "Fix37 5-second pulse skipped because another AAH Tally request is active; Tally is not held by the health check.";
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        // ERP 9 CORE LIVE MODE: unlike the previous process-only hold mode, every
        // normal timer cycle performs the bounded current-company compatibility probe.
        // When the linked ERP 9 company is open, the normal authority pulse covers
        // Ledger/Group plus Stock/Unit/Godown/VoucherType counts and queues the shared
        // background baseline/delta when any required authority is incomplete. Prime
        // keeps the established discovery pipeline.

        // Serialize all Hub traffic sent to Tally's single HTTP/XML endpoint. A
        // background health probe must not compete with voucher export, report read,
        // or master synchronization.
        var gateEntered = forceLinkedSynchronization
            ? await WaitForTallyOperationAsync()
            : await _tallyOperationGate.WaitAsync(0);
        if (!gateEntered) return;

        var exposeBusy = forceLinkedSynchronization;
        if (exposeBusy) IsBusy = true;
        var wasConnected = IsConnected;
        try
        {
            var nowUtc = DateTime.UtcNow;
            // Fix37: company discovery is identity-only. Object counts + ALTERID arrive
            // in the one bundled authority pulse below, avoiding separate count traffic.
            var discovered = await _httpService.DiscoverCompaniesAsync(
                cancellationToken,
                includeLightweightCounts: false);
            _lastFullDiscoveryUtc = nowUtc;

            _consecutiveConnectionFailures = 0;
            _lastSuccessfulConnectionUtc = nowUtc;
            if (isFiveSecondBackgroundPulse) _backgroundXmlRetryNotBeforeUtc = DateTime.MinValue;
            IsConnected = true;

            if (discovered.Count == 0 && OpenCompanies.Count > 0)
            {
                _consecutiveEmptyDiscoveries++;
                if (_consecutiveEmptyDiscoveries < 3)
                {
                    ConnectionStatus = "Tally connected • confirming open company";
                    LastOperationStatus = "Tally returned an empty company list once; retaining the confirmed link while it is rechecked.";
                    return;
                }
            }
            else if (discovered.Count > 0)
            {
                _consecutiveEmptyDiscoveries = 0;
            }

            var client = _clientContext.CurrentClient;
            var confirmedLink = client is null ? null : _repository.GetLinkForClient(client.Id);
            ConnectionStatus = discovered.Count == 0
                ? confirmedLink is not null
                    ? $"Tally linked • {confirmedLink.CompanyName} • company not open"
                    : client is null
                        ? "Tally server running • no company open"
                        : "Tally server running • client not linked"
                : client is null
                    ? "Tally server running • no client open"
                    : confirmedLink is null
                        ? "Tally server running • client not linked"
                        : $"Tally linked • {confirmedLink.CompanyName} • checking company";

            ApplyCompanyList(discovered);
            LastOperationStatus = discovered.Count == 0
                ? "Tally is running, but no company is open."
                : $"Refreshed {discovered.Count:N0} open Tally company(s) at {DateTime.Now:HH:mm:ss}.";

            if (client is null && discovered.Count > 0 && IsInitialLocalPreparationReady())
            {
                // Fix38: startup Tally is its own final phase. With no workspace open,
                // synchronize active linked clients inside the Tally sequence instead of
                // making client-open the trigger for Tally authority work.
                QueueBackgroundLinkedClientSequence(OpenCompanies.ToArray(), "Startup Tally client sequence");
            }
            if (client is not null)
            {
                var link = confirmedLink;
                var linkedCompany = link is null
                    ? null
                    : OpenCompanies.FirstOrDefault(company =>
                        company.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase));

                if (linkedCompany is not null &&
                    (!_manualSelectionPending || SelectedCompany is null))
                    SetSelectionFromRememberedLink(linkedCompany);

                if (link is not null && linkedCompany is not null)
                {
                    // Fix110 R1: transport availability and client authority are separate.
                    // Once the confirmed CompanyKey is actually open, identify that linked
                    // company explicitly instead of leaving the generic company-count text.
                    ConnectionStatus = $"Tally linked • {link.CompanyName}";
                    var marker = await _httpService.GetVoucherChangeMarkerAsync(linkedCompany, cancellationToken);
                    linkedCompany.ChangeToken = marker.Token;
                    linkedCompany.VoucherCount = marker.VoucherCount;
                    linkedCompany.LedgerCount = marker.LedgerCount;
                    linkedCompany.GroupCount = marker.GroupCount;
                    linkedCompany.VoucherTypeCount = marker.VoucherTypeCount;
                    linkedCompany.StockItemCount = marker.StockItemCount;
                    linkedCompany.UnitCount = marker.UnitCount;
                    linkedCompany.GodownCount = marker.GodownCount;
                    var state = _repository.GetBankSyncState(client.Id, client.FinancialYear, linkedCompany.CompanyKey);
                    var becameConnected = !wasConnected;
                    var linkedCompanyBecameAvailable = !_lastAvailableLinkedCompanyKey.Equals(
                        linkedCompany.CompanyKey, StringComparison.OrdinalIgnoreCase);
                    _lastAvailableLinkedCompanyKey = linkedCompany.CompanyKey;
                    // Fix10: a newly opened linked company gets first priority for
                    // live location capture. Later connected refreshes revalidate once
                    // per minute, not on every five-second Master/Voucher pulse.
                    // The normal serialized XML exit is already held here.
                    var pathScope = $"{client.Id}|{client.FinancialYear}|{link.CompanyKey}";
                    if (!_lastLiveCompanyFolderScope.Equals(pathScope, StringComparison.OrdinalIgnoreCase) ||
                        linkedCompanyBecameAvailable ||
                        nowUtc - _lastLiveCompanyFolderCheckUtc > TimeSpan.FromMinutes(1))
                    {
                        _lastLiveCompanyFolderScope = pathScope;
                        _lastLiveCompanyFolderCheckUtc = nowUtc;
                        try
                        {
                            var location = await _liveCompanyLocationReader.ReadLiveCompanyLocationAsync(linkedCompany.Name, cancellationToken);
                            if (location is not null)
                                _applicationLaunchService.SaveVerifiedLiveCompanyFolder(client, link, linkedCompany, location);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            // Optional metadata discovery must NEVER interrupt or
                            // reclassify the normal 5-second Tally authority cycle.
                            AutomationDiagnosticLog.Write("TALLY-LIVE-PATH", "Location probe unavailable; existing binding preserved: " + ex.Message, string.Empty);
                        }
                    }
                    var stockAuthorityReady = HasCompleteLiveStockMasterAuthority(client, linkedCompany, marker, state);
                    var accountingMasterAuthorityReady = HasCompleteLiveAccountingMasterAuthority(client, marker);
                    var voucherAuthorityReady = HasCompleteActiveFyVoucherAuthority(client, linkedCompany, state);
                    var needsBaseline = state is null || state.LastFullSyncUtc == DateTime.MinValue || state.LastAlterId <= 0 ||
                                        !stockAuthorityReady || !accountingMasterAuthorityReady || !voucherAuthorityReady;
                    var changed = state is null || !marker.Token.Equals(state.LastTallyChangeToken, StringComparison.Ordinal) ||
                                  (marker.HasReliableAlterId && marker.LastAlterId > state.LastAlterId);

                    // A normal 5-second Tally pulse that confirms the linked company,
                    // marker, and all local FY/Master/Stock authorities is an idle
                    // heartbeat. Do NOT write to the database just to count it.
                    // Other-company probes, failed requests, pending deltas and forced
                    // refreshes cannot count toward automatic shutdown.
                    if (isFiveSecondBackgroundPulse && !forceLinkedSynchronization &&
                        !changed && !needsBaseline && state is not null &&
                        marker.HasReliableAlterId && marker.LastAlterId == state.LastAlterId)
                    {
                        _confirmedIdlePulseClientId = client.Id;
                        _confirmedIdlePulseFy = client.FinancialYear;
                        _confirmedIdlePulseCompanyKey = linkedCompany.CompanyKey;
                        _confirmedIdlePulseAlterId = state.LastAlterId;
                        _confirmedIdlePulseUtc = DateTime.UtcNow;
                    }

                    if (forceLinkedSynchronization || becameConnected || linkedCompanyBecameAvailable || needsBaseline || changed)
                    {
                        var reason = forceLinkedSynchronization ? "Manual refresh" :
                            (becameConnected || linkedCompanyBecameAvailable) ? "Tally connected" :
                            !accountingMasterAuthorityReady ? "Tally accounting Master authority incomplete" :
                            !voucherAuthorityReady ? "Tally active-FY Voucher authority incomplete" :
                            !stockAuthorityReady ? "Tally Stock Master authority incomplete" :
                            needsBaseline ? "Initial authority baseline" : "Tally ALTERID update detected";
                        if (!forceLinkedSynchronization && !AutoTallyImportPreference.IsEnabled(_clientPreferenceRepository, client))
                        {
                            LastOperationStatus = $"Auto Tally Import is off for {client.ClientName} · FY {client.FinancialYear}; heartbeat remains connected but Master/Voucher import is paused for this client.";
                        }
                        else if (!IsInitialLocalPreparationReady())
                        {
                            // FIXED41: only the initial startup ordering may defer Tally. After
                            // that point a detected ALTERID/count mismatch is independent of
                            // ordinary local Auto Import/background queues and must be consumed.
                            LastOperationStatus = $"{reason}: initial local preparation is still completing; the next 5-second pulse will retry without advancing the Tally checkpoint.";
                        }
                        else if (forceLinkedSynchronization)
                        {
                            // Fix67: an explicit Refresh Tally command means exactly that:
                            // rebuild the current active-FY Company/Master/Voucher baseline from
                            // one fresh authority response. This both validates the current parser
                            // and clears a prior persisted synchronization error after success.
                            await SynchronizeLinkedCompanyAsync(
                                client, linkedCompany, reason, marker,
                                forceFullBaseline: forceLinkedSynchronization);
                        }
                        else
                        {
                            QueueBackgroundSynchronization(client, linkedCompany, reason, marker);
                        }
                    }
                    else
                    {
                        LastOperationStatus = $"Tally current • ALTERID {state!.LastAlterId:N0} • checked {DateTime.Now:HH:mm:ss}.";
                    }
                }
                else
                {
                    // Fix109: transport reachability is not client authority. If Tally is
                    // running with another company open, say so explicitly and do not make
                    // the current Accounting client look linked merely because HTTP works.
                    ConnectionStatus = link is null
                        ? "Tally server running • client not linked"
                        : $"Tally server running • linked company '{link.CompanyName}' not open";
                    LastOperationStatus = link is null
                        ? "Tally is reachable, but this Accounting client has no confirmed CompanyKey. Live Masters and vouchers from open companies are not adopted."
                        : $"The confirmed company '{link.CompanyName}' is not currently open. No other open company will be used as this client's authority.";

                    // Re-arm connection-cycle synchronization. If the linked company is
                    // closed and later opened again, its full Master authority is read
                    // again immediately instead of waiting for the five-minute stale timer.
                    _lastAvailableLinkedCompanyKey = string.Empty;
                }
            }
        }
        catch (OperationCanceledException) when (isFiveSecondBackgroundPulse && cancellationToken.IsCancellationRequested)
        {
            // Fix123 ERP9 non-blocking pulse: the five-second scheduler has a strict
            // time budget. If ERP 9 does not answer inside that budget, release the
            // coordinator immediately and retry on the next cycle. No Master, Voucher,
            // report or normal Tally feature is disabled; only this low-priority pulse yields.
            IsConnected = wasConnected || IsConnected;
            ConnectionStatus = IsConnected ? "Tally linked • background refresh yielded" : ConnectionStatus;
            LastOperationStatus = "5-second Tally refresh yielded without holding AAH; normal Tally processing remains enabled and will retry on the next cycle.";
        }
        catch (Exception exception)
        {
            if (_httpService.IsImportInProgress)
            {
                IsConnected = true;
                ConnectionStatus = "Tally connected • processing import";
                LastOperationStatus = "Tally is processing an intentional AAH import. Health state is preserved until the import finishes.";
                return;
            }

            _consecutiveConnectionFailures++;
            if (isFiveSecondBackgroundPulse)
            {
                // Retry soon enough to retain live behavior, but never launch a new 2-5 second
                // background timeout on every timer tick while Tally/XML is unavailable.
                var backoffSeconds = Math.Min(30, Math.Max(10, _consecutiveConnectionFailures * 5));
                _backgroundXmlRetryNotBeforeUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);
            }
            var recentlyHealthy = _lastSuccessfulConnectionUtc != DateTime.MinValue &&
                                  DateTime.UtcNow - _lastSuccessfulConnectionUtc < TimeSpan.FromSeconds(60);
            if (recentlyHealthy && _consecutiveConnectionFailures < 3)
            {
                IsConnected = true;
                ConnectionStatus = "Tally connected • retrying";
                LastOperationStatus =
                    $"Tally was temporarily busy; keeping the live link and retrying automatically. {exception.Message}";
            }
            else
            {
                IsConnected = false;
                _lastAvailableLinkedCompanyKey = string.Empty;
                ConnectionStatus = "Tally offline";
                LastOperationStatus = $"Tally auto-connect is waiting: {exception.Message}";
            }
            // Do not clear OpenCompanies/SelectedCompany on a failed probe. The old
            // clearing behavior made a remembered link disconnect for one poll and
            // reconnect on the next successful poll.
        }
        finally
        {
            if (exposeBusy) IsBusy = false;
            _tallyOperationGate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<bool> WaitForTallyOperationAsync(CancellationToken cancellationToken = default)
    {
        await _tallyOperationGate.WaitAsync(cancellationToken);
        return true;
    }

    private void QueueBackgroundLinkedClientSequence(
        IReadOnlyList<TallyCompanyInfo> openCompanies,
        string reason)
    {
        if (_disposed || openCompanies.Count == 0) return;
        if (Interlocked.CompareExchange(ref _backgroundTallySyncScheduled, 1, 0) != 0) return;

        ConnectionStatus = "Tally connected • client authority sequence queued";
        LastOperationStatus = $"{reason}: linked clients queued after local preparation.";
        Changed?.Invoke(this, EventArgs.Empty);
        _ = RunBackgroundLinkedClientSequenceAsync(openCompanies, reason);
    }

    private async Task RunBackgroundLinkedClientSequenceAsync(
        IReadOnlyList<TallyCompanyInfo> openCompanies,
        string reason)
    {
        try
        {
            await _tallyOperationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || !IsInitialLocalPreparationReady()) return;
                var now = DateTime.UtcNow;
                var clients = _repository.GetAllLinks()
                    .Select(link => new { Link = link, Client = _clientRepository.GetById(link.ClientId) })
                    .Where(item => item.Client is not null && !item.Client.IsArchived)
                    .Select(item => new
                    {
                        item.Link,
                        Client = item.Client!,
                        LastOpened = _clientRepository.GetWorkspaceLastOpenedUtc(item.Client!.Id, item.Client.FinancialYear)
                    })
                    .Where(item =>
                    {
                        var activity = item.LastOpened != DateTime.MinValue ? item.LastOpened : item.Client.UpdatedUtc;
                        return activity == DateTime.MinValue || now - activity <= TimeSpan.FromDays(183);
                    })
                    .OrderByDescending(item => item.LastOpened != DateTime.MinValue ? item.LastOpened : item.Client.UpdatedUtc)
                    .ToArray();

                foreach (var item in clients)
                {
                    if (_disposed || !IsInitialLocalPreparationReady()) break;
                    if (!AutoTallyImportPreference.IsEnabled(_clientPreferenceRepository, item.Client)) continue;
                    var company = openCompanies.FirstOrDefault(candidate =>
                        candidate.CompanyKey.Equals(item.Link.CompanyKey, StringComparison.OrdinalIgnoreCase));
                    if (company is null) continue;

                    try
                    {
                        var marker = await _httpService.GetVoucherChangeMarkerAsync(company).ConfigureAwait(false);
                        company.ChangeToken = marker.Token;
                        company.VoucherCount = marker.VoucherCount;
                        company.LedgerCount = marker.LedgerCount;
                        company.GroupCount = marker.GroupCount;
                        company.VoucherTypeCount = marker.VoucherTypeCount;
                        company.StockItemCount = marker.StockItemCount;
                        company.UnitCount = marker.UnitCount;
                        company.GodownCount = marker.GodownCount;
                        var state = _repository.GetBankSyncState(item.Client.Id, item.Client.FinancialYear, company.CompanyKey);
                        var stockAuthorityReady = HasCompleteLiveStockMasterAuthority(item.Client, company, marker, state);
                        var accountingMasterAuthorityReady = HasCompleteLiveAccountingMasterAuthority(item.Client, marker);
                        var voucherAuthorityReady = HasCompleteActiveFyVoucherAuthority(item.Client, company, state);
                        var needsBaseline = state is null || state.LastFullSyncUtc == DateTime.MinValue || state.LastAlterId <= 0 ||
                                            !stockAuthorityReady || !accountingMasterAuthorityReady || !voucherAuthorityReady;
                        var changed = state is null || !marker.Token.Equals(state.LastTallyChangeToken, StringComparison.Ordinal) ||
                                      (marker.HasReliableAlterId && marker.LastAlterId > state.LastAlterId);
                        if (!needsBaseline && !changed) continue;

                        await SynchronizeLinkedCompanyAsync(
                            item.Client, company, $"{reason} • {item.Client.ClientCode}", marker).ConfigureAwait(false);
                        try
                        {
                            await _bankStatementReconciliationService.PreloadLinkedVoucherCacheAsync(
                                item.Client, usePreSynchronizedAuthority: true).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            LastOperationStatus += $" Bank voucher reconciliation will retry locally: {exception.Message}";
                        }
                    }
                    catch (Exception exception)
                    {
                        LastOperationStatus = $"{reason}: {item.Client.ClientCode} deferred: {exception.Message}";
                    }
                }
            }
            finally
            {
                _tallyOperationGate.Release();
            }
        }
        catch (Exception exception)
        {
            IsConnected = true;
            ConnectionStatus = "Tally connected • client sequence retry pending";
            LastOperationStatus = $"Tally client sequence will retry: {exception.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _backgroundTallySyncScheduled, 0);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void QueueBackgroundSynchronization(
        ClientProfile client,
        TallyCompanyInfo company,
        string reason,
        TallyVoucherChangeMarker? marker = null)
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _backgroundTallySyncScheduled, 1, 0) != 0) return;

        ConnectionStatus = "Tally connected • background data sync queued";
        LastOperationStatus = $"{reason}: ALTERID authority update queued in background.";
        Changed?.Invoke(this, EventArgs.Empty);
        _ = RunBackgroundSynchronizationAsync(client, company, reason, marker);
    }

    private async Task RunBackgroundSynchronizationAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        string reason,
        TallyVoucherChangeMarker? marker)
    {
        try
        {
            await _tallyOperationGate.WaitAsync();
            try
            {
                if (_disposed) return;
                IsConnected = true;
                ConnectionStatus = "Tally connected • background data sync";
                await SynchronizeLinkedCompanyAsync(client, company, reason, marker);

                // Fix37: Bank reconciliation consumes the synchronized local authority.
                // It must not issue another broad Tally read after this delta/baseline merge.
                try
                {
                    var cached = await _bankStatementReconciliationService.PreloadLinkedVoucherCacheAsync(
                        client, usePreSynchronizedAuthority: true);
                    LastOperationStatus += $" Bank voucher reconciliation ready from synchronized authority: {cached:N0} voucher(s).";
                }
                catch (Exception exception)
                {
                    LastOperationStatus += $" Bank voucher reconciliation will retry locally: {exception.Message}";
                }
            }
            finally
            {
                _tallyOperationGate.Release();
            }
        }
        catch (Exception exception)
        {
            // A background extraction failure must not rewrite the confirmed live
            // connection as offline. The next connection cycle can retry the snapshot.
            IsConnected = true;
            ConnectionStatus = "Tally connected • background sync retry pending";
            LastOperationStatus = $"Background Tally synchronization will retry: {exception.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _backgroundTallySyncScheduled, 0);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Fix131 R3: explicit Stock Master diagnostic/import. This is intentionally
    // stock-only: no voucher baseline, no Sales/Purchase import and no accounting
    // Ledger/Group archive/rewrite. It forces a fresh ERP9 Master response so the
    // Stock Master screen can prove the live inventory path independently.
    public async Task<TallyStockPullResult> ImportCurrentClientStockMastersAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TallyLiveConnectionCoordinator));
        var client = _clientContext.CurrentClient
            ?? throw new InvalidOperationException("Open the Accounting client before importing Stock Masters from Tally.");
        var link = _repository.GetLinkForClient(client.Id)
            ?? throw new InvalidOperationException("This Accounting client is not linked to a Tally company.");

        await WaitForTallyOperationAsync(cancellationToken);
        IsBusy = true;
        try
        {
            var company = OpenCompanies.FirstOrDefault(item =>
                item.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase));
            if (company is null)
            {
                var discovered = await _httpService.DiscoverCompaniesAsync(
                    cancellationToken, includeLightweightCounts: false);
                ApplyCompanyList(discovered);
                company = OpenCompanies.FirstOrDefault(item =>
                    item.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase));
            }
            if (company is null)
                throw new InvalidOperationException($"Linked Tally company '{link.CompanyName}' is not currently open. Open that company in Tally and retry Stock Import.");
            var before = _stockMasterRepository.GetStockItems(
                client.Id, client.FinancialYear, includeArchived: true)
                .Where(item => item.Source.Equals("Live Tally", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            ConnectionStatus = "Tally connected • importing Stock Masters";
            LastOperationStatus = "Stock Master: requesting one fresh Tally Master response…";
            Changed?.Invoke(this, EventArgs.Empty);

            var xml = await _httpService.ExportStockMastersAsync(company, cancellationToken);
            var result = await Task.Run(() => _masterSyncService.SynchronizeStockOnly(
                xml, client.Id, client.FinancialYear, company.Name), cancellationToken);
            var after = _stockMasterRepository.GetStockItems(
                client.Id, client.FinancialYear, includeArchived: true)
                .Where(item => item.Source.Equals("Live Tally", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var beforeIds = before.Select(item => item.TallyMasterId.Trim())
                .Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var afterIds = after.Select(item => item.TallyMasterId.Trim())
                .Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newMasterIds = afterIds.Count(id => !beforeIds.Contains(id));

            if (_httpService.IsLocalErp9Runtime)
                _erp9StockAuthorityVerifiedThisSession[Erp9StockSessionKey(client, company)] = 0;
            if (result.StockItemCount > company.StockItemCount)
                company.StockItemCount = result.StockItemCount;

            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.StockMasters,
                "Manual Import Stock From Tally completed");
            LastOperationStatus = $"Stock Master import complete • Tally returned {result.StockItemCount:N0} item(s) / {result.UnitCount:N0} unit(s) / {result.GodownCount:N0} godown(s) • AAH Live Tally items {before.Length:N0} → {after.Length:N0}.";
            ConnectionStatus = $"Tally linked • {link.CompanyName}";
            return new TallyStockPullResult(
                result.StockItemCount, result.UnitCount, result.GodownCount,
                result.UpsertedObjects, before.Length, after.Length, newMasterIds, result.Status);
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Tally linked • {link.CompanyName} • Stock import failed";
            LastOperationStatus = $"Stock Master import failed: {ex.Message}";
            throw;
        }
        finally
        {
            IsBusy = false;
            _tallyOperationGate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<TallyClientLink> ConfirmSelectedCompanyAsync()
    {
        var client = _clientContext.CurrentClient
            ?? throw new InvalidOperationException("Open a client before confirming a Tally company.");
        var company = SelectedCompany
            ?? throw new InvalidOperationException("Select an open Tally company from the top dropdown first.");

        ValidateClientCompanyIdentity(client, company);
        await _tallyOperationGate.WaitAsync();
        IsBusy = true;
        try
        {
            var link = _repository.ConfirmLink(client.Id, company);
            // Repair 19: a confirmed Client/FY link also captures the bounded local Tally
            // executable/data-path metadata needed by the Dashboard Open Tally button.
            // This is metadata-only; it never writes Tally.ini or Tally company data.
            _applicationLaunchService.CaptureConfirmedLink(client, company);
            try
            {
                var verifiedLocation = await _liveCompanyLocationReader.ReadLiveCompanyLocationAsync(company.Name);
                if (verifiedLocation is not null)
                    _applicationLaunchService.SaveVerifiedLiveCompanyFolder(client, link, company, verifiedLocation);
            }
            catch (Exception ex)
            {
                // The logical Link operation succeeded. Missing optional path TDL
                // support must not invalidate that saved link or its import state.
                AutomationDiagnosticLog.Write("TALLY-LIVE-PATH", "Link saved; live location unavailable: " + ex.Message, string.Empty);
            }
            _manualSelectionPending = false;
            LastOperationStatus = _httpService.IsLocalErp9Runtime
                ? $"Linked {company.Name} to {client.ClientCode}. ERP 9 background synchronization queued…"
                : $"Linked {company.Name} to {client.ClientCode}. ALTERID authority synchronization queued in background…";
            Changed?.Invoke(this, EventArgs.Empty);

            // Confirming a link is a lightweight connection action. The baseline/delta
            // authority synchronization and Bank/Tally reconciliation are queued after
            // this method releases the foreground operation gate, so the UI does not
            // wait for Tally to build the full accounting response.
            QueueBackgroundSynchronization(client, company, "Link confirmed");
            EnrichCompanyLinks();
            QueueLiveXmlGeneration();
            Changed?.Invoke(this, EventArgs.Empty);
            return _repository.GetLinkForClient(client.Id) ?? link;
        }
        finally
        {
            IsBusy = false;
            _tallyOperationGate.Release();
        }
    }

    public void CancelPendingSelection()
    {
        _manualSelectionPending = false;
        _lastErp9CoreMasterScope = string.Empty;
        _lastErp9CoreMasterFingerprint = string.Empty;
        var client = _clientContext.CurrentClient;
        var link = client is null
            ? null
            : _repository.GetLinkForClient(client.Id);

        if (link is null)
        {
            SelectedCompany = null;
            _manualSelectionPending = false;
            LastOperationStatus = "Pending Tally company selection cleared.";
            return;
        }

        var linkedCompany = OpenCompanies.FirstOrDefault(company =>
            company.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase));
        SetSelectionFromRememberedLink(linkedCompany);
        LastOperationStatus = "Returned to the confirmed Tally company link.";
    }

    public void UnlinkCurrentClient()
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            return;
        }

        _repository.UnlinkClient(client.Id);
        _lastErp9CoreMasterScope = string.Empty;
        _lastErp9CoreMasterFingerprint = string.Empty;
        _manualSelectionPending = SelectedCompany is not null;
        LastOperationStatus =
            $"Tally company link removed from {client.ClientCode}. Existing accounting data and history were retained.";
        EnrichCompanyLinks();
        QueueLiveXmlGeneration();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<TallyMasterMergeUpdateResult> PrepareMasterMergeTargetAsync(
        MasterRecord source,
        MasterRecord target,
        decimal? mergedOpeningBalance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var client = _clientContext.CurrentClient
            ?? throw new InvalidOperationException("Open the client workspace before Merge + Update.");
        if (source.ClientId != client.Id || target.ClientId != client.Id ||
            !source.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase) ||
            !target.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected Masters do not belong to the current Client + FY.");

        var sourceIsTallyBacked = MasterAuthority.IsTallySource(source.Source);
        var link = _repository.GetLinkForClient(client.Id);
        if (link is null)
        {
            if (sourceIsTallyBacked)
                throw new InvalidOperationException($"Merge stopped because FROM '{source.Name}' is Tally-backed but this client has no confirmed Tally link. Link/open the authoritative company first.");
            return new TallyMasterMergeUpdateResult(false, false, false, "No confirmed Tally company link; the local-source merge will remain local until a Tally company is linked.",
                Deferred: false, PendingMergeId: 0, AllowTallySourceArchive: false);
        }
        // Fix52: Ledger Merge + Update is a durable AAH consolidation rule, not a
        // foreground Tally migration. Record FROM -> INTO immediately and let the
        // normal linked-company authority cycle apply that rule to any current/future
        // active-FY Voucher that still arrives with FROM. The complete Voucher XML is
        // already in the shared authority cache, so clicking Merge never asks Tally
        // to resend a Voucher and never blocks the local merge on a live Voucher ALTER.
        if (target.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
        {
            var localUsage = _masterRepository.GetCurrentMasterUsageCount(
                client.Id, client.FinancialYear, source.PersistentId);
            var pendingId = _masterRepository.QueuePendingTallyMasterMerge(
                client.Id, client.FinancialYear, link.CompanyKey, source, target, mergedOpeningBalance, localUsage);
            return new TallyMasterMergeUpdateResult(
                false, false, false,
                $"AAH recorded durable Ledger merge FROM '{source.Name}' INTO '{target.Name}'. Local references can merge now. The normal Tally authority pulse will automatically ALTER any cached/current-FY Voucher that still uses FROM, then retire FROM in Tally when no Voucher remains.",
                Deferred: true, PendingMergeId: pendingId, AllowTallySourceArchive: sourceIsTallyBacked);
        }

        var company = OpenCompanies.FirstOrDefault(item =>
            item.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase));
        if (company is null)
        {
            // Fix47: Ledger Merge + Update remains available while the confirmed Tally
            // company is closed even when FROM already has AAH entries. Local references
            // are consolidated now; the exact Tally Voucher migration is durably queued
            // and runs before inbound Master synchronization when the company next opens.
            var localUsage = _masterRepository.GetCurrentMasterUsageCount(
                client.Id, client.FinancialYear, source.PersistentId);
            if (sourceIsTallyBacked &&
                source.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                localUsage > 0)
                throw new InvalidOperationException(
                    $"Group merge while Tally is closed is still blocked because FROM '{source.Name}' has {localUsage:N0} child/reference(s). Ledger Voucher migration is automatic; Group child migration is not changed by Fix47.");

            var pendingId = _masterRepository.QueuePendingTallyMasterMerge(
                client.Id, client.FinancialYear, link.CompanyKey, source, target, mergedOpeningBalance, localUsage);
            return new TallyMasterMergeUpdateResult(
                false, false, false,
                $"Linked Tally company is closed. AAH will consolidate FROM '{source.Name}' INTO '{target.Name}' now; Tally merge #{pendingId:N0} is stored and will automatically move all live FROM Ledger vouchers before deleting that Ledger when the company opens.",
                Deferred: true, PendingMergeId: pendingId, AllowTallySourceArchive: sourceIsTallyBacked);
        }
        ValidateClientCompanyIdentity(client, company);

        await _tallyOperationGate.WaitAsync(cancellationToken);
        IsBusy = true;
        try
        {
            if (target.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ApplyLiveLedgerMasterMergeAsync(
                    client, company, source, target, mergedOpeningBalance, sourceIsTallyBacked, cancellationToken);
                LastOperationStatus = result.Status;
                Changed?.Invoke(this, EventArgs.Empty);
                return result;
            }

            // Fix51 complete-authority reuse: Groups/Ledgers were already collected in
            // the connection baseline and are maintained by the 5-second Master ALTERID
            // delta. Group merge must not issue another broad Group/Ledger read.
            var synchronizedTallyMasters = _masterRepository.Get(
                    client.Id, client.FinancialYear, includeArchived: false)
                .Where(master => MasterAuthority.IsTallySource(master.Source))
                .ToArray();
            var liveGroups = synchronizedTallyMasters
                .Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var targetAuthority = liveGroups.FirstOrDefault(master =>
                TallyMasterIdentity.Equivalent(master.Name, target.Name));
            var sourceAuthority = liveGroups.FirstOrDefault(master =>
                TallyMasterIdentity.Equivalent(master.Name, source.Name) &&
                !TallyMasterIdentity.Equivalent(master.Name, target.Name));
            var targetExists = targetAuthority is not null || MasterAuthority.IsTallySource(target.Source);
            var liveTargetName = targetAuthority?.Name ?? target.Name;
            var sourceExistsAsDifferentLiveMaster = sourceAuthority is not null;
            var liveSourceName = sourceAuthority?.Name ?? source.Name;

            if (sourceExistsAsDifferentLiveMaster)
            {
                var liveChildren = synchronizedTallyMasters.Count(master =>
                    (master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ||
                     master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase)) &&
                    TallyMasterIdentity.Equivalent(master.Parent, liveSourceName));
                if (liveChildren > 0)
                    throw new InvalidOperationException($"Merge direction is not safe: FROM Group '{source.Name}' still has {liveChildren:N0} synchronized Tally child Master(s). Group child migration is not changed by Fix47.");
            }

            var tallyTarget = new MasterRecord
            {
                PersistentId = target.PersistentId,
                ClientId = target.ClientId,
                FinancialYear = target.FinancialYear,
                Name = liveTargetName,
                Parent = target.Parent,
                Type = target.Type,
                Company = target.Company,
                Source = target.Source,
                OpeningBalance = target.OpeningBalance,
                IsArchived = false,
                IsUserEdited = true
            };

            ConnectionStatus = "Tally connected • Master update in progress";
            var response = await _httpService.UpsertAccountingMasterAsync(
                company, tallyTarget, targetExists ? "Alter" : "Create", cancellationToken);

            var sourceDeletedInTally = false;
            if (sourceExistsAsDifferentLiveMaster)
            {
                await _httpService.DeleteAccountingMasterAsync(company, source.Type, liveSourceName, cancellationToken);
                sourceDeletedInTally = true;
            }

            ConnectionStatus = "Tally connected";
            var action = targetExists ? "altered" : "created";
            var deleteText = sourceDeletedInTally ? $" FROM '{source.Name}' was deleted from Tally after zero live child usage was verified." : string.Empty;
            LastOperationStatus = $"Tally {target.Type} '{target.Name}' {action} and live-verified before local merge.{deleteText} Created={response.Created}; Altered={response.Altered}.";
            Changed?.Invoke(this, EventArgs.Empty);
            return new TallyMasterMergeUpdateResult(true, targetExists, sourceDeletedInTally, LastOperationStatus,
                Deferred: false, PendingMergeId: 0, AllowTallySourceArchive: sourceIsTallyBacked);
        }
        finally
        {
            IsBusy = false;
            if (IsConnected) ConnectionStatus = "Tally connected";
            _tallyOperationGate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }


    private async Task<(IReadOnlyList<TallyVoucherIndexRecord> Index, IReadOnlyList<TallyVoucherXmlSnapshot> Full)>
        ReadCompleteActiveFyVoucherAuthorityAsync(
            ClientProfile client,
            TallyCompanyInfo company,
            DateTime fromDate,
            DateTime toDate,
            CancellationToken cancellationToken)
    {
        if (!_httpService.IsLocalErp9Runtime)
        {
            var snapshot = await _httpService.ExportFullCompanySnapshotAsync(
                company, fromDate.Date, toDate.Date, cancellationToken);
            return (snapshot.Vouchers, snapshot.FullVouchers);
        }

        var index = await _httpService.ExportVoucherIndexFreshAsync(
            company, fromDate.Date, toDate.Date, cancellationToken);
        var full = await _httpService.ExportCompleteVoucherXmlAuthorityAsync(
            company, index, fromDate.Date, toDate.Date, cancellationToken);
        return (index, full);
    }

    private async Task<IReadOnlyList<TallyVoucherIndexRecord>> GetMergeVoucherIndexAuthorityAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        fromDate = fromDate.Date;
        toDate = toDate.Date;
        var state = _repository.GetBankSyncState(client.Id, client.FinancialYear, company.CompanyKey);

        // Fix49: Merge + Update reuses the same durable FY Voucher Index that the
        // connected-Tally baseline + ALTERID pulse already maintains. A destructive
        // merge performs only the normal tiny ALTERID freshness pulse here; it does not
        // redownload the full FY index when that synchronized authority is current.
        if (state is not null && state.LastAlterId > 0 &&
            _repository.HasCompleteVoucherXmlAuthority(company.CompanyKey, client.FinancialYear))
        {
            ConnectionStatus = "Tally connected • confirming cached FY authority";
            var marker = await _httpService.GetVoucherChangeMarkerAsync(company, cancellationToken);
            var countReduced = marker.VoucherCount < state.LastVoucherCount;

            if (countReduced || !marker.HasReliableAlterId)
            {
                // A count reduction can represent a delete that an ALTERID-only delta
                // cannot reconstruct, and an unreliable ALTERID cannot prove freshness.
                // Refresh only the active FY in these exceptional cases — never BooksFrom.
                ConnectionStatus = "Tally connected • refreshing complete active-FY authority";
                var refreshed = await ReadCompleteActiveFyVoucherAuthorityAsync(
                    client, company, fromDate, toDate, cancellationToken);
                _repository.ReplaceVoucherIndex(company.CompanyKey, client.FinancialYear, refreshed.Index);
                _repository.ReplaceVoucherXmlAuthority(company.CompanyKey, client.FinancialYear, refreshed.Full);
                // Do not advance the shared ALTERID checkpoint here. The normal
                // coordinator still owns Master+Voucher delta publication and will
                // advance the checkpoint only after both authorities are merged.
                return refreshed.Index;
            }

            if (marker.LastAlterId > state.LastAlterId)
            {
                ConnectionStatus = $"Tally connected • merging Voucher delta after ALTERID {state.LastAlterId:N0}";
                var delta = await _httpService.ExportAuthorityChangesAfterAlterIdAsync(
                    company, fromDate, toDate, state.LastAlterId, cancellationToken);
                _repository.UpsertVoucherIndexRecords(company.CompanyKey, client.FinancialYear, delta.Vouchers);
                _repository.UpsertVoucherXmlAuthority(company.CompanyKey, client.FinancialYear, delta.FullVouchers);
                // The merge needs only current Voucher authority. Leave the shared
                // checkpoint unchanged so the normal synchronization cycle can still
                // apply the same delta to Master authority before advancing ALTERID.
            }

            return _repository.GetVoucherIndexRange(
                company.CompanyKey, client.FinancialYear, fromDate, toDate);
        }

        // If the connection baseline has not finished yet (or this installation was
        // upgraded from the old index-only authority), seed the complete active-FY
        // Voucher authority once. Merge never creates a second lightweight-only cache.
        ConnectionStatus = "Tally connected • seeding complete active-FY authority";
        var initial = await ReadCompleteActiveFyVoucherAuthorityAsync(
            client, company, fromDate, toDate, cancellationToken);
        _repository.ReplaceVoucherIndex(company.CompanyKey, client.FinancialYear, initial.Index);
        _repository.ReplaceVoucherXmlAuthority(company.CompanyKey, client.FinancialYear, initial.Full);
        return initial.Index;
    }

    private async Task<IReadOnlyList<TallyVoucherXmlSnapshot>> RefreshMergeVoucherAuthorityAfterWriteAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        IReadOnlyList<TallyVoucherXmlSnapshot> originalSnapshots,
        CancellationToken cancellationToken)
    {
        var expected = originalSnapshots
            .Where(item => !string.IsNullOrWhiteSpace(item.MasterId))
            .Select(item => item.MasterId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (expected.Length == 0) return Array.Empty<TallyVoucherXmlSnapshot>();

        var state = _repository.GetBankSyncState(client.Id, client.FinancialYear, company.CompanyKey);
        var afterAlterId = state?.LastAlterId ?? 0L;
        var marker = await _httpService.GetVoucherChangeMarkerAsync(company, cancellationToken);
        var deltaReturnedEveryExpectedVoucher = false;
        if (marker.HasReliableAlterId && marker.LastAlterId > afterAlterId)
        {
            var delta = await _httpService.ExportAuthorityChangesAfterAlterIdAsync(
                company, fromDate.Date, toDate.Date, afterAlterId, cancellationToken);
            _repository.UpsertVoucherIndexRecords(company.CompanyKey, client.FinancialYear, delta.Vouchers);
            _repository.UpsertVoucherXmlAuthority(company.CompanyKey, client.FinancialYear, delta.FullVouchers);
            var deltaIds = delta.FullVouchers.Select(item => item.MasterId.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            deltaReturnedEveryExpectedVoucher = expected.All(deltaIds.Contains);
        }

        if (deltaReturnedEveryExpectedVoucher)
            return _repository.GetVoucherXmlAuthorityByMasterIds(
                company.CompanyKey, client.FinancialYear, expected);

        // Some Tally builds can acknowledge ALTER before their global ALTERID marker is
        // visible. Verification may then read only the exact Voucher MASTERIDs just
        // written. This is post-write verification, never a broad pre-write reread.
        var exact = await _httpService.ExportVoucherXmlSnapshotsByKnownMasterIdsForVerificationAsync(
            company, expected, fromDate.Date, toDate.Date, cancellationToken);
        _repository.UpsertVoucherXmlAuthority(company.CompanyKey, client.FinancialYear, exact);
        return exact;
    }

    private async Task<TallyMasterMergeUpdateResult> ApplyLiveLedgerMasterMergeAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        MasterRecord source,
        MasterRecord target,
        decimal? mergedOpeningBalance,
        bool sourceIsTallyBacked,
        CancellationToken cancellationToken)
    {
        // Fix51: Master identity is already part of the synchronized initial/delta
        // authority. Do not issue another broad Ledger collection just because Merge
        // was clicked. The local Live-Tally Master rows are the current published
        // authority for exact Tally names.
        var synchronizedLedgers = _masterRepository.Get(
                client.Id, client.FinancialYear, includeArchived: false)
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             MasterAuthority.IsTallySource(master.Source))
            .ToArray();
        var targetAuthority = synchronizedLedgers.FirstOrDefault(item =>
            TallyMasterIdentity.Equivalent(item.Name, target.Name));
        var sourceAuthority = synchronizedLedgers.FirstOrDefault(item =>
            TallyMasterIdentity.Equivalent(item.Name, source.Name) &&
            !TallyMasterIdentity.Equivalent(item.Name, target.Name));

        var targetExists = targetAuthority is not null || MasterAuthority.IsTallySource(target.Source);
        var sourceExists = sourceAuthority is not null || sourceIsTallyBacked;
        var liveTargetName = targetAuthority?.Name ?? target.Name;
        var liveSourceName = sourceAuthority?.Name ?? source.Name;

        var targetCreated = false;
        var targetOpeningAltered = false;
        var sourceDeleted = false;
        var voucherMigrationAttempted = false;
        IReadOnlyList<TallyVoucherXmlSnapshot> sourceVoucherSnapshots = Array.Empty<TallyVoucherXmlSnapshot>();
        TallyImportResponse targetResponse = new();

        var tallyTarget = new MasterRecord
        {
            PersistentId = target.PersistentId,
            ClientId = target.ClientId,
            FinancialYear = target.FinancialYear,
            Name = liveTargetName,
            Parent = target.Parent,
            Type = target.Type,
            Company = target.Company,
            Source = target.Source,
            OpeningBalance = mergedOpeningBalance,
            IsArchived = false,
            IsUserEdited = true
        };

        async Task RestoreBeforeFailureAsync()
        {
            // The full pre-merge Voucher snapshots are the only safe rollback source.
            // Never perform target->source text replacement because an original Voucher
            // can legitimately contain both Ledgers.
            if (voucherMigrationAttempted && sourceVoucherSnapshots.Count > 0 && !sourceDeleted)
            {
                try
                {
                    await _httpService.RestoreVoucherXmlSnapshotsAsync(company, sourceVoucherSnapshots, cancellationToken);
                }
                catch
                {
                    // Keep both Masters alive. The original exception remains authoritative;
                    // a later retry can complete the migration because FROM was not deleted.
                }
            }

            if (targetOpeningAltered && targetExists)
            {
                var rollbackTarget = new MasterRecord
                {
                    PersistentId = target.PersistentId,
                    ClientId = target.ClientId,
                    FinancialYear = target.FinancialYear,
                    Name = liveTargetName,
                    Parent = target.Parent,
                    Type = target.Type,
                    Company = target.Company,
                    Source = target.Source,
                    OpeningBalance = target.OpeningBalance,
                    IsArchived = false,
                    IsUserEdited = true
                };
                try { await _httpService.UpsertAccountingMasterAsync(company, rollbackTarget, "Alter", cancellationToken); }
                catch { }
            }

            if (targetCreated && !sourceDeleted)
            {
                try { await _httpService.DeleteAccountingMasterAsync(company, "Ledger", liveTargetName, cancellationToken); }
                catch { }
            }
        }

        try
        {
            // If INTO is not yet a live Tally Ledger it must exist before any Voucher can
            // be rewritten to it. Existing INTO is deliberately left untouched here.
            if (!targetExists)
            {
                ConnectionStatus = "Tally connected • creating merge target";
                // Mark intent before the call: if Tally accepts the Create but the
                // subsequent live verification/read fails, rollback must still try to
                // remove the merge-only target rather than leave an orphan Master.
                targetCreated = true;
                targetResponse = await _httpService.UpsertAccountingMasterAsync(
                    company, tallyTarget, "Create", cancellationToken);
                liveTargetName = tallyTarget.Name;
            }

            if (sourceExists)
            {
                var (fyFrom, fyTo) = FinancialYearDates(client.FinancialYear);
                ConnectionStatus = "Tally connected • reusing active-FY Voucher Index";
                var synchronizedFyIndex = await GetMergeVoucherIndexAuthorityAsync(
                    client, company, fyFrom, fyTo, cancellationToken);

                // Fix51: the initial connection baseline already stored every complete
                // FY Voucher object. The lightweight index is used only to identify the
                // affected MASTERIDs; the pre-write full XML comes from the same durable
                // authority cache. Merge does not ask Tally to resend those Vouchers.
                var sourceMasterIds = synchronizedFyIndex
                    .Where(voucher => VoucherIndexUsesLedgerAuthority(voucher, liveSourceName))
                    .Select(voucher => voucher.MasterId.Trim())
                    .Where(id => id.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                sourceVoucherSnapshots = _repository.GetVoucherXmlAuthorityByMasterIds(
                    company.CompanyKey, client.FinancialYear, sourceMasterIds);
                if (sourceVoucherSnapshots.Count != sourceMasterIds.Length)
                {
                    var cached = sourceVoucherSnapshots.Select(snapshot => snapshot.MasterId.Trim())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var missing = sourceMasterIds.Where(id => !cached.Contains(id)).Take(10).ToArray();
                    throw new InvalidOperationException(
                        $"Complete Tally FY authority is missing Voucher MASTERID(s): {string.Join(", ", missing)}. Merge was stopped so Tally is not queried through a second pre-write Voucher path.");
                }
                _httpService.ValidateCachedVoucherSnapshotsForLedger(sourceVoucherSnapshots, liveSourceName);

                if (sourceVoucherSnapshots.Count > 0)
                {
                    ConnectionStatus = $"Tally connected • moving {sourceVoucherSnapshots.Count:N0} cached voucher(s) to INTO Ledger";
                    voucherMigrationAttempted = true;
                    await _httpService.AlterVoucherLedgerReferencesAsync(
                        company, sourceVoucherSnapshots, liveSourceName, liveTargetName, cancellationToken);

                    // Post-write is the only Voucher reread. Merge must not recursively
                    // enter the whole connection synchronizer (especially while a deferred
                    // merge is itself being resolved). Apply only the changed-object delta
                    // to the shared full-Voucher authority, leaving the normal 5-second
                    // coordinator as the sole owner of the published ALTERID checkpoint.
                    ConnectionStatus = "Tally connected • applying post-write Voucher delta";
                    var currentSnapshots = await RefreshMergeVoucherAuthorityAfterWriteAsync(
                        client, company, fyFrom, fyTo, sourceVoucherSnapshots, cancellationToken);
                    _httpService.VerifyVoucherLedgerMigrationFromCachedAuthority(
                        sourceVoucherSnapshots, currentSnapshots, liveSourceName, liveTargetName);
                }
            }

            // Fix47 old-backup compatibility: do not ALTER an existing target merely to
            // write the same opening balance again. Older Tally backups can reject that
            // harmless re-save even though Voucher Ledger migration itself is valid.
            var openingChangeRequired = !OpeningBalancesEquivalent(target.OpeningBalance, mergedOpeningBalance);
            if (targetExists && openingChangeRequired)
            {
                ConnectionStatus = "Tally connected • updating merged opening balance";
                // Mark intent before the call for the same accepted-write/failed-read
                // case: rollback will attempt to restore the original target opening.
                targetOpeningAltered = true;
                targetResponse = await _httpService.UpsertAccountingMasterAsync(
                    company, tallyTarget, "Alter", cancellationToken);
            }

            if (sourceExists)
            {
                ConnectionStatus = "Tally connected • deleting consolidated FROM Ledger";
                await _httpService.DeleteAccountingMasterAsync(company, "Ledger", liveSourceName, cancellationToken);
                sourceDeleted = true;
            }

            ConnectionStatus = "Tally connected";
            var voucherText = sourceVoucherSnapshots.Count > 0
                ? $" {sourceVoucherSnapshots.Count:N0} live Tally voucher(s) were transferred FROM '{source.Name}' INTO '{target.Name}' and verified."
                : " No live Tally vouchers required Ledger-reference migration.";
            var openingText = targetExists && !OpeningBalancesEquivalent(target.OpeningBalance, mergedOpeningBalance)
                ? " The merged opening balance was altered and live-verified."
                : targetExists
                    ? " INTO opening balance was already correct, so no opening-balance ALTER was sent."
                    : " INTO was created with the merged opening balance.";
            var deleteText = sourceDeleted
                ? $" FROM '{source.Name}' was deleted only after Voucher migration completed."
                : string.Empty;
            var status = $"Tally Ledger merge completed.{voucherText}{openingText}{deleteText} Created={targetResponse.Created}; Altered={targetResponse.Altered}.";
            return new TallyMasterMergeUpdateResult(
                true, targetExists, sourceDeleted, status,
                Deferred: false, PendingMergeId: 0, AllowTallySourceArchive: sourceIsTallyBacked);
        }
        catch
        {
            if (!sourceDeleted)
                await RestoreBeforeFailureAsync();
            throw;
        }
    }

    private static bool VoucherIndexUsesLedgerAuthority(TallyVoucherIndexRecord voucher, string ledgerName)
    {
        if (voucher.LedgerPostings.Any(posting => TallyMasterIdentity.Equivalent(posting.LedgerName, ledgerName))) return true;
        if (TallyMasterIdentity.Equivalent(voucher.PartyLedger, ledgerName)) return true;
        static IEnumerable<string> Split(string? value) =>
            (value ?? string.Empty).Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Split(voucher.DebitLedgers).Any(name => TallyMasterIdentity.Equivalent(name, ledgerName)) ||
               Split(voucher.CreditLedgers).Any(name => TallyMasterIdentity.Equivalent(name, ledgerName));
    }

    private static bool OpeningBalancesEquivalent(decimal? first, decimal? second) =>
        Math.Abs((first ?? 0m) - (second ?? 0m)) <= 0.01m;

    public void QueueCurrentClientMasterSynchronization(string reason)
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var link = _repository.GetLinkForClient(client.Id);
        if (link is null) return;
        var company = OpenCompanies.FirstOrDefault(item => item.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase));
        if (company is null) return;
        QueueBackgroundSynchronization(client, company, reason);
    }

    private async Task<int> RefreshLiveLedgerIdentityAuthorityAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        CancellationToken cancellationToken)
    {
        var authorities = await _httpService.ExportFreshLedgerNameAuthoritiesAsync(company, cancellationToken);
        if (authorities.Count == 0)
            throw new InvalidOperationException("Tally returned no live Ledgers while refreshing Master identity. Voucher export was blocked.");

        var missingStableId = authorities.Where(item => item.MasterId.Trim().Length == 0 || item.Guid.Trim().Length == 0)
            .Take(10).ToArray();
        if (missingStableId.Length > 0)
            throw new InvalidOperationException(
                "Tally Ledger identity refresh omitted MASTERID/GUID for: " +
                string.Join(", ", missingStableId.Select(item => item.DisplayName)) +
                ". Voucher export was blocked rather than guessing by Ledger name.");

        var records = authorities.Select(item => new MasterRecord
        {
            ClientId = client.Id,
            FinancialYear = client.FinancialYear,
            Name = item.DisplayName.Trim().Length > 0 ? item.DisplayName.Trim() : item.ExactName.Trim(),
            Parent = string.IsNullOrWhiteSpace(item.Parent) ? "Primary" : item.Parent.Trim(),
            Type = "Ledger",
            Company = company.Name,
            Source = "Live Tally",
            TallyMasterId = item.MasterId.Trim(),
            TallyGuid = item.Guid.Trim(),
            TallyAlterId = item.AlterId
        }).ToArray();

        // MASTERID is the primary identity; GUID is the secondary identity. Name is
        // mutable display/execution authority. Reconciliation rewrites every supported
        // AAH Ledger reference to Tally's current name before any Voucher XML is built.
        return await Task.Run(() => _masterRepository.Upsert(
            client.Id, client.FinancialYear, records, markUserEdited: false), cancellationToken);
    }

    public async Task<TallyBankImportResult> ImportBankRangeAsync(
        DateTime fromDate,
        DateTime toDate)
    {
        var client = _clientContext.CurrentClient
            ?? throw new InvalidOperationException(
                "Open a client before importing Bank Statement vouchers to Tally.");
        var link = _repository.GetLinkForClient(client.Id)
            ?? throw new InvalidOperationException(
                "Confirm a Tally company link in the dashboard first.");
        var company = OpenCompanies.FirstOrDefault(item =>
            item.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "The linked Tally company is not currently open.");
        ValidateClientCompanyIdentity(client, company);

        await _tallyOperationGate.WaitAsync();
        IsBusy = true;
        try
        {
            // Fix66: do not ask Tally for Ledger MASTERID/GUID in a separate request.
            // The normal full baseline already exports Ledger identity with the Master,
            // and every later Master rename/change arrives through the same ALTERID delta
            // used below. One final freshness pulse therefore refreshes both Master and
            // Voucher authority without a redundant pre-import Ledger collection read.
            ConnectionStatus = "Tally connected • final freshness check";
            LastOperationStatus =
                $"Checking current Master/Voucher ALTERID authority before Bank import {fromDate:dd.MM.yyyy} to {toDate:dd.MM.yyyy}…";
            var preMarker = await _httpService.GetVoucherChangeMarkerAsync(company);
            await SynchronizeLinkedCompanyAsync(client, company, "Pre-import live authority", preMarker, forceFullBaseline: true);
            var authorityAlterId = _repository.GetBankSyncState(client.Id, client.FinancialYear, company.CompanyKey)?.LastAlterId ?? preMarker.LastAlterId;

            ConnectionStatus = "Tally connected • import in progress";
            LastOperationStatus =
                $"Authority current at ALTERID {authorityAlterId:N0}. Deciding Bank Create / Modify / Skip / Review…";
            var result = await _bankImportService.ImportAsync(
                client, company, fromDate, toDate, cancellationToken: default,
                usePreSynchronizedVoucherAuthority: true, synchronizedAuthorityAlterId: authorityAlterId,
                // Manual Bank range action keeps its historic Final Ledger / Suspense fallback.
                allowUnmappedSuspense: true);
            LastOperationStatus = result.Status;

            if (result.ImportedTransactions > 0)
            {
                ConnectionStatus = "Tally connected • verifying imported vouchers";
                await SynchronizeLinkedCompanyAsync(
                    client,
                    company,
                    "Bank voucher import completed",
                    forceFullBaseline: true);
            }
            ConnectionStatus = "Tally connected";

            Changed?.Invoke(this, EventArgs.Empty);
            return result;
        }
        finally
        {
            IsBusy = false;
            _tallyOperationGate.Release();
        }
    }

    public async Task<TallyReportSnapshot> GetReportAsync(
        string reportName,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        var client = _clientContext.CurrentClient
            ?? throw new InvalidOperationException("Open a client before reading Tally reports.");
        var link = _repository.GetLinkForClient(client.Id)
            ?? throw new InvalidOperationException("Confirm a Tally company link in the dashboard first.");
        var company = OpenCompanies.FirstOrDefault(item =>
            item.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The linked Tally company is not currently open.");

        var state = _repository.GetBankSyncState(client.Id, client.FinancialYear, company.CompanyKey);
        var authorityAlterId = state?.LastAlterId ?? 0L;
        var cached = _repository.GetReportAuthority(
            company.CompanyKey, client.FinancialYear, reportName, fromDate, toDate, authorityAlterId);
        if (cached is not null) return cached;

        await _tallyOperationGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await _httpService.ExportReportAsync(
                company,
                reportName,
                fromDate,
                toDate,
                cancellationToken);
            _repository.UpsertReportAuthority(
                company.CompanyKey, client.FinancialYear, authorityAlterId, snapshot);
            return snapshot;
        }
        finally
        {
            _tallyOperationGate.Release();
        }
    }

    // Fix20: opt-in OUTBOUND Auto Recreate uses the existing R37 restore barrier.
    // Only a voucher already classified DeletedInTally after fresh full-FY authority
    // may be staged. RebindRequired/MissingLiveCandidate/ExternalDeleteConflict and
    // pending local changes are never promoted by this automatic option.
    // Called ONLY with the existing export operation gate held, after the fresh
    // Master/Voucher baseline. The Bank/Accounting exporters still decide live
    // duplicate/rebind/CREATE and save the new verified Tally MASTERID.
    private int StageVerifiedAutoRecreate(
        ClientProfile client, TallyCompanyInfo company, TallyVoucherExportRequest request)
    {
        if (!request.AutoRecreateConfirmedTallyDeletions || request.AllowInteractiveRecoveryPrompt ||
            request.RestoreSourceAnchors.Count != 0) return 0;
        var candidates = _repository.GetVoucherRecoveryCandidates(client.Id, client.FinancialYear, company.CompanyKey);
        static bool InPeriod(IReadOnlyList<TallyVoucherExportRange> ranges, DateTime day) =>
            ranges.Any(range => range.Contains(day));
        var staged = 0;
        // A small bounded batch prevents a multi-year delete/recreate event from
        // writing thousands of vouchers in a single unattended run.
        foreach (var candidate in candidates)
        {
            if (staged >= 100) break;
            if (!candidate.State.Equals("DeletedInTally", StringComparison.OrdinalIgnoreCase) ||
                candidate.VoucherDate is not DateTime day ||
                candidate.SourceAnchor.Length == 0 || candidate.PreviousMasterId.Length == 0 ||
                candidate.VerifiedVersion.Length == 0) continue;
            var chosen = candidate.EntityType switch
            {
                "Bank Voucher" => InPeriod(request.BankStatementRanges, day),
                "Purchase Voucher" => InPeriod(request.PurchaseRanges, day),
                "Sales Voucher" => InPeriod(request.SalesRanges, day),
                "Cash Voucher" => InPeriod(request.CashEntryRanges, day),
                "Manual Journal" or "GST Journal" => InPeriod(request.JournalRanges, day),
                _ => false
            };
            if (!chosen) continue;
            // The old MASTERID must STILL be absent. A live return or an exact
            // replacement is resolved by the downstream source-bound classifier;
            // never assume a stale DeletedInTally state authorizes CREATE.
            if (_repository.FindVoucherByMasterId(
                    company.CompanyKey, client.FinancialYear, candidate.PreviousMasterId) is not null) continue;
            if (_repository.RequestVoucherRestoreToTally(
                    client.Id, client.FinancialYear, company.CompanyKey,
                    candidate.EntityType, candidate.SourceAnchor, "DeletedInTally",
                    $"AUTO_RECREATE: user-enabled per Client/FY; confirmed Tally deletion, fresh FY authority, original MASTERID {candidate.PreviousMasterId}; duplicate-safe recovery."))
                staged++;
        }
        if (staged > 0)
            AutomationDiagnosticLog.Write("TALLY-AUTO-RECREATE",
                $"Staged {staged:N0} confirmed DeletedInTally source(s) through the existing RestoreRequested/MASTERID-first export exit. No conflicts or RebindRequired sources were promoted.",
                client.ClientCode);
        return staged;
    }

    public async Task<TallyVoucherExportResult> ExportSelectedAsync(
        TallyVoucherExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var client = _clientContext.CurrentClient
            ?? throw new InvalidOperationException("Open a client before exporting vouchers to Tally.");
        var link = _repository.GetLinkForClient(client.Id)
            ?? throw new InvalidOperationException("Confirm a Tally company link in the dashboard first.");
        var company = OpenCompanies.FirstOrDefault(item =>
            item.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The linked Tally company is not currently open.");
        ValidateClientCompanyIdentity(client, company);

        var bankRanges = CoalesceContiguousRanges(
            request.BankStatementRanges.Select(range => range.Normalize())).ToArray();
        var purchaseRanges = request.PurchaseRanges.Select(range => range.Normalize()).ToArray();
        var salesRanges = request.SalesRanges.Select(range => range.Normalize()).ToArray();
        var cashEntryRanges = request.CashEntryRanges.Select(range => range.Normalize()).ToArray();
        var journalRanges = request.JournalRanges.Select(range => range.Normalize()).ToArray();
        var allRanges = bankRanges.Concat(purchaseRanges).Concat(salesRanges)
            .Concat(cashEntryRanges).Concat(journalRanges).ToArray();
        if (allRanges.Length == 0)
            throw new InvalidOperationException("Select at least one Tally export module and period.");

        var result = new TallyVoucherExportResult
        {
            FromDate = allRanges.Min(range => range.FromDate),
            ToDate = allRanges.Max(range => range.ToDate)
        };

        await _tallyOperationGate.WaitAsync(cancellationToken);
        IsBusy = true;
        try
        {
            // Fix68: one explicit export begins from one complete fresh active-FY
            // Company/Master/Voucher authority. Master and Voucher MASTERID/GUID/ALTERID
            // are consumed from those object responses; no Ledger-ID-only reread is made.
            if (_httpService.IsLocalErp9Runtime)
                _httpService.ResetErp9DiagnosticArtifacts();

            // Fix68 explicit-write preflight: rebuild the complete active-FY authority
            // once before any module is allowed to decide SKIP / ALTER / CREATE. The
            // five-second background connection pulse remains lightweight.
            ConnectionStatus = "Tally connected • final freshness check";
            LastOperationStatus = "Checking Tally ALTERID before export…";
            Changed?.Invoke(this, EventArgs.Empty);
            var preWriteMarker = await _httpService.GetVoucherChangeMarkerAsync(company);
            // Fix27: automatic import reuses the proved complete active-FY cache and
            // applies the reliable ALTERID delta instead of requesting a ~42 MB full
            // company snapshot for every single ready voucher. The synchronization
            // method itself forces one full baseline whenever cache/master/stock/
            // FY authority is incomplete or live counts moved backwards.
            // Manual Export retains the original explicit full-freshness behavior.
            // CL017: the Tally Recovery page already forces one complete fresh FY
            // baseline BEFORE explicit user approval. Preserve that full authority,
            // then require a new reliable ALTERID marker/delta before actual CREATE.
            // A second unconditional full FY download is unnecessary and can strain
            // Tally immediately before the import. Normal Manual Export is unchanged.
            var explicitRestore = request.RestoreSourceAnchors.Count > 0;
            var preWriteFull = (!request.GenerateDiagnosticPackOnlyWhenMaterial && !explicitRestore) ||
                !preWriteMarker.HasReliableAlterId;
            await SynchronizeLinkedCompanyAsync(client, company, "Final export freshness", preWriteMarker,
                forceFullBaseline: preWriteFull);
            var authorityAlterId = _repository.GetBankSyncState(client.Id, client.FinancialYear, company.CompanyKey)?.LastAlterId ?? preWriteMarker.LastAlterId;
            ConnectionStatus = "Tally connected • import in progress";
            LastOperationStatus = $"Authority current at ALTERID {authorityAlterId:N0}. Using prepared local XML/duplicate decisions…";
            Changed?.Invoke(this, EventArgs.Empty);

            // Auto Recreate is a SELECTION/RESTORE permission, not a second writer.
            // Fresh FY authority and the existing serialized export gate precede it.
            // Manual Export, or an automatic job after Auto Recreate is switched OFF,
            // must not accidentally resume stale AUTO_RECREATE RestoreRequested rows
            // from a failed earlier batch. Only unsubmitted auto-owned permissions are
            // reversed; prior verified versions/MasterIDs remain preserved.
            var autoRecreateCancelled = request.AutoRecreateConfirmedTallyDeletions ? 0 :
                _repository.CancelQueuedAutoRecreateRequests(client.Id, client.FinancialYear, company.CompanyKey);
            if (autoRecreateCancelled > 0)
                AutomationDiagnosticLog.Write("TALLY-AUTO-RECREATE",
                    $"Auto Recreate OFF: cancelled {autoRecreateCancelled:N0} unsent automatic restore request(s); explicitly reviewed manual restores were not affected.", client.ClientCode);
            var autoRecreateStaged = StageVerifiedAutoRecreate(client, company, request);

            // Pin one verified ERP 9 Ledger/Group snapshot for the entire multi-period
            // export. The previous Bank loop could re-read every Ledger/Group for every
            // selected month. Master CREATE/ALTER imports still invalidate the snapshot,
            // so the next lookup immediately refreshes when authority genuinely changes.
            using var erp9CoreSnapshot = _httpService.BeginErp9CoreSnapshotScope(company);

            if (purchaseRanges.Length > 0 || salesRanges.Length > 0 || journalRanges.Length > 0)
                await _accountingRefreshCoordinator.RefreshAsync(client, cancellationToken);

            var statuses = new List<string>();
            var responsePaths = new List<string>();
            foreach (var bankRange in bankRanges)
            {
                try
                {
                    var bank = await _bankImportService.ImportAsync(
                        client, company, bankRange.FromDate, bankRange.ToDate, cancellationToken,
                        usePreSynchronizedVoucherAuthority: true, synchronizedAuthorityAlterId: authorityAlterId,
                        explicitRestoreSourceAnchors: request.RestoreSourceAnchors,
                        allowUnmappedSuspense: request.AllowBankSuspenseForUnmapped);
                    Merge(result, bank);
                    // Fix68 Bank import live-verifies its accepted date span from one
                    // same-response Voucher authority read and updates the local index.
                    // Do not ask Tally again for a redundant post-range ALTERID delta.
                    if (bank.GroupResponsePath.Length > 0) responsePaths.Add(bank.GroupResponsePath);
                    if (bank.DuplicateDeleteResponsePath.Length > 0) responsePaths.Add(bank.DuplicateDeleteResponsePath);
                    if (bank.MasterResponsePath.Length > 0) responsePaths.Add(bank.MasterResponsePath);
                    if (bank.ResponsePath.Length > 0) responsePaths.Add(bank.ResponsePath);
                    if (bank.MetadataResponsePath.Length > 0) responsePaths.Add(bank.MetadataResponsePath);
                    statuses.Add(bank.Status);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A live ERP 9 read/preflight failure is not a Tally IMPORTRESULT
                    // error. Keep TallyErrors reserved for errors actually returned by
                    // Tally after a send, and record the fail-closed read separately.
                    result.LiveReadErrors++;
                    var detail = $"Bank Statement {bankRange.FromDate:MMM yyyy} blocked before send: {exception.Message}";
                    result.LiveReadErrorDetails.Add(detail);
                    statuses.Add(detail);
                }
            }

            if (purchaseRanges.Length > 0 || salesRanges.Length > 0 ||
                cashEntryRanges.Length > 0 || journalRanges.Length > 0)
            {
                try
                {
                    var accounting = await _accountingExportService.ExportAsync(
                        client, company, purchaseRanges, salesRanges,
                        cashEntryRanges, journalRanges, cancellationToken,
                        usePreSynchronizedVoucherAuthority: true, synchronizedAuthorityAlterId: authorityAlterId,
                        confirmMissingRecovery: request.AllowInteractiveRecoveryPrompt
                            ? async descriptions =>
                            {
                                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                                if (dispatcher is null) return false;
                                return await dispatcher.InvokeAsync(() =>
                                    System.Windows.MessageBox.Show(
                                        $"{descriptions.Count} entries from an earlier incomplete export have no verified Tally identity.\n\n" +
                                        string.Join(", ", descriptions) +
                                        "\n\nRetry these missing entries? AAH will reread Tally and create only entries still absent. Existing vouchers will not be resent. Choose No if these entries were intentionally deleted from Tally.",
                                        "Recover missing accounting vouchers", System.Windows.MessageBoxButton.YesNo,
                                        System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes);
                            }
                            : _ => Task.FromResult(false),
                        explicitRestoreSourceAnchors: request.RestoreSourceAnchors,
                        includeRecurringCash: request.IncludeRecurringCash);
                    Merge(result, accounting);
                    if (accounting.MasterResponsePath.Length > 0) responsePaths.Add(accounting.MasterResponsePath);
                    if (accounting.ResponsePath.Length > 0) responsePaths.Add(accounting.ResponsePath);
                    statuses.Add(accounting.Status);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    result.TallyErrors++;
                    statuses.Add($"Purchase/Sales/Cash/Journal export failed: {exception.Message}");
                }
            }

            result.Status = statuses.Count == 0
                ? "Select at least one Tally export module."
                : string.Join(" ", statuses
                    .Where(status => !string.IsNullOrWhiteSpace(status))
                    .Distinct(StringComparer.OrdinalIgnoreCase));

            result.AutoRecreateBatchLimitReached = autoRecreateStaged >= 100 && result.Imported > 0;
            if (autoRecreateStaged > 0)
                result.Status += $" Auto Recreate: {autoRecreateStaged:N0} confirmed deleted source(s) staged through the existing restore authority; new MASTERIDs require normal read-back verification.";
            LastOperationStatus = result.Status;
            if (result.Imported > 0)
            {
                // Fix27: accepted IMPORTRESULT remains write-success authority. For
                // automatic sessions the final existing ALTERID delta captures the new
                // voucher MASTERIDs without transferring the full FY again. Missing /
                // inconsistent local authority or an unreliable marker still forces
                // the full baseline; Manual Export keeps its original full readback.
                ConnectionStatus = "Tally connected • verifying final authority";
                if (request.GenerateDiagnosticPackOnlyWhenMaterial)
                    AutomationDiagnosticLog.Write("TALLY-AUTO-EXPORT-POSTWRITE",
                        $"Tally accepted {result.Imported} voucher(s); beginning established final authority snapshot; do not resend accepted IMPORTRESULT.",
                        client.ClientCode);
                var postWriteMarker = await _httpService.GetVoucherChangeMarkerAsync(company, cancellationToken);
                // Both writers already verify CREATE/ALTER by bounded live MASTERID
                // readback. Restore reuses delta-first final authority rather than
                // forcing a second complete FY snapshot after a successful import.
                var postWriteFull = (!request.GenerateDiagnosticPackOnlyWhenMaterial && !explicitRestore) ||
                    !postWriteMarker.HasReliableAlterId;
                await SynchronizeLinkedCompanyAsync(client, company, "Voucher import completed", postWriteMarker,
                    forceFullBaseline: postWriteFull);
                if (request.GenerateDiagnosticPackOnlyWhenMaterial)
                    AutomationDiagnosticLog.Write("TALLY-AUTO-EXPORT-POSTWRITE",
                        "Established post-import Master/Voucher authority delta/baseline finished; IMPORTRESULT never resent.", client.ClientCode);
            }

            // Manual Export always retains its established diagnostic pack.
            // Automatic export uses that SAME local authority/IMPORTRESULT report
            // after a material outcome, never on idle/no-op cycles. Repeated
            // conflict-only reports are throttled without suppressing write reports.
            var materialAutomaticDiagnostic = result.Imported > 0 || result.TallyErrors > 0 ||
                result.LiveReadErrors > 0 || result.UnverifiedAfterImport > 0 ||
                (result.ConflictsBlocked > 0 &&
                 DateTime.UtcNow - _lastAutoConflictDiagnosticUtc >= TimeSpan.FromMinutes(15));
            if (request.GenerateDiagnosticPack &&
                (!request.GenerateDiagnosticPackOnlyWhenMaterial || materialAutomaticDiagnostic))
            {
                try
                {
                    LastDiagnosticReportPath = await GenerateDiagnosticPackAsync(
                        client, company, result, responsePaths, cancellationToken,
                        allowAppLogFallback: request.GenerateDiagnosticPackOnlyWhenMaterial);
                    if (request.GenerateDiagnosticPackOnlyWhenMaterial &&
                        result.ConflictsBlocked > 0 && result.Imported == 0)
                        _lastAutoConflictDiagnosticUtc = DateTime.UtcNow;
                    if (LastDiagnosticReportPath.Length > 0)
                        result.Status += $" Diagnostic report: {LastDiagnosticReportPath}";
                }
                catch (Exception diagnosticException) when (diagnosticException is not OperationCanceledException)
                {
                    result.Status += $" Diagnostic report could not be generated: {diagnosticException.Message}";
                }
            }

            ConnectionStatus = "Tally connected";
            Changed?.Invoke(this, EventArgs.Empty);
            return result;
        }
        finally
        {
            IsBusy = false;
            if (IsConnected && !_httpService.IsImportInProgress &&
                !ConnectionStatus.Contains("background", StringComparison.OrdinalIgnoreCase))
                ConnectionStatus = "Tally connected";
            _tallyOperationGate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static IReadOnlyList<TallyVoucherExportRange> CoalesceContiguousRanges(
        IEnumerable<TallyVoucherExportRange> ranges)
    {
        var ordered = ranges
            .Select(range => range.Normalize())
            .OrderBy(range => range.FromDate)
            .ThenBy(range => range.ToDate)
            .ToArray();
        if (ordered.Length <= 1) return ordered;

        var result = new List<TallyVoucherExportRange>();
        var currentFrom = ordered[0].FromDate.Date;
        var currentTo = ordered[0].ToDate.Date;
        for (var index = 1; index < ordered.Length; index++)
        {
            var next = ordered[index];
            if (next.FromDate.Date <= currentTo.AddDays(1))
            {
                if (next.ToDate.Date > currentTo) currentTo = next.ToDate.Date;
                continue;
            }

            result.Add(new TallyVoucherExportRange { FromDate = currentFrom, ToDate = currentTo });
            currentFrom = next.FromDate.Date;
            currentTo = next.ToDate.Date;
        }
        result.Add(new TallyVoucherExportRange { FromDate = currentFrom, ToDate = currentTo });
        return result;
    }

    private static void Merge(TallyVoucherExportResult target, TallyVoucherExportResult source)
    {
        target.Selected += source.Selected; target.Ready += source.Ready; target.Imported += source.Imported;
        target.ReadyCreates += source.ReadyCreates; target.ReadyAlters += source.ReadyAlters;
        target.ExactDuplicatesSkipped += source.ExactDuplicatesSkipped;
        target.ExistingSourceDuplicatesSkipped += source.ExistingSourceDuplicatesSkipped;
        target.RepeatedSourceCandidatesSkipped += source.RepeatedSourceCandidatesSkipped;
        target.ConflictsBlocked += source.ConflictsBlocked;
        foreach (var detail in source.ConflictDetails) if (!target.ConflictDetails.Contains(detail, StringComparer.OrdinalIgnoreCase)) target.ConflictDetails.Add(detail);
        target.MissingLedgers += source.MissingLedgers; target.Unsupported += source.Unsupported;
        target.PaymentReturnPairTransactionsSkipped += source.PaymentReturnPairTransactionsSkipped;
        target.TreatmentCorrected += source.TreatmentCorrected; target.TreatmentRequired += source.TreatmentRequired;
        foreach (var detail in source.TreatmentDetails) if (!target.TreatmentDetails.Contains(detail, StringComparer.OrdinalIgnoreCase)) target.TreatmentDetails.Add(detail);
        target.TallyCreated += source.TallyCreated; target.TallyAltered += source.TallyAltered;
        target.TallyDeleted += source.TallyDeleted;
        target.TallyIgnored += source.TallyIgnored; target.TallyErrors += source.TallyErrors;
        target.LiveReadErrors += source.LiveReadErrors;
        foreach (var detail in source.LiveReadErrorDetails)
            if (!target.LiveReadErrorDetails.Contains(detail, StringComparer.OrdinalIgnoreCase))
                target.LiveReadErrorDetails.Add(detail);
        target.UnverifiedAfterImport += source.UnverifiedAfterImport;
        if (source.LineError.Length > 0) target.LineError = source.LineError;
        if (source.ResponsePath.Length > 0) target.ResponsePath = source.ResponsePath;
    }

    private static void Merge(TallyVoucherExportResult target, TallyBankImportResult source)
    {
        target.Selected += source.SelectedTransactions; target.Ready += source.ReadyTransactions;
        target.ReadyCreates += source.ReadyCreateTransactions; target.ReadyAlters += source.ReadyAlterTransactions;
        target.Imported += source.ImportedTransactions; target.ExactDuplicatesSkipped += source.ExactDuplicatesSkipped;
        target.ExistingSourceDuplicatesSkipped += source.ExistingSourceDuplicatesSkipped;
        target.RepeatedSourceCandidatesSkipped += source.RepeatedSourceCandidatesSkipped; target.ConflictsBlocked += source.ConflictsBlocked; target.MissingLedgers += source.MissingLedgerTransactions;
        foreach (var detail in source.ConflictDetails) if (!target.ConflictDetails.Contains(detail, StringComparer.OrdinalIgnoreCase)) target.ConflictDetails.Add(detail);
        target.Unsupported += source.UnsupportedTransactions;
        target.PaymentReturnPairTransactionsSkipped += source.PaymentReturnPairTransactionsSkipped;
        target.TreatmentCorrected += source.TreatmentCorrectedTransactions; target.TreatmentRequired += source.TreatmentRequiredTransactions;
        foreach (var detail in source.TreatmentDetails) if (!target.TreatmentDetails.Contains(detail, StringComparer.OrdinalIgnoreCase)) target.TreatmentDetails.Add(detail);
        target.TallyCreated += source.TallyCreated;
        target.TallyAltered += source.TallyAltered; target.TallyDeleted += source.TallyDeleted;
        target.TallyIgnored += source.TallyIgnored; target.TallyErrors += source.TallyErrors;
        target.UnverifiedAfterImport += source.UnverifiedAfterImport;
        if (source.LiveVerificationError.Length > 0)
        {
            target.LiveReadErrors++;
            target.LiveReadErrorDetails.Add(
                $"Bank Statement {source.FromDate:MMM yyyy} post-import verification pending: {source.LiveVerificationError}");
        }
        if (source.LineError.Length > 0) target.LineError = source.LineError;
        if (source.ResponsePath.Length > 0) target.ResponsePath = source.ResponsePath;
    }

    // Throttle unresolved auto-only conflict diagnostics; accepted writes always
    // earn their own report. Never add Tally network reads for diagnostics.
    private DateTime _lastAutoConflictDiagnosticUtc = DateTime.MinValue;

    private async Task<string> GenerateDiagnosticPackAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        TallyVoucherExportResult result,
        IReadOnlyCollection<string> responsePaths,
        CancellationToken cancellationToken,
        bool allowAppLogFallback = false)
    {
        var linked = _automationService.GetFolders()
            .Where(folder => folder.ClientId == client.Id &&
                             folder.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(folder => Directory.Exists(folder.FolderPath))
            .ThenBy(folder => folder.FolderPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        // Manual Export retains its original linked-Automation-Folder location.
        // Automatic sessions fall back to AAH's own logs only when no linked
        // Automation Folder exists; a confirmed Tally company link is different.
        if (!allowAppLogFallback && (linked is null || !Directory.Exists(linked.FolderPath))) return string.Empty;
        var diagnosticBase = linked is not null && Directory.Exists(linked.FolderPath)
            ? linked.FolderPath
            : Path.Combine(ApplicationDataPaths.RootFolder, "Logs");
        var root = Path.Combine(diagnosticBase, "AAH_TALLY_REPORTS");
        var safeClient = SafeFilePart(client.ClientCode.Length > 0 ? client.ClientCode : client.Id.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);

        // Diagnostics are delivered as a single ZIP. Keep only one previous Hub-created
        // ZIP for this client before creating the new current pack; remove old extracted/
        // staging folders. Unrelated files/folders under AAH_TALLY_REPORTS are untouched.
        CleanupOldDiagnosticArtifacts(root, safeClient);

        var folder = Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeClient}-{result.FromDate:yyyyMMdd}-{result.ToDate:yyyyMMdd}");
        Directory.CreateDirectory(folder);

        IReadOnlyList<TallyVoucherIndexRecord> live = Array.Empty<TallyVoucherIndexRecord>();
        var liveIndexError = string.Empty;
        try
        {
            if (_httpService.IsLocalErp9Runtime)
            {
                // ERP 9 Legacy Live already performed bounded date-filtered Voucher-collection
                // reads for each export/preflight range and persisted those verified records.
                // Do not issue a second full-FY voucher request merely to build diagnostics;
                // on older ERP 9 that duplicate read was the largest avoidable delay.
                live = _repository.GetVoucherIndexRange(
                    company.CompanyKey,
                    client.FinancialYear,
                    result.FromDate,
                    result.ToDate);
            }
            else
            {
                // FIX4: diagnostics are local evidence only. Never turn one import into
                // another Tally read merely to build a ZIP; the post-import full snapshot
                // maintained by the coordinator is the live authority.
                live = _repository.GetVoucherIndexRange(
                    company.CompanyKey, client.FinancialYear, result.FromDate, result.ToDate);
            }

            WriteVoucherIndexCsv(Path.Combine(folder, "AAH_TALLY_01-Live-Voucher-Index.csv"), live);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed live-index parse used to abort the entire diagnostic pack,
            // which made ERP 9 compatibility failures impossible to investigate.
            // Keep duplicate safety unchanged, but still emit reports/import logs and
            // a precise index error for diagnosis.
            liveIndexError = exception.Message;
            File.WriteAllText(
                Path.Combine(folder, "AAH_TALLY_01-Live-Voucher-Index-Error.txt"),
                liveIndexError,
                Encoding.UTF8);
        }

        WriteFingerprintCsv(Path.Combine(folder, "AAH_TALLY_02-Local-Fingerprint-History.csv"),
            _repository.GetImportFingerprintAudit(client.Id, client.FinancialYear, company.CompanyKey));

        // Fix69: diagnostics must expose the identity AAH actually saved, not only the
        // Tally-side snapshot. This makes it possible to compare AAH PersistentId ->
        // Tally MASTERID/GUID/ALTERID -> current Ledger name row-for-row after any rename.
        var aahMasterIdentityRows = _masterRepository.Get(client.Id, client.FinancialYear, includeArchived: true);
        var tallyLedgerDisplayNameCollisions = aahMasterIdentityRows
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             !master.IsArchived &&
                             master.TallyMasterId.Trim().Length > 0 &&
                             TallyMasterIdentity.Key(master.Name).Length > 0)
            .GroupBy(master => TallyMasterIdentity.Key(master.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(master => master.TallyMasterId.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => new
            {
                DisplayName = group.First().Name.Trim(),
                MasterIds = group.Select(master => master.TallyMasterId.Trim())
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        WriteAahMasterIdentityCsv(
            Path.Combine(folder, "AAH_TALLY_03-AAH-Master-Identity.csv"),
            aahMasterIdentityRows);

        if (_httpService.IsLocalErp9Runtime)
        {
            foreach (var artifact in _httpService.GetErp9DiagnosticArtifacts())
            {
                var fileName = "AAH_TALLY_ERP9_" + SafeFilePart(artifact.Key);
                File.WriteAllText(Path.Combine(folder, fileName), artifact.Value, Encoding.UTF8);
            }
        }

        // FIX7: always preserve the exact one-request native full-snapshot exchange.
        // This is diagnostic-only and never sends another Tally request.
        foreach (var artifact in _httpService.GetFullSnapshotDiagnosticArtifacts())
        {
            var fileName = "AAH_TALLY_" + SafeFilePart(artifact.Key);
            File.WriteAllText(Path.Combine(folder, fileName), artifact.Value, Encoding.UTF8);
        }

        var reportSummary = new StringBuilder();
        if (_httpService.IsLocalErp9Runtime)
        {
            reportSummary.AppendLine(
                "ERP 9 Legacy Live Link: normal supported Tally reports/statistics and Master/Voucher processing remain enabled. " +
                "The five-second background pulse is low-priority and yields instead of holding AAH. Diagnostics reuse native Master/Voucher authority already collected where possible, " +
                "including Stock Items/Units as well as Ledger/Group data, local fingerprint history, import request/response XML and Tally.Imp. " +
                "The Live Voucher Index CSV is reused from the just-in-time Voucher-collection ranges already captured during this export; " +
                "AAH does not send a second full-FY ERP 9 voucher-index request only for diagnostics.");
        }
        else
        {
            // FIX4: supporting diagnostic reports used to send 13 additional Tally
            // requests after an import. The automatic diagnostic pack now stays fully
            // local so the one-snapshot read/write pipeline is not followed by hidden
            // report traffic that can make Tally appear held. Reports remain available
            // through their explicit UI actions when the user requests one.
            reportSummary.AppendLine(
                "Modern Tally automatic diagnostics reuse the synchronized voucher/master cache. " +
                "No additional Statistics/register/report requests were sent while building this pack.");
        }

        var copied = new List<string>();
        var number = 1;
        foreach (var responsePath in responsePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var responseName = $"AAH_TALLY_Import-{number:00}-Response.xml";
            File.Copy(responsePath, Path.Combine(folder, responseName), true);
            copied.Add(responseName);
            var requestPath = responsePath.EndsWith("-response.xml", StringComparison.OrdinalIgnoreCase)
                ? responsePath[..^"-response.xml".Length] + "-request.xml"
                : string.Empty;
            if (requestPath.Length > 0 && File.Exists(requestPath))
            {
                var requestName = $"AAH_TALLY_Import-{number:00}-Request.xml";
                File.Copy(requestPath, Path.Combine(folder, requestName), true);
                copied.Add(requestName);
            }
            number++;
        }

        // The automatic ZIP includes the durable AutoImport timeline, so a Tally
        // native crash after IMPORTRESULT can be aligned to final-authority reads.
        // No new network requests and no change to manual export diagnostics.
        if (allowAppLogFallback)
        {
            try
            {
                var autoLog = AutomationDiagnosticLog.LogPath;
                if (File.Exists(autoLog))
                    File.Copy(autoLog, Path.Combine(folder, "AAH_TALLY_04-AutoImport-Timeline.log"), true);
            }
            catch { /* best-effort evidence only; export must not fail for logs */ }
        }

        var tallyImpCopy = TryCopyTallyImportLog(folder);

        var summary = $"""
Accounting Automation Hub - Tally Export Diagnostic Pack
Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
Client: {client.ClientCode} - {client.ClientName}
Financial Year: {client.FinancialYear}
Tally Company: {company.Name}
Period: {result.FromDate:dd.MM.yyyy} to {result.ToDate:dd.MM.yyyy}

Hub result
Status: {result.Status}
Selected: {result.Selected}
Ready: {result.Ready}
Ready CREATE: {result.ReadyCreates}
Ready ALTER: {result.ReadyAlters}
Tally write accepted / imported: {result.Imported}
Exact duplicates skipped from LIVE Tally preflight: {result.ExactDuplicatesSkipped}
Previously imported/altered existing source rows skipped from LIVE Tally preflight: {result.ExistingSourceDuplicatesSkipped}
Repeated candidates inside this Hub export selection: {result.RepeatedSourceCandidatesSkipped}
Conflicts blocked from LIVE Tally preflight: {result.ConflictsBlocked}
Conflict details:
{(result.ConflictDetails.Count == 0 ? "(none)" : string.Join(Environment.NewLine, result.ConflictDetails.Select(detail => "- " + detail)))}
Missing details/ledgers: {result.MissingLedgers}
Unsupported: {result.Unsupported}
Payment Return paired rows skipped (net-zero reject/reversal): {result.PaymentReturnPairTransactionsSkipped}
Bank treatment auto-corrected: {result.TreatmentCorrected}
Bank treatment requires review: {result.TreatmentRequired}
Bank treatment details:
{(result.TreatmentDetails.Count == 0 ? "(none)" : string.Join(Environment.NewLine, result.TreatmentDetails.Select(detail => "- " + detail)))}
Live-unverified after import: {result.UnverifiedAfterImport}
Tally CREATED: {result.TallyCreated}
Tally ALTERED: {result.TallyAltered}
Tally DELETED (proven later exact duplicates only): {result.TallyDeleted}
Tally IGNORED: {result.TallyIgnored}
Tally ERRORS: {result.TallyErrors}
AAH LIVE READ / VERIFICATION ERRORS: {result.LiveReadErrors}
AAH LIVE READ / VERIFICATION DETAILS:
{(result.LiveReadErrorDetails.Count == 0 ? "(none)" : string.Join(Environment.NewLine, result.LiveReadErrorDetails.Select(detail => "- " + detail)))}
LINEERROR: {result.LineError}

Important duplicate rule
Local TallyImportFingerprints are ownership/audit evidence only. They never block export by themselves.
A Bank voucher is skipped as an exact duplicate or previously-imported existing source only when the freshly-read live Tally Voucher collection still proves a matching live row.
Bank first-pass identity uses Voucher Type, Bank Ledger/direction, date, amount, reference and narration; only the selected top/counterpart Ledger is excluded. MASTERID-bound rows verify the saved previous version before ALTER.
A previously Imported source is recognized as an existing live voucher when strong Bank text evidence still identifies that source even after voucher type/Ledger treatment was altered.
When a source is MASTERID-bound, AAH first proves the fresh live Voucher equals its saved previous verified version; only then is the complete desired new version ALTERed on that same MASTERID.
For repeated identical Bank occurrences, previously Imported sources claim one live MASTERID first. A later strictly identical first-pass Bank identity with a strictly newer MASTERID may be deleted only when it duplicates that retained occurrence and a distinct not-yet-imported statement source needs the slot; the replacement source is then CREATEd and live-verified.
A new source is not blocked only because another voucher shares date/amount/accounting shape when non-empty Bank text contradicts that live row.
Bank duplicate identity is strict: Voucher Type, Bank Ledger/direction, date, amount, reference and narration all match; only the selected top/counterpart Ledger is excluded from first-pass identity.
For Purchase/Sales/Cash/Journal CREATE recovery, the source document number is verified through REFERENCE because Tally may auto-assign VOUCHERNUMBER. After MASTERID binding, AAH captures Tally's exact Voucher Number in the previous verified version and any later external change to that number is a conflict.
Reference is not mandatory because ERP 9 commonly stores Bank vouchers with a blank Reference.
After import, a clean Tally IMPORTRESULT is retained as write-success authority; post-write ALTERID verification is separate and never causes an automatic resend.

Live voucher index rows: {live.Count}
Live voucher index status: {(_httpService.IsLocalErp9Runtime
    ? (liveIndexError.Length == 0 ? "Available" : "Unavailable - " + liveIndexError)
    : liveIndexError.Length == 0 ? "Available - synchronized ALTERID authority cache" : "Unavailable - " + liveIndexError)}
Authority model: initial baseline followed by Master+Voucher ALTERID deltas
Last synchronized ALTERID: {(_repository.GetBankSyncState(client.Id, client.FinancialYear, company.CompanyKey)?.LastAlterId ?? 0L)}
AAH Master identity rows: {aahMasterIdentityRows.Count:N0}; Ledgers with Tally MASTERID: {aahMasterIdentityRows.Count(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && master.TallyMasterId.Trim().Length > 0):N0}; identity file: AAH_TALLY_03-AAH-Master-Identity.csv
Tally Ledger display-name collisions (distinct MASTERIDs after AAH display cleanup): {tallyLedgerDisplayNameCollisions.Length:N0}
{(tallyLedgerDisplayNameCollisions.Length == 0 ? "(none)" : string.Join(Environment.NewLine, tallyLedgerDisplayNameCollisions.Select(collision => $"- {collision.DisplayName}: MASTERID {string.Join(", ", collision.MasterIds)}")))}
Copied request/response files: {string.Join(", ", copied)}
Tally.Imp detailed import log: {(tallyImpCopy.Length > 0 ? tallyImpCopy : "Not found through the running Tally process path") }

Tally-style report availability
{reportSummary}
""";
        File.WriteAllText(Path.Combine(folder, "AAH_TALLY_00-Summary.txt"), summary, Encoding.UTF8);

        // A single uploadable package is created beside the staging folder. Once the
        // ZIP is complete, delete the extracted staging folder as well so the client
        // directory contains only the newest diagnostic ZIP.
        var zipPath = Path.Combine(root, $"AAH_TALLY_DIAGNOSTIC_{DateTime.Now:yyyyMMdd-HHmmss}_{safeClient}.zip");
        ZipFile.CreateFromDirectory(folder, zipPath, CompressionLevel.Fastest, false);
        DeleteDiagnosticDirectory(folder);
        return zipPath;
    }

    private static void CleanupOldDiagnosticArtifacts(string root, string safeClient)
    {
        // Fix63 cumulative retention repair: before creating the new diagnostic keep
        // only one previous completed bundle. The new current ZIP then makes the
        // promised maximum of two (Current + Previous), never three.
        foreach (var oldZip in Directory.EnumerateFiles(
                     root, $"AAH_TALLY_DIAGNOSTIC_*_{safeClient}.zip", SearchOption.TopDirectoryOnly)
                 .OrderByDescending(File.GetLastWriteTimeUtc).Skip(1))
        {
            try { File.Delete(oldZip); }
            catch { /* A locked old diagnostic must not prevent creation of the new pack. */ }
        }

        foreach (var oldFolder in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (!IsHubDiagnosticStagingFolder(oldFolder, safeClient) &&
                !IsHubDiagnosticExtractedFolder(oldFolder, safeClient)) continue;
            try { DeleteDiagnosticDirectory(oldFolder); }
            catch { /* Retry on the next diagnostic run if an old folder is locked. */ }
        }
    }

    private static bool IsHubDiagnosticExtractedFolder(string path, string safeClient)
    {
        var name = Path.GetFileName(path);
        const string prefix = "AAH_TALLY_DIAGNOSTIC_";
        var suffix = $"_{safeClient}";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var stampLength = name.Length - prefix.Length - suffix.Length;
        if (stampLength != 15) return false;
        var stamp = name.Substring(prefix.Length, stampLength);
        return DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static bool IsHubDiagnosticStagingFolder(string path, string safeClient)
    {
        var name = Path.GetFileName(path);
        var marker = $"-{safeClient}-";
        if (name.Length < 33 || name.Length < 15 || name[8] != '-') return false;
        if (!DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return false;
        var markerIndex = name.IndexOf(marker, 15, StringComparison.OrdinalIgnoreCase);
        if (markerIndex != 15) return false;
        var dates = name[(markerIndex + marker.Length)..];
        if (dates.Length != 17 || dates[8] != '-') return false;
        return DateTime.TryParseExact(dates[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
               DateTime.TryParseExact(dates[9..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static void DeleteDiagnosticDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (Exception exception)
            {
                last = exception;
                Thread.Sleep(75);
            }
        }
        throw new IOException($"Could not remove diagnostic staging folder '{path}'.", last);
    }

    private static void WriteVoucherIndexCsv(string path, IReadOnlyCollection<TallyVoucherIndexRecord> rows)
    {
        var lines = new List<string> { "Date,Voucher Type,Voucher Number,Reference,Party Ledger,Debit Ledgers,Credit Ledgers,Amount,Master ID,Alter ID,GUID,Narration,Content Hash" };
        lines.AddRange(rows.OrderBy(row => row.VoucherDate).ThenBy(row => row.VoucherType).ThenBy(row => row.VoucherNumber)
            .Select(row => string.Join(",", new[]
            {
                Csv(row.VoucherDate?.ToString("yyyy-MM-dd") ?? string.Empty), Csv(row.VoucherType), Csv(row.VoucherNumber),
                Csv(row.Reference), Csv(row.PartyLedger), Csv(row.DebitLedgers), Csv(row.CreditLedgers),
                Csv(row.Amount.ToString("0.00", CultureInfo.InvariantCulture)), Csv(row.MasterId), Csv(row.AlterId),
                Csv(row.Guid), Csv(row.Narration), Csv(row.ContentHash)
            })));
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static void WriteAahMasterIdentityCsv(
        string path,
        IReadOnlyCollection<MasterRecord> rows)
    {
        var lines = new List<string>
        {
            "AAH Master ID,Type,Name,Parent,Source,Archived,User Edited,Tally Master ID,Tally GUID,Tally Alter ID,Internal Row ID,Updated UTC"
        };
        lines.AddRange(rows
            .OrderBy(row => row.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.PersistentId)
            .Select(row => string.Join(",", new[]
            {
                Csv(row.AahMasterId),
                Csv(row.Type), Csv(row.Name), Csv(row.Parent), Csv(row.Source),
                Csv(row.IsArchived ? "Yes" : "No"), Csv(row.IsUserEdited ? "Yes" : "No"),
                Csv(row.TallyMasterId), Csv(row.TallyGuid),
                Csv(row.TallyAlterId > 0 ? row.TallyAlterId.ToString(CultureInfo.InvariantCulture) : string.Empty),
                Csv(row.PersistentId.ToString(CultureInfo.InvariantCulture)),
                Csv(row.UpdatedUtc == DateTime.MinValue ? string.Empty : row.UpdatedUtc.ToString("O"))
            })));
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static void WriteFingerprintCsv(string path, IReadOnlyCollection<TallyImportFingerprintAuditRow> rows)
    {
        var lines = new List<string> { "Entity Type,Source Key,Content Hash,Local Status,Seen Count,First Seen UTC,Last Seen UTC,Response Summary" };
        lines.AddRange(rows.Select(row => string.Join(",", new[]
        {
            Csv(row.EntityType), Csv(row.SourceKey), Csv(row.ContentHash), Csv(row.Status), Csv(row.SeenCount.ToString(CultureInfo.InvariantCulture)),
            Csv(row.FirstSeenUtc == DateTime.MinValue ? string.Empty : row.FirstSeenUtc.ToString("O")),
            Csv(row.LastSeenUtc == DateTime.MinValue ? string.Empty : row.LastSeenUtc.ToString("O")), Csv(row.ResponseSummary)
        })));
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static void WriteReportCsv(string path, TallyReportSnapshot snapshot)
    {
        var lines = new List<string>
        {
            $"Report,{Csv(snapshot.DisplayName)}",
            $"Report ID,{Csv(snapshot.ReportId)}",
            $"Available,{snapshot.IsAvailable}",
            $"Status,{Csv(snapshot.Status)}",
            $"Error,{Csv(snapshot.Error)}",
            "",
            "Particulars,Debit,Credit,Amount,Detail"
        };
        lines.AddRange(snapshot.Rows.Select(row => string.Join(",", new[]
        {
            Csv(row.Particulars), Csv(row.Debit.ToString("0.00", CultureInfo.InvariantCulture)),
            Csv(row.Credit.ToString("0.00", CultureInfo.InvariantCulture)), Csv(row.Amount.ToString("0.00", CultureInfo.InvariantCulture)), Csv(row.Detail)
        })));
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static string TryCopyTallyImportLog(string destinationFolder)
    {
        foreach (var processName in new[] { "TallyPrime", "Tally", "tallyprime", "tally" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch { continue; }
            foreach (var process in processes)
            {
                try
                {
                    var executable = process.MainModule?.FileName ?? string.Empty;
                    var directory = executable.Length > 0 ? Path.GetDirectoryName(executable) ?? string.Empty : string.Empty;
                    if (directory.Length == 0) continue;
                    foreach (var name in new[] { "Tally.Imp", "tally.imp", "TALLY.IMP" })
                    {
                        var source = Path.Combine(directory, name);
                        if (!File.Exists(source)) continue;
                        var target = Path.Combine(destinationFolder, "AAH_TALLY_Tally.Imp.txt");
                        var bytes = File.ReadAllBytes(source);
                        string text;
                        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
                        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
                        else
                            text = Encoding.UTF8.GetString(bytes);
                        File.WriteAllText(target, text, new UTF8Encoding(true));
                        return Path.GetFileName(target);
                    }
                }
                catch
                {
                    // Diagnostic enrichment only; process module access can be denied.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        return string.Empty;
    }

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var text = new string((value ?? string.Empty).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return text.Length == 0 ? "Report" : text.Replace(' ', '-');
    }

    private static string Csv(string value)
    {
        value ??= string.Empty;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    public TallyClientLink? GetCurrentClientLink()
    {
        var client = _clientContext.CurrentClient;
        return client is null
            ? null
            : _repository.GetLinkForClient(client.Id);
    }

    public bool IsCurrentSelectionPending()
    {
        var selected = SelectedCompany;
        var client = _clientContext.CurrentClient;
        if (selected is null || client is null)
        {
            return false;
        }

        var link = _repository.GetLinkForClient(client.Id);
        return link is null ||
               !link.CompanyKey.Equals(selected.CompanyKey, StringComparison.OrdinalIgnoreCase);
    }

    public static (DateTime From, DateTime To) FinancialYearDates(string financialYear)
    {
        var digits = new string((financialYear ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length >= 4 && int.TryParse(digits[..4], out var startYear))
        {
            return (
                new DateTime(startYear, 4, 1),
                new DateTime(startYear + 1, 3, 31));
        }

        var now = DateTime.Today;
        var start = now.Month >= 4 ? now.Year : now.Year - 1;
        return (
            new DateTime(start, 4, 1),
            new DateTime(start + 1, 3, 31));
    }

    public static string IdentityMatchText(
        ClientProfile client,
        TallyCompanyInfo company)
    {
        var clientGstin = TallyCompanyInfo.NormalizeGstin(client.Gstin);
        var companyGstin = TallyCompanyInfo.NormalizeGstin(company.Gstin);
        if (clientGstin.Length > 0 && companyGstin.Length > 0 &&
            !clientGstin.Equals(companyGstin, StringComparison.OrdinalIgnoreCase))
            return "GSTIN mismatch - confirmation/import is blocked.";

        var (appFyStart, _) = FinancialYearDates(client.FinancialYear);
        var financialYearBeginning = TallyCompanyInfo.ParseTallyDate(company.StartingFrom);
        var booksBeginning = TallyCompanyInfo.ParseTallyDate(company.BooksFrom);
        if (financialYearBeginning is null || booksBeginning is null)
            return "Tally Financial Year Beginning / Books Beginning From could not be read - confirmation/import is blocked.";
        if (financialYearBeginning.Value.Date > appFyStart.Date)
            return $"Tally Financial Year Beginning ({financialYearBeginning.Value:dd-MMM-yyyy}) is later than app FY start ({appFyStart:dd-MMM-yyyy}) - import is blocked.";
        if (booksBeginning.Value.Date > appFyStart.Date)
            return $"Tally Books Beginning From ({booksBeginning.Value:dd-MMM-yyyy}) is later than app FY start ({appFyStart:dd-MMM-yyyy}) - import is blocked.";

        return $"Tally books cover app FY {client.FinancialYear}: FY beginning {financialYearBeginning.Value:dd-MMM-yyyy}, books beginning {booksBeginning.Value:dd-MMM-yyyy}. Current Tally display period is not used for import eligibility.";
    }

    public static bool CanConfirmIdentity(
        ClientProfile client,
        TallyCompanyInfo company)
    {
        var clientGstin = TallyCompanyInfo.NormalizeGstin(client.Gstin);
        var companyGstin = TallyCompanyInfo.NormalizeGstin(company.Gstin);
        if (clientGstin.Length > 0 && companyGstin.Length > 0 &&
            !clientGstin.Equals(companyGstin, StringComparison.OrdinalIgnoreCase)) return false;

        var (appFyStart, _) = FinancialYearDates(client.FinancialYear);
        var financialYearBeginning = TallyCompanyInfo.ParseTallyDate(company.StartingFrom);
        var booksBeginning = TallyCompanyInfo.ParseTallyDate(company.BooksFrom);
        return financialYearBeginning is not null && booksBeginning is not null &&
               financialYearBeginning.Value.Date <= appFyStart.Date &&
               booksBeginning.Value.Date <= appFyStart.Date;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _liveXmlTimer.Stop();
        _liveXmlTimer.Tick -= OnLiveXmlTimerTick;
        _clientContext.PropertyChanged -= OnClientContextChanged;
        _accountingRefreshCoordinator.Refreshed -= OnAccountingRefreshed;

        // Do not dispose the semaphores here. Dispatcher/HTTP callbacks can still be
        // unwinding during application shutdown and their finally blocks must be able
        // to Release safely; disposing the gates first caused shutdown-time
        // ObjectDisposedException crashes. They are tiny managed objects and die with
        // the process after this coordinator is detached.
        _httpService.Dispose();
    }

    /// <summary>
    /// R24: opt-in, one-client unattended Auto Tally Import cycle. It deliberately uses
    /// the EXISTING RefreshNowAsync(forceLinkedSynchronization) / FY Master+Voucher
    /// baseline and its duplicate/ALTERID protections; no parallel importer exists.
    /// Background folder watchers remain independent of this cycle.
    /// </summary>
    private void MaybeScheduleAutomaticTallySession()
    {
        // The first Tally baseline cannot depend on all local import queues being idle:
        // some queued sources wait for Master/FY authority from Tally. Open first,
        // then allow the established background sync and queue-idle gates to complete.
        // Outbound READY was already proved by the selected XML/local-plan gate.
        // Do not wait for unrelated global startup/local import queues before
        // launching a single ready voucher; normal Tally authority remains the
        // final write gate inside ExportSelectedAsync.
        if (_disposed || _automationService.IsPaused) return;
        var client = _clientContext.CurrentClient;
        if (client is null || client.IsArchived) return;
        var link = _repository.GetLinkForClient(client.Id);
        if (link is null || !AutoTallyImportPreference.AutoOpen(_clientPreferenceRepository, client)) return;
        // Auto Open is exclusively dependent on outbound Auto Import (Automatic
        // Tally Sync) AND an actual module-scoped READY signal. An enabled inbound
        // Tally -> AAH heartbeat is never launch authority.
        if (!IsOutboundAutoSyncEnabled(client) ||
            _outboundReadyClientId != client.Id ||
            !_outboundReadyFy.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;
        // Each new READY selection can open immediately, even a single ALTER.
        // A running Tally process and the in-flight gate remain safety barriers.
        // Only an actual failed launch gets a short retry backoff to prevent
        // spinning on a bad executable/company path. No successful-launch cooldown.
        if (DateTime.UtcNow < _nextFailedAutoOpenRetryUtc ||
            TallyPrivateCompanySessionService.AnyTallyRunning() ||
            Volatile.Read(ref _automaticLaunchConsumedForReady) != 0) return;
        if (Interlocked.CompareExchange(ref _autoSessionScheduled, 1, 0) != 0) return;
        _lastAutomaticSessionAttemptUtc = DateTime.UtcNow;
        _ = RunAutomaticTallySessionAsync(client, link);
    }

    private async Task RunAutomaticTallySessionAsync(ClientProfile client, TallyClientLink link)
    {
        try
        {
            // Avoid stealing another client/FY's active coordinator or Tally process.
            if (_disposed || _clientContext.CurrentClient is not { } active ||
                active.Id != client.Id || active.FinancialYear != client.FinancialYear ||
                _automationService.IsPaused || !IsOutboundAutoSyncEnabled(client) ||
                _outboundReadyClientId != client.Id ||
                !_outboundReadyFy.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;
            var exe = _applicationLaunchService.LocateTallyExecutableForIcon(client.Id);
            if (string.IsNullOrWhiteSpace(exe))
            {
                _nextFailedAutoOpenRetryUtc = DateTime.UtcNow.AddSeconds(5);
                AutomationDiagnosticLog.Write("TALLY-AUTO-OPEN-WAIT", "No validated Tally executable for this linked client/FY; queued work retained.", client.ClientCode);
                return;
            }
            var start = await _privateSessions.StartAsync(client, link, exe, automatic: true);
            if (!start.Success)
            {
                _nextFailedAutoOpenRetryUtc = DateTime.UtcNow.AddSeconds(5);
                AutomationDiagnosticLog.Write("TALLY-AUTO-OPEN", start.Message, client.ClientCode);
                return;
            }
            _nextFailedAutoOpenRetryUtc = DateTime.MinValue;
            Interlocked.Exchange(ref _automaticLaunchConsumedForReady, 1);
            ConnectionStatus = "Tally Auto Open • waiting for verified company";
            // The same coordinator's normal XML discovery is the authority for loaded
            // companies. Never consider a successful Process.Start to be a sync success.
            var deadline = DateTime.UtcNow.AddSeconds(80);
            TallyCompanyInfo? target = null;
            while (!_disposed && DateTime.UtcNow < deadline)
            {
                if (!_privateSessions.OwnsAutomaticSession(client) ||
                    _clientContext.CurrentClient is not { } current || current.Id != client.Id ||
                    current.FinancialYear != client.FinancialYear) return;
                // Fix27: never run an additional 1.5-second XML discovery loop.
                // The established five-second coordinator already owns the single
                // serialized Tally HTTP exit and the live company identity marker.
                // Only its FRESH successful post-launch read can authorize export.
                await Task.Delay(1000);
                if (!IsConnected || _lastSuccessfulConnectionUtc < _lastAutomaticSessionAttemptUtc ||
                    !_lastAvailableLinkedCompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                target = OpenCompanies.Count == 1 ? OpenCompanies.SingleOrDefault(company =>
                    company.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase) &&
                    company.Name.Equals(link.CompanyName, StringComparison.OrdinalIgnoreCase)) : null;
                if (target is not null) break;
            }
            if (target is null)
            {
                AutomationDiagnosticLog.Write("TALLY-AUTO-OPEN", "Loaded company identity could not be verified; left Tally open for review.", client.ClientCode);
                return;
            }

            // Fix23: the automatic SESSION RUNNER is not an additional export or
            // synchronization exit. The normal five-second Tally pulse discovers the
            // verified company and wakes Dashboard's EXISTING ExportSelectedAsync call.
            // That one shared writer already owns its before/after Master+Voucher
            // freshness and IMPORTRESULT checks. Do NOT force a separate full-FY
            // baseline here between Auto Open and the common export exit.
            AutomationDiagnosticLog.Write("TALLY-AUTO-SESSION",
                "Correct linked company verified; normal 5-second pulse will hand off to the existing Export Selected to Tally procedure; no extra full snapshot requested.",
                client.ClientCode);
            var synced = false; // Auto Close waits for authority proved by the shared exit.
            // Auto Close is dependent on outbound Auto Import, not on inbound sync.
            // AAH keeps its owned session available until heartbeat authority is proved.
            if (!IsOutboundAutoSyncEnabled(client) ||
                !AutoTallyImportPreference.AutoClose(_clientPreferenceRepository, client)) return;
            var closeDeadline = DateTime.UtcNow.AddHours(1);
            var consecutiveIdleHeartbeats = 0;
            var lastCountedHeartbeat = DateTime.MinValue;
            var lastIdleAlterId = -1L;
            while (!_disposed && DateTime.UtcNow < closeDeadline && _privateSessions.OwnsAutomaticSession(client))
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (_clientContext.CurrentClient is not { } closingClient ||
                    closingClient.Id != client.Id || closingClient.FinancialYear != client.FinancialYear ||
                    !IsOutboundAutoSyncEnabled(client) ||
                    !AutoTallyImportPreference.AutoClose(_clientPreferenceRepository, client)) return;
                var stillWorking = _outboundReadyClientId == client.Id ||
                    HasPendingOutboundWork(client) || _backgroundTallySyncScheduled != 0 ||
                    _httpService.IsImportInProgress || _httpService.IsExportInProgress ||
                    !_automationService.IsLocalPreparationComplete;
                if (stillWorking)
                {
                    consecutiveIdleHeartbeats = 0;
                    continue;
                }
                var latestState = _repository.GetBankSyncState(client.Id, client.FinancialYear, target.CompanyKey);
                synced = latestState is not null && latestState.LastFullSyncUtc >= _lastAutomaticSessionAttemptUtc &&
                    HasCompleteActiveFyVoucherAuthority(client, target, latestState);
                // No-change 5-second health pulses do not advance the persisted
                // LastIncrementalSyncUtc. Count fresh SUCCESSFUL in-memory marker
                // observations, not timer ticks or database commits. This also avoids
                // writing an unchanged SQLite checkpoint every five seconds.
                var matchingPulse = _confirmedIdlePulseClientId == client.Id &&
                    _confirmedIdlePulseFy.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase) &&
                    _confirmedIdlePulseCompanyKey.Equals(target.CompanyKey, StringComparison.OrdinalIgnoreCase) &&
                    _confirmedIdlePulseAlterId == latestState?.LastAlterId &&
                    _confirmedIdlePulseUtc > lastCountedHeartbeat;
                if (!synced || !matchingPulse)
                {
                    if (!synced) consecutiveIdleHeartbeats = 0;
                    continue;
                }
                lastCountedHeartbeat = _confirmedIdlePulseUtc;
                if (lastIdleAlterId != latestState!.LastAlterId)
                {
                    lastIdleAlterId = latestState.LastAlterId;
                    consecutiveIdleHeartbeats = 1;
                }
                else consecutiveIdleHeartbeats++;
                if (consecutiveIdleHeartbeats < 3) continue;

                // Third successful idle heartbeat is not final permission by itself.
                // Collect final fresh Master/Voucher authority using the existing path.
                await RefreshNowAsync(forceLinkedSynchronization: true);
                var finalState = _repository.GetBankSyncState(client.Id, client.FinancialYear, target.CompanyKey);
                var finalReady = finalState is not null &&
                    finalState.LastAlterId == lastIdleAlterId &&
                    HasCompleteActiveFyVoucherAuthority(client, target, finalState) &&
                    IsOutboundAutoSyncEnabled(client) &&
                    AutoTallyImportPreference.AutoClose(_clientPreferenceRepository, client) &&
                    _outboundReadyClientId != client.Id && !HasPendingOutboundWork(client) &&
                    !_httpService.IsImportInProgress && !_httpService.IsExportInProgress &&
                    _backgroundTallySyncScheduled == 0 && _automationService.IsLocalPreparationComplete;
                if (!finalReady)
                {
                    consecutiveIdleHeartbeats = 0;
                    continue;
                }
                // RefreshNowAsync above performed the authoritative serialized read.
                // Do not immediately send another parallel company discovery.
                var onlyOwn = IsConnected && OpenCompanies.Count == 1 &&
                    _lastAvailableLinkedCompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase) &&
                    OpenCompanies[0].CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase) &&
                    OpenCompanies[0].Name.Equals(link.CompanyName, StringComparison.OrdinalIgnoreCase);
                if (!onlyOwn) return;
                if (!IsOutboundAutoSyncEnabled(client) ||
                    !AutoTallyImportPreference.AutoClose(_clientPreferenceRepository, client)) return;
                if (_privateSessions.TryCloseAutomaticSession(client, finalReady,
                    _automationService.IsLocalPreparationComplete, onlyOwn)) return;
                // A user dialog or close refusal cannot be overridden with a kill.
                return;
            }
            AutomationDiagnosticLog.Write("TALLY-AUTO-CLOSE", "Auto-close safety wait ended without complete FY authority and a quiet queue; Tally left open, no process killed.", client.ClientCode);
        }
        catch (Exception ex)
        {
            AutomationDiagnosticLog.Write("TALLY-AUTO-SESSION", "Unattended cycle deferred; user Tally remains untouched: " + ex.Message, client.ClientCode);
        }
        finally
        {
            Interlocked.Exchange(ref _autoSessionScheduled, 0);
        }
    }

    private async void OnTimerTick(
        object? sender,
        EventArgs e)
    {
        if (_disposed || IsBusy) return;

        // FIXED41 R8 Repair 4 transport safety bar. A passive TCP probe sends no XML and
        // never acquires TallyRequestGate. When Tally is offline, the five-second scheduler
        // therefore stays cheap instead of launching Report/Collection calls that each wait
        // for their request timeout. When transport is healthy, normal authority processing
        // continues unchanged. A prior background XML failure gets a short passive-only
        // cooldown; manual refresh/import/export is never routed through this timer guard.
        var transportReachable = await _httpService.ProbeEndpointAsync(TimeSpan.FromMilliseconds(750));
        if (_disposed) return;
        if (!transportReachable)
        {
            // Only one exact-company session per cooldown, after the established local
            // preparation phase. Never try while another Tally process is already running.
            MaybeScheduleAutomaticTallySession();
            _consecutiveConnectionFailures++;
            if (_consecutiveConnectionFailures >= 2) IsConnected = false;
            _lastAvailableLinkedCompanyKey = string.Empty;
            ConnectionStatus = IsConnected ? "Tally linked • transport rechecking" : "Tally offline";
            LastOperationStatus = "5-second Tally safety pulse: HTTP port is not reachable; no XML request was sent.";
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (DateTime.UtcNow < _backgroundXmlRetryNotBeforeUtc)
        {
            ConnectionStatus = IsConnected ? "Tally linked • XML retry cooling down" : "Tally transport reachable • XML retry pending";
            LastOperationStatus = "5-second Tally safety pulse: transport is reachable; previous background XML timeout is cooling down before the normal authority retry.";
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Fix123: ERP 9 keeps the SAME normal five-second authority processing as the
        // rest of AAH: company/link verification, ALTERID/count pulse, Master/Voucher
        // delta detection, background synchronization and report-cache warm-up.
        // The fix is only that the low-priority timer gets a strict four-second budget
        // and a zero-wait coordinator gate. It may yield/retry, but it must never disable
        // normal Master refresh, reports, statistics, imports, exports or other Tally work.
        if (_httpService.IsLocalErp9Runtime)
        {
            using var pulseBudget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await RefreshNowAsync(
                forceLinkedSynchronization: false,
                cancellationToken: pulseBudget.Token,
                isFiveSecondBackgroundPulse: true);
            return;
        }

        await RefreshNowAsync(
            forceLinkedSynchronization: false,
            isFiveSecondBackgroundPulse: true);
    }

    private async Task RunErp9PassiveHeartbeatAsync()
    {
        var reachable = await _httpService.ProbeEndpointAsync(TimeSpan.FromMilliseconds(750));
        if (_disposed) return;

        if (reachable)
        {
            _consecutiveConnectionFailures = 0;
            _lastSuccessfulConnectionUtc = DateTime.UtcNow;
            IsConnected = true;

            var client = _clientContext.CurrentClient;
            var link = client is null ? null : _repository.GetLinkForClient(client.Id);
            ConnectionStatus = link is not null
                ? $"Tally.ERP 9 linked • {link.CompanyName}"
                : client is null
                    ? "Tally.ERP 9 server running • no client open"
                    : "Tally.ERP 9 server running • client not linked";
            LastOperationStatus = $"ERP 9 passive heartbeat {DateTime.Now:HH:mm:ss} • transport reachable • no XML request sent • Tally released.";
        }
        else
        {
            _consecutiveConnectionFailures++;
            if (_consecutiveConnectionFailures >= 2) IsConnected = false;
            ConnectionStatus = IsConnected
                ? "Tally.ERP 9 linked • transport rechecking"
                : "Tally.ERP 9 not reachable";
            LastOperationStatus = "ERP 9 passive heartbeat could not reach port 9000. No XML request was queued; AAH remains available.";
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnClientContextChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveClientContext.CurrentClient))
        {
            return;
        }

        _manualSelectionPending = false;
        // Fix27: client opening is not a pending automatic export signal.
        _outboundReadyClientId = 0;
        _outboundReadyFy = string.Empty;
        _automaticReadySourceRevision = string.Empty;
        Interlocked.Exchange(ref _automaticLaunchConsumedForReady, 0);
        _lastErp9CoreMasterScope = string.Empty;
        _lastErp9CoreMasterFingerprint = string.Empty;
        var client = _clientContext.CurrentClient;
        var link = client is null
            ? null
            : _repository.GetLinkForClient(client.Id);
        var linkedCompany = link is null
            ? null
            : OpenCompanies.FirstOrDefault(company =>
                company.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase));
        SetSelectionFromRememberedLink(linkedCompany);
        Changed?.Invoke(this, EventArgs.Empty);
        QueueLiveXmlGeneration();
        _ = RefreshNowAsync(forceLinkedSynchronization: false);
    }

    private async Task SynchronizeLinkedCompanyAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        string reason,
        TallyVoucherChangeMarker? knownMarker = null,
        bool forceFullBaseline = false)
    {
        try
        {
            var (fromDate, toDate) = FinancialYearDates(client.FinancialYear);

            var marker = knownMarker ?? await _httpService.GetVoucherChangeMarkerAsync(company);

            // Fix52: unresolved Ledger merges are durable AAH FROM -> INTO rules. They
            // do not trigger a special pre-sync Voucher read. Group merges retain their
            // pre-inbound safety handling because they move Master hierarchy rather than
            // Voucher Ledger references. The ordinary baseline/delta below is the single
            // authority read for the pulse; Ledger rules are applied only after it.
            var unresolvedPending = _masterRepository.GetUnresolvedPendingTallyMasterMerges(
                client.Id, client.FinancialYear, company.CompanyKey);
            var hadPendingGroup = unresolvedPending.Any(item =>
                item.SourceType.Equals("Group", StringComparison.OrdinalIgnoreCase));

            if (!await ProcessPendingTallyMasterMergesAsync(client, company))
            {
                IsConnected = true;
                ConnectionStatus = "Tally connected • Group merge needs action";
                return;
            }

            // A pending Group merge may have changed Tally Masters. Ledger mappings make
            // no pre-sync Tally write, so they must not cause a redundant marker request.
            if (hadPendingGroup)
                marker = await _httpService.GetVoucherChangeMarkerAsync(company);

            var state = _repository.GetBankSyncState(client.Id, client.FinancialYear, company.CompanyKey);
            var link = _repository.GetLinkForClient(client.Id);
            var countReduced = state is not null && (
                marker.VoucherCount < state.LastVoucherCount ||
                (marker.LedgerCount > 0 && link is not null && link.LedgerCount > 0 && marker.LedgerCount < link.LedgerCount) ||
                (marker.GroupCount > 0 && link is not null && link.GroupCount > 0 && marker.GroupCount < link.GroupCount));
            var fullVoucherAuthorityReady = HasCompleteActiveFyVoucherAuthority(client, company, state);
            // Fix64 Ledger identity readiness. The actual connected Tally company proves that
            // every Ledger carries MASTERID + GUID, so the active AAH Live-Tally set is
            // considered ready only when it covers the live Ledger count and every row
            // has both stable identifiers. A partial historical backfill may never allow
            // Voucher export to rely on stale Ledger names.
            var liveLedgerMasters = _masterRepository.Get(client.Id, client.FinancialYear, includeArchived: false)
                .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                 MasterAuthority.IsTallySource(master.Source))
                .ToArray();
            var ledgerMasterIdentityReady = marker.LedgerCount <= 0
                ? liveLedgerMasters.Length == 0 || liveLedgerMasters.All(master =>
                    master.TallyMasterId.Trim().Length > 0 && master.TallyGuid.Trim().Length > 0)
                : liveLedgerMasters.Length >= marker.LedgerCount && liveLedgerMasters.All(master =>
                    master.TallyMasterId.Trim().Length > 0 && master.TallyGuid.Trim().Length > 0);
            // Fix67: the five-second pulse stays lightweight, but the explicit Refresh
            // Tally command is a true full refresh. It must not merely report the ALTERID
            // marker as current while leaving an older failed-baseline message on screen.
            var stockMasterIdentityReady = HasCompleteLiveStockMasterAuthority(client, company, marker, state);
            var needBaseline = forceFullBaseline ||
                               state is null || state.LastFullSyncUtc == DateTime.MinValue || state.LastAlterId <= 0 ||
                               countReduced || !fullVoucherAuthorityReady || !ledgerMasterIdentityReady || !stockMasterIdentityReady;

            if (needBaseline)
            {
                LastOperationStatus = forceFullBaseline
                    ? $"{reason}: rebuilding one fresh Tally authority baseline…"
                    : $"{reason}: collecting one initial Tally authority baseline…";
                ConnectionStatus = "Tally connected • authority baseline";
                TallyMasterSyncResult masterResult;
                IReadOnlyList<TallyVoucherIndexRecord> vouchers;
                IReadOnlyList<TallyVoucherXmlSnapshot> fullVouchers;
                long baselineAlterId;

                if (_httpService.IsLocalErp9Runtime)
                {
                    // Fix125: if Stock authority itself is why the baseline is running,
                    // bypass the short ERP9 core-Master cache. Ledger/Group may already
                    // be current while the previous cache predates a successful StockItem
                    // Collection/List-of-Accounts read.
                    if (forceFullBaseline || !stockMasterIdentityReady)
                        _httpService.RequireFreshErp9MasterAuthorityForStockBaseline();

                    // ERP 9 baseline uses its proven native master + date-bounded voucher
                    // collections. This is baseline-only; normal cycles below are ALTERID deltas.
                    var masterXml = await _httpService.ExportMastersAsync(company);
                    masterResult = await Task.Run(() => _masterSyncService.Synchronize(
                        masterXml, client.Id, client.FinancialYear, company.Name,
                        liveLedgerParents: null, liveGroupNames: null, ledgerGroupOnlyAuthority: true));
                    if (marker.StockItemCount > 0 && masterResult.StockItemCount < marker.StockItemCount)
                        throw new InvalidOperationException($"ERP 9 reported {marker.StockItemCount:N0} live Stock Item(s), but the Master baseline returned only {masterResult.StockItemCount:N0}. The checkpoint was not advanced; Stock Master authority will retry instead of silently accepting an incomplete inventory Master read.");
                    vouchers = await _httpService.ExportVoucherIndexAsync(company, fromDate, toDate);
                    fullVouchers = await _httpService.ExportCompleteVoucherXmlAuthorityAsync(
                        company, vouchers, fromDate, toDate);
                    baselineAlterId = marker.LastAlterId;
                }
                else
                {
                    var snapshot = await _httpService.ExportFullCompanySnapshotAsync(company, fromDate, toDate);
                    masterResult = await Task.Run(() => _masterSyncService.Synchronize(
                        snapshot.Xml, client.Id, client.FinancialYear, company.Name,
                        snapshot.LedgerParents, snapshot.GroupNames));
                    vouchers = snapshot.Vouchers;
                    fullVouchers = snapshot.FullVouchers;
                    baselineAlterId = Math.Max(marker.LastAlterId, snapshot.AuthorityAlterId);
                }

                // Fix125: the ERP 9 lightweight count can be unknown/zero even when
                // the dedicated StockItem/List-of-Accounts baseline proves live items.
                // Keep the in-memory company snapshot aligned with the verified result.
                if (masterResult.StockItemCount > company.StockItemCount)
                    company.StockItemCount = masterResult.StockItemCount;

                _repository.ReplaceVoucherIndex(company.CompanyKey, client.FinancialYear, vouchers);
                _repository.ReplaceVoucherXmlAuthority(company.CompanyKey, client.FinancialYear, fullVouchers);
                // Fix85: a complete baseline is also the deletion/rebuild boundary for
                // previously bound Voucher MASTERIDs. Classify missing identities now so
                // no later module can silently reinterpret them as CREATE candidates.
                var missingVoucherBindings = _repository.ReconcileMissingVoucherBindingAuthority(
                    client.Id, client.FinancialYear, company.CompanyKey,
                    vouchers.Select(item => item.MasterId));
                if (!_repository.HasCompleteVoucherXmlAuthority(company.CompanyKey, client.FinancialYear, vouchers.Count))
                    throw new InvalidOperationException("Initial Tally FY baseline did not persist a complete full-Voucher authority. The synchronization checkpoint was not advanced.");

                // CL017: disable the automatic previous-FY Fixed Assets read entirely.
                // Previously cached Fixed Assets evidence is preserved for local display;
                // current-FY Tally Master/Voucher synchronization is unchanged.
                var voucherIdentityGroups = vouchers.GroupBy(VoucherIdentity, StringComparer.OrdinalIgnoreCase).ToArray();
                var duplicateVoucherCount = voucherIdentityGroups.Sum(group => Math.Max(0, group.Count() - 1));
                var conflictingVoucherCount = voucherIdentityGroups.Count(group => group.Select(v => v.ContentHash).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any());
                var now = DateTime.UtcNow;
                _repository.UpsertBankSyncState(new TallyBankSyncState
                {
                    ClientId=client.Id, FinancialYear=client.FinancialYear, CompanyKey=company.CompanyKey,
                    LastTallyChangeToken=marker.Token, LastVoucherCount=marker.VoucherCount, LastFyVoucherCount=vouchers.Count, LastAlterId=baselineAlterId,
                    LastBankInputToken=state?.LastBankInputToken ?? string.Empty,
                    LastStockItemCount=masterResult.StockItemCount, LastStockAuthorityUtc=now,
                    LastFullSyncUtc=now, LastIncrementalSyncUtc=now,
                    LastStatus=$"{reason}: complete FY baseline synchronized at ALTERID {baselineAlterId}; Stock authority {masterResult.StockItemCount:N0} item(s)."
                });
                if (_httpService.IsLocalErp9Runtime)
                    _erp9StockAuthorityVerifiedThisSession[Erp9StockSessionKey(client, company)] = 0;
                _repository.UpdateSynchronization(
                    client.Id, company, masterResult, marker.VoucherCount > 0 ? marker.VoucherCount : vouchers.Count,
                    duplicateVoucherCount, conflictingVoucherCount, marker.Token,
                    $"{reason}. Complete FY authority baseline synchronized; normal updates now use ALTERID deltas. ALTERID {baselineAlterId:N0}; {vouchers.Count:N0} FY voucher(s) with {fullVouchers.Count:N0} complete Voucher object(s). Missing bound Voucher identities {missingVoucherBindings.Missing:N0}; deletion candidates {missingVoucherBindings.MissingCandidates:N0}; rebind-required {missingVoucherBindings.RebindRequired:N0}; external-delete conflicts {missingVoucherBindings.Conflicts:N0}. {masterResult.Status}");
                await _accountingRefreshCoordinator.RefreshAsync(client);
                _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters,
                    $"{reason}: initial Tally authority baseline synchronized");
                if (masterResult.StockItemCount > 0 || masterResult.UnitCount > 0 || masterResult.GodownCount > 0)
                    _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.StockMasters,
                        $"{reason}: live Tally Stock/Unit authority synchronized");
                if (vouchers.Count > 0 || fullVouchers.Count > 0)
                    _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Journal,
                        $"{reason}: live Tally Voucher authority synchronized");
                LastOperationStatus = $"{reason}: complete baseline ready • ALTERID {baselineAlterId:N0} • {vouchers.Count:N0} FY Voucher indexes + {fullVouchers.Count:N0} complete Voucher object(s) • missing bound {missingVoucherBindings.Missing:N0} (delete-candidate {missingVoucherBindings.MissingCandidates:N0}, rebind {missingVoucherBindings.RebindRequired:N0}).";
            }
            else if (!marker.HasReliableAlterId || marker.LastAlterId <= state!.LastAlterId)
            {
                // FIXED41: a pulse token/count may never be acknowledged unless the local
                // Master + active-FY Voucher authority is already proved complete. This
                // closes the old failure where a new Ledger count was saved while the
                // Ledger itself was never imported, permanently hiding it from later pulses.
                if (!HasCompleteLiveAccountingMasterAuthority(client, marker) ||
                    !HasCompleteActiveFyVoucherAuthority(client, company, state) ||
                    !HasCompleteLiveStockMasterAuthority(client, company, marker, state))
                    throw new InvalidOperationException("Tally pulse changed but local authority is incomplete. Checkpoint was not advanced; the next 5-second pulse will repair the baseline.");

                var synchronizedState = state!;
                synchronizedState.LastTallyChangeToken = marker.Token;
                synchronizedState.LastVoucherCount = marker.VoucherCount;
                if (synchronizedState.LastFyVoucherCount < 0)
                {
                    var (fyFrom, fyTo) = FinancialYearDates(client.FinancialYear);
                    synchronizedState.LastFyVoucherCount = _repository.GetVoucherIndexRange(
                        company.CompanyKey, client.FinancialYear, fyFrom, fyTo).Count;
                }
                synchronizedState.LastIncrementalSyncUtc = DateTime.UtcNow;
                synchronizedState.LastStatus = $"{reason}: no newer ALTERID after verified local authority.";
                _repository.UpsertBankSyncState(synchronizedState);
                _repository.UpdateAuthorityCheckpoint(client.Id, company, marker, synchronizedState.LastStatus);
                LastOperationStatus = $"Tally current • ALTERID {synchronizedState.LastAlterId:N0}.";
            }
            else
            {
                ConnectionStatus = "Tally connected • merging ALTERID delta";
                LastOperationStatus = $"{reason}: reading changes after ALTERID {state.LastAlterId:N0}…";
                var delta = await _httpService.ExportAuthorityChangesAfterAlterIdAsync(
                    company, fromDate, toDate, state.LastAlterId);
                var masterDelta = await Task.Run(() => _masterSyncService.SynchronizeDelta(
                    delta.Xml, client.Id, client.FinancialYear, company.Name));
                _repository.UpsertVoucherIndexRecords(company.CompanyKey, client.FinancialYear, delta.Vouchers);
                _repository.UpsertVoucherXmlAuthority(company.CompanyKey, client.FinancialYear, delta.FullVouchers);
                var localFyVoucherCount = _repository.GetVoucherIndexRange(company.CompanyKey, client.FinancialYear, fromDate, toDate).Count;
                if (!_repository.HasCompleteVoucherXmlAuthority(company.CompanyKey, client.FinancialYear, localFyVoucherCount))
                    throw new InvalidOperationException("Tally ALTERID delta left the full FY Voucher authority incomplete. The checkpoint was not advanced.");
                if (!HasCompleteLiveAccountingMasterAuthority(client, marker))
                    throw new InvalidOperationException("Tally ALTERID delta did not restore the complete Ledger/Group Master authority. The checkpoint was not advanced; the next 5-second pulse will repair from baseline.");
                var nextAlterId = Math.Max(marker.LastAlterId, delta.MaxAlterId);
                state.LastTallyChangeToken = marker.Token;
                state.LastVoucherCount = marker.VoucherCount;
                state.LastFyVoucherCount = localFyVoucherCount;
                state.LastAlterId = nextAlterId;
                state.LastIncrementalSyncUtc = DateTime.UtcNow;
                if (masterDelta.StockItemCount > 0 || marker.StockItemCount > 0)
                {
                    state.LastStockItemCount = _stockMasterRepository.GetStockItems(
                            client.Id, client.FinancialYear, includeArchived: false)
                        .Count(item => item.Source.Equals("Live Tally", StringComparison.OrdinalIgnoreCase) &&
                                       item.TallyMasterId.Trim().Length > 0);
                    state.LastStockAuthorityUtc = DateTime.UtcNow;
                }
                state.LastStatus = $"{reason}: merged {masterDelta.UpsertedObjects:N0} master change(s) and {delta.Vouchers.Count:N0} complete voucher change(s) through ALTERID {nextAlterId:N0}.";
                _repository.UpsertBankSyncState(state);
                _repository.UpdateAuthorityCheckpoint(client.Id, company, marker, state.LastStatus);
                if (masterDelta.UpsertedObjects > 0)
                {
                    await _accountingRefreshCoordinator.RefreshAsync(client);
                    _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, state.LastStatus);
                    if (masterDelta.StockItemCount > 0 || masterDelta.UnitCount > 0 || masterDelta.GodownCount > 0)
                        _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.StockMasters, state.LastStatus);
                }
                if (delta.Vouchers.Count > 0 || delta.FullVouchers.Count > 0)
                    _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Journal, state.LastStatus);
                LastOperationStatus = state.LastStatus;
            }

            // Fix52: after the one normal baseline/delta has refreshed the shared
            // complete FY authority, apply any durable Ledger FROM -> INTO merge rules
            // directly from that cache. This stage performs writes only; it never asks
            // Tally to resend a pre-write Voucher. The next normal 5-second delta is
            // the verification/update path, exactly like the Inter-Bank workflow.
            await ProcessPendingTallyLedgerMergeAliasesFromAuthorityAsync(
                client, company, fromDate, toDate, CancellationToken.None);

            var reportAuthorityState = _repository.GetBankSyncState(
                client.Id, client.FinancialYear, company.CompanyKey);
            if (reportAuthorityState is not null && reportAuthorityState.LastAlterId > 0 &&
                _repository.CountReportAuthorityAtAlterId(
                    company.CompanyKey, client.FinancialYear, fromDate, toDate, reportAuthorityState.LastAlterId) <
                    TallyReportCatalog.DisplayNames.Count)
            {
                QueueReportAuthorityWarmup(client, company, fromDate, toDate, reportAuthorityState.LastAlterId);
            }

            IsConnected = true;
            ConnectionStatus = "Tally connected • authority current";
        }
        catch (Exception exception)
        {
            _repository.UpdateSyncError(client.Id, company.CompanyKey, exception.Message);
            LastOperationStatus = $"Tally authority synchronization could not complete: {exception.Message}";
            IsConnected = true;
            ConnectionStatus = "Tally connected • authority retry pending";
            if (reason.StartsWith("Pre-import", StringComparison.OrdinalIgnoreCase) ||
                reason.StartsWith("Final export", StringComparison.OrdinalIgnoreCase))
                throw;
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool HasCompleteLiveAccountingMasterAuthority(
        ClientProfile client,
        TallyVoucherChangeMarker marker)
    {
        var live = _masterRepository.Get(client.Id, client.FinancialYear, includeArchived: false)
            .Where(master => MasterAuthority.IsTallySource(master.Source))
            .ToArray();
        var ledgers = live.Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)).ToArray();
        var groups = live.Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase)).ToArray();
        var ledgerReady = marker.LedgerCount <= 0
            ? ledgers.All(master => master.TallyMasterId.Trim().Length > 0 && master.TallyGuid.Trim().Length > 0)
            : ledgers.Length >= marker.LedgerCount && ledgers.All(master => master.TallyMasterId.Trim().Length > 0 && master.TallyGuid.Trim().Length > 0);
        var groupReady = marker.GroupCount <= 0
            ? groups.All(master => master.TallyMasterId.Trim().Length > 0 && master.TallyGuid.Trim().Length > 0)
            : groups.Length >= marker.GroupCount && groups.All(master => master.TallyMasterId.Trim().Length > 0 && master.TallyGuid.Trim().Length > 0);
        return ledgerReady && groupReady;
    }

    private bool HasCompleteActiveFyVoucherAuthority(
        ClientProfile client,
        TallyCompanyInfo company,
        TallyBankSyncState? state)
    {
        var (fromDate, toDate) = FinancialYearDates(client.FinancialYear);
        var localCount = _repository.GetVoucherIndexRange(
            company.CompanyKey, client.FinancialYear, fromDate, toDate).Count;
        if (state is null || state.LastFyVoucherCount < 0)
        {
            // FIXED41: every legacy checkpoint is unproved for the active FY because the
            // historical field stored only company-wide $$NumVouchers. Force exactly one
            // baseline to establish the FY-specific count, including a proved zero-FY case.
            return false;
        }
        if (localCount != state.LastFyVoucherCount) return false;
        return _repository.HasCompleteVoucherXmlAuthority(company.CompanyKey, client.FinancialYear, localCount);
    }

    private bool HasCompleteLiveStockMasterAuthority(
        ClientProfile client,
        TallyCompanyInfo company,
        TallyVoucherChangeMarker marker,
        TallyBankSyncState? state)
    {
        // Fix125: ERP 9's lightweight $$NumStockItems marker is not a reliable proof of
        // emptiness on every build. A previous Ledger/Voucher checkpoint therefore cannot
        // suppress the first dedicated Stock authority read. Persist an independent proof
        // (count + timestamp) after a successful Master baseline.
        var liveMasterIds = _stockMasterRepository.GetStockItems(
                client.Id, client.FinancialYear, includeArchived: false)
            .Where(item => item.Source.Equals("Live Tally", StringComparison.OrdinalIgnoreCase) &&
                           item.TallyMasterId.Trim().Length > 0)
            .Select(item => item.TallyMasterId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var expected = marker.StockItemCount;
        if (expected > 0) return liveMasterIds >= expected;

        if (_httpService.IsLocalErp9Runtime)
        {
            // Fix131: even a durable prior Stock checkpoint cannot suppress this session's
            // first real ERP9 StockItem/List-of-Accounts read. Some ERP9 builds report a
            // zero lightweight Stock count while Stock Items exist or were added later.
            if (!_erp9StockAuthorityVerifiedThisSession.ContainsKey(Erp9StockSessionKey(client, company)))
                return false;
            if (state is null || state.LastStockAuthorityUtc == default)
                return false;
            return liveMasterIds >= Math.Max(0, state.LastStockItemCount);
        }

        return true;
    }

    private static string Erp9StockSessionKey(ClientProfile client, TallyCompanyInfo company) =>
        $"{client.Id}|{client.FinancialYear.Trim().ToUpperInvariant()}|{company.CompanyKey.Trim().ToUpperInvariant()}";

    private void QueueReportAuthorityWarmup(
        ClientProfile client,
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        long authorityAlterId)
    {
        // CL017: Financial Statements report-authority collection must not send
        // unsolicited Tally XML requests during the background heartbeat/import.
        // Cached report data remains available; an explicit user report request is
        // still handled by the original GetReportAsync path.
    }

    private static bool IsBackgroundReportTransportFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        return error.Contains("did not complete within", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("Tally HTTP 5", StringComparison.OrdinalIgnoreCase);
    }

    private async Task WarmReportAuthorityAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        long authorityAlterId)
    {
        try
        {
            foreach (var reportName in TallyReportCatalog.DisplayNames)
            {
                if (_disposed) break;
                var currentState = _repository.GetBankSyncState(
                    client.Id, client.FinancialYear, company.CompanyKey);
                if (currentState is null || currentState.LastAlterId != authorityAlterId) break;
                if (_repository.GetReportAuthority(
                        company.CompanyKey, client.FinancialYear, reportName, fromDate, toDate, authorityAlterId) is not null)
                    continue;

                await _tallyOperationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_disposed) break;
                    var snapshot = await _httpService.ExportReportAsync(
                        company, reportName, fromDate, toDate).ConfigureAwait(false);
                    _repository.UpsertReportAuthority(
                        company.CompanyKey, client.FinancialYear, authorityAlterId, snapshot);

                    // Safety bar only: an unsupported/missing report remains normal cached
                    // output and the suite continues. A transport timeout is different: once
                    // Tally has already failed to answer one low-priority background report,
                    // do not immediately line up the rest of the catalog behind the same busy
                    // endpoint. The next normal healthy cycle can resume from the first missing
                    // report because every completed snapshot above is still preserved.
                    if (!snapshot.IsAvailable && IsBackgroundReportTransportFailure(snapshot.Error))
                    {
                        LastOperationStatus = $"Report authority warm-up paused after Tally transport failure at '{reportName}'. Existing report authority is preserved and the missing reports will resume automatically.";
                        _reportWarmupRetryNotBeforeUtc = DateTime.UtcNow.AddSeconds(30);
                        return;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // An unavailable feature/report is itself cacheable output. ExportReportAsync
                    // normally returns an unavailable snapshot; only transport/runtime failures
                    // land here. Do not let one report block complete Master/Voucher authority.
                    LastOperationStatus = $"Report authority warm-up deferred at '{reportName}': {exception.Message}";
                    _reportWarmupRetryNotBeforeUtc = DateTime.UtcNow.AddSeconds(30);
                    return;
                }
                finally
                {
                    _tallyOperationGate.Release();
                }

                // Give the 5-second connection/ALTERID pulse and user operations a chance
                // to acquire the gate between reports. Tally is never held across the suite.
                await Task.Delay(25).ConfigureAwait(false);
            }

            // A complete/clean pass removes only the failure cooldown. Missing/unsupported
            // reports are still cached by ExportReportAsync and keep the existing behavior.
            _reportWarmupRetryNotBeforeUtc = DateTime.MinValue;
        }
        finally
        {
            Interlocked.Exchange(ref _backgroundReportAuthorityScheduled, 0);
        }
    }

    private async Task ProcessPendingTallyLedgerMergeAliasesFromAuthorityAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        var pending = _masterRepository.GetUnresolvedPendingTallyMasterMerges(
                client.Id, client.FinancialYear, company.CompanyKey)
            .Where(item => item.SourceType.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (pending.Length == 0) return;

        // This cache was refreshed by the normal baseline/delta immediately before this
        // method. Do not perform a marker, index, full-Voucher or report read here.
        var fyIndex = _repository.GetVoucherIndexRange(
            company.CompanyKey, client.FinancialYear, fromDate.Date, toDate.Date);
        var synchronizedMasters = _masterRepository.Get(
            client.Id, client.FinancialYear, includeArchived: true).ToArray();

        foreach (var item in pending)
        {
            var durableWatchEstablished = item.Status.Equals("Watching", StringComparison.OrdinalIgnoreCase);

            // AAH already merged the local row. Keep FROM hidden even though the inbound
            // Tally Master baseline/delta can legitimately still contain it until the
            // queued Voucher substitutions and final Master delete complete.
            _masterRepository.Archive(item.SourcePersistentId, archived: true);

            try
            {
                var canonicalTargetName = _masterRepository.ResolveCanonicalName(
                    client.Id, client.FinancialYear, "Ledger", item.TargetName);
                if (string.IsNullOrWhiteSpace(canonicalTargetName)) canonicalTargetName = item.TargetName;

                var localTarget = synchronizedMasters.FirstOrDefault(master =>
                    !master.IsArchived &&
                    master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                    TallyMasterIdentity.Equivalent(master.Name, canonicalTargetName));
                var tallyTarget = synchronizedMasters.FirstOrDefault(master =>
                    master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                    MasterAuthority.IsTallySource(master.Source) &&
                    TallyMasterIdentity.Equivalent(master.Name, canonicalTargetName));
                var liveTargetName = tallyTarget?.Name ?? canonicalTargetName;
                var targetWasCreated = false;

                // Vouchers cannot be altered to a Ledger that does not yet exist. Create
                // only the target Master when necessary; this is a write, not an authority
                // reread, and subsequent pulses will absorb it through the Master delta.
                if (tallyTarget is null)
                {
                    var createTarget = new MasterRecord
                    {
                        PersistentId = item.TargetPersistentId,
                        ClientId = client.Id,
                        FinancialYear = client.FinancialYear,
                        Name = liveTargetName,
                        Parent = localTarget?.Parent ?? item.TargetParent,
                        Type = "Ledger",
                        Company = company.Name,
                        Source = "AAH Merge Target",
                        OpeningBalance = localTarget?.OpeningBalance ?? item.MergedOpeningBalance,
                        IsArchived = false,
                        IsUserEdited = true
                    };
                    ConnectionStatus = $"Tally connected • creating merge target '{liveTargetName}'";
                    await _httpService.UpsertAccountingMasterAsync(
                        company, createTarget, "Create", cancellationToken);
                    targetWasCreated = true;
                }

                var sourceMasterIds = fyIndex
                    .Where(voucher => VoucherIndexUsesLedgerAuthority(voucher, item.SourceName))
                    .Select(voucher => voucher.MasterId.Trim())
                    .Where(masterId => masterId.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (sourceMasterIds.Length > 0)
                {
                    var snapshots = _repository.GetVoucherXmlAuthorityByMasterIds(
                        company.CompanyKey, client.FinancialYear, sourceMasterIds);
                    var cachedIds = snapshots.Select(snapshot => snapshot.MasterId.Trim())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var missing = sourceMasterIds.Where(masterId => !cachedIds.Contains(masterId)).Take(10).ToArray();
                    if (missing.Length > 0)
                        throw new InvalidOperationException(
                            $"Complete FY authority is missing cached Voucher MASTERID(s) {string.Join(", ", missing)} for durable merge '{item.SourceName}' -> '{liveTargetName}'. No second Tally Voucher read was attempted.");

                    _httpService.ValidateCachedVoucherSnapshotsForLedger(snapshots, item.SourceName);
                    ConnectionStatus = $"Tally connected • applying merge rule to {snapshots.Count:N0} cached voucher(s)";
                    var response = await _httpService.AlterVoucherLedgerReferencesAsync(
                        company, snapshots, item.SourceName, liveTargetName, cancellationToken);

                    // Do not reread/verify here. The next ordinary 5-second ALTERID delta
                    // will replace these exact cached Voucher objects. If FROM remains in
                    // that refreshed authority, this same durable rule stays Pending and is
                    // safely retried; the user never has to intervene or re-run Merge.
                    var status =
                        $"Durable merge '{item.SourceName}' -> '{liveTargetName}': sent ALTER for {snapshots.Count:N0} cached FY Voucher(s) " +
                        $"(Tally Altered={response.Altered}; Ignored={response.Ignored}; Errors={response.Errors}). Awaiting normal authority delta.";
                    _masterRepository.UpdatePendingTallyMasterMergeStatus(
                        item.Id, durableWatchEstablished ? "Watching" : "Pending", status);
                    _masterRepository.Archive(item.SourcePersistentId, archived: true);
                    LastOperationStatus = status;
                    continue;
                }

                // No current-FY cached Voucher still uses FROM. Only now finish the Master
                // side. Existing INTO gets an opening-balance ALTER only when this pending
                // mapping still points to its original target and the balance really differs.
                var finalTargetOpening = localTarget?.OpeningBalance ?? item.MergedOpeningBalance;
                var mappingWasRetargeted = !TallyMasterIdentity.Equivalent(canonicalTargetName, item.TargetName);
                if (!durableWatchEstablished && !targetWasCreated && !mappingWasRetargeted &&
                    !OpeningBalancesEquivalent(item.TargetOriginalOpeningBalance, finalTargetOpening))
                {
                    var alterTarget = new MasterRecord
                    {
                        PersistentId = item.TargetPersistentId,
                        ClientId = client.Id,
                        FinancialYear = client.FinancialYear,
                        Name = liveTargetName,
                        Parent = localTarget?.Parent ?? item.TargetParent,
                        Type = "Ledger",
                        Company = company.Name,
                        Source = tallyTarget?.Source ?? "Tally",
                        OpeningBalance = finalTargetOpening,
                        IsArchived = false,
                        IsUserEdited = true
                    };
                    ConnectionStatus = $"Tally connected • applying merged opening balance to '{liveTargetName}'";
                    await _httpService.UpsertAccountingMasterAsync(
                        company, alterTarget, "Alter", cancellationToken);
                }

                var tallySource = synchronizedMasters.FirstOrDefault(master =>
                    master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                    MasterAuthority.IsTallySource(master.Source) &&
                    TallyMasterIdentity.Equivalent(master.Name, item.SourceName) &&
                    !TallyMasterIdentity.Equivalent(master.Name, liveTargetName));
                if (tallySource is not null && !item.SkipSourceDelete)
                {
                    ConnectionStatus = $"Tally connected • retiring merged FROM '{item.SourceName}'";
                    try
                    {
                        await _httpService.DeleteAccountingMasterAsync(
                            company, "Ledger", tallySource.Name, cancellationToken);
                    }
                    catch (Exception deleteException) when (deleteException is not OperationCanceledException)
                    {
                        // Fix61: Tally commonly refuses deletion of Ledgers that are
                        // protected/in-use. Master deletion is best-effort only; preserve
                        // the durable A->B Voucher rewrite rule and never retry this delete.
                        _masterRepository.MarkPendingTallyLedgerDeleteSkipped(
                            item.Id, "Tally rejected Master delete; deletion skipped permanently: " + deleteException.Message);
                    }
                }

                // Keep the rule alive for the whole Client + FY. A later/manual Tally
                // Voucher can still arrive with the retired FROM spelling; the normal
                // authority pulse must detect it and silently apply the same A -> B ALTER.
                _masterRepository.UpdatePendingTallyMasterMergeStatus(item.Id, "Watching",
                    $"Durable merge rule active: '{item.SourceName}' -> '{liveTargetName}'. No cached active-FY Voucher currently uses FROM.");
                _masterRepository.Archive(item.SourcePersistentId, archived: true);
                LastOperationStatus =
                    $"Durable Ledger merge rule active: '{item.SourceName}' -> '{liveTargetName}'. Future active-FY Vouchers using FROM will be altered automatically.";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Merge is an unattended reconciliation rule. A transport/import problem
                // must not undo the local merge, show a blocking dialog, or demand user
                // action. Keep A -> B durable and retry from the next refreshed authority.
                _masterRepository.UpdatePendingTallyMasterMergeStatus(
                    item.Id, durableWatchEstablished ? "Watching" : "Pending", exception.Message);
                _masterRepository.Archive(item.SourcePersistentId, archived: true);
                LastOperationStatus =
                    $"Durable Ledger merge '{item.SourceName}' -> '{item.TargetName}' remains queued and will retry on the next Tally authority pulse: {exception.Message}";
            }
        }
    }

    private async Task<bool> ProcessPendingTallyMasterMergesAsync(
        ClientProfile client,
        TallyCompanyInfo company,
        CancellationToken cancellationToken = default)
    {
        var pending = _masterRepository.GetUnresolvedPendingTallyMasterMerges(
            client.Id, client.FinancialYear, company.CompanyKey);
        if (pending.Count == 0) return true;

        foreach (var item in pending)
        {
            var source = new MasterRecord
            {
                PersistentId = item.SourcePersistentId, ClientId = item.ClientId, FinancialYear = item.FinancialYear,
                Type = item.SourceType, Name = item.SourceName, Parent = item.SourceParent, OpeningBalance = item.SourceOpeningBalance, Source = "Tally (Deferred Merge)"
            };
            var target = new MasterRecord
            {
                PersistentId = item.TargetPersistentId, ClientId = item.ClientId, FinancialYear = item.FinancialYear,
                Type = item.TargetType, Name = item.TargetName, Parent = item.TargetParent, OpeningBalance = item.TargetOriginalOpeningBalance, Source = "AAH Deferred Merge"
            };

            if (source.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            {
                // Fix52: Ledger mappings are applied only after the normal baseline/delta
                // has refreshed the complete FY Voucher authority. Keep the already-merged
                // FROM row archived here so inbound Master synchronization cannot surface it
                // as an active duplicate while the durable FROM -> INTO rule is pending.
                _masterRepository.Archive(source.PersistentId, archived: true);
                continue;
            }

            var sourceExists = false;
            var sourceDeletedInTally = false;
            try
            {
                // The last synchronized Master authority remains the pre-write source for
                // a deferred Group merge. Include archived rows because FROM was archived
                // locally when the deferred merge was queued while Tally was closed.
                var synchronizedTallyMasters = _masterRepository.Get(
                        client.Id, client.FinancialYear, includeArchived: true)
                    .Where(master => MasterAuthority.IsTallySource(master.Source))
                    .ToArray();
                var groups = synchronizedTallyMasters
                    .Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var targetAuthority = groups
                    .Where(master => !master.IsArchived)
                    .FirstOrDefault(master => TallyMasterIdentity.Equivalent(master.Name, target.Name))
                    ?? groups.FirstOrDefault(master => TallyMasterIdentity.Equivalent(master.Name, target.Name));
                var sourceAuthority = groups.FirstOrDefault(master =>
                    TallyMasterIdentity.Equivalent(master.Name, source.Name) &&
                    !TallyMasterIdentity.Equivalent(master.Name, target.Name));
                var targetExists = targetAuthority is not null;
                var liveTargetName = targetAuthority?.Name ?? target.Name;
                sourceExists = sourceAuthority is not null;
                var liveSourceName = sourceAuthority?.Name ?? source.Name;
                if (sourceExists)
                {
                    var childCount = synchronizedTallyMasters.Count(master =>
                        !master.IsArchived &&
                        (master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ||
                         master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase)) &&
                        TallyMasterIdentity.Equivalent(master.Parent, liveSourceName));
                    if (childCount > 0)
                    {
                        var error = $"Deferred merge FROM Group '{source.Name}' has {childCount:N0} live Tally child Master(s).";
                        var restored = _masterRepository.RestoreMergedArchive(client.Id, client.FinancialYear, source.PersistentId, error);
                        if (restored.Restored)
                        {
                            LastOperationStatus = error + $" AAH restored FROM '{source.Name}' locally; the deferred merge will not retry.";
                            continue;
                        }
                        _masterRepository.UpdatePendingTallyMasterMergeStatus(item.Id, "Needs Action", error);
                        LastOperationStatus = error + " Local rollback could not be completed, so inbound Tally Master synchronization remains held.";
                        return false;
                    }
                }

                var tallyTarget = new MasterRecord
                {
                    PersistentId = target.PersistentId, ClientId = target.ClientId, FinancialYear = target.FinancialYear,
                    Name = liveTargetName, Parent = target.Parent, Type = target.Type, Company = company.Name, Source = target.Source,
                    OpeningBalance = target.OpeningBalance, IsArchived = false, IsUserEdited = true
                };
                await _httpService.UpsertAccountingMasterAsync(company, tallyTarget, targetExists ? "Alter" : "Create", cancellationToken);
                if (sourceExists)
                {
                    await _httpService.DeleteAccountingMasterAsync(company, source.Type, liveSourceName, cancellationToken);
                    sourceDeletedInTally = true;
                }

                _masterRepository.UpdatePendingTallyMasterMergeStatus(item.Id, "Completed");
                LastOperationStatus = $"Deferred Group merge #{item.Id:N0} applied to Tally before inbound synchronization.";
            }
            catch (Exception exception)
            {
                if (sourceExists && !sourceDeletedInTally)
                {
                    var restored = _masterRepository.RestoreMergedArchive(
                        client.Id, client.FinancialYear, source.PersistentId,
                        $"Tally rejected/deferred Group merge #{item.Id}: {exception.Message}");
                    if (restored.Restored)
                    {
                        LastOperationStatus =
                            $"Deferred Group merge #{item.Id:N0} was not accepted by Tally. " +
                            $"AAH restored FROM '{source.Name}' locally; no retry will be sent. Tally: {exception.Message}";
                        continue;
                    }
                }

                _masterRepository.UpdatePendingTallyMasterMergeStatus(item.Id, "Pending", exception.Message);
                LastOperationStatus = $"Deferred Group merge #{item.Id:N0} will retry before inbound synchronization: {exception.Message}";
                return false;
            }

        }
        return true;
    }

    private static string VoucherIdentity(
        TallyVoucherIndexRecord voucher)
    {
        if (!string.IsNullOrWhiteSpace(voucher.Reference))
        {
            return $"REF|{voucher.Reference.Trim().ToUpperInvariant()}";
        }

        return string.Join(
            "|",
            "VOUCHER",
            voucher.VoucherType.Trim().ToUpperInvariant(),
            voucher.VoucherNumber.Trim().ToUpperInvariant(),
            voucher.VoucherDate?.ToString("yyyyMMdd") ?? string.Empty,
            voucher.PartyLedger.Trim().ToUpperInvariant());
    }

    private void ApplyCompanyList(
        IReadOnlyList<TallyCompanyInfo> discovered)
    {
        var selectedKey = SelectedCompany?.CompanyKey;
        var links = _repository.GetAllLinks()
            .ToDictionary(link => link.CompanyKey, StringComparer.OrdinalIgnoreCase);

        OpenCompanies.Clear();
        foreach (var company in discovered.OrderBy(company => company.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!links.ContainsKey(company.CompanyKey))
            {
                var sameNameLinks = links.Values
                    .Where(candidate => candidate.CompanyName.Equals(company.Name, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (sameNameLinks.Length == 1)
                {
                    var remembered = sameNameLinks[0];
                    var companyNumberCompatible = company.CompanyNumber.Length == 0 ||
                                                  remembered.CompanyNumber.Length == 0 ||
                                                  company.CompanyNumber.Equals(remembered.CompanyNumber, StringComparison.OrdinalIgnoreCase);
                    var gstinCompatible = company.Gstin.Length == 0 ||
                                          remembered.Gstin.Length == 0 ||
                                          company.Gstin.Equals(remembered.Gstin, StringComparison.OrdinalIgnoreCase);
                    var useRememberedErp9Identity = _httpService.IsLocalErp9Runtime &&
                                                   companyNumberCompatible && gstinCompatible;

                    // ERP 9 compatibility probes do not always expose the same identity
                    // fields as Prime. Reuse the single remembered same-name company key
                    // when the available strong identifiers do not conflict. This keeps an
                    // already-confirmed ERP 9 link attached even when a newly-derived FY or
                    // omitted CompanyNumber/GSTIN would otherwise manufacture a new key.
                    if (useRememberedErp9Identity ||
                        company.Error.Length > 0 || company.CompanyNumber.Length == 0 || company.FinancialYear.Length == 0)
                    {
                        company.CompanyKey = remembered.CompanyKey;
                        if (company.CompanyNumber.Length == 0) company.CompanyNumber = remembered.CompanyNumber;
                        if (company.Gstin.Length == 0) company.Gstin = remembered.Gstin;
                        if (company.StateName.Length == 0) company.StateName = remembered.StateName;
                        if (company.BooksFrom.Length == 0) company.BooksFrom = remembered.BooksFrom;
                        if (company.StartingFrom.Length == 0) company.StartingFrom = remembered.StartingFrom;
                        if (company.FinancialYear.Length == 0) company.FinancialYear = remembered.FinancialYear;
                    }
                }
            }

            if (links.TryGetValue(company.CompanyKey, out var link))
            {
                company.LinkedClientId = link.ClientId;
                company.LinkedClientCode = link.ClientCode;
                company.LinkedClientName = link.ClientName;
                _repository.UpdateCompanySeen(company);
            }
            OpenCompanies.Add(company);
        }

        if (!string.IsNullOrWhiteSpace(selectedKey))
        {
            var replacement = OpenCompanies.FirstOrDefault(company =>
                company.CompanyKey.Equals(selectedKey, StringComparison.OrdinalIgnoreCase));
            if (replacement is not null)
            {
                _selectedCompany = replacement;
                OnPropertyChanged(nameof(SelectedCompany));
                OnPropertyChanged(nameof(HasSelectedCompany));
                return;
            }

            _selectedCompany = null;
            _manualSelectionPending = false;
            OnPropertyChanged(nameof(SelectedCompany));
            OnPropertyChanged(nameof(HasSelectedCompany));
        }

        if (OpenCompanies.Count == 1 && _clientContext.CurrentClient is null)
        {
            _selectedCompany = OpenCompanies[0];
            OnPropertyChanged(nameof(SelectedCompany));
            OnPropertyChanged(nameof(HasSelectedCompany));
        }
        else if (OpenCompanies.Count == 0)
        {
            _selectedCompany = null;
            OnPropertyChanged(nameof(SelectedCompany));
            OnPropertyChanged(nameof(HasSelectedCompany));
        }
    }

    private void EnrichCompanyLinks()
    {
        var links = _repository.GetAllLinks()
            .ToDictionary(link => link.CompanyKey, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < OpenCompanies.Count; index++)
        {
            var company = OpenCompanies[index];
            if (links.TryGetValue(company.CompanyKey, out var link))
            {
                company.LinkedClientId = link.ClientId;
                company.LinkedClientCode = link.ClientCode;
                company.LinkedClientName = link.ClientName;
            }
            else
            {
                company.LinkedClientId = 0;
                company.LinkedClientCode = string.Empty;
                company.LinkedClientName = string.Empty;
            }

            // Raise a collection replace notification so DisplayText refreshes
            // immediately in the global dropdown after link/unlink.
            OpenCompanies[index] = company;
        }
        OnPropertyChanged(nameof(OpenCompanies));
    }

    private void SetSelectionFromRememberedLink(TallyCompanyInfo? company)
    {
        _selectedCompany = company;
        _manualSelectionPending = false;
        OnPropertyChanged(nameof(SelectedCompany));
        OnPropertyChanged(nameof(HasSelectedCompany));
        QueueLiveXmlGeneration();
    }

    private void OnAccountingRefreshed(object? sender, EventArgs e) =>
        QueueLiveXmlGeneration();

    private void QueueLiveXmlGeneration()
    {
        if (_disposed) return;
        Interlocked.Increment(ref _liveXmlGeneration);

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _liveXmlTimer.Stop();
            _liveXmlTimer.Start();
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            _liveXmlTimer.Stop();
            _liveXmlTimer.Start();
        }, DispatcherPriority.Background);
    }

    private async void OnLiveXmlTimerTick(object? sender, EventArgs e)
    {
        _liveXmlTimer.Stop();
        var generation = Interlocked.Read(ref _liveXmlGeneration);
        await GenerateLiveXmlNowAsync(generation);
    }

    private async Task GenerateLiveXmlNowAsync(long generation)
    {
        if (_disposed) return;
        if (!await _liveXmlGate.WaitAsync(0))
        {
            QueueLiveXmlGeneration();
            return;
        }

        try
        {
            var client = _clientContext.CurrentClient;
            if (client is null)
            {
                LiveXmlStatus = "Live Tally XML waiting for an active client.";
                LastLiveXmlPath = string.Empty;
                LastLiveXmlReadyVouchers = 0;
                LastLiveXmlNewMasters = 0;
                return;
            }

            var link = _repository.GetLinkForClient(client.Id);
            if (link is null || string.IsNullOrWhiteSpace(link.CompanyName))
            {
                LiveXmlStatus = "Live Tally XML waiting for a confirmed Tally company link.";
                LastLiveXmlPath = string.Empty;
                LastLiveXmlReadyVouchers = 0;
                LastLiveXmlNewMasters = 0;
                DeleteLiveXmlIfPresent(client);
                return;
            }

            var company = new TallyCompanyInfo
            {
                CompanyKey = link.CompanyKey,
                Name = link.CompanyName,
                CompanyNumber = link.CompanyNumber,
                Gstin = link.Gstin,
                StateName = link.StateName,
                BooksFrom = link.BooksFrom,
                StartingFrom = link.StartingFrom,
                FinancialYear = link.FinancialYear
            };
            var (fromDate, toDate) = FinancialYearDates(client.FinancialYear);

            LiveXmlStatus = "Refreshing live Tally XML from current Purchase/Sales/Journal accounting…";
            var generatedAt = DateTime.Now;
            var build = await Task.Run(() =>
            {
                using var trace = ApplicationPerformanceTrace.Measure("Tally.LiveXml.BuildWrite", $"Client={client.ClientCode};FY={client.FinancialYear}");
                var snapshot = _accountingExportService.GenerateLiveXml(
                    client,
                    company,
                    fromDate,
                    toDate);

                var folder = GetLiveXmlFolder(client);
                Directory.CreateDirectory(folder);
                var xmlPath = Path.Combine(folder, "AAH_TALLY_LIVE_CURRENT.xml");
                var statusPath = Path.Combine(folder, "AAH_TALLY_LIVE_CURRENT_STATUS.txt");
                WriteAtomicText(xmlPath, snapshot.Xml);
                WriteAtomicText(
                    statusPath,
                    $"Accounting Automation Hub - Live Tally XML\r\n" +
                    $"Generated: {generatedAt:yyyy-MM-dd HH:mm:ss}\r\n" +
                    $"Client: {client.ClientCode} | FY {client.FinancialYear}\r\n" +
                    $"Tally company: {company.Name}\r\n" +
                    $"Period: {snapshot.FromDate:dd-MM-yyyy} to {snapshot.ToDate:dd-MM-yyyy}\r\n" +
                    $"Selected: {snapshot.Selected:N0}\r\n" +
                    $"Ready in XML: {snapshot.Ready:N0}\r\n" +
                    $"New Masters to CREATE: {snapshot.NewMastersToCreate:N0}\r\n" +
                    $"Missing ledger/details: {snapshot.MissingLedgers:N0}\r\n" +
                    $"Unsupported/incomplete: {snapshot.Unsupported:N0}\r\n" +
                    $"Cached Tally authority ALTERID: {snapshot.AuthorityAlterId:N0}\r\n" +
                    $"Cached Tally exact duplicates skipped: {snapshot.ExactDuplicatesSkipped:N0}\r\n" +
                    $"Cached Tally conflicts blocked: {snapshot.ConflictsBlocked:N0}\r\n" +
                    $"Repeated local source candidates skipped: {snapshot.RepeatedSourceCandidatesSkipped:N0}\r\n\r\n" +
                    "This file is generated automatically from current Hub accounting data using the synchronized Tally authority cache.\r\n" +
                    "It is NOT imported into Tally automatically. Tally Export performs one final lightweight freshness check before sending this prepared work.\r\n");
                return (Snapshot: snapshot, XmlPath: xmlPath);
            });

            if (_disposed || generation != Interlocked.Read(ref _liveXmlGeneration))
            {
                QueueLiveXmlGeneration();
                return;
            }

            var snapshot = build.Snapshot;
            var xmlPath = build.XmlPath;
            LastLiveXmlPath = xmlPath;
            LastLiveXmlReadyVouchers = snapshot.Ready;
            LastLiveXmlNewMasters = snapshot.NewMastersToCreate;
            LiveXmlStatus =
                $"Live XML current • {snapshot.Ready:N0} voucher(s) • {DateTime.Now:HH:mm:ss} • AAH_TALLY_LIVE_CURRENT.xml" +
                (snapshot.MissingLedgers > 0 || snapshot.Unsupported > 0
                    ? $" • needs attention {snapshot.MissingLedgers + snapshot.Unsupported:N0}"
                    : string.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            LiveXmlStatus = "Live Tally XML needs attention: " + exception.Message;
            LastLiveXmlReadyVouchers = 0;
            LastLiveXmlNewMasters = 0;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _liveXmlGate.Release();
        }
    }

    private string GetLiveXmlFolder(ClientProfile client)
    {
        var linked = _automationService.GetFolders()
            .Where(folder => folder.ClientId == client.Id &&
                             folder.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(folder => Directory.Exists(folder.FolderPath))
            .ThenBy(folder => folder.FolderPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (linked is not null && !string.IsNullOrWhiteSpace(linked.FolderPath) &&
            Directory.Exists(linked.FolderPath))
        {
            return linked.FolderPath;
        }

        var safeClient = string.Concat((client.ClientCode.Length > 0 ? client.ClientCode : client.Id.ToString(CultureInfo.InvariantCulture))
            .Where(character => !Path.GetInvalidFileNameChars().Contains(character)));
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Accounting Automation Hub",
            "Live Tally XML",
            safeClient,
            client.FinancialYear);
    }

    private void DeleteLiveXmlIfPresent(ClientProfile client)
    {
        try
        {
            var folder = GetLiveXmlFolder(client);
            var path = Path.Combine(folder, "AAH_TALLY_LIVE_CURRENT.xml");
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Link removal must never fail because an old preview could not be deleted.
        }
    }

    private static void WriteAtomicText(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    private static void ValidateClientCompanyIdentity(
        ClientProfile client,
        TallyCompanyInfo company)
    {
        if (!CanConfirmIdentity(client, company))
        {
            throw new InvalidOperationException(
                IdentityMatchText(client, company));
        }

        if (company.IsLinked && company.LinkedClientId != client.Id)
        {
            throw new InvalidOperationException(
                $"This Tally company is already linked to {company.LinkedClientCode}. Unlink it there first.");
        }
    }
}

public sealed record TallyMasterMergeUpdateResult(
    bool TallyUpdated,
    bool TargetAlreadyExisted,
    bool SourceDeletedInTally,
    string Status,
    bool Deferred = false,
    long PendingMergeId = 0,
    bool AllowTallySourceArchive = false);

public sealed record TallyStockPullResult(
    int ReturnedStockItems,
    int ReturnedUnits,
    int ReturnedGodowns,
    int UpsertedObjects,
    int LiveTallyItemsBefore,
    int LiveTallyItemsAfter,
    int NewMasterIds,
    string Status);
