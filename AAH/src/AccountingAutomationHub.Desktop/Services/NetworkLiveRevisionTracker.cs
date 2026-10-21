namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Monotonic in-process revision for authoritative Main Server accounting changes.
/// LAN clients use it only as a cheap invalidation signal; the actual data remains the
/// server-built read-only snapshot and is downloaded only after the revision changes.
/// </summary>
public static class NetworkLiveRevisionTracker
{
    private static long _accountingDataRevision = 1;

    public static long AccountingDataRevision => Interlocked.Read(ref _accountingDataRevision);

    public static long BumpAccountingData() => Interlocked.Increment(ref _accountingDataRevision);
}
