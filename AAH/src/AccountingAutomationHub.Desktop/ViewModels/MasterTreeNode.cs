using System.Collections.ObjectModel;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class MasterTreeNode
{
    public string Title { get; init; } = string.Empty;
    public string NodeKind { get; init; } = "All";
    public string FilterValue { get; init; } = string.Empty;
    public int RecordCount { get; set; }
    public bool IsExpanded { get; set; }
    public ObservableCollection<MasterTreeNode> Children { get; } = [];

    public string DisplayTitle => RecordCount > 0
        ? $"{Title} ({RecordCount:N0})"
        : Title;
}
