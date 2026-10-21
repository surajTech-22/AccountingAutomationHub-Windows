using System.Windows;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Governance;

namespace AccountingAutomationHub.Desktop.Views;

public partial class QuickLanUserWindow : Window
{
    private readonly ApplicationPreferenceService _settings;
    private readonly ApplicationUserRecord? _editingUser;
    public IReadOnlyList<string> Roles { get; private set; } = Array.Empty<string>();
    public QuickLanUserWindow(ApplicationPreferenceService settings, ApplicationUserRecord? editingUser = null)
    {
        _settings=settings;_editingUser=editingUser;
        Roles = editingUser?.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase)==true
            ? new[]{GovernanceRoles.Administrator}
            : GovernanceRoles.All.Where(x => !x.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase)).ToArray();
        InitializeComponent();DataContext=this;
        if(editingUser is not null)
        {
            Title="Edit AAH User";HeaderBlock.Text="Edit AAH User";HelpBlock.Text="Edit the selected Main Server identity. User ID stays fixed so enrolled devices, Client/FY grants and audit history remain bound correctly. Leave password blank to keep the current password.";
            UserBox.Text=editingUser.WindowsUserName;UserBox.IsReadOnly=true;DisplayBox.Text=editingUser.DisplayName;
            RoleBox.SelectedItem=Roles.FirstOrDefault(x=>x.Equals(editingUser.RoleName,StringComparison.OrdinalIgnoreCase))??Roles.FirstOrDefault();
            ActiveBox.IsChecked=editingUser.IsActive;
            if(editingUser.RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase)){ActiveBox.IsChecked=true;ActiveBox.IsEnabled=false;}
            PasswordHint.Text="Leave password blank to keep the existing password. Enter a new password only when an Administrator intentionally resets it.";SaveButton.Content="Save User Changes";
        }
        else { RoleBox.SelectedIndex=0; ActiveBox.IsChecked=true; }
    }

    private void Save_Click(object sender,RoutedEventArgs e)
    {
        var user=UserBox.Text.Trim();var display=DisplayBox.Text.Trim();var role=RoleBox.SelectedItem?.ToString()??GovernanceRoles.ReadOnly;
        if(user.Length==0){StatusBlock.Text="User ID is required.";return;}
        if(!string.Equals(PasswordBox.Password,ConfirmBox.Password,StringComparison.Ordinal)){StatusBlock.Text="Passwords do not match.";return;}
        if(_editingUser is null && string.IsNullOrEmpty(PasswordBox.Password)){StatusBlock.Text="Password is required when creating a user.";return;}
        try
        {
            var governance=new SqliteGovernanceRepository();governance.Initialize();
            var existing=governance.GetUsers().FirstOrDefault(x=>x.WindowsUserName.Equals(user,StringComparison.OrdinalIgnoreCase));
            var active=_editingUser?.RoleName.Equals(GovernanceRoles.Administrator,StringComparison.OrdinalIgnoreCase)==true||ActiveBox.IsChecked==true;
            governance.SaveUser(new ApplicationUserRecord{Id=existing?.Id??0,WindowsUserName=user,DisplayName=display.Length>0?display:user,RoleName=role,IsActive=active,CreatedUtc=existing?.CreatedUtc??DateTime.UtcNow,UpdatedUtc=DateTime.UtcNow,LastSeenUtc=existing?.LastSeenUtc});
            var security=new ServerSecurityRepository();security.Initialize();
            if(!string.IsNullOrEmpty(PasswordBox.Password))
            {
                var identity=new NetworkIdentityService(_settings,governance,security);identity.ConfigurePassword(user,PasswordBox.Password);
            }
            if(_editingUser is not null && (!role.Equals(_editingUser.RoleName,StringComparison.OrdinalIgnoreCase)||active!=_editingUser.IsActive||!string.IsNullOrEmpty(PasswordBox.Password))) security.DeleteSessionsForUser(user);
            DialogResult=true;
        }
        catch(Exception ex){StatusBlock.Text=ex.Message;}
    }
}
