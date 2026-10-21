using System.Windows;
using System.Windows.Controls;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AuditLinkSelectionWindow : Window
{
    private readonly List<AuditLinkSelectionItem> _allItems;

    public AuditLinkSelectionWindow(string title, string subtitle, IEnumerable<AuditLinkSelectionItem> items)
    {
        InitializeComponent();
        TitleBlock.Text = title;
        SubtitleBlock.Text = subtitle;
        _allItems = items?.ToList() ?? new List<AuditLinkSelectionItem>();
        ApplyFilter();
    }

    public AuditLinkSelectionItem? SelectedItem { get; private set; }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var search = SearchBox?.Text?.Trim() ?? string.Empty;
        var filtered = search.Length == 0
            ? _allItems
            : _allItems.Where(item => item.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        OptionsGrid.ItemsSource = filtered;
        CountBlock.Text = $"{filtered.Count:N0} option(s)";
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (OptionsGrid.SelectedItem is not AuditLinkSelectionItem selected)
        {
            MessageBox.Show(this, "Select one row to link.", "Audit Link", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedItem = selected;
        DialogResult = true;
    }

    private void OptionsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (OptionsGrid.SelectedItem is AuditLinkSelectionItem) Link_Click(sender, e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class AuditLinkSelectionItem
{
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public object? Value { get; set; }
    public string SearchText => $"{Name} {Code} {FinancialYear} {Gstin}";
}
