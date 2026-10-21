using System.Windows;
using AccountingAutomationHub.Desktop.Models;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AuditVoucherAllEntriesWindow : Window
{
    public AuditVoucherAllEntriesWindow(AuditAdvancedVoucherWorkbenchModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    public string SelectedSessionId { get; private set; } = string.Empty;

    private void EditVoucher_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string sessionId && !string.IsNullOrWhiteSpace(sessionId))
        {
            SelectedSessionId = sessionId;
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
