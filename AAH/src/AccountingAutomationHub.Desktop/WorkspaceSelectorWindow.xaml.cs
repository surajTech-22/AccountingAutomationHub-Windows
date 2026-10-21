using System.Windows;
using System.Windows.Input;

namespace AccountingAutomationHub.Desktop;

public enum AahWorkspaceKind
{
    Accounting,
    Audit,
    FinancialStatementsIncomeTax
}

public partial class WorkspaceSelectorWindow : Window
{
    public WorkspaceSelectorWindow(Window? placementSource = null)
    {
        InitializeComponent();
        if (placementSource is not null)
        {
            Owner = placementSource;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = placementSource.RestoreBounds.Left;
            Top = placementSource.RestoreBounds.Top;
            Width = Math.Max(MinWidth, placementSource.RestoreBounds.Width);
            Height = Math.Max(MinHeight, placementSource.RestoreBounds.Height);
            if (placementSource.WindowState == WindowState.Maximized)
                WindowState = WindowState.Maximized;
        }
    }

    public AahWorkspaceKind? SelectedWorkspace { get; private set; }

    private void Accounting_Click(object sender, RoutedEventArgs e)
    {
        SelectedWorkspace = AahWorkspaceKind.Accounting;
        DialogResult = true;
    }

    private void Audit_Click(object sender, RoutedEventArgs e)
    {
        SelectedWorkspace = AahWorkspaceKind.Audit;
        DialogResult = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
