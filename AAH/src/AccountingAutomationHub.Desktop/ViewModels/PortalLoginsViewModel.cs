using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using AccountingAutomationHub.Application.Clients;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class PortalLoginsViewModel : PageViewModel
{
    private const string GstPrefix = "PortalLogin.GST.";
    private const string ItPrefix = "PortalLogin.IT.";

    private readonly ActiveClientContext _clientContext;
    private readonly IClientPreferenceRepository _preferences;
    private readonly BackgroundAutomationService _automation;
    private readonly ChromePortalLoginService _chrome = new();

    private string _gstUserId = string.Empty;
    private string _gstPassword = string.Empty;
    private string _incomeTaxUserId = string.Empty;
    private string _incomeTaxPassword = string.Empty;
    private string _gstLastOutcome = string.Empty;
    private string _itLastOutcome = string.Empty;
    private string _gstLastSuccess = string.Empty;
    private string _gstLastFailure = string.Empty;
    private string _itLastSuccess = string.Empty;
    private string _itLastFailure = string.Empty;
    private string _gstMessage = "Save credentials, then choose Login.";
    private string _itMessage = "Save credentials, then choose Login.";
    private string _statusText = "Portal credentials are scoped to the active Client/FY.";

    public PortalLoginsViewModel(
        ActiveClientContext clientContext,
        IClientPreferenceRepository preferences,
        BackgroundAutomationService automation)
        : base("Portal Logins", "GST and Income Tax assisted Chrome login for the active Client/FY")
    {
        _clientContext = clientContext;
        _preferences = preferences;
        _automation = automation;
        SaveGstCommand = new RelayCommand(() => Save(PortalLoginKind.Gst));
        SaveIncomeTaxCommand = new RelayCommand(() => Save(PortalLoginKind.IncomeTax));
        LoginGstCommand = new AsyncRelayCommand(() => LoginAsync(PortalLoginKind.Gst));
        LoginIncomeTaxCommand = new AsyncRelayCommand(() => LoginAsync(PortalLoginKind.IncomeTax));
        OpenCredentialFileCommand = new RelayCommand(OpenCredentialFile);
        RefreshCommand = new RelayCommand(PrepareForDisplay);
        _chrome.SessionStatusChanged += OnSessionStatusChanged;
        _automation.PortalCredentialsChanged += OnPortalCredentialsChanged;
        _clientContext.PropertyChanged += OnClientContextChanged;
        PrepareForDisplay();
    }

    public string GstUserId { get => _gstUserId; set => SetProperty(ref _gstUserId, value ?? string.Empty); }
    public string GstPassword
    {
        get => _gstPassword;
        set { if (SetProperty(ref _gstPassword, value ?? string.Empty)) OnPropertyChanged(nameof(GstSuccessfulPasswordDisplay)); }
    }
    public string IncomeTaxUserId { get => _incomeTaxUserId; set => SetProperty(ref _incomeTaxUserId, value ?? string.Empty); }
    public string IncomeTaxPassword
    {
        get => _incomeTaxPassword;
        set { if (SetProperty(ref _incomeTaxPassword, value ?? string.Empty)) OnPropertyChanged(nameof(ItSuccessfulPasswordDisplay)); }
    }

    public string GstLastOutcome { get => _gstLastOutcome; private set { if (SetProperty(ref _gstLastOutcome, value)) NotifyGstStatus(); } }
    public string ItLastOutcome { get => _itLastOutcome; private set { if (SetProperty(ref _itLastOutcome, value)) NotifyItStatus(); } }
    public string GstLastSuccess { get => _gstLastSuccess; private set { if (SetProperty(ref _gstLastSuccess, value)) OnPropertyChanged(nameof(GstResultCaption)); } }
    public string GstLastFailure { get => _gstLastFailure; private set { if (SetProperty(ref _gstLastFailure, value)) { OnPropertyChanged(nameof(GstResultCaption)); OnPropertyChanged(nameof(GstFailureCaption)); } } }
    public string ItLastSuccess { get => _itLastSuccess; private set { if (SetProperty(ref _itLastSuccess, value)) OnPropertyChanged(nameof(ItResultCaption)); } }
    public string ItLastFailure { get => _itLastFailure; private set { if (SetProperty(ref _itLastFailure, value)) { OnPropertyChanged(nameof(ItResultCaption)); OnPropertyChanged(nameof(ItFailureCaption)); } } }
    public string GstMessage { get => _gstMessage; private set => SetProperty(ref _gstMessage, value); }
    public string ItMessage { get => _itMessage; private set => SetProperty(ref _itMessage, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public bool GstLastSucceeded => GstLastOutcome.Equals("Success", StringComparison.OrdinalIgnoreCase);
    public bool GstLastFailed => GstLastOutcome.Equals("Failure", StringComparison.OrdinalIgnoreCase);
    public bool ItLastSucceeded => ItLastOutcome.Equals("Success", StringComparison.OrdinalIgnoreCase);
    public bool ItLastFailed => ItLastOutcome.Equals("Failure", StringComparison.OrdinalIgnoreCase);
    public string GstStatusSymbol => GstLastSucceeded ? "✓" : GstLastFailed ? "✕" : "•";
    public string ItStatusSymbol => ItLastSucceeded ? "✓" : ItLastFailed ? "✕" : "•";
    public Brush GstStatusBrush => GstLastSucceeded ? Brushes.ForestGreen : GstLastFailed ? Brushes.Firebrick : Brushes.DimGray;
    public Brush ItStatusBrush => ItLastSucceeded ? Brushes.ForestGreen : ItLastFailed ? Brushes.Firebrick : Brushes.DimGray;
    public string GstSuccessfulPasswordDisplay => GstLastSucceeded ? GstPassword : string.Empty;
    public string ItSuccessfulPasswordDisplay => ItLastSucceeded ? IncomeTaxPassword : string.Empty;
    public string GstResultCaption => GstLastSucceeded ? $"Last login: {GstLastSuccess}" : GstLastFailed ? $"Last non-login: {GstLastFailure}" : "No completed login recorded";
    public string ItResultCaption => ItLastSucceeded ? $"Last login: {ItLastSuccess}" : ItLastFailed ? $"Last non-login: {ItLastFailure}" : "No completed login recorded";
    public string GstFailureCaption => GstLastFailed ? $"Last non-login: {GstLastFailure}" : string.Empty;
    public string ItFailureCaption => ItLastFailed ? $"Last non-login: {ItLastFailure}" : string.Empty;

    public RelayCommand SaveGstCommand { get; }
    public RelayCommand SaveIncomeTaxCommand { get; }
    public AsyncRelayCommand LoginGstCommand { get; }
    public AsyncRelayCommand LoginIncomeTaxCommand { get; }
    public RelayCommand OpenCredentialFileCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public void PrepareForDisplay()
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            GstUserId = GstPassword = IncomeTaxUserId = IncomeTaxPassword = string.Empty;
            StatusText = "Open a Client/FY first.";
            return;
        }

        // If the prompt file was edited while AAH was closed or the watcher was paused,
        // import it before showing the protected credential-store values.
        foreach (var folder in _automation.GetFolders().Where(x => x.ClientId == client.Id && x.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)))
        {
            var path = PortalCredentialFileService.PathFor(folder.FolderPath);
            if (File.Exists(path)) PortalCredentialFileService.TryImport(path, client.Id, client.FinancialYear, out _);
        }

        var gst = PortalCredentialStore.Load(client.Id, client.FinancialYear, PortalLoginKind.Gst);
        var it = PortalCredentialStore.Load(client.Id, client.FinancialYear, PortalLoginKind.IncomeTax);
        GstUserId = gst.UserId;
        GstPassword = gst.Password;
        IncomeTaxUserId = it.UserId;
        IncomeTaxPassword = it.Password;
        GstLastOutcome = _preferences.Get(client.Id, GstPrefix + "LastOutcome");
        ItLastOutcome = _preferences.Get(client.Id, ItPrefix + "LastOutcome");
        GstLastSuccess = _preferences.Get(client.Id, GstPrefix + "LastSuccess");
        GstLastFailure = _preferences.Get(client.Id, GstPrefix + "LastFailure");
        ItLastSuccess = _preferences.Get(client.Id, ItPrefix + "LastSuccess");
        ItLastFailure = _preferences.Get(client.Id, ItPrefix + "LastFailure");
        GstMessage = _preferences.Get(client.Id, GstPrefix + "LastMessage");
        ItMessage = _preferences.Get(client.Id, ItPrefix + "LastMessage");
        if (string.IsNullOrWhiteSpace(GstMessage)) GstMessage = "Save credentials, then choose Login.";
        if (string.IsNullOrWhiteSpace(ItMessage)) ItMessage = "Save credentials, then choose Login.";
        StatusText = $"{client.ClientName} • FY {client.FinancialYear}. Chrome fills credentials and stops before final authentication.";
    }

    private void Save(PortalLoginKind portal)
    {
        var client = _clientContext.CurrentClient;
        if (client is null) { StatusText = "Open a Client/FY first."; return; }
        var credential = portal == PortalLoginKind.Gst
            ? new PortalCredential(GstUserId.Trim(), GstPassword)
            : new PortalCredential(IncomeTaxUserId.Trim(), IncomeTaxPassword);
        PortalCredentialStore.Save(client.Id, client.FinancialYear, portal, credential.UserId, credential.Password);
        WritePromptFiles(client.Id, client.FinancialYear);
        StatusText = portal == PortalLoginKind.Gst
            ? "GST credentials saved in Windows Credential Manager and synchronized to linked-folder prompt files."
            : "Income Tax credentials saved in Windows Credential Manager and synchronized to linked-folder prompt files.";
    }

    private async Task LoginAsync(PortalLoginKind portal)
    {
        Save(portal);
        var result = portal == PortalLoginKind.Gst
            ? await _chrome.PrepareAsync(portal, GstUserId, GstPassword).ConfigureAwait(true)
            : await _chrome.PrepareAsync(portal, IncomeTaxUserId, IncomeTaxPassword).ConfigureAwait(true);
        if (portal == PortalLoginKind.Gst) GstMessage = result.Message; else ItMessage = result.Message;
        if (!result.Prepared) RecordResult(portal, false, result.Message, result.TimestampUtc);
    }

    private void OnSessionStatusChanged(object? sender, PortalLoginSessionResult result)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (result.Portal == PortalLoginKind.Gst) GstMessage = result.Message; else ItMessage = result.Message;
            if (result.LoginSucceeded.HasValue)
                RecordResult(result.Portal, result.LoginSucceeded.Value, result.Message, result.TimestampUtc);
        }));
    }

    private void RecordResult(PortalLoginKind portal, bool success, string message, DateTime utc)
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var prefix = portal == PortalLoginKind.Gst ? GstPrefix : ItPrefix;
        var stamp = utc.ToLocalTime().ToString("dd-MMM-yyyy hh:mm:ss tt");
        _preferences.Set(client.Id, prefix + "LastOutcome", success ? "Success" : "Failure");
        _preferences.Set(client.Id, prefix + (success ? "LastSuccess" : "LastFailure"), stamp);
        _preferences.Set(client.Id, prefix + "LastMessage", message ?? string.Empty);
        if (portal == PortalLoginKind.Gst)
        {
            GstLastOutcome = success ? "Success" : "Failure";
            if (success) GstLastSuccess = stamp; else GstLastFailure = stamp;
            GstMessage = message ?? string.Empty;
        }
        else
        {
            ItLastOutcome = success ? "Success" : "Failure";
            if (success) ItLastSuccess = stamp; else ItLastFailure = stamp;
            ItMessage = message ?? string.Empty;
        }
    }

    private void WritePromptFiles(long clientId, string financialYear)
    {
        var gst = PortalCredentialStore.Load(clientId, financialYear, PortalLoginKind.Gst);
        var it = PortalCredentialStore.Load(clientId, financialYear, PortalLoginKind.IncomeTax);
        foreach (var folder in _automation.GetFolders().Where(x => x.ClientId == clientId && x.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase)))
        {
            try { PortalCredentialFileService.WriteForFolder(folder, gst, it); }
            catch { }
        }
    }

    private void OpenCredentialFile()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) { StatusText = "Open a Client/FY first."; return; }
        var folder = _automation.GetFolders().FirstOrDefault(x => x.ClientId == client.Id && x.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase));
        if (folder is null || !Directory.Exists(folder.FolderPath)) { StatusText = "This Client/FY has no available linked Automation Folder."; return; }
        PortalCredentialFileService.EnsureFile(folder);
        var path = PortalCredentialFileService.ShortcutPathFor(folder.FolderPath);
        try
        {
            if (!File.Exists(path))
            {
                StatusText = "The independent linked-folder credential shortcut is unavailable. Check folder write permissions.";
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
            StatusText = "Opened the independent linked-folder portal credential editor.";
        }
        catch (Exception exception) { StatusText = "Credential prompt file could not be opened: " + exception.Message; }
    }

    private void OnPortalCredentialsChanged(object? sender, PortalCredentialsChangedEventArgs e)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != e.ClientId || !client.FinancialYear.Equals(e.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(PrepareForDisplay));
    }

    private void OnClientContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveClientContext.CurrentClient)) PrepareForDisplay();
    }

    private void NotifyGstStatus()
    {
        OnPropertyChanged(nameof(GstLastSucceeded));
        OnPropertyChanged(nameof(GstLastFailed));
        OnPropertyChanged(nameof(GstStatusSymbol));
        OnPropertyChanged(nameof(GstStatusBrush));
        OnPropertyChanged(nameof(GstSuccessfulPasswordDisplay));
        OnPropertyChanged(nameof(GstResultCaption));
        OnPropertyChanged(nameof(GstFailureCaption));
    }

    private void NotifyItStatus()
    {
        OnPropertyChanged(nameof(ItLastSucceeded));
        OnPropertyChanged(nameof(ItLastFailed));
        OnPropertyChanged(nameof(ItStatusSymbol));
        OnPropertyChanged(nameof(ItStatusBrush));
        OnPropertyChanged(nameof(ItSuccessfulPasswordDisplay));
        OnPropertyChanged(nameof(ItResultCaption));
        OnPropertyChanged(nameof(ItFailureCaption));
    }
}
