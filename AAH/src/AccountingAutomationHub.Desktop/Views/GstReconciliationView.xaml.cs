using System.Windows;
using System.Windows.Controls;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class GstReconciliationView : UserControl
{
    public GstReconciliationView() => InitializeComponent();

    private async void NilSalesSelection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
            checkBox.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateSource();

        if (DataContext is GstReconciliationViewModel viewModel)
            await viewModel.AutoSaveNilSalesSelectionsAsync();
    }

    private void OpenJournal_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainWindowViewModel main)
        {
            main.NavigateFromAccountingActivity("Journal");
        }
    }

    private void OpenPurchase_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainWindowViewModel main)
            main.NavigateFromAccountingActivity("Purchase");
    }
}
