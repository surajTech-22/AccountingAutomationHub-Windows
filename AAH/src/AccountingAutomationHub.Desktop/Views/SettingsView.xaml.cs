using System.Windows.Controls;
using System.Linq;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void MappingHeaderFields_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && sender is DataGrid grid)
            viewModel.SetMappingFieldSelection(grid.SelectedItems.Cast<SettingsHeaderFieldRow>());
    }
}
