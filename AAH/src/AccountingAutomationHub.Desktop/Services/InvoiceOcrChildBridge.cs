using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>Only AAH creates requests; Child never connects to the AAH database or Tally.</summary>
internal static class InvoiceOcrChildBridge
{
    public const string InstalledExe = @"C:\AccountingAutomationHub\InvoiceVisionChild\InvoiceVisionChild.exe";
    // Bound concurrent phone scans to ONE structural-engine worker; each verified
    // scan resumes its existing AAH fallback without waiting for all scans.
    private static readonly SemaphoreSlim StructuralQueue = new(1, 1);
    internal sealed class Status
    {
        public bool Stage1Succeeded { get; init; }
        public string Message { get; init; } = "";
        public string Stage1Archive { get; init; } = "";
        public string RequestId { get; init; } = "";
    }
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static bool MatchesSelectedHandoff(IReadOnlyList<string> paths, long clientId, string financialYear,
        string scanId, string stage1Archive, string evidenceFolder)
    {
        try {
            if (string.IsNullOrWhiteSpace(stage1Archive) || string.IsNullOrWhiteSpace(evidenceFolder) ||
                !File.Exists(stage1Archive) || paths.Count == 0 || paths.Any(p => !File.Exists(p))) return false;
            var baseDir = Path.GetFullPath(Path.Combine(evidenceFolder, "ChildHandoff"));
            var archiveFull = Path.GetFullPath(stage1Archive);
            if (!archiveFull.StartsWith(baseDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) return false;
            var responsePath = Path.Combine(baseDir, "response.json");
            if (!File.Exists(responsePath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(responsePath));
            var r = doc.RootElement;
            if (r.GetProperty("schema").GetString() != "AAH-INVOICE-OCR-RESULT-V1" ||
                r.GetProperty("stage1").GetString() != "PROVISIONAL_REVIEW_REQUIRED" ||
                r.GetProperty("clientId").GetInt64() != clientId ||
                r.GetProperty("financialYear").GetString() != financialYear ||
                r.GetProperty("scanId").GetString() != scanId ||
                !Path.GetFullPath(r.GetProperty("stage1Archive").GetString() ?? "")
                    .Equals(archiveFull, StringComparison.OrdinalIgnoreCase)) return false;
            var sources = r.GetProperty("sourceFiles");
            if (sources.GetArrayLength() != paths.Count) return false;
            for (var i = 0; i < paths.Count; i++) {
                var full = Path.GetFullPath(paths[i]);
                if (!full.Equals(sources[i].GetProperty("path").GetString(), StringComparison.OrdinalIgnoreCase) ||
                    !HashFile(full).Equals(sources[i].GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        } catch { return false; }
    }
    public static bool OpenReview(IReadOnlyList<string> paths, long clientId, string financialYear, string scanId, string stage1Archive, string evidenceFolder)
    {
        if (!File.Exists(InstalledExe)) throw new FileNotFoundException("Invoice OCR Child is not installed", InstalledExe);
        if (paths.Count == 0 || !File.Exists(paths[0])) throw new FileNotFoundException("Original phone image is missing.");
        var start = new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(InstalledExe)! };
        foreach (var path in paths.Where(File.Exists)) {
            start.ArgumentList.Add("--source"); start.ArgumentList.Add(path);
        }
        start.ArgumentList.Add("--client-id"); start.ArgumentList.Add(clientId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--fy"); start.ArgumentList.Add(financialYear);
        start.ArgumentList.Add("--scan-id"); start.ArgumentList.Add(scanId);
        // Only an archive already produced inside THIS scan's AAH evidence folder may be shown.
        // A manually run/old result is not accepted as an AAH verified handoff.
        var verifiedStage1 = MatchesSelectedHandoff(paths, clientId, financialYear, scanId, stage1Archive, evidenceFolder);
        if (verifiedStage1) {
            start.ArgumentList.Add("--stage1-archive"); start.ArgumentList.Add(Path.GetFullPath(stage1Archive));
        }
        if (Process.Start(start) is null) throw new IOException("Invoice OCR Child process failed to start.");
        return verifiedStage1;
    }
    private static string DescribeChildFailure(string responsePath, int exitCode)
    {
        try {
            if (File.Exists(responsePath)) {
                using var doc = JsonDocument.Parse(File.ReadAllText(responsePath));
                if (doc.RootElement.TryGetProperty("message", out var field)) {
                    var detail = field.GetString() ?? "";
                    if (detail.Length > 350) detail = detail[..350];
                    return $"Invoice OCR Child Stage 1 failed (exit {exitCode}): {detail}; AAH OCR fallback active.";
                }
            }
        } catch { }
        return $"Invoice OCR Child Stage 1 failed (exit {exitCode}, response absent or invalid); AAH OCR fallback active.";
    }
    public static async Task<Status> TryStage1Async(IReadOnlyList<string> paths, long clientId, string financialYear,
        string scanId, string evidenceFolder, CancellationToken cancellationToken)
    {
        try {
        await StructuralQueue.WaitAsync(cancellationToken);
        try {
        if (!File.Exists(InstalledExe)) return new Status { Message = "Child unavailable; using unchanged AAH OCR fallback." };
        if (paths.Count == 0 || paths.Count > 20 || paths.Any(path => !File.Exists(path)))
            return new Status { Message = "Original phone photo unavailable; Child bypassed." };
        var runDir = Path.Combine(evidenceFolder, "ChildHandoff");
        Directory.CreateDirectory(runDir);
        var requestPath = Path.Combine(runDir, "request.json");
        var responsePath = Path.Combine(runDir, "response.json");
        var requestId = Guid.NewGuid().ToString("D");
        var original = paths.Select(x => new { path = Path.GetFullPath(x), sha256 = HashFile(x) }).ToArray();
        var request = new {
            schema = "AAH-INVOICE-OCR-HANDOFF-V1", requestId, clientId, financialYear, scanId,
            sourceFiles = original, outputDirectory = Path.Combine(runDir, "Stage1", requestId), responsePath
        };
        var temp = requestPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(request), cancellationToken);
        File.Move(temp, requestPath, true);
        if (File.Exists(responsePath)) File.Delete(responsePath);
        var start = new ProcessStartInfo(InstalledExe) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(InstalledExe)! };
        start.ArgumentList.Add("--aah-handoff"); start.ArgumentList.Add(requestPath);
        try {
            using var child = new Process { StartInfo = start };
            if (!child.Start()) return new Status { Message = "Child did not start; using AAH fallback." };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) {
                try { if (!child.HasExited) child.Kill(true); } catch { }
                if (cancellationToken.IsCancellationRequested) throw;
                return new Status { Message = "Child timed out; using AAH OCR fallback." };
            }
            if (child.ExitCode != 0 || !File.Exists(responsePath))
                return new Status { Message = DescribeChildFailure(responsePath, child.ExitCode) };
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(responsePath, cancellationToken));
            var response = json.RootElement;
            if (response.GetProperty("schema").GetString() != "AAH-INVOICE-OCR-RESULT-V1" ||
                response.GetProperty("requestId").GetString() != requestId ||
                response.GetProperty("clientId").GetInt64() != clientId ||
                response.GetProperty("financialYear").GetString() != financialYear ||
                response.GetProperty("scanId").GetString() != scanId ||
                response.GetProperty("stage1").GetString() != "PROVISIONAL_REVIEW_REQUIRED" ||
                response.GetProperty("stage2").GetString() != "GATED" ||
                response.GetProperty("stage3").GetString() != "GATED" ||
                response.GetProperty("stage4").GetString() != "GATED" ||
                response.GetProperty("finalInvoiceVerified").GetBoolean() ||
                response.GetProperty("accountingAllowed").GetBoolean())
                return new Status { Message = "Child returned uncertified or inconsistent evidence; using AAH fallback." };
            var returned = response.GetProperty("sourceFiles");
            if (returned.GetArrayLength() != original.Length) throw new InvalidDataException("Child changed the number of source photographs.");
            for (int i=0; i<original.Length; i++) {
                if (returned[i].GetProperty("path").GetString() != original[i].path ||
                    !string.Equals(returned[i].GetProperty("sha256").GetString(), original[i].sha256, StringComparison.OrdinalIgnoreCase) ||
                    !HashFile(original[i].path).Equals(original[i].sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Child source hash mismatch; no OCR evidence accepted.");
            }
            var archive = response.GetProperty("stage1Archive").GetString() ?? "";
            if (!File.Exists(archive) || !Path.GetFullPath(archive).StartsWith(
                Path.GetFullPath(Path.Combine(runDir,"Stage1")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Child evidence archive path invalid.");
            return new Status { Stage1Succeeded = true, Stage1Archive = archive, RequestId = requestId,
                Message = "Child physical layout returned (Stage 1 provisional). Stages 2–4 gated; existing AAH OCR fallback handles accounting review." };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new Status { Message = "Child handoff did not verify: " + ex.Message + "; using AAH OCR fallback." }; }
        }
        finally { StructuralQueue.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new Status { Message = "Child request could not be prepared: " + ex.Message + "; using AAH OCR fallback." }; }
    }
}
