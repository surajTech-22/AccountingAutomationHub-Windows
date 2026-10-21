using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class TaxCreditReconciliationView : UserControl
{
    public TaxCreditReconciliationView() => InitializeComponent();

    private void TracesPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is TaxCreditReconciliationViewModel viewModel && sender is PasswordBox box)
            viewModel.PendingTracesPassword = box.Password;
    }

    private void ForgetPassword_Click(object sender, RoutedEventArgs e)
    {
        TracesPasswordBox.Password = string.Empty;
        if (DataContext is TaxCreditReconciliationViewModel viewModel && viewModel.ForgetPasswordCommand.CanExecute(null))
            viewModel.ForgetPasswordCommand.Execute(null);
    }

    private void PartyStatementGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePartySelectionCounts();

    private void PartyBooksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePartySelectionCounts();

    private void EntryStatementGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEntrySelectionCounts();

    private void EntryBooksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEntrySelectionCounts();

    private void PartyStatementGrid_PreviewKeyDown(object sender, KeyEventArgs e) => HandleGridKeyboard(PartyStatementGrid, e, null, () => ExecuteIfPossible((DataContext as TaxCreditReconciliationViewModel)?.LinkSelectedPartyCommand));
    private void PartyBooksGrid_PreviewKeyDown(object sender, KeyEventArgs e) => HandleGridKeyboard(PartyBooksGrid, e, BooksPartySearchBox, () => ExecuteIfPossible((DataContext as TaxCreditReconciliationViewModel)?.LinkSelectedPartyCommand));
    private void EntryStatementGrid_PreviewKeyDown(object sender, KeyEventArgs e) => HandleGridKeyboard(EntryStatementGrid, e, null, () => ExecuteIfPossible((DataContext as TaxCreditReconciliationViewModel)?.PrepareEntryMatchCommand));
    private void EntryBooksGrid_PreviewKeyDown(object sender, KeyEventArgs e) => HandleGridKeyboard(EntryBooksGrid, e, null, () => ExecuteIfPossible((DataContext as TaxCreditReconciliationViewModel)?.PrepareEntryMatchCommand));

    private static void HandleGridKeyboard(DataGrid grid, KeyEventArgs e, Control? searchTarget, Action enterAction)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.A)
        {
            grid.SelectAll(); e.Handled = true; return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.F && searchTarget is not null)
        {
            searchTarget.Focus(); e.Handled = true; return;
        }
        if (e.Key == Key.Escape)
        {
            grid.UnselectAll(); e.Handled = true; return;
        }
        if (e.Key == Key.Space && grid.CurrentItem is not null)
        {
            if (grid.SelectedItems.Contains(grid.CurrentItem)) grid.SelectedItems.Remove(grid.CurrentItem);
            else grid.SelectedItems.Add(grid.CurrentItem);
            e.Handled = true; return;
        }
        if (e.Key == Key.Enter)
        {
            enterAction(); e.Handled = true;
        }
    }

    private static void ExecuteIfPossible(System.Windows.Input.ICommand? command)
    {
        if (command?.CanExecute(null) == true) command.Execute(null);
    }

    private void UpdatePartySelectionCounts()
    {
        if (DataContext is TaxCreditReconciliationViewModel viewModel)
            viewModel.UpdatePartySelectionCounts(PartyStatementGrid.SelectedItems.Count, PartyBooksGrid.SelectedItems.Count);
    }

    private void UpdateEntrySelectionCounts()
    {
        if (DataContext is TaxCreditReconciliationViewModel viewModel)
            viewModel.UpdateEntrySelectionCounts(EntryStatementGrid.SelectedItems.Count, EntryBooksGrid.SelectedItems.Count);
    }
}
