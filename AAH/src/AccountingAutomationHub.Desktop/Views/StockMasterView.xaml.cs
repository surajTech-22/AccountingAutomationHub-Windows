using System.Windows.Controls;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class StockMasterView : UserControl
{
    public StockMasterView() => InitializeComponent();

    private void StockTree_SelectedItemChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is StockMasterViewModel viewModel && e.NewValue is MasterTreeNode node)
            viewModel.SelectedSection = node.NodeKind == "Root" && node.Children.Count > 0 ? node.Children[0] : node;
    }
}
