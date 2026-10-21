using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AccountingAutomationHub.NetworkClient;

public partial class MainWindow : Window
{
    private readonly NetworkClientApi _api=new();
    private readonly List<FileSystemWatcher> _watchers=[];
    private readonly NetworkClientSettings _settings=NetworkClientSettings.Load();
    private readonly DispatcherTimer _heartbeatTimer=new(){Interval=TimeSpan.FromMinutes(10)};
    public MainWindow()
    {
        InitializeComponent(); _api.LoadStoredDevice();
        ServerBox.Text=_settings.Server.Length>0?_settings.Server:ServerBox.Text; UserBox.Text=_settings.UserName;
        _heartbeatTimer.Tick+=async(_,_)=>await Run(async()=>{StatusText.Text=await _api.HeartbeatAsync();foreach(var link in _settings.FolderLinks){var token=WindowsCredentialStore.LoadFolderToken(link.LinkId);if(token.Length>0)await _api.HeartbeatFolderLinkAsync(link.LinkId,token);}});
    }
    private async void Enroll_Click(object sender,RoutedEventArgs e){await Run(async()=>{_api.Server=ServerBox.Text.Trim();var code=EnrollmentCodeBox.Text.Trim();if(string.IsNullOrWhiteSpace(code))throw new InvalidOperationException("Enter the one-use enrollment code from the AAH Main Server.");await _api.EnrollAsync(code,UserBox.Text,PasswordBox.Password,TotpBox.Text,Environment.MachineName);StatusText.Text="Device enrolled. Click Login.";});}
    private async void Login_Click(object sender,RoutedEventArgs e){await Run(async()=>{_api.Server=ServerBox.Text.Trim();_api.LoadStoredDevice();await _api.LoginAsync(UserBox.Text,PasswordBox.Password,TotpBox.Text);_settings.Server=_api.Server;_settings.UserName=UserBox.Text.Trim();_settings.Save();await LoadClients();StatusText.Text=await _api.HeartbeatAsync();StartPersistedFolderLinks();_heartbeatTimer.Start();});}
    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await Run(LoadClients);
    private async Task LoadClients(){ClientList.ItemsSource=await _api.GetClientsAsync();}
    private async void ClientList_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(ClientList.SelectedItem is not NetworkClientApi.ClientInfo c)return;
        try { SummaryBox.Text=await _api.GetSummaryAsync(c.Id); }
        catch(Exception ex) when(ex.Message.Contains("403",StringComparison.OrdinalIgnoreCase))
        {
            SummaryBox.Text="Accounting summary is not permitted for this role. File upload remains available for this authorized Client/FY.";
        }
        catch(Exception ex){StatusText.Text=ex.Message;}
    }
    private async void Upload_Click(object sender,RoutedEventArgs e){if(ClientList.SelectedItem is not NetworkClientApi.ClientInfo c)return;var d=new Microsoft.Win32.OpenFileDialog{Title="Select accounting source file"};if(d.ShowDialog()!=true)return;await Run(async()=>StatusText.Text=await _api.UploadAsync(c.Id,d.FileName));}
    private async void Export_Click(object sender,RoutedEventArgs e){if(ClientList.SelectedItem is not NetworkClientApi.ClientInfo c)return;var kind=((ComboBoxItem)ExportKind.SelectedItem).Content?.ToString()??"bank";await Run(async()=>{var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"AAH Network Exports");var path=await _api.ExportAsync(c.Id,kind,folder);StatusText.Text=$"Export downloaded to {path}. Main Server also retained its own copy.";});}
    private async void Update_Click(object sender,RoutedEventArgs e)
    {
        await Run(async()=>
        {
            var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads");
            StatusText.Text=await _api.DownloadApprovedUpdateAsync(folder);
        });
    }
    private async void LinkFolder_Click(object sender,RoutedEventArgs e)
    {
        if(ClientList.SelectedItem is not NetworkClientApi.ClientInfo c)return;
        using var d=new Forms.FolderBrowserDialog{Description="Choose a local drop folder. New files are uploaded to the Main Server; deleting them locally never deletes the server-retained copy."};
        if(d.ShowDialog()!=Forms.DialogResult.OK)return;
        await Run(async()=>
        {
            var link=await _api.CreateFolderLinkAsync(c.Id,d.SelectedPath);
            WindowsCredentialStore.SaveFolderToken(link.LinkId,link.Token);
            _settings.FolderLinks.RemoveAll(x=>x.LinkId.Equals(link.LinkId,StringComparison.OrdinalIgnoreCase)||x.LocalFolder.Equals(d.SelectedPath,StringComparison.OrdinalIgnoreCase));
            _settings.FolderLinks.Add(new FolderLinkConfig{LinkId=link.LinkId,ClientId=c.Id,LocalFolder=d.SelectedPath,CreatedUtc=DateTime.UtcNow});_settings.Save();
            StartFolderLink(_settings.FolderLinks[^1]);
            StatusText.Text=$"Drop folder linked: {d.SelectedPath}. Server authority expires it after the configured inactivity period; local deletion never deletes the retained server copy.";
        });
    }

    private void StartPersistedFolderLinks()
    {
        foreach(var watcher in _watchers)watcher.Dispose();_watchers.Clear();
        foreach(var link in _settings.FolderLinks.Where(x=>Directory.Exists(x.LocalFolder)))StartFolderLink(link);
    }

    private void StartFolderLink(FolderLinkConfig link)
    {
        if(!Directory.Exists(link.LocalFolder))return;var token=WindowsCredentialStore.LoadFolderToken(link.LinkId);if(string.IsNullOrWhiteSpace(token))return;
        var watcher=new FileSystemWatcher(link.LocalFolder){IncludeSubdirectories=false,EnableRaisingEvents=true,NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite};
        watcher.Created+=async(_,a)=>await TryFolderUpload(link,token,a.FullPath);
        watcher.Renamed+=async(_,a)=>await TryFolderUpload(link,token,a.FullPath);
        _watchers.Add(watcher);
        _=Task.Run(async()=>{foreach(var file in Directory.EnumerateFiles(link.LocalFolder)){await TryFolderUpload(link,token,file);}});
    }

    private async Task TryFolderUpload(FolderLinkConfig link,string token,string path)
    {
        try
        {
            if(!await WaitForStableFileAsync(path))return;
            await _api.UploadAsync(link.ClientId,path,link.LinkId,token);
            Dispatcher.Invoke(()=>StatusText.Text=$"Uploaded from linked drop folder: {Path.GetFileName(path)}. Main Server retained its own copy.");
        }
        catch(Exception ex){Dispatcher.Invoke(()=>StatusText.Text=$"Folder upload attention: {ex.Message}");}
    }

    private static async Task<bool> WaitForStableFileAsync(string path)
    {
        long previousLength=-1;DateTime previousWrite=DateTime.MinValue;var stableChecks=0;
        for(var attempt=0;attempt<12;attempt++)
        {
            await Task.Delay(750);
            if(!File.Exists(path))return false;
            try
            {
                var info=new FileInfo(path);
                if(info.Length==previousLength&&info.LastWriteTimeUtc==previousWrite)stableChecks++;else stableChecks=0;
                previousLength=info.Length;previousWrite=info.LastWriteTimeUtc;
                if(stableChecks>=2)
                {
                    using var probe=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                    return true;
                }
            }
            catch(IOException){stableChecks=0;}
            catch(UnauthorizedAccessException){stableChecks=0;}
        }
        throw new IOException($"File did not become stable within the upload wait window: {Path.GetFileName(path)}");
    }
    private async Task Run(Func<Task> action){try{await action();}catch(Exception ex){StatusText.Text=ex.Message;}}
    protected override void OnClosed(EventArgs e){_heartbeatTimer.Stop();foreach(var w in _watchers)w.Dispose();_api.Dispose();base.OnClosed(e);}
}
