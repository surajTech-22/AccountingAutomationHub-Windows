namespace AccountingAutomationHub.Domain.Banking;

public sealed class BankAccountRecord
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string IfscCode { get; set; } = string.Empty;
    public decimal? OdLimit { get; set; }
    public decimal? DrawingPower { get; set; }
    public string LedgerName { get; set; } = string.Empty;
    // Fix74: permanent Tally financial-ledger binding. Bank/account display names are mutable;
    // once the live Master comparison binds this Bank Account, Inter-Bank and Bank export use
    // the Tally Ledger MASTERID as the accounting identity and keep the name only as display.
    public string TallyLedgerMasterId { get; set; } = string.Empty;
    public string TallyLedgerGuid { get; set; } = string.Empty;
    public long TallyLedgerAlterId { get; set; }
    public string TallyLedgerVerifiedName { get; set; } = string.Empty;
    public bool HasTallyLedgerIdentity => TallyLedgerMasterId.Trim().Length > 0;
    // UI-only rename history. This is hydrated from ClientPreferences by the desktop
    // view-model and is deliberately not part of the BankAccounts persistence schema.
    public string PreviousAuthorityName { get; set; } = string.Empty;
    // UI/report balance projection. These values are derived from BankBalanceAuthority
    // and are not persisted in BankAccounts.
    public decimal? OpeningBalance { get; set; }
    public decimal? ClosingBalance { get; set; }
    public string BalanceStatus { get; set; } = string.Empty;
    public string MissingPeriodText { get; set; } = string.Empty;
    public string OpeningBalanceText => BankBusinessBalance.Format(OpeningBalance);
    public string ClosingBalanceText => BankBusinessBalance.Format(ClosingBalance);
    public string DisplayName => BuildDisplayName();
    public string AuthorityName => string.IsNullOrWhiteSpace(LedgerName) ? DisplayName : LedgerName.Trim();
    public string BankAccountListName => AuthorityName;
    public string RenameHistoryToolTip
    {
        get
        {
            var oldName = PreviousAuthorityName.Trim();
            if (oldName.Length == 0) oldName = DisplayName;
            return $"Old name: {oldName}{Environment.NewLine}New name: {AuthorityName}";
        }
    }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    private string BuildDisplayName()
    {
        var bank = BankName.Trim();
        var type = AccountType.Trim();
        var account = AccountNumber.Trim();
        var bankAlreadyContainsType = type.Length > 0 &&
                                      bank.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                          .Any(token => token.Equals(type, StringComparison.OrdinalIgnoreCase));
        var bankAndType = bankAlreadyContainsType
            ? bank
            : string.Join(" ", new[] { bank, type }.Where(value => value.Length > 0));
        return bankAndType + (account.Length > 0 ? $" - {account}" : string.Empty);
    }
}
