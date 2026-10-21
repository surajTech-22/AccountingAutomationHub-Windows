using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Clients;
using AccountingAutomationHub.Infrastructure.Tally;

namespace AccountingAutomationHub.Desktop.Services;

public sealed record TallyLaunchResult(bool Success, string Message);

/// <summary>
/// Opens only the Tally company already confirmed for the active AAH Client/FY.
/// It never edits Tally.ini, company data, vouchers or the saved AAH Tally link.
/// </summary>
public sealed class TallyApplicationLaunchService
{
    private readonly TallyLinkFoundationService _foundation = new();

    public void CaptureConfirmedLink(ClientProfile client, TallyCompanyInfo company, string verifiedDataRoot = "")
    {
        if (client.Id <= 0 || string.IsNullOrWhiteSpace(company.Name))
            return;

        try
        {
            var existing = _foundation.Get(client.Id);
            var sameKey = existing is not null &&
                existing.CompanyKey.Equals(company.CompanyKey, StringComparison.OrdinalIgnoreCase);
            // AAH has freshly verified the linked XML company. Capture the application
            // family ONLY if exactly one Windows Tally process is available and its
            // executable VERSIONINFO identifies the product. Never use registry order
            // or a folder/file extension to label ERP 9 versus Prime.
            var observed = TallyInstalledProductIdentityService.ReadOnlyRunningTally();
            var executable = observed?.ExecutablePath ?? (sameKey ? existing!.ExecutablePath : string.Empty);
            var dataPath = verifiedDataRoot.Length > 0 ? verifiedDataRoot :
                (observed is not null ? ResolveDataPath(executable, null) : string.Empty);
            if (dataPath.Length == 0 && sameKey)
                dataPath = existing!.DataPath;
            var product = observed ?? (sameKey ? TallyInstalledProductIdentityService.ReadExecutable(executable) : null);
            if (observed is not null)
                AutomationDiagnosticLog.Write("TALLY-PRODUCT-VERSION",
                    $"Observed linked company {company.Name}; {product!.Family} {product.ProductVersion} ({product.FileVersion}) from running executable VERSIONINFO.", executable);

            _foundation.Save(new ClientTallyLinkRecord
            {
                ClientId = client.Id,
                HostDeviceId = Environment.MachineName,
                ExecutablePath = executable,
                DataPath = dataPath,
                CompanyName = company.Name.Trim(),
                CompanyGuid = sameKey ? existing!.CompanyGuid : string.Empty,
                CompanyKey = company.CompanyKey.Trim(),
                CompanyNumber = company.CompanyNumber.Trim(),
                ApplicationProductFamily = product?.Family ?? (sameKey ? existing!.ApplicationProductFamily : string.Empty),
                ApplicationProductVersion = product?.ProductVersion ?? (sameKey ? existing!.ApplicationProductVersion : string.Empty),
                ApplicationFileVersion = product?.FileVersion ?? (sameKey ? existing!.ApplicationFileVersion : string.Empty),
                ApplicationVersionEvidence = product?.Evidence ?? (sameKey ? existing!.ApplicationVersionEvidence : string.Empty),
                ApplicationVersionConfirmedUtc = observed is not null ? DateTime.UtcNow :
                    (sameKey ? existing!.ApplicationVersionConfirmedUtc : null),
                Validated = executable.Length > 0 && (product is not null ||
                    (sameKey && existing!.Validated)),
                LastValidatedUtc = DateTime.UtcNow,
                BackupState = existing?.BackupState ?? string.Empty,
                BackupProductFamily = existing?.BackupProductFamily ?? string.Empty,
                BackupClassificationEvidence = existing?.BackupClassificationEvidence ?? string.Empty
            });
        }
        catch
        {
            // The launcher is optional convenience metadata. A filesystem/registry issue
            // must never roll back or block the authoritative Client/FY -> Tally link.
        }
    }

    /// <summary>
    /// After fresh Tally XML discovery AND a targeted live TDL location response,
    /// persist the exact folder against the SAME Client/FY + CompanyKey.
    /// A general Data root, matching company name, or stale Tally.ini is never enough.
    /// </summary>
    public bool SaveVerifiedLiveCompanyFolder(ClientProfile client, TallyClientLink link,
        TallyCompanyInfo live, TallyLiveCompanyLocation location)
    {
        if (client.Id <= 0 || link.ClientId != client.Id ||
            !link.CompanyKey.Equals(live.CompanyKey, StringComparison.OrdinalIgnoreCase) ||
            !link.CompanyName.Equals(live.Name, StringComparison.OrdinalIgnoreCase) ||
            !location.CompanyName.Equals(live.Name, StringComparison.OrdinalIgnoreCase) ||
            (link.Gstin.Length > 0 && live.Gstin.Length > 0 &&
             !link.Gstin.Equals(live.Gstin, StringComparison.OrdinalIgnoreCase))) return false;

        var number = location.CompanyNumber.Trim();
        if (number.Length == 0 || number.Length > 64 || !number.All(char.IsDigit)) return false;
        string candidate;
        try
        {
            // Fix13: SVCurrentPath denotes Tally's LIVE data root, not necessarily
            // the final company directory. A nested data root can itself end in the
            // numeric company ID (e.g. ...\100001\100001). Fix12 treated that
            // root as the company and silently rejected the valid numbered child.
            // Never scan sibling folders, other roots, or Tally's installed defaults.
            var root = Path.GetFullPath(location.DataRoot.Trim().Trim('"'));
            var rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var candidates = rootName.Equals(number, StringComparison.OrdinalIgnoreCase)
                ? new[] { root, Path.Combine(root, number) }
                : new[] { Path.Combine(root, number) };
            var verified = new List<string>();
            var failures = new List<string>();
            foreach (var path in candidates)
            {
                try { verified.Add(TallyPrivateCompanySessionService.VerifyCompanyFolder(path, number)); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    failures.Add(path + ": " + ex.Message);
                }
            }
            if (verified.Count != 1)
            {
                AutomationDiagnosticLog.Write("TALLY-LIVE-PATH",
                    verified.Count > 1
                        ? "Live data root has multiple valid numbered company folders; location not saved (ambiguous)."
                        : "Live data root has no verified numbered company folder; location not saved. " + string.Join(" | ", failures),
                    root);
                return false;
            }
            candidate = verified[0];
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AutomationDiagnosticLog.Write("TALLY-LIVE-PATH", "Invalid live Tally data-root path; existing binding preserved: " + ex.Message, location.DataRoot);
            return false;
        }

        // Database identity is updated only if the same original link still exists.
        var repository = new SqliteTallyRepository();
        repository.Initialize();
        if (!repository.SaveVerifiedLiveCompanyNumber(client.Id, link.CompanyKey, link.CompanyName, number))
        {
            AutomationDiagnosticLog.Write("TALLY-LIVE-PATH", "Live company number was not saved: the linked Client/CompanyKey/CompanyName row no longer matched; existing folder binding preserved.", candidate);
            return false;
        }
        var preferences = new SqliteClientPreferenceRepository();
        preferences.Initialize();
        var existingPath = AutoTallyImportPreference.ExactCompanyFolder(preferences, client);
        var existingKey = AutoTallyImportPreference.ExactCompanyKey(preferences, client);
        if (!existingPath.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
            !existingKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase))
        {
            // Disable the old binding's authority until the replacement is recorded.
            AutoTallyImportPreference.SetExactCompanyKey(preferences, client.Id, client.FinancialYear, string.Empty);
            AutoTallyImportPreference.SetExactCompanyFolder(preferences, client.Id, client.FinancialYear, candidate);
            AutoTallyImportPreference.SetExactCompanyKey(preferences, client.Id, client.FinancialYear, link.CompanyKey);
            AutomationDiagnosticLog.Write("TALLY-LIVE-PATH",
                $"Verified physical company folder for {client.ClientCode}, FY {client.FinancialYear}; " +
                (existingPath.Length == 0 ? "initial capture" : "confirmed location change"), candidate);
        }
        // Refresh launch profile for this machine, but do not use it as company authority.
        CaptureConfirmedLink(client, new TallyCompanyInfo
        {
            Name = live.Name,
            CompanyKey = live.CompanyKey,
            CompanyNumber = number
        }, Path.GetDirectoryName(candidate) ?? string.Empty);
        return true;
    }

    public async Task<TallyLaunchResult> OpenLinkedCompanyAsync(
        ClientProfile client,
        TallyClientLink link,
        TallyLiveConnectionCoordinator coordinator)
    {
        if (client.Id <= 0 || link.ClientId != client.Id)
            return new TallyLaunchResult(false, "The saved Tally link does not belong to the active Client/FY.");

        // Fix9: OpenCompanies is a *remembered* XML discovery list, intentionally kept
        // by the five-second coordinator after a failed/offline probe. It is NEVER
        // fresh evidence that a Windows Tally process or visible window still exists.
        // The logo is a manual *application* launcher; test live Windows processes
        // before deciding to activate an existing instance instead of starting one.
        if (TallyPrivateCompanySessionService.AnyTallyRunning())
        {
            if (TryBringTallyToFront(link.CompanyName, allowAnyTallyWindow: true))
                return new TallyLaunchResult(false,
                    $"Tally is already running. Its window was brought forward, but the linked company '{link.CompanyName}' was NOT verified or selected. Open the linked company in Tally, or close that session normally to enable exact-folder startup.");
            return new TallyLaunchResult(false,
                "Windows reports an existing Tally process, but AAH cannot find its visible main window. " +
                "No second Tally process was started. Check Task Manager for a hidden or unresponsive " +
                "Tally process, then close it normally before retrying the Tally logo.");
        }

        var authoritative = new SqliteTallyRepository();
        authoritative.Initialize();
        var freshLink = authoritative.GetLinkForClient(client.Id);
        if (freshLink is null || !freshLink.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase))
            return new TallyLaunchResult(false, "The linked Tally company changed. Refresh the Client/FY link before opening Tally.");
        link = freshLink;
        var saved = _foundation.Get(client.Id);
        var companyNumber = NormalizeCompanyNumber(link.CompanyNumber);
        // The old Client/FY link may predate Company Number capture. The separate
        // launcher profile can supply it only when it identifies the SAME linked company.
        if (companyNumber.Length == 0 && saved is not null &&
            (link.CompanyKey.Length > 0 && saved.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase)))
            companyNumber = NormalizeCompanyNumber(saved.CompanyNumber);

        var savedExecutableMatchesLink = saved is not null &&
            saved.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(saved.ExecutablePath) && File.Exists(saved.ExecutablePath);
        var executable = savedExecutableMatchesLink ? Path.GetFullPath(saved!.ExecutablePath) :
            ResolveExecutablePath(null, link.CompanyName);
        if (companyNumber.Length == 0 &&
            TryBringTallyToFront(link.CompanyName, allowAnyTallyWindow: true))
            return new TallyLaunchResult(true,
                $"Tally is running. Company Number is unavailable for {link.CompanyName}; select this exact linked company manually, refresh AAH and press Link.");

        if (executable.Length == 0)
        {
            return new TallyLaunchResult(
                false,
                "AAH could not find the Tally executable. Open Tally once, select this linked company and press Link again so AAH can capture the exact local Tally installation.");
        }

        // The logo is a MANUAL Tally launcher, independent of Auto Open and Auto Import.
        // Prefer R11's exact-path startup only when its separately verified folder/key
        // are available. A legacy link lacking those details must not make the logo inert.
        // Never send bare /LOAD:number against global Data: numbers are not unique.
        if (TallyPrivateCompanySessionService.AnyTallyRunning())
        {
            if (TryBringTallyToFront(link.CompanyName, allowAnyTallyWindow: true))
                return new TallyLaunchResult(true,
                    $"Tally was brought forward. {link.CompanyName} is not confirmed loaded; use Tally's Select Company / Specify Path to select the linked company. No second Tally process was launched.");
            return new TallyLaunchResult(false,
                "Tally is already running but its window is unavailable. No second Tally process was launched.");
        }

        if (!savedExecutableMatchesLink)
            return LaunchTallyForManualSelection(executable, link.CompanyName,
                "No executable for the same saved Client/CompanyKey has been verified. Open and link this company once with AAH connected.");
        var exact = await TallyPrivateCompanySessionService.Shared.StartAsync(client, link, executable, automatic: false);
        if (exact.Success) return exact;
        // Only known pre-startup validation failures allow fallback. An exception from
        // Process.Start / private launch could have created a process that has not yet
        // appeared in enumeration. Never retry such an uncertain startup.
        if (!AllowsManualSelectionFallback(exact.Message) ||
            TallyPrivateCompanySessionService.AnyTallyRunning())
            return exact;
        return LaunchTallyForManualSelection(executable, link.CompanyName, exact.Message);
    }

    private static bool AllowsManualSelectionFallback(string reason)
    {
        var prefixes = new[]
        {
            "Client/FY link mismatch; Tally not started.",
            "Exact private-data-root startup was verified for TallyPrime only",
            "Exact company path has not been bound",
            "Set the exact linked company folder",
            "A verified numeric Tally company number is required.",
            "The full company directory must exist",
            "The exact directory has no recognized Tally company profile."
        };
        return prefixes.Any(prefix => reason.StartsWith(prefix, StringComparison.Ordinal));
    }

    // Standalone manual startup: TallyPrime's R10 private-CWD /NOINILOAD behavior was
    // verified, but company-number-less links have no authority to auto-select a company.
    // This starts Tally in its company selection UI without modifying installed Tally.ini,
    // any company files, the AAH link, or automatic-session ownership. ERP9 continues to
    // use its normal executable startup, without assuming R10's private INI behavior.
    private static TallyLaunchResult LaunchTallyForManualSelection(string executable, string linkedName, string whyExactNotAvailable)
    {
        try
        {
            if (TallyPrivateCompanySessionService.AnyTallyRunning())
                return new TallyLaunchResult(false, "Tally started independently; AAH did not open another process.");
            var installation = Path.GetDirectoryName(executable)
                ?? throw new InvalidOperationException("Tally installation directory is missing.");
            var isPrime = installation.Contains("TallyPrime", StringComparison.OrdinalIgnoreCase);
            var working = installation;
            var arguments = string.Empty;
            if (isPrime)
            {
                var iniPath = Path.Combine(installation, "Tally.ini");
                if (!File.Exists(iniPath))
                    return new TallyLaunchResult(false,
                        "TallyPrime installation Tally.ini was not found. AAH will not guess startup configuration.");
                var ini = new List<string>();
                foreach (var line in File.ReadAllLines(iniPath))
                {
                    var trimmed = line.Trim();
                    var divider = trimmed.IndexOf('=');
                    if (divider > 0)
                    {
                        var key = trimmed[..divider].Trim();
                        if (key.Equals("Load", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("Default Companies", StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    ini.Add(line);
                }
                ini.Insert(0, "Default Companies = No");
                working = Path.Combine(Path.GetTempPath(), "AAH_TALLY_MANUAL_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(working);
                File.WriteAllLines(Path.Combine(working, "Tally.ini"), ini, new UTF8Encoding(false));
                arguments = "/NOINILOAD";
            }
            var info = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = working,
                UseShellExecute = false
            };
            using var started = Process.Start(info);
            if (started is null)
                return new TallyLaunchResult(false, "Windows did not start Tally.");
            return new TallyLaunchResult(true,
                $"Tally opened for manual company selection (PID {started.Id}). Linked company '{linkedName}' was NOT automatically selected because exact-folder startup is unavailable: {whyExactNotAvailable}. Select the exact company in Tally; AAH's saved link and import settings were not changed.");
        }
        catch (Exception ex)
        {
            return new TallyLaunchResult(false, "Unable to start Tally for manual selection: " + ex.Message);
        }
    }

    public ClientTallyLinkRecord? GetSavedLaunchProfile(long clientId) =>
        clientId > 0 ? _foundation.Get(clientId) : null;

    /// <summary>Find the actual local Tally executable for its genuine Windows icon.
    /// No Tally process or disk files are changed.</summary>
    public string LocateTallyExecutableForIcon(long clientId)
    {
        var profile = GetSavedLaunchProfile(clientId);
        return ResolveExecutablePath(profile?.ExecutablePath, profile?.CompanyName ?? string.Empty);
    }

    private static bool CompanyMatches(TallyCompanyInfo company, TallyClientLink link)
    {
        if (company.CompanyKey.Length > 0 && link.CompanyKey.Length > 0)
            return company.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase);

        // A numbered or renamed company never matches an explicitly keyed link just
        // because another folder has the same numeric identifier or display name.
        if (link.CompanyKey.Length > 0) return false;
        if (company.CompanyNumber.Length > 0 && link.CompanyNumber.Length > 0)
            return company.CompanyNumber.Trim().Equals(link.CompanyNumber.Trim(), StringComparison.OrdinalIgnoreCase) &&
                   company.Name.Trim().Equals(link.CompanyName.Trim(), StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static string NormalizeCompanyNumber(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > 64)
            return string.Empty;

        // Command-line injection is impossible when only the company-number character set is accepted.
        return text.All(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            ? text
            : string.Empty;
    }

    private static string ResolveExecutablePath(string? savedPath, string companyName)
    {
        if (!string.IsNullOrWhiteSpace(savedPath) && File.Exists(savedPath))
            return Path.GetFullPath(savedPath);

        var running = FindRunningTallyProcesses(companyName)
            .Select(TryGetExecutablePath)
            .FirstOrDefault(path => path.Length > 0 && File.Exists(path));
        if (!string.IsNullOrWhiteSpace(running))
            return running;

        foreach (var registered in RegisteredAppPaths())
        {
            if (File.Exists(registered))
                return Path.GetFullPath(registered);
        }

        foreach (var candidate in KnownExecutableCandidates())
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return string.Empty;
    }

    private static string ResolveDataPath(string executablePath, string? savedDataPath)
    {
        if (!string.IsNullOrWhiteSpace(savedDataPath) && Directory.Exists(savedDataPath))
            return Path.GetFullPath(savedDataPath);

        if (executablePath.Length == 0)
            return string.Empty;

        try
        {
            var directory = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrWhiteSpace(directory))
                return string.Empty;

            var iniCandidates = new[]
            {
                Path.Combine(directory, "Tally.ini"),
                Path.Combine(directory, "tally.ini")
            };
            foreach (var iniPath in iniCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(iniPath))
                    continue;

                foreach (var raw in File.ReadLines(iniPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                        continue;

                    var split = line.IndexOf('=');
                    if (split <= 0)
                        continue;

                    var key = line[..split].Trim();
                    if (!key.Equals("Data", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var value = line[(split + 1)..].Trim().Trim('"');
                    if (value.Length == 0)
                        continue;

                    var resolved = Path.IsPathRooted(value)
                        ? value
                        : Path.Combine(directory, value);
                    return Path.GetFullPath(resolved);
                }
            }

            var defaultData = Path.Combine(directory, "Data");
            return Directory.Exists(defaultData) ? Path.GetFullPath(defaultData) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryBringTallyToFront(string companyName, bool allowAnyTallyWindow)
    {
        foreach (var process in FindRunningTallyProcesses(companyName))
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero)
                    continue;
                if (!allowAnyTallyWindow && companyName.Length > 0 &&
                    !process.MainWindowTitle.Contains(companyName, StringComparison.OrdinalIgnoreCase))
                    continue;
                _ = ShowWindowAsync(process.MainWindowHandle, 9); // SW_RESTORE
                _ = SetForegroundWindow(process.MainWindowHandle);
                return true;
            }
            catch
            {
                // Try another bounded Tally process.
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static IEnumerable<Process> FindRunningTallyProcesses(string companyName)
    {
        var names = new[] { "tally", "tallyprime", "tallyerp9", "tally.ERP9" };
        var results = new List<Process>();
        foreach (var name in names)
        {
            try { results.AddRange(Process.GetProcessesByName(name)); }
            catch { }
        }

        return results
            .GroupBy(process => process.Id)
            .Select(group => group.First())
            .OrderByDescending(process =>
            {
                try
                {
                    return companyName.Length > 0 &&
                           process.MainWindowTitle.Contains(companyName, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            })
            .ThenBy(process => process.Id)
            .ToArray();
    }

    private static string TryGetExecutablePath(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName ?? string.Empty;
            process.Dispose();
            return path;
        }
        catch
        {
            process.Dispose();
            return string.Empty;
        }
    }

    private static IEnumerable<string> RegisteredAppPaths()
    {
        var results = new List<string>();
        var names = new[] { "Tally.exe", "TallyPrime.exe" };
        var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
        foreach (var hive in hives)
        foreach (var view in views)
        foreach (var name in names)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}");
                var value = key?.GetValue(null)?.ToString()?.Trim().Trim('"') ?? string.Empty;
                if (value.Length > 0)
                    results.Add(value);
            }
            catch
            {
                // Registry probing is bounded best-effort discovery only.
            }
        }
        return results;
    }

    private static IEnumerable<string> KnownExecutableCandidates()
    {
        var roots = new[]
        {
            @"C:\TallyPrime",
            @"C:\Tally.ERP9",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TallyPrime"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "TallyPrime"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tally.ERP9"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tally.ERP9"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TallyPrime"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TallyPrime")
        };
        var fileNames = new[] { "Tally.exe", "TallyPrime.exe" };
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var fileName in fileNames)
            yield return Path.Combine(root, fileName);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
}
