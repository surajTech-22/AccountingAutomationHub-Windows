using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Models.Tally;

public sealed class TallyExportMonthOption : ViewModelBase
{
    private bool _isSelected;

    public string PeriodKey { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string ShortLabel { get; init; } = string.Empty;
    public string ShortcutText { get; init; } = string.Empty;
    public int MonthIndex { get; init; }
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }
    public int SourceCount { get; init; }
    public int ImportedCount { get; init; }
    public string StatusState { get; init; } = "Grey";
    public string StatusText { get; init; } = "No data";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public override string ToString() => Label;
}
