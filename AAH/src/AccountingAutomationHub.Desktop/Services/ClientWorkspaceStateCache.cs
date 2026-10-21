using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AccountingAutomationHub.Domain.Tax;
using AccountingAutomationHub.Infrastructure.Tax;
using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Application.Gst;
using AccountingAutomationHub.Application.Imports;
using AccountingAutomationHub.Application.Journals;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Domain.Accounting;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Gst;
using AccountingAutomationHub.Domain.Imports;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Sales;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Infrastructure.Accounting;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Sales;
using AccountingAutomationHub.Infrastructure.Persistence;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Completion 61 client/FY/module working-set cache.
///
/// SQLite remains authoritative. Every Client/FY owns an independent memory partition and
/// every module in that partition owns an independent logical stream. Physical SQLite/cache
/// preparation is bounded to a small worker pool so modules do not saturate the database, disk,
/// GC or WPF dispatcher. Priority work for the selected Client/FY overtakes background preload.
///
/// A module is published as soon as its own read/preparation finishes. Persisted cache is also
/// split by module, so restoring one large workspace file can never become a startup barrier.
/// Writes still commit to SQLite first and invalidate only the affected module(s).
/// </summary>
public sealed class ClientWorkspaceStateCache : IDisposable
{
    private static readonly ClientWorkspaceModule[] Modules =
    [
        ClientWorkspaceModule.Banking,
        ClientWorkspaceModule.LedgerMapping,
        ClientWorkspaceModule.Cash,
        ClientWorkspaceModule.Purchase,
        ClientWorkspaceModule.Sales,
        ClientWorkspaceModule.Journal,
        ClientWorkspaceModule.Gst,
        ClientWorkspaceModule.Masters,
        ClientWorkspaceModule.Stock,
        ClientWorkspaceModule.Statements,
        ClientWorkspaceModule.Actions,
        ClientWorkspaceModule.TaxCredit
    ];

    private static readonly string[] StockSimpleMasterTypes =
    [
        "Stock Group", "Stock Category", "Godown", "Cost Category", "Cost Centre",
        "Tax Classification", "TaxUnit"
    ];

    private readonly IBankingRepository _bankingRepository;
    private readonly IPurchaseRecordRepository _purchaseRepository;
    private readonly ISalesRecordRepository _salesRepository;
    private readonly IJournalRepository _journalRepository;
    private readonly IGstRepository _gstRepository;
    private readonly SqliteMasterRepository _masterRepository;
    private readonly IStockMasterRepository _stockMasterRepository;
    private readonly IActionRequiredRepository _actionRequiredRepository;
    private readonly BankLedgerMappingService _ledgerMappingService;
    private readonly LedgerTotalService _ledgerTotalService;
    private readonly GstJournalGenerationService _journalGenerationService;
    private readonly SqliteClientRepository _clientRepository;
    private readonly SqliteTaxCreditRepository _taxCreditRepository;
    private readonly TaxCreditReconciliationService _taxCreditReconciliationService;
    private readonly WorkspaceCacheRevisionStore _revisions;
    private readonly WorkspaceSourceChangeMonitor _sourceChangeMonitor;
    private readonly ConcurrentDictionary<string, (long ClientId, string FinancialYear)> _registeredScopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _authoritativeReadyModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task[] _workers;
    private Task _startupTask = Task.CompletedTask;
    private int _stopping;
    private AccountingLiveChangeHub? _liveHub;


    private static readonly TimeSpan InactiveClientThreshold = TimeSpan.FromDays(183);
    // R43 memory bound: current Client/FY plus two recent scopes remain resident; all other
    // prepared snapshots stay compressed on disk until the user opens that Client/FY.
    private const int MaxResidentClientScopes = 3;

    private const int PersistedCacheSchemaVersion = 7;
    private const long PersistedCacheSoftLimitBytes = 256L * 1024 * 1024;
    private const long PersistedCacheTargetBytes = 192L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, ClientWorkspaceSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _readyModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _runningModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _refreshAgain = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _restoreAttemptedModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _moduleGenerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _queueVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _executingModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _priorityRefreshAgain = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _authoritativeRefreshAgain = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inactiveValidationClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _coldClientScopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _residentScopeTouches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _freezeAfterReady = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _startupAuthoritativePending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _moduleFailureCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<ModuleWorkItem> _priorityWork = new();
    private readonly ConcurrentQueue<ModuleWorkItem> _backgroundWork = new();
    private readonly SemaphoreSlim _workSignal = new(0);
    // R43: cache preparation is I/O/serialization-heavy. More than two workers caused
    // concurrent SQLite reads + JSON/GZip allocations to fight WPF scrolling/GC. Priority
    // work still overtakes background work, but physical preparation is capped at two.
    private static readonly int PreparationWorkerCount = 2;
    private int _snapshotGeneration;
    private int _startupSchedulingComplete;
    private int _persistedMaintenanceScheduled;

    public ClientWorkspaceStateCache(
        IBankingRepository bankingRepository,
        SqlitePurchaseRecordRepository purchaseRepository,
        SqliteSalesRecordRepository salesRepository,
        SqliteJournalRepository journalRepository,
        IGstRepository gstRepository,
        SqliteMasterRepository masterRepository,
        IStockMasterRepository stockMasterRepository,
        IActionRequiredRepository actionRequiredRepository,
        BankLedgerMappingService ledgerMappingService,
        LedgerTotalService ledgerTotalService,
        SqliteClientRepository clientRepository)
    {
        _bankingRepository = bankingRepository;
        _purchaseRepository = purchaseRepository;
        _salesRepository = salesRepository;
        _journalRepository = journalRepository;
        _gstRepository = gstRepository;
        _masterRepository = masterRepository;
        _stockMasterRepository = stockMasterRepository;
        _actionRequiredRepository = actionRequiredRepository;
        _ledgerMappingService = ledgerMappingService;
        _ledgerTotalService = ledgerTotalService;
        _clientRepository = clientRepository;
        _taxCreditRepository = new SqliteTaxCreditRepository();
        _taxCreditRepository.Initialize();
        _taxCreditReconciliationService = new TaxCreditReconciliationService(
            _taxCreditRepository, salesRepository, journalRepository);
        _revisions = new WorkspaceCacheRevisionStore(
            Path.Combine(ApplicationDataPaths.RootFolder, "Cache", "WorkspaceStateV7", "Revisions"),
            WorkspaceCacheRevisionStore.AccountingSourceStamp);
        _journalGenerationService = new GstJournalGenerationService(journalRepository, purchaseRepository, salesRepository);
        Directory.CreateDirectory(PersistedCacheFolder);
        _ = Task.Run(MaintainPersistedCacheStorage);

        // Completion 61 Build Repair 3: every Client/FY/module remains an independent
        // logical stream, but physical SQLite/cache preparation is deliberately bounded.
        // Unbounded Task.Run fan-out created 100+ simultaneous jobs on multi-client firms,
        // starving WPF/GC and making the "parallel" cache slower than the old serial path.
        _workers = Enumerable.Range(0, PreparationWorkerCount)
            .Select(_ => Task.Run(PreparationWorkerLoopAsync)).ToArray();
        // External database/network replacements and older writers may not publish
        // through this process's live-change hub. Observe them independently of pages.
        _sourceChangeMonitor = new WorkspaceSourceChangeMonitor(
            WorkspaceCacheRevisionStore.AccountingSourceStamp, RevalidateRegisteredScopes);
    }

    /// <summary>Raised independently for every Client/FY/module as soon as that module is ready.</summary>
    public event Action<ClientWorkspaceModuleUpdate>? ModuleReady;
    public event Action<ClientWorkspaceModuleUpdate>? ModuleAuthoritativeReady;

    /// <summary>
    /// Raised when an authoritative module read fails. The cache never converts a failed
    /// repository read into a valid empty module. Transient failures are retried automatically;
    /// this event exists so the visible shell/diagnostics can distinguish "still loading" from
    /// "the authoritative SQLite read failed".
    /// </summary>
    public event Action<ClientWorkspaceModuleFailure>? ModuleFailed;

    /// <summary>
    /// Compatibility/full-workspace event. It is raised only after every module in a Client/FY
    /// partition is ready; page hydration must use ModuleReady and must not wait on this event.
    /// </summary>
    public event Action<ClientWorkspaceSnapshot>? SnapshotReady;

    private static void NotifySafely<T>(Action<T>? handlers, T value, string stage)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try { handler(value); }
            catch (Exception exception) { PipelineRuntimeDiagnostics.NavigationFailed(stage, exception); }
        }
    }

    // Scheduling completion is not a global data barrier. Each Client/FY/module has
    // its own authoritative readiness and can be consumed independently.
    public bool IsStartupPreparationComplete => Volatile.Read(ref _startupSchedulingComplete) == 1;

    public bool IsStartupAuthoritativePreparationComplete =>
        IsStartupPreparationComplete && _startupAuthoritativePending.IsEmpty;

    public bool IsModuleCurrent(long clientId, string financialYear, ClientWorkspaceModule module) =>
        IsModuleAuthoritativeReady(clientId, financialYear, module);

    public IReadOnlyList<ClientWorkspaceModule> RegisteredModules => Modules;

    public bool IsModuleAuthoritativeReady(long clientId, string financialYear, ClientWorkspaceModule module) =>
        IsModuleAuthoritativeReadyCore(clientId, financialYear, module);

    private bool IsModuleAuthoritativeReadyCore(long clientId, string financialYear, ClientWorkspaceModule module)
    {
        var key = ModuleKey(clientId, financialYear, module);
        return _authoritativeReadyModules.ContainsKey(key) &&
               _revisions.CanRestore(key, _revisions.Current(key));
    }

    public bool IsUpdateCurrent(ClientWorkspaceModuleUpdate update) =>
        update.SourceGeneration == Volatile.Read(ref _snapshotGeneration) &&
        _revisions.IsCurrent(ModuleKey(update.ClientId, update.FinancialYear, update.Module), update.Generation);

    public void AttachLiveChangeHub(AccountingLiveChangeHub hub)
    {
        if (ReferenceEquals(_liveHub, hub)) return;
        if (_liveHub is not null) _liveHub.Changed -= OnCommittedChange;
        _liveHub = hub;
        hub.Changed += OnCommittedChange;
    }

    private void OnCommittedChange(object? sender, AccountingLiveChange change)
    {
        // The normal in-process commit already tells us exactly which Client/FY and dependency
        // streams changed. Acknowledge its SQLite/WAL watermark before the one-second external
        // safety monitor sees the same write and invalidates every registered workspace.
        _sourceChangeMonitor.AcknowledgeCurrentSourceStamp();
        var modules = change.Area switch
        {
            AccountingLiveArea.Banking => new[] { ClientWorkspaceModule.Banking, ClientWorkspaceModule.LedgerMapping, ClientWorkspaceModule.Statements, ClientWorkspaceModule.Actions },
            AccountingLiveArea.Cash => new[] { ClientWorkspaceModule.Cash, ClientWorkspaceModule.Statements, ClientWorkspaceModule.Actions },
            AccountingLiveArea.Purchase => new[] { ClientWorkspaceModule.Purchase, ClientWorkspaceModule.Journal, ClientWorkspaceModule.Gst, ClientWorkspaceModule.Statements, ClientWorkspaceModule.TaxCredit, ClientWorkspaceModule.Actions },
            AccountingLiveArea.Sales => new[] { ClientWorkspaceModule.Sales, ClientWorkspaceModule.Journal, ClientWorkspaceModule.Gst, ClientWorkspaceModule.Statements, ClientWorkspaceModule.TaxCredit, ClientWorkspaceModule.Actions },
            AccountingLiveArea.Journal => new[] { ClientWorkspaceModule.Journal, ClientWorkspaceModule.Gst, ClientWorkspaceModule.Statements, ClientWorkspaceModule.TaxCredit, ClientWorkspaceModule.Actions },
            AccountingLiveArea.Gst => new[] { ClientWorkspaceModule.Gst, ClientWorkspaceModule.Journal, ClientWorkspaceModule.Actions },
            AccountingLiveArea.Masters => new[] { ClientWorkspaceModule.Masters, ClientWorkspaceModule.LedgerMapping, ClientWorkspaceModule.Journal, ClientWorkspaceModule.Statements, ClientWorkspaceModule.Purchase, ClientWorkspaceModule.Sales, ClientWorkspaceModule.TaxCredit, ClientWorkspaceModule.Actions },
            AccountingLiveArea.StockMasters => new[] { ClientWorkspaceModule.Stock, ClientWorkspaceModule.Purchase, ClientWorkspaceModule.Sales, ClientWorkspaceModule.Actions },
            AccountingLiveArea.TaxCredit => new[] { ClientWorkspaceModule.TaxCredit, ClientWorkspaceModule.Journal, ClientWorkspaceModule.Statements, ClientWorkspaceModule.Actions },
            AccountingLiveArea.Actions => new[] { ClientWorkspaceModule.Actions },
            AccountingLiveArea.Statements => new[] { ClientWorkspaceModule.Statements },
            _ => Modules
        };
        if (change.Area is AccountingLiveArea.Banking or AccountingLiveArea.Cash or AccountingLiveArea.Purchase or
            AccountingLiveArea.Sales or AccountingLiveArea.Journal or AccountingLiveArea.Masters or
            AccountingLiveArea.TaxCredit or AccountingLiveArea.Statements)
            _ledgerTotalService.Invalidate(change.ClientId, change.FinancialYear);
        var clientKey = Key(change.ClientId, change.FinancialYear);
        if (_inactiveValidationClients.ContainsKey(clientKey) || _coldClientScopes.ContainsKey(clientKey))
        {
            MarkModulesDirtyWithoutRefresh(change.ClientId, change.FinancialYear, modules);
            return;
        }
        Invalidate(change.ClientId, change.FinancialYear, modules);
    }

    public ClientWorkspaceSnapshot CopyForPresentation(ClientWorkspaceSnapshot snapshot)
    {
        // Avoid a large temporary UTF-16 JSON string for every presentation clone. UTF-8 bytes
        // materially reduce LOH/GC pressure while preserving the same deep-copy isolation.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        return JsonSerializer.Deserialize<ClientWorkspaceSnapshot>(bytes)!;
    }


    public bool TryPeekReadOnly(long clientId, string financialYear, out ClientWorkspaceSnapshot snapshot)
    {
        // Read-only fast path for Dashboard/navigation/count projections. Never bind editable
        // DataGrid rows directly to this shared authority snapshot.
        if (_snapshots.TryGetValue(Key(clientId, financialYear), out var shared))
        {
            snapshot = shared;
            return true;
        }
        snapshot = default!;
        return false;
    }

    public bool TryGet(long clientId, string financialYear, out ClientWorkspaceSnapshot snapshot)
    {
        // Never perform disk or SQLite work from a caller checking cache readiness.
        // Persisted restore runs inside the same independent module worker as fresh loading.
        if (_snapshots.TryGetValue(Key(clientId, financialYear), out var shared))
        {
            snapshot = CopyForPresentation(shared);
            return true;
        }
        snapshot = default!;
        return false;
    }

    public bool TryGetModulesForPresentation(
        long clientId,
        string financialYear,
        IEnumerable<ClientWorkspaceModule> modules,
        out ClientWorkspaceSnapshot snapshot)
    {
        // R43: navigation must not JSON-clone the entire Client/FY merely to open one page.
        // Project only the visible module streams, then deep-copy that small projection so
        // editable WPF collections remain isolated from shared cache authority.
        if (!_snapshots.TryGetValue(Key(clientId, financialYear), out var shared))
        {
            snapshot = default!;
            return false;
        }

        var projected = ClientWorkspaceSnapshot.Empty(clientId, financialYear) with
        {
            LoadedUtc = shared.LoadedUtc,
            DashboardAccountingMetrics = shared.DashboardAccountingMetrics,
            DashboardTotalRecords = shared.DashboardTotalRecords
        };
        foreach (var module in modules.Distinct())
            projected = MergeModule(projected, shared, module);
        snapshot = CopyForPresentation(projected);
        return true;
    }

    public bool IsModuleReady(long clientId, string financialYear, ClientWorkspaceModule module) =>
        _readyModules.ContainsKey(ModuleKey(clientId, financialYear, module));

    public void StartPreload(IEnumerable<ClientProfile> clients)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        var candidates = clients.Where(client => !client.IsArchived && client.Id > 0).ToArray();
        Volatile.Write(ref _startupSchedulingComplete, 0);
        _startupTask = Task.Run(() =>
        {
            try
            {
                var ordered = candidates
                    .Select(client => new
                    {
                        Client = client,
                        LastOpenedUtc = GetLastOpenedSafely(client)
                    })
                    .OrderByDescending(item => item.LastOpenedUtc)
                    .ThenByDescending(item => item.Client.UpdatedUtc)
                    .ToArray();

                // R43: preload only a bounded recent working set into managed memory. Older,
                // inactive and overflow Client/FYs remain on compressed disk cache and are
                // promoted/restored only when opened. This prevents all-client RAM growth.
                var now = DateTime.UtcNow;
                var residentSlots = 0;
                foreach (var item in ordered)
                {
                    var clientKey = Key(item.Client.Id, item.Client.FinancialYear);
                    _registeredScopes[clientKey] = (item.Client.Id, item.Client.FinancialYear);
                    var activityUtc = item.LastOpenedUtc > item.Client.UpdatedUtc
                        ? item.LastOpenedUtc
                        : item.Client.UpdatedUtc;
                    var inactive = activityUtc != DateTime.MinValue &&
                                   now - activityUtc >= InactiveClientThreshold;
                    var preloadResident = !inactive && residentSlots < MaxResidentClientScopes;

                    if (inactive) _inactiveValidationClients[clientKey] = 0;
                    else _inactiveValidationClients.TryRemove(clientKey, out _);

                    if (!preloadResident)
                    {
                        _coldClientScopes[clientKey] = 0;
                        // R44: RAM residency and snapshot synchronization are separate.
                        // Active overflow Client/FYs still refresh their module snapshots to
                        // compressed disk in the background, but are evicted from managed RAM.
                        // Truly inactive (183+ day) scopes retain the frozen-on-open policy.
                        if (!inactive)
                        {
                            foreach (var module in Modules)
                            {
                                var moduleKey = ModuleKey(item.Client.Id, item.Client.FinancialYear, module);
                                _startupAuthoritativePending[moduleKey] = 0;
                                QueueModuleRefreshInternal(item.Client.Id, item.Client.FinancialYear,
                                    module, priority: false, authoritative: true, forceInactive: true);
                            }
                        }
                        continue;
                    }

                    residentSlots++;
                    _coldClientScopes.TryRemove(clientKey, out _);
                    _residentScopeTouches[clientKey] = DateTime.UtcNow.Ticks;
                    foreach (var module in Modules)
                    {
                        var moduleKey = ModuleKey(item.Client.Id, item.Client.FinancialYear, module);
                        _startupAuthoritativePending[moduleKey] = 0;
                        QueueModuleRefreshInternal(item.Client.Id, item.Client.FinancialYear,
                            module, priority: false, authoritative: true, forceInactive: true);
                    }
                }
            }
            catch (Exception exception)
            {
                PipelineRuntimeDiagnostics.NavigationFailed("Startup cache preparation", exception);
            }
            finally { Volatile.Write(ref _startupSchedulingComplete, 1); }
        });
    }

    private DateTime GetLastOpenedSafely(ClientProfile client)
    {
        try { return _clientRepository.GetWorkspaceLastOpenedUtc(client.Id, client.FinancialYear); }
        catch (Exception ex)
        {
            PipelineRuntimeDiagnostics.NavigationFailed("Cache recent-client priority", ex);
            return DateTime.MinValue;
        }
    }

    private void RevalidateRegisteredScopes()
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        foreach (var scope in _registeredScopes.Values)
        {
            var key = Key(scope.ClientId, scope.FinancialYear);
            if (_inactiveValidationClients.ContainsKey(key) || _coldClientScopes.ContainsKey(key))
            {
                MarkScopeDirtyWithoutRefresh(scope.ClientId, scope.FinancialYear);
                continue;
            }
            Invalidate(scope.ClientId, scope.FinancialYear);
        }
    }

    private void MarkScopeDirtyWithoutRefresh(long clientId, string financialYear) =>
        MarkModulesDirtyWithoutRefresh(clientId, financialYear, Modules);

    private void MarkModulesDirtyWithoutRefresh(long clientId, string financialYear, IEnumerable<ClientWorkspaceModule> modules)
    {
        foreach (var module in modules.Distinct())
        {
            var moduleKey = ModuleKey(clientId, financialYear, module);
            try { _moduleGenerations[moduleKey] = _revisions.MarkDirty(moduleKey); }
            catch (Exception exception)
            {
                PipelineRuntimeDiagnostics.NavigationFailed("Cold cache change checkpoint", exception);
            }
            _authoritativeReadyModules.TryRemove(moduleKey, out _);
            _readyModules.TryRemove(moduleKey, out _);
        }
    }

    private bool ScopeHasRunningWork(long clientId, string financialYear)
    {
        foreach (var module in Modules)
        {
            var moduleKey = ModuleKey(clientId, financialYear, module);
            if (_runningModules.ContainsKey(moduleKey) || _executingModules.ContainsKey(moduleKey)) return true;
        }
        return false;
    }

    private void TrimResidentScopes(string keepKey)
    {
        var targetCount = _snapshots.ContainsKey(keepKey)
            ? MaxResidentClientScopes
            : MaxResidentClientScopes - 1;
        while (_snapshots.Count > targetCount)
        {
            var candidate = _snapshots.Keys
                .Where(key => !key.Equals(keepKey, StringComparison.OrdinalIgnoreCase))
                .Where(key => _registeredScopes.ContainsKey(key))
                .OrderBy(key => _residentScopeTouches.TryGetValue(key, out var ticks) ? ticks : long.MinValue)
                .FirstOrDefault(key =>
                {
                    var scope = _registeredScopes[key];
                    return !ScopeHasRunningWork(scope.ClientId, scope.FinancialYear);
                });
            if (candidate is null) break;
            if (!_registeredScopes.TryGetValue(candidate, out var scope)) break;

            _snapshots.TryRemove(candidate, out _);
            _coldClientScopes[candidate] = 0;
            _residentScopeTouches.TryRemove(candidate, out _);
            foreach (var module in Modules)
            {
                var moduleKey = ModuleKey(scope.ClientId, scope.FinancialYear, module);
                _readyModules.TryRemove(moduleKey, out _);
                _authoritativeReadyModules.TryRemove(moduleKey, out _);
                _restoreAttemptedModules.TryRemove(moduleKey, out _);
            }
        }
    }

    public bool RecordClientOpenedAndEnsurePrepared(ClientProfile? client)
    {
        if (client is null || client.Id <= 0 || string.IsNullOrWhiteSpace(client.FinancialYear)) return false;
        var clientKey = Key(client.Id, client.FinancialYear);
        var wasInactive = _inactiveValidationClients.TryRemove(clientKey, out _);
        var wasCold = _coldClientScopes.TryRemove(clientKey, out _);
        _freezeAfterReady.TryRemove(clientKey, out _);
        _residentScopeTouches[clientKey] = DateTime.UtcNow.Ticks;
        TrimResidentScopes(clientKey);
        _clientRepository.RecordWorkspaceOpened(client.Id, client.FinancialYear);

        // Active clients are already kept current by startup preparation + narrow live
        // invalidations. Opening them must not trigger another all-module refresh. Promote
        // only missing streams. A six-month frozen client is the one exception: opening it
        // explicitly reactivates validation and performs a full background rescan/recache
        // after its stored snapshot has already painted.
        foreach (var module in Modules)
        {
            var moduleKey = ModuleKey(client.Id, client.FinancialYear, module);
            if (wasInactive || wasCold)
            {
                QueueModuleRefreshInternal(client.Id, client.FinancialYear, module, priority: true, authoritative: true, forceInactive: true);
                continue;
            }

            // If startup preparation is already executing this stream, do not schedule a
            // second authoritative pass merely because the user opened the client. A queued
            // background stream may still be promoted to priority before execution.
            if (!IsModuleAuthoritativeReady(client.Id, client.FinancialYear, module) &&
                !_executingModules.ContainsKey(moduleKey))
                QueueModuleRefreshInternal(client.Id, client.FinancialYear, module, priority: true, authoritative: true, forceInactive: true);
        }
        return wasInactive || wasCold;
    }

    public bool IsClientValidationActive(long clientId, string financialYear) =>
        !_inactiveValidationClients.ContainsKey(Key(clientId, financialYear));

    public void QueueRefresh(ClientProfile? client)
    {
        if (client is null) return;
        QueueRefresh(client.Id, client.FinancialYear);
    }

    public void QueueRefresh(long clientId, string financialYear)
    {
        foreach (var module in Modules)
            QueueModuleRefreshInternal(clientId, financialYear, module, priority: false, authoritative: true);
    }

    public void QueuePriorityRefresh(ClientProfile? client)
    {
        if (client is null) return;
        QueuePriorityRefresh(client.Id, client.FinancialYear);
    }

    public void QueuePriorityRefresh(long clientId, string financialYear)
    {
        // The currently selected Client/FY overtakes background preloads without creating
        // an unbounded second set of Task.Run jobs.
        foreach (var module in Modules)
            QueueModuleRefreshInternal(clientId, financialYear, module, priority: true, authoritative: true);
    }

    public void EnsureModulePrepared(long clientId, string financialYear, ClientWorkspaceModule module)
    {
        if (IsModuleAuthoritativeReady(clientId, financialYear, module)) return;
        var key = ModuleKey(clientId, financialYear, module);
        if (_executingModules.ContainsKey(key)) return;
        QueueModuleRefreshInternal(clientId, financialYear, module, priority: true, authoritative: true, forceInactive: true);
    }

    public void QueueModuleRefresh(long clientId, string financialYear, ClientWorkspaceModule module) =>
        QueueModuleRefreshInternal(clientId, financialYear, module, priority: false, authoritative: true);

    public void QueuePriorityModuleRefresh(long clientId, string financialYear, ClientWorkspaceModule module) =>
        QueueModuleRefreshInternal(clientId, financialYear, module, priority: true, authoritative: true, forceInactive: true);

    public void CheckpointPresentationClose(long clientId, string financialYear, IEnumerable<ClientWorkspaceModule> modules)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear) || Volatile.Read(ref _stopping) != 0) return;
        // Closing a page never owns or destroys background authority. It only guarantees that
        // any dirty stream it was presenting is promoted so the newest committed SQLite state
        // replaces the durable cache. Already-current streams are left untouched.
        foreach (var module in modules.Distinct())
        {
            var key = ModuleKey(clientId, financialYear, module);
            if (IsModuleAuthoritativeReadyCore(clientId, financialYear, module)) continue;
            QueueModuleRefreshInternal(clientId, financialYear, module, priority: true, authoritative: true, forceInactive: true);
        }
    }

    private void QueueModuleRefreshInternal(
        long clientId,
        string financialYear,
        ClientWorkspaceModule module,
        bool priority,
        bool authoritative,
        bool forceInactive = false)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear)) return;
        if (Volatile.Read(ref _stopping) != 0) return;
        _registeredScopes[Key(clientId, financialYear)] = (clientId, financialYear);
        var moduleKey = ModuleKey(clientId, financialYear, module);
        PipelineRuntimeDiagnostics.RefreshQueued(clientId, financialYear);

        if (!_runningModules.TryAdd(moduleKey, 0))
        {
            // If preparation is already executing, one follow-up is enough; remember whether
            // the follow-up is priority. If it is only waiting in the background queue, a
            // priority request is promoted by adding a priority copy. The stale background
            // copy is rejected by QueueVersion when it is eventually dequeued.
            if (_executingModules.ContainsKey(moduleKey))
            {
                _refreshAgain[moduleKey] = 0;
                if (priority) _priorityRefreshAgain[moduleKey] = 0;
                if (authoritative) _authoritativeRefreshAgain[moduleKey] = 0;
            }
            else if (priority || authoritative)
            {
                var promotedQueueVersion = _queueVersions.AddOrUpdate(moduleKey, 1L, static (_, current) => current + 1L);
                EnqueueWork(new ModuleWorkItem(
                    clientId,
                    financialYear,
                    module,
                    moduleKey,
                    Volatile.Read(ref _snapshotGeneration),
                    _revisions.Current(moduleKey),
                    promotedQueueVersion,
                    Priority: priority,
                    Authoritative: authoritative));
            }

            PipelineRuntimeDiagnostics.RefreshMerged(clientId, financialYear);
            return;
        }

        var queueVersion = _queueVersions.AddOrUpdate(moduleKey, 1L, static (_, current) => current + 1L);
        EnqueueWork(new ModuleWorkItem(
            clientId,
            financialYear,
            module,
            moduleKey,
            Volatile.Read(ref _snapshotGeneration),
            _revisions.Current(moduleKey),
            queueVersion,
            Priority: priority,
            Authoritative: authoritative));
    }

    private void EnqueueWork(ModuleWorkItem work)
    {
        if (work.Priority) _priorityWork.Enqueue(work);
        else _backgroundWork.Enqueue(work);
        _workSignal.Release();
    }

    private async Task PreparationWorkerLoopAsync()
    {
        while (true)
        {
            try { await _workSignal.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            if (Volatile.Read(ref _stopping) != 0 && _priorityWork.IsEmpty && _backgroundWork.IsEmpty && _executingModules.IsEmpty)
                break;

            ModuleWorkItem work;
            if (!_priorityWork.TryDequeue(out work) && !_backgroundWork.TryDequeue(out work))
                continue;

            // A promoted/stale background queue item may remain after the priority copy ran.
            // QueueVersion prevents that stale item from accidentally consuming a later refresh
            // for the same module key after the priority copy has completed.
            if (!_runningModules.ContainsKey(work.ModuleKey)) continue;
            if (_queueVersions.GetOrAdd(work.ModuleKey, 0L) != work.QueueVersion) continue;
            if (!_executingModules.TryAdd(work.ModuleKey, 0))
            {
                _refreshAgain[work.ModuleKey] = 0;
                if (work.Priority) _priorityRefreshAgain[work.ModuleKey] = 0;
                continue;
            }

            try
            {
                RefreshModuleCore(
                    work.ClientId,
                    work.FinancialYear,
                    work.Module,
                    work.ModuleKey,
                    work.SnapshotGeneration,
                    work.ModuleGeneration,
                    work.Priority,
                    work.Authoritative);
            }
            finally
            {
                _executingModules.TryRemove(work.ModuleKey, out _);
            }
        }
    }

    public void Invalidate(long clientId, string financialYear) =>
        Invalidate(clientId, financialYear, Modules);

    public void Invalidate(long clientId, string financialYear, params ClientWorkspaceModule[] modules)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear)) return;
        var requested = modules is { Length: > 0 } ? modules.Distinct().ToArray() : Modules;
        foreach (var module in requested)
        {
            var moduleKey = ModuleKey(clientId, financialYear, module);
            try { _moduleGenerations[moduleKey] = _revisions.MarkDirty(moduleKey); }
            catch (Exception exception)
            {
                // A failed cache checkpoint must never undo the committed SQLite write.
                // Keep the last-good display and fail visibly; the next startup also
                // validates the database/WAL watermark before trusting a saved snapshot.
                PipelineRuntimeDiagnostics.NavigationFailed("Cache change checkpoint", exception);
            }
            _authoritativeReadyModules.TryRemove(moduleKey, out _);
            _readyModules.TryRemove(moduleKey, out _);
            // Do not delete the only saved copy or clear the last committed grid.
            // The generation-checked worker replaces it after a successful readback.
            QueueModuleRefreshInternal(clientId, financialYear, module, priority: true,
                authoritative: true, forceInactive: true);
        }
    }

    public void ResetForNetworkSnapshot(IEnumerable<ClientProfile> clients)
    {
        Interlocked.Increment(ref _snapshotGeneration);
        var previousScopes = _snapshots.Values.ToArray();
        foreach (var snapshot in previousScopes)
            foreach (var module in Modules)
            {
                var moduleKey = ModuleKey(snapshot.ClientId, snapshot.FinancialYear, module);
                _moduleGenerations[moduleKey] = _revisions.MarkDirty(moduleKey);
            }
        _snapshots.Clear();
        _readyModules.Clear();
        _authoritativeReadyModules.Clear();
        _refreshAgain.Clear();
        _priorityRefreshAgain.Clear();
        _authoritativeRefreshAgain.Clear();
        _inactiveValidationClients.Clear();
        _coldClientScopes.Clear();
        _residentScopeTouches.Clear();
        _freezeAfterReady.Clear();
        _startupAuthoritativePending.Clear();
        _moduleFailureCounts.Clear();
        Volatile.Write(ref _startupSchedulingComplete, 0);
        _restoreAttemptedModules.Clear();
        // Durable revisions survive a network snapshot replacement.
        // An old worker cannot publish into the new source generation.
        _queueVersions.Clear();
        while (_priorityWork.TryDequeue(out var priorityStale))
        {
            if (!_executingModules.ContainsKey(priorityStale.ModuleKey))
                _runningModules.TryRemove(priorityStale.ModuleKey, out _);
        }
        while (_backgroundWork.TryDequeue(out var backgroundStale))
        {
            if (!_executingModules.ContainsKey(backgroundStale.ModuleKey))
                _runningModules.TryRemove(backgroundStale.ModuleKey, out _);
        }
        StartPreload(clients);
    }

    private void TryRestorePersistedModuleForStartup(long clientId, string financialYear, ClientWorkspaceModule module)
    {
        var moduleKey = ModuleKey(clientId, financialYear, module);
        if (!_restoreAttemptedModules.TryAdd(moduleKey, 0)) return;
        if (!TryLoadPersistedModule(clientId, financialYear, module, out var persisted, out var trusted)) return;
        var generation = _revisions.Current(moduleKey);
        TryPublishModule(clientId, financialYear, module, moduleKey,
            Volatile.Read(ref _snapshotGeneration), generation, persisted, trusted, out _);
    }

    private bool TryPublishModule(long clientId, string financialYear, ClientWorkspaceModule module,
        string moduleKey, int generation, long moduleGeneration, ClientWorkspaceSnapshot partial,
        bool authoritative, out ClientWorkspaceSnapshot merged)
    {
        // Freeze repository-owned mutable records before sharing with any page.
        partial = CopyForPresentation(partial);
        ClientWorkspaceSnapshot? result = null;
        var accepted = _revisions.RunIfCurrent(moduleKey, moduleGeneration, () =>
        {
            if (generation != Volatile.Read(ref _snapshotGeneration)) return false;
            result = MergeIntoSnapshot(clientId, financialYear, partial, module);
            _readyModules[moduleKey] = 0;
            if (authoritative && _revisions.IsAuthoritative(moduleKey, moduleGeneration))
                _authoritativeReadyModules[moduleKey] = 0;
            return true;
        });
        merged = result ?? ClientWorkspaceSnapshot.Empty(clientId, financialYear);
        if (!accepted) return false;
        if (AllModulesReady(clientId, financialYear))
            merged = RefreshDashboardOutputIfReady(clientId, financialYear, module);
        // `partial` is already a background-thread deep copy of only this module. Publish that
        // isolated projection plus the small Dashboard output instead of exposing the cumulative
        // authority snapshot or forcing WPF to JSON-clone every module on the dispatcher.
        var presentation = partial with
        {
            DashboardAccountingMetrics = merged.DashboardAccountingMetrics,
            DashboardTotalRecords = merged.DashboardTotalRecords
        };
        var update = new ClientWorkspaceModuleUpdate(clientId, financialYear, module, partial.LoadedUtc,
            presentation, moduleGeneration, authoritative, generation);
        if (authoritative)
            NotifySafely(ModuleAuthoritativeReady, update, "Cache notification/ModuleAuthoritativeReady");
        NotifySafely(ModuleReady, update, "Cache notification/ModuleReady");
        if (AllModulesReady(clientId, financialYear))
        {
            var dashboardOnly = ClientWorkspaceSnapshot.Empty(clientId, financialYear) with
            {
                LoadedUtc = merged.LoadedUtc,
                DashboardAccountingMetrics = merged.DashboardAccountingMetrics,
                DashboardTotalRecords = merged.DashboardTotalRecords
            };
            NotifySafely(SnapshotReady, dashboardOnly, "Cache notification/SnapshotReady");
        }
        return true;
    }

    private void RefreshModuleCore(
        long clientId,
        string financialYear,
        ClientWorkspaceModule module,
        string moduleKey,
        int generation,
        long moduleGeneration,
        bool priority,
        bool authoritative)
    {
        PipelineRuntimeDiagnostics.RefreshBegin(clientId, $"{financialYear}:{module}");
        Exception? failure = null;
        var retryAttempt = 0;
        try
        {
            // Persisted restore belongs to this module's worker. It can provide an immediate
            // warm view, but never performs file I/O on the WPF thread.
            if (_restoreAttemptedModules.TryAdd(moduleKey, 0) &&
                TryLoadPersistedModule(clientId, financialYear, module, out var persisted, out var trusted) &&
                IsGenerationCurrent(moduleKey, generation, moduleGeneration))
            {
                TryPublishModule(clientId, financialYear, module, moduleKey, generation,
                    moduleGeneration, persisted, trusted, out _);

                // Every Client/FY prepares independently. A verified current snapshot
                // can be reused; provisional snapshots are rebuilt from SQLite.
                if (!authoritative || trusted)
                {
                    if (trusted)
                    {
                        _moduleFailureCounts.TryRemove(moduleKey, out _);
                        _startupAuthoritativePending.TryRemove(moduleKey, out _);
                        TryFinalizeFrozenWorkspace(clientId, financialYear);
                    }
                    return;
                }
            }

            // Fix131: a frozen/inactive stream whose snapshot was already restored by the
            // startup first pass must not fall through into a fresh SQLite read merely because
            // its restore attempt was consumed before this worker started. If no saved snapshot
            // was available, the worker may still rebuild it from SQLite as a recovery path.
            if (!authoritative && IsModuleAuthoritativeReady(clientId, financialYear, module)) return;

            var sourceStamp = _revisions.CaptureSourceStamp();
            var partial = LoadModuleSnapshot(clientId, financialYear, module);
            if (!IsGenerationCurrent(moduleKey, generation, moduleGeneration)) return;
            if (!sourceStamp.Equals(_revisions.CaptureSourceStamp(), StringComparison.Ordinal))
            {
                // A concurrent SQLite write changed the source while this multi-table read
                // was being assembled. Keep the last-good view and retry the affected stream.
                _revisions.MarkUncertain(moduleKey);
                _authoritativeReadyModules.TryRemove(moduleKey, out _);
                _refreshAgain[moduleKey] = 0;
                return;
            }

            // A read is not authoritative until its durable projection has committed.
            // A newer edit can invalidate the generation during serialization or I/O.
            if (!SavePersistedModule(partial, module, moduleKey, generation, moduleGeneration, sourceStamp))
            {
                _authoritativeReadyModules.TryRemove(moduleKey, out _);
                _refreshAgain[moduleKey] = 0;
                return;
            }
            if (!TryPublishModule(clientId, financialYear, module, moduleKey, generation,
                    moduleGeneration, partial, authoritative: true, out _)) return;
            _moduleFailureCounts.TryRemove(moduleKey, out _);
            _startupAuthoritativePending.TryRemove(moduleKey, out _);
            TryFinalizeFrozenWorkspace(clientId, financialYear);
        }
        catch (Exception exception)
        {
            // Cache preparation failure never changes authoritative SQLite state. Only the
            // failed module remains unavailable and can be retried independently. A failed
            // local cache stream must not permanently block the separate Tally phase.
            // Fix128: do not silently strand the page in "preparing" forever. Record the
            // actual error, report it to the shell and retry a bounded number of times.
            failure = exception;
            _authoritativeReadyModules.TryRemove(moduleKey, out _);
            retryAttempt = _moduleFailureCounts.AddOrUpdate(moduleKey, 1, static (_, current) => current + 1);
            PipelineRuntimeDiagnostics.RefreshFailed(clientId, $"{financialYear}:{module}", exception);
            NotifySafely(ModuleFailed, new ClientWorkspaceModuleFailure(
                clientId,
                financialYear,
                module,
                retryAttempt,
                exception.Message), "Cache notification/ModuleFailed");
            _startupAuthoritativePending.TryRemove(moduleKey, out _);
        }
        finally
        {
            PipelineRuntimeDiagnostics.RefreshEnd(clientId, $"{financialYear}:{module}");
            PipelineRuntimeDiagnostics.WriteReportIfDue();
            _runningModules.TryRemove(moduleKey, out _);
            if (_refreshAgain.TryRemove(moduleKey, out _))
            {
                var followUpPriority = _priorityRefreshAgain.TryRemove(moduleKey, out _);
                _authoritativeRefreshAgain.TryRemove(moduleKey, out _);
                QueueModuleRefreshInternal(clientId, financialYear, module, followUpPriority, authoritative: true, forceInactive: true);
            }
            else
            {
                _priorityRefreshAgain.TryRemove(moduleKey, out _);
                _authoritativeRefreshAgain.TryRemove(moduleKey, out _);
            }

            // Disk-synchronized overflow scopes must not accumulate full Client/FY objects in RAM.
            var scopeKey = Key(clientId, financialYear);
            if (_coldClientScopes.ContainsKey(scopeKey))
                _snapshots.TryRemove(scopeKey, out _);

            // Retry transient SQLite/cache preparation failures after this worker has fully
            // released the module key. Three bounded retries are enough to ride through a
            // startup writer/WAL/schema burst without creating an endless failure loop.
            if (failure is not null && retryAttempt <= 3)
            {
                var delayMs = 180 * retryAttempt;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                    QueueModuleRefreshInternal(
                        clientId,
                        financialYear,
                        module,
                        priority: true,
                        authoritative: true,
                        forceInactive: true);
                });
            }
        }
    }

    private bool IsGenerationCurrent(string moduleKey, int snapshotGeneration, long moduleGeneration) =>
        snapshotGeneration == Volatile.Read(ref _snapshotGeneration) &&
        _revisions.IsCurrent(moduleKey, moduleGeneration);

    private ClientWorkspaceSnapshot MergeIntoSnapshot(
        long clientId,
        string financialYear,
        ClientWorkspaceSnapshot partial,
        ClientWorkspaceModule module)
    {
        var key = Key(clientId, financialYear);
        return _snapshots.AddOrUpdate(
            key,
            _ => MergeModuleAndBuildDashboardSafe(ClientWorkspaceSnapshot.Empty(clientId, financialYear), partial, module),
            (_, current) => MergeModuleAndBuildDashboardSafe(current, partial, module));
    }

    private static ClientWorkspaceSnapshot MergeModuleAndBuildDashboardSafe(
        ClientWorkspaceSnapshot current,
        ClientWorkspaceSnapshot partial,
        ClientWorkspaceModule module)
    {
        // R43: module merges are intentionally cheap. The former implementation rescanned
        // every Bank/Purchase/Sales/Journal/Master/Action row after *each* of the 12 module
        // completions. Dashboard is consumed only after all currently-invalid streams are
        // ready, so rebuild it once from the latest aggregate in TryPublishModule instead.
        return MergeModule(current, partial, module);
    }

    private ClientWorkspaceSnapshot RefreshDashboardOutputIfReady(long clientId, string financialYear, ClientWorkspaceModule module)
    {
        var key = Key(clientId, financialYear);
        if (!AllModulesReady(clientId, financialYear))
            return _snapshots.TryGetValue(key, out var notReady)
                ? notReady
                : ClientWorkspaceSnapshot.Empty(clientId, financialYear);
        if (!_snapshots.TryGetValue(key, out var latest))
            return ClientWorkspaceSnapshot.Empty(clientId, financialYear);

        try
        {
            // If another module publishes while the projection is being calculated, retry on
            // its newest aggregate rather than writing an older Dashboard over newer data.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                latest = _snapshots.TryGetValue(key, out var current) ? current : latest;
                var projected = BuildDashboardOutput(latest);
                if (_snapshots.TryUpdate(key, projected, latest)) return projected;
            }
            return _snapshots.TryGetValue(key, out var settled) ? settled : latest;
        }
        catch (Exception exception)
        {
            PipelineRuntimeDiagnostics.DashboardProjectionFailed(clientId, financialYear, module.ToString(), exception);
            return latest;
        }
    }

    private ClientWorkspaceSnapshot LoadModuleSnapshot(long clientId, string financialYear, ClientWorkspaceModule module)
    {
        var loaded = ClientWorkspaceSnapshot.Empty(clientId, financialYear) with { LoadedUtc = DateTime.UtcNow };
        switch (module)
        {
            case ClientWorkspaceModule.Banking:
            {
                // Cache preparation is READ-ONLY. Completion 67 originally ran the complete
                // transaction/file/inter-file/account reconciliation every time the Banking
                // cache was prepared, so startup preload + client-open priority refresh could
                // repeat thousands of balance checks even when no Bank data changed.
                // BankStatementImportService and explicit Banking mutation paths own the
                // authoritative balance-control pass; the cache only reads its persisted result.
                var accounts = _bankingRepository.GetAccounts(clientId, financialYear).ToArray();
                var statements = _bankingRepository.GetStatements(clientId, financialYear).ToArray();
                var transactions = _bankingRepository.GetTransactions(clientId, financialYear, includeArchived: true).ToArray();
                var reconciliation = _bankingRepository.GetReconciliation(clientId, financialYear).ToArray();
                var transfers = _bankingRepository.GetTransferMatches(clientId, financialYear).ToArray();
                return loaded with
                {
                    BankAccounts = accounts,
                    BankStatements = statements,
                    BankTransactions = transactions,
                    BankReconciliation = reconciliation,
                    BankTransfers = transfers
                };
            }
            case ClientWorkspaceModule.LedgerMapping:
            {
                // Cache preparation is read-only here as well. Bank imports and explicit
                // Rebuild Mapping actions own coverage repair. Re-reading all Bank transactions
                // and rebuilding mappings merely because the workspace opened caused the
                // Ledger Mapping stream to compete with Banking for the same thousands of rows.
                var mappings = _bankingRepository.GetLedgerMappings(clientId, financialYear).ToArray();
                return loaded with { BankLedgerMappings = mappings };
            }
            case ClientWorkspaceModule.Cash:
            {
                var rows = _bankingRepository.GetCashEntries(clientId, financialYear).ToArray();
                return loaded with { CashEntries = rows };
            }
            case ClientWorkspaceModule.Purchase:
            {
                var rows = _purchaseRepository.GetRecords(clientId, financialYear).ToArray();
                return loaded with { Purchases = rows };
            }
            case ClientWorkspaceModule.Sales:
            {
                // Fix128: Sales cache preparation must distinguish an authoritative empty
                // database from a failed database read. The old Safe(...) wrapper converted
                // every SQLite/schema/read failure into an empty Sales module, which could
                // make a successfully imported client appear as 0 rows after reopening AAH.
                var rows = _salesRepository.GetRecords(clientId, financialYear).ToArray();
                return loaded with { Sales = rows };
            }
            case ClientWorkspaceModule.Journal:
            {
                var vouchers = _journalRepository.GetVouchers(clientId, financialYear).ToArray();
                var settings = SafeValue(() => _journalRepository.GetSettings(clientId, financialYear));
                var mappings = Safe(() => _journalRepository.GetLedgerMappings(clientId, financialYear, pendingOnly: false));
                return loaded with
                {
                    Journals = vouchers,
                    JournalSettings = settings,
                    JournalLedgerMappings = mappings
                };
            }
            case ClientWorkspaceModule.Gst:
            {
                var reconciliation = _gstRepository.GetReconciliation(clientId, financialYear).ToArray();
                var returns = _gstRepository.GetReturns(clientId, financialYear).ToArray();
                var ledgers = _gstRepository.GetLedgerPeriods(clientId, financialYear).ToArray();
                var settings = SafeValue(() => _journalRepository.GetSettings(clientId, financialYear));
                var nilSales = Safe(() => _journalGenerationService.GetNilSalesOptions(clientId, financialYear));
                return loaded with
                {
                    GstReconciliation = reconciliation,
                    GstReturns = returns,
                    GstLedgerPeriods = ledgers,
                    GstJournalSettings = settings,
                    GstNilSalesOptions = nilSales
                };
            }
            case ClientWorkspaceModule.TaxCredit:
            {
                var rows = _taxCreditRepository.GetCurrentRows(clientId, financialYear).ToArray();
                var currentImport = _taxCreditRepository.GetCurrentImport(clientId, financialYear);
                var reconciliation = _taxCreditReconciliationService.Build(clientId, financialYear);
                var ledgers = _masterRepository.Get(clientId, financialYear, false, string.Empty).ToArray();
                _masterRepository.PopulateUsageCounts(clientId, financialYear, ledgers);
                return loaded with
                {
                    TaxCreditRows = rows,
                    TaxCreditImport = currentImport,
                    TaxCreditReconciliationRows = reconciliation.Rows.ToArray(),
                    TaxCreditLedgerMasters = ledgers
                };
            }
            case ClientWorkspaceModule.Masters:
            {
                var masters = _masterRepository.Get(clientId, financialYear, false, string.Empty).ToArray();
                _masterRepository.PopulateUsageCounts(clientId, financialYear, masters);
                return loaded with { Masters = masters };
            }
            case ClientWorkspaceModule.Stock:
            {
                var items = _stockMasterRepository.GetStockItems(clientId, financialYear, true, string.Empty).ToArray();
                var units = _stockMasterRepository.GetUnits(clientId, financialYear, true, string.Empty).ToArray();
                var simple = StockSimpleMasterTypes
                    .SelectMany(type => Safe(() => _stockMasterRepository.GetSimpleMasters(clientId, financialYear, type, true, string.Empty)))
                    .ToArray();
                var aliases = Safe(() => _stockMasterRepository.GetItemAliases(clientId, financialYear));
                var unitMappings = Safe(() => _stockMasterRepository.GetUnitMappings(clientId, financialYear));
                var conversions = Safe(() => _stockMasterRepository.GetQuantityConversionRules(clientId, financialYear));
                var unitProfiles = Safe(() => _stockMasterRepository.GetStockItemUnitProfiles(clientId, financialYear));
                return loaded with
                {
                    StockItems = items,
                    StockUnits = units,
                    StockSimpleMasters = simple,
                    StockItemAliases = aliases,
                    StockUnitMappings = unitMappings,
                    QuantityConversions = conversions,
                    StockItemUnitProfiles = unitProfiles
                };
            }
            case ClientWorkspaceModule.Statements:
            {
                // Statements own their complete read model. They intentionally do not wait
                // for the Masters worker even though both read Master data.
                var masters = _masterRepository.Get(clientId, financialYear, false, string.Empty).ToArray();
                var totals = _ledgerTotalService.GetAll(clientId, financialYear).ToArray();
                return loaded with { StatementMasters = masters, LedgerTotals = totals };
            }
            case ClientWorkspaceModule.Actions:
            {
                var actions = _actionRequiredRepository.GetOpen(clientId, financialYear).ToArray();
                return loaded with { OpenActions = actions };
            }
            default:
                return loaded;
        }
    }

    private static ClientWorkspaceSnapshot MergeModule(
        ClientWorkspaceSnapshot current,
        ClientWorkspaceSnapshot partial,
        ClientWorkspaceModule module) => module switch
    {
        ClientWorkspaceModule.Banking => current with
        {
            LoadedUtc = Max(current.LoadedUtc, partial.LoadedUtc),
            BankAccounts = partial.BankAccounts,
            BankStatements = partial.BankStatements,
            BankTransactions = partial.BankTransactions,
            BankReconciliation = partial.BankReconciliation,
            BankTransfers = partial.BankTransfers
        },
        ClientWorkspaceModule.LedgerMapping => current with
        {
            LoadedUtc = Max(current.LoadedUtc, partial.LoadedUtc),
            BankLedgerMappings = partial.BankLedgerMappings
        },
        ClientWorkspaceModule.Cash => current with { LoadedUtc = Max(current.LoadedUtc, partial.LoadedUtc), CashEntries = partial.CashEntries },
        ClientWorkspaceModule.Purchase => current with { LoadedUtc = Max(current.LoadedUtc, partial.LoadedUtc), Purchases = partial.Purchases },
        ClientWorkspaceModule.Sales => current with { LoadedUtc = Max(current.LoadedUtc, partial.LoadedUtc), Sales = partial.Sales },
        ClientWorkspaceModule.Journal => current with
        {
            LoadedUtc = Max(current.LoadedUtc, partial.LoadedUtc),
            Journals = partial.Journals,
            JournalSettings = partial.JournalSettings,
            JournalLedgerMappings = partial.JournalLedgerMappings
        },
        ClientWorkspaceModule.Gst => current with
        {
            LoadedUtc = Max(current.LoadedUtc, partial.LoadedUtc),
            GstReconciliation = partial.GstReconciliation,
            GstReturns = partial.GstReturns,
            GstLedgerPeriods = partial.GstLedgerPeriods,
            GstJournalSettings = partial.GstJournalSettings,
            GstNilSalesOptions = partial.GstNilSalesOptions
        },
        ClientWorkspaceModule.Masters => current with { LoadedUtc = Max(current.LoadedUtc, partial.LoadedUtc), Masters = partial.Masters },
        ClientWorkspaceModule.Stock => current with
        {
            LoadedUtc = Max(current.LoadedUtc, partial.LoadedUtc),
            StockItems = partial.StockItems,
            StockUnits = partial.StockUnits,
            StockSimpleMasters = partial.StockSimpleMasters,
            StockItemAliases = partial.StockItemAliases,
            StockUnitMappings = partial.StockUnitMappings,
            QuantityConversions = partial.QuantityConversions,
            StockItemUnitProfiles = partial.StockItemUnitProfiles
        },
        ClientWorkspaceModule.Statements => current with
        {
            LoadedUtc = Max(current.LoadedUtc, partial.LoadedUtc),
            StatementMasters = partial.StatementMasters,
            LedgerTotals = partial.LedgerTotals
        },
        ClientWorkspaceModule.Actions => current with { LoadedUtc = Max(current.LoadedUtc, partial.LoadedUtc), OpenActions = partial.OpenActions },
        ClientWorkspaceModule.TaxCredit => current with
        {
            LoadedUtc = Max(current.LoadedUtc, partial.LoadedUtc),
            TaxCreditRows = partial.TaxCreditRows,
            TaxCreditImport = partial.TaxCreditImport,
            TaxCreditReconciliationRows = partial.TaxCreditReconciliationRows,
            TaxCreditLedgerMasters = partial.TaxCreditLedgerMasters
        },
        _ => current
    };

    private static ClientWorkspaceSnapshot BuildDashboardOutput(ClientWorkspaceSnapshot snapshot)
    {
        var (fyFrom, fyTo) = TallyLiveConnectionCoordinator.FinancialYearDates(snapshot.FinancialYear);
        var bankRows = snapshot.BankTransactions
            .Where(row => !(row.RecordStatus ?? string.Empty).StartsWith("Archived", StringComparison.OrdinalIgnoreCase))
            .Where(row => row.TransactionDate.Date >= fyFrom.Date && row.TransactionDate.Date <= fyTo.Date)
            .ToArray();
        var purchaseCount = snapshot.Purchases
            .Where(row => row.GstDate is DateTime date && row.DocumentDate.HasValue &&
                          (row.DocumentNumber ?? string.Empty).Trim().Length > 0 && (row.SupplierName ?? string.Empty).Trim().Length > 0 &&
                          date.Date >= fyFrom.Date && date.Date <= fyTo.Date)
            .Select(row => string.Join("|",
                DashboardKey(row.Destination), DashboardKey(row.DocumentType), DashboardKey(row.DocumentNumber),
                row.GstDate!.Value.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
                DashboardKey(row.SupplierGstin), DashboardKey(row.SupplierName)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var salesCount = snapshot.Sales
            .Where(row => (row.GstDate ?? row.DocumentDate) is DateTime date &&
                          date.Date >= fyFrom.Date && date.Date <= fyTo.Date)
            .Select(row => string.Join("|",
                DashboardKey(row.Destination), DashboardKey(row.DocumentType), DashboardKey(row.DocumentNumber),
                (row.GstDate ?? row.DocumentDate)!.Value.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
                DashboardKey(row.CustomerGstin), DashboardKey(row.CustomerName)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var fyJournals = snapshot.Journals.Where(row => row.VoucherDate.Date >= fyFrom.Date && row.VoucherDate.Date <= fyTo.Date).ToArray();
        var cashRows = snapshot.CashEntries.Where(row => row.EntryDate.Date >= fyFrom.Date && row.EntryDate.Date <= fyTo.Date).ToArray();
        var creditorCount = snapshot.Purchases
            .Where(row => row.GstDate is DateTime date && date.Date >= fyFrom.Date && date.Date <= fyTo.Date)
            .Select(row => DashboardKey((row.SupplierGstin ?? string.Empty).Length > 0 ? row.SupplierGstin : row.SupplierName))
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var debtorCount = snapshot.Sales
            .Where(row => (row.GstDate ?? row.DocumentDate) is DateTime date && date.Date >= fyFrom.Date && date.Date <= fyTo.Date)
            .Select(row => DashboardKey((row.CustomerGstin ?? string.Empty).Length > 0 ? row.CustomerGstin : row.CustomerName))
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        var activeBankIds = bankRows.Select(row => row.Id).Where(id => id > 0).ToHashSet();
        var mappedBankIds = snapshot.BankLedgerMappings
            .Where(row => (row.Status ?? string.Empty).Equals("Mapped", StringComparison.OrdinalIgnoreCase))
            .SelectMany(row => row.TransactionIds).Where(activeBankIds.Contains).ToHashSet();
        var suspense = bankRows.Count(row => !(row.Mode ?? string.Empty).Equals("Contra", StringComparison.OrdinalIgnoreCase) &&
                                            (string.IsNullOrWhiteSpace(row.LedgerName) ||
                                             !(row.MappingStatus ?? string.Empty).Equals("Mapped", StringComparison.OrdinalIgnoreCase)));
        var createdBankLedgers = snapshot.Masters
            .Where(item => (item.Type ?? string.Empty).Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !item.IsArchived)
            .Where(item => (item.Source ?? string.Empty).Contains("Bank Ledger Mapping", StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.Name ?? string.Empty).Trim()).Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var pendingMapping = Math.Max(0, activeBankIds.Count - mappedBankIds.Count);
        var completion = activeBankIds.Count == 0 ? 0d : Math.Round(mappedBankIds.Count * 100d / activeBankIds.Count, 1);
        var purchaseReview = snapshot.Purchases.Count(row =>
            (row.ReconciliationStatus ?? string.Empty).Contains("Review", StringComparison.OrdinalIgnoreCase) ||
            (row.ReconciliationStatus ?? string.Empty).Contains("Mismatch", StringComparison.OrdinalIgnoreCase));
        var automaticJournal = fyJournals.Count(row => (row.GenerationMode ?? string.Empty).Equals("Automatic", StringComparison.OrdinalIgnoreCase));
        var manualJournal = fyJournals.Length - automaticJournal;
        var journalReview = fyJournals.Count(DashboardJournalNeedsReview);
        var cashLedgers = cashRows
            .Select(row => (((row.CommonLedger ?? string.Empty).Length > 0 ? row.CommonLedger : row.EntryLedger) ?? string.Empty).Trim())
            .Where(name => name.Length > 0)
            .GroupBy(DashboardKey, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.First()}: {group.Count():N0}")
            .ToArray();

        int ActionCount(string module) => snapshot.OpenActions
            .Where(item => (item.Module ?? string.Empty).Equals(module, StringComparison.OrdinalIgnoreCase))
            .Count(ActionRequiredPolicy.IsImportBlocking);
        var bankActions = ActionCount("Banking");
        var purchaseActions = ActionCount("Purchase");
        var salesActions = ActionCount("Sales") + ActionCount("Credit Note");
        var journalActions = ActionCount("Journal");
        var cashActions = ActionCount("Cash Entry");

        IReadOnlyList<DashboardAccountingMetric> metrics =
        [
            new DashboardAccountingMetric
            {
                Label = "Bank Statement", Value = bankRows.Length,
                Detail = $"{snapshot.BankAccounts.Count:N0} bank(s) • {bankRows.Length:N0} transaction(s) • {createdBankLedgers:N0} ledger(s) created • {suspense:N0} in Suspense",
                TargetPage = "Banking", HasActionRequired = bankActions > 0, ActionRequiredCount = bankActions,
                IsBankStatementMetric = true, PendingMappingCount = pendingMapping, CompletedMappingCount = mappedBankIds.Count,
                MappingTransactionCount = activeBankIds.Count, MappingCompletionPercent = completion,
                StatusState = bankActions > 0 ? "Red" : bankRows.Length > 0 ? "Green" : "Grey"
            },
            new DashboardAccountingMetric
            {
                Label = "Purchase", Value = purchaseCount,
                Detail = $"{creditorCount:N0} creditor(s) • {purchaseCount:N0} voucher(s) • {purchaseReview:N0} reconciliation review",
                TargetPage = "Purchase", HasActionRequired = purchaseActions > 0, ActionRequiredCount = purchaseActions,
                StatusState = purchaseActions > 0 ? "Red" : purchaseCount > 0 ? "Green" : "Grey"
            },
            new DashboardAccountingMetric
            {
                Label = "Sales", Value = salesCount, Detail = $"{debtorCount:N0} debtor(s) • {salesCount:N0} voucher(s)",
                TargetPage = "Sales", HasActionRequired = salesActions > 0, ActionRequiredCount = salesActions,
                StatusState = salesActions > 0 ? "Red" : salesCount > 0 ? "Green" : "Grey"
            },
            new DashboardAccountingMetric
            {
                Label = "Journal", Value = fyJournals.Length,
                Detail = $"Manual {manualJournal:N0} • Automatic {automaticJournal:N0} • {journalReview:N0} review",
                TargetPage = "Journal", HasActionRequired = journalActions > 0, ActionRequiredCount = journalActions,
                StatusState = journalActions > 0 ? "Red" : fyJournals.Length > 0 ? "Green" : "Grey"
            },
            new DashboardAccountingMetric
            {
                Label = "Cash Entries", Value = cashRows.Length,
                Detail = cashLedgers.Length > 1 ? string.Join(" • ", cashLedgers) : $"{cashRows.Length:N0} cash-entry row(s)",
                TargetPage = "Cash Entry", HasActionRequired = cashActions > 0, ActionRequiredCount = cashActions,
                StatusState = cashActions > 0 ? "Red" : cashRows.Length > 0 ? "Green" : "Grey"
            },
            new DashboardAccountingMetric
            {
                Label = "Stock Master", Value = snapshot.StockItems.Count,
                Detail = snapshot.StockItems.Count > 0 ? $"{snapshot.StockItems.Count:N0} stock item(s)" : "Open Stock Master for manual setup/import",
                TargetPage = "Stock Master", StatusState = snapshot.StockItems.Count > 0 ? "Green" : "Grey"
            }
        ];

        return snapshot with
        {
            DashboardAccountingMetrics = metrics,
            DashboardTotalRecords = bankRows.Length + purchaseCount + salesCount + fyJournals.Length + cashRows.Length
        };
    }

    private static bool DashboardJournalNeedsReview(JournalVoucher voucher)
    {
        if (voucher.Lines.Count == 0) return true;
        var debit = voucher.Lines.Where(line => (line.DrCr ?? string.Empty).Equals("Dr", StringComparison.OrdinalIgnoreCase)).Sum(line => line.Amount);
        var credit = voucher.Lines.Where(line => (line.DrCr ?? string.Empty).Equals("Cr", StringComparison.OrdinalIgnoreCase)).Sum(line => line.Amount);
        return voucher.Lines.Any(line => string.IsNullOrWhiteSpace(line.LedgerName)) || Math.Abs(debit - credit) > 0.01m;
    }

    private static string DashboardKey(string? value) =>
        string.Join(" ", (value ?? string.Empty).Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static ClientWorkspaceSnapshot ClearModules(ClientWorkspaceSnapshot current, IReadOnlyCollection<ClientWorkspaceModule> modules)
    {
        foreach (var module in modules) current = MergeModule(current, ClientWorkspaceSnapshot.Empty(current.ClientId, current.FinancialYear), module);
        return current;
    }

    private bool AllModulesReady(long clientId, string financialYear) =>
        Modules.All(module => IsModuleReady(clientId, financialYear, module));

    private static DateTime Max(DateTime left, DateTime right) => left >= right ? left : right;

    private static bool NeedsLedgerCoverageRebuild(
        IReadOnlyList<BankTransactionRecord> transactions,
        IReadOnlyList<BankLedgerMapping> mappings)
    {
        if (transactions.Count == 0) return false;
        var eligibleIds = transactions
            .Where(item => item.Id > 0)
            .Where(item => !item.RecordStatus.StartsWith("Archived", StringComparison.OrdinalIgnoreCase))
            .Where(item => !ExcelLedgerMappingParser.IsLedgerMappingCoverageExcludedMode(item.Mode))
            .Select(item => item.Id)
            .ToHashSet();
        if (eligibleIds.Count == 0) return false;

        var coveredIds = mappings
            .SelectMany(item => item.TransactionIds)
            .Where(id => id > 0)
            .ToHashSet();
        return eligibleIds.Any(id => !coveredIds.Contains(id));
    }

    private readonly record struct ModuleWorkItem(
        long ClientId,
        string FinancialYear,
        ClientWorkspaceModule Module,
        string ModuleKey,
        int SnapshotGeneration,
        long ModuleGeneration,
        long QueueVersion,
        bool Priority,
        bool Authoritative);

    private static string PersistedCacheFolder =>
        Path.Combine(ApplicationDataPaths.RootFolder, "Cache", "WorkspaceStateV7");

    private static string FrozenCacheFolder =>
        Path.Combine(ApplicationDataPaths.RootFolder, "Cache", "WorkspaceStateV7-Frozen");

    // Fix132 compatibility: Fix118-Fix127 paint-ready snapshots used schema/folder V5.
    // V6 remains the write authority, but an existing V5 snapshot is a safe acceleration
    // source and must be allowed to paint immediately instead of forcing a full SQLite
    // rebuild for every older client. Missing newer fields deserialize to defaults; the
    // normal authoritative V6 refresh replaces/migrates the snapshot in the background.
    private static string PreviousPersistedCacheFolder =>
        Path.Combine(ApplicationDataPaths.RootFolder, "Cache", "WorkspaceStateV6");

    private static string PreviousFrozenCacheFolder =>
        Path.Combine(ApplicationDataPaths.RootFolder, "Cache", "WorkspaceStateV6-Frozen");

    private static string LegacyPersistedCacheFolder =>
        Path.Combine(ApplicationDataPaths.RootFolder, "Cache", "WorkspaceStateV5");

    private static string LegacyFrozenCacheFolder =>
        Path.Combine(ApplicationDataPaths.RootFolder, "Cache", "WorkspaceStateV5-Frozen");

    private static string FrozenCachePath(long clientId, string financialYear, ClientWorkspaceModule module)
    {
        var safeFy = new string((financialYear ?? string.Empty)
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
            .ToArray());
        return Path.Combine(FrozenCacheFolder, $"{clientId}_{safeFy}_{module}.json.gz");
    }

    private void TryFinalizeFrozenWorkspace(long clientId, string financialYear)
    {
        var clientKey = Key(clientId, financialYear);
        if (!_freezeAfterReady.ContainsKey(clientKey) || !AllModulesReady(clientId, financialYear)) return;
        try
        {
            Directory.CreateDirectory(FrozenCacheFolder);
            foreach (var module in Modules)
            {
                var source = PersistedCachePath(clientId, financialYear, module);
                if (!File.Exists(source)) return;
            }
            foreach (var module in Modules)
                File.Copy(PersistedCachePath(clientId, financialYear, module), FrozenCachePath(clientId, financialYear, module), true);
            _clientRepository.SetWorkspaceCacheValidationActive(clientId, financialYear, false);
            _freezeAfterReady.TryRemove(clientKey, out _);
        }
        catch
        {
            // A frozen copy is an acceleration snapshot only. SQLite remains authoritative
            // and the next startup can retry finalizing this inactive Client/FY.
        }
    }

    private static string PersistedCachePath(long clientId, string financialYear, ClientWorkspaceModule module)
    {
        var safeFy = new string((financialYear ?? string.Empty)
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
            .ToArray());
        return Path.Combine(PersistedCacheFolder, $"{clientId}_{safeFy}_{module}.json.gz");
    }

    private bool TryLoadPersistedModule(
        long clientId,
        string financialYear,
        ClientWorkspaceModule module,
        out ClientWorkspaceSnapshot snapshot, out bool trusted)
    {
        snapshot = default!;
        trusted = false;
        var safeFy = new string((financialYear ?? string.Empty)
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
            .ToArray());
        var fileName = $"{clientId}_{safeFy}_{module}.json.gz";
        var candidates = new (string Path, int Schema)[]
        {
            (Path.Combine(PersistedCacheFolder, fileName), PersistedCacheSchemaVersion),
            (Path.Combine(PersistedCacheFolder, fileName + ".previous"), PersistedCacheSchemaVersion),
            (Path.Combine(FrozenCacheFolder, fileName), PersistedCacheSchemaVersion),
            (Path.Combine(PreviousPersistedCacheFolder, fileName), 6),
            (Path.Combine(PreviousFrozenCacheFolder, fileName), 6),
            (Path.Combine(LegacyPersistedCacheFolder, fileName), 5),
            (Path.Combine(LegacyFrozenCacheFolder, fileName), 5)
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate.Path)) continue;
            try
            {
                using var file = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                var envelope = JsonSerializer.Deserialize<PersistedWorkspaceModuleEnvelope>(gzip);
                if (envelope?.Snapshot is null ||
                    envelope.SchemaVersion != candidate.Schema ||
                    envelope.Module != module ||
                    envelope.Snapshot.ClientId != clientId ||
                    !envelope.Snapshot.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
                    continue;
                snapshot = envelope.Snapshot;
                trusted = candidate.Schema == PersistedCacheSchemaVersion &&
                    _revisions.CanRestore(ModuleKey(clientId, financialYear, module), envelope.Revision);
                return true;
            }
            catch
            {
                // A bad acceleration file is never accounting authority. Delete only the
                // unreadable candidate and continue to the next V6/V5/frozen fallback.
                PipelineRuntimeDiagnostics.NavigationFailed("Cache restore candidate", new InvalidDataException(candidate.Path));
            }
        }
        return false;
    }

    private bool SavePersistedModule(ClientWorkspaceSnapshot snapshot, ClientWorkspaceModule module,
        string moduleKey, int generation, long moduleGeneration, string expectedSourceStamp)
    {
        if (!IsGenerationCurrent(moduleKey, generation, moduleGeneration)) return false;
        Directory.CreateDirectory(PersistedCacheFolder);
        var path = PersistedCachePath(snapshot.ClientId, snapshot.FinancialYear, module);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                JsonSerializer.Serialize(gzip, new PersistedWorkspaceModuleEnvelope
                {
                    SchemaVersion = PersistedCacheSchemaVersion,
                    Module = module,
                    Revision = moduleGeneration,
                    Snapshot = snapshot
                });
            }
            if (generation != Volatile.Read(ref _snapshotGeneration)) return false;
            if (!_revisions.CommitIfCurrent(moduleKey, moduleGeneration, expectedSourceStamp,
                    () =>
                    {
                        if (File.Exists(path)) File.Copy(path, path + ".previous", true);
                        File.Move(temp, path, true);
                    })) return false;
            QueuePersistedCacheMaintenance();
            return true;
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private void QueuePersistedCacheMaintenance()
    {
        if (Interlocked.Exchange(ref _persistedMaintenanceScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000).ConfigureAwait(false);
                MaintainPersistedCacheStorage();
            }
            finally
            {
                Interlocked.Exchange(ref _persistedMaintenanceScheduled, 0);
            }
        });
    }

    private static void MaintainPersistedCacheStorage()
    {
        try
        {
            Directory.CreateDirectory(PersistedCacheFolder);
            var now = DateTime.UtcNow;
            foreach (var temp in Directory.EnumerateFiles(PersistedCacheFolder, "*.tmp"))
            {
                try { if (now - File.GetLastWriteTimeUtc(temp) > TimeSpan.FromDays(1)) File.Delete(temp); }
                catch { }
            }

            // V7 is the current acceleration format. Once the same module has a V7 copy,
            // older V5/V6 acceleration files are reproducible duplicates and can be removed.
            foreach (var legacyRoot in new[]
                     {
                         PreviousPersistedCacheFolder, PreviousFrozenCacheFolder,
                         LegacyPersistedCacheFolder, LegacyFrozenCacheFolder
                     })
            {
                if (!Directory.Exists(legacyRoot)) continue;
                foreach (var legacy in Directory.EnumerateFiles(legacyRoot, "*.json.gz"))
                {
                    try
                    {
                        var name = Path.GetFileName(legacy);
                        if (File.Exists(Path.Combine(PersistedCacheFolder, name)) ||
                            File.Exists(Path.Combine(FrozenCacheFolder, name)))
                            File.Delete(legacy);
                    }
                    catch { }
                }
            }

            // A V7 frozen copy is only an acceleration fallback. If the same current V7
            // module exists, the frozen byte-for-byte role is redundant and may safely go.
            if (Directory.Exists(FrozenCacheFolder))
            {
                foreach (var frozen in Directory.EnumerateFiles(FrozenCacheFolder, "*.json.gz"))
                {
                    try
                    {
                        if (File.Exists(Path.Combine(PersistedCacheFolder, Path.GetFileName(frozen))))
                            File.Delete(frozen);
                    }
                    catch { }
                }
            }

            // `.previous` is crash acceleration only. Bound it before it can double a very
            // large multi-client cache. The latest module snapshot is never evicted here.
            var currentFiles = Directory.EnumerateFiles(PersistedCacheFolder, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(file => !file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            long total = currentFiles.Sum(file => SafeCacheLength(file));
            if (total > PersistedCacheSoftLimitBytes)
            {
                foreach (var previous in currentFiles
                             .Where(file => file.Name.EndsWith(".previous", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(file => file.LastWriteTimeUtc))
                {
                    if (total <= PersistedCacheTargetBytes) break;
                    var length = SafeCacheLength(previous);
                    try
                    {
                        previous.Delete();
                        total = Math.Max(0, total - length);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static long SafeCacheLength(FileInfo file)
    {
        try { return file.Length; }
        catch { return 0L; }
    }

    private sealed class PersistedWorkspaceModuleEnvelope
    {
        public PersistedWorkspaceModuleEnvelope() { }
        public int SchemaVersion { get; set; }
        public ClientWorkspaceModule Module { get; set; }
        public long Revision { get; set; }
        public ClientWorkspaceSnapshot? Snapshot { get; set; }
    }

    private static IReadOnlyList<T> Safe<T>(Func<IReadOnlyList<T>> loader) => loader().ToArray();

    private static T? SafeValue<T>(Func<T> loader) where T : class => loader();

    public async Task StopAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        if (_liveHub is not null) _liveHub.Changed -= OnCommittedChange;
        try { await _sourceChangeMonitor.StopAsync(timeout).ConfigureAwait(false); }
        catch (Exception ex) { PipelineRuntimeDiagnostics.NavigationFailed("Cache source monitor shutdown", ex); }
        try { await _startupTask.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { }
        // Finish queued work within the shutdown budget. A job that cannot finish
        // leaves its revision dirty, so the next launch rebuilds from SQLite.
        var deadline = DateTime.UtcNow + timeout;
        while ((!_priorityWork.IsEmpty || !_backgroundWork.IsEmpty || !_executingModules.IsEmpty) &&
               DateTime.UtcNow < deadline)
            await Task.Delay(50).ConfigureAwait(false);
        _lifetime.Cancel();
        _workSignal.Release(PreparationWorkerCount);
        try { await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
    }

    public void Dispose()
    {
        try { StopAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
        catch (Exception exception) { PipelineRuntimeDiagnostics.NavigationFailed("Cache shutdown", exception); }
    }

    private static string Key(long clientId, string financialYear) =>
        $"{clientId}\u001f{financialYear.Trim().ToUpperInvariant()}";

    private static string ModuleKey(long clientId, string financialYear, ClientWorkspaceModule module) =>
        $"{Key(clientId, financialYear)}\u001f{module}";
}

public enum ClientWorkspaceModule
{
    Banking,
    LedgerMapping,
    Cash,
    Purchase,
    Sales,
    Journal,
    Gst,
    Masters,
    Stock,
    Statements,
    Actions,
    TaxCredit
}

public sealed record ClientWorkspaceModuleUpdate(
    long ClientId,
    string FinancialYear,
    ClientWorkspaceModule Module,
    DateTime LoadedUtc,
    ClientWorkspaceSnapshot Snapshot,
    long Generation = 0,
    bool IsAuthoritative = false,
    int SourceGeneration = 0);

public sealed record ClientWorkspaceModuleFailure(
    long ClientId,
    string FinancialYear,
    ClientWorkspaceModule Module,
    int Attempt,
    string Error);

public sealed record ClientWorkspaceSnapshot
{
    public long ClientId { get; init; }
    public string FinancialYear { get; init; } = string.Empty;
    public DateTime LoadedUtc { get; init; }
    public IReadOnlyList<BankAccountRecord> BankAccounts { get; init; } = [];
    public IReadOnlyList<BankStatementRecord> BankStatements { get; init; } = [];
    public IReadOnlyList<BankTransactionRecord> BankTransactions { get; init; } = [];
    public IReadOnlyList<BankReconciliationSummary> BankReconciliation { get; init; } = [];
    public IReadOnlyList<BankTransferMatch> BankTransfers { get; init; } = [];
    public IReadOnlyList<BankLedgerMapping> BankLedgerMappings { get; init; } = [];
    public IReadOnlyList<CashEntryRecord> CashEntries { get; init; } = [];
    public IReadOnlyList<PurchaseImportRecord> Purchases { get; init; } = [];
    public IReadOnlyList<SalesImportRecord> Sales { get; init; } = [];
    public IReadOnlyList<JournalVoucher> Journals { get; init; } = [];
    public JournalGenerationSettings? JournalSettings { get; init; }
    public IReadOnlyList<JournalLedgerMapping> JournalLedgerMappings { get; init; } = [];
    public IReadOnlyList<GstReconciliationRow> GstReconciliation { get; init; } = [];
    public IReadOnlyList<Gstr3bReturnRecord> GstReturns { get; init; } = [];
    public IReadOnlyList<GstLedgerPeriodBalance> GstLedgerPeriods { get; init; } = [];
    public JournalGenerationSettings? GstJournalSettings { get; init; }
    public IReadOnlyList<GstNilSalesPeriodOption> GstNilSalesOptions { get; init; } = [];
    public IReadOnlyList<TaxCreditStatementRow> TaxCreditRows { get; init; } = [];
    public TaxCreditStatementImport? TaxCreditImport { get; init; }
    public IReadOnlyList<TaxCreditReconciliationRow> TaxCreditReconciliationRows { get; init; } = [];
    public IReadOnlyList<MasterRecord> TaxCreditLedgerMasters { get; init; } = [];
    public IReadOnlyList<MasterRecord> Masters { get; init; } = [];
    public IReadOnlyList<StockItemRecord> StockItems { get; init; } = [];
    public IReadOnlyList<StockUnitRecord> StockUnits { get; init; } = [];
    public IReadOnlyList<StockSimpleMasterRecord> StockSimpleMasters { get; init; } = [];
    public IReadOnlyList<StockItemAliasRecord> StockItemAliases { get; init; } = [];
    public IReadOnlyList<StockUnitMappingRecord> StockUnitMappings { get; init; } = [];
    public IReadOnlyList<QuantityConversionRule> QuantityConversions { get; init; } = [];
    public IReadOnlyList<StockItemUnitProfileRecord> StockItemUnitProfiles { get; init; } = [];
    public IReadOnlyList<MasterRecord> StatementMasters { get; init; } = [];
    public IReadOnlyList<LedgerTotalSnapshot> LedgerTotals { get; init; } = [];
    public IReadOnlyList<ActionRequiredItem> OpenActions { get; init; } = [];
    // Fix38 paint-ready Dashboard output is prepared with module cache work, never calculated
    // by Dashboard during client selection.
    public IReadOnlyList<DashboardAccountingMetric> DashboardAccountingMetrics { get; init; } = [];
    public int DashboardTotalRecords { get; init; }

    public static ClientWorkspaceSnapshot Empty(long clientId, string financialYear) => new()
    {
        ClientId = clientId,
        FinancialYear = financialYear ?? string.Empty,
        LoadedUtc = DateTime.MinValue
    };
}
