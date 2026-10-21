using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace AccountingAutomationHub.Desktop.Controls;

/// <summary>
/// Searchable selector used by Banking filters and Master selectors.
/// It preserves the source collection, supports free text when requested,
/// and provides mouse-wheel, arrow, Page Up/Page Down, Home and End navigation.
/// </summary>
public class SearchableComboBox : ComboBox
{
    public static readonly DependencyProperty SourceItemsProperty = DependencyProperty.Register(
        nameof(SourceItems),
        typeof(IEnumerable),
        typeof(SearchableComboBox),
        new PropertyMetadata(null, OnSourceItemsChanged));

    public static readonly DependencyProperty AllowCustomTextProperty = DependencyProperty.Register(
        nameof(AllowCustomText),
        typeof(bool),
        typeof(SearchableComboBox),
        new PropertyMetadata(true));

    public static readonly DependencyProperty EnableMouseWheelSelectionProperty = DependencyProperty.Register(
        nameof(EnableMouseWheelSelection),
        typeof(bool),
        typeof(SearchableComboBox),
        new PropertyMetadata(true));

    public static readonly DependencyProperty HandleEnterKeyProperty = DependencyProperty.Register(
        nameof(HandleEnterKey),
        typeof(bool),
        typeof(SearchableComboBox),
        new PropertyMetadata(true));

    public static readonly DependencyProperty OpenOnFocusProperty = DependencyProperty.Register(
        nameof(OpenOnFocus),
        typeof(bool),
        typeof(SearchableComboBox),
        new PropertyMetadata(false));

    public static readonly DependencyProperty SelectFirstVisibleOnNoMatchProperty = DependencyProperty.Register(
        nameof(SelectFirstVisibleOnNoMatch),
        typeof(bool),
        typeof(SearchableComboBox),
        new PropertyMetadata(true));

    public static readonly DependencyProperty UpdateSourceWhileTypingProperty = DependencyProperty.Register(
        nameof(UpdateSourceWhileTyping),
        typeof(bool),
        typeof(SearchableComboBox),
        new PropertyMetadata(true));

    private readonly ObservableCollection<object> _items = [];
    private readonly ICollectionView _view;
    private INotifyCollectionChanged? _observedCollection;
    private TextBox? _editableTextBox;
    private bool _selectionTextUpdate;
    private bool _filteringFromEditor;
    private bool _sourceReloadInProgress;
    private DispatcherOperation? _pendingReload;
    private bool _suppressNextFocusOpen;
    private bool _popupSelectionCommitInProgress;
    private string _lastCommittedText = string.Empty;

    public SearchableComboBox()
    {
        IsEditable = true;
        IsTextSearchEnabled = false;
        StaysOpenOnEdit = true;
        MaxDropDownHeight = 360;
        ScrollViewer.SetCanContentScroll(this, true);
        _view = CollectionViewSource.GetDefaultView(_items);
        ItemsSource = _view;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SelectionChanged += OnSelectionChanged;
        AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        LostKeyboardFocus += OnLostKeyboardFocus;
    }

    public IEnumerable? SourceItems
    {
        get => (IEnumerable?)GetValue(SourceItemsProperty);
        set => SetValue(SourceItemsProperty, value);
    }

    public bool AllowCustomText
    {
        get => (bool)GetValue(AllowCustomTextProperty);
        set => SetValue(AllowCustomTextProperty, value);
    }

    public bool EnableMouseWheelSelection
    {
        get => (bool)GetValue(EnableMouseWheelSelectionProperty);
        set => SetValue(EnableMouseWheelSelectionProperty, value);
    }

    public bool HandleEnterKey
    {
        get => (bool)GetValue(HandleEnterKeyProperty);
        set => SetValue(HandleEnterKeyProperty, value);
    }

    public bool OpenOnFocus
    {
        get => (bool)GetValue(OpenOnFocusProperty);
        set => SetValue(OpenOnFocusProperty, value);
    }

    public bool SelectFirstVisibleOnNoMatch
    {
        get => (bool)GetValue(SelectFirstVisibleOnNoMatchProperty);
        set => SetValue(SelectFirstVisibleOnNoMatchProperty, value);
    }

    public bool UpdateSourceWhileTyping
    {
        get => (bool)GetValue(UpdateSourceWhileTypingProperty);
        set => SetValue(UpdateSourceWhileTypingProperty, value);
    }

    public void ActivateSelector(bool showAllSuggestions = true)
    {
        AttachEditableTextBox();
        if (showAllSuggestions)
        {
            ApplyFilter(string.Empty);
        }

        Focus();
        if (_editableTextBox is not null)
        {
            _editableTextBox.Focus();
            _editableTextBox.CaretIndex = _editableTextBox.Text.Length;
            _editableTextBox.SelectionLength = 0;
        }

        if (showAllSuggestions)
        {
            IsDropDownOpen = true;
            BringSelectedItemIntoView();
        }
    }

    public void BeginTyping(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        AttachEditableTextBox();
        if (_editableTextBox is null)
        {
            SetCurrentValue(TextProperty, text);
            if (UpdateSourceWhileTyping) GetBindingExpression(TextProperty)?.UpdateSource();
            Focus();
            return;
        }

        // Typing on a selected mapping cell must behave like Excel: the first
        // character becomes the first character of the new value. Clear the old
        // ComboBox selection before updating Text so WPF cannot restore the stale
        // selection and eat that first key.
        _selectionTextUpdate = true;
        try
        {
            if (SelectedItem is not null)
                _lastCommittedText = GetValueText(SelectedItem);
            SelectedItem = null;
            _editableTextBox.Text = text;
            _editableTextBox.CaretIndex = text.Length;
            _editableTextBox.SelectionLength = 0;
            SetCurrentValue(TextProperty, text);
            if (UpdateSourceWhileTyping) GetBindingExpression(TextProperty)?.UpdateSource();
        }
        finally
        {
            _selectionTextUpdate = false;
        }

        _editableTextBox.Focus();
        ApplyFilter(text);
        if (_view.Cast<object>().Any()) IsDropDownOpen = true;
    }

    public override void OnApplyTemplate()
    {
        DetachEditableTextBox();
        base.OnApplyTemplate();
        AttachEditableTextBox();
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is not ComboBoxItem container) return;
        container.PreviewMouseLeftButtonDown -= ComboBoxItemPreviewMouseLeftButtonDown;
        container.PreviewMouseLeftButtonDown += ComboBoxItemPreviewMouseLeftButtonDown;
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ComboBoxItem container)
            container.PreviewMouseLeftButtonDown -= ComboBoxItemPreviewMouseLeftButtonDown;
        base.ClearContainerForItemOverride(element, item);
    }

    private void ComboBoxItemPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The editable ComboBox popup is hosted in a separate WPF visual tree.
        // Waiting for ComboBox.SelectionChanged after the normal mouse-focus transfer
        // lets LostKeyboardFocus commit/close the editor first, which made Bank Rename
        // options look clickable but left RenameTo unchanged. Commit the exact option on
        // MouseDown, before focus can leave the editable text box.
        if (sender is not ComboBoxItem { Content: { } clicked }) return;

        _popupSelectionCommitInProgress = true;
        SelectedItem = clicked;
        SetEditableText(GetValueText(clicked), updateSource: true);
        IsDropDownOpen = false;
        ApplyFilter(string.Empty);
        e.Handled = true;

        // Keep the committed value stable while popup focus settles. Only suppress
        // OpenOnFocus when focus actually has to be returned to the editable TextBox.
        // The previous implementation always armed the suppression flag; if focus was
        // already in the editor, that flag survived and could suppress a later genuine
        // user open. Cash Entry exposed this as a popup that appeared to open/close by
        // itself or would not accept the next Ledger selection.
        Dispatcher.BeginInvoke(() =>
        {
            AttachEditableTextBox();
            if (_editableTextBox is not null && !_editableTextBox.IsKeyboardFocusWithin)
            {
                _suppressNextFocusOpen = true;
                _editableTextBox.Focus();
            }
            else if (_editableTextBox is null && !IsKeyboardFocusWithin)
            {
                _suppressNextFocusOpen = true;
                Focus();
            }

            _popupSelectionCommitInProgress = false;
            Dispatcher.BeginInvoke(() => _suppressNextFocusOpen = false, DispatcherPriority.Background);
        }, DispatcherPriority.Input);
    }

    private static void OnSourceItemsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not SearchableComboBox comboBox) return;
        comboBox.ObserveCollection(args.OldValue as INotifyCollectionChanged, false);
        comboBox.ObserveCollection(args.NewValue as INotifyCollectionChanged, true);
        comboBox.ReloadItems();
    }

    private void ObserveCollection(INotifyCollectionChanged? collection, bool attach)
    {
        if (collection is null) return;
        if (attach)
        {
            _observedCollection = collection;
            collection.CollectionChanged += SourceCollectionChanged;
        }
        else
        {
            collection.CollectionChanged -= SourceCollectionChanged;
            if (ReferenceEquals(_observedCollection, collection)) _observedCollection = null;
        }
    }

    private void SourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Master/ledger option collections are often replaced in a burst.  Reloading
        // the complete searchable view for every Add produced a quadratic WPF storm
        // when Journal had many Ledgers and several selector cells were visible.
        if (_pendingReload is { Status: DispatcherOperationStatus.Pending }) return;
        _pendingReload = Dispatcher.BeginInvoke(new Action(() =>
        {
            _pendingReload = null;
            if (IsLoaded) ReloadItems();
        }), DispatcherPriority.DataBind);
    }

    private void ReloadItems()
    {
        // SourceItems may itself be derived from the bound Text value (Purchase GST
        // Treatment is one such selector). Rebuilding ItemsSource can make WPF raise
        // SelectionChanged while the source binding is still being updated. Writing
        // Text back to the source from that synthetic selection creates this loop:
        // Text setter -> SourceItems changed -> ReloadItems -> SelectionChanged ->
        // Text setter, eventually terminating the UI thread with 0xC00000FD.
        // A source reload is display synchronization only; it must never write back.
        if (_sourceReloadInProgress) return;
        _sourceReloadInProgress = true;
        try
        {
            // A recycled DataGrid cell can receive SourceItems before its Text target
            // binding has restored the row value. Pull the current bound value first,
            // then rebuild the option view. This is target-only synchronization and is
            // protected by the Fix 25 source-reload guard, so it cannot write back or
            // recreate the former SelectionChanged recursion.
            GetBindingExpression(TextProperty)?.UpdateTarget();

            var selectedText = SelectedItem is null ? string.Empty : GetValueText(SelectedItem);
            var typedText = Text;
            _items.Clear();
            if (SourceItems is not null)
            {
                foreach (var item in SourceItems)
                {
                    if (item is not null) _items.Add(item);
                }
            }

            ApplyFilter(string.Empty);
            var lookupText = string.IsNullOrWhiteSpace(selectedText) ? typedText : selectedText;
            var selected = string.IsNullOrWhiteSpace(lookupText)
                ? null
                : _items.FirstOrDefault(item => string.Equals(GetValueText(item), lookupText, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                SelectedItem = selected;
                _lastCommittedText = GetValueText(selected);
                SetEditableText(_lastCommittedText, updateSource: false);
            }
            else if (AllowCustomText && !string.IsNullOrWhiteSpace(typedText))
            {
                SetEditableText(typedText, updateSource: false);
            }
        }
        finally
        {
            _sourceReloadInProgress = false;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        GetBindingExpression(TextProperty)?.UpdateTarget();
        var boundText = Text?.Trim() ?? string.Empty;
        if (boundText.Length > 0) _lastCommittedText = boundText;
        AttachEditableTextBox();
        if (_observedCollection is null && SourceItems is INotifyCollectionChanged collection)
            ObserveCollection(collection, true);
        ReloadItems();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_pendingReload is { Status: DispatcherOperationStatus.Pending })
            _pendingReload.Abort();
        _pendingReload = null;
        DetachEditableTextBox();
        if (_observedCollection is not null) ObserveCollection(_observedCollection, false);
    }

    private void AttachEditableTextBox()
    {
        if (_editableTextBox is not null) return;
        _editableTextBox = Template?.FindName("PART_EditableTextBox", this) as TextBox;
        if (_editableTextBox is null) return;
        _editableTextBox.TextChanged += EditableTextBoxTextChanged;
        _editableTextBox.GotKeyboardFocus += EditableTextBoxGotKeyboardFocus;
    }

    private void DetachEditableTextBox()
    {
        if (_editableTextBox is null) return;
        _editableTextBox.TextChanged -= EditableTextBoxTextChanged;
        _editableTextBox.GotKeyboardFocus -= EditableTextBoxGotKeyboardFocus;
        _editableTextBox = null;
    }

    private void EditableTextBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_editableTextBox is null) return;

        if (_suppressNextFocusOpen)
        {
            _suppressNextFocusOpen = false;
            return;
        }

        // Preserve the user's text and place the caret at the end. Selecting all
        // text made a mapping cell look like the whole column had turned blue
        // and caused the first typed character to replace the complete value.
        _editableTextBox.CaretIndex = _editableTextBox.Text.Length;
        _editableTextBox.SelectionLength = 0;

        if (OpenOnFocus)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsKeyboardFocusWithin) return;
                ApplyFilter(string.Empty);
                IsDropDownOpen = true;
                BringSelectedItemIntoView();
            }, DispatcherPriority.Input);
        }
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!OpenOnFocus || IsKeyboardFocusWithin || e.OriginalSource is ComboBoxItem) return;
        Dispatcher.BeginInvoke(() => ActivateSelector(showAllSuggestions: true), DispatcherPriority.Input);
    }

    private void EditableTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_selectionTextUpdate || _editableTextBox is null) return;

        // Once the user starts typing, the old ComboBox selection must no longer
        // be allowed to rewrite the editor text while the filtered view refreshes.
        // That WPF selection refresh was the source of the missing-first-character
        // behaviour seen when a Ledger Mapping selector was edited for the first time.
        var typedText = _editableTextBox.Text;
        if (SelectedItem is not null &&
            !string.Equals(GetValueText(SelectedItem), typedText, StringComparison.OrdinalIgnoreCase))
        {
            _selectionTextUpdate = true;
            try
            {
                SelectedItem = null;
                _editableTextBox.Text = typedText;
                _editableTextBox.CaretIndex = typedText.Length;
            }
            finally
            {
                _selectionTextUpdate = false;
            }
        }

        SetCurrentValue(TextProperty, typedText);
        if (UpdateSourceWhileTyping) GetBindingExpression(TextProperty)?.UpdateSource();

        _filteringFromEditor = true;
        try
        {
            ApplyFilter(typedText);
        }
        finally
        {
            _filteringFromEditor = false;
        }

        if (_editableTextBox.IsKeyboardFocusWithin && _view.Cast<object>().Any()) IsDropDownOpen = true;
    }

    private void ApplyFilter(string? searchText)
    {
        var term = searchText?.Trim() ?? string.Empty;
        _view.Filter = item =>
            term.Length == 0 ||
            GetDisplayText(item).Contains(term, StringComparison.OrdinalIgnoreCase) ||
            GetValueText(item).Contains(term, StringComparison.OrdinalIgnoreCase);
        _view.Refresh();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filteringFromEditor || SelectedItem is null) return;
        _lastCommittedText = GetValueText(SelectedItem);

        // Selection changes caused by rebuilding SourceItems are synthetic WPF state
        // synchronization. Never push them back through the bound Text property.
        // Real mouse/keyboard/user selections still update the binding normally.
        SetEditableText(_lastCommittedText, updateSource: !_sourceReloadInProgress);
        ApplyFilter(string.Empty);
        BringSelectedItemIntoView();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F4 ||
            ((e.Key == Key.Down || e.Key == Key.Up) &&
             Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)))
        {
            ToggleCompleteDropDown();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                IsDropDownOpen = false;
                ApplyFilter(string.Empty);
                if (!AllowCustomText && !SelectFirstVisibleOnNoMatch)
                    RestoreLastCommittedText();
                e.Handled = true;
                return;

            case Key.Down:
                IsDropDownOpen = true;
                MoveSelection(1);
                e.Handled = true;
                return;

            case Key.Up:
                IsDropDownOpen = true;
                MoveSelection(-1);
                e.Handled = true;
                return;

            case Key.PageDown:
                IsDropDownOpen = true;
                MoveSelection(10);
                e.Handled = true;
                return;

            case Key.PageUp:
                IsDropDownOpen = true;
                MoveSelection(-10);
                e.Handled = true;
                return;

            case Key.Home:
                if (!IsDropDownOpen) return;
                SelectBoundary(first: true);
                e.Handled = true;
                return;

            case Key.End:
                if (!IsDropDownOpen) return;
                SelectBoundary(first: false);
                e.Handled = true;
                return;

            case Key.Enter:
                CommitCurrentText();
                IsDropDownOpen = false;
                ApplyFilter(string.Empty);
                // Mapping cells deliberately let Enter bubble to DataGrid after the
                // current text is committed, giving Excel-like commit-and-move behavior.
                e.Handled = HandleEnterKey;
                return;
        }
    }

    private void ToggleCompleteDropDown()
    {
        if (IsDropDownOpen)
        {
            IsDropDownOpen = false;
            return;
        }

        ApplyFilter(string.Empty);
        IsDropDownOpen = true;
        BringSelectedItemIntoView();
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!EnableMouseWheelSelection || !_view.Cast<object>().Any()) return;

        // Optional behaviour for selectors that deliberately support wheel
        // selection. Ledger Mapping disables this so the mouse wheel scrolls
        // the workbench instead of silently changing Final Ledger/Group values.
        if (IsDropDownOpen) return;

        MoveSelection(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // A Popup lives in a separate WPF visual tree. Moving focus from the editable
        // TextBox into one of this ComboBox's popup items is not a real editor exit.
        // Closing/committing here races the item click and was the remaining Cash Ledger
        // selector failure. Let the popup item commit complete first.
        if (_popupSelectionCommitInProgress || IsFocusMovingIntoOwnDropDown(e.NewFocus)) return;

        CommitCurrentText();
        IsDropDownOpen = false;
        ApplyFilter(string.Empty);
    }

    private bool IsFocusMovingIntoOwnDropDown(IInputElement? newFocus)
    {
        if (newFocus is not DependencyObject target) return false;

        // ContainerFromElement understands the ComboBox item-container relationship
        // even though the Popup is hosted in its own presentation window. Avoid walking
        // VisualTreeHelper parents directly because keyboard focus can also land on a
        // ContentElement, which is not guaranteed to have a visual parent.
        var container = ItemsControl.ContainerFromElement(this, target) as ComboBoxItem;
        return container is not null && ItemContainerGenerator.IndexFromContainer(container) >= 0;
    }

    private void MoveSelection(int delta)
    {
        var visible = _view.Cast<object>().ToList();
        if (visible.Count == 0) return;

        var index = SelectedItem is null ? -1 : visible.IndexOf(SelectedItem);
        if (index < 0 && !string.IsNullOrWhiteSpace(Text))
        {
            index = visible.FindIndex(item => string.Equals(GetValueText(item), Text, StringComparison.OrdinalIgnoreCase));
        }
        if (index < 0) index = delta > 0 ? -1 : visible.Count;
        index = Math.Clamp(index + delta, 0, visible.Count - 1);
        SelectedItem = visible[index];
        BringSelectedItemIntoView();
    }

    private void SelectBoundary(bool first)
    {
        var visible = _view.Cast<object>().ToList();
        if (visible.Count == 0) return;
        SelectedItem = first ? visible[0] : visible[^1];
        BringSelectedItemIntoView();
    }

    private void CommitCurrentText()
    {
        var typed = _editableTextBox?.Text?.Trim() ?? Text?.Trim() ?? string.Empty;
        var exact = _items.FirstOrDefault(item =>
            string.Equals(GetValueText(item), typed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(GetDisplayText(item), typed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            SelectedItem = exact;
            return;
        }

        if (AllowCustomText)
        {
            SelectedItem = null;
            SetEditableText(typed, updateSource: true);
            return;
        }

        if (SelectedItem is not null)
        {
            SetEditableText(GetValueText(SelectedItem), updateSource: true);
            return;
        }

        if (SelectFirstVisibleOnNoMatch)
        {
            var first = _view.Cast<object>().FirstOrDefault();
            if (first is not null) SelectedItem = first;
            return;
        }

        RestoreLastCommittedText();
    }

    private void RestoreLastCommittedText()
    {
        var restored = _lastCommittedText.Trim();
        var item = restored.Length == 0
            ? null
            : _items.FirstOrDefault(candidate =>
                string.Equals(GetValueText(candidate), restored, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            SelectedItem = item;
            SetEditableText(GetValueText(item), updateSource: false);
            return;
        }

        SelectedItem = null;
        SetEditableText(restored, updateSource: false);
    }

    private void SetEditableText(string value, bool updateSource)
    {
        _selectionTextUpdate = true;
        try
        {
            SetCurrentValue(TextProperty, value);
            if (_editableTextBox is not null)
            {
                _editableTextBox.Text = value;
                _editableTextBox.CaretIndex = value.Length;
            }
            if (updateSource) GetBindingExpression(TextProperty)?.UpdateSource();
        }
        finally
        {
            _selectionTextUpdate = false;
        }
    }

    private static string GetDisplayText(object? item) => item switch
    {
        ISuggestionTextItem rich => rich.SuggestionDisplay?.Trim() ?? string.Empty,
        _ => item?.ToString()?.Trim() ?? string.Empty
    };

    private static string GetValueText(object? item) => item switch
    {
        ISuggestionTextItem rich => rich.SuggestionValue?.Trim() ?? string.Empty,
        _ => item?.ToString()?.Trim() ?? string.Empty
    };

    private void BringSelectedItemIntoView()
    {
        if (SelectedItem is null) return;
        Dispatcher.BeginInvoke(
            () => (ItemContainerGenerator.ContainerFromItem(SelectedItem) as ComboBoxItem)?.BringIntoView(),
            DispatcherPriority.Background);
    }
}
