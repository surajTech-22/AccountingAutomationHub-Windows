using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Launches InvoiceVision Child v2 for local photo/PDF invoice review.
/// Never submits accounting or Tally requests directly.
/// </summary>
public static class InvoiceVisionChildLauncher
{
    public const bool IsLinked = true;

    private static string ResolveExecutable()
    {
        return InvoiceOcrChildBridge.ResolveExecutable()
            ?? throw new FileNotFoundException("InvoiceVision Child v2 is not installed at " + InvoiceOcrChildBridge.InstalledExe);
    }

    public static void Open(long clientId, string financialYear, IEnumerable<string>? paths = null, bool watchMobile = false)
    {
        var exe = ResolveExecutable();
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        if (clientId > 0)
        {
            info.ArgumentList.Add("--client-id");
            info.ArgumentList.Add(clientId.ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(financialYear))
        {
            info.ArgumentList.Add("--fy");
            info.ArgumentList.Add(financialYear);
        }
        foreach (var path in paths ?? Array.Empty<string>())
        {
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            var extension = Path.GetExtension(path);
            if (File.Exists(path) && !new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp", ".pdf" }
                .Contains(extension, StringComparer.OrdinalIgnoreCase))
                continue;
            info.ArgumentList.Add("--source");
            info.ArgumentList.Add(Path.GetFullPath(path));
        }
        if (Process.Start(info) is null)
            throw new InvalidOperationException("InvoiceVision Child v2 could not start.");
    }
}
