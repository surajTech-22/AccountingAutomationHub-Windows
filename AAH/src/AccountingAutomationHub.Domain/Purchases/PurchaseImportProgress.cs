namespace AccountingAutomationHub.Domain.Purchases;

public sealed record PurchaseImportProgress(
    int Percent,
    string Stage);
