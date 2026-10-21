using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Clients;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// R24: R10/R11 verified company-number startup with private CWD/Tally.ini.
/// This service NEVER injects a second company into an existing Tally process.
/// Only AAH-owned automatic startup processes may be closed automatically.
/// </summary>
public sealed class TallyPrivateCompanySessionService
{
    public static TallyPrivateCompanySessionService Shared { get; } = new();
    private readonly SqliteClientPreferenceRepository _preferences = new();
    private readonly object _gate = new();
    private static readonly SemaphoreSlim StartupGate = new(1, 1);
    private OwnedSession? _owned;

    // Fix32: Tally can display two successive startup screens. Limit automatic
    // minimization to TWO visible startup presentations for this verified
    // AAH-owned PID/start-time, then stop permanently. Manual launch is normal.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    private static uint? ReadLastUserInputTick()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.dwTime : null;
    }

    private async Task MinimizeAutomaticStartupPresentationsAsync(Process process, int pid, DateTime startUtc)
    {
        if (!OperatingSystem.IsWindows()) return;
        var successfulMinimizes = 0;
        var firstHwnd = IntPtr.Zero;
        var observedMinimizedBetweenPresentations = false;
        var observedInitialIconicWindow = false;
        uint? userInputTickAtInitialIconic = null;
        uint? userInputTickAfterFirst = null;
        try
        {
            // Bounded *startup-only* window, never a process-lifetime or import
            // monitor. The second startup show may reuse the same HWND.
            for (var attempt = 0; attempt < 48; attempt++)
            {
                await Task.Delay(250).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_owned is not { Automatic: true } current ||
                        current.Process.Id != pid || current.StartedUtc != startUtc)
                        return;
                }
                process.Refresh();
                if (process.HasExited) return;
                var hwnd = process.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;
                if (IsIconic(hwnd))
                {
                    if (successfulMinimizes > 0)
                        observedMinimizedBetweenPresentations = true;
                    else if (!observedInitialIconicWindow)
                    {
                        observedInitialIconicWindow = true;
                        userInputTickAtInitialIconic = ReadLastUserInputTick();
                    }
                    continue;
                }

                // The startup hint may already have minimized Tally before the
                // first GUI is discovered. A user's later Restore is not an
                // automatic second launch; respect input even in that case.
                if (successfulMinimizes == 0 && observedInitialIconicWindow)
                {
                    var currentInput = ReadLastUserInputTick();
                    if (userInputTickAtInitialIconic.HasValue && currentInput.HasValue &&
                        userInputTickAtInitialIconic.Value != currentInput.Value)
                    {
                        AutomationDiagnosticLog.Write("TALLY-AUTO-BACKGROUND",
                            $"User restored initially minimized Tally PID {pid}; leaving it visible.");
                        return;
                    }
                }

                if (successfulMinimizes > 0)
                {
                    // A second distinct window, or the same window genuinely
                    // restored after we observed the first minimize take effect.
                    // Never fire two minimize calls against the same paint event.
                    if (hwnd == firstHwnd && !observedMinimizedBetweenPresentations)
                        continue;
                    var currentUserInput = ReadLastUserInputTick();
                    if (userInputTickAfterFirst.HasValue && currentUserInput.HasValue &&
                        userInputTickAfterFirst.Value != currentUserInput.Value)
                    {
                        AutomationDiagnosticLog.Write("TALLY-AUTO-BACKGROUND",
                            $"User input occurred after automatic startup minimize for PID {pid}; leaving restored Tally alone.");
                        return;
                    }
                }

                if (!ShowWindowAsync(hwnd, 7)) // SW_SHOWMINNOACTIVE; never activate
                    continue;
                successfulMinimizes++;
                if (successfulMinimizes == 1)
                {
                    firstHwnd = hwnd;
                    observedMinimizedBetweenPresentations = false;
                    userInputTickAfterFirst = ReadLastUserInputTick();
                }
                AutomationDiagnosticLog.Write("TALLY-AUTO-BACKGROUND",
                    $"AAH-owned Tally PID {pid} startup minimize {successfulMinimizes}/2. No further minimization after the second startup presentation.");
                if (successfulMinimizes == 2) return;
            }
            AutomationDiagnosticLog.Write("TALLY-AUTO-BACKGROUND",
                $"AAH-owned Tally PID {pid} startup window expired with {successfulMinimizes}/2 supplemental minimizes; no later automatic minimize is allowed.");
        }
        catch (Exception ex)
        {
            AutomationDiagnosticLog.Write("TALLY-AUTO-BACKGROUND",
                "Bounded automatic startup minimize stopped: " + ex.Message);
        }
    }

    // API shared with the manual exporter. The automatic import must NEVER
    // poll/minimize the Tally window. This wrapper is intentionally transparent
    // to MasterID, duplicate authority, queuing, and successful write readback.
    public Task<T> WithoutAutomaticForegroundAsync<T>(ClientProfile client, Func<Task<T>> operation)
    {
        return operation();
    }

    private sealed record OwnedSession(Process Process, DateTime StartedUtc, long ClientId, string Fy, string CompanyFolder, bool Automatic);

    public TallyPrivateCompanySessionService() => _preferences.Initialize();

    public static string VerifyCompanyFolder(string folder, string companyNumber)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Set the exact linked company folder in Settings > Auto Tally Import. The global Tally.ini Data root is not a company identity.");
        if (string.IsNullOrWhiteSpace(companyNumber) || !companyNumber.All(char.IsDigit))
            throw new InvalidOperationException("A verified numeric Tally company number is required.");
        var full = Path.GetFullPath(folder.Trim().Trim('"'));
        if (!Directory.Exists(full) || !Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)).Equals(companyNumber, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The full company directory must exist and end in its linked company number. No fallback to a different folder is allowed.");
        if (!new[] { "Company.1800", "Company.900", "Company.500" }.Any(file => File.Exists(Path.Combine(full, file))))
            throw new InvalidOperationException("The exact directory has no recognized Tally company profile. No launch attempted.");
        return full;
    }

    public static bool AnyTallyRunning()
    {
        foreach (var name in new[] { "tally", "tallyprime", "tallyerp9" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { return true; } // fail closed if unable to enumerate
            try { if (processes.Length > 0) return true; }
            finally { foreach (var p in processes) p.Dispose(); }
        }
        return false;
    }

    public static string CreatePrivateIni(string executable, string companyFolder)
    {
        var source = Path.Combine(Path.GetDirectoryName(executable)!, "Tally.ini");
        if (!File.Exists(source)) throw new FileNotFoundException("Installed Tally.ini not found", source);
        // R11 (proven on the user's TallyPrime): the private Data setting must be
        // INSIDE [Tally] if that section exists. A top-of-file, unscoped Data line
        // can be ignored, leaving Tally to fall back to Public\\TallyPrime\\data.
        // Never edit the installed INI or the company folder.
        var original = File.ReadAllBytes(source);
        System.Text.Encoding encoding;
        int bom = 0;
        if (original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF)
        {
            encoding = new UTF8Encoding(true); bom = 3;
        }
        else if (original.Length >= 2 && original[0] == 0xFF && original[1] == 0xFE)
        {
            encoding = new UnicodeEncoding(false, true); bom = 2;
        }
        else if (original.Length >= 2 && original[0] == 0xFE && original[1] == 0xFF)
        {
            encoding = new UnicodeEncoding(true, true); bom = 2;
        }
        else
        {
            // Byte-preserving for non-BOM legacy INI files (including ANSI).
            encoding = System.Text.Encoding.Latin1;
        }
        var sourceText = encoding.GetString(original, bom, original.Length - bom);
        var incomingLines = sourceText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<string>();
        foreach (var line in incomingLines)
        {
            var trimmed = line.Trim();
            var split = trimmed.IndexOf('=');
            if (!trimmed.StartsWith(';') && !trimmed.StartsWith('#') && split > 0)
            {
                var key = trimmed[..split].Trim();
                if (key.Equals("Data", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Load", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Default Companies", StringComparison.OrdinalIgnoreCase))
                {
                    // Match tested R11: retain old settings only as comments.
                    result.Add("; AAH Fix12 disabled startup setting: " + line);
                    continue;
                }
            }
            result.Add(line);
        }
        var tallyHeaders = result.Select((value, index) => (value, index))
            .Where(x => x.value.Trim().Equals("[Tally]", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.index).ToArray();
        if (tallyHeaders.Length > 1)
            throw new InvalidOperationException("Multiple [Tally] INI sections; exact company startup safely refused.");
        var dataRoot = Path.GetDirectoryName(companyFolder)
            ?? throw new InvalidOperationException("Company folder has no parent data directory.");
        var insertAt = tallyHeaders.Length == 1 ? tallyHeaders[0] + 1 : 0;
        result.InsertRange(insertAt, new[] { "Data = " + dataRoot, "Default Companies = No" });
        // Before starting a process, fail closed unless there is exactly one
        // effective Data and no effective Load, and Data resides in [Tally].
        var activeData = result.Select((value, index) => (value, index))
            .Where(x => !x.value.TrimStart().StartsWith(';') &&
                        !x.value.TrimStart().StartsWith('#') &&
                        x.value.TrimStart().StartsWith("Data", StringComparison.OrdinalIgnoreCase) &&
                        x.value.TrimStart().Length > 4 &&
                        x.value.TrimStart()[4..].TrimStart().StartsWith('='))
            .ToArray();
        var activeLoad = result.Any(line =>
            !line.TrimStart().StartsWith(';') && !line.TrimStart().StartsWith('#') &&
            line.TrimStart().StartsWith("Load", StringComparison.OrdinalIgnoreCase) &&
            line.TrimStart().Length > 4 && line.TrimStart()[4..].TrimStart().StartsWith('='));
        if (activeData.Length != 1 || activeData[0].index != insertAt || activeLoad ||
            (tallyHeaders.Length == 1 && !result.Take(insertAt).Last().Trim().Equals("[Tally]", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Private Tally.ini exact data-root validation failed; Tally not started.");
        var privateRoot = Path.Combine(Path.GetTempPath(), "AAH_TALLY_PRIVATE_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateRoot);
        var privateIni = Path.Combine(privateRoot, "Tally.ini");
        var content = string.Join("\r\n", result);
        File.WriteAllText(privateIni, content, encoding);
        if (!string.Equals(File.ReadAllText(privateIni, encoding), content, StringComparison.Ordinal))
            throw new InvalidOperationException("Private Tally.ini did not read back identically; Tally not started.");
        AutomationDiagnosticLog.Write("TALLY-PRIVATE-DATA-ROOT",
            "Verified private Tally.ini Data root inside [Tally] section; original installed Tally.ini untouched.", dataRoot);
        return privateRoot;
    }

    public async Task<TallyLaunchResult> StartAsync(ClientProfile client, TallyClientLink link, string executable, bool automatic)
    {
        if (client.Id != link.ClientId ||
            (link.FinancialYear.Length > 0 && !client.FinancialYear.Equals(link.FinancialYear, StringComparison.OrdinalIgnoreCase)))
            return new TallyLaunchResult(false, "Client/FY link mismatch; Tally not started.");
        if (!File.Exists(executable)) return new TallyLaunchResult(false, "Saved Tally executable not found.");
        // Fix14: ERP 9 Series A Release 6.6 also passed a controlled Windows test
        // (/NOINILOAD /LOAD:10002 + isolated CWD Tally.ini, verified PID/GUID/path).
        // Classify the REAL installed binary using Windows product VERSIONINFO.
        // Do not infer family from Company.900, folder names or XML VERSION.
        var product = TallyInstalledProductIdentityService.ReadExecutable(executable);
        var legacyPrimePath = Path.GetDirectoryName(Path.GetFullPath(executable))!
            .Contains("TallyPrime", StringComparison.OrdinalIgnoreCase);
        // Preserve the previously working Prime path for older Prime builds with
        // incomplete VERSIONINFO; never allow a positively identified ERP9 binary
        // to pass that compatibility fallback.
        var isPrime = product?.Family == "TallyPrime" || (product is null && legacyPrimePath);
        var isTestedErp9 = product?.Family == "Tally.ERP 9" &&
            product.ProductVersion.Equals("Series A Release 6.6", StringComparison.OrdinalIgnoreCase);
        if (!isPrime && !isTestedErp9)
            return new TallyLaunchResult(false,
                "Unknown or untested Tally software family/release; exact company startup safely refused. ERP 9 tested only for Series A Release 6.6.");
        var recorded = new TallyLinkFoundationService().Get(client.Id);
        if (recorded is null || !recorded.CompanyKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(recorded.ExecutablePath) ||
            !Path.GetFullPath(recorded.ExecutablePath).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
            return new TallyLaunchResult(false, "Verified Client/CompanyKey executable binding is unavailable; exact startup safely refused.");
        if (recorded.ApplicationProductFamily.Length > 0 &&
            !recorded.ApplicationProductFamily.Equals(product?.Family ?? "TallyPrime", StringComparison.OrdinalIgnoreCase))
            return new TallyLaunchResult(false, "Saved Tally product family differs from the installed executable; exact startup safely refused.");
        var storedKey = AutoTallyImportPreference.ExactCompanyKey(_preferences, client);
        if (string.IsNullOrWhiteSpace(storedKey) || !storedKey.Equals(link.CompanyKey, StringComparison.OrdinalIgnoreCase))
            return new TallyLaunchResult(false, "Exact company path has not been bound to this confirmed Tally CompanyKey. Re-save the path in Auto Tally Import settings.");
        string folder;
        try { folder = VerifyCompanyFolder(AutoTallyImportPreference.ExactCompanyFolder(_preferences, client), link.CompanyNumber); }
        catch (Exception ex) { return new TallyLaunchResult(false, ex.Message); }
        await StartupGate.WaitAsync();
        try
        {
        lock (_gate)
        {
            if (_owned is { } held && IsRunning(held.Process))
                return new TallyLaunchResult(false, "An AAH-owned Tally session is already active.");
            if (AnyTallyRunning())
                return new TallyLaunchResult(false, "Tally is already running. AAH will not create a second process or load an unverified company into it.");
        }
        string working;
        try
        {
            working = CreatePrivateIni(executable, folder);
            // UseShellExecute=false is important: the verified private working-directory must be honored.
            var info = new ProcessStartInfo(executable, $"/NOINILOAD /LOAD:{link.CompanyNumber}")
            {
                WorkingDirectory = working, UseShellExecute = false,
                // Background Auto Import must not make Tally the foreground window.
                // WindowStyle is a startup hint for Tally's own GUI (not a hidden
                // console, process injection or a second company); Manual Open remains normal.
                WindowStyle = automatic ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal
            };
            var process = Process.Start(info) ?? throw new InvalidOperationException("Tally did not start.");
            var startedUtc = process.StartTime.ToUniversalTime();
            lock (_gate) _owned = new OwnedSession(process, startedUtc, client.Id, client.FinancialYear, folder, automatic);
            if (automatic)
                _ = MinimizeAutomaticStartupPresentationsAsync(process, process.Id, startedUtc);
            await Task.Yield();
            AutomationDiagnosticLog.Write("TALLY-AUTO-OPEN", $"Started {product?.Family ?? "TallyPrime"} {product?.ProductVersion ?? "legacy"} exact company {link.CompanyName} in AAH-owned Tally PID {process.Id}; awaiting the normal Master/Voucher authority cycle.", folder);
            return new TallyLaunchResult(true, $"Opening {link.CompanyName} from verified folder; Tally data is not considered collected until the normal authority synchronization completes.");
        }
        catch (Exception ex)
        {
            return new TallyLaunchResult(false, "Exact-path Tally startup failed: " + ex.Message);
        }
        }
        finally { StartupGate.Release(); }
    }

    public bool OwnsAutomaticSession(ClientProfile client)
    {
        lock (_gate) return _owned is { Automatic: true } session && session.ClientId == client.Id &&
            session.Fy.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase) && IsRunning(session.Process);
    }

    public bool TryCloseAutomaticSession(ClientProfile client, bool authoritySuccessful, bool localQueueIdle, bool noOtherLoadedCompanies)
    {
        if (!authoritySuccessful || !localQueueIdle || !noOtherLoadedCompanies) return false;
        OwnedSession session;
        lock (_gate)
        {
            if (_owned is not { Automatic: true } own || own.ClientId != client.Id ||
                !own.Fy.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase) || !IsRunning(own.Process)) return false;
            session = own;
            // A restored/visible automatic window may now be in deliberate user use.
            // Never auto-close it simply because its original PID belonged to AAH.
            // Minimized idle sessions retain the existing guarded Auto Close behavior.
            if (OperatingSystem.IsWindows())
            {
                session.Process.Refresh();
                var userVisibleHwnd = session.Process.MainWindowHandle;
                if (userVisibleHwnd != IntPtr.Zero && !IsIconic(userVisibleHwnd)) return false;
            }
            // An external/user-owned Tally process must never be closed, nor any new PID.
            var all = new List<Process>();
            try
            {
                foreach (var name in new[] { "tally", "tallyprime", "tallyerp9" })
                    all.AddRange(Process.GetProcessesByName(name));
                if (all.Select(pid => pid.Id).Distinct().Count() != 1 ||
                    all[0].Id != session.Process.Id) return false;
            }
            finally { foreach (var p in all) p.Dispose(); }
            if (session.Process.StartTime.ToUniversalTime() != session.StartedUtc) return false;
            // Ownership is released BEFORE requesting graceful shutdown; no repeated close attempts.
            _owned = null;
        }
        try
        {
            if (!session.Process.CloseMainWindow()) return false; // never Kill or force termination
            AutomationDiagnosticLog.Write("TALLY-AUTO-CLOSE", $"Requested graceful shutdown for AAH-owned PID {session.Process.Id}, after authority sync and idle checks.", session.CompanyFolder);
            return true;
        }
        catch (Exception ex)
        {
            AutomationDiagnosticLog.Write("TALLY-AUTO-CLOSE", "Graceful close was not completed: " + ex.Message, session.CompanyFolder);
            return false;
        }
    }

    private static bool IsRunning(Process p)
    {
        try { return !p.HasExited; }
        catch { return false; }
    }
}
