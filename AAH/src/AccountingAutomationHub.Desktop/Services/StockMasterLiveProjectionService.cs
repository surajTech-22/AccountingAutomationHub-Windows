using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Infrastructure.Purchases;
using AccountingAutomationHub.Infrastructure.Sales;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Fix137: application-owned, generation-guarded Stock projection. Reads are detached
/// from the UI, writes are restricted to inventory JSON, and concurrent source edits
/// always win. A failed projection retains the saved rows and retries from fresh data.
/// </summary>
public sealed class StockMasterLiveProjectionService : IDisposable
{
    private sealed class ScopeState
    {
        public readonly object Sync = new();
        public long Generation;
        public long CompletedGeneration;
        public bool Running;
        public string Company = string.Empty;
        public string Reason = string.Empty;
        public Task? Worker;
    }

    private readonly IPurchaseRecordRepository _purchaseRepository;
    private readonly ISalesRecordRepository _salesRepository;
    private readonly PurchaseStockNormalizationService _purchaseNormalization;
    private readonly SalesStockNormalizationService _salesNormalization;
    private readonly AccountingLiveChangeHub _liveChangeHub;
    private readonly ConcurrentDictionary<string, ScopeState> _scopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _workLimit = new(2, 2);
    private int _disposed;

    public StockMasterLiveProjectionService(
        IPurchaseRecordRepository purchaseRepository,
        ISalesRecordRepository salesRepository,
        PurchaseStockNormalizationService purchaseNormalization,
        SalesStockNormalizationService salesNormalization,
        AccountingLiveChangeHub liveChangeHub)
    {
        _purchaseRepository = purchaseRepository;
        _salesRepository = salesRepository;
        _purchaseNormalization = purchaseNormalization;
        _salesNormalization = salesNormalization;
        _liveChangeHub = liveChangeHub;
    }

    public void Queue(long clientId, string financialYear, string company, string reason)
    {
        if (Volatile.Read(ref _disposed) != 0 || clientId <= 0 || string.IsNullOrWhiteSpace(financialYear)) return;
        var fy = financialYear.Trim();
        var key = $"{clientId}|{fy.ToUpperInvariant()}";
        var state = _scopes.GetOrAdd(key, _ => new ScopeState());
        lock (state.Sync)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            state.Company = company ?? string.Empty;
            state.Reason = reason ?? string.Empty;
            state.Generation++;
            if (state.Running) return;
            state.Running = true;
            var token = _shutdown.Token;
            state.Worker = Task.Run(() => DrainAsync(state, clientId, fy, token));
        }
    }

    private async Task DrainAsync(ScopeState state, long clientId, string fy, CancellationToken token)
    {
        var failures = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Coalesce bursts, but do not make the open client or page a prerequisite.
                await Task.Delay(failures == 0 ? 180 : Math.Min(30000, 1000 * (1 << Math.Min(failures - 1, 4))), token).ConfigureAwait(false);
                long generation; string company; string reason;
                lock (state.Sync)
                {
                    generation = state.Generation;
                    company = state.Company;
                    reason = state.Reason;
                }
                var acquired = false;
                try
                {
                    await _workLimit.WaitAsync(token).ConfigureAwait(false);
                    acquired = true;
                    var result = ProjectOnce(state, generation, clientId, fy, company, reason, token);
                    if (result.NeedsRetry)
                    {
                        // A concurrent edit/import won. Re-read both modules; never
                        // reapply the old row object or its obsolete JSON.
                        failures++;
                        if (failures >= 3)
                            PipelineRuntimeDiagnostics.RefreshFailed(clientId, fy + "|Stock projection",
                                new InvalidOperationException($"{result.Conflicted} source revision conflict(s); latest saved rows retained. Retrying from SQLite."));
                        continue;
                    }
                    failures = 0;
                    lock (state.Sync)
                    {
                        state.CompletedGeneration = generation;
                        if (state.Generation == generation) return;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    failures++;
                    PipelineRuntimeDiagnostics.RefreshFailed(clientId, fy + "|Stock projection", exception);
                    // A failure is not an empty module. Keep the last-good projection
                    // and retry with a bounded delay, without requiring navigation.
                }
                finally { if (acquired) _workLimit.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lock (state.Sync)
            {
                state.Running = false;
                if (!token.IsCancellationRequested && Volatile.Read(ref _disposed) == 0 &&
                    state.Generation != state.CompletedGeneration)
                {
                    state.Running = true;
                    state.Worker = Task.Run(() => DrainAsync(state, clientId, fy, token));
                }
            }
        }
    }

    private StockProjectionWriteResult ProjectOnce(ScopeState state, long generation,
        long clientId, string fy, string company, string reason, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var purchaseSnapshots = _purchaseRepository.GetStockProjectionSnapshots(clientId, fy);
        var invalidPurchases = purchaseSnapshots.Count(s => s.Record is null);
        var purchases = purchaseSnapshots.Where(s => s.Record is not null && HasPurchaseInventory(s.Record)).ToArray();
        var effectiveCompany = company.Trim();
        if (effectiveCompany.Length == 0)
            effectiveCompany = purchases.Select(s => s.Record!.CompanyName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim() ?? string.Empty;
        var purchaseRows = purchases.Select(s => s.Record!).ToArray();
        var purchaseSummary = purchaseRows.Length == 0 ? new AccountingAutomationHub.Infrastructure.Purchases.StockNormalizationSummary()
            : _purchaseNormalization.Normalize(clientId, fy, effectiveCompany, purchaseRows);
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(state, generation)) return new(0, 0, 0, 0);
        var purchaseResult = purchases.Length == 0 ? new StockProjectionWriteResult(0, 0, 0, 0)
            : _purchaseRepository.ApplyStockProjections(clientId, fy, purchases.Select(s =>
                new StockProjectionChange(s.Record!.PersistentId, s.OriginalRecordJson, s.OriginalUpdatedUtc,
                    s.OriginalBatchId, s.OriginalIsUserEdited, JsonSerializer.Serialize(s.Record))));
        if (purchaseResult.Updated > 0)
            _liveChangeHub.Publish(clientId, fy, AccountingLiveArea.Purchase, $"Live Stock Master projection · {reason}");
        if (purchaseResult.Updated > 0 ||
            purchaseSummary.StockItemsCreated + purchaseSummary.BaseUnitsFilled > 0)
            _liveChangeHub.Publish(clientId, fy, AccountingLiveArea.StockMasters, $"Live Stock Master projection completed · {reason}");

        var salesSnapshots = _salesRepository.GetStockProjectionSnapshots(clientId, fy);
        var invalidSales = salesSnapshots.Count(s => s.Record is null);
        var sales = salesSnapshots.Where(s => s.Record is not null && HasSalesInventory(s.Record)).ToArray();
        if (effectiveCompany.Length == 0)
            effectiveCompany = sales.Select(s => s.Record!.CompanyName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim() ?? string.Empty;
        var salesRows = sales.Select(s => s.Record!).ToArray();
        var salesSummary = salesRows.Length == 0 ? new AccountingAutomationHub.Infrastructure.Sales.StockNormalizationSummary()
            : _salesNormalization.Normalize(clientId, fy, effectiveCompany, salesRows);
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(state, generation)) return purchaseResult;
        var salesResult = sales.Length == 0 ? new StockProjectionWriteResult(0, 0, 0, 0)
            : _salesRepository.ApplyStockProjections(clientId, fy, sales.Select(s =>
                new StockProjectionChange(s.Record!.PersistentId, s.OriginalRecordJson, s.OriginalUpdatedUtc,
                    s.OriginalBatchId, s.OriginalIsUserEdited, JsonSerializer.Serialize(s.Record))));
        if (salesResult.Updated > 0)
            _liveChangeHub.Publish(clientId, fy, AccountingLiveArea.Sales, $"Live Stock Master projection · {reason}");
        if (salesResult.Updated > 0 ||
            salesSummary.StockItemsCreated + salesSummary.BaseUnitsFilled > 0)
            _liveChangeHub.Publish(clientId, fy, AccountingLiveArea.StockMasters, $"Live Stock Master projection completed · {reason}");
        var invalid = invalidPurchases + invalidSales + purchaseResult.Invalid + salesResult.Invalid;
        if (invalid > 0)
            PipelineRuntimeDiagnostics.RefreshFailed(clientId, fy + "|Stock projection",
                new InvalidOperationException($"{invalid} source row(s) were unreadable or invalid. They were not overwritten; explicit source repair is required."));
        return new(purchaseResult.Updated + salesResult.Updated,
            purchaseResult.Unchanged + salesResult.Unchanged,
            purchaseResult.Conflicted + salesResult.Conflicted,
            purchaseResult.Missing + salesResult.Missing, invalid);
    }

    private static bool IsCurrent(ScopeState state, long generation)
    {
        lock (state.Sync) return state.Generation == generation;
    }

    private static bool HasPurchaseInventory(AccountingAutomationHub.Domain.Purchases.PurchaseImportRecord row) =>
        !string.IsNullOrWhiteSpace(row.ItemName) || row.ActualQuantity != 0m || row.BilledQuantity != 0m || !string.IsNullOrWhiteSpace(row.OriginalItemName);

    private static bool HasSalesInventory(AccountingAutomationHub.Domain.Sales.SalesImportRecord row) =>
        !string.IsNullOrWhiteSpace(row.ItemName) || row.ActualQuantity != 0m || row.BilledQuantity != 0m || !string.IsNullOrWhiteSpace(row.OriginalItemName);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        // Do not dispose a semaphore or token source while a worker is still using it.
        // The app owns the service; shutdown never waits on a WPF presentation thread.
        var workers = _scopes.Values.Select(s => s.Worker).Where(t => t is not null).Cast<Task>().ToArray();
        _ = Task.WhenAll(workers).ContinueWith(_ =>
        {
            _workLimit.Dispose();
            _shutdown.Dispose();
        }, TaskScheduler.Default);
    }
}
