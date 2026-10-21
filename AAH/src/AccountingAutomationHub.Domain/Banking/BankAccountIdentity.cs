namespace AccountingAutomationHub.Domain.Banking;

/// <summary>
/// Stable Bank account identity authority. A complete, unmasked account number is
/// the primary physical-account identifier. Bank display wording and Account Type
/// are normalized so suffixes such as SB/CA/OD do not split one real account.
/// A normalized Bank family / IFSC bank code is retained as a safety boundary so
/// an identical account number at two unrelated banks is never merged blindly.
/// </summary>
public static class BankAccountIdentity
{
    private static readonly HashSet<string> TrailingAccountTypeTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "SB", "SAVING", "SAVINGS", "CA", "CURRENT", "OD", "CC", "ACCOUNT", "AC"
    };

    public static string StrongAccountNumber(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0 || raw.IndexOf('*') >= 0 || raw.IndexOf('•') >= 0)
            return string.Empty;

        var compact = new string(raw
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

        // Short or obviously masked values (for example XX0724) are not strong enough to
        // merge two accounts. Full numeric/alphanumeric account numbers remain authoritative.
        if (compact.Length < 6 || compact.Count(char.IsDigit) < 4 || compact.Count(ch => ch == 'X') >= 2)
            return string.Empty;

        return compact;
    }

    public static string BankFamily(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0) return string.Empty;

        var normalized = new string(raw
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : ' ')
            .ToArray());
        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // Statement exports often append account type to the Bank name itself. Remove only
        // trailing generic account-type words; the actual Bank-name words stay authoritative.
        while (tokens.Count > 0 && TrailingAccountTypeTokens.Contains(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);

        return string.Join(" ", tokens);
    }

    public static string IfscBankCode(string? value)
    {
        var compact = new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (compact.Length < 4) return string.Empty;
        var firstFour = compact[..4];
        return firstFour.All(char.IsLetter) ? firstFour : string.Empty;
    }

    public static string BuildKey(BankAccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var strongNumber = StrongAccountNumber(account.AccountNumber);
        if (strongNumber.Length > 0)
        {
            var family = BankFamily(account.BankName);
            if (family.Length > 0)
                return $"{account.ClientId}|{account.FinancialYear.Trim()}|BANK|{family}|ACCOUNT|{strongNumber}";

            var ifscBank = IfscBankCode(account.IfscCode);
            if (ifscBank.Length > 0)
                return $"{account.ClientId}|{account.FinancialYear.Trim()}|IFSC|{ifscBank}|ACCOUNT|{strongNumber}";

            return $"{account.ClientId}|{account.FinancialYear.Trim()}|ACCOUNT|{strongNumber}";
        }

        return $"{account.ClientId}|{account.FinancialYear.Trim()}|FALLBACK|{account.BankName.Trim()}|{account.AccountNumber.Trim()}|{account.AccountType.Trim()}";
    }

    public static bool Same(BankAccountRecord left, BankAccountRecord right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.ClientId != right.ClientId ||
            !left.FinancialYear.Equals(right.FinancialYear, StringComparison.OrdinalIgnoreCase))
            return false;

        var leftNumber = StrongAccountNumber(left.AccountNumber);
        var rightNumber = StrongAccountNumber(right.AccountNumber);
        if (leftNumber.Length > 0 && rightNumber.Length > 0)
        {
            if (!leftNumber.Equals(rightNumber, StringComparison.OrdinalIgnoreCase))
                return false;

            var leftFamily = BankFamily(left.BankName);
            var rightFamily = BankFamily(right.BankName);
            if (leftFamily.Length > 0 && rightFamily.Length > 0)
            {
                if (leftFamily.Equals(rightFamily, StringComparison.OrdinalIgnoreCase))
                    return true;

                var leftIfsc = IfscBankCode(left.IfscCode);
                var rightIfsc = IfscBankCode(right.IfscCode);
                return leftIfsc.Length > 0 && rightIfsc.Length > 0 &&
                       leftIfsc.Equals(rightIfsc, StringComparison.OrdinalIgnoreCase);
            }

            var fallbackLeftIfsc = IfscBankCode(left.IfscCode);
            var fallbackRightIfsc = IfscBankCode(right.IfscCode);
            return fallbackLeftIfsc.Length == 0 || fallbackRightIfsc.Length == 0 ||
                   fallbackLeftIfsc.Equals(fallbackRightIfsc, StringComparison.OrdinalIgnoreCase);
        }

        return left.BankName.Equals(right.BankName, StringComparison.OrdinalIgnoreCase) &&
               left.AccountNumber.Equals(right.AccountNumber, StringComparison.OrdinalIgnoreCase) &&
               left.AccountType.Equals(right.AccountType, StringComparison.OrdinalIgnoreCase);
    }
}
