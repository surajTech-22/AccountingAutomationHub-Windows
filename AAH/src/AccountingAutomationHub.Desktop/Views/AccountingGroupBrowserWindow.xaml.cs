using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AccountingAutomationHub.Desktop.Models;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AccountingGroupBrowserWindow : Window
{
    private readonly ObservableCollection<AccountingStatementLine> _rows = [];
    private readonly ICollectionView _view;
    public string SelectedGroup { get; private set; } = string.Empty;

    public AccountingGroupBrowserWindow(IEnumerable<AccountingStatementLine> rows)
    {
        InitializeComponent();
        foreach (var row in rows) _rows.Add(row);
        GroupsGrid.ItemsSource = _rows;
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = Filter;
    }

    private bool Filter(object item) => item is AccountingStatementLine row && (SearchBox.Text.Trim().Length == 0 || row.Label.Contains(SearchBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _view.Refresh();
    private void GroupsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();
    private void OpenGroup_Click(object sender, RoutedEventArgs e) => Accept();
    private void AllGroups_Click(object sender, RoutedEventArgs e) { SelectedGroup = string.Empty; DialogResult = true; }
    private void Accept() { if (GroupsGrid.SelectedItem is not AccountingStatementLine row) return; SelectedGroup = row.Label; DialogResult = true; }
}
