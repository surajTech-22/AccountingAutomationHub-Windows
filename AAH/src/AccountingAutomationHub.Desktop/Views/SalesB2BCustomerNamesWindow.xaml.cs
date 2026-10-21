using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class SalesB2BCustomerNamesWindow : Window
{
    private bool _committingName;

    public SalesB2BCustomerNamesWindow(SalesImportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.B2BSearchText = string.Empty;
        Loaded += (_, _) => viewModel.BeginB2BCustomerRenameSession();
        Closed += (_, _) => viewModel.EndB2BCustomerRenameSession();
    }

    private async void CustomerNameEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await CommitCustomerNameAsync(sender as TextBox);
        e.Handled = true;
    }

    private async void CustomerNameEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        await CommitCustomerNameAsync(sender as TextBox);

    private async Task CommitCustomerNameAsync(TextBox? editor)
    {
        if (_committingName || editor?.DataContext is not SalesB2BCustomerMatchRow row || DataContext is not SalesImportViewModel viewModel)
            return;

        var name = editor.Text?.Trim() ?? string.Empty;
        if (name.Length == 0) return;
        if (row.IsMatched && string.Equals(name, row.CustomerName, StringComparison.Ordinal)) return;

        try
        {
            _committingName = true;
            if (await viewModel.SaveB2BCustomerNameFromPanelAsync(row.Gstin, name))
                Keyboard.ClearFocus();
        }
        finally
        {
            _committingName = false;
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SalesImportViewModel viewModel)
            return;

        viewModel.B2BSearchText = string.Empty;
    }

    private void DeleteAllManualNames_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SalesImportViewModel viewModel || !viewModel.CanEditSales)
            return;

        var answer = MessageBox.Show(
            this,
            "Delete all manually entered B2B customer names for the current Client and Financial Year?\n\nOriginal Recipient/Trade names will be restored where available. Customer Master records, GST source files and imported transactions will not be deleted.",
            "Delete All Manual B2B Names",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes)
            viewModel.DeleteAllB2BManualNames();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
