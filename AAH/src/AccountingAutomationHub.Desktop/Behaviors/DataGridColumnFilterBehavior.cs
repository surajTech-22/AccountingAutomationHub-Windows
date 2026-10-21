using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AccountingAutomationHub.Desktop.Views;
using AccountingAutomationHub.Domain.Purchases;

namespace AccountingAutomationHub.Desktop.Behaviors;

/// <summary>
/// Application-wide, virtualization-safe DataGrid column value filters.
/// It layers its predicate on top of any filter already owned by the source view,
/// so page-specific filtering remains authoritative. Date columns are presented as
/// Year -> Month -> Date rather than one long alphabetic string list.
/// </summary>
public static class DataGridColumnFilterBehavior
{
    public static readonly RoutedUICommand OpenFilterCommand = new(
        "Open column filter",
        nameof(OpenFilterCommand),
        typeof(DataGridColumnFilterBehavior));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(DataGridColumnFilterBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty TotalTextProperty = DependencyProperty.RegisterAttached(
        "TotalText",
        typeof(string),
        typeof(DataGridColumnFilterBehavior),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TotalPathProperty = DependencyProperty.RegisterAttached(
        "TotalPath",
        typeof(string),
        typeof(DataGridColumnFilterBehavior),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ShowTotalsProperty = DependencyProperty.RegisterAttached(
        "ShowTotals",
        typeof(bool),
        typeof(DataGridColumnFilterBehavior),
        new PropertyMetadata(true, OnShowTotalsChanged));

    private static readonly ConditionalWeakTable<DataGrid, GridFilterState> States = new();
    private static readonly ConditionalWeakTable<object, PersistedGridFilterState> PersistedStates = new();

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetTotalText(DependencyObject element, string value) => element.SetValue(TotalTextProperty, value ?? string.Empty);
    public static string GetTotalText(DependencyObject element) => (string)element.GetValue(TotalTextProperty);
    public static void SetTotalPath(DependencyObject element, string value) => element.SetValue(TotalPathProperty, value ?? string.Empty);
    public static string GetTotalPath(DependencyObject element) => (string)element.GetValue(TotalPathProperty);
    public static void SetShowTotals(DependencyObject element, bool value) => element.SetValue(ShowTotalsProperty, value);
    public static bool GetShowTotals(DependencyObject element) => (bool)element.GetValue(ShowTotalsProperty);

    /// <summary>
    /// Applies one or more module-level search terms without replacing Excel-style
    /// column filters. Every non-empty term must be found somewhere on the row; a
    /// term searches all simple public row properties so dynamically hidden columns
    /// and template columns are still included.
    /// </summary>
    public static void SetGlobalSearchTerms(
        DataGrid grid,
        IEnumerable<string>? terms,
        string? defaultDatePath = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        Attach(grid);
        if (!States.TryGetValue(grid, out var state)) return;

        state.SearchTerms.Clear();
        state.DefaultDatePath = (defaultDatePath ?? string.Empty).Trim();
        RebuildSearchFieldAliases(grid, state);
        if (terms is not null)
        {
            foreach (var term in terms
                         .Select(value => (value ?? string.Empty).Trim())
                         .Where(value => value.Length > 0)
                         .Distinct(StringComparer.CurrentCultureIgnoreCase))
            {
                state.SearchTerms.Add(term);
            }
        }

        BindView(grid, state);
        ApplySearchSortCommands(state);
        if (state.View?.CanFilter == true) ApplyFilters(grid, state);
        else QueueTotalsRefresh(grid);
    }

    /// <summary>
    /// Fix13 common reset used by every module that participates in the global DataGrid
    /// sort/filter behavior. Clears global search terms, all column filters, persisted
    /// filter state and sort descriptions without replacing the page-owned base filter.
    /// </summary>
    public static void ClearAllSortAndFilters(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        Attach(grid);
        if (!States.TryGetValue(grid, out var state)) return;

        BindView(grid, state);
        Interlocked.Increment(ref state.FilterGeneration);
        state.PreparedMatches = null;
        state.PreparedSource = null;
        state.Filters.Clear();
        state.SearchTerms.Clear();

        var persistenceSource = GetPersistenceSource(grid);
        if (persistenceSource is not null && PersistedStates.TryGetValue(persistenceSource, out var persisted))
            persisted.Filters.Clear();

        if (state.View is { } view)
        {
            if (view.CanSort) view.SortDescriptions.Clear();
            if (view.CanFilter) view.Filter = state.OriginalFilter;
            view.Refresh();
        }

        foreach (var column in grid.Columns) column.SortDirection = null;
        QueueTotalsRefresh(grid);
    }

    private static void OnShowTotalsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DataGrid grid) QueueTotalsRefresh(grid);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        if ((bool)e.NewValue) Attach(grid);
        else Detach(grid);
    }

    private static void Attach(DataGrid grid)
    {
        if (States.TryGetValue(grid, out _)) return;

        var state = new GridFilterState();
        state.CommandBinding = new CommandBinding(OpenFilterCommand, OpenFilterExecuted, OpenFilterCanExecute);
        grid.CommandBindings.Add(state.CommandBinding);
        grid.Loaded += Grid_Loaded;
        grid.Unloaded += Grid_Unloaded;
        grid.DataContextChanged += Grid_DataContextChanged;
        grid.CellEditEnding += Grid_CellEditEnding;
        grid.RowEditEnding += Grid_RowEditEnding;
        States.Add(grid, state);
    }

    private static void Detach(DataGrid grid)
    {
        if (!States.TryGetValue(grid, out var state)) return;
        RestoreOriginalViewFilter(state);
        if (state.CommandBinding is not null) grid.CommandBindings.Remove(state.CommandBinding);
        grid.Loaded -= Grid_Loaded;
        grid.Unloaded -= Grid_Unloaded;
        grid.DataContextChanged -= Grid_DataContextChanged;
        grid.CellEditEnding -= Grid_CellEditEnding;
        grid.RowEditEnding -= Grid_RowEditEnding;
        UnsubscribeViewCollectionChanged(state);
        Interlocked.Increment(ref state.FilterGeneration);
        state.PreparedMatches = null;
        state.PreparedSource = null;
        States.Remove(grid);
    }

    private static void Grid_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGrid grid && States.TryGetValue(grid, out var state))
        {
            state.OwningTabControl = FindOwningTabControl(grid);
            BindView(grid, state);
        }
    }

    private static void Grid_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || !States.TryGetValue(grid, out var state)) return;

        PersistFilters(grid, state);
        Interlocked.Increment(ref state.TotalsGeneration);

        // R8 Repair 8 inner-tab safety bar: a normal WPF TabControl selection unloads
        // the outgoing DataGrid even though the owning module remains loaded. Do not tear
        // down and immediately rebuild the same ICollectionView/filter state in that case.
        // When the whole module closes, the TabControl is no longer loaded and the normal
        // cleanup path still restores the page-owned filter and removes subscriptions.
        if (state.OwningTabControl?.TryGetTarget(out var owner) == true && owner.IsLoaded && owner.IsVisible)
            return;

        RestoreOriginalViewFilter(state);
        state.OwningTabControl = null;
    }

    private static void Grid_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is DataGrid grid && States.TryGetValue(grid, out var state))
            BindView(grid, state);
    }

    private static void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        InvalidateSearchProjection(e.Row.Item);
        QueueFilterOrTotalsRefresh(sender as DataGrid);
    }

    private static void Grid_RowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
    {
        InvalidateSearchProjection(e.Row.Item);
        QueueFilterOrTotalsRefresh(sender as DataGrid);
    }

    private static void QueueFilterOrTotalsRefresh(DataGrid? grid)
    {
        if (grid is null) return;
        _ = grid.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!States.TryGetValue(grid, out var state)) return;
            if (state.Filters.Count > 0 || state.SearchTerms.Count > 0) ApplyFilters(grid, state);
            else QueueTotalsRefresh(grid);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private static void QueueTotalsRefresh(DataGrid? grid)
    {
        if (grid is null || !States.TryGetValue(grid, out var state)) return;
        var generation = Interlocked.Increment(ref state.TotalsGeneration);

        // R8 R6 navigation safety bar: DataGrid Loaded/Unloaded and ReplaceRange can fire
        // several times while the user is rapidly switching modules or inner tabs. Header
        // totals are presentation-only, so coalesce that burst and calculate only for the
        // grid that is still loaded after the UI settles. The exact total rules are retained.
        _ = Task.Run(async () =>
        {
            await Task.Delay(120).ConfigureAwait(false);
            if (generation != Volatile.Read(ref state.TotalsGeneration)) return;
            _ = grid.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != Volatile.Read(ref state.TotalsGeneration) ||
                    !States.TryGetValue(grid, out var current) || !ReferenceEquals(current, state) ||
                    !grid.IsLoaded) return;
                UpdateHeaderTotals(grid, state, generation);
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        });
    }

    private static void OpenFilterCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = false;
        if (sender is not DataGrid grid || e.Parameter is not DataGridColumnHeader header)
            return;

        // WPF can query a header command while a virtualized/recycled header has not yet
        // been associated with its DataGridColumn. Treat that transient state as
        // non-filterable instead of dereferencing a null Column during command requery.
        var column = header.Column;
        if (column is null)
        {
            e.Handled = true;
            return;
        }

        e.CanExecute = GetPropertyPath(column).Length > 0 && GetView(grid)?.CanFilter == true;
        e.Handled = true;
    }

    private static void OpenFilterExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is not DataGrid grid || e.Parameter is not DataGridColumnHeader header) return;
        if (!States.TryGetValue(grid, out var state)) return;

        var column = header.Column;
        if (column is null) return;

        BindView(grid, state);
        var path = GetPropertyPath(column);
        if (path.Length == 0 || state.View?.CanFilter != true) return;

        var values = GetSourceItems(grid)
            .Select(item => new ValueRow(item, ReadPath(item, path)))
            .ToArray();

        var menu = BuildMenu(grid, state, column, path, values);
        var filterButton = FindFilterButton(header);
        menu.PlacementTarget = filterButton is not null ? (UIElement)filterButton : header;
        menu.Placement = PlacementMode.Bottom;
        menu.MaxHeight = 560;
        menu.MaxWidth = 600;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static ContextMenu BuildMenu(
        DataGrid grid,
        GridFilterState state,
        DataGridColumn column,
        string path,
        IReadOnlyList<ValueRow> rows)
    {
        var menu = new ContextMenu();
        var label = column.Header?.ToString()?.Trim() ?? path;

        var sortAscending = new MenuItem { Header = "Sort ascending" };
        sortAscending.Click += (_, _) => ApplySort(state, path, ListSortDirection.Ascending);
        menu.Items.Add(sortAscending);
        var sortDescending = new MenuItem { Header = "Sort descending" };
        sortDescending.Click += (_, _) => ApplySort(state, path, ListSortDirection.Descending);
        menu.Items.Add(sortDescending);
        var clearSort = new MenuItem { Header = "Clear sort" };
        clearSort.Click += (_, _) => { if (state.View?.CanSort == true) { state.View.SortDescriptions.Clear(); foreach (var gridColumn in grid.Columns) gridColumn.SortDirection = null; state.View.Refresh(); } };
        menu.Items.Add(clearSort);
        var clearAllSortAndFilters = new MenuItem { Header = "Clear all sort & filters", FontWeight = FontWeights.SemiBold };
        clearAllSortAndFilters.Click += (_, _) => ClearAllSortAndFilters(grid);
        menu.Items.Add(clearAllSortAndFilters);
        menu.Items.Add(new Separator());

        var chooseValues = new MenuItem { Header = "Choose Values…", FontWeight = FontWeights.SemiBold };
        chooseValues.Click += (_, _) =>
        {
            var options = rows.GroupBy(row => IsBlank(row.Value) ? "(Blanks)" : FormatValue(row.Value), StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(group => (group.Key, group.Count())).ToArray();
            var window = new ColumnValueFilterWindow(label, options) { Owner = Window.GetWindow(grid) };
            if (window.ShowDialog() != true) return;
            if (window.SelectedValues.Count == options.Length) state.Filters.Remove(column);
            else state.Filters[column] = ColumnFilter.Multi(path, window.SelectedValues);
            ApplyFilters(grid, state);
        };
        menu.Items.Add(chooseValues);

        var advanced = new MenuItem { Header = "Search / Advanced Filter…", FontWeight = FontWeights.SemiBold };
        advanced.Click += (_, _) =>
        {
            var window = new AdvancedColumnFilterWindow(label) { Owner = Window.GetWindow(grid) };
            if (window.ShowDialog() != true) return;
            if (window.ClearRequested) state.Filters.Remove(column);
            else state.Filters[column] = ColumnFilter.Advanced(path, window.FilterOperator, window.Value1, window.Value2);
            ApplyFilters(grid, state);
        };
        menu.Items.Add(advanced);
        var nonBlanksItem = new MenuItem { Header = "(Non-Blanks)" };
        nonBlanksItem.Click += (_, _) => { state.Filters[column] = ColumnFilter.NonBlank(path); ApplyFilters(grid, state); };
        menu.Items.Add(nonBlanksItem);
        menu.Items.Add(new Separator());

        var clearThis = new MenuItem { Header = $"All {label}", FontWeight = FontWeights.SemiBold };
        clearThis.Click += (_, _) =>
        {
            state.Filters.Remove(column);
            ApplyFilters(grid, state);
        };
        menu.Items.Add(clearThis);

        if (state.Filters.Count > 0)
        {
            var clearAll = new MenuItem { Header = "Clear all column filters" };
            clearAll.Click += (_, _) =>
            {
                state.Filters.Clear();
                ApplyFilters(grid, state);
            };
            menu.Items.Add(clearAll);
        }
        menu.Items.Add(new Separator());

        var nonBlank = rows.Where(row => !IsBlank(row.Value)).ToArray();
        if (rows.Any(row => IsBlank(row.Value)))
        {
            var blanks = new MenuItem { Header = "(Blanks)" };
            blanks.Click += (_, _) =>
            {
                state.Filters[column] = ColumnFilter.Blank(path);
                ApplyFilters(grid, state);
            };
            menu.Items.Add(blanks);
            if (nonBlank.Length > 0) menu.Items.Add(new Separator());
        }

        if (TryBuildDateRows(nonBlank, out var dateRows))
        {
            AddDateMenus(menu, grid, state, column, path, dateRows);
            return menu;
        }

        if (TryBuildNumericRows(nonBlank, out var numericRows))
        {
            foreach (var group in numericRows.GroupBy(item => item.Number).OrderBy(group => group.Key))
            {
                var sample = group.First().Raw;
                var item = new MenuItem
                {
                    Header = $"{FormatValue(sample)}  ({group.Count():N0})",
                    ToolTip = FormatValue(sample)
                };
                item.Click += (_, _) =>
                {
                    state.Filters[column] = ColumnFilter.Exact(path, sample);
                    ApplyFilters(grid, state);
                };
                menu.Items.Add(item);
            }
            return menu;
        }

        foreach (var group in nonBlank
                     .GroupBy(row => FormatValue(row.Value), StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var sample = group.First().Value;
            var full = group.Key;
            var display = full.Length > 110 ? full[..110] + "…" : full;
            var item = new MenuItem { Header = $"{display}  ({group.Count():N0})", ToolTip = full };
            item.Click += (_, _) =>
            {
                state.Filters[column] = ColumnFilter.Exact(path, sample);
                ApplyFilters(grid, state);
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    private static void AddDateMenus(
        ContextMenu menu,
        DataGrid grid,
        GridFilterState state,
        DataGridColumn column,
        string path,
        IReadOnlyList<DateValueRow> rows)
    {
        foreach (var yearGroup in rows.GroupBy(row => row.Date.Year).OrderByDescending(group => group.Key))
        {
            var yearMenu = new MenuItem { Header = $"{yearGroup.Key}  ({yearGroup.Count():N0})" };
            var allYear = new MenuItem { Header = $"All {yearGroup.Key}" };
            allYear.Click += (_, _) =>
            {
                var start = new DateTime(yearGroup.Key, 1, 1);
                state.Filters[column] = ColumnFilter.DateRange(path, start, start.AddYears(1));
                ApplyFilters(grid, state);
            };
            yearMenu.Items.Add(allYear);
            yearMenu.Items.Add(new Separator());

            foreach (var monthGroup in yearGroup.GroupBy(row => row.Date.Month).OrderBy(group => group.Key))
            {
                var monthName = CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(monthGroup.Key);
                var monthMenu = new MenuItem { Header = $"{monthName}  ({monthGroup.Count():N0})" };
                var allMonth = new MenuItem { Header = $"All {monthName} {yearGroup.Key}" };
                allMonth.Click += (_, _) =>
                {
                    var start = new DateTime(yearGroup.Key, monthGroup.Key, 1);
                    state.Filters[column] = ColumnFilter.DateRange(path, start, start.AddMonths(1));
                    ApplyFilters(grid, state);
                };
                monthMenu.Items.Add(allMonth);
                monthMenu.Items.Add(new Separator());

                foreach (var dateGroup in monthGroup.GroupBy(row => row.Date.Date).OrderBy(group => group.Key))
                {
                    var date = dateGroup.Key;
                    var dateItem = new MenuItem { Header = $"{date:dd.MM.yyyy}  ({dateGroup.Count():N0})" };
                    dateItem.Click += (_, _) =>
                    {
                        state.Filters[column] = ColumnFilter.DateRange(path, date, date.AddDays(1));
                        ApplyFilters(grid, state);
                    };
                    monthMenu.Items.Add(dateItem);
                }
                yearMenu.Items.Add(monthMenu);
            }
            menu.Items.Add(yearMenu);
        }
    }

    private static void ApplySort(GridFilterState state, string path, ListSortDirection direction)
    {
        if (state.View?.CanSort != true || string.IsNullOrWhiteSpace(path)) return;
        state.View.SortDescriptions.Clear();
        state.View.SortDescriptions.Add(new SortDescription(path, direction));
        state.View.Refresh();
    }

    private static void ApplyFilters(DataGrid grid, GridFilterState state)
    {
        BindView(grid, state);
        var view = state.View;
        if (view?.CanFilter != true) return;
        PersistFilters(grid, state);

        // Completion 61: capture only row references on the dispatcher, then perform the
        // expensive column/date/numeric/global-search evaluation on a worker. The WPF
        // ICollectionView receives an O(1) membership predicate when that result is ready.
        var rows = GetSourceItems(grid).ToArray();
        var filters = state.Filters.Values.ToArray();
        var terms = state.SearchTerms.ToArray();
        var aliases = state.FieldAliases.ToArray();
        var defaultDatePath = state.DefaultDatePath;
        var generation = Interlocked.Increment(ref state.FilterGeneration);

        var context = new GridFilterState { DefaultDatePath = defaultDatePath };
        context.SearchTerms.AddRange(terms);
        foreach (var pair in aliases) context.FieldAliases[pair.Key] = pair.Value;

        _ = Task.Run(() =>
        {
            var matched = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var source = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var item in rows)
            {
                source.Add(item);
                if (!filters.All(filter => filter.Matches(ReadPath(item, filter.Path)))) continue;
                if (!MatchesGlobalSearch(item, context)) continue;
                matched.Add(item);
            }
            return (Matched: matched, Source: source);
        }).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully || generation != Volatile.Read(ref state.FilterGeneration)) return;
            _ = grid.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != Volatile.Read(ref state.FilterGeneration) ||
                    !States.TryGetValue(grid, out var current) || !ReferenceEquals(current, state) ||
                    state.View?.CanFilter != true || !ReferenceEquals(state.View, view)) return;

                state.PreparedMatches = task.Result.Matched;
                state.PreparedSource = task.Result.Source;
                state.View.Filter = item =>
                {
                    if (state.OriginalFilter is not null && !state.OriginalFilter(item)) return false;
                    if (state.PreparedMatches?.Contains(item) == true) return true;
                    // A row added after the worker captured its snapshot is evaluated once
                    // until the next filter generation instead of being incorrectly hidden.
                    if (state.PreparedSource?.Contains(item) != true)
                    {
                        if (!filters.All(filter => filter.Matches(ReadPath(item, filter.Path)))) return false;
                        return MatchesGlobalSearch(item, context);
                    }
                    return false;
                };
                state.View.Refresh();
                QueueTotalsRefresh(grid);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private static void BindView(DataGrid grid, GridFilterState state)
    {
        var view = GetView(grid);
        if (view is null) return;
        if (ReferenceEquals(view, state.View))
        {
            // A cached inner tab is returning to the same live collection view. If no
            // collection/column shape changed while hidden, its totals and filter state
            // are already current and no rebuild is required.
            if (state.LastTotalsCollectionVersion != Volatile.Read(ref state.CollectionVersion) ||
                state.LastTotalsColumnCount != grid.Columns.Count)
                QueueTotalsRefresh(grid);
            return;
        }
        RestoreOriginalViewFilter(state);
        state.View = view;
        state.OriginalFilter = view.CanFilter ? view.Filter : null;
        state.CollectionVersion = 0;
        state.LastTotalsCollectionVersion = -1;
        state.LastTotalsColumnCount = -1;
        SubscribeViewCollectionChanged(grid, state, view);
        RestorePersistedFilters(grid, state);
        if ((state.Filters.Count > 0 || state.SearchTerms.Count > 0) && view.CanFilter) ApplyFilters(grid, state);
        else QueueTotalsRefresh(grid);
    }

    private static void SubscribeViewCollectionChanged(DataGrid grid, GridFilterState state, ICollectionView view)
    {
        UnsubscribeViewCollectionChanged(state);
        if (view is not INotifyCollectionChanged notify) return;
        NotifyCollectionChangedEventHandler handler = (_, _) =>
        {
            Interlocked.Increment(ref state.CollectionVersion);
            if (grid.IsLoaded) QueueTotalsRefresh(grid);
            else Interlocked.Increment(ref state.TotalsGeneration);
        };
        notify.CollectionChanged += handler;
        state.CollectionChangedSource = notify;
        state.CollectionChangedHandler = handler;
    }

    private static void UnsubscribeViewCollectionChanged(GridFilterState state)
    {
        if (state.CollectionChangedSource is not null && state.CollectionChangedHandler is not null)
            state.CollectionChangedSource.CollectionChanged -= state.CollectionChangedHandler;
        state.CollectionChangedSource = null;
        state.CollectionChangedHandler = null;
    }

    private static void UpdateHeaderTotals(DataGrid grid, GridFilterState state, int generation)
    {
        if (!grid.IsLoaded || state.View is null) return;
        var collectionVersion = Volatile.Read(ref state.CollectionVersion);
        if (!GetShowTotals(grid))
        {
            foreach (var column in grid.Columns) SetTotalText(column, string.Empty);
            state.LastTotalsCollectionVersion = collectionVersion;
            state.LastTotalsColumnCount = grid.Columns.Count;
            return;
        }

        // Capture only UI-owned column metadata and row references on the dispatcher.
        // Numeric reflection/summing runs on a worker, matching the existing background
        // filtering contract and keeping tab/module input responsive.
        var rows = state.View.Cast<object>().Where(IsRealItem).ToArray();
        var plans = grid.Columns.Select(column => new TotalColumnPlan(
            column,
            column.Visibility == Visibility.Visible ? GetTotalCandidatePaths(column) : Array.Empty<string>()))
            .ToArray();

        _ = Task.Run(() =>
        {
            var results = new string[plans.Length];
            for (var i = 0; i < plans.Length; i++)
            {
                var text = string.Empty;
                foreach (var path in plans[i].CandidatePaths)
                {
                    if (!IsAllowedPurchaseTotal(rows, path)) continue;
                    if (TrySumPurchaseInvoiceLevel(rows, path, out var total) ||
                        TrySumVisibleRows(rows, path, out total))
                    {
                        text = $"Σ {total:N2}";
                        break;
                    }
                }
                results[i] = text;
            }
            return results;
        }).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully ||
                generation != Volatile.Read(ref state.TotalsGeneration) ||
                collectionVersion != Volatile.Read(ref state.CollectionVersion)) return;
            _ = grid.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != Volatile.Read(ref state.TotalsGeneration) ||
                    collectionVersion != Volatile.Read(ref state.CollectionVersion) ||
                    !States.TryGetValue(grid, out var current) || !ReferenceEquals(current, state) ||
                    !grid.IsLoaded) return;
                for (var i = 0; i < plans.Length; i++) SetTotalText(plans[i].Column, task.Result[i]);
                state.LastTotalsCollectionVersion = collectionVersion;
                state.LastTotalsColumnCount = plans.Length;
            }), System.Windows.Threading.DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private static WeakReference<TabControl>? FindOwningTabControl(DependencyObject element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is TabControl tabs) return new WeakReference<TabControl>(tabs);
            var visualParent = current is Visual ? VisualTreeHelper.GetParent(current) : null;
            current = visualParent ?? LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private static string[] GetTotalCandidatePaths(DataGridColumn column)
    {
        var paths = new List<string>(3);
        static void Add(List<string> target, string? value)
        {
            var path = (value ?? string.Empty).Trim();
            if (path.Length > 0 && !target.Contains(path, StringComparer.OrdinalIgnoreCase)) target.Add(path);
        }

        Add(paths, GetTotalPath(column));
        if (column is DataGridBoundColumn bound && bound.Binding is Binding binding) Add(paths, binding.Path?.Path);
        Add(paths, column.SortMemberPath);
        return paths.ToArray();
    }

    private static bool TrySumDisplayedColumn(IReadOnlyCollection<object> rows, DataGridColumn column, out decimal total)
    {
        total = 0m;

        // Purchase grids declare their monetary total path explicitly on the same
        // DataGridColumn. This removes every display-index/header-order dependency:
        // reordering, hiding, freezing or horizontal scrolling can never move a total
        // under a neighbouring heading.
        var explicitPath = GetTotalPath(column).Trim();
        if (explicitPath.Length > 0)
        {
            return IsAllowedPurchaseTotal(rows, explicitPath) &&
                   (TrySumPurchaseInvoiceLevel(rows, explicitPath, out total) ||
                    TrySumVisibleRows(rows, explicitPath, out total));
        }

        // A total belongs to the value actually displayed by this column. SortMemberPath can
        // legitimately point at a different backing property, so never use it ahead of the
        // bound value. It is only a fallback for formatted display text (for example
        // OpeningBalanceText -> OpeningBalance) and template columns.
        if (column is DataGridBoundColumn bound && bound.Binding is Binding binding &&
            binding.Path?.Path is { Length: > 0 } boundPath)
        {
            var path = boundPath.Trim();
            if (IsAllowedPurchaseTotal(rows, path) &&
                (TrySumPurchaseInvoiceLevel(rows, path, out total) ||
                 TrySumVisibleRows(rows, path, out total)))
                return true;
        }

        var sortPath = column.SortMemberPath?.Trim() ?? string.Empty;
        if (sortPath.Length > 0 &&
            IsAllowedPurchaseTotal(rows, sortPath) &&
            (TrySumPurchaseInvoiceLevel(rows, sortPath, out total) ||
             TrySumVisibleRows(rows, sortPath, out total)))
            return true;

        total = 0m;
        return false;
    }

    private static readonly HashSet<string> PurchaseInvoiceLevelTotalPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(PurchaseImportRecord.SourceDocumentValue),
        nameof(PurchaseImportRecord.DocumentValue),
        nameof(PurchaseImportRecord.TwoBInvoiceValue),
        nameof(PurchaseImportRecord.TwoBTaxableValue),
        nameof(PurchaseImportRecord.TwoBIgst),
        nameof(PurchaseImportRecord.TwoBCgst),
        nameof(PurchaseImportRecord.TwoBSgst),
        nameof(PurchaseImportRecord.DifferenceInvoiceValue),
        nameof(PurchaseImportRecord.DifferenceTaxableValue),
        nameof(PurchaseImportRecord.DifferenceIgst),
        nameof(PurchaseImportRecord.DifferenceCgst),
        nameof(PurchaseImportRecord.DifferenceSgst)
    };

    private static readonly HashSet<string> PurchaseMonetaryTotalPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(PurchaseImportRecord.SourceDocumentValue),
        nameof(PurchaseImportRecord.DocumentValue),
        nameof(PurchaseImportRecord.TaxableValue),
        nameof(PurchaseImportRecord.Igst),
        nameof(PurchaseImportRecord.Cgst),
        nameof(PurchaseImportRecord.Sgst),
        nameof(PurchaseImportRecord.Cess),
        nameof(PurchaseImportRecord.RoundOff),
        nameof(PurchaseImportRecord.TwoBInvoiceValue),
        nameof(PurchaseImportRecord.TwoBTaxableValue),
        nameof(PurchaseImportRecord.TwoBIgst),
        nameof(PurchaseImportRecord.TwoBCgst),
        nameof(PurchaseImportRecord.TwoBSgst),
        nameof(PurchaseImportRecord.DifferenceInvoiceValue),
        nameof(PurchaseImportRecord.DifferenceTaxableValue),
        nameof(PurchaseImportRecord.DifferenceIgst),
        nameof(PurchaseImportRecord.DifferenceCgst),
        nameof(PurchaseImportRecord.DifferenceSgst)
    };

    private static bool IsPurchaseGridRows(IReadOnlyCollection<object> rows) =>
        rows.Count > 0 && rows.All(row => row is PurchaseImportRecord);

    private static bool IsAllowedPurchaseTotal(IReadOnlyCollection<object> rows, string path) =>
        !IsPurchaseGridRows(rows) || PurchaseMonetaryTotalPaths.Contains(path);

    private static bool TrySumPurchaseInvoiceLevel(
        IReadOnlyCollection<object> rows,
        string path,
        out decimal total)
    {
        total = 0m;
        if (!PurchaseInvoiceLevelTotalPaths.Contains(path) ||
            rows.Count == 0 ||
            rows.Any(row => row is not PurchaseImportRecord))
            return false;

        var any = false;
        foreach (var group in rows
                     .Cast<PurchaseImportRecord>()
                     .GroupBy(BuildPurchaseDocumentTotalKey, StringComparer.OrdinalIgnoreCase))
        {
            var value = ReadPath(group.First(), path);
            if (value is null) continue;
            try
            {
                if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
                {
                    total += Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    any = true;
                }
            }
            catch
            {
                total = 0m;
                return false;
            }
        }

        total = Math.Round(total, 2, MidpointRounding.AwayFromZero);
        return any;
    }

    private static string BuildPurchaseDocumentTotalKey(PurchaseImportRecord row)
    {
        var source = !string.IsNullOrWhiteSpace(row.Source2APath)
            ? row.Source2APath
            : row.Source2BPath;
        return string.Join(
            "\u001f",
            (source ?? string.Empty).Trim(),
            (row.SupplierGstin ?? string.Empty).Trim(),
            (row.DocumentType ?? string.Empty).Trim(),
            (row.DocumentNumber ?? string.Empty).Trim(),
            row.DocumentDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty,
            (row.OriginalDocumentNumber ?? string.Empty).Trim(),
            row.OriginalDocumentDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static bool MatchesGlobalSearch(object item, GridFilterState state)
    {
        if (state.SearchTerms.Count == 0) return true;
        return state.SearchTerms.All(term => MatchesSearchCommand(item, term, state));
    }

    private static bool MatchesSearchCommand(object item, string rawTerm, GridFilterState state)
    {
        var term = (rawTerm ?? string.Empty).Trim();
        if (term.Length == 0) return true;
        if (TryParseSortCommand(term, state, out _, out _)) return true;

        if (TryParseMonthRange(term, out var startMonth, out var endMonth) &&
            state.DefaultDatePath.Length > 0)
        {
            return MatchesMonthRange(ReadPath(item, state.DefaultDatePath), startMonth, endMonth);
        }

        if (TryParseNumericFieldCommand(term, state, out var numericPath, out var op, out var number))
            return TryReadDecimal(ReadPath(item, numericPath), out var actual) && CompareNumber(actual, op, number);

        if (TrySplitFieldQuery(term, state, out var fieldPath, out var query))
        {
            var value = ReadPath(item, fieldPath);
            if (TryParseMonthRange(query, out startMonth, out endMonth))
                return MatchesMonthRange(value, startMonth, endMonth);
            if (TryParseDateRange(query, out var startDate, out var endDate))
                return TryReadDate(value, out var actualDate) && actualDate.Date >= startDate.Date && actualDate.Date <= endDate.Date;
            if (TryParseQueryDate(query, out var queryDate))
                return TryReadDate(value, out var actualDate) && actualDate.Date == queryDate.Date;
            if (TryReadDecimalText(query, out var queryNumber) && TryReadDecimal(value, out var valueNumber))
                return valueNumber == queryNumber;
            return FormatValue(value).Contains(query, StringComparison.CurrentCultureIgnoreCase);
        }

        if (TryParseQueryDate(term, out var dateTerm) && state.DefaultDatePath.Length > 0)
            return TryReadDate(ReadPath(item, state.DefaultDatePath), out var rowDate) && rowDate.Date == dateTerm.Date;

        // Plain numbers must match numeric values, not only their comma-formatted display
        // text (for example 26000 must find 26,000.00). Plain text retains all-fields search.
        if (TryReadDecimalText(term, out var plainNumber))
        {
            foreach (var property in GetSearchableProperties(item.GetType()))
            {
                object? value;
                try { value = property.GetValue(item); }
                catch { continue; }
                if (TryReadDecimal(value, out var numberValue) && numberValue == plainNumber)
                    return true;
            }
        }

        var values = GetSearchablePropertyValues(item);
        return values.Any(value => value.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }

    private static void RebuildSearchFieldAliases(DataGrid grid, GridFilterState state)
    {
        state.FieldAliases.Clear();
        foreach (var column in grid.Columns)
        {
            var path = GetDisplayedPropertyPath(column);
            if (path.Length == 0) continue;
            AddSearchAlias(state, path, path);
            var header = column.Header?.ToString()?.Trim() ?? string.Empty;
            if (header.Length > 0) AddSearchAlias(state, header, path);
        }
    }

    private static void AddSearchAlias(GridFilterState state, string alias, string path)
    {
        var key = alias.Trim();
        if (key.Length == 0 || path.Length == 0) return;
        state.FieldAliases[key] = path;
        state.FieldAliases[NormalizeFieldName(key)] = path;
    }

    private static string GetDisplayedPropertyPath(DataGridColumn column)
    {
        if (column is DataGridBoundColumn { Binding: Binding binding } &&
            binding.Path?.Path is { Length: > 0 } path)
            return path.Trim();
        return (column.SortMemberPath ?? string.Empty).Trim();
    }

    private static string NormalizeFieldName(string value) =>
        Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), "[^a-z0-9]+", string.Empty);

    private static bool TryResolveField(GridFilterState state, string field, out string path)
    {
        field = (field ?? string.Empty).Trim();
        if (state.FieldAliases.TryGetValue(field, out var exactPath))
        {
            path = exactPath;
            return true;
        }

        var normalized = NormalizeFieldName(field);
        if (state.FieldAliases.TryGetValue(normalized, out var normalizedPath))
        {
            path = normalizedPath;
            return true;
        }

        path = string.Empty;
        return false;
    }

    private static bool TrySplitFieldQuery(string term, GridFilterState state, out string path, out string query)
    {
        foreach (var alias in state.FieldAliases.Keys
                     .Where(key => key.Any(char.IsLetter) && key.Contains(' '))
                     .OrderByDescending(key => key.Length))
        {
            if (!term.StartsWith(alias + " ", StringComparison.CurrentCultureIgnoreCase)) continue;
            if (!TryResolveField(state, alias, out path)) continue;
            query = term[(alias.Length + 1)..].Trim();
            return query.Length > 0;
        }

        // Also allow property-like/single-word headers: "supplier taluka", "cgst 1000".
        var firstSpace = term.IndexOf(' ');
        if (firstSpace > 0 && TryResolveField(state, term[..firstSpace], out path))
        {
            query = term[(firstSpace + 1)..].Trim();
            return query.Length > 0;
        }

        path = query = string.Empty;
        return false;
    }

    private static bool TryParseNumericFieldCommand(
        string term,
        GridFilterState state,
        out string path,
        out string op,
        out decimal number)
    {
        path = op = string.Empty;
        number = 0m;
        var match = Regex.Match(
            term,
            @"^(?<op>>=|<=|==|!=|<>|>|<|=|≥|≤)\s*(?<number>[-+]?\d[\d,]*(?:\.\d+)?)\s+(?:in|on)\s+(?<field>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success &&
            TryResolveField(state, match.Groups["field"].Value, out path) &&
            TryReadDecimalText(match.Groups["number"].Value, out number))
        {
            op = match.Groups["op"].Value;
            return true;
        }

        match = Regex.Match(
            term,
            @"^(?<field>.+?)\s*(?<op>>=|<=|==|!=|<>|>|<|=|≥|≤)\s*(?<number>[-+]?\d[\d,]*(?:\.\d+)?)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success ||
            !TryResolveField(state, match.Groups["field"].Value, out path) ||
            !TryReadDecimalText(match.Groups["number"].Value, out number))
            return false;
        op = match.Groups["op"].Value;
        return true;
    }

    private static bool CompareNumber(decimal actual, string op, decimal expected) => op switch
    {
        ">" => actual > expected,
        ">=" or "≥" => actual >= expected,
        "<" => actual < expected,
        "<=" or "≤" => actual <= expected,
        "!=" or "<>" => actual != expected,
        _ => actual == expected
    };

    private static bool TryReadDecimal(object? value, out decimal number)
    {
        number = 0m;
        if (value is null) return false;
        try
        {
            if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            {
                number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch { return false; }
        return TryReadDecimalText(Convert.ToString(value, CultureInfo.CurrentCulture), out number);
    }

    private static bool TryReadDecimalText(string? value, out decimal number)
    {
        var text = (value ?? string.Empty).Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number) ||
               decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out number);
    }

    private static bool TryReadDate(object? value, out DateTime date)
    {
        if (value is DateTime direct) { date = direct; return true; }
        if (value is DateTimeOffset offset) { date = offset.DateTime; return true; }
        return TryParseQueryDate(Convert.ToString(value, CultureInfo.CurrentCulture), out date);
    }

    private static bool TryParseQueryDate(string? text, out DateTime date)
    {
        var formats = new[]
        {
            "dd.MM.yyyy", "dd-MM-yyyy", "dd/MM/yyyy", "yyyy-MM-dd",
            "d.M.yyyy", "d-M-yyyy", "d/M/yyyy", "dd-MMM-yyyy", "d-MMM-yyyy"
        };
        return DateTime.TryParseExact((text ?? string.Empty).Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date) ||
               DateTime.TryParseExact((text ?? string.Empty).Trim(), formats, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static bool TryParseDateRange(string text, out DateTime start, out DateTime end)
    {
        start = end = default;
        var parts = Regex.Split(text.Trim(), @"\s+to\s+", RegexOptions.IgnoreCase);
        return parts.Length == 2 && TryParseQueryDate(parts[0], out start) && TryParseQueryDate(parts[1], out end);
    }

    private static readonly Dictionary<string, int> SearchMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["january"] = 1,
        ["feb"] = 2, ["february"] = 2,
        ["mar"] = 3, ["march"] = 3,
        ["apr"] = 4, ["april"] = 4,
        ["may"] = 5,
        ["jun"] = 6, ["june"] = 6,
        ["jul"] = 7, ["july"] = 7,
        ["aug"] = 8, ["august"] = 8,
        ["sep"] = 9, ["sept"] = 9, ["september"] = 9,
        ["oct"] = 10, ["october"] = 10,
        ["nov"] = 11, ["november"] = 11,
        ["dec"] = 12, ["december"] = 12
    };

    private static bool TryParseMonthRange(string text, out int startMonth, out int endMonth)
    {
        startMonth = endMonth = 0;
        var parts = Regex.Split((text ?? string.Empty).Trim(), @"\s+to\s+", RegexOptions.IgnoreCase);
        if (parts.Length == 1 && SearchMonths.TryGetValue(parts[0].Trim(), out startMonth))
        {
            endMonth = startMonth;
            return true;
        }
        if (parts.Length == 2 &&
            SearchMonths.TryGetValue(parts[0].Trim(), out startMonth) &&
            SearchMonths.TryGetValue(parts[1].Trim(), out endMonth))
            return true;
        return false;
    }

    private static bool MatchesMonthRange(object? value, int startMonth, int endMonth)
    {
        if (!TryReadDate(value, out var date)) return false;
        var order = new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 1, 2, 3 };
        var start = Array.IndexOf(order, startMonth);
        var end = Array.IndexOf(order, endMonth);
        var actual = Array.IndexOf(order, date.Month);
        if (start < 0 || end < 0 || actual < 0) return false;
        return start <= end
            ? actual >= start && actual <= end
            : actual >= start || actual <= end;
    }

    private static bool TryParseSortCommand(
        string term,
        GridFilterState state,
        out string path,
        out ListSortDirection direction)
    {
        path = string.Empty;
        direction = ListSortDirection.Ascending;
        var match = Regex.Match(
            term,
            @"^sort\s+(?<field>.+?)\s+(?<direction>ascending|asc|up|↑|descending|desc|down|↓)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !TryResolveField(state, match.Groups["field"].Value, out path)) return false;
        var value = match.Groups["direction"].Value;
        direction = value.Equals("descending", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("desc", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("down", StringComparison.OrdinalIgnoreCase) ||
                    value == "↓"
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        return true;
    }

    private static void ApplySearchSortCommands(GridFilterState state)
    {
        foreach (var term in state.SearchTerms)
        {
            if (TryParseSortCommand(term, state, out var path, out var direction))
                ApplySort(state, path, direction);
        }
    }

    private static readonly Dictionary<Type, PropertyInfo[]> SearchableProperties = new();
    private static readonly object SearchablePropertiesLock = new();
    private static readonly ConditionalWeakTable<object, SearchProjection> SearchProjectionCache = new();

    /// <summary>
    /// Completion 61 cache-side filter preparation. Module workers call this while rows are
    /// already on a background thread, so the first search does not perform reflection and
    /// string formatting for every row on WPF's dispatcher. The weak table follows row
    /// lifetime and therefore cannot keep old client caches alive.
    /// </summary>
    public static void PrewarmSearchProjection(IEnumerable<object>? rows)
    {
        if (rows is null) return;
        foreach (var row in rows)
        {
            if (row is null) continue;
            _ = GetSearchProjection(row);
        }
    }

    public static void InvalidateSearchProjection(object? row)
    {
        if (row is not null) SearchProjectionCache.Remove(row);
    }

    private static PropertyInfo[] GetSearchableProperties(Type type)
    {
        lock (SearchablePropertiesLock)
        {
            if (SearchableProperties.TryGetValue(type, out var cached)) return cached;
            cached = type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                .Where(property => IsSearchablePropertyType(property.PropertyType))
                .ToArray();
            SearchableProperties[type] = cached;
            return cached;
        }
    }

    private static IReadOnlyList<string> GetSearchablePropertyValues(object item) =>
        GetSearchProjection(item).Values;

    private static SearchProjection GetSearchProjection(object item) =>
        SearchProjectionCache.GetValue(item, static row =>
        {
            var properties = GetSearchableProperties(row.GetType());
            var result = new List<string>(properties.Length);
            foreach (var property in properties)
            {
                object? value;
                try { value = property.GetValue(row); }
                catch { continue; }
                var formatted = FormatValue(value);
                if (formatted.Length > 0) result.Add(formatted);
            }
            return new SearchProjection(result.ToArray());
        });

    private static bool IsSearchablePropertyType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(string) ||
               type == typeof(DateTime) ||
               type == typeof(DateTimeOffset) ||
               type == typeof(bool) ||
               type.IsEnum ||
               type == typeof(byte) || type == typeof(sbyte) ||
               type == typeof(short) || type == typeof(ushort) ||
               type == typeof(int) || type == typeof(uint) ||
               type == typeof(long) || type == typeof(ulong) ||
               type == typeof(float) || type == typeof(double) || type == typeof(decimal);
    }

    private static bool TrySumVisibleRows(IReadOnlyCollection<object> rows, string path, out decimal total)
    {
        total = 0m;
        if (rows.Count == 0 || !IsSummablePath(path)) return false;
        var any = false;
        foreach (var row in rows)
        {
            var value = ReadPath(row, path);
            if (value is null) continue;
            try
            {
                if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
                {
                    total += Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    any = true;
                }
            }
            catch
            {
                return false;
            }
        }
        total = Math.Round(total, 2, MidpointRounding.AwayFromZero);
        return any;
    }

    private static bool IsSummablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var leaf = path.Split('.').Last();
        var excluded = new[] { "Id", "PersistentId", "BatchId", "StatementId", "AccountId", "SourceRow", "Sequence", "LineNumber", "Rate", "Percent", "Percentage", "Days", "Age", "DecimalPlaces", "ConversionFactor", "RunningBalance" };
        return !excluded.Any(token => leaf.Equals(token, StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(token, StringComparison.OrdinalIgnoreCase));
    }

    private static void PersistFilters(DataGrid grid, GridFilterState state)
    {
        var source = GetPersistenceSource(grid);
        if (source is null) return;
        var persisted = PersistedStates.GetValue(source, _ => new PersistedGridFilterState());
        persisted.Filters.Clear();
        foreach (var filter in state.Filters.Values)
            persisted.Filters[filter.Path] = filter;
    }

    private static void RestorePersistedFilters(DataGrid grid, GridFilterState state)
    {
        if (state.Filters.Count > 0) return;
        var source = GetPersistenceSource(grid);
        if (source is null || !PersistedStates.TryGetValue(source, out var persisted) || persisted.Filters.Count == 0)
            return;
        foreach (var column in grid.Columns)
        {
            var path = GetPropertyPath(column);
            if (path.Length > 0 && persisted.Filters.TryGetValue(path, out var filter))
                state.Filters[column] = filter;
        }
    }

    private static object? GetPersistenceSource(DataGrid grid)
    {
        if (grid.ItemsSource is ICollectionView collectionView && collectionView.SourceCollection is object source)
            return source;
        return grid.ItemsSource;
    }

    private static ICollectionView? GetView(DataGrid grid)
    {
        if (grid.ItemsSource is null) return null;
        return CollectionViewSource.GetDefaultView(grid.ItemsSource);
    }

    private static void RestoreOriginalViewFilter(GridFilterState state)
    {
        UnsubscribeViewCollectionChanged(state);
        if (state.View?.CanFilter == true)
        {
            try
            {
                state.View.Filter = state.OriginalFilter;
                state.View.Refresh();
            }
            catch
            {
                // A disposed/replaced view should never block navigation.
            }
        }
        state.View = null;
        state.OriginalFilter = null;
    }

    private static IEnumerable<object> GetSourceItems(DataGrid grid)
    {
        if (grid.ItemsSource is ICollectionView view && view.SourceCollection is IEnumerable source)
            return source.Cast<object>().Where(IsRealItem).ToArray();
        if (grid.ItemsSource is IEnumerable enumerable)
            return enumerable.Cast<object>().Where(IsRealItem).ToArray();
        return [];
    }

    private static bool IsRealItem(object item) =>
        item is not CollectionViewGroup && !ReferenceEquals(item, CollectionView.NewItemPlaceholder);

    private static string GetPropertyPath(DataGridColumn? column)
    {
        if (column is null) return string.Empty;
        if (!string.IsNullOrWhiteSpace(column.SortMemberPath)) return column.SortMemberPath.Trim();
        if (column is DataGridBoundColumn bound && bound.Binding is Binding binding && binding.Path?.Path is { Length: > 0 } path)
            return path.Trim();
        return string.Empty;
    }

    private static object? ReadPath(object? source, string path)
    {
        if (source is null || path.Length == 0) return source;
        object? value = source;
        foreach (var segment in path.Split('.'))
        {
            if (value is null) return null;
            var property = value.GetType().GetProperty(segment, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property is null) return null;
            value = property.GetValue(value);
        }
        return value;
    }

    private static bool TryBuildDateRows(IReadOnlyList<ValueRow> rows, out IReadOnlyList<DateValueRow> dates)
    {
        var result = new List<DateValueRow>(rows.Count);
        foreach (var row in rows)
        {
            if (!TryConvertDate(row.Value, out var date))
            {
                dates = [];
                return false;
            }
            result.Add(new DateValueRow(row.Value, date));
        }
        dates = result;
        return result.Count > 0;
    }

    private static bool TryConvertDate(object? value, out DateTime date)
    {
        if (value is DateTime dt) { date = dt; return true; }
        if (value is DateTimeOffset dto) { date = dto.DateTime; return true; }
        if (value is string text)
        {
            var trimmed = text.Trim();
            var formats = new[] { "dd.MM.yyyy", "dd-MM-yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "dd-MMM-yyyy", "d.M.yyyy", "d-M-yyyy", "d/M/yyyy" };
            if (DateTime.TryParseExact(trimmed, formats, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date) ||
                DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date))
                return true;
        }
        date = default;
        return false;
    }

    private static bool TryBuildNumericRows(IReadOnlyList<ValueRow> rows, out IReadOnlyList<NumericValueRow> numbers)
    {
        var result = new List<NumericValueRow>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Value is null)
            {
                numbers = [];
                return false;
            }
            try
            {
                if (row.Value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
                    result.Add(new NumericValueRow(row.Value, Convert.ToDecimal(row.Value, CultureInfo.InvariantCulture)));
                else
                {
                    numbers = [];
                    return false;
                }
            }
            catch
            {
                numbers = [];
                return false;
            }
        }
        numbers = result;
        return result.Count > 0;
    }

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        DateTime date => date.ToString("dd.MM.yyyy"),
        DateTimeOffset date => date.ToString("dd.MM.yyyy"),
        decimal number => number.ToString("N2"),
        double number => number.ToString("N2"),
        float number => number.ToString("N2"),
        byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToDecimal(value).ToString("N0"),
        bool flag => flag ? "Yes" : "No",
        _ => Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty
    };

    private static bool IsBlank(object? value) => value is null || (value is string text && string.IsNullOrWhiteSpace(text));

    private static Button? FindFilterButton(DependencyObject header)
    {
        if (header is Button button) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(header); i++)
        {
            var child = VisualTreeHelper.GetChild(header, i);
            if (child is Button found) return found;
            var nested = FindFilterButton(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private sealed class SearchProjection
    {
        public SearchProjection(string[] values) => Values = values;
        public IReadOnlyList<string> Values { get; }
    }

    private sealed class PersistedGridFilterState
    {
        public Dictionary<string, ColumnFilter> Filters { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class GridFilterState
    {
        public Dictionary<DataGridColumn, ColumnFilter> Filters { get; } = new();
        public List<string> SearchTerms { get; } = [];
        public Dictionary<string, string> FieldAliases { get; } = new(StringComparer.CurrentCultureIgnoreCase);
        public string DefaultDatePath { get; set; } = string.Empty;
        public ICollectionView? View { get; set; }
        public Predicate<object>? OriginalFilter { get; set; }
        public CommandBinding? CommandBinding { get; set; }
        public INotifyCollectionChanged? CollectionChangedSource { get; set; }
        public NotifyCollectionChangedEventHandler? CollectionChangedHandler { get; set; }
        public int FilterGeneration;
        public int TotalsGeneration;
        public int CollectionVersion;
        public int LastTotalsCollectionVersion = -1;
        public int LastTotalsColumnCount = -1;
        public WeakReference<TabControl>? OwningTabControl { get; set; }
        public HashSet<object>? PreparedMatches { get; set; }
        public HashSet<object>? PreparedSource { get; set; }
    }

    private readonly record struct ValueRow(object Item, object? Value);
    private readonly record struct DateValueRow(object? Raw, DateTime Date);
    private readonly record struct NumericValueRow(object Raw, decimal Number);
    private readonly record struct TotalColumnPlan(DataGridColumn Column, IReadOnlyList<string> CandidatePaths);

    private sealed record ColumnFilter(string Path, FilterKind Kind, object? ExactValue, DateTime? Start, DateTime? End, string Operator = "", string Query1 = "", string Query2 = "", IReadOnlySet<string>? SelectedValues = null)
    {
        public static ColumnFilter Blank(string path) => new(path, FilterKind.Blank, null, null, null);
        public static ColumnFilter NonBlank(string path) => new(path, FilterKind.NonBlank, null, null, null);
        public static ColumnFilter Exact(string path, object? value) => new(path, FilterKind.Exact, value, null, null);
        public static ColumnFilter DateRange(string path, DateTime start, DateTime end) => new(path, FilterKind.DateRange, null, start, end);
        public static ColumnFilter Advanced(string path, string op, string value1, string value2) => new(path, FilterKind.Advanced, null, null, null, op, value1, value2);
        public static ColumnFilter Multi(string path, IEnumerable<string> values) => new(path, FilterKind.Multi, null, null, null, SelectedValues: values.ToHashSet(StringComparer.CurrentCultureIgnoreCase));

        public bool Matches(object? value)
        {
            return Kind switch
            {
                FilterKind.Blank => IsBlank(value),
                FilterKind.NonBlank => !IsBlank(value),
                FilterKind.DateRange => TryDate(value, out var date) && date >= Start!.Value && date < End!.Value,
                FilterKind.Advanced => AdvancedMatches(value),
                FilterKind.Multi => SelectedValues?.Contains(IsBlank(value) ? "(Blanks)" : FormatValue(value)) == true,
                _ => ValuesEqual(value, ExactValue)
            };
        }

        private bool AdvancedMatches(object? value)
        {
            var op = Operator.Trim().ToUpperInvariant();
            var text = Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
            if (op == "CONTAINS") return text.Contains(Query1, StringComparison.CurrentCultureIgnoreCase);
            if (op == "DOES NOT CONTAIN") return !text.Contains(Query1, StringComparison.CurrentCultureIgnoreCase);
            if (op == "STARTS WITH") return text.StartsWith(Query1, StringComparison.CurrentCultureIgnoreCase);
            if (op == "ENDS WITH") return text.EndsWith(Query1, StringComparison.CurrentCultureIgnoreCase);

            DateTime secondDate = default;
            if (TryDate(value, out var valueDate) && TryQueryDate(Query1, out var firstDate) &&
                (!op.Equals("BETWEEN", StringComparison.OrdinalIgnoreCase) || TryQueryDate(Query2, out secondDate)))
            {
                var compareDate = valueDate.Date.CompareTo(firstDate.Date);
                return op switch
                {
                    "EQUALS" => compareDate == 0,
                    "NOT EQUAL" => compareDate != 0,
                    "GREATER THAN" => compareDate > 0,
                    "GREATER OR EQUAL" => compareDate >= 0,
                    "LESS THAN" => compareDate < 0,
                    "LESS OR EQUAL" => compareDate <= 0,
                    "BETWEEN" => compareDate >= 0 && valueDate.Date <= secondDate.Date,
                    _ => false
                };
            }

            decimal q2 = 0m;
            if (TryComparable(value, Query1, out var first, out var q1) &&
                (!op.Equals("BETWEEN", StringComparison.OrdinalIgnoreCase) || TryComparable(value, Query2, out _, out q2)))
            {
                var compare1 = first.CompareTo(q1);
                return op switch
                {
                    "EQUALS" => compare1 == 0,
                    "NOT EQUAL" => compare1 != 0,
                    "GREATER THAN" => compare1 > 0,
                    "GREATER OR EQUAL" => compare1 >= 0,
                    "LESS THAN" => compare1 < 0,
                    "LESS OR EQUAL" => compare1 <= 0,
                    "BETWEEN" => compare1 >= 0 && first.CompareTo(q2) <= 0,
                    _ => false
                };
            }

            var compareText = string.Compare(text, Query1, StringComparison.CurrentCultureIgnoreCase);
            return op switch
            {
                "EQUALS" => compareText == 0,
                "NOT EQUAL" => compareText != 0,
                "GREATER THAN" => compareText > 0,
                "GREATER OR EQUAL" => compareText >= 0,
                "LESS THAN" => compareText < 0,
                "LESS OR EQUAL" => compareText <= 0,
                "BETWEEN" => compareText >= 0 && string.Compare(text, Query2, StringComparison.CurrentCultureIgnoreCase) <= 0,
                _ => false
            };
        }

        private static bool TryQueryDate(string query, out DateTime date)
        {
            var formats = new[] { "dd.MM.yyyy", "dd-MM-yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "dd-MMM-yyyy", "d.M.yyyy", "d-M-yyyy", "d/M/yyyy" };
            return DateTime.TryParseExact(query?.Trim(), formats, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date) ||
                   DateTime.TryParseExact(query?.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date) ||
                   DateTime.TryParse(query, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date);
        }

        private static bool TryComparable(object? value, string query, out decimal valueNumber, out decimal queryNumber)
        {
            valueNumber = queryNumber = 0m;
            try
            {
                if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
                    valueNumber = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                else if (!decimal.TryParse((Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out valueNumber))
                    return false;
                return decimal.TryParse((query ?? string.Empty).Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out queryNumber) ||
                       decimal.TryParse(query, NumberStyles.Number, CultureInfo.CurrentCulture, out queryNumber);
            }
            catch { return false; }
        }

        private static bool ValuesEqual(object? first, object? second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first is null || second is null) return false;
            if (first is string || second is string)
                return string.Equals(Convert.ToString(first)?.Trim(), Convert.ToString(second)?.Trim(), StringComparison.CurrentCultureIgnoreCase);
            if (first is DateTime firstDate && second is DateTime secondDate) return firstDate == secondDate;
            return first.Equals(second);
        }

        private static bool TryDate(object? value, out DateTime date) => DataGridColumnFilterBehavior.TryConvertDate(value, out date);
    }

    private enum FilterKind
    {
        Exact,
        Blank,
        NonBlank,
        DateRange,
        Advanced,
        Multi
    }
}
