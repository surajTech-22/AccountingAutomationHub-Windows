namespace AccountingAutomationHub.Infrastructure.Persistence;

public static class ApplicationDataPaths
{
    private static string? _rootOverride;

    public static void ConfigureRootOverride(string? rootFolder)
    {
        _rootOverride = string.IsNullOrWhiteSpace(rootFolder) ? null : Path.GetFullPath(rootFolder.Trim());
    }

    public static string RootFolder =>
        _rootOverride ??
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Accounting Automation Hub");

    public static string DataFolder => Path.Combine(RootFolder, "Data");
    public static string DatabasePath => Path.Combine(DataFolder, "accounting-hub.db");
    public static string AuditDatabasePath => Path.Combine(DataFolder, "audit-hub.db");
    public static string DocumentVaultFolder => Path.Combine(RootFolder, "DocumentVault");
    public static string UniversalDumpFolder => Path.Combine(RootFolder, "UniversalDump");
    public static string VerifiedBackupFolder => Path.Combine(RootFolder, "VerifiedBackups");

    public static void EnsureFolders()
    {
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(DocumentVaultFolder);
        Directory.CreateDirectory(UniversalDumpFolder);
        Directory.CreateDirectory(VerifiedBackupFolder);
    }
}
