using System.Windows;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Automation;

namespace AccountingAutomationHub.Desktop.Views;

public partial class PortalCredentialPromptWindow : Window
{
    private readonly AutomationFolderRegistration _folder;

    public PortalCredentialPromptWindow(AutomationFolderRegistration folder)
    {
        _folder = folder;
        InitializeComponent();
        ContextText.Text = $"{folder.ClientName} · FY {folder.FinancialYear}\n{folder.FolderPath}";
        var gst = PortalCredentialStore.Load(folder.ClientId, folder.FinancialYear, PortalLoginKind.Gst);
        var incomeTax = PortalCredentialStore.Load(folder.ClientId, folder.FinancialYear, PortalLoginKind.IncomeTax);
        GstUserIdBox.Text = gst.UserId;
        GstPasswordBox.Password = gst.Password;
        IncomeTaxUserIdBox.Text = incomeTax.UserId;
        IncomeTaxPasswordBox.Password = incomeTax.Password;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var gstUser = GstUserIdBox.Text.Trim();
            var gstPass = GstPasswordBox.Password;
            var taxUser = IncomeTaxUserIdBox.Text.Trim();
            var taxPass = IncomeTaxPasswordBox.Password;
            var gstEntered = gstUser.Length > 0 || gstPass.Length > 0;
            var taxEntered = taxUser.Length > 0 || taxPass.Length > 0;
            if ((gstEntered && (gstUser.Length == 0 || gstPass.Length == 0)) ||
                (taxEntered && (taxUser.Length == 0 || taxPass.Length == 0)))
            {
                StatusText.Text = "Complete both User ID and Password for each portal you want to save. GST and Income Tax are independent.";
                return;
            }
            if (!gstEntered && !taxEntered)
            {
                StatusText.Text = "Enter one complete GST or Income Tax credential pair.";
                return;
            }
            if (gstEntered)
                PortalCredentialStore.Save(_folder.ClientId, _folder.FinancialYear, PortalLoginKind.Gst, gstUser, gstPass);
            if (taxEntered)
                PortalCredentialStore.Save(_folder.ClientId, _folder.FinancialYear, PortalLoginKind.IncomeTax, taxUser, taxPass);
            StatusText.Text = "Saved selected portal credentials to the existing AAH Client/FY Windows Credential Manager entries.";
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = "Credentials could not be saved: " + exception.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
