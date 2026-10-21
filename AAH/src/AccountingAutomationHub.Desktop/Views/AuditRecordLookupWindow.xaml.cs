using System.Windows;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AuditRecordLookupWindow : Window
{
    public AuditRecordLookupWindow(AuditRecordRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        InitializeComponent();
        DataContext = row;
    }
}
