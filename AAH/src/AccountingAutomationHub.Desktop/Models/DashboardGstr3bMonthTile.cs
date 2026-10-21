namespace AccountingAutomationHub.Desktop.Models;

public sealed class DashboardGstr3bMonthTile
{
    public string PeriodKey { get; init; } = string.Empty;
    public string Month { get; init; } = string.Empty;
    public string MonthLabel { get; init; } = string.Empty;
    public string StatusText { get; init; } = "Not available";
    public string StatusState { get; init; } = "Grey";
    public string Detail { get; init; } = string.Empty;
    public string TargetPage { get; init; } = "GST";
}
