namespace AccountingAutomationHub.Domain.Sales;

public sealed class SalesB2BCustomerNameOverride
{
    public string Gstin { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public DateTime UpdatedUtc { get; init; }
}
