namespace AccountingAutomationHub.Desktop.Models;

public sealed class SalesB2CCustomerMatchRow
{
    public string SourceKey { get; set; } = string.Empty;
    public IReadOnlyList<string> SourceKeys { get; set; } = Array.Empty<string>();
    public string SourceSection { get; set; } = string.Empty;
    public string PlaceOfSupply { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public int TransactionCount { get; set; }
    public bool IsMatched => CustomerName.Length > 0 && !CustomerName.StartsWith("B2C Sales", StringComparison.OrdinalIgnoreCase);
}
