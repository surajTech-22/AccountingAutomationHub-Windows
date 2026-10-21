namespace AccountingAutomationHub.Desktop.Models;

public sealed class DashboardClientRow
{
    public long ClientId { get; init; }

    public string ClientCode { get; init; } = string.Empty;

    public string ClientName { get; init; } = string.Empty;

    public string TradeName { get; init; } = string.Empty;

    public string LegalName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Gstin { get; init; } = string.Empty;

    public string FinancialYear { get; init; } = string.Empty;

    public bool IsCurrent { get; init; }

    public bool IsBankImported { get; init; }

    public bool IsPurchaseImported { get; init; }

    public bool IsSalesImported { get; init; }

    public string BankText { get; init; } = string.Empty;

    public string PurchaseText { get; init; } = string.Empty;

    public string SalesText { get; init; } = string.Empty;

    public string FilingStatus { get; init; } = string.Empty;

    public bool IsFilingChecked { get; init; }

    public int CompletedImports { get; init; }

    public int PendingImports => 3 - CompletedImports;

    public bool IsComplete => CompletedImports == 3;

    public string CompletionText => $"{CompletedImports}/3";

    public string OverallStatus =>
        IsComplete
            ? "Complete"
            : $"{PendingImports} pending";
}
