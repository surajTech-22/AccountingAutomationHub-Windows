namespace AccountingAutomationHub.Desktop.Models;

public sealed class GlobalSearchResult
{
    public string Category { get; init; } = string.Empty;
    public string ModuleTitle { get; init; } = string.Empty;
    public string PrimaryText { get; init; } = string.Empty;
    public string SecondaryText { get; init; } = string.Empty;
    public string SearchText { get; init; } = string.Empty;
    public int Score { get; init; }

    public string DisplayText => string.IsNullOrWhiteSpace(SecondaryText)
        ? $"{Category} · {PrimaryText}"
        : $"{Category} · {PrimaryText} — {SecondaryText}";

    public override string ToString() => DisplayText;
}
