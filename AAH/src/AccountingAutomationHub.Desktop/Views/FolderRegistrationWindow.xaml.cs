using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Common;

namespace AccountingAutomationHub.Desktop.Views;

public partial class FolderRegistrationWindow : Window, INotifyPropertyChanged
{
    private string _errorText = string.Empty;
    private string _clientName = string.Empty;
    private string _gstin = string.Empty;
    private string _financialYear = string.Empty;
    private string _lookupStatusText = string.Empty;
    private bool _applyingAutomaticName;
    private bool _clientNameWasEditedManually;
    private bool _lastLookupHadFolderEvidence;
    private string _lastLookupGstin = string.Empty;
    private CancellationTokenSource? _lookupCancellation;
    private readonly Func<string, string, CancellationToken, Task<GstinProfileLookupResult>>? _lookupProfileAsync;
    private readonly Func<IReadOnlyList<ClientGroupFolderRecord>>? _groupProvider;
    private readonly Func<string, Window, string?>? _createGroup;
    private string _selectedGroupId = string.Empty;
    private string _selectedGroupPath = "No Group";

    public FolderRegistrationWindow(
        string folderPath,
        IReadOnlyList<ClientProfile> existingClients,
        long preferredClientId = 0,
        Func<string, string, CancellationToken, Task<GstinProfileLookupResult>>? lookupProfileAsync = null,
        Func<IReadOnlyList<ClientGroupFolderRecord>>? groupProvider = null,
        Func<string, Window, string?>? createGroup = null)
    {
        InitializeComponent();
        FolderPath = folderPath;
        _lookupProfileAsync = lookupProfileAsync;
        _groupProvider = groupProvider;
        _createGroup = createGroup;
        ExistingClients = existingClients
            .Where(client => !client.IsArchived)
            .OrderBy(client => client.ClientName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(client => client.FinancialYear, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        FinancialYears = BuildFinancialYears();
        FinancialYear = GetCurrentFinancialYear();
        DataContext = this;
        Closed += (_, _) =>
        {
            _lookupCancellation?.Cancel();
            _lookupCancellation?.Dispose();
            _lookupCancellation = null;
        };
        Loaded += (_, _) =>
        {
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 40);
            MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width - 40);
            if (ExistingClients.Count > 0)
            {
                var preferredIndex = preferredClientId > 0
                    ? Array.FindIndex(ExistingClients.ToArray(), client => client.Id == preferredClientId)
                    : -1;
                ExistingClientBox.SelectedIndex = preferredIndex >= 0 ? preferredIndex : 0;
                ExistingModeRadio.IsChecked = true;
                ExistingClientBox.Focus();
            }
            else
            {
                NewModeRadio.IsChecked = true;
                NameBox.Focus();
            }
            UpdateModeState();
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SaveRequested;
    public event EventHandler? CancelRequested;

    public string FolderPath { get; }
    public IReadOnlyList<ClientProfile> ExistingClients { get; }
    public ClientProfile? SelectedExistingClient => ExistingClientBox.SelectedItem as ClientProfile;
    public bool LinkExistingClient => ExistingModeRadio.IsChecked == true;
    public bool PendingLinkExistingClient { get; private set; }
    public long PendingExistingClientId { get; private set; }
    public string SelectedGroupId => _selectedGroupId;
    public string SelectedGroupPath
    {
        get => _selectedGroupPath;
        private set
        {
            if (_selectedGroupPath == value) return;
            _selectedGroupPath = value;
            OnPropertyChanged();
        }
    }

    public string ClientName
    {
        get => _clientName;
        set
        {
            var normalized = value ?? string.Empty;
            if (_clientName == normalized) return;
            _clientName = normalized;
            if (!_applyingAutomaticName) _clientNameWasEditedManually = normalized.Trim().Length > 0;
            OnPropertyChanged();
        }
    }

    public string Gstin
    {
        get => _gstin;
        set
        {
            var normalized = new string((value ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
            if (_gstin == normalized) return;
            _gstin = normalized;
            OnPropertyChanged();
            ScheduleProfileLookup();
        }
    }

    public string FinancialYear
    {
        get => _financialYear;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            if (_financialYear == normalized) return;
            _financialYear = normalized;
            OnPropertyChanged();
            if (IsLoaded && Gstin.Length == 15) ScheduleProfileLookup();
        }
    }
    public IReadOnlyList<string> FinancialYears { get; }
    public string ErrorText => _errorText;
    public bool HasError => _errorText.Length > 0;
    public string LookupStatusText
    {
        get => _lookupStatusText;
        private set
        {
            if (_lookupStatusText == value) return;
            _lookupStatusText = value;
            OnPropertyChanged();
        }
    }

    public void ShowError(string message)
    {
        _errorText = message?.Trim() ?? string.Empty;
        NotifyErrorChanged();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        // Capture the user's mode/selection BEFORE raising SaveRequested.
        // The previous implementation refreshed DataContext just before this event.
        // Rebinding ItemsSource could clear ComboBox.SelectedItem, so App.xaml.cs then
        // fell into the new-client path and raised "Name is required" even though
        // the Existing Client radio button visibly remained selected.
        var linkExisting = LinkExistingClient;
        var selectedExisting = SelectedExistingClient;

        if (linkExisting)
        {
            if (selectedExisting is null)
            {
                ShowError("Choose the existing client / Financial Year to link.");
                return;
            }

            PendingLinkExistingClient = true;
            PendingExistingClientId = selectedExisting.Id;
        }
        else
        {
            PendingLinkExistingClient = false;
            PendingExistingClientId = 0;
            ClientName = AccountingText.DisplayName(ClientName);
            Gstin = new string(Gstin.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
            FinancialYear = FinancialYear.Trim();
            if (ClientName.Length == 0 && Gstin.Length == 0)
            {
                ShowError("Enter either Name or GSTIN.");
                return;
            }
            if (Gstin.Length is > 0 and not 15)
            {
                ShowError("GSTIN must be blank or exactly 15 letters/numbers.");
                return;
            }
            if (ClientName.Length == 0 && Gstin.Length == 15)
            {
                await ResolveProfileNameAsync(forceLookup: true);
                ClientName = AccountingText.DisplayName(ClientName);
                var gstinFoundInFolder = _lastLookupHadFolderEvidence &&
                                         _lastLookupGstin.Equals(Gstin, StringComparison.OrdinalIgnoreCase);
                if (ClientName.Length == 0 && !gstinFoundInFolder)
                {
                    ShowError("GSTIN was not found under the configured E-returns Financial Year/downloaded tree or in matching GSTR-3B files. Check the E-returns root/FY or add the matching GSTR-3B source and try again.");
                    return;
                }
            }
            if (!IsFinancialYear(FinancialYear))
            {
                ShowError("Choose or enter Financial Year as YYYY-YY, for example 2025-26.");
                return;
            }
        }

        ClearError();
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void GroupPicker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _groupProvider is null) return;
        var folders = _groupProvider.Invoke() ?? [];
        var menu = new ContextMenu();
        var createTop = new MenuItem { Header = "+ Create New Group" };
        createTop.Click += (_, _) => CreateAndSelectGroup(string.Empty);
        menu.Items.Add(createTop);
        menu.Items.Add(new Separator());
        var noGroup = new MenuItem { Header = "No Group / Ungrouped" };
        noGroup.Click += (_, _) => SelectGroup(string.Empty, "No Group");
        menu.Items.Add(noGroup);
        menu.Items.Add(new Separator());

        var byParent = folders.GroupBy(x => x.ParentId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.Name, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        MenuItem Build(ClientGroupFolderRecord folder, string parentPath)
        {
            var path = string.IsNullOrWhiteSpace(parentPath) ? folder.Name : parentPath + " > " + folder.Name;
            var item = new MenuItem { Header = folder.Name };
            var use = new MenuItem { Header = $"Select {folder.Name}" };
            use.Click += (_, _) => SelectGroup(folder.Id, path);
            item.Items.Add(use);
            var createHere = new MenuItem { Header = "+ Create New Group Here" };
            createHere.Click += (_, _) => CreateAndSelectGroup(folder.Id);
            item.Items.Add(createHere);
            if (byParent.TryGetValue(folder.Id, out var children) && children.Length > 0)
            {
                item.Items.Add(new Separator());
                foreach (var child in children) item.Items.Add(Build(child, path));
            }
            return item;
        }
        if (byParent.TryGetValue(string.Empty, out var roots))
            foreach (var root in roots) menu.Items.Add(Build(root, string.Empty));
        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void CreateAndSelectGroup(string parentId)
    {
        if (_createGroup is null) return;
        var id = _createGroup.Invoke(parentId ?? string.Empty, this);
        if (string.IsNullOrWhiteSpace(id) || _groupProvider is null) return;
        var data = new ClientGroupCatalogData { Folders = _groupProvider.Invoke().Select(x => new ClientGroupFolderRecord { Id=x.Id, Name=x.Name, Type=x.Type, ParentId=x.ParentId, LinkedCaProfileId=x.LinkedCaProfileId }).ToList() };
        SelectGroup(id, ClientGroupCreateDialogService.BuildPath(data, id));
    }

    private void SelectGroup(string id, string path)
    {
        _selectedGroupId = id ?? string.Empty;
        SelectedGroupPath = string.IsNullOrWhiteSpace(path) ? "No Group" : path;
    }

    private void ScheduleProfileLookup()
    {
        _lookupCancellation?.Cancel();
        _lookupCancellation?.Dispose();
        _lookupCancellation = null;

        if (_lookupProfileAsync is null || Gstin.Length != 15)
        {
            _lastLookupHadFolderEvidence = false;
            _lastLookupGstin = string.Empty;
            LookupStatusText = Gstin.Length == 0 ? string.Empty : "Enter a complete 15-character GSTIN.";
            return;
        }

        _lookupCancellation = new CancellationTokenSource();
        var token = _lookupCancellation.Token;
        _ = ResolveProfileNameAfterDelayAsync(token);
    }

    private async Task ResolveProfileNameAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken);
            await ResolveProfileNameAsync(forceLookup: false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Normal while the user continues typing.
        }
    }

    private async Task ResolveProfileNameAsync(bool forceLookup, CancellationToken cancellationToken = default)
    {
        if (_lookupProfileAsync is null || Gstin.Length != 15) return;
        var lookupGstin = Gstin;
        _lastLookupGstin = lookupGstin;
        _lastLookupHadFolderEvidence = false;
        var lookupFinancialYear = FinancialYear.Trim();
        LookupStatusText = "Checking E-returns GSTIN folder first, then matching GSTR-3B files…";
        try
        {
            var profile = await _lookupProfileAsync(lookupGstin, lookupFinancialYear, cancellationToken);
            if (!Gstin.Equals(lookupGstin, StringComparison.OrdinalIgnoreCase) ||
                !FinancialYear.Trim().Equals(lookupFinancialYear, StringComparison.OrdinalIgnoreCase)) return;

            _lastLookupGstin = lookupGstin;
            _lastLookupHadFolderEvidence = profile.SourcePath.Length > 0 &&
                                           profile.Gstin.Equals(lookupGstin, StringComparison.OrdinalIgnoreCase);
            var discoveredName = profile.TradeName.Length > 0 ? profile.TradeName : profile.LegalName;
            if (discoveredName.Length > 0 && (ClientName.Trim().Length == 0 || !_clientNameWasEditedManually))
            {
                _applyingAutomaticName = true;
                try
                {
                    ClientName = AccountingText.DisplayName(discoveredName);
                    _clientNameWasEditedManually = false;
                }
                finally
                {
                    _applyingAutomaticName = false;
                }
            }

            var sourceName = Path.GetFileName(profile.SourcePath);
            if (string.IsNullOrWhiteSpace(sourceName)) sourceName = "GST source";
            LookupStatusText = discoveredName.Length > 0 && _lastLookupHadFolderEvidence
                ? $"GSTIN found and Client Name filled automatically from {sourceName}."
                : _lastLookupHadFolderEvidence
                    ? $"GSTIN found from {sourceName}. AAH checked GSTR-3B too, but no Trade/Legal Name was available yet."
                    : forceLookup
                        ? "GSTIN not found in the configured E-returns FY/downloaded tree or matching GSTR-3B files."
                        : "No matching GSTIN found yet. AAH checks E-returns first, then GSTR-3B.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LookupStatusText = $"E-returns/GSTR-3B GSTIN profile lookup could not complete: {exception.Message}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        CancelRequested?.Invoke(this, EventArgs.Empty);

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        UpdateModeState();
        if (IsLoaded) ClearError();
    }

    private void ExistingClientBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ExistingClientBox.SelectedItem is not null && IsLoaded)
        {
            ExistingModeRadio.IsChecked = true;
            ClearError();
        }
    }

    private void UpdateModeState()
    {
        if (ExistingClientBox is null || NewClientPanel is null) return;
        ExistingClientBox.IsEnabled = ExistingModeRadio.IsChecked == true;
        NewClientPanel.IsEnabled = NewModeRadio.IsChecked == true;
    }

    private void ClearError()
    {
        if (_errorText.Length == 0) return;
        _errorText = string.Empty;
        NotifyErrorChanged();
    }

    private void NotifyErrorChanged()
    {
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static bool IsFinancialYear(string value)
    {
        if (value.Length != 7 || value[4] != '-') return false;
        return int.TryParse(value[..4], out var start)
               && int.TryParse(value[5..], out var end)
               && end == (start + 1) % 100;
    }

    private static IReadOnlyList<string> BuildFinancialYears()
    {
        var current = DateTime.Today.Month >= 4 ? DateTime.Today.Year : DateTime.Today.Year - 1;
        return Enumerable.Range(current - 10, 16)
            .OrderByDescending(start => start)
            .Select(start => $"{start:0000}-{(start + 1) % 100:00}")
            .ToArray();
    }

    private static string GetCurrentFinancialYear()
    {
        var today = DateTime.Today;
        var start = today.Month >= 4 ? today.Year : today.Year - 1;
        return $"{start:0000}-{(start + 1) % 100:00}";
    }
}
