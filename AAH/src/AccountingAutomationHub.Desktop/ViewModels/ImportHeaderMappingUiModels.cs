using AccountingAutomationHub.Infrastructure.Imports;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class SettingsHeaderFieldRow
{
    public string Module { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool Required { get; init; }
    public string Requirement => Required ? "Required" : "Optional";
}

public sealed class PendingGlobalHeaderMappingRow : ViewModelBase
{
    private string _sourceHeader = string.Empty;
    private string _status = "Pending";

    public string Module { get; init; } = string.Empty;
    public string CanonicalField { get; init; } = string.Empty;
    public string CanonicalDisplay { get; init; } = string.Empty;
    public string SourceHeader
    {
        get => _sourceHeader;
        set => SetProperty(ref _sourceHeader, value ?? string.Empty);
    }
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value ?? string.Empty);
    }
}

public sealed record ActionHeaderCanonicalOption(string Key, string DisplayName, bool Required)
{
    public string Display => Required ? $"{DisplayName} · Required" : DisplayName;
}

public sealed class ActionMatchedHeaderRow
{
    public int ColumnIndex { get; init; }
    public string ColumnLabel { get; init; } = string.Empty;
    public string SourceHeader { get; init; } = string.Empty;
    public string CanonicalField { get; init; } = string.Empty;
    public string CanonicalDisplay { get; init; } = string.Empty;
    public string MatchType { get; init; } = string.Empty;
}

public sealed class ActionUnmatchedHeaderRow : ViewModelBase
{
    private ActionHeaderCanonicalOption? _selectedCanonical;
    public int ColumnIndex { get; init; }
    public string ColumnLabel { get; init; } = string.Empty;
    public string SourceHeader { get; init; } = string.Empty;
    public Action? SelectionChanged { get; init; }
    public ActionHeaderCanonicalOption? SelectedCanonical
    {
        get => _selectedCanonical;
        set
        {
            if (SetProperty(ref _selectedCanonical, value)) SelectionChanged?.Invoke();
        }
    }
}
