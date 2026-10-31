namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Explicit opt-in gate. Multiple GSTINs alone are NOT separate branch books:
/// existing GST-wise Sales/Purchase import and merged GST return workflow must
/// remain unchanged until the user deliberately enables branch accounting.
/// </summary>
public static class BranchAccountingModePreference
{
    public const string Prefix = "Clients.BranchAccountingMode.V1.";
    public static string Key(long clientId) => Prefix + clientId;
    public static bool IsEnabled(string? value) =>
        "true".Equals(value?.Trim(), System.StringComparison.OrdinalIgnoreCase) ||
        "1".Equals(value?.Trim(), System.StringComparison.Ordinal);
}
