using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Domain.Finance;

namespace AccountingAutomationHub.Desktop.Views;

public partial class FixedAssetsView : UserControl
{
    public FixedAssetsView()
    {
        InitializeComponent();
        Loaded += (_, _) => { FixedAssetSearchFilterBar.Attach(FixedAssetRegisterGrid); FixedAssetSearchFilterBar.RefreshFields(); };
    }

    private void DepreciationCell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FixedAssetRecord asset } && DataContext is FixedAssetsViewModel viewModel)
            viewModel.ShowDepreciationDetails(asset);
    }

    private void FixedAssetRegisterGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not FixedAssetsViewModel viewModel || viewModel.SelectedAsset is null) return;
        if (viewModel.EditAssetCommand.CanExecute(null)) viewModel.EditAssetCommand.Execute(null);
    }

    private void OptionalColumn_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string header } checkBox) return;
        var column = FixedAssetRegisterGrid.Columns.FirstOrDefault(item =>
            string.Equals(Convert.ToString(item.Header), header, StringComparison.OrdinalIgnoreCase));
        if (column is not null)
            column.Visibility = checkBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
}
