namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Keep the "Exit Completely" action separate from window hide-to-tray.
/// The losing secondary instance must never clean up databases or accounting
/// services owned by the primary. Manual and Tally jobs block requested exit.
/// </summary>
internal static class AppExitPolicy
{
    internal static bool ShouldCleanupRuntime(bool isOwner) => isOwner;

    internal static bool CanExitCompletely(bool tallyBusy, bool automationBusy, bool distributedBusy) =>
        !tallyBusy && !automationBusy && !distributedBusy;
}
