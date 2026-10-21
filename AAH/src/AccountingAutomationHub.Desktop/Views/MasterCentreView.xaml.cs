using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Desktop.Views;

public partial class MasterCentreView : UserControl
{
    public MasterCentreView() => InitializeComponent();

    private void MetricTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border &&
            border.Tag is string metric &&
            DataContext is MasterCentreViewModel viewModel)
        {
            viewModel.ApplyMetricFilter(metric);
            e.Handled = true;
        }
    }


    private void MasterGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && DataContext is MasterCentreViewModel viewModel)
            viewModel.SetGridSelection(grid.SelectedItems.OfType<MasterRecord>());
    }

    private void MasterTree_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MasterCentreViewModel viewModel &&
            e.NewValue is MasterTreeNode node)
        {
            viewModel.SelectedTreeNode = node;
        }
    }

    private void MasterCentreView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && MasterGrid.IsKeyboardFocusWithin)
            CancelMasterEdit(e);
    }

    private void MasterGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CancelMasterEdit(e);
    }

    private void MasterEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CancelMasterEdit(e);
    }

    private void CancelMasterEdit(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is DependencyObject focused)
        {
            var combo = FindVisualParent<ComboBox>(focused);
            if (combo is not null) combo.IsDropDownOpen = false;
        }

        MasterGrid.CancelEdit(DataGridEditingUnit.Cell);
        MasterGrid.CancelEdit(DataGridEditingUnit.Row);
        MasterGrid.Focus();
        e.Handled = true;
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
