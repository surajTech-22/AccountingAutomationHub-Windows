using System.Windows;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Infrastructure.Governance;

namespace AccountingAutomationHub.Desktop.Views;

public partial class NetworkCredentialSetupWindow : Window
{
    private readonly NetworkIdentityService _identity;

    public NetworkCredentialSetupWindow(string userName, ApplicationPreferenceService settings)
    {
        InitializeComponent();
        UserName = userName.Trim();
        var governance = new SqliteGovernanceRepository(); governance.Initialize();
        var security = new ServerSecurityRepository(); security.Initialize();
        _identity = new NetworkIdentityService(settings, governance, security);
        StatusText = "Create the password here, then share the User ID and password with the authorised AAH user through your normal secure office process.";
        DataContext = this;
    }

    public string UserName { get; }
    public string StatusText { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(PasswordBox.Password, ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            StatusText = "Passwords do not match."; DataContext = null; DataContext = this; return;
        }
        try
        {
            _identity.ConfigurePassword(UserName, PasswordBox.Password);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText = exception.Message; DataContext = null; DataContext = this;
        }
    }
}
