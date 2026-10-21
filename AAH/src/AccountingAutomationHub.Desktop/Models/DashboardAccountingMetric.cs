namespace AccountingAutomationHub.Desktop.Models;

public sealed class DashboardAccountingMetric
{
    public string Label { get; init; } = string.Empty;
    public int Value { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string TargetPage { get; init; } = string.Empty;
    public bool HasActionRequired { get; init; }
    public int ActionRequiredCount { get; init; }

    // Dashboard-only presentation metadata.  The Bank Statement card keeps all of
    // its normal accounting information, but its primary emphasis is Ledger Mapping
    // completion so the user can see pending work without another dashboard tile.
    public bool IsBankStatementMetric { get; init; }
    public int PendingMappingCount { get; init; }
    public int CompletedMappingCount { get; init; }
    public int MappingTransactionCount { get; init; }
    public double MappingCompletionPercent { get; init; }
    public string StatusState { get; init; } = "Neutral";
}
