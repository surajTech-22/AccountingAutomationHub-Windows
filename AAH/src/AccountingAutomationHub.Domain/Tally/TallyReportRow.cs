using System.Globalization;

namespace AccountingAutomationHub.Domain.Tally;

public sealed class TallyReportRow
{
    public string Particulars { get; init; } = string.Empty;
    public decimal Debit { get; init; }
    public decimal Credit { get; init; }
    public decimal Amount { get; init; }
    public string Detail { get; init; } = string.Empty;

    public string DebitDisplay => Debit == 0m
        ? string.Empty
        : Debit.ToString("N2", CultureInfo.CurrentCulture);

    public string CreditDisplay => Credit == 0m
        ? string.Empty
        : Credit.ToString("N2", CultureInfo.CurrentCulture);

    public string AmountDisplay => Amount == 0m
        ? string.Empty
        : Amount.ToString("N2", CultureInfo.CurrentCulture);
}
