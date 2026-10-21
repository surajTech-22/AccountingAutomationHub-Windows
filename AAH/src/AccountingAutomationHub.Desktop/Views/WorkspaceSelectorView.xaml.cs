using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccountingAutomationHub.Desktop;

namespace AccountingAutomationHub.Desktop.Views;

public partial class WorkspaceSelectorView : UserControl
{
    public WorkspaceSelectorView()
    {
        InitializeComponent();
    }

    private Window? HostWindow => Window.GetWindow(this);

    private void Accounting_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app) app.SelectWorkspaceFromHost(AahWorkspaceKind.Accounting);
    }

    private void Audit_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app) app.SelectWorkspaceFromHost(AahWorkspaceKind.Audit);
    }

    private void FinancialTax_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app) app.SelectWorkspaceFromHost(AahWorkspaceKind.FinancialStatementsIncomeTax);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var host = HostWindow;
        if (host is null) return;
        if (e.ClickCount == 2)
        {
            host.WindowState = host.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed) host.DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (HostWindow is { } host) host.WindowState = WindowState.Minimized;
    }

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (HostWindow is not { } host) return;
        host.WindowState = host.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app) app.CancelWorkspaceSelectorFromHost();
        else HostWindow?.Close();
    }
}
