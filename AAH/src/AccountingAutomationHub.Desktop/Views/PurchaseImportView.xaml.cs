using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class PurchaseImportView : UserControl
{
    public PurchaseImportView() => InitializeComponent();

    private void SummaryTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string target })
        {
            return;
        }

        if (DataContext is PurchaseImportViewModel vm && target is "Matched" or "2A Only" or "2B Only" or "Differences")
            vm.ApplyReconciliationFilter(target);

        RecordsTabs.SelectedIndex = target switch
        {
            "Purchase" => 0,
            "RCM" => 1,
            "Debit Notes" => 2,
            "Debit Note Reverse" => 3,
            "Matched" or "2A Only" or "2B Only" or "Differences" => 4,
            "All / Recon" => 4,
            "Issues" => 5,
            _ => RecordsTabs.SelectedIndex
        };
        RecordsTabs.Focus();
        e.Handled = true;
    }


    private void ReimportPurchaseMonthButton_Click(object sender, RoutedEventArgs e)
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

    private async void ReimportPurchaseMonthMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PurchaseImportViewModel vm ||
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
