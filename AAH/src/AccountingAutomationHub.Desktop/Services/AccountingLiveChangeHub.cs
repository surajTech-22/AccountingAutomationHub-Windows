using System;
using System.Threading;

namespace AccountingAutomationHub.Desktop.Services;

public enum AccountingLiveArea
{
    Masters, StockMasters, Banking, Cash, Purchase, Sales, Journal, Gst,
    TaxCredit, Actions, Statements, Settings
}

public sealed record AccountingLiveChange(
    long ClientId, string FinancialYear, AccountingLiveArea Area, string Reason);

public sealed class AccountingLiveChangeHub
{
    private long _revision;
    public event EventHandler<AccountingLiveChange>? Changed;
    public long LastPublishedRevision => Interlocked.Read(ref _revision);

    public void Publish(long clientId, string financialYear, AccountingLiveArea area, string reason)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear)) return;
        Interlocked.Increment(ref _revision);
        var change = new AccountingLiveChange(clientId, financialYear.Trim(), area, reason);
        var handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler<AccountingLiveChange> handler in handlers.GetInvocationList())
        {
            try { handler(this, change); }
            catch (Exception exception)
            {
                PipelineRuntimeDiagnostics.NavigationFailed("Committed-change subscriber", exception);
            }
        }
    }
}
