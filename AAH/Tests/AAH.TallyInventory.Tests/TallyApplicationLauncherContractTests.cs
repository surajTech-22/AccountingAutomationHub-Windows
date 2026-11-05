using System;
using System.IO;

internal static class TallyApplicationLauncherContractTests
{
    private static void Check(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException("Tally launcher contract: " + failure);
    }

    public static void Run()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(
                   root.FullName, "AAH", "src", "AccountingAutomationHub.Desktop",
                   "Services", "TallyApplicationLaunchService.cs")))
            root = root.Parent;
        Check(root is not null, "source checkout unavailable");
        static string Read(DirectoryInfo root, string path) =>
            File.ReadAllText(Path.Combine(root.FullName, "AAH", "src",
                "AccountingAutomationHub.Desktop", path));

        var launcher = Read(root!, "Services/TallyApplicationLaunchService.cs");
        var shell = Read(root!, "ViewModels/MainWindowViewModel.cs");
        var dashboard = Read(root!, "Views/DashboardView.xaml");
        var start = launcher.IndexOf("public async Task<TallyLaunchResult> OpenLinkedCompanyAsync(",
            StringComparison.Ordinal);
        var end = launcher.IndexOf("private static bool AllowsManualSelectionFallback(", start,
            StringComparison.Ordinal);
        Check(start >= 0 && end > start, "existing linked Tally launcher is missing");
        var open = launcher[start..end];
        var running = open.IndexOf("if (TallyPrivateCompanySessionService.AnyTallyRunning())",
            StringComparison.Ordinal);
        var focus = open.IndexOf("if (TryBringTallyToFront(link.CompanyName, allowAnyTallyWindow: true))",
            running, StringComparison.Ordinal);
        var focusedSuccess = open.IndexOf("return new TallyLaunchResult(true,", focus,
            StringComparison.Ordinal);
        var foregroundFailure = open.IndexOf("return new TallyLaunchResult(false,", focus,
            StringComparison.Ordinal);
        Check(running >= 0 && focus > running &&
              focusedSuccess > focus &&
              (foregroundFailure < 0 || focusedSuccess < foregroundFailure),
              "restoring the existing Tally window must succeed, not show a failure modal that steals focus");
        Check(open.Contains("TallyPrivateCompanySessionService.Shared.StartAsync(", StringComparison.Ordinal) &&
              open.Contains("if (!AllowsManualSelectionFallback(exact.Message) ||", StringComparison.Ordinal) &&
              open.Contains("TallyPrivateCompanySessionService.AnyTallyRunning())", StringComparison.Ordinal) &&
              open.Contains("LaunchTallyForManualSelection(executable, link.CompanyName, exact.Message)", StringComparison.Ordinal),
              "exact saved company startup and no-double-process safety must remain intact");

        var fallback = launcher[end..launcher.IndexOf("private static TallyLaunchResult LaunchTallyForManualSelection(", end,
            StringComparison.Ordinal)];
        Check(fallback.Contains("Verified Client/CompanyKey executable binding is unavailable;", StringComparison.Ordinal),
              "legacy saved Tally launcher metadata must allow ordinary manual-selection startup");
        Check(!fallback.Contains("\"Client/FY link mismatch; Tally not started.\"", StringComparison.Ordinal) &&
              !fallback.Contains("Saved Tally product family differs", StringComparison.Ordinal),
              "foreign Client/FY or product-family identity mismatch must not enable a fallback");
        Check(dashboard.Contains("Command=\"{Binding DataContext.OpenLinkedTallyCommand, RelativeSource={RelativeSource AncestorType={x:Type Window}}}\"",
                  StringComparison.Ordinal) &&
              shell.Contains("OpenLinkedTallyCommand = new AsyncRelayCommand(", StringComparison.Ordinal) &&
              shell.Contains("if (!result.Success)", StringComparison.Ordinal),
              "original dashboard Tally logo must keep using the existing launcher command");
        Check(!open.Contains("ImportXmlAsync(", StringComparison.Ordinal) &&
              !open.Contains("UpsertSourceBinding(", StringComparison.Ordinal) &&
              !open.Contains("UnlinkCurrentClientAsync(", StringComparison.Ordinal),
              "the Tally logo must remain launch-only; no accounting or saved link writes");
        Console.WriteLine("PASS: original Tally logo opens/restores Tally without a false warning, and legacy launch remains safe.");
    }
}
