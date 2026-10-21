using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace AccountingAutomationHub.Desktop.Views;

public partial class ColumnValueFilterWindow : Window
{
    private readonly ObservableCollection<FilterValueOption> _items;
    private readonly ICollectionView _view;
    public ColumnValueFilterWindow(string columnName, IEnumerable<(string Display, int Count)> values)
    {
        InitializeComponent();
        TitleText.Text = $"Choose values: {columnName}";
        _items = new ObservableCollection<FilterValueOption>(values.Select(item => new FilterValueOption { Display = item.Display, Count = item.Count, IsSelected = true }));
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = Filter;
        ValuesList.ItemsSource = _view;
        UpdateCount();
    }
    public IReadOnlyList<string> SelectedValues { get; private set; } = [];
    private bool Filter(object value) => value is FilterValueOption item && (string.IsNullOrWhiteSpace(SearchBox.Text) || item.Display.Contains(SearchBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { _view.Refresh(); UpdateCount(); }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var item in _view.Cast<FilterValueOption>()) item.IsSelected = true; ValuesList.Items.Refresh(); UpdateCount(); }
    private void ClearAll_Click(object sender, RoutedEventArgs e) { foreach (var item in _view.Cast<FilterValueOption>()) item.IsSelected = false; ValuesList.Items.Refresh(); UpdateCount(); }
    private void Apply_Click(object sender, RoutedEventArgs e) { SelectedValues = _items.Where(item => item.IsSelected).Select(item => item.Display).ToArray(); DialogResult = true; }
    private void UpdateCount() { if (CountText is not null) CountText.Text = $"{_items.Count(item => item.IsSelected):N0} of {_items.Count:N0} selected"; }
    private sealed class FilterValueOption : INotifyPropertyChanged
    {
        private bool _isSelected;
        public string Display { get; init; } = string.Empty;
        public int Count { get; init; }
        public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
