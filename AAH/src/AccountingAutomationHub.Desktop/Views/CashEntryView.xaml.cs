using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.Controls;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Domain.Banking;

namespace AccountingAutomationHub.Desktop.Views;

public partial class CashEntryView : UserControl
{
    private bool _rowSavePending;

    public CashEntryView()
    {
        InitializeComponent();
        CashSearchFilterBar.Attach("EntryDate", CashDebitCreditGrid, CashSingleAmountGrid);
    }

    private void CashEntryGrid_RowEditEnding(
        object sender,
        DataGridRowEditEndingEventArgs e)
    {
        if (ViewModel is not { CanEditAccounting: true }) return;
        if (e.EditAction != DataGridEditAction.Commit ||
            _rowSavePending ||
            e.Row.Item is not CashEntryRecord entry)
        {
            return;
        }

        // A searchable Ledger popup is part of the current cell interaction even
        // though WPF hosts the Popup in a separate window. Do not validate/save the
        // row while that list is open; validation can publish live refresh work and
        // disturb the popup before its Ledger choice has committed.
        if (FindVisualChild<SearchableComboBox>(e.Row) is { IsDropDownOpen: true }) return;

        _rowSavePending = true;

        Dispatcher.BeginInvoke(
            () =>
            {
                try
                {
                    ViewModel?.SaveCashEntryRecord(entry);
                }
                finally
                {
                    _rowSavePending = false;
                }
            },
            DispatcherPriority.Background);
    }

    private void CashLedgerPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not { CanEditAccounting: true }) return;
        if (sender is not LedgerPicker picker) return;
        var entry = picker.DataContext as CashEntryRecord;
        if (entry is null && ViewModel?.IsCashVoucherEditorOpen == true)
            entry = ViewModel.CashVoucherDraft;
        if (entry is null) return;

        // Wait for LedgerPicker to commit its exact Master selection before asking
        // the central Ledger Total service for balance/suggestion information.
        Dispatcher.BeginInvoke(
            () => ViewModel?.RefreshCashLedgerTotalFor(entry),
            DispatcherPriority.DataBind);
    }

    private void BulkDeleteCashEntries_Click(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null) return;
        var grid = vm.IsDebitCreditLayout ? CashDebitCreditGrid : CashSingleAmountGrid;
        var selected = grid.SelectedItems.Cast<object>().OfType<CashEntryRecord>().ToArray();
        vm.DeleteSelectedCashEntries(selected);
    }

    private void AdvancedCloneCash_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { CanEditAccounting: true }) return;
        var vm = ViewModel;
        var source = vm?.SelectedCashEntry;
        if (vm is null || source is null || source.Id <= 0) return;
        if (System.Windows.Application.Current is not App app || app.Preferences is null) return;
        var window = new AdvancedCloneWindow(source.ClientId, source.FinancialYear, source.Amount, source.EntryDate, app.Preferences)
        {
            Owner = Window.GetWindow(this)
        };
        if (window.ShowDialog() == true && window.Schedule.Count > 0)
            vm.CreateCashCloneSeries(window.Schedule);
    }

    private void RecurringCash_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ViewModel is not { CanEditAccounting: true } vm || vm.RecurringClientId <= 0) return;
        if (System.Windows.Application.Current is not App app || app.Preferences is null) return;
        var window = new RecurringDirectWindow(
            vm.RecurringClientId, vm.RecurringFinancialYear, "Cash", app.Preferences, vm.RefreshCashAfterRecurring)
        {
            Owner = System.Windows.Window.GetWindow(this)
        };
        window.ShowDialog();
    }

    private BankingViewModel? ViewModel =>
        (DataContext as CashEntryViewModel)?.Core;

    // Do not validate/save a Cash row merely because its Ledger popup closes or
    // moves keyboard focus. Popup focus is part of normal ComboBox selection and
    // the old lifecycle handlers could validate an incomplete row while the user
    // was still choosing a Ledger, making the list close/reopen and feel impossible
    // to select. RowEditEnding and Save Selected Row remain the persistence points.

    private void CashSelectorGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { CanEditAccounting: true }) return;
        if (sender is not DataGrid grid || !IsCashSelectorColumn(grid.CurrentColumn)) return;
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

    private void CashSelectorGrid_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (ViewModel is not { CanEditAccounting: true }) return;
        if (sender is not DataGrid grid || string.IsNullOrEmpty(e.Text) ||
            !IsCashSelectorColumn(grid.CurrentColumn)) return;
        if (Keyboard.FocusedElement is DependencyObject focused &&
            FindVisualParent<SearchableComboBox>(focused) is not null) return;

        var selector = GetCurrentSelector(grid);
        if (selector is null)
            grid.Dispatcher.BeginInvoke(() => GetCurrentSelector(grid)?.BeginTyping(e.Text), DispatcherPriority.Input);
        else
            selector.BeginTyping(e.Text);
        e.Handled = true;
    }

    private static bool IsCashSelectorColumn(DataGridColumn? column)
    {
        var header = column?.Header?.ToString() ?? string.Empty;
        return header.Equals("Ledger Name", StringComparison.OrdinalIgnoreCase) ||
               header.Equals("Map / Create As", StringComparison.OrdinalIgnoreCase) ||
               header.Equals("Parent Group", StringComparison.OrdinalIgnoreCase);
    }

    private static SearchableComboBox? GetCurrentSelector(DataGrid grid)
    {
        if (grid.CurrentItem is null || grid.CurrentColumn is null) return null;
        grid.ScrollIntoView(grid.CurrentItem, grid.CurrentColumn);
        grid.UpdateLayout();
        var content = grid.CurrentColumn.GetCellContent(grid.CurrentItem);
        return content as SearchableComboBox ?? FindVisualChild<SearchableComboBox>(content);
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
