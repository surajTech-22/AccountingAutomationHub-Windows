namespace AccountingAutomationHub.Domain.Banking;

/// <summary>
/// Central classification policy for determining when a mapped Bank Statement ledger
/// represents a Bank, Bank OD/OCC, Secured Loan From Banks, or Cash-in-Hand counterpart
/// account and must therefore use Mode = 'Contra'.
/// </summary>
public static class BankContraLedgerPolicy
{
    public static string NormalizeGroupKey(string? groupName) =>
        new((groupName ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static bool IsDirectBankOrOverdraftGroupName(string? groupName)
    {
        var raw = (groupName ?? string.Empty).Trim();
        if (raw.Length == 0) return false;

        var key = NormalizeGroupKey(raw);
        if (key is "BANKACCOUNTS" or "BANKACCOUNT"
            or "BANKODAC" or "BANKODACCOUNTS" or "BANKODACCOUNT"
            or "BANKOCCAC" or "BANKOCCACCOUNTS" or "BANKOCCACCOUNT"
            or "OD" or "OCC"
            or "SECUREDLOANS" or "SECUREDLOAN" or "SECUREDLOANFROMBANKS" or "SECUREDLOANSFROMBANKS")
        {
            return true;
        }

        var upper = raw.ToUpperInvariant();
        if (upper.Contains("BANK ACCOUNT", StringComparison.Ordinal) ||
            upper.Contains("BANK OD", StringComparison.Ordinal) ||
            upper.Contains("BANK OCC", StringComparison.Ordinal) ||
            upper.Contains("OVERDRAFT", StringComparison.Ordinal) ||
            upper.Contains("CASH CREDIT", StringComparison.Ordinal) ||
            upper.Contains("SECURED LOAN", StringComparison.Ordinal) ||
            upper.Contains("OD A/C", StringComparison.Ordinal) ||
            upper.Contains("OD ACCOUNT", StringComparison.Ordinal) ||
            upper.Contains("OCC", StringComparison.Ordinal))
        {
            return true;
        }

        return upper.Contains("BANK", StringComparison.Ordinal) &&
               !upper.Contains("CHARGE", StringComparison.Ordinal) &&
               !upper.Contains("EXPENSE", StringComparison.Ordinal) &&
               !upper.Contains("INTEREST", StringComparison.Ordinal);
    }

    public static bool IsDirectCashInHandGroupName(string? groupName)
    {
        var raw = (groupName ?? string.Empty).Trim();
        if (raw.Length == 0) return false;

        var key = NormalizeGroupKey(raw);
        if (key is "CASHINHAND" or "CASHATHAND") return true;

        var upper = raw.ToUpperInvariant();
        return upper.Contains("CASH-IN-HAND", StringComparison.Ordinal) ||
               upper.Contains("CASH IN HAND", StringComparison.Ordinal);
    }

    public static bool IsDirectContraGroupName(string? groupName) =>
        IsDirectBankOrOverdraftGroupName(groupName) || IsDirectCashInHandGroupName(groupName);

    public static bool IsBankOrOverdraftGroup(
        string? groupName,
        IReadOnlyDictionary<string, string>? groupHierarchy = null) =>
        MatchesHierarchy(groupName, IsDirectBankOrOverdraftGroupName, groupHierarchy);

    public static bool IsContraCounterpartGroup(
        string? groupName,
        IReadOnlyDictionary<string, string>? groupHierarchy = null) =>
        MatchesHierarchy(groupName, IsDirectContraGroupName, groupHierarchy);

    private static bool MatchesHierarchy(
        string? groupName,
        Func<string?, bool> predicate,
        IReadOnlyDictionary<string, string>? groupHierarchy)
    {
        var current = (groupName ?? string.Empty).Trim();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; current.Length > 0 && depth < 64 && visited.Add(current); depth++)
        {
            if (predicate(current)) return true;
            if (groupHierarchy is null || !groupHierarchy.TryGetValue(current, out var parent)) break;
            current = (parent ?? string.Empty).Trim();
        }
        return false;
    }

    public static bool IsContraCounterpartLedger(
        string? ledgerName,
        IReadOnlyDictionary<string, string> ledgerGroups,
        IReadOnlyDictionary<string, string>? groupHierarchy = null)
    {
        var trimmed = (ledgerName ?? string.Empty).Trim();
        if (trimmed.Length == 0) return false;

        if (ledgerGroups.TryGetValue(trimmed, out var group) &&
            IsContraCounterpartGroup(group, groupHierarchy))
        {
            return true;
        }

        return false;
    }

    public static string ResolveModeForLedger(
        string? currentMode,
        string? ledgerName,
        decimal debit,
        decimal credit,
        IReadOnlyDictionary<string, string> ledgerGroups,
        IReadOnlyDictionary<string, string>? groupHierarchy = null)
    {
        var defaultVoucherMode = debit > 0m || credit <= 0m ? "Payment" : "Receipt";
        var existingMode = (currentMode ?? string.Empty).Trim();
        var trimmedLedger = (ledgerName ?? string.Empty).Trim();

        if (trimmedLedger.Length == 0)
            return existingMode.Length > 0 ? existingMode : defaultVoucherMode;

        if (IsContraCounterpartLedger(trimmedLedger, ledgerGroups, groupHierarchy))
            return "Contra";

        if (existingMode.Equals("Contra", StringComparison.OrdinalIgnoreCase) || existingMode.Length == 0)
            return defaultVoucherMode;

        return existingMode;
    }
}
