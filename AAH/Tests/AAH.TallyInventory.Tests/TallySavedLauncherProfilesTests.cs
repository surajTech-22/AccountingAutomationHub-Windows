using System;
using System.IO;

internal static class TallySavedLauncherProfilesTests
{
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("Tally saved launcher profiles: " + reason);
    }

    public static void Run()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,
                   "AAH", "src", "AccountingAutomationHub.Desktop", "Services", "DistributedExecutionFoundationService.cs")))
            root = root.Parent;
        Check(root is not null, "source checkout unavailable");
        static string Read(DirectoryInfo root, string path) =>
            File.ReadAllText(Path.Combine(root.FullName, "AAH", "src",
                "AccountingAutomationHub.Desktop", "Services", path));

        var foundation = Read(root!, "DistributedExecutionFoundationService.cs");
        var launcher = Read(root!, "TallyApplicationLaunchService.cs");
        var privateSession = Read(root!, "TallyPrivateCompanySessionService.cs");
        var start = foundation.IndexOf("public sealed class TallyLinkFoundationService", StringComparison.Ordinal);
        var end = foundation.IndexOf("public sealed record ClientAutoImportPolicy", start, StringComparison.Ordinal);
        Check(start >= 0 && end > start, "original Tally profile persistence class unavailable");
        var profiles = foundation[start..end];

        Check(profiles.Contains("private static readonly object ProfileGate", StringComparison.Ordinal) &&
              profiles.Contains("new Mutex(false, ProfileMutexName)", StringComparison.Ordinal) &&
              profiles.Contains("ownership.WaitOne(", StringComparison.Ordinal) &&
              profiles.Contains("ownership.ReleaseMutex()", StringComparison.Ordinal) &&
              profiles.Contains("WithExclusiveProfileWrite(() =>", StringComparison.Ordinal),
              "all launcher profile writes must be serialized across instances and processes");
        Check(profiles.Contains("File.Replace(temporary, _path, _path + \".bak\"", StringComparison.Ordinal) &&
              profiles.Contains("File.Move(temporary, _path)", StringComparison.Ordinal) &&
              profiles.Contains("ReadValidatedProfiles(backup)", StringComparison.Ordinal) &&
              profiles.Contains("throw new InvalidDataException(", StringComparison.Ordinal) &&
              !profiles.Contains("catch { return []; }", StringComparison.Ordinal),
              "all clients must survive an interrupted save; corrupt profiles must not become an empty list");
        Check(launcher.Contains("TryRestoreVerifiedLauncherProfile(", StringComparison.Ordinal) &&
              launcher.Contains("AutoTallyImportPreference.ExactCompanyKey(preferences, client)", StringComparison.Ordinal) &&
              launcher.Contains("AutoTallyImportPreference.ExactCompanyFolder(preferences, client)", StringComparison.Ordinal) &&
              launcher.Contains("TallyPrivateCompanySessionService.VerifyCompanyFolder(", StringComparison.Ordinal) &&
              launcher.Contains("TallyInstalledProductIdentityService.ReadExecutable(executable)", StringComparison.Ordinal),
              "Tally logo must reuse saved Client/FY/CompanyKey and exact verified folder, not require another Link");
        Check(launcher.Contains("TallyPrivateCompanySessionService.Shared.StartAsync(client, link, executable, automatic: false)", StringComparison.Ordinal) &&
              launcher.Contains("return new TallyLaunchResult(true,", StringComparison.Ordinal) &&
              privateSession.Contains("recorded.CompanyKey.Equals(link.CompanyKey", StringComparison.Ordinal) &&
              privateSession.Contains("AutoTallyImportPreference.ExactCompanyKey(_preferences, client)", StringComparison.Ordinal),
              "existing exact-company opener and link authority must remain in force");
        Console.WriteLine("PASS: saved launcher profiles are atomic and recover from existing verified Client/FY exact company settings.");
    }
}
