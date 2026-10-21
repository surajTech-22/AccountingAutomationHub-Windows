namespace AccountingAutomationHub.Domain.Sales;

public sealed class SalesCustomerMasterNameDetail
{
    public string Gstin { get; init; } = string.Empty;
    public string RecipientName { get; init; } = string.Empty;
    public string TradeName { get; init; } = string.Empty;
    public string PreferredName { get; init; } = string.Empty;
    public string SourceConflict { get; init; } = string.Empty;
}
