using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class SalesImportView : UserControl
{
    public SalesImportView() => InitializeComponent();

    private void SummaryTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string target })
        {
            return;
        }

        if (target.Equals("B2B", StringComparison.OrdinalIgnoreCase))
        {
            if (DataContext is SalesImportViewModel sales)
            {
                var window = new SalesB2BCustomerNamesWindow(sales)
                {
                    Owner = Window.GetWindow(this)
                };
                window.ShowDialog();
            }
            e.Handled = true;
            return;
        }

        if (target.Equals("B2C", StringComparison.OrdinalIgnoreCase))
        {
            if (DataContext is SalesImportViewModel sales)
            {
                // Fix126: rebuild from the currently painted Sales rows immediately
                // before opening so the popup cannot retain a stale empty collection.
                sales.RefreshB2CCustomerPanelForDisplay();
                var window = new SalesB2CCustomerNamesWindow(sales) { Owner = Window.GetWindow(this) };
                window.ShowDialog();
            }
            e.Handled = true;
            return;
        }

        RecordsTabs.SelectedIndex = target switch
        {
            "Sales" => 0,
            "Credit Notes" => 1,
            "All" => 2,
            "Issues" => 3,
            _ => RecordsTabs.SelectedIndex
        };
        RecordsTabs.Focus();
        e.Handled = true;
    }

    private void ReimportSalesMonthButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.ContextMenu is not ContextMenu menu)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async void ReimportSalesMonthMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SalesImportViewModel vm ||
            sender is not MenuItem { Tag: string monthText } ||
            !int.TryParse(monthText, out var month))
        {
            return;
        }

        await vm.ReimportAutomaticSourcesForMonthAsync(month);
        e.Handled = true;
    }

    private void OpenJournal_Click(object sender, RoutedEventArgs e) =>
        NavigateLinkedModule("Journal");

    private void OpenGstr3b_Click(object sender, RoutedEventArgs e) =>
        NavigateLinkedModule("GST");

    private void NavigateLinkedModule(string module)
    {
        if (Window.GetWindow(this)?.DataContext is MainWindowViewModel main)
        {
            main.NavigateFromAccountingActivity(module);
        }
    }
}
