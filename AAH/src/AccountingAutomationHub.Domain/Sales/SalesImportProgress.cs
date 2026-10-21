namespace AccountingAutomationHub.Domain.Sales;

public sealed record SalesImportProgress(
    int Percent,
    string Stage);
