using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Desktop.Views;

public partial class NewGroupMasterWindow : Window
{
    private readonly IReadOnlyDictionary<string, MasterRecord> _groups;
    private readonly string _editingGroupName;

    public NewGroupMasterWindow(
        IEnumerable<MasterRecord> groups,
        string company,
        string? defaultParent = null,
        MasterRecord? editRecord = null)
    {
        InitializeComponent();
        CompanyText.Text = company ?? string.Empty;
        _editingGroupName = editRecord?.Name?.Trim() ?? string.Empty;
        _groups = groups
            .Where(group => !group.IsArchived && group.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            .GroupBy(group => group.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(group => group.Name.Trim(), StringComparer.OrdinalIgnoreCase);

        ParentBox.Items.Add("Primary");
        foreach (var name in _groups.Keys
                     .Where(name => !name.Equals("Primary", StringComparison.OrdinalIgnoreCase) &&
                                    !_editingGroupName.Equals(name, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            ParentBox.Items.Add(name);

        var requested = editRecord?.Parent?.Trim();
        if (string.IsNullOrWhiteSpace(requested)) requested = string.IsNullOrWhiteSpace(defaultParent) ? "Primary" : defaultParent.Trim();
        ParentBox.SelectedItem = ParentBox.Items.Cast<object>()
            .FirstOrDefault(item => string.Equals(item?.ToString(), requested, StringComparison.OrdinalIgnoreCase)) ?? "Primary";

        if (editRecord is not null)
        {
            Title = "Edit Manual Group";
            HeaderText.Text = "Edit Manual Group";
            IntroText.Text = "Update this local Manual Group. Renaming the Group also updates child Masters and local Ledger Group references. Tally is not altered from this screen.";
            ActionButton.Content = "Update Group";
            GroupNameBox.Text = editRecord.Name;
        }

        Loaded += (_, _) =>
        {
            GroupNameBox.Focus();
            GroupNameBox.SelectAll();
        };
    }

    public string GroupName => GroupNameBox.Text.Trim();
    public string ParentGroup => ParentBox.SelectedItem?.ToString()?.Trim() ?? "Primary";

    private void ParentBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var name = ParentGroup;
        if (name.Equals("Primary", StringComparison.OrdinalIgnoreCase))
        {
            ParentParentText.Text = "Top level • local only until Tally Group Nature is specified";
            return;
        }

        ParentParentText.Text = _groups.TryGetValue(name, out var parent)
            ? (string.IsNullOrWhiteSpace(parent.Parent) ? "Primary" : parent.Parent.Trim())
            : string.Empty;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (GroupName.Length == 0)
        {
            ValidationText.Text = "Enter the Group name.";
            GroupNameBox.Focus();
            return;
        }

        if (!ParentGroup.Equals("Primary", StringComparison.OrdinalIgnoreCase) && !_groups.ContainsKey(ParentGroup))
        {
            ValidationText.Text = "Select Primary or an existing parent Group.";
            ParentBox.Focus();
            return;
        }

        if (GroupName.Equals(ParentGroup, StringComparison.OrdinalIgnoreCase))
        {
            ValidationText.Text = "A Group cannot be placed under itself.";
            ParentBox.Focus();
            return;
        }

        if (_editingGroupName.Length > 0 && IsDescendantOfEditedGroup(ParentGroup))
        {
            ValidationText.Text = "A Group cannot be moved under one of its own child/descendant Groups.";
            ParentBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private bool IsDescendantOfEditedGroup(string candidateParent)
    {
        if (_editingGroupName.Length == 0 || candidateParent.Equals("Primary", StringComparison.OrdinalIgnoreCase)) return false;
        var current = candidateParent;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (current.Length > 0 && !current.Equals("Primary", StringComparison.OrdinalIgnoreCase) && visited.Add(current))
        {
            if (current.Equals(_editingGroupName, StringComparison.OrdinalIgnoreCase)) return true;
            if (!_groups.TryGetValue(current, out var group)) return false;
            current = group.Parent?.Trim() ?? string.Empty;
        }
        return false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }
}
