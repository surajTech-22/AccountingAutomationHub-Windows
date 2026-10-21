using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace AccountingAutomationHub.Infrastructure.Spreadsheets;

/// <summary>
/// Serializes AAH-owned hidden Excel automation and guarantees COM/process cleanup.
/// The user's already-running Excel processes are never terminated.
/// </summary>
public static class ExcelComWorkbookMaterializer
{
    private static readonly object ExcelGate = new();
    private static readonly Regex SafePart = new("[^A-Za-z0-9_-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ConvertToOpenXml(
        string sourcePath,
        string password,
        int corruptLoad = 0,
        string purpose = "Workbook")
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("Microsoft Excel on Windows is required to inspect or recover this workbook.");
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("The workbook source could not be found.", sourcePath);

        lock (ExcelGate)
        {
            return ConvertToOpenXmlCore(sourcePath, password ?? string.Empty, corruptLoad, purpose);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string ConvertToOpenXmlCore(string sourcePath, string password, int corruptLoad, string purpose)
    {
        var excelType = Type.GetTypeFromProgID("Excel.Application")
            ?? throw new InvalidOperationException("Microsoft Excel is required to inspect or recover this workbook.");

        var existingExcelPids = SnapshotExcelProcessIds();
        object? excelObject = null;
        object? workbooksObject = null;
        object? workbookObject = null;
        int? ownedExcelPid = null;
        var targetPath = CreateOwnedTempWorkbookPath(purpose);

        try
        {
            excelObject = Activator.CreateInstance(excelType)
                ?? throw new InvalidOperationException("Microsoft Excel could not be started.");
            dynamic excel = excelObject;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            try { excel.EnableEvents = false; } catch { }
            try { excel.ScreenUpdating = false; } catch { }
            try { excel.AskToUpdateLinks = false; } catch { }
            // msoAutomationSecurityForceDisable = 3. Never run workbook macros while AAH inspects data.
            try { excel.AutomationSecurity = 3; } catch { }

            ownedExcelPid = TryGetExcelProcessId(excelObject);

            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            var encryptedOpenXmlContainer = SpreadsheetSourceInspector.IsEncryptedOpenXmlContainer(sourcePath);
            object openFormat = encryptedOpenXmlContainer ? Type.Missing : 5;

            workbookObject = workbooks.Open(
                sourcePath,
                0,
                true,
                openFormat,
                password,
                Type.Missing,
                true,
                Type.Missing,
                Type.Missing,
                false,
                false,
                Type.Missing,
                false,
                true,
                corruptLoad);

            dynamic workbook = workbookObject;
            workbook.SaveAs(
                targetPath,
                51,
                string.Empty,
                string.Empty,
                false,
                false,
                1,
                2,
                false,
                Type.Missing,
                Type.Missing,
                true);

            workbook.Close(false);
            ReleaseCom(ref workbookObject);

            excel.Quit();
            ReleaseCom(ref workbooksObject);
            ReleaseCom(ref excelObject);
            ForceComCollection();

            EnsureOwnedExcelExited(ownedExcelPid, existingExcelPids);

            if (!SpreadsheetSourceInspector.IsReadableOpenXmlWorkbook(targetPath))
            {
                TryDelete(targetPath);
                throw new InvalidDataException("Microsoft Excel opened the source but did not produce a readable .xlsx package.");
            }

            return targetPath;
        }
        catch
        {
            try
            {
                if (workbookObject is not null)
                {
                    dynamic workbook = workbookObject;
                    workbook.Close(false);
                }
            }
            catch { }
            finally { ReleaseCom(ref workbookObject); }

            try
            {
                if (excelObject is not null)
                {
                    dynamic excel = excelObject;
                    excel.Quit();
                }
            }
            catch { }

            ReleaseCom(ref workbooksObject);
            ReleaseCom(ref excelObject);
            ForceComCollection();
            EnsureOwnedExcelExited(ownedExcelPid, existingExcelPids);
            TryDelete(targetPath);
            throw;
        }
        finally
        {
            ReleaseCom(ref workbookObject);
            ReleaseCom(ref workbooksObject);
            ReleaseCom(ref excelObject);
        }
    }

    private static string CreateOwnedTempWorkbookPath(string purpose)
    {
        var safePurpose = SafePart.Replace((purpose ?? "Workbook").Trim(), "_").Trim('_');
        if (safePurpose.Length == 0) safePurpose = "Workbook";
        var folder = Path.Combine(Path.GetTempPath(), "AccountingAutomationHub", "ExcelInterop");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{safePurpose}_{Guid.NewGuid():N}.xlsx");
    }

    private static HashSet<int> SnapshotExcelProcessIds()
    {
        var result = new HashSet<int>();
        try
        {
            foreach (var process in Process.GetProcessesByName("EXCEL"))
            {
                using (process)
                {
                    try { result.Add(process.Id); } catch { }
                }
            }
        }
        catch { }
        return result;
    }

    private static int? TryGetExcelProcessId(object excelObject)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            dynamic excel = excelObject;
            var hwnd = new IntPtr(Convert.ToInt64(excel.Hwnd));
            if (hwnd == IntPtr.Zero) return null;
            _ = GetWindowThreadProcessId(hwnd, out var processId);
            return processId == 0 ? null : checked((int)processId);
        }
        catch { return null; }
    }

    private static void EnsureOwnedExcelExited(int? processId, IReadOnlySet<int> preExistingPids)
    {
        if (processId is null || processId <= 0 || preExistingPids.Contains(processId.Value)) return;
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (process.HasExited) return;
            if (process.WaitForExit(2500)) return;

            // This PID did not exist before AAH created the hidden COM Application.
            // It is therefore safe to terminate only this AAH-started orphan if Excel
            // ignored Quit because a COM RCW remained alive internally.
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2500);
        }
        catch { }
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseCom(ref object? value)
    {
        if (value is null) return;
        try
        {
            if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
        catch { }
        finally { value = null; }
    }

    private static void ForceComCollection()
    {
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
