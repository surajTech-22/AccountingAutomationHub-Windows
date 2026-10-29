namespace AccountingAutomationHub.Domain.Clients;

/// <summary>
/// Distinguishes a GSTIN-derived placeholder from a source-backed business
/// name. A saved manual client name is never silently overwritten.
/// </summary>
public static class GstClientNameRecognitionPolicy
{
    public static bool IsUnresolvedName(string? name, string? gstin)
    {
        var cleaned = NormalizeIdentity(name);
        var normalizedGstin = NormalizeIdentity(gstin);
        return cleaned.Length == 0 ||
            (normalizedGstin.Length == 15 &&
             cleaned.Equals(normalizedGstin, StringComparison.OrdinalIgnoreCase));
    }

    public static bool HasSourceBusinessName(string? name, string? gstin)
    {
        var cleaned = NormalizeIdentity(name);
        if (cleaned.Length == 0 || IsUnresolvedName(name, gstin))
            return false;
        // Reject another bare GSTIN/PAN as a business name. Account-identity
        // strings and placeholders must not overwrite manually chosen names.
        if (cleaned.Length == 15 && char.IsDigit(cleaned[0]) && char.IsDigit(cleaned[1]) &&
            cleaned.All(char.IsLetterOrDigit))
            return false;
        if (cleaned.Length == 10 && cleaned.Take(5).All(char.IsLetter) &&
            cleaned.Skip(5).Take(4).All(char.IsDigit) && char.IsLetter(cleaned[^1]))
            return false;
        return true;
    }

    private static string NormalizeIdentity(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant).ToArray());
}
