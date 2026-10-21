namespace AccountingAutomationHub.Domain.Tally;

public sealed class TallyReportSnapshot
{
    public string DisplayName { get; init; } = string.Empty;
    public string ReportId { get; init; } = string.Empty;
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }
    public bool IsAvailable { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public DateTime RefreshedUtc { get; init; }
    public int ResponseCharacters { get; init; }
    public string RawResponse { get; init; } = string.Empty;
    public IReadOnlyList<TallyReportRow> Rows { get; init; } = [];

    public decimal TotalDebit => Rows.Sum(row => row.Debit);
    public decimal TotalCredit => Rows.Sum(row => row.Credit);
    public decimal TotalAmount => Rows.Sum(row => row.Amount);
}
