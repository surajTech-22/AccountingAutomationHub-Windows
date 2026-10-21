using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Review-only child process. Never submits accounting or Tally requests.
/// The child is versioned and installed separately by the three-component updater.
/// </summary>
public static class InvoiceVisionChildLauncher
{
    private static string ResolveExecutable()
    {
        var paths = new[]
        {
            Path.Combine(@"C:\AccountingAutomationHub", "InvoiceVisionChild", "InvoiceVisionChild.exe"),
            Path.Combine(AppContext.BaseDirectory, "InvoiceVisionChild", "InvoiceVisionChild.exe"),
        };
        return paths.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(
            "InvoiceVision Child is not installed. Run the combined AAH + Mobile + InvoiceVision updater.");
    }

    public static void Open(long clientId, string financialYear, IEnumerable<string>? paths = null, bool watchMobile = false)
    {
        var exe = ResolveExecutable();
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        if (clientId > 0) { info.ArgumentList.Add("--client-id"); info.ArgumentList.Add(clientId.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        if (!string.IsNullOrWhiteSpace(financialYear)) { info.ArgumentList.Add("--fy"); info.ArgumentList.Add(financialYear); }
        if (watchMobile) info.ArgumentList.Add("--watch-mobile");
        foreach (var path in paths ?? Array.Empty<string>())
        {
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            var extension = Path.GetExtension(path);
            if (File.Exists(path) && !new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp", ".pdf" }
                .Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;
            info.ArgumentList.Add("--source"); info.ArgumentList.Add(path);
        }
        if (Process.Start(info) is null) throw new InvalidOperationException("InvoiceVision Child could not start.");
    }
}
