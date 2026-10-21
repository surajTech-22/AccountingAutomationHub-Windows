namespace AccountingAutomationHub.Domain.Purchases;

public sealed class PurchaseAutoImportResult
{
    public string CompanyName { get; init; } = string.Empty;

    public string ClientGstin { get; init; } = string.Empty;

    public string FinancialYear { get; init; } = string.Empty;

    public bool WasBlocked { get; init; }

    public int PersistedRows { get; set; }

    public string SourceFingerprint { get; set; } = string.Empty;

    public required IReadOnlyList<PurchaseImportRecord> Records { get; init; }

    public required IReadOnlyList<PurchaseImportIssue> Issues { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required PurchaseImportSummary Summary { get; init; }

    public required PurchaseSourceVerification Verification { get; init; }

    public required IReadOnlyList<PurchaseSourceManifestItem> SourceManifest { get; init; }
}
