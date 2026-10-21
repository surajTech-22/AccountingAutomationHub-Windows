using System.Windows;
using System.Windows.Controls;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Desktop.Services;

namespace AccountingAutomationHub.Desktop.Views;

public partial class MobileLinkView : UserControl
{
    public MobileLinkView()
    {
        InitializeComponent();
    }

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var message = button.Tag?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(message)) return;
        TransientInfoPopupService.Show(button, "Information", message);
    }

    private void CopyPayload_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MobileLinkViewModel viewModel || string.IsNullOrWhiteSpace(viewModel.PayloadPreview))
            return;

        Clipboard.SetText(viewModel.PayloadPreview);
    }
}
