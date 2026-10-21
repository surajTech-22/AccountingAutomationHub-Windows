using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class FinancialStatementsView : UserControl
{
    private bool _sourceColumnsHidden;
    private bool _splitColumnsHidden;

    public FinancialStatementsView() => InitializeComponent();

    private void TbMappingInfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target) return;
        var summary = DataContext is FinancialStatementsViewModel vm ? vm.CrossYearMappingSummary : string.Empty;
        var message = "Statement and Nature are shown separately from Mapping Details. TallyPrime Group -> C1 (FS Heads) -> C2 (Notes Heads) -> C3 (Notes Sub-Heads)." +
                      (string.IsNullOrWhiteSpace(summary) ? string.Empty : $"\n\n{summary}");
        TransientInfoPopupService.Show(target, "Mapping Information", message);
    }

    private void MappingComboBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsDropDownOpen) return;
        SelectContainingTrialBalanceRow(combo);
        combo.Focus();
        combo.IsDropDownOpen = true;
        e.Handled = true;
    }

    private void MappingComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsDropDownOpen) return;
        if (e.Key is not (Key.Enter or Key.Space or Key.Down or Key.F4)) return;
        SelectContainingTrialBalanceRow(combo);
        combo.Focus();
        combo.IsDropDownOpen = true;
        e.Handled = true;
    }

    private static void SelectContainingTrialBalanceRow(DependencyObject element)
    {
        var current = element;
        DataGridRow? row = null;
        DataGrid? grid = null;
        while (current is not null)
        {
            if (row is null && current is DataGridRow foundRow) row = foundRow;
            if (current is DataGrid foundGrid) { grid = foundGrid; break; }
            current = VisualTreeHelper.GetParent(current);
        }
        if (row is null || grid is null) return;
        row.IsSelected = true;
        grid.SelectedItem = row.Item;
        grid.CurrentItem = row.Item;
    }

    private void ToggleSourceColumns_Click(object sender, RoutedEventArgs e)
    {
        _sourceColumnsHidden = !_sourceColumnsHidden;
        var visibility = _sourceColumnsHidden ? Visibility.Collapsed : Visibility.Visible;
        foreach (var column in new DataGridColumn[] { FyParentGroupColumn, FySourceNatureColumn, FyOpeningColumn, FyDebitColumn, FyCreditColumn, PyParentGroupColumn, PySourceNatureColumn, PyOpeningColumn, PyDebitColumn, PyCreditColumn })
            column.Visibility = visibility;
        ToggleSourceColumnsButton.Content = _sourceColumnsHidden ? "Show Source" : "Hide Source";
    }

    private void ToggleSplitColumns_Click(object sender, RoutedEventArgs e)
    {
        _splitColumnsHidden = !_splitColumnsHidden;
        var visibility = _splitColumnsHidden ? Visibility.Collapsed : Visibility.Visible;
        foreach (var column in new DataGridColumn[] { FyFirstSplitColumn, FySecondSplitColumn, PyFirstSplitColumn, PySecondSplitColumn })
            column.Visibility = visibility;
        ToggleSplitColumnsButton.Content = _splitColumnsHidden ? "Show Split" : "Hide Split";
    }

}
