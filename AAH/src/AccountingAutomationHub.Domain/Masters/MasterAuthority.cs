namespace AccountingAutomationHub.Domain.Masters;

public static class MasterAuthority
{
    public static bool IsTallySource(string? source) =>
        (source ?? string.Empty).Contains("Tally", StringComparison.OrdinalIgnoreCase);

    public static bool IsSystemProtectedSource(string? source) =>
        (source ?? string.Empty).Trim().Equals("Standard hierarchy", StringComparison.OrdinalIgnoreCase);

    public static bool IsLocalEditableSource(string? source) =>
        !IsTallySource(source) && !IsSystemProtectedSource(source);

    public static string EditAuthorityStatus(string? source)
    {
        if (IsTallySource(source)) return "Tally • Read-only";
        if (IsSystemProtectedSource(source)) return "System hierarchy • Protected";
        return "Local / Manual • Editable";
    }
}
