using System.Collections;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.Controls;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Domain.Purchases;

namespace AccountingAutomationHub.Desktop.Views;

public partial class PurchaseRecordsGrid :
    UserControl
{
    private readonly ConditionalWeakTable<SearchableComboBox, SelectorEditState> _selectorEditStates = new();
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(PurchaseRecordsGrid),
            new PropertyMetadata(null));

    public static readonly DependencyProperty FinancialYearStartProperty =
        DependencyProperty.Register(nameof(FinancialYearStart), typeof(DateTime?), typeof(PurchaseRecordsGrid), new PropertyMetadata(null));
    public static readonly DependencyProperty FinancialYearEndProperty =
        DependencyProperty.Register(nameof(FinancialYearEnd), typeof(DateTime?), typeof(PurchaseRecordsGrid), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowTwoBOnlyActionProperty =
        DependencyProperty.Register(nameof(ShowTwoBOnlyAction), typeof(bool), typeof(PurchaseRecordsGrid), new PropertyMetadata(false));

    public PurchaseRecordsGrid()
    {
        InitializeComponent();
        SearchFilterBar.Attach("GstDate", RecordsGrid);
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(
            ItemsSourceProperty);
        set => SetValue(
            ItemsSourceProperty,
            value);
    }
    public DateTime? FinancialYearStart
    {
        get => (DateTime?)GetValue(FinancialYearStartProperty);
        set => SetValue(FinancialYearStartProperty, value);
    }
    public DateTime? FinancialYearEnd
    {
        get => (DateTime?)GetValue(FinancialYearEndProperty);
        set => SetValue(FinancialYearEndProperty, value);
    }
    public bool ShowTwoBOnlyAction
    {
        get => (bool)GetValue(ShowTwoBOnlyActionProperty);
        set => SetValue(ShowTwoBOnlyActionProperty, value);
    }

    private void DeleteSelectedRows_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PurchaseImportViewModel { CanDeletePurchase: true }) return;
        var selected = RecordsGrid.SelectedItems
            .OfType<PurchaseImportRecord>()
            .Concat(RecordsGrid.SelectedCells.Select(cell => cell.Item).OfType<PurchaseImportRecord>())
            .GroupBy(row => row.PersistentId > 0 ? $"ID:{row.PersistentId}" : $"REF:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(row)}")
            .Select(group => group.First())
            .ToArray();

        if (DataContext is PurchaseImportViewModel viewModel)
            viewModel.DeleteRows(selected);
    }

    private void IncludeTwoBOnlyRows_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PurchaseImportViewModel { CanEditPurchase: true } viewModel) return;
        var selected = RecordsGrid.SelectedItems
            .OfType<PurchaseImportRecord>()
            .Concat(RecordsGrid.SelectedCells.Select(cell => cell.Item).OfType<PurchaseImportRecord>())
            .Where(row => row.ReconciliationStatus.Equals("2B Only", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => row.PersistentId > 0 ? $"ID:{row.PersistentId}" : $"REF:{RuntimeHelpers.GetHashCode(row)}")
            .Select(group => group.First())
            .ToArray();
        viewModel.IncludeTwoBOnlyRows(selected);
    }

    private void AddTwoBRateLine_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PurchaseImportViewModel { CanEditPurchase: true } viewModel) return;
        var selected = RecordsGrid.SelectedItems
            .OfType<PurchaseImportRecord>()
            .Concat(RecordsGrid.SelectedCells.Select(cell => cell.Item).OfType<PurchaseImportRecord>())
            .FirstOrDefault(row => row.IsTwoBOnlyEvidence);
        viewModel.AddTwoBRateLine(selected);
    }

    private void RecordsGrid_CellEditEnding(
        object sender,
        DataGridCellEditEndingEventArgs e)
    {
        if (DataContext is not PurchaseImportViewModel { CanEditPurchase: true }) return;
        if (e.EditAction != DataGridEditAction.Commit ||
            e.Row.Item is not PurchaseImportRecord row)
            return;

        var header = e.Column.Header?.ToString() ?? string.Empty;
        var recalculateTax = header.Equals("Rate %", StringComparison.OrdinalIgnoreCase) ||
                             header.Equals("Taxable", StringComparison.OrdinalIgnoreCase);
        ScheduleAutomaticSave(row, recalculateTax);
    }

    private void GstTreatment_GotKeyboardFocus(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is SearchableComboBox { DataContext: PurchaseImportRecord row } combo)
            ArmSelectorEdit(combo, row.GstApplicability);
    }

    private void GstTreatment_DropDownClosed(
        object sender,
        EventArgs e)
    {
        if (sender is SearchableComboBox { DataContext: PurchaseImportRecord row } combo)
            ScheduleAutomaticSaveIfValueChanged(combo, row, () => row.GstApplicability, finalizeIfUnchanged: false);
    }

    private void GstTreatment_LostKeyboardFocus(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is SearchableComboBox { DataContext: PurchaseImportRecord row } combo)
            ScheduleAutomaticSaveIfValueChanged(combo, row, () => row.GstApplicability, finalizeIfUnchanged: true);
    }

    private void TwoBReconciliationTreatment_GotKeyboardFocus(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is SearchableComboBox { DataContext: PurchaseImportRecord row } combo)
            ArmSelectorEdit(combo, row.ReconciliationGstApplicability);
    }

    private void TwoBReconciliationTreatment_DropDownClosed(
        object sender,
        EventArgs e)
    {
        if (sender is SearchableComboBox { DataContext: PurchaseImportRecord row } combo)
            CommitTwoBReconciliationTreatment(combo, row, finalizeIfUnchanged: false);
    }

    private void TwoBReconciliationTreatment_LostKeyboardFocus(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is SearchableComboBox { DataContext: PurchaseImportRecord row } combo)
            CommitTwoBReconciliationTreatment(combo, row, finalizeIfUnchanged: true);
    }

    private void CommitTwoBReconciliationTreatment(
        SearchableComboBox combo,
        PurchaseImportRecord row,
        bool finalizeIfUnchanged)
    {
        if (!_selectorEditStates.TryGetValue(combo, out var state) || !state.IsArmed)
            return;

        Dispatcher.BeginInvoke(
            () =>
            {
                if (!_selectorEditStates.TryGetValue(combo, out var deferredState) || !deferredState.IsArmed)
                    return;

                combo.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
                // Read the user's committed selector text. For retained 2B evidence the
                // derived reconciliation getter intentionally reflects Destination, so it
                // cannot expose the requested Included state until validation promotes
                // the invoice. The control text is the authoritative user intent here.
                var current = (combo.Text ?? string.Empty).Trim();
                if (string.Equals(deferredState.OriginalValue, current, StringComparison.OrdinalIgnoreCase))
                {
                    if (finalizeIfUnchanged) deferredState.IsArmed = false;
                    return;
                }

                deferredState.IsArmed = false;
                if (DataContext is PurchaseImportViewModel { CanEditPurchase: true } viewModel)
                {
                    if (row.IsTwoBOnlyEvidence)
                    {
                        viewModel.SetTwoBInclusionState(row, current);
                    }
                    else
                    {
                        row.GstApplicability = current;
                        viewModel.QueueAutomaticSave(row);
                    }
                }
            },
            DispatcherPriority.Background);
    }

    private void CostLedger_GotKeyboardFocus(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is SearchableComboBox { DataContext: PurchaseImportRecord row } combo)
            ArmSelectorEdit(combo, row.PurchaseLedger);
    }

    private void CostLedger_DropDownClosed(
        object sender,
        EventArgs e)
    {
        if (sender is SearchableComboBox { DataContext: PurchaseImportRecord row } combo)
            ScheduleAutomaticSaveIfValueChanged(combo, row, () => row.PurchaseLedger, finalizeIfUnchanged: false);
    }

    private void CostLedger_LostKeyboardFocus(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is SearchableComboBox { DataContext: PurchaseImportRecord row } combo)
            ScheduleAutomaticSaveIfValueChanged(combo, row, () => row.PurchaseLedger, finalizeIfUnchanged: true);
    }

    private void ArmSelectorEdit(SearchableComboBox combo, string value)
    {
        var state = _selectorEditStates.GetValue(combo, _ => new SelectorEditState());
        if (state.IsArmed) return;
        state.OriginalValue = (value ?? string.Empty).Trim();
        state.IsArmed = true;
    }

    private void ScheduleAutomaticSaveIfValueChanged(
        SearchableComboBox combo,
        PurchaseImportRecord row,
        Func<string> readCurrentValue,
        bool finalizeIfUnchanged)
    {
        if (!_selectorEditStates.TryGetValue(combo, out var state) || !state.IsArmed)
            return;

        Dispatcher.BeginInvoke(
            () =>
            {
                if (!_selectorEditStates.TryGetValue(combo, out var deferredState) || !deferredState.IsArmed)
                    return;

                var current = (readCurrentValue() ?? string.Empty).Trim();
                if (string.Equals(deferredState.OriginalValue, current, StringComparison.Ordinal))
                {
                    if (finalizeIfUnchanged) deferredState.IsArmed = false;
                    return;
                }

                deferredState.OriginalValue = current;
                ScheduleAutomaticSave(row);
            },
            DispatcherPriority.Background);
    }

    private sealed class SelectorEditState
    {
        public string OriginalValue { get; set; } = string.Empty;
        public bool IsArmed { get; set; }
    }

    private void ScheduleAutomaticSave(PurchaseImportRecord row, bool recalculateTwoBRate = false)
    {
        if (DataContext is not PurchaseImportViewModel { CanEditPurchase: true }) return;
        Dispatcher.BeginInvoke(
            () =>
            {
                RecordsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                RecordsGrid.CommitEdit(DataGridEditingUnit.Row, true);

                if (DataContext is PurchaseImportViewModel viewModel)
                {
                    if (recalculateTwoBRate)
                        viewModel.RecalculateTwoBRateLine(row);
                    viewModel.QueueAutomaticSave(row);
                }
            },
            DispatcherPriority.Background);
    }

    private void RecordsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not PurchaseImportViewModel { CanEditPurchase: true }) return;
        if (sender is not DataGrid grid || !IsSelectorColumn(grid.CurrentColumn)) return;
        if (Keyboard.FocusedElement is DependencyObject focused &&
            FindVisualParent<SearchableComboBox>(focused) is not null) return;

        var open = e.Key is Key.F2 or Key.F4 ||
                   ((e.Key is Key.Down or Key.Up) && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
        if (!open) return;
        var selector = GetCurrentSelector(grid);
        if (selector is null)
            grid.Dispatcher.BeginInvoke(() => GetCurrentSelector(grid)?.ActivateSelector(true), DispatcherPriority.Input);
        else
            selector.ActivateSelector(true);
        e.Handled = true;
    }

    private void RecordsGrid_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (DataContext is not PurchaseImportViewModel { CanEditPurchase: true }) return;
        if (sender is not DataGrid grid || string.IsNullOrEmpty(e.Text) ||
            !IsSelectorColumn(grid.CurrentColumn)) return;
        if (Keyboard.FocusedElement is DependencyObject focused &&
            FindVisualParent<SearchableComboBox>(focused) is not null) return;
        var selector = GetCurrentSelector(grid);
        if (selector is null)
            grid.Dispatcher.BeginInvoke(() => GetCurrentSelector(grid)?.BeginTyping(e.Text), DispatcherPriority.Input);
        else
            selector.BeginTyping(e.Text);
        e.Handled = true;
    }

    private static bool IsSelectorColumn(DataGridColumn? column)
    {
        var header = column?.Header?.ToString() ?? string.Empty;
        return header.Equals("GST Treatment", StringComparison.OrdinalIgnoreCase) ||
               header.Equals("Purchase / Cost Ledger", StringComparison.OrdinalIgnoreCase);
    }

    private static SearchableComboBox? GetCurrentSelector(DataGrid grid)
    {
        if (grid.CurrentItem is null || grid.CurrentColumn is null) return null;
        grid.ScrollIntoView(grid.CurrentItem, grid.CurrentColumn);
        grid.UpdateLayout();
        var content = grid.CurrentColumn.GetCellContent(grid.CurrentItem);
        if (content is SearchableComboBox direct && direct.IsVisible) return direct;
        return FindVisibleVisualChild<SearchableComboBox>(content);
    }

    private static T? FindVisibleVisualChild<T>(DependencyObject? source) where T : FrameworkElement
    {
        if (source is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(source); i++)
        {
            var child = VisualTreeHelper.GetChild(source, i);
            if (child is T match && match.IsVisible) return match;
            var nested = FindVisibleVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject? source) where T : DependencyObject
    {
        if (source is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(source); i++)
        {
            var child = VisualTreeHelper.GetChild(source, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

}
