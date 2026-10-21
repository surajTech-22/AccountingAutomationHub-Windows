namespace AccountingAutomationHub.Desktop.Services;

internal static class SingleInstanceOwnership
{
    // Existing kernel object does not imply an existing owner. A retained handle
    // can keep a named mutex alive after the previous application's thread exits.
    internal static bool TryAcquire(Mutex mutex, bool createdNew)
    {
        if (createdNew) return true; // new Mutex(initiallyOwned: true)
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; } // WaitOne granted ownership
    }
}
