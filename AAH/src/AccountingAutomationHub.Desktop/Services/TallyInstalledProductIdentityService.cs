using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Classify the installed Tally application from Windows executable VERSIONINFO.
/// Company data file extensions, directory names and XML VERSION are not product identity.
/// Caller has already verified company-name/number/Client-FY from a fresh XML response.
/// </summary>
internal sealed record TallyExecutableProduct(string Family, string ProductVersion,
    string FileVersion, string ExecutablePath, string Evidence);

internal static class TallyInstalledProductIdentityService
{
    public static TallyExecutableProduct? ReadExecutable(string executable)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return null;
            var path = Path.GetFullPath(executable);
            var info = FileVersionInfo.GetVersionInfo(path);
            var product = (info.ProductName ?? string.Empty).Trim();
            var description = (info.FileDescription ?? string.Empty).Trim();
            var labels = product + " " + description;
            var family = labels.Contains("TallyPrime", StringComparison.OrdinalIgnoreCase)
                ? "TallyPrime"
                : labels.Contains("Tally.ERP 9", StringComparison.OrdinalIgnoreCase) ||
                  labels.Contains("Tally.ERP9", StringComparison.OrdinalIgnoreCase)
                    ? "Tally.ERP 9" : string.Empty;
            if (family.Length == 0 || !Path.GetFileName(path).Equals("Tally.exe", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(path).Equals("TallyPrime.exe", StringComparison.OrdinalIgnoreCase))
                return null;
            return new TallyExecutableProduct(family, (info.ProductVersion ?? string.Empty).Trim(),
                (info.FileVersion ?? string.Empty).Trim(), path, "Windows executable VERSIONINFO");
        }
        catch { return null; }
    }

    // When several Tally installations are running, the HTTP listener PID must be
    // established first. Never guess which version belongs to a linked company.
    public static TallyExecutableProduct? ReadOnlyRunningTally()
    {
        var processes = new List<Process>();
        try
        {
            foreach (var name in new[] { "tally", "tallyprime", "tallyerp9" })
                processes.AddRange(Process.GetProcessesByName(name));
            var unique = processes.GroupBy(p => p.Id).Select(g => g.First()).ToArray();
            if (unique.Length != 1) return null;
            try { return ReadExecutable(unique[0].MainModule?.FileName ?? string.Empty); }
            catch { return null; }
        }
        catch { return null; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
