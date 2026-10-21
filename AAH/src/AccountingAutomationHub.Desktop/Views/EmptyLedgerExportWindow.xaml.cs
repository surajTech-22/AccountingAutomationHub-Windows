using System.Windows;
using System.Windows.Input;

namespace AccountingAutomationHub.Desktop.Views;

public partial class EmptyLedgerExportWindow : Window
{
    public EmptyLedgerExportWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }
}
