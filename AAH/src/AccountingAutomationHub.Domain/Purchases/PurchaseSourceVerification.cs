namespace AccountingAutomationHub.Domain.Purchases;

public sealed class PurchaseSourceVerification
{
    public string EReturnsRoot { get; init; } = string.Empty;

    public bool Blocked { get; init; }

    public required PurchaseFormVerification TwoA { get; init; }

    public required PurchaseFormVerification TwoB { get; init; }
}

public sealed class PurchaseFormVerification
{
    public string Form { get; init; } = string.Empty;

    public string SourceOrigin { get; init; } = string.Empty;

    public string SourceBranch { get; init; } = string.Empty;

    public string Status { get; init; } = "Not Found";

    public bool Blocked { get; init; }

    public string ClientRoot { get; init; } = string.Empty;

    public string AnnualSource { get; init; } = string.Empty;

    public string AnnualEmbeddedTimestamp { get; init; } = string.Empty;

    public IReadOnlyList<string> MonthlySources { get; init; } = [];

    public IReadOnlyList<string> MonthlyMonthKeys { get; init; } = [];

    public IReadOnlyList<string> SelectedSources { get; init; } = [];

    public int AnnualDocumentCount { get; init; }

    public int MonthlyDocumentCount { get; init; }

    public int MissingInMonthlyCount { get; init; }

    public int ExtraInMonthlyCount { get; init; }

    public int DifferentDocumentCount { get; init; }

    public IReadOnlyList<string> MissingInMonthly { get; init; } = [];

    public IReadOnlyList<string> ExtraInMonthly { get; init; } = [];

    public IReadOnlyList<string> DifferentDocuments { get; init; } = [];
}
