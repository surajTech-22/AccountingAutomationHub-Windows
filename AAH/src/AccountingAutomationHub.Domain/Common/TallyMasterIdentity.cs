using System.Text;

namespace AccountingAutomationHub.Domain.Common;

/// <summary>
/// Tally master-name identity. Tally treats spacing and punctuation as non-relevant
/// for duplicate master names (for example CST / C.S.T. / C. S. T.). This identity
/// is intentionally used only for master/ledger matching, never narration matching.
/// </summary>
public static class TallyMasterIdentity
{
    public static string Key(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Trim().Normalize(NormalizationForm.FormKC);
        return new string(normalized
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    public static bool Equivalent(string? first, string? second)
    {
        var firstKey = Key(first);
        return firstKey.Length > 0 &&
               firstKey.Equals(Key(second), StringComparison.OrdinalIgnoreCase);
    }
}
