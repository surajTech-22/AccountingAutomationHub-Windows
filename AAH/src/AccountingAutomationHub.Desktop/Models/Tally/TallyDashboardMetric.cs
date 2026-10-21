namespace AccountingAutomationHub.Desktop.Models.Tally;

public sealed record TallyDashboardMetric(
    string Label,
    string Value,
    string Detail = "");
