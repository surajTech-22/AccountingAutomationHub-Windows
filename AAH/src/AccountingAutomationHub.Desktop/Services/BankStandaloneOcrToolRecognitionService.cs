using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// R46 R35 bridge to the user's already-installed standalone Bank OCR V10 tool.
///
/// This is intentionally NOT a second Paddle runtime resolver. The standalone tool owns its
/// Python/llama/GGUF layout relative to OCR_WORKER.py. AAH finds the existing tool package,
/// starts its own OCR_WORKER.py exactly as the tool does, waits only until raw page JSON exists,
/// then stops the owned process tree before Bank-statement document verification can become
/// invoice accounting authority. Mobile Invoice consumes only those raw Paddle document pages.
/// </summary>
internal sealed class BankStandaloneOcrToolRecognitionService
{
    private static readonly SemaphoreSlim GlobalStandaloneToolGate = new(1, 1);
    private static readonly Regex PageFileRegex = new(@"page_(?<page>\d+)\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<BankOcrRecognitionBatch> RecognizeRawPagesAsync(
        IReadOnlyList<string> sourcePaths,
        string runRoot,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sources = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var statusPath = Path.Combine(runRoot, "standalone-bank-ocr-status.json");
        var logPath = Path.Combine(runRoot, "standalone-bank-ocr.log");
        var manifestPath = Path.Combine(runRoot, "standalone-bank-ocr-sources.json");

        if (sources.Count == 0)
            return BankOcrRecognitionBatch.Failed("No captured invoice image is available for the existing Bank OCR tool.", logPath, statusPath, manifestPath);

        foreach (var source in sources)
        {
            if (!File.Exists(source))
                return BankOcrRecognitionBatch.Failed($"The invoice image no longer exists: {source}", logPath, statusPath, manifestPath);
            if (!BankOcrExcelConversionService.IsSupportedOcrSource(source))
                return BankOcrRecognitionBatch.Failed($"The existing Bank OCR tool does not support '{Path.GetExtension(source)}'.", logPath, statusPath, manifestPath);
        }

        if (!OperatingSystem.IsWindows())
            return BankOcrRecognitionBatch.Failed("The existing Bank OCR tool is a Windows-only local OCR tool.", logPath, statusPath, manifestPath);

        Directory.CreateDirectory(runRoot);
        var settings = AdvancedBankOcrSettings.Load();
        var tool = BankStandaloneOcrToolLocator.Resolve(settings.ExternalRootFolder);
        if (!tool.IsReady)
        {
            return BankOcrRecognitionBatch.Failed(
                tool.FailureMessage.Length > 0
                    ? tool.FailureMessage
                    : "The already-installed Bank OCR V10 tool could not be found.",
                logPath,
                statusPath,
                manifestPath);
        }

        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(sources),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);

        if (GlobalStandaloneToolGate.CurrentCount == 0)
            progress?.Invoke("Mobile Invoice OCR • existing Bank OCR tool is busy; waiting for the current OCR job to finish");

        await GlobalStandaloneToolGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunToolUntilRawPagesReadyAsync(
                sources,
                tool,
                manifestPath,
                statusPath,
                logPath,
                runRoot,
                progress,
                settings.TimeoutMinutes,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            GlobalStandaloneToolGate.Release();
        }
    }

    private static async Task<BankOcrRecognitionBatch> RunToolUntilRawPagesReadyAsync(
        IReadOnlyList<string> sources,
        BankStandaloneOcrToolInfo tool,
        string manifestPath,
        string statusPath,
        string logPath,
        string runRoot,
        Action<string>? progress,
        int timeoutMinutes,
        CancellationToken cancellationToken)
    {
        progress?.Invoke($"Mobile Invoice OCR • using existing Bank OCR V10 tool at {tool.RootFolder}");
        progress?.Invoke("Mobile Invoice OCR • starting the Bank OCR worker directly; invoice will consume raw Paddle document/table pages only");

        var info = new ProcessStartInfo
        {
            FileName = tool.PythonExecutable,
            WorkingDirectory = tool.RootFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };
        info.ArgumentList.Add(tool.WorkerScript);
        info.ArgumentList.Add("--batch-manifest");
        info.ArgumentList.Add(manifestPath);
        info.ArgumentList.Add("--status");
        info.ArgumentList.Add(statusPath);
        info.ArgumentList.Add("--log");
        info.ArgumentList.Add(logPath);
        info.ArgumentList.Add("--device");
        info.ArgumentList.Add("cpu");

        using var processJob = BankOcrJobObject.TryCreateKillOnClose();
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                return BankOcrRecognitionBatch.Failed("The existing Bank OCR worker could not be started.", logPath, statusPath, manifestPath);
        }
        catch (Exception exception)
        {
            return BankOcrRecognitionBatch.Failed("The existing Bank OCR worker could not be started: " + exception.Message, logPath, statusPath, manifestPath);
        }

        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        _ = processJob?.TryAssign(process);

        var timeoutAt = DateTime.UtcNow.AddMinutes(Math.Clamp(timeoutMinutes, 5, 720));
        string batchOutput = string.Empty;
        string lastStage = string.Empty;
        string lastMessage = string.Empty;

        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = ReadStatus(statusPath);
            if (state is not null)
            {
                if (state.Stage.Length > 0 && !state.Stage.Equals(lastStage, StringComparison.OrdinalIgnoreCase))
                {
                    lastStage = state.Stage;
                    lastMessage = state.Message;
                    if (state.Message.Length > 0)
                        progress?.Invoke("Bank OCR tool • " + state.Message);
                }
                if (state.OutputDirectory.Length > 0)
                    batchOutput = state.OutputDirectory;

                // The standalone V10 worker has finished raw OCR at this point. Anything after
                // OCR_BATCH_COMPLETE is Bank-statement grouping/verification and must not become
                // Mobile Invoice accounting authority. Stop the AAH-owned process tree here.
                if (IsRawOcrCompleteStage(state.Stage))
                    break;

                if (state.Stage.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                    break;
            }

            if (process.HasExited)
                break;

            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        }

        // AAH owns only the process it launched. Kill the tree before the standalone tool can
        // continue into Bank-statement financial verification. BankOcrJobObject is an additional
        // kill-on-close barrier for any child llama.cpp process inherited from this worker.
        TryKillOwnedTree(process);
        try
        {
            using var exitWait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(exitWait.Token).ConfigureAwait(false);
        }
        catch { }

        var finalState = ReadStatus(statusPath);
        if (finalState is not null)
        {
            if (finalState.OutputDirectory.Length > 0) batchOutput = finalState.OutputDirectory;
            if (finalState.Stage.Length > 0) lastStage = finalState.Stage;
            if (finalState.Message.Length > 0) lastMessage = finalState.Message;
        }

        var pages = CollectRawPages(batchOutput, sources);
        if (pages.Count > 0)
        {
            progress?.Invoke($"Mobile Invoice OCR • existing Bank OCR tool produced {pages.Count:N0} raw Paddle page(s); Bank statement verification was bypassed");
            return new BankOcrRecognitionBatch
            {
                WasSuccessful = true,
                Pages = pages,
                SourceErrors = CollectSourceErrors(batchOutput),
                LogPath = logPath,
                StatusPath = statusPath,
                ResultPath = batchOutput
            };
        }

        var detail = lastMessage.Length > 0
            ? lastMessage
            : ReadLastUsefulLogLine(logPath);
        if (DateTime.UtcNow >= timeoutAt)
            detail = "Timed out waiting for raw pages from the existing Bank OCR tool.";
        if (detail.Length == 0)
            detail = $"The existing Bank OCR tool stopped before producing raw Paddle page JSON (stage: {lastStage}).";

        return BankOcrRecognitionBatch.Failed(detail, logPath, statusPath, batchOutput.Length > 0 ? batchOutput : manifestPath);
    }

    private static bool IsRawOcrCompleteStage(string stage) =>
        stage.Equals("OCR_BATCH_COMPLETE", StringComparison.OrdinalIgnoreCase) ||
        stage.Equals("DOCUMENT_GROUPING", StringComparison.OrdinalIgnoreCase) ||
        stage.Equals("DOCUMENT_ORDERING", StringComparison.OrdinalIgnoreCase) ||
        stage.Equals("DOCUMENT_VERIFY", StringComparison.OrdinalIgnoreCase) ||
        stage.Equals("DOCUMENT_BUILD", StringComparison.OrdinalIgnoreCase) ||
        stage.Equals("BATCH_SUCCESS", StringComparison.OrdinalIgnoreCase);

    private static List<BankOcrRecognitionPage> CollectRawPages(string batchOutput, IReadOnlyList<string> sources)
    {
        var pages = new List<BankOcrRecognitionPage>();
        if (string.IsNullOrWhiteSpace(batchOutput) || !Directory.Exists(batchOutput)) return pages;
        var rawRoot = Path.Combine(batchOutput, "SOURCE_PAGES");
        if (!Directory.Exists(rawRoot)) return pages;

        IEnumerable<string> jsonFiles;
        try { jsonFiles = Directory.EnumerateFiles(rawRoot, "page_*.json", SearchOption.AllDirectories).ToArray(); }
        catch { return pages; }

        foreach (var jsonPath in jsonFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(rawRoot, jsonPath);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var sourceIndex = parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSource)
                ? parsedSource
                : 1;
            var match = PageFileRegex.Match(Path.GetFileName(jsonPath));
            var sourcePage = match.Success && int.TryParse(match.Groups["page"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPage)
                ? parsedPage
                : 1;
            var sourcePath = sourceIndex >= 1 && sourceIndex <= sources.Count ? sources[sourceIndex - 1] : sources[0];
            pages.Add(new BankOcrRecognitionPage
            {
                Id = $"S{sourceIndex:000}P{sourcePage:000}",
                SourcePath = sourcePath,
                SourceIndex = sourceIndex,
                SourcePage = sourcePage,
                JsonPath = jsonPath
            });
        }

        return pages;
    }

    private static IReadOnlyList<string> CollectSourceErrors(string batchOutput)
    {
        if (string.IsNullOrWhiteSpace(batchOutput) || !Directory.Exists(batchOutput)) return [];
        var rawRoot = Path.Combine(batchOutput, "SOURCE_PAGES");
        if (!Directory.Exists(rawRoot)) return [];
        try
        {
            return Directory.EnumerateFiles(rawRoot, "ERROR.txt", SearchOption.AllDirectories)
                .Select(path =>
                {
                    try { return File.ReadAllText(path, Encoding.UTF8).Trim(); }
                    catch { return string.Empty; }
                })
                .Where(value => value.Length > 0)
                .Take(20)
                .ToArray();
        }
        catch { return []; }
    }

    private static StandaloneStatus? ReadStatus(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            return new StandaloneStatus
            {
                Stage = Text(root, "stage"),
                Message = Text(root, "message"),
                OutputDirectory = Text(root, "output_dir")
            };
        }
        catch { return null; }
    }

    private static string Text(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static string ReadLastUsefulLogLine(string path)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            return File.ReadLines(path, Encoding.UTF8)
                .Select(line => line.Trim())
                .LastOrDefault(line => line.Length > 0) ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private static void TryKillOwnedTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private sealed class StandaloneStatus
    {
        public string Stage { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
    }
}

/// <summary>
/// Finds the already-installed Bank OCR V10 TOOL, not its individual Python/model components.
/// The tool package itself is the authority for its relative runtime/model paths.
/// </summary>
internal static class BankStandaloneOcrToolLocator
{
    private static readonly object Sync = new();
    private static string _cachedToolRoot = string.Empty;

    public static BankStandaloneOcrToolInfo Resolve(string configuredRoot = "")
    {
        var candidates = new List<string>();
        AddCandidate(candidates, configuredRoot);
        AddCandidate(candidates, Environment.GetEnvironmentVariable("AAH_BANK_OCR_HOME"));

        foreach (var candidate in candidates)
        {
            var direct = InspectCandidateAndNearby(candidate);
            if (direct.IsReady) return Cache(direct);
        }

        lock (Sync)
        {
            if (_cachedToolRoot.Length > 0)
            {
                var cached = Build(_cachedToolRoot);
                if (cached.IsReady) return cached;
                _cachedToolRoot = string.Empty;
            }

            foreach (var root in CommonUserSearchRoots())
            {
                var found = FindToolUnder(root, maxDepth: 7, maxDirectories: 5000);
                if (found.IsReady) return Cache(found);
            }
        }

        var preferred = candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        var preferredInfo = preferred.Length > 0 ? InspectCandidateAndNearby(preferred) : new BankStandaloneOcrToolInfo();
        return new BankStandaloneOcrToolInfo
        {
            RootFolder = preferredInfo.RootFolder,
            FailureMessage = preferredInfo.FailureMessage.Length > 0
                ? preferredInfo.FailureMessage
                : "The existing Bank OCR V10 tool was not found. AAH looked for the installed OCR_WORKER.py / RUN_OCR_V10_FAST.bat tool package, not for a second Python/model runtime. Open Settings > PDF / Banking / Sales and point Advanced Bank Scan OCR to the already-working Bank OCR tool folder once if it has been moved."
        };
    }

    private static BankStandaloneOcrToolInfo InspectCandidateAndNearby(string candidate)
    {
        var direct = Build(candidate);
        if (direct.IsReady) return direct;
        if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) return direct;

        try
        {
            foreach (var child in Directory.EnumerateDirectories(candidate).Take(100))
            {
                var childInfo = Build(child);
                if (childInfo.IsReady) return childInfo;
            }
        }
        catch { }
        return direct;
    }

    private static IEnumerable<string> CommonUserSearchRoots()
    {
        var values = new List<string>();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddCandidate(values, Path.Combine(profile, "Downloads"));
        AddCandidate(values, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddCandidate(values, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddCandidate(values, Environment.GetEnvironmentVariable("OneDrive"));
        AddCandidate(values, Environment.GetEnvironmentVariable("OneDriveConsumer"));
        AddCandidate(values, Environment.GetEnvironmentVariable("OneDriveCommercial"));
        return values.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static BankStandaloneOcrToolInfo FindToolUnder(string root, int maxDepth, int maxDirectories)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return new BankStandaloneOcrToolInfo();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;

        while (queue.Count > 0 && visited < maxDirectories)
        {
            var (path, depth) = queue.Dequeue();
            visited++;
            var info = Build(path);
            if (info.IsReady) return info;
            if (depth >= maxDepth) continue;

            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(path).Take(350).ToArray(); }
            catch { continue; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (ShouldSkipDirectory(name)) continue;
                queue.Enqueue((child, depth + 1));
            }
        }
        return new BankStandaloneOcrToolInfo();
    }

    private static bool ShouldSkipDirectory(string name) =>
        name.Equals("_runtime", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("_llama_runtime", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("_llama_models", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("_model_profile", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("_outputs", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("_runs", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AppData", StringComparison.OrdinalIgnoreCase);

    private static BankStandaloneOcrToolInfo Build(string? root)
    {
        root = (root ?? string.Empty).Trim().Trim('"');
        if (root.Length == 0) return new BankStandaloneOcrToolInfo();
        try { root = Path.GetFullPath(root); } catch { }

        var worker = Path.Combine(root, "OCR_WORKER.py");
        var launcher = Path.Combine(root, "RUN_OCR_V10_FAST.bat");
        var documentVerifier = Path.Combine(root, "DOCUMENT_VERIFY_V10.py");
        var python = Path.Combine(root, "_runtime", "Python313", "python.exe");
        var missing = new List<string>();
        if (!File.Exists(worker)) missing.Add("OCR_WORKER.py");
        if (!File.Exists(launcher)) missing.Add("RUN_OCR_V10_FAST.bat");
        if (!File.Exists(documentVerifier)) missing.Add("DOCUMENT_VERIFY_V10.py");
        if (!File.Exists(python)) missing.Add("_runtime\\Python313\\python.exe");

        return new BankStandaloneOcrToolInfo
        {
            RootFolder = root,
            WorkerScript = worker,
            LauncherScript = launcher,
            PythonExecutable = python,
            IsReady = missing.Count == 0,
            FailureMessage = missing.Count == 0
                ? string.Empty
                : $"The selected folder is not the already-installed Bank OCR V10 tool. Missing tool component(s): {string.Join(", ", missing)}."
        };
    }

    private static BankStandaloneOcrToolInfo Cache(BankStandaloneOcrToolInfo info)
    {
        lock (Sync) _cachedToolRoot = info.RootFolder;
        return info;
    }

    private static void AddCandidate(ICollection<string> values, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        var value = candidate.Trim().Trim('"');
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
    }
}

internal sealed class BankStandaloneOcrToolInfo
{
    public bool IsReady { get; set; }
    public string RootFolder { get; set; } = string.Empty;
    public string WorkerScript { get; set; } = string.Empty;
    public string LauncherScript { get; set; } = string.Empty;
    public string PythonExecutable { get; set; } = string.Empty;
    public string FailureMessage { get; set; } = string.Empty;
}
