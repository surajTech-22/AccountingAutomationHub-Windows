using AccountingAutomationHub.Desktop.Controls;
using AccountingAutomationHub.Domain.Accounting;

namespace AccountingAutomationHub.Desktop.Models;

/// <summary>
/// Rich, read-only Ledger choice used by accounting entry pickers.
/// The value committed to the record is always Name; group and balance are display metadata.
/// </summary>
public sealed class LedgerPickerOption : ISuggestionTextItem
{
    public string Name { get; init; } = string.Empty;
    public string ParentGroup { get; init; } = string.Empty;
    public decimal OpeningBalance { get; init; }
    public decimal DebitTotal { get; init; }
    public decimal CreditTotal { get; init; }
    public decimal ClosingBalance { get; init; }
    public int MovementCount { get; init; }

    public string BalanceText => FormatBalance(ClosingBalance);
    public string GroupText => string.IsNullOrWhiteSpace(ParentGroup) ? "Group not available" : ParentGroup;
    public string ActivityText => $"Dr {DebitTotal:N2}  •  Cr {CreditTotal:N2}  •  {MovementCount:N0} entr{(MovementCount == 1 ? "y" : "ies")}";
    public string HoverText => $"{Name}\nClosing balance: {BalanceText}\nGroup: {GroupText}";
    public string SuggestionValue => Name;
    public string SuggestionDisplay => $"{Name}  •  {GroupText}  •  {BalanceText}";

    // Editable WPF ComboBox can fall back to ToString() when keyboard focus leaves
    // the selector. Returning rich display text here caused the committed Ledger name
    // to be replaced by "Name • Group • Balance" and then fail strict matching when
    // the user moved to Amount. The popup still renders SuggestionDisplay explicitly.
    public override string ToString() => Name;

    public static LedgerPickerOption FromSnapshot(LedgerTotalSnapshot snapshot) =>
        new()
        {
            Name = snapshot.LedgerName,
            ParentGroup = snapshot.ParentGroup,
            OpeningBalance = snapshot.OpeningBalance,
            DebitTotal = snapshot.TotalDebit,
            CreditTotal = snapshot.TotalCredit,
            ClosingBalance = snapshot.ClosingBalance,
            MovementCount = snapshot.MovementCount
        };

    public static string FormatBalance(decimal value)
    {
        if (Math.Abs(value) < 0.005m) return "0.00";
        return $"{Math.Abs(value):N2} {(value > 0m ? "Dr" : "Cr")}";
    }
}
