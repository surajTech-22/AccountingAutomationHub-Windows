namespace AccountingAutomationHub.Domain.Banking;

/// <summary>
/// One accounting-books sign convention for Bank balances inside AAH.
/// Positive = Debit balance. Negative = Credit balance.
///
/// Bank statements commonly label the customer's positive balance as CR and an
/// overdraft as DR because the statement is written from the bank's perspective.
/// Once imported into AAH those balances are business Ledger balances instead:
/// statement CR => business Dr (positive); statement DR => business Cr (negative).
/// </summary>
public static class BankBusinessBalance
{
    public static decimal CalculateClosing(decimal openingBalance, decimal statementDebits, decimal statementCredits) =>
        Math.Round(openingBalance + statementCredits - statementDebits, 2, MidpointRounding.AwayFromZero);

    public static string Format(decimal? balance)
    {
        if (!balance.HasValue) return "-";
        var rounded = Math.Round(balance.Value, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded) < 0.005m) return "0.00";
        return $"{Math.Abs(rounded):N2} {(rounded > 0m ? "Dr" : "Cr")}";
    }

    public static string Nature(decimal? balance)
    {
        if (!balance.HasValue || Math.Abs(balance.Value) < 0.005m) return string.Empty;
        return balance.Value > 0m ? "Dr" : "Cr";
    }

    public static decimal? WithNature(decimal? absoluteAmount, string? nature)
    {
        if (!absoluteAmount.HasValue) return null;
        var amount = Math.Abs(absoluteAmount.Value);
        return string.Equals((nature ?? string.Empty).Trim(), "Cr", StringComparison.OrdinalIgnoreCase)
            ? -amount
            : amount;
    }
}
