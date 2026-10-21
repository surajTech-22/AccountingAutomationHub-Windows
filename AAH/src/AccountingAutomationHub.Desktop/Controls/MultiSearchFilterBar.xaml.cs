using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.Behaviors;

namespace AccountingAutomationHub.Desktop.Controls;

public partial class MultiSearchFilterBar : UserControl
{
    public static readonly DependencyProperty SearchTextProperty =
        DependencyProperty.Register(
            nameof(SearchText),
            typeof(string),
            typeof(MultiSearchFilterBar),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSearchTextChanged));

    private readonly List<DataGrid> _targets = [];
    private string _defaultDatePath = string.Empty;
    private bool _acceptingSuggestion;
    private readonly DispatcherTimer _searchDebounceTimer;
    public ObservableCollection<string> AvailableFields { get; } = [];
    public event EventHandler? SearchApplied;

    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value ?? string.Empty);
    }

    public MultiSearchFilterBar()
    {
        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        InitializeComponent();
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ApplySearch();
        };
    }

    // Fix137 R46 R9: Ctrl+F/global-search navigation delegates to the active module's
    // shared search bar. Optional text lets Dashboard All Search carry the same query into
    // the destination module instead of opening an unrelated page-level search.
    public void FocusSearchBox(string? searchText = null)
    {
        if (searchText is not null)
        {
            SearchText = searchText;
            SearchBox.Text = searchText;
            _searchDebounceTimer.Stop();
            ApplySearch();
        }
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void Attach(params DataGrid[] grids) => Attach(string.Empty, grids);
    public void Attach(string defaultDatePath, params DataGrid[] grids)
    {
        _defaultDatePath = (defaultDatePath ?? string.Empty).Trim();
        _targets.Clear();
        foreach (var grid in grids.Where(grid => grid is not null).Distinct()) _targets.Add(grid);
        RefreshFields();
        ApplySearch();
    }

    public void RefreshFields()
    {
        var fields = _targets.SelectMany(grid => grid.Columns)
            .Select(column => column.Header?.ToString()?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToArray();
        AvailableFields.Clear();
        foreach (var field in fields) AvailableFields.Add(field);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_acceptingSuggestion) RefreshSuggestions();
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void RefreshSuggestions()
    {
        RefreshFields();
        var text = SearchBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 0) { SuggestionsPopup.IsOpen = false; return; }
        var tokens = text.Split(new[]{' ', '\t', ':', '=', '>', '<', ','}, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 1).ToArray();
        var matches = AvailableFields.Where(field => tokens.Any(token =>
                field.Contains(token, StringComparison.CurrentCultureIgnoreCase) ||
                field.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word => word.StartsWith(token, StringComparison.CurrentCultureIgnoreCase))))
            .Take(8).ToArray();
        SuggestionsList.ItemsSource = matches;
        SuggestionsPopup.IsOpen = matches.Length > 0;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearSearchFromEscape();
            e.Handled = true;
            return;
        }
        if (!SuggestionsPopup.IsOpen) return;
        if (e.Key == Key.Down && SuggestionsList.Items.Count > 0) { SuggestionsList.SelectedIndex = Math.Min(SuggestionsList.SelectedIndex + 1, SuggestionsList.Items.Count - 1); e.Handled = true; }
        else if (e.Key == Key.Up && SuggestionsList.Items.Count > 0) { SuggestionsList.SelectedIndex = Math.Max(0, SuggestionsList.SelectedIndex - 1); e.Handled = true; }
        else if ((e.Key == Key.Enter || e.Key == Key.Tab) && SuggestionsList.SelectedItem is string) { AcceptSuggestion(); e.Handled = true; }
    }

    // Fix137 R46 R14: Escape is a search-local action. Clear only the search term,
    // close suggestions, keep the module open, and leave existing column sort/filter state intact.
    public void ClearSearchFromEscape()
    {
        _searchDebounceTimer.Stop();
        SuggestionsPopup.IsOpen = false;
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        else ApplySearch();
        SearchBox.Focus();
    }

    private void Suggestion_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void Suggestion_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSuggestion();

    private void AcceptSuggestion()
    {
        if (SuggestionsList.SelectedItem is not string field) return;
        var text = SearchBox.Text ?? string.Empty;
        var caret = SearchBox.CaretIndex;
        var left = text[..Math.Min(caret, text.Length)];
        var start = left.Length;
        while (start > 0 && !char.IsWhiteSpace(left[start - 1]) && left[start - 1] != ',') start--;
        var right = text[Math.Min(caret, text.Length)..];
        _acceptingSuggestion = true;
        SearchBox.Text = text[..start] + field + " " + right.TrimStart();
        SearchBox.CaretIndex = Math.Min(SearchBox.Text.Length, start + field.Length + 1);
        _acceptingSuggestion = false;
        SuggestionsPopup.IsOpen = false;
        SearchBox.Focus();
        _searchDebounceTimer.Stop();
        ApplySearch();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        _searchDebounceTimer.Stop();
        SearchBox.Clear();
        SuggestionsPopup.IsOpen = false;
        foreach (var grid in _targets)
            DataGridColumnFilterBehavior.ClearAllSortAndFilters(grid);
        SearchBox.Focus();
    }
    private void ApplySearch()
    {
        var text = SearchBox?.Text?.Trim() ?? string.Empty;
        // A comma is an explicit AND separator. DataGridColumnFilterBehavior requires
        // every supplied term to match somewhere on the row, so this applies the same
        // AND-search rule everywhere the shared search bar is used.
        var terms = text.Length == 0
            ? Array.Empty<string>()
            : text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var grid in _targets) DataGridColumnFilterBehavior.SetGlobalSearchTerms(grid, terms, _defaultDatePath);
        SearchApplied?.Invoke(this, EventArgs.Empty);
    }

    private static void OnSearchTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MultiSearchFilterBar bar || bar.SearchBox is null) return;
        var next = e.NewValue as string ?? string.Empty;
        if (string.Equals(bar.SearchBox.Text, next, StringComparison.Ordinal)) return;

        bar.SearchBox.Text = next;
        bar.SearchBox.CaretIndex = bar.SearchBox.Text.Length;
        bar._searchDebounceTimer.Stop();
        bar.ApplySearch();
    }
}
