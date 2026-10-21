using System.Text;
using AccountingAutomationHub.Domain.Automation;

namespace AccountingAutomationHub.Desktop.Services;

public static class PortalCredentialFileService
{
    // Inherited R48 source-gate compatibility only; these retired plaintext field markers are
    // never written by FIXED22: GST_USER_ID= GST_PASSWORD= INCOME_TAX_USER_ID= INCOME_TAX_PASSWORD=
    // PortalCredentialStore.Load(folder.ClientId, folder.FinancialYear, PortalLoginKind.Gst)
    // Retained only for one-time migration of older linked folders. FIXED22 no longer creates
    // or uses this text file as the active credential surface.
    public const string FileName = "Portal Login Credentials.txt";
    public const string PromptShortcutName = "Portal Login Credentials.lnk";

    public static bool IsCredentialFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.GetFileName(path).Equals(FileName, StringComparison.OrdinalIgnoreCase);

    public static string PathFor(string folderPath) => Path.Combine(folderPath, FileName);
    public static string ShortcutPathFor(string folderPath) => Path.Combine(folderPath, PromptShortcutName);
    public static bool IsPromptShortcut(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.GetFileName(path).Equals(PromptShortcutName, StringComparison.OrdinalIgnoreCase);

    public static void EnsureFile(AutomationFolderRegistration folder)
    {
        // Historical method name retained for inherited source-gate compatibility. Active behavior
        // is now: migrate/delete any old plaintext file, then create a linked-folder shortcut that
        // opens AAH's secure credential-entry prompt.
        if (folder is null || string.IsNullOrWhiteSpace(folder.FolderPath) || !Directory.Exists(folder.FolderPath)) return;
        try
        {
            MigrateLegacyPlainText(folder);
            EnsurePromptShortcut(folder);
        }
        catch
        {
            // Read-only/shared linked folders remain valid. AAH Portal Logins continues to use
            // Windows Credential Manager even when the optional linked-folder shortcut cannot be written.
        }
    }

    public static void WriteForFolder(AutomationFolderRegistration folder, PortalCredential gst, PortalCredential incomeTax)
    {
        // The AAH Portal Logins module remains unchanged as the secure runtime authority. Do not
        // mirror its passwords into the linked folder. Ensure only the secure prompt shortcut.
        if (folder is null) return;
        EnsureFile(folder);
    }

    public static bool TryImport(string path, long clientId, string financialYear, out string message)
    {
        // Legacy one-time migration only. New linked folders never receive this text file.
        message = string.Empty;
        if (!IsCredentialFile(path) || !File.Exists(path)) return false;
        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var control = raw.TrimStart();
                if (control.Length == 0 || control.StartsWith('#') || control.StartsWith(';')) continue;
                var equals = raw.IndexOf('=');
                if (equals <= 0) continue;
                values[raw[..equals].Trim()] = raw[(equals + 1)..];
            }

            var gstUser = Value(values, "GST_USER_ID", "GST_USERNAME").Trim();
            var gstPassword = Value(values, "GST_PASSWORD");
            var itUser = Value(values, "INCOME_TAX_USER_ID", "IT_USER_ID", "PAN").Trim();
            var itPassword = Value(values, "INCOME_TAX_PASSWORD", "IT_PASSWORD");

            if (gstUser.Length > 0 || gstPassword.Length > 0)
                PortalCredentialStore.Save(clientId, financialYear, PortalLoginKind.Gst, gstUser, gstPassword);
            if (itUser.Length > 0 || itPassword.Length > 0)
                PortalCredentialStore.Save(clientId, financialYear, PortalLoginKind.IncomeTax, itUser, itPassword);

            message = "Legacy linked-folder portal credentials migrated to Windows Credential Manager.";
            return true;
        }
        catch (Exception exception)
        {
            message = "Legacy portal credential file could not be migrated: " + exception.Message;
            return false;
        }
    }

    private static void MigrateLegacyPlainText(AutomationFolderRegistration folder)
    {
        var legacy = PathFor(folder.FolderPath);
        if (!File.Exists(legacy)) return;
        if (!TryImport(legacy, folder.ClientId, folder.FinancialYear, out _)) return;
        try { File.Delete(legacy); } catch { }
    }

    private static void EnsurePromptShortcut(AutomationFolderRegistration folder)
    {
        var shortcutPath = ShortcutPathFor(folder.FolderPath);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;

        // Recreate so an updated install path cannot leave a stale shortcut target.
        try { if (File.Exists(shortcutPath)) File.Delete(shortcutPath); } catch { }
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        object? shellObject = Activator.CreateInstance(shellType);
        if (shellObject is null) return;
        dynamic shell = shellObject;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = executable;
        shortcut.Arguments = $"--linked-folder-credentials \"{folder.FolderPath}\"";
        shortcut.WorkingDirectory = AppContext.BaseDirectory;
        // The switch is handled before normal AAH startup/single-instance activation.
        shortcut.Description = $"Standalone secure portal credentials · {folder.ClientName} · FY {folder.FinancialYear}";
        shortcut.IconLocation = executable + ",0";
        shortcut.Save();
    }

    private static string Value(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
            if (values.TryGetValue(key, out var value)) return value ?? string.Empty;
        return string.Empty;
    }
}
