namespace AccountingAutomationHub.Domain.Clients;

public sealed class GstinProfileLookupResult
{
    public string Gstin { get; init; } = string.Empty;

    public string LegalName { get; init; } = string.Empty;

    public string TradeName { get; init; } = string.Empty;

    public string Pan { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public string SourcePath { get; init; } = string.Empty;

    public DateTime SourceModifiedUtc { get; init; }

    public string Gstr3bYear { get; init; } = string.Empty;

    public string Gstr3bPeriod { get; init; } = string.Empty;

    public string FilingFrequency { get; init; } = string.Empty;

    public string PeriodStartKey { get; init; } = string.Empty;

    public string PeriodEndKey { get; init; } = string.Empty;

    public string Arn { get; init; } = string.Empty;

    public string ArnDate { get; init; } = string.Empty;

    public bool WasFound =>
        LegalName.Length > 0 ||
        TradeName.Length > 0;

    public bool HasGstr3bMetadata =>
        SourceType.Equals(
            "GSTR-3B PDF",
            StringComparison.OrdinalIgnoreCase) &&
        PeriodEndKey.Length == 6;
}
