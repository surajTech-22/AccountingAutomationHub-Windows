using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AccountingAutomationHub.Desktop.Controls;

public interface ISuggestionTextItem
{
    string SuggestionDisplay { get; }
    string SuggestionValue { get; }
}

/// <summary>
/// Excel-style text editor with lightweight suggestions. Unlike ComboBox it is a
/// real TextBox, so WPF never replaces typed characters from SelectedItem state.
/// </summary>
public sealed class SuggestTextBox : TextBox
{
    public static readonly RoutedEvent EscapePressedEvent = EventManager.RegisterRoutedEvent(
        nameof(EscapePressed), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SuggestTextBox));

    public event RoutedEventHandler EscapePressed
    {
        add => AddHandler(EscapePressedEvent, value);
        remove => RemoveHandler(EscapePressedEvent, value);
    }

    public static readonly RoutedEvent SuggestionChosenEvent = EventManager.RegisterRoutedEvent(
        nameof(SuggestionChosen), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SuggestTextBox));

    public event RoutedEventHandler SuggestionChosen
    {
        add => AddHandler(SuggestionChosenEvent, value);
        remove => RemoveHandler(SuggestionChosenEvent, value);
    }

    public static readonly DependencyProperty SourceItemsProperty = DependencyProperty.Register(
        nameof(SourceItems), typeof(IEnumerable), typeof(SuggestTextBox),
        new PropertyMetadata(null, OnSourceItemsChanged));

    private readonly Popup _popup;
    private readonly ListBox _list;
    private INotifyCollectionChanged? _observedCollection;
    private bool _choosing;
    private bool _explicitEditorActive;
    private bool _sourceRefreshPending;

    public SuggestTextBox()
    {
        _list = new ListBox
        {
            MaxHeight = 320,
            Background = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 180
        };
        VirtualizingStackPanel.SetIsVirtualizing(_list, true);
        VirtualizingStackPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_list, true);
        var border = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(201, 212, 226)),
            BorderThickness = new Thickness(1),
            Child = _list,
            SnapsToDevicePixels = true
        };

        _popup = new Popup
        {
            Placement = PlacementMode.Bottom,
            PlacementTarget = this,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = border
        };

        _list.PreviewMouseLeftButtonDown += Suggestion_MouseLeftButtonDown;
        _list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _popup.IsOpen = false;
            RaiseEvent(new RoutedEventArgs(EscapePressedEvent, this));
            e.Handled = true;
        };

        TextChanged += (_, _) => RefreshSuggestions();
        // Opening a selector must behave like a real dropdown: show the complete
        // Master list first even when the cell already contains a value. Typing then
        // narrows the list through TextChanged.
        GotKeyboardFocus += (_, _) => RefreshSuggestions(showAllSuggestions: true);
        LostKeyboardFocus += (_, e) =>
        {
            // The popup lives in a separate WPF visual tree. Clicking a ListBoxItem
            // can move keyboard focus out of the TextBox before MouseUp. Closing here
            // used to destroy the popup before the clicked ledger/group could be
            // selected. Keep it alive while focus is moving inside our suggestion list.
            if (!_choosing && !IsSuggestionListFocus(e.NewFocus)) _popup.IsOpen = false;
        };
        SizeChanged += (_, _) => _list.MinWidth = Math.Max(180, ActualWidth);
        Loaded += (_, _) => ObserveSourceCollection(SourceItems as INotifyCollectionChanged);
        Unloaded += (_, _) => ObserveSourceCollection(null);
    }

    public bool IsExplicitEditorActive => _explicitEditorActive;

    public void ActivateEditor(bool selectAll = false, bool showAllSuggestions = false)
    {
        _explicitEditorActive = true;
        IsReadOnly = false;
        Focusable = true;
        Focus();
        Keyboard.Focus(this);
        if (selectAll)
            SelectAll();
        else
        {
            CaretIndex = Text?.Length ?? 0;
            SelectionLength = 0;
        }
        RefreshSuggestions(showAllSuggestions);
    }

    public void ShowAllSuggestions() => RefreshSuggestions(showAllSuggestions: true);

    public void DeactivateEditor()
    {
        _popup.IsOpen = false;
        _explicitEditorActive = false;
        IsReadOnly = true;
        Focusable = false;
    }

    public IEnumerable? SourceItems
    {
        get => (IEnumerable?)GetValue(SourceItemsProperty);
        set => SetValue(SourceItemsProperty, value);
    }

    private static void OnSourceItemsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not SuggestTextBox editor) return;
        editor.ObserveSourceCollection(args.NewValue as INotifyCollectionChanged);
        editor.RefreshSuggestions();
    }

    private void ObserveSourceCollection(INotifyCollectionChanged? collection)
    {
        if (ReferenceEquals(_observedCollection, collection)) return;
        if (_observedCollection is not null)
            _observedCollection.CollectionChanged -= SourceCollectionChanged;
        _observedCollection = collection;
        if (_observedCollection is not null)
            _observedCollection.CollectionChanged += SourceCollectionChanged;
    }

    private void SourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Tally Master hydration can replace hundreds of suggestion values at once. Coalesce
        // that collection burst into one background refresh, and do nothing at all for an
        // inactive editor. This prevents a dropdown from repeatedly closing/reopening while
        // the user is selecting a Final Ledger or Ledger Group.
        if (!IsKeyboardFocusWithin) return;
        if (_sourceRefreshPending) return;
        _sourceRefreshPending = true;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            _sourceRefreshPending = false;
            if (IsKeyboardFocusWithin && !_choosing) RefreshSuggestions();
        }));
    }

    public void BeginTyping(string firstText)
    {
        ActivateEditor();
        Text = firstText ?? string.Empty;
        CaretIndex = Text.Length;
        SelectionLength = 0;
        // Publish the first character immediately.  Leaving the bound row blank until
        // LostFocus lets DataGrid refresh reapply the old source value when the second
        // character arrives, which is the first-letter-loss bug users observed.
        GetBindingExpression(TextProperty)?.UpdateSource();
        RefreshSuggestions();
    }

    public void CloseSuggestions() => _popup.IsOpen = false;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _popup.IsOpen = false;
            RaiseEvent(new RoutedEventArgs(EscapePressedEvent, this));
            e.Handled = true;
            return;
        }

        if (_popup.IsOpen && _list.Items.Count > 0)
        {
            if (e.Key == Key.Down)
            {
                _list.SelectedIndex = Math.Min(_list.Items.Count - 1, Math.Max(0, _list.SelectedIndex + 1));
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Up)
            {
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex < 0 ? 0 : _list.SelectedIndex - 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                CommitHighlightedSuggestion();
                // Let Enter/Tab continue to the DataGrid so it can commit/move.
            }
        }

        base.OnPreviewKeyDown(e);
    }

    private void RefreshSuggestions(bool showAllSuggestions = false)
    {
        // Choosing updates Text and the bound row synchronously. Do not let those
        // TextChanged/PropertyChanged notifications reopen the popup after a user
        // has just selected an item.
        if (_choosing)
        {
            _popup.IsOpen = false;
            return;
        }

        if (!IsKeyboardFocusWithin || SourceItems is null)
        {
            _popup.IsOpen = false;
            return;
        }

        var query = showAllSuggestions ? string.Empty : Text.Trim();
        var values = SourceItems.Cast<object?>()
            .Select(item => item switch
            {
                ISuggestionTextItem rich => new SuggestionChoice(rich.SuggestionDisplay.Trim(), rich.SuggestionValue.Trim()),
                _ => new SuggestionChoice(item?.ToString()?.Trim() ?? string.Empty, item?.ToString()?.Trim() ?? string.Empty)
            })
            .Where(item => item.Display.Length > 0 && item.Value.Length > 0)
            .GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(item => query.Length == 0 ||
                           item.Display.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           item.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => query.Length > 0 && item.Value.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _list.ItemsSource = values;
        _list.SelectedIndex = -1;
        _list.MinWidth = Math.Max(180, ActualWidth);
        _popup.IsOpen = values.Count > 0;
    }

    private void Suggestion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Choose on MouseDown, before WPF transfers focus from the TextBox into the
        // popup. Waiting for MouseUp allowed LostKeyboardFocus/DataGrid commit logic
        // to close the popup first, making the dropdown look selectable but ignore
        // the click. Resolve the exact ListBoxItem under the pointer so selection does
        // not depend on ListBox's later default mouse processing.
        var source = e.OriginalSource as DependencyObject;
        var container = source is null ? null : ItemsControl.ContainerFromElement(_list, source) as ListBoxItem;
        if (container?.Content is not SuggestionChoice selected) return;

        _list.SelectedItem = selected;
        Choose(selected.Value);
        e.Handled = true;
    }

    public bool CommitHighlightedSuggestion()
    {
        if (!_popup.IsOpen || _list.SelectedItem is not SuggestionChoice selected) return false;
        Choose(selected.Value);
        return true;
    }

    public bool IsSuggestionListFocus(IInputElement? element)
    {
        if (element is not DependencyObject current) return false;
        while (current is not null)
        {
            if (ReferenceEquals(current, _list)) return true;
            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private void Choose(string value)
    {
        _choosing = true;
        try
        {
            Text = value;
            CaretIndex = Text.Length;
            SelectionLength = 0;
            GetBindingExpression(TextProperty)?.UpdateSource();
            _popup.IsOpen = false;
            RaiseEvent(new RoutedEventArgs(SuggestionChosenEvent, this));
            Focus();
        }
        finally
        {
            _choosing = false;
        }
    }

    private sealed record SuggestionChoice(string Display, string Value)
    {
        public override string ToString() => Display;
    }
}
