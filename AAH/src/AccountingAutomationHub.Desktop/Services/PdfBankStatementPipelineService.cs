using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AccountingAutomationHub.Domain.Banking;

namespace AccountingAutomationHub.Desktop.Services;

public sealed class PdfBankStatementPipelineService
{
    private readonly PdfImportSettings _settings;

    public PdfBankStatementPipelineService(ApplicationPreferenceService? preferences = null)
    {
        _settings = PdfImportSettings.Load();
        if (preferences is null) return;

        var configuredPython = preferences.Get(ApplicationSettingKeys.PdfPythonExecutable);
        if (!string.IsNullOrWhiteSpace(configuredPython))
            _settings.PythonExecutable = configuredPython.Trim();
        _settings.TimeoutMinutes = Math.Clamp(
            preferences.GetInt(ApplicationSettingKeys.PdfTimeoutMinutes, _settings.TimeoutMinutes),
            1,
            240);
        _settings.DeleteGeneratedWorkbook = preferences.GetBool(ApplicationSettingKeys.PdfDeleteGeneratedWorkbook);
    }

    public async Task<PdfPipelineFileResult> ProcessAsync(
        string pdfPath,
        CancellationToken cancellationToken = default,
        bool automationMode = false,
        IReadOnlyList<string>? passwordCandidates = null)
    {
        // Final fail-closed boundary: even if a caller bypasses the Banking UI
        // and Auto Import router, image-only or mixed PDFs MUST NOT launch the
        // normal external digital-PDF Python pipeline or alter source-side XLSX.
        // The caller must use the existing BankOcrExcelConversionService instead.
        var sourceKind = BankPdfDocumentClassifier.Classify(pdfPath);
        if (sourceKind is BankPdfDocumentKind.Scanned or BankPdfDocumentKind.Mixed)
            return PdfPipelineFileResult.Failed(
                pdfPath, BankImportErrorCode.O002_OcrSourceUnreadable,
                $"This {sourceKind} PDF requires the existing Advanced Bank OCR pathway. Digital PDF extraction was not started. No workbook or source file was changed.");

        var sourceFile = Path.GetFileName(pdfPath);
        var script = ResolvePipelineScript();
        if (script is null)
            return PdfPipelineFileResult.Failed(pdfPath, BankImportErrorCode.P002_PipelineScriptMissing,
                "The external PDF runtime script was not found at C:\\BankPDFImporter\\Python\\pdf_pipeline_v4_merged.py. Accounting Automation Hub does not install, replace, back up, restore, or delete Python runtime files.");

        var python = ResolvePython();
        if (python is null)
            return PdfPipelineFileResult.Failed(pdfPath, BankImportErrorCode.P001_PythonExecutableMissing,
                "Python could not be found. Configure PdfImport:PythonExecutable or install Python for the current Windows user.");

        var expectedWorkbook = Path.ChangeExtension(pdfPath, ".xlsx");
        string? backupPath = null;
        if (File.Exists(expectedWorkbook))
        {
            var backupFolder = Path.Combine(Path.GetTempPath(), "AccountingAutomationHub", "PdfImport");
            Directory.CreateDirectory(backupFolder);
            backupPath = Path.Combine(backupFolder, $"AAH_PreExisting_{Guid.NewGuid():N}.xlsx");
            File.Copy(expectedWorkbook, backupPath, true);
            File.Delete(expectedWorkbook);
        }

        var logFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Accounting Automation Hub", "logs", "pdf-import");
        Directory.CreateDirectory(logFolder);
        var logPath = Path.Combine(logFolder, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Sanitize(Path.GetFileNameWithoutExtension(pdfPath))}.log");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = python.Executable,
                WorkingDirectory = Path.GetDirectoryName(script) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var prefix in python.PrefixArguments) startInfo.ArgumentList.Add(prefix);
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add(pdfPath);
            startInfo.Environment["PYTHONUTF8"] = "1";
            startInfo.Environment["PYTHONUNBUFFERED"] = "1";

            // Completion 23 password bridge. Android/Windows controllers can supply
            // password candidates learned from the original file name and from the
            // source app's shared subject/text. The live external Python runtime is
            // never edited by AAH; candidates are exposed only to this child process.
            // Current/future compatible Python runtimes can consume either the JSON
            // list or the primary candidate without any plaintext sidecar file.
            var suppliedCandidates = (passwordCandidates ?? Array.Empty<string>())
                .Select(candidate => (candidate ?? string.Empty).Trim())
                .Where(candidate => candidate.Length is > 0 and <= 64)
                .Distinct(StringComparer.Ordinal)
                .Take(12)
                .ToArray();
            if (suppliedCandidates.Length > 0)
            {
                startInfo.Environment["AAH_PASSWORD_CANDIDATES_JSON"] = JsonSerializer.Serialize(suppliedCandidates);
                startInfo.Environment["AAH_PDF_PASSWORD"] = suppliedCandidates[0];
            }

            // Do not redirect the external pipeline's Registry/Cache paths. The Hub must
            // invoke the user's live Python runtime with the same path behavior it has
            // when the user runs it directly from Command Prompt.
            if (automationMode)
            {
                startInfo.Environment["AAH_AUTOMATION_MODE"] = "1";
                startInfo.Environment["AAH_NONINTERACTIVE"] = "1";
            }

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) stdout.AppendLine(args.Data); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) stderr.AppendLine(args.Data); };

            if (!process.Start())
                return PdfPipelineFileResult.Failed(pdfPath, BankImportErrorCode.P001_PythonExecutableMissing, "Python could not be started.", logPath, backupPath);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, _settings.TimeoutMinutes)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await File.WriteAllTextAsync(logPath, BuildLog(pdfPath, script, python, -1, stdout, stderr, "TIMEOUT"), CancellationToken.None);
                return PdfPipelineFileResult.Failed(pdfPath, BankImportErrorCode.P005_PdfProcessingTimeout,
                    $"The PDF pipeline did not finish within {_settings.TimeoutMinutes} minute(s).", logPath, backupPath);
            }

            await File.WriteAllTextAsync(logPath, BuildLog(pdfPath, script, python, process.ExitCode, stdout, stderr, string.Empty), CancellationToken.None);
            var combined = stdout.ToString() + Environment.NewLine + stderr.ToString();
            if (automationMode && TryGetAutomationBlock(combined, out var blockedCode, out var blockedMessage))
            {
                // A background import must never accept a workbook produced after an
                // interactive decision was skipped. Action Center owns that resolution.
                try { if (File.Exists(expectedWorkbook)) File.Delete(expectedWorkbook); } catch { }
                return PdfPipelineFileResult.Failed(pdfPath, blockedCode, blockedMessage, logPath, backupPath);
            }
            if (!File.Exists(expectedWorkbook))
            {
                var code = ClassifyPipelineFailure(combined, process.ExitCode);
                var friendly = BuildPipelineFailureMessage(combined, process.ExitCode);
                return PdfPipelineFileResult.Failed(pdfPath, code, friendly, logPath, backupPath);
            }

            return new PdfPipelineFileResult
            {
                PdfPath = pdfPath,
                GeneratedWorkbookPath = expectedWorkbook,
                LogPath = logPath,
                WasSuccessful = true,
                ExistingWorkbookBackupPath = backupPath ?? string.Empty
            };
        }
        catch (Exception exception)
        {
            try { await File.WriteAllTextAsync(logPath, exception.ToString(), CancellationToken.None); } catch { }
            return PdfPipelineFileResult.Failed(pdfPath, BankImportErrorCode.P003_PythonDependencyFailure, exception.Message, logPath, backupPath);
        }
        finally
        {
            // On a failed extraction there is no generated workbook to import, so restore
            // a pre-existing same-name workbook immediately. On success CleanupAsync
            // restores it only after the generated workbook has been imported.
            if (!File.Exists(expectedWorkbook) && !string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
            {
                try
                {
                    File.Copy(backupPath, expectedWorkbook, true);
                    File.Delete(backupPath);
                }
                catch { /* the failure result already points to the saved log */ }
            }
        }
    }

    public Task<BankStatementImportIssue?> CleanupAsync(PdfPipelineFileResult item)
    {
        try
        {
            if (_settings.DeleteGeneratedWorkbook && !string.IsNullOrWhiteSpace(item.GeneratedWorkbookPath) && File.Exists(item.GeneratedWorkbookPath))
                File.Delete(item.GeneratedWorkbookPath);
            if (!string.IsNullOrWhiteSpace(item.ExistingWorkbookBackupPath) && File.Exists(item.ExistingWorkbookBackupPath))
            {
                File.Copy(item.ExistingWorkbookBackupPath, Path.ChangeExtension(item.PdfPath, ".xlsx"), true);
                File.Delete(item.ExistingWorkbookBackupPath);
            }
            return Task.FromResult<BankStatementImportIssue?>(null);
        }
        catch (Exception exception)
        {
            return Task.FromResult<BankStatementImportIssue?>(BankStatementImportIssue.Create(
                Path.GetFileName(item.PdfPath), "Temporary Cleanup", BankImportErrorCode.P009_TemporaryCleanupFailure, exception.Message, retryable: false));
        }
    }

    private const string OriginalPdfRoot = @"C:\BankPDFImporter";
    public static string ProductionPipelinePath => Path.Combine(OriginalPdfRoot, "Python", "pdf_pipeline_v4_merged.py");

    private string? ResolvePipelineScript()
    {
        // CALL-ONLY CONTRACT:
        // Accounting Automation Hub may execute the user's external Python runtime,
        // but it must never create, copy, replace, back up, restore, delete, or otherwise
        // manage Python runtime files/folders. The live external file is authoritative.
        var originalScript = Path.Combine(
            OriginalPdfRoot,
            "Python",
            "pdf_pipeline_v4_merged.py");

        return File.Exists(originalScript) ? originalScript : null;
    }

    private PythonCommand? ResolvePython()
    {
        if (!string.IsNullOrWhiteSpace(_settings.PythonExecutable) && CanStart(_settings.PythonExecutable, ["--version"]))
            return new PythonCommand(_settings.PythonExecutable, []);
        // Match the most common direct Command Prompt invocation first. A user can
        // still pin an exact interpreter in Settings -> PDF Python Executable.
        if (CanStart("python", ["--version"])) return new PythonCommand("python", []);
        if (CanStart("py", ["-3", "--version"])) return new PythonCommand("py", ["-3"]);
        if (CanStart("python3", ["--version"])) return new PythonCommand("python3", []);
        return null;
    }

    private static bool CanStart(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo { FileName = executable, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null) return false;
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch { return false; }
    }


    private static string BuildPipelineFailureMessage(string output, int exitCode)
    {
        if (output.Contains("AAH_PASSWORD_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return "This PDF needs a password that the local password-learning engine could not resolve. Use Action Center → Retry Import; the Hub will call the same external Python pipeline interactively once so you can resolve it.";
        if (output.Contains("AAH_MAPPING_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("AAH_UNKNOWN_BANK_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return "This PDF format or bank layout is not learned yet. Use Action Center → Retry Import; the Hub will call the same external Python pipeline interactively once so the mapping can be saved and reused.";
        if (output.Contains("AAH_LINE_CONFIRMATION_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return "This non-tabular PDF needs one-time line confirmation. Use Action Center → Retry Import; the Hub will call the same external Python pipeline interactively once. Background Auto Import itself remains non-interactive.";
        return exitCode == 0
            ? "The Python pipeline finished without creating the expected same-name Excel workbook."
            : $"The Python pipeline exited with code {exitCode}. See the saved log for details.";
    }

    private static bool TryGetAutomationBlock(
        string output,
        out BankImportErrorCode errorCode,
        out string message)
    {
        if (output.Contains("AAH_PASSWORD_REQUIRED", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = BankImportErrorCode.P004_PdfPasswordFailure;
            message = "This PDF needs a password that Auto Import could not resolve locally. Background processing stayed non-interactive; Action Center → Retry Import now reruns the same external Python pipeline interactively so the password can be learned.";
            return true;
        }
        if (output.Contains("AAH_LINE_CONFIRMATION_REQUIRED", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = BankImportErrorCode.P008_MappingWizardFailure;
            message = "This PDF needs one-time line/table confirmation. Auto Import did not open a background wizard; Action Center → Retry Import now reruns the same external Python pipeline interactively.";
            return true;
        }
        if (output.Contains("AAH_MAPPING_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("AAH_UNKNOWN_BANK_REQUIRED", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = BankImportErrorCode.P008_MappingWizardFailure;
            message = "This PDF format or bank needs one-time mapping. Auto Import stayed non-interactive; Action Center → Retry Import now reruns the same external Python pipeline interactively so the learned mapping can be saved.";
            return true;
        }

        errorCode = BankImportErrorCode.P008_MappingWizardFailure;
        message = string.Empty;
        return false;
    }

    private static BankImportErrorCode ClassifyPipelineFailure(string output, int exitCode)
    {
        if (output.Contains("AAH_PASSWORD_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return BankImportErrorCode.P004_PdfPasswordFailure;
        if (output.Contains("AAH_MAPPING_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("AAH_UNKNOWN_BANK_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("AAH_LINE_CONFIRMATION_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return BankImportErrorCode.P008_MappingWizardFailure;
        if (output.Contains("No module named", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("ModuleNotFoundError", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("ImportError", StringComparison.OrdinalIgnoreCase))
            return BankImportErrorCode.P003_PythonDependencyFailure;
        if (output.Contains("password", StringComparison.OrdinalIgnoreCase) || output.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
            return BankImportErrorCode.P004_PdfPasswordFailure;
        if (output.Contains("wizard", StringComparison.OrdinalIgnoreCase) || output.Contains("mapping", StringComparison.OrdinalIgnoreCase))
            return BankImportErrorCode.P008_MappingWizardFailure;
        return exitCode == 0 ? BankImportErrorCode.P006_GeneratedWorkbookMissing : BankImportErrorCode.P003_PythonDependencyFailure;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { }
    }

    private static string BuildLog(string pdfPath, string script, PythonCommand python, int exitCode, StringBuilder stdout, StringBuilder stderr, string note) =>
        $"PDF: {pdfPath}{Environment.NewLine}Script: {script}{Environment.NewLine}Python: {python.Executable} {string.Join(' ', python.PrefixArguments)}{Environment.NewLine}Exit code: {exitCode}{Environment.NewLine}{note}{Environment.NewLine}{Environment.NewLine}STDOUT{Environment.NewLine}{stdout}{Environment.NewLine}STDERR{Environment.NewLine}{stderr}";

    private static string Sanitize(string name) => string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private sealed record PythonCommand(string Executable, IReadOnlyList<string> PrefixArguments);
}

public sealed class PdfPipelineFileResult
{
    public string PdfPath { get; set; } = string.Empty;
    public string GeneratedWorkbookPath { get; set; } = string.Empty;
    public string LogPath { get; set; } = string.Empty;
    public string ExistingWorkbookBackupPath { get; set; } = string.Empty;
    public bool WasSuccessful { get; set; }
    public BankStatementImportIssue? Issue { get; set; }

    public static PdfPipelineFileResult Failed(string pdfPath, BankImportErrorCode code, string message, string logPath = "", string? backupPath = null) => new()
    {
        PdfPath = pdfPath,
        LogPath = logPath,
        ExistingWorkbookBackupPath = backupPath ?? string.Empty,
        Issue = BankStatementImportIssue.Create(Path.GetFileName(pdfPath), "PDF Pipeline", code, message),
        WasSuccessful = false
    };
}

internal sealed class PdfImportSettings
{
    public string PythonExecutable { get; set; } = string.Empty;
    public int TimeoutMinutes { get; set; } = 30;
    public bool DeleteGeneratedWorkbook { get; set; } = true;

    public static PdfImportSettings Load()
    {
        var settings = new PdfImportSettings();
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("PdfImport", out var section))
                {
                    settings.PythonExecutable = section.TryGetProperty("PythonExecutable", out var python) ? python.GetString() ?? string.Empty : string.Empty;
                    settings.TimeoutMinutes = section.TryGetProperty("TimeoutMinutes", out var timeout) && timeout.TryGetInt32(out var minutes) ? minutes : 30;
                    settings.DeleteGeneratedWorkbook = !section.TryGetProperty("DeleteGeneratedWorkbook", out var delete) || delete.GetBoolean();
                }
            }
        }
        catch
        {
            settings = new PdfImportSettings();
        }

        try
        {
            var repository = new AccountingAutomationHub.Infrastructure.Settings.SqliteApplicationPreferenceRepository();
            var preferences = new ApplicationPreferenceService(repository);
            var python = preferences.Get(ApplicationSettingKeys.PdfPythonExecutable);
            if (!string.IsNullOrWhiteSpace(python)) settings.PythonExecutable = python.Trim();
            settings.TimeoutMinutes = Math.Clamp(preferences.GetInt(ApplicationSettingKeys.PdfTimeoutMinutes, settings.TimeoutMinutes), 1, 240);
            settings.DeleteGeneratedWorkbook = preferences.GetBool(ApplicationSettingKeys.PdfDeleteGeneratedWorkbook);
        }
        catch
        {
            // The packaged appsettings defaults remain valid if the preferences DB is unavailable.
        }
        return settings;
    }
}
