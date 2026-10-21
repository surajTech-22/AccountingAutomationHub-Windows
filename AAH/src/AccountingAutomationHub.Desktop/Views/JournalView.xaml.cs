using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.Controls;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Domain.Journals;

namespace AccountingAutomationHub.Desktop.Views;

public partial class JournalView : UserControl
{
    public JournalView()
    {
        InitializeComponent();
        JournalSearchFilterBar.Attach("VoucherDate", SummaryGrid, VoucherGrid, LineGrid);
    }

    private void SummaryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SummaryGrid.SelectedItem is JournalSummaryRow row)
        {
            OpenSummaryRow(row);
            e.Handled = true;
        }
    }

    private void SummaryGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SummaryGrid.SelectedItem is JournalSummaryRow row)
        {
            OpenSummaryRow(row);
            e.Handled = true;
        }
    }

    private void SummaryOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: JournalSummaryRow row })
        {
            OpenSummaryRow(row);
        }
    }

    private void OpenSummaryRow(JournalSummaryRow row)
    {
        if (DataContext is not JournalViewModel viewModel ||
            !viewModel.OpenSummaryPeriod(row.PeriodKey))
        {
            return;
        }

        JournalTabs.SelectedIndex = 1;
        Dispatcher.BeginInvoke(() =>
        {
            if (viewModel.SelectedVoucher is null)
            {
                return;
            }

            VoucherGrid.ScrollIntoView(viewModel.SelectedVoucher);
            VoucherGrid.SelectedItem = viewModel.SelectedVoucher;
            VoucherGrid.Focus();
        }, DispatcherPriority.Input);
    }

    private void OpenGstr3b_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainWindowViewModel main)
        {
            main.NavigateFromAccountingActivity("GST");
        }
    }

    private void VoucherGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not JournalViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Delete && !IsCellEditor(e.OriginalSource))
        {
            viewModel.DeleteSelectedVoucher();
            e.Handled = true;
        }
        else if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            viewModel.AddManual();
            e.Handled = true;
        }
        else if (e.Key == Key.V &&
                 Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                 ClipboardContainsMultiCellBlock())
        {
            viewModel.PreviewSmartClipboard();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            viewModel.SaveVoucher();
            e.Handled = true;
        }
    }

    private void LineGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not JournalViewModel viewModel ||
            sender is not DataGrid grid)
        {
            return;
        }

        if (TryActivateSelectorFromGridCell(grid, e))
        {
            return;
        }

        if (e.Key == Key.Delete && !IsCellEditor(e.OriginalSource))
        {
            viewModel.DeleteSelectedLine();
            e.Handled = true;
        }
        else if (e.Key == Key.V &&
                 Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                 ClipboardContainsMultiCellBlock())
        {
            viewModel.PreviewLineClipboard();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            viewModel.SaveVoucher();
            e.Handled = true;
        }
        else if (e.Key == Key.Insert)
        {
            viewModel.AddLine();
            e.Handled = true;
        }
    }

    private void LineGrid_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not DataGrid grid ||
            string.IsNullOrEmpty(e.Text) ||
            !IsSelectorColumn(grid.CurrentColumn))
        {
            return;
        }

        if (Keyboard.FocusedElement is DependencyObject focused &&
            FindVisualParent<SearchableComboBox>(focused) is not null)
        {
            return;
        }

        var selector = GetCurrentSelector(grid);
        if (selector is null)
        {
            grid.Dispatcher.BeginInvoke(() =>
            {
                var deferred = GetCurrentSelector(grid);
                deferred?.BeginTyping(e.Text);
            }, DispatcherPriority.Input);
            e.Handled = true;
            return;
        }

        selector.BeginTyping(e.Text);
        e.Handled = true;
    }

    private static bool TryActivateSelectorFromGridCell(DataGrid grid, KeyEventArgs e)
    {
        if (!IsSelectorColumn(grid.CurrentColumn))
        {
            return false;
        }

        if (Keyboard.FocusedElement is DependencyObject focused &&
            FindVisualParent<SearchableComboBox>(focused) is not null)
        {
            return false;
        }

        var openSelector =
            e.Key == Key.F2 ||
            e.Key == Key.F4 ||
            ((e.Key == Key.Down || e.Key == Key.Up) &&
             Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));

        if (!openSelector)
        {
            return false;
        }

        var selector = GetCurrentSelector(grid);
        if (selector is null)
        {
            grid.Dispatcher.BeginInvoke(() =>
            {
                GetCurrentSelector(grid)?.ActivateSelector(showAllSuggestions: true);
            }, DispatcherPriority.Input);
        }
        else
        {
            selector.ActivateSelector(showAllSuggestions: true);
        }

        e.Handled = true;
        return true;
    }

    private static SearchableComboBox? GetCurrentSelector(DataGrid grid)
    {
        if (grid.CurrentItem is null || grid.CurrentColumn is null)
        {
            return null;
        }

        grid.ScrollIntoView(grid.CurrentItem, grid.CurrentColumn);
        grid.UpdateLayout();
        var content = grid.CurrentColumn.GetCellContent(grid.CurrentItem);
        return content as SearchableComboBox ??
               FindVisualChild<SearchableComboBox>(content);
    }

    private static bool IsSelectorColumn(DataGridColumn? column)
    {
        var header = column?.Header?.ToString() ?? string.Empty;
        return header.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ||
               header.Equals("Tax Head", StringComparison.OrdinalIgnoreCase) ||
               header.Equals("Role", StringComparison.OrdinalIgnoreCase);
    }

    private void VoucherDate_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not JournalViewModel viewModel) return;
        Dispatcher.BeginInvoke(
            viewModel.RefreshSelectedJournalLedgerTotal,
            DispatcherPriority.DataBind);
    }

    private void AdvancedCloneJournal_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JournalViewModel vm || vm.SelectedVoucher is not { PersistentId: > 0 } source) return;
        if (System.Windows.Application.Current is not App app || app.Preferences is null) return;
        var amount = source.Lines.Where(line => line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase)).Sum(line => Math.Abs(line.Amount));
        var window = new AdvancedCloneWindow(source.ClientId, source.FinancialYear, amount, source.VoucherDate, app.Preferences)
        {
            Owner = Window.GetWindow(this)
        };
        if (window.ShowDialog() == true && window.Schedule.Count > 0)
            vm.CreateJournalCloneSeries(window.Schedule);
    }

    private void RecurringJournal_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JournalViewModel vm || vm.RecurringClientId <= 0) return;
        if (System.Windows.Application.Current is not App app || app.Preferences is null) return;
        var window = new RecurringDirectWindow(
            vm.RecurringClientId, vm.RecurringFinancialYear, "Journal", app.Preferences, vm.Refresh)
        {
            Owner = Window.GetWindow(this)
        };
        window.ShowDialog();
    }

    private static bool IsCellEditor(object source) =>
        source is TextBoxBase or ComboBox or DatePicker;

    private static T? FindVisualChild<T>(DependencyObject? source) where T : DependencyObject
    {
        if (source is null)
        {
            return null;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool ClipboardContainsMultiCellBlock()
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                return false;
            }

            var text = Clipboard.GetText();
            return text.Contains('\t') || text.Contains('\r') || text.Contains('\n');
        }
        catch
        {
            return false;
        }
    }
}
