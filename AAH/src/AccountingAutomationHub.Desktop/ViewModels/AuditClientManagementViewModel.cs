using System.Collections.ObjectModel;
using System.Net.Mail;
using System.Windows;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Application.Settings;
using AccountingAutomationHub.Domain.Audit;
using AccountingAutomationHub.Infrastructure.Audit;

namespace AccountingAutomationHub.Desktop.ViewModels;

/// <summary>
/// Audit-owned Client Directory. It deliberately mirrors the Accounting client-management
/// workflow while persisting only to audit-hub.db.
/// </summary>
public sealed class AuditClientManagementViewModel : ViewModelBase
{
    private const string AllFinancialYearsFilter = "All Financial Years";
    private const string AuditGroupPreferenceKey = "Audit.ClientGroups.V1";
    private const string AuditGroupModePreferenceKey = "Audit.ClientGroups.Mode";
    private const string AuditCurrentGroupPreferenceKey = "Audit.ClientGroups.Current";
    private readonly SqliteAuditRepository _repository;
    private readonly IApplicationPreferenceRepository _applicationPreferences;
    private readonly List<AuditClientProfile> _allClients = [];

    private AuditClientProfile? _selectedClient;
    private string _searchText = string.Empty;
    private bool _includeArchived;
    private string _selectedFinancialYearFilter = AllFinancialYearsFilter;
    private string _sortColumn = "ClientCode";
    private bool _sortAscending = true;
    private bool _isEditorOpen;
    private long _editingId;
    private bool _editingArchived;
    private string _clientCode = string.Empty;
    private string _clientName = string.Empty;
    private string _legalName = string.Empty;
    private string _tradeName = string.Empty;
    private string _gstin = string.Empty;
    private string _pan = string.Empty;
    private string _state = string.Empty;
    private string _financialYear = string.Empty;
    private string _address = string.Empty;
    private string _phone = string.Empty;
    private string _email = string.Empty;
    private string _notes = string.Empty;
    private ClientGroupCatalogData _groupCatalog = new();
    private AuditClientGroupRow? _selectedGroup;
    private bool _isGroupWiseDirectory;
    private string _currentGroupId = string.Empty;
    private string _groupNameDraft = string.Empty;
    private string _selectedGroupType = ClientGroupFolderTypes.CompanyGroup;
    private string _statusText = "Create an Audit client or select an existing Audit client.";
    private bool _isGroupManagerOpen;
    private bool _isGroupFolderEditorOpen;
    private AuditClientGroupFolderNode? _selectedGroupFolder;
    private AuditClientGroupSourceItem? _selectedGroupSourceItem;
    private string _groupManagerStatus = "Select an ungrouped Audit client or top-level Group, then move it into the shared hierarchy.";
    private string _validationText = string.Empty;

    public AuditClientManagementViewModel(SqliteAuditRepository repository, IApplicationPreferenceRepository applicationPreferences)
    {
        _repository = repository;
        _applicationPreferences = applicationPreferences;
        _repository.Initialize();
        _applicationPreferences.Initialize();

        NewCommand = new RelayCommand(BeginNew);
        SaveCommand = new RelayCommand(Save);
        SetCurrentCommand = new RelayCommand(SetCurrent, () => SelectedClient is not null && !SelectedClient.IsArchived);
        EditSelectedCommand = new RelayCommand(EditSelected, () => SelectedClient is not null);
        CloseEditorCommand = new RelayCommand(CloseEditor);
        ArchiveCommand = new RelayCommand(Archive, () => EditingId > 0 && !EditingArchived);
        RestoreCommand = new RelayCommand(Restore, () => EditingId > 0 && EditingArchived);
        ReloadCommand = new RelayCommand(() => LoadClients());
        ToggleSortCommand = new ParameterRelayCommand(parameter => ToggleSort(parameter?.ToString()));
        NewGroupCommand = new RelayCommand(CreateGroup);
        OpenGroupManagerCommand = new RelayCommand(OpenGroupManager);
        CloseGroupManagerCommand = new RelayCommand(CloseGroupManager);
        BeginAddGroupFolderCommand = new RelayCommand(BeginAddGroupFolder);
        SaveGroupFolderCommand = new RelayCommand(SaveGroupFolder);
        CancelGroupFolderCommand = new RelayCommand(CancelGroupFolder);
        DeleteGroupFolderCommand = new RelayCommand(DeleteSelectedGroupFolder, () => SelectedGroupFolder is not null);
        MoveSelectedGroupSourceCommand = new RelayCommand(MoveSelectedGroupSource, () => SelectedGroupSourceItem is not null && SelectedGroupFolder is not null);
        ToggleClientDirectoryViewCommand = new RelayCommand(() => IsGroupWiseDirectory = !IsGroupWiseDirectory);
        RenameGroupCommand = new RelayCommand(RenameSelectedGroup, () => SelectedGroup is not null);
        DeleteGroupCommand = new RelayCommand(DeleteSelectedGroup, () => SelectedGroup is not null);
        OpenGroupCommand = new RelayCommand(OpenSelectedGroup, () => SelectedGroup is not null);
        BackGroupCommand = new RelayCommand(NavigateGroupBack, () => CurrentGroupId.Length > 0);
        MoveClientHereCommand = new RelayCommand(MoveSelectedClientHere, () => IsGroupWiseDirectory && SelectedClient is not null);
        UngroupClientCommand = new RelayCommand(UngroupSelectedClient, () => SelectedClient is not null);

        GroupTypes.Add(ClientGroupFolderTypes.Firm);
        GroupTypes.Add(ClientGroupFolderTypes.Company);
        GroupTypes.Add(ClientGroupFolderTypes.Ca);
        GroupTypes.Add(ClientGroupFolderTypes.Auditor);
        GroupTypes.Add(ClientGroupFolderTypes.CompanyGroup);
        LoadGroupCatalog();
        BuildFinancialYearOptions();
        LoadClients();
        IsEditorOpen = false;
    }

    public event Action<AuditClientProfile?>? CurrentClientChanged;
    public event EventHandler? DirectoryChanged;

    public ObservableCollection<AuditClientProfile> Clients { get; } = [];
    public ObservableCollection<string> FinancialYearOptions { get; } = [];
    public ObservableCollection<string> FinancialYearFilters { get; } = [];
    public ObservableCollection<AuditClientGroupRow> GroupRows { get; } = [];
    public ObservableCollection<string> GroupTypes { get; } = [];
    public ObservableCollection<AuditClientGroupFolderNode> GroupFolderRoots { get; } = [];
    public ObservableCollection<AuditClientGroupSourceItem> GroupSourceItems { get; } = [];

    public AuditClientProfile? SelectedClient
    {
        get => _selectedClient;
        set
        {
            if (!SetProperty(ref _selectedClient, value)) return;
            RaiseCommands();
        }
    }

    public AuditClientGroupRow? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value)) return;
            RenameGroupCommand.RaiseCanExecuteChanged();
            DeleteGroupCommand.RaiseCanExecuteChanged();
            OpenGroupCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsGroupManagerOpen
    {
        get => _isGroupManagerOpen;
        private set => SetProperty(ref _isGroupManagerOpen, value);
    }

    public bool IsGroupFolderEditorOpen
    {
        get => _isGroupFolderEditorOpen;
        private set => SetProperty(ref _isGroupFolderEditorOpen, value);
    }

    public AuditClientGroupFolderNode? SelectedGroupFolder
    {
        get => _selectedGroupFolder;
        set
        {
            if (!SetProperty(ref _selectedGroupFolder, value)) return;
            OnPropertyChanged(nameof(SelectedGroupPath));
            DeleteGroupFolderCommand.RaiseCanExecuteChanged();
            MoveSelectedGroupSourceCommand.RaiseCanExecuteChanged();
        }
    }

    public AuditClientGroupSourceItem? SelectedGroupSourceItem
    {
        get => _selectedGroupSourceItem;
        set
        {
            if (!SetProperty(ref _selectedGroupSourceItem, value)) return;
            MoveSelectedGroupSourceCommand.RaiseCanExecuteChanged();
        }
    }

    public string SelectedGroupPath => SelectedGroupFolder?.Path ?? "Select destination Group";
    public string GroupManagerStatus { get => _groupManagerStatus; private set => SetProperty(ref _groupManagerStatus, value); }

    public bool IsGroupWiseDirectory
    {
        get => _isGroupWiseDirectory;
        set
        {
            if (!SetProperty(ref _isGroupWiseDirectory, value)) return;
            OnPropertyChanged(nameof(IsClientWiseDirectory));
            _repository.SaveWorkspaceSetting(AuditGroupModePreferenceKey, value ? "Group" : "Client");
            RefreshGroupDirectory();
            ApplyFilter();
            RaiseCommands();
        }
    }

    public bool IsClientWiseDirectory
    {
        get => !IsGroupWiseDirectory;
        set { if (value) IsGroupWiseDirectory = false; }
    }

    public string CurrentGroupId
    {
        get => _currentGroupId;
        private set
        {
            if (!SetProperty(ref _currentGroupId, value ?? string.Empty)) return;
            _repository.SaveWorkspaceSetting(AuditCurrentGroupPreferenceKey, _currentGroupId);
            OnPropertyChanged(nameof(CurrentGroupPath));
            BackGroupCommand.RaiseCanExecuteChanged();
        }
    }

    public string CurrentGroupPath => BuildCurrentGroupPath();
    public string GroupNameDraft { get => _groupNameDraft; set => SetProperty(ref _groupNameDraft, value); }
    public string SelectedGroupType { get => _selectedGroupType; set => SetProperty(ref _selectedGroupType, ClientGroupFolderTypes.Normalize(value)); }
    public int GroupCount => _groupCatalog.Folders.Count;

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilter(); }
    }

    public bool IncludeArchived
    {
        get => _includeArchived;
        set { if (SetProperty(ref _includeArchived, value)) ApplyFilter(); }
    }

    public string SelectedFinancialYearFilter
    {
        get => _selectedFinancialYearFilter;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? AllFinancialYearsFilter : value;
            if (SetProperty(ref _selectedFinancialYearFilter, normalized)) ApplyFilter();
        }
    }

    public bool IsEditorOpen
    {
        get => _isEditorOpen;
        private set => SetProperty(ref _isEditorOpen, value);
    }

    public long EditingId
    {
        get => _editingId;
        private set { if (SetProperty(ref _editingId, value)) OnPropertyChanged(nameof(EditorModeText)); }
    }

    public bool EditingArchived
    {
        get => _editingArchived;
        private set
        {
            if (!SetProperty(ref _editingArchived, value)) return;
            OnPropertyChanged(nameof(EditorStatusText));
            RaiseCommands();
        }
    }

    public string ClientCode { get => _clientCode; set => SetProperty(ref _clientCode, value); }
    public string ClientName { get => _clientName; set => SetProperty(ref _clientName, value); }
    public string LegalName { get => _legalName; set => SetProperty(ref _legalName, value); }
    public string TradeName { get => _tradeName; set => SetProperty(ref _tradeName, value); }
    public string Pan { get => _pan; set => SetProperty(ref _pan, (value ?? string.Empty).Trim().ToUpperInvariant()); }
    public string State { get => _state; set => SetProperty(ref _state, value); }
    public string FinancialYear { get => _financialYear; set => SetProperty(ref _financialYear, value); }
    public string Address { get => _address; set => SetProperty(ref _address, value); }
    public string Phone { get => _phone; set => SetProperty(ref _phone, value); }
    public string Email { get => _email; set => SetProperty(ref _email, value); }
    public string Notes { get => _notes; set => SetProperty(ref _notes, value); }

    public string Gstin
    {
        get => _gstin;
        set
        {
            var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (!SetProperty(ref _gstin, normalized)) return;
            if (normalized.Length == 15)
            {
                if (string.IsNullOrWhiteSpace(Pan)) Pan = normalized.Substring(2, 10);
                if (string.IsNullOrWhiteSpace(State) && StateByCode.TryGetValue(normalized[..2], out var state)) State = state;
            }
        }
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ValidationText
    {
        get => _validationText;
        private set
        {
            if (!SetProperty(ref _validationText, value)) return;
            OnPropertyChanged(nameof(HasValidationError));
        }
    }

    public bool HasValidationError => ValidationText.Length > 0;
    public string EditorModeText => EditingId == 0 ? "Add new Audit client" : "Edit Audit client";
    public string EditorStatusText => EditingArchived ? "Archived Audit profile" : "Audit-owned profile · separate from Accounting";
    public int TotalCount => _allClients.Count;
    public int ActiveCount => _allClients.Count(x => !x.IsArchived);
    public int ArchivedCount => _allClients.Count(x => x.IsArchived);
    public int GstinCount => _allClients.Count(x => !x.IsArchived && x.Gstin.Length == 15);
    public int NeedsProfileCount => _allClients.Count(x => !x.IsArchived && (x.LegalName.Length == 0 || x.TradeName.Length == 0));
    public int VisibleCount => Clients.Count;
    public string SortDescriptionText => $"Sorted by {SortCaption(_sortColumn)} {(_sortAscending ? "↑" : "↓")}";
    public string ClientCodeSortIndicator => SortIndicator("ClientCode");
    public string NameSortIndicator => SortIndicator("Name");
    public string GstinSortIndicator => SortIndicator("Gstin");
    public string StateSortIndicator => SortIndicator("State");
    public string FinancialYearSortIndicator => SortIndicator("FinancialYear");
    public string UpdatedSortIndicator => SortIndicator("Updated");

    public RelayCommand NewCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SetCurrentCommand { get; }
    public RelayCommand EditSelectedCommand { get; }
    public RelayCommand CloseEditorCommand { get; }
    public RelayCommand ArchiveCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public ParameterRelayCommand ToggleSortCommand { get; }
    public RelayCommand NewGroupCommand { get; }
    public RelayCommand OpenGroupManagerCommand { get; }
    public RelayCommand CloseGroupManagerCommand { get; }
    public RelayCommand BeginAddGroupFolderCommand { get; }
    public RelayCommand SaveGroupFolderCommand { get; }
    public RelayCommand CancelGroupFolderCommand { get; }
    public RelayCommand DeleteGroupFolderCommand { get; }
    public RelayCommand MoveSelectedGroupSourceCommand { get; }
    public RelayCommand ToggleClientDirectoryViewCommand { get; }
    public RelayCommand RenameGroupCommand { get; }
    public RelayCommand DeleteGroupCommand { get; }
    public RelayCommand OpenGroupCommand { get; }
    public RelayCommand BackGroupCommand { get; }
    public RelayCommand MoveClientHereCommand { get; }
    public RelayCommand UngroupClientCommand { get; }

    public void Reload(long preferredId = 0) => LoadClients(preferredId);

    public void BeginNew()
    {
        EditingId = 0;
        EditingArchived = false;
        ClientCode = GenerateNextClientCode();
        ClientName = string.Empty;
        LegalName = string.Empty;
        TradeName = string.Empty;
        Gstin = string.Empty;
        Pan = string.Empty;
        State = string.Empty;
        FinancialYear = DefaultFinancialYear();
        Address = string.Empty;
        Phone = string.Empty;
        Email = string.Empty;
        Notes = string.Empty;
        ValidationText = string.Empty;
        IsEditorOpen = true;
        RaiseCommands();
    }

    public void BeginEdit(AuditClientProfile client)
    {
        ArgumentNullException.ThrowIfNull(client);
        EditingId = client.Id;
        EditingArchived = client.IsArchived;
        ClientCode = client.ClientCode;
        ClientName = client.ClientName;
        LegalName = client.LegalName;
        TradeName = client.TradeName;
        Gstin = client.Gstin;
        Pan = client.Pan;
        State = client.State;
        FinancialYear = client.FinancialYear;
        Address = client.Address;
        Phone = client.Phone;
        Email = client.Email;
        Notes = client.Notes;
        ValidationText = string.Empty;
        IsEditorOpen = true;
        RaiseCommands();
    }

    private void EditSelected()
    {
        if (SelectedClient is not null) BeginEdit(SelectedClient);
    }

    private void Save()
    {
        ValidationText = ValidateEditor();
        if (ValidationText.Length > 0) return;

        try
        {
            var existing = EditingId > 0 ? _repository.GetClient(EditingId) : null;
            var profile = existing?.Clone() ?? new AuditClientProfile();
            profile.Id = EditingId;
            profile.ClientCode = ClientCode.Trim();
            profile.ClientName = ClientName.Trim();
            profile.LegalName = LegalName.Trim();
            profile.TradeName = TradeName.Trim();
            profile.Gstin = Gstin.Trim().ToUpperInvariant();
            profile.Pan = Pan.Trim().ToUpperInvariant();
            profile.State = State.Trim();
            profile.FinancialYear = NormalizeFinancialYear(FinancialYear);
            profile.Address = Address.Trim();
            profile.Phone = Phone.Trim();
            profile.Email = Email.Trim();
            profile.Notes = Notes.Trim();
            profile.IsArchived = EditingArchived;

            var saved = _repository.SaveClient(profile);
            _repository.SetCurrentClient(saved.Id);
            IsEditorOpen = false;
            LoadClients(saved.Id);
            StatusText = $"Audit client '{saved.ClientName}' saved and opened for FY {saved.FinancialYear}.";
            CurrentClientChanged?.Invoke(saved);
            DirectoryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ValidationText = ex.Message;
        }
    }

    private void SetCurrent()
    {
        if (SelectedClient is null || SelectedClient.IsArchived) return;
        _repository.SetCurrentClient(SelectedClient.Id);
        StatusText = $"Audit workspace opened for {SelectedClient.ClientName} · FY {SelectedClient.FinancialYear}.";
        CurrentClientChanged?.Invoke(SelectedClient.Clone());
    }

    private void Archive()
    {
        if (EditingId <= 0 || EditingArchived) return;
        if (MessageBox.Show($"Archive Audit client '{ClientName}'?\n\nOnly the Audit client is archived. Accounting is not changed.", "Archive Audit Client", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _repository.ArchiveClient(EditingId, true);
        IsEditorOpen = false;
        LoadClients();
        DirectoryChanged?.Invoke(this, EventArgs.Empty);
        CurrentClientChanged?.Invoke(_repository.GetCurrentClient());
    }

    private void Restore()
    {
        if (EditingId <= 0 || !EditingArchived) return;
        _repository.ArchiveClient(EditingId, false);
        IsEditorOpen = false;
        LoadClients(EditingId);
        DirectoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CloseEditor()
    {
        IsEditorOpen = false;
        ValidationText = string.Empty;
    }

    private void LoadClients(long preferredId = 0)
    {
        SynchronizeSharedGroupHierarchy();
        var selectedId = preferredId > 0 ? preferredId : SelectedClient?.Id ?? _repository.GetCurrentClientId();
        _allClients.Clear();
        _allClients.AddRange(_repository.GetClients(includeArchived: true));
        RebuildFinancialYearFilters();
        RefreshGroupDirectory();
        RebuildGroupManager();
        ApplyFilter();
        SelectedClient = Clients.FirstOrDefault(x => x.Id == selectedId) ?? Clients.FirstOrDefault();
        RaiseCountProperties();
        StatusText = _allClients.Count == 0
            ? "No Audit clients have been created."
            : $"{ActiveCount:N0} active Audit client(s), {ArchivedCount:N0} archived.";
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        IEnumerable<AuditClientProfile> query = _allClients.Where(client =>
            (IncludeArchived || !client.IsArchived) &&
            (SelectedFinancialYearFilter == AllFinancialYearsFilter || client.FinancialYear.Equals(SelectedFinancialYearFilter, StringComparison.OrdinalIgnoreCase)) &&
            MatchesSearch(client, search));

        if (IsGroupWiseDirectory)
        {
            var assignments = _groupCatalog.Assignments
                .GroupBy(item => item.ClientId)
                .ToDictionary(group => group.Key, group => group.Last().FolderId);
            query = query.Where(client =>
                CurrentGroupId.Length == 0
                    ? !assignments.ContainsKey(client.Id)
                    : assignments.TryGetValue(client.Id, out var folderId) && folderId.Equals(CurrentGroupId, StringComparison.OrdinalIgnoreCase));
        }

        query = ApplySort(query);
        var selectedId = SelectedClient?.Id ?? 0;
        Clients.Clear();
        foreach (var client in query) Clients.Add(client);
        SelectedClient = selectedId > 0 ? Clients.FirstOrDefault(x => x.Id == selectedId) : SelectedClient;
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(SortDescriptionText));
    }

    private IEnumerable<AuditClientProfile> ApplySort(IEnumerable<AuditClientProfile> query)
    {
        Func<AuditClientProfile, object> key = _sortColumn switch
        {
            "Name" => x => (object)(x.TradeName.Length > 0 ? x.TradeName : x.ClientName),
            "Gstin" => x => x.Gstin,
            "State" => x => x.State,
            "FinancialYear" => x => x.FinancialYear,
            "Updated" => x => x.UpdatedUtc,
            _ => x => x.ClientCode
        };
        var ordered = _sortAscending ? query.OrderBy(key) : query.OrderByDescending(key);
        return ordered.ThenBy(x => x.IsArchived).ThenBy(x => x.ClientCode, StringComparer.OrdinalIgnoreCase);
    }

    private void ToggleSort(string? column)
    {
        if (string.IsNullOrWhiteSpace(column)) return;
        if (_sortColumn.Equals(column, StringComparison.OrdinalIgnoreCase)) _sortAscending = !_sortAscending;
        else { _sortColumn = column; _sortAscending = true; }
        ApplyFilter();
        OnPropertyChanged(nameof(ClientCodeSortIndicator));
        OnPropertyChanged(nameof(NameSortIndicator));
        OnPropertyChanged(nameof(GstinSortIndicator));
        OnPropertyChanged(nameof(StateSortIndicator));
        OnPropertyChanged(nameof(FinancialYearSortIndicator));
        OnPropertyChanged(nameof(UpdatedSortIndicator));
    }

    private static bool MatchesSearch(AuditClientProfile client, string search)
    {
        if (search.Length == 0) return true;
        return Contains(client.ClientCode, search) || Contains(client.ClientName, search) || Contains(client.LegalName, search) ||
               Contains(client.TradeName, search) || Contains(client.Gstin, search) || Contains(client.Pan, search) ||
               Contains(client.State, search) || Contains(client.Phone, search) || Contains(client.Email, search);
    }

    private static bool Contains(string value, string search) => (value ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase);

    private string ValidateEditor()
    {
        var code = ClientCode.Trim();
        var name = ClientName.Trim();
        var fy = NormalizeFinancialYear(FinancialYear);
        if (code.Length == 0) return "Client Code is required.";
        if (name.Length == 0 && Gstin.Trim().Length == 0) return "Client Name or GSTIN is required.";
        if (fy.Length == 0) return "Financial Year must be in YYYY-YY format, for example 2025-26.";
        if (Gstin.Trim().Length is > 0 and not 15) return "GSTIN must contain 15 characters or be left blank.";
        if (Pan.Trim().Length is > 0 and not 10) return "PAN must contain 10 characters or be left blank.";
        if (Email.Trim().Length > 0)
        {
            try { _ = new MailAddress(Email.Trim()); }
            catch { return "Enter a valid email address or leave Email blank."; }
        }
        if (name.Length == 0 && Gstin.Trim().Length == 15) ClientName = Gstin.Trim().ToUpperInvariant();
        FinancialYear = fy;
        return string.Empty;
    }


    private void LoadGroupCatalog()
    {
        var auditData = ClientGroupingCatalog.Load(_repository.GetWorkspaceSetting(AuditGroupPreferenceKey));
        var sharedData = ClientGroupingCatalog.Load(_applicationPreferences.Get(ClientGroupingCatalog.PreferenceKey));

        // FIXED29: Accounting is the single hierarchy authority. If an older Audit-only
        // installation already has folders but Accounting has none, migrate those folders
        // once into the shared hierarchy. Audit client assignments remain Audit-owned.
        if (sharedData.Folders.Count == 0 && auditData.Folders.Count > 0)
        {
            sharedData.Folders = CloneFolders(auditData.Folders);
            _applicationPreferences.Set(ClientGroupingCatalog.PreferenceKey, ClientGroupingCatalog.Save(sharedData));
        }

        _groupCatalog = MergeAuditAssignmentsOntoSharedHierarchy(auditData, sharedData);
        _isGroupWiseDirectory = _repository.GetWorkspaceSetting(AuditGroupModePreferenceKey)
            .Equals("Group", StringComparison.OrdinalIgnoreCase);
        var savedCurrent = _repository.GetWorkspaceSetting(AuditCurrentGroupPreferenceKey).Trim();
        _currentGroupId = _groupCatalog.Folders.Any(folder => folder.Id.Equals(savedCurrent, StringComparison.OrdinalIgnoreCase))
            ? savedCurrent
            : string.Empty;
        OnPropertyChanged(nameof(IsGroupWiseDirectory));
        OnPropertyChanged(nameof(IsClientWiseDirectory));
        OnPropertyChanged(nameof(CurrentGroupId));
        OnPropertyChanged(nameof(CurrentGroupPath));
        OnPropertyChanged(nameof(GroupCount));
        RefreshGroupDirectory();
        RebuildGroupManager();
    }

    private void SynchronizeSharedGroupHierarchy()
    {
        var sharedData = ClientGroupingCatalog.Load(_applicationPreferences.Get(ClientGroupingCatalog.PreferenceKey));
        if (sharedData.Folders.Count == 0 && _groupCatalog.Folders.Count == 0) return;
        var currentAudit = new ClientGroupCatalogData
        {
            Folders = CloneFolders(_groupCatalog.Folders),
            Assignments = _groupCatalog.Assignments.Select(x => new ClientGroupAssignmentRecord { ClientId = x.ClientId, FolderId = x.FolderId }).ToList()
        };
        _groupCatalog = MergeAuditAssignmentsOntoSharedHierarchy(currentAudit, sharedData);
    }

    private void SaveGroupCatalog()
    {
        ClientGroupingCatalog.Normalize(_groupCatalog);

        // Save the folder structure to the exact same application preference used by
        // Accounting Client Groups, while retaining Accounting's own Client assignments.
        var shared = ClientGroupingCatalog.Load(_applicationPreferences.Get(ClientGroupingCatalog.PreferenceKey));
        shared.Folders = CloneFolders(_groupCatalog.Folders);
        _applicationPreferences.Set(ClientGroupingCatalog.PreferenceKey, ClientGroupingCatalog.Save(shared));

        // Audit assignments stay independent because Audit Client IDs belong to audit-hub.db.
        var audit = new ClientGroupCatalogData
        {
            Folders = CloneFolders(_groupCatalog.Folders),
            Assignments = _groupCatalog.Assignments.Select(x => new ClientGroupAssignmentRecord { ClientId = x.ClientId, FolderId = x.FolderId }).ToList()
        };
        _repository.SaveWorkspaceSetting(AuditGroupPreferenceKey, ClientGroupingCatalog.Save(audit));
        RefreshGroupDirectory();
        RebuildGroupManager();
        ApplyFilter();
        RaiseCountProperties();
    }

    private static List<ClientGroupFolderRecord> CloneFolders(IEnumerable<ClientGroupFolderRecord> folders) =>
        folders.Select(x => new ClientGroupFolderRecord
        {
            Id = x.Id,
            Name = x.Name,
            Type = x.Type,
            ParentId = x.ParentId,
            LinkedCaProfileId = x.LinkedCaProfileId
        }).ToList();

    private static ClientGroupCatalogData MergeAuditAssignmentsOntoSharedHierarchy(
        ClientGroupCatalogData auditData,
        ClientGroupCatalogData sharedData)
    {
        ClientGroupingCatalog.Normalize(auditData);
        ClientGroupingCatalog.Normalize(sharedData);
        var merged = new ClientGroupCatalogData { Folders = CloneFolders(sharedData.Folders) };
        var sharedIds = merged.Folders.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldById = auditData.Folders.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var sharedByPath = merged.Folders
            .GroupBy(x => BuildFolderPathKey(merged.Folders, x.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Id, StringComparer.OrdinalIgnoreCase);

        foreach (var assignment in auditData.Assignments)
        {
            var targetId = sharedIds.Contains(assignment.FolderId) ? assignment.FolderId : string.Empty;
            if (targetId.Length == 0 && oldById.ContainsKey(assignment.FolderId))
            {
                var path = BuildFolderPathKey(auditData.Folders, assignment.FolderId);
                if (path.Length > 0 && sharedByPath.TryGetValue(path, out var mapped)) targetId = mapped;
            }
            if (targetId.Length > 0)
                merged.Assignments.Add(new ClientGroupAssignmentRecord { ClientId = assignment.ClientId, FolderId = targetId });
        }
        ClientGroupingCatalog.Normalize(merged);
        return merged;
    }

    private static string BuildFolderPathKey(IReadOnlyList<ClientGroupFolderRecord> folders, string folderId)
    {
        var byId = folders.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        var current = folderId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (current.Length > 0 && visited.Add(current) && byId.TryGetValue(current, out var folder))
        {
            parts.Add($"{ClientGroupFolderTypes.Normalize(folder.Type)}:{folder.Name.Trim()}");
            current = folder.ParentId ?? string.Empty;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private void RefreshGroupDirectory()
    {
        ClientGroupingCatalog.Normalize(_groupCatalog);
        if (CurrentGroupId.Length > 0 && !_groupCatalog.Folders.Any(folder => folder.Id.Equals(CurrentGroupId, StringComparison.OrdinalIgnoreCase)))
            CurrentGroupId = string.Empty;

        var assignmentCounts = _groupCatalog.Assignments
            .GroupBy(item => item.FolderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.ClientId).Distinct().Count(), StringComparer.OrdinalIgnoreCase);
        var childCounts = _groupCatalog.Folders
            .Where(folder => folder.ParentId.Length > 0)
            .GroupBy(folder => folder.ParentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var selectedId = SelectedGroup?.Id ?? string.Empty;
        GroupRows.Clear();
        foreach (var folder in _groupCatalog.Folders
                     .Where(folder => folder.ParentId.Equals(CurrentGroupId, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase))
        {
            assignmentCounts.TryGetValue(folder.Id, out var directClients);
            childCounts.TryGetValue(folder.Id, out var childGroups);
            GroupRows.Add(new AuditClientGroupRow(folder.Id, folder.Name, folder.Type, directClients, childGroups));
        }
        SelectedGroup = GroupRows.FirstOrDefault(row => row.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(CurrentGroupPath));
        OnPropertyChanged(nameof(GroupCount));
    }

    private void OpenGroupManager()
    {
        SynchronizeSharedGroupHierarchy();
        RebuildGroupManager();
        IsGroupFolderEditorOpen = false;
        IsGroupManagerOpen = true;
        GroupManagerStatus = "Same procedure as Accounting: create top-level Groups, then drag Clients or Groups into the shared hierarchy.";
    }

    public void CloseGroupManager()
    {
        IsGroupFolderEditorOpen = false;
        IsGroupManagerOpen = false;
    }

    private void BeginAddGroupFolder()
    {
        GroupNameDraft = string.Empty;
        SelectedGroupType = ClientGroupFolderTypes.CompanyGroup;
        IsGroupFolderEditorOpen = true;
    }

    private void CancelGroupFolder()
    {
        IsGroupFolderEditorOpen = false;
        GroupNameDraft = string.Empty;
    }

    private void SaveGroupFolder()
    {
        var name = GroupNameDraft.Trim();
        if (name.Length == 0)
        {
            GroupManagerStatus = "Enter a Group name first.";
            return;
        }
        var type = ClientGroupFolderTypes.Normalize(SelectedGroupType);
        // Same as Accounting: + Make Group creates at the top level; nesting happens
        // afterward by drag/move so creation never guesses a parent.
        if (_groupCatalog.Folders.Any(x => string.IsNullOrWhiteSpace(x.ParentId) &&
                                           x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            GroupManagerStatus = $"A top-level Group named '{name}' already exists.";
            return;
        }
        var folder = new ClientGroupFolderRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Type = type,
            ParentId = string.Empty
        };
        _groupCatalog.Folders.Add(folder);
        IsGroupFolderEditorOpen = false;
        GroupNameDraft = string.Empty;
        SaveGroupCatalog();
        SelectedGroupFolder = FindGroupFolderNode(folder.Id);
        SelectedGroupSourceItem = GroupSourceItems.FirstOrDefault(x => x.Folder?.Id.Equals(folder.Id, StringComparison.OrdinalIgnoreCase) == true);
        GroupManagerStatus = $"Created {type} - {name} at the top level. Drag it into another Group if nesting is required.";
        DirectoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteSelectedGroupFolder()
    {
        if (SelectedGroupFolder is null || SelectedGroupFolder.IsClient) return;
        var folder = _groupCatalog.Folders.FirstOrDefault(x => x.Id.Equals(SelectedGroupFolder.Id, StringComparison.OrdinalIgnoreCase));
        if (folder is null) return;
        if (MessageBox.Show(
                $"Delete Group '{folder.Name}'?\n\nClients and child Groups will move to its parent. Audit client data is not deleted.",
                "Delete Client Group", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var child in _groupCatalog.Folders.Where(x => x.ParentId.Equals(folder.Id, StringComparison.OrdinalIgnoreCase)))
            child.ParentId = folder.ParentId;
        foreach (var assignment in _groupCatalog.Assignments.Where(x => x.FolderId.Equals(folder.Id, StringComparison.OrdinalIgnoreCase)))
            assignment.FolderId = folder.ParentId;
        _groupCatalog.Assignments.RemoveAll(x => string.IsNullOrWhiteSpace(x.FolderId));
        _groupCatalog.Folders.Remove(folder);
        SaveGroupCatalog();
        GroupManagerStatus = $"Deleted '{folder.Name}'. Clients and child Groups were preserved.";
        DirectoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MoveSelectedGroupSource()
    {
        if (SelectedGroupSourceItem is null || SelectedGroupFolder is null) return;
        MoveGroupSourceTo(SelectedGroupSourceItem, SelectedGroupFolder);
    }

    public bool CanMoveGroupSourceTo(AuditClientGroupSourceItem source, AuditClientGroupFolderNode? destination, out string reason)
    {
        reason = string.Empty;
        if (destination is null || destination.IsClient)
        {
            reason = "Select a destination Group.";
            return false;
        }
        if (source.Client is not null) return true;
        if (source.Folder is null) return false;
        var moving = _groupCatalog.Folders.FirstOrDefault(x => x.Id.Equals(source.Folder.Id, StringComparison.OrdinalIgnoreCase));
        var target = _groupCatalog.Folders.FirstOrDefault(x => x.Id.Equals(destination.Id, StringComparison.OrdinalIgnoreCase));
        if (moving is null || target is null) return false;
        if (moving.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase))
        {
            reason = "A Group cannot be moved into itself.";
            return false;
        }
        if (moving.Type.Equals(ClientGroupFolderTypes.Firm, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Firm is a top-level Group and cannot be nested.";
            return false;
        }
        var cursor = target;
        var byId = _groupCatalog.Folders.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (cursor is not null && visited.Add(cursor.Id))
        {
            if (cursor.Id.Equals(moving.Id, StringComparison.OrdinalIgnoreCase))
            {
                reason = "That move would create a Group cycle.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(cursor.ParentId) || !byId.TryGetValue(cursor.ParentId, out var parent)) break;
            cursor = parent;
        }
        return true;
    }

    public void MoveGroupSourceTo(AuditClientGroupSourceItem source, AuditClientGroupFolderNode? destination)
    {
        if (!CanMoveGroupSourceTo(source, destination, out var reason))
        {
            GroupManagerStatus = reason.Length > 0 ? reason : "The selected item cannot be moved there.";
            return;
        }
        var destinationId = destination!.Id;
        if (source.Client is not null)
        {
            _groupCatalog.Assignments.RemoveAll(x => x.ClientId == source.Client.Id);
            _groupCatalog.Assignments.Add(new ClientGroupAssignmentRecord { ClientId = source.Client.Id, FolderId = destinationId });
            GroupManagerStatus = $"Moved {source.Client.ClientName} into {destination.Path}.";
        }
        else if (source.Folder is not null)
        {
            var moving = _groupCatalog.Folders.First(x => x.Id.Equals(source.Folder.Id, StringComparison.OrdinalIgnoreCase));
            moving.ParentId = destinationId;
            GroupManagerStatus = $"Moved {moving.Type} - {moving.Name} into {destination.Path}.";
        }
        SaveGroupCatalog();
        SelectedGroupFolder = FindGroupFolderNode(destinationId);
        DirectoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildGroupManager()
    {
        ClientGroupingCatalog.Normalize(_groupCatalog);
        var selectedId = SelectedGroupFolder?.Id ?? string.Empty;
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<AuditClientGroupFolderNode>(GroupFolderRoots.Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!node.IsClient && node.IsExpanded) expanded.Add(node.Id);
            for (var i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
        GroupFolderRoots.Clear();

        var assignments = _groupCatalog.Assignments
            .GroupBy(x => x.ClientId)
            .ToDictionary(x => x.Key, x => x.Last().FolderId);
        var clientsByFolder = assignments
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(a => _allClients.FirstOrDefault(c => c.Id == a.Key))
                    .Where(c => c is not null && !c.IsArchived).Cast<AuditClientProfile>()
                    .OrderBy(c => c.ClientName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.FinancialYear).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var byParent = _groupCatalog.Folders
            .GroupBy(x => x.ParentId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.Name, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);

        AuditClientGroupFolderNode Build(ClientGroupFolderRecord record, string parentPath, HashSet<string>? ancestry = null)
        {
            ancestry ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var path = parentPath.Length == 0 ? record.Name : $"{parentPath} / {record.Name}";
            var node = new AuditClientGroupFolderNode(record, path) { IsExpanded = expanded.Contains(record.Id) };
            if (!ancestry.Add(record.Id)) return node;
            if (byParent.TryGetValue(record.Id, out var children))
                foreach (var child in children)
                    if (!ancestry.Contains(child.Id)) node.Children.Add(Build(child, path, new HashSet<string>(ancestry, StringComparer.OrdinalIgnoreCase)));
            if (clientsByFolder.TryGetValue(record.Id, out var clients))
                foreach (var client in clients) node.Children.Add(AuditClientGroupFolderNode.ForClient(client, $"{path} / {client.ClientName}"));
            return node;
        }

        foreach (var root in _groupCatalog.Folders.Where(x => string.IsNullOrWhiteSpace(x.ParentId)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            GroupFolderRoots.Add(Build(root, string.Empty));

        GroupSourceItems.Clear();
        var assignedIds = _groupCatalog.Assignments.Select(x => x.ClientId).ToHashSet();
        foreach (var client in _allClients.Where(x => !x.IsArchived && !assignedIds.Contains(x.Id))
                     .OrderBy(x => x.ClientName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.FinancialYear))
            GroupSourceItems.Add(AuditClientGroupSourceItem.ForClient(client, "Not grouped"));
        foreach (var folder in _groupCatalog.Folders.Where(x => string.IsNullOrWhiteSpace(x.ParentId)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            GroupSourceItems.Add(AuditClientGroupSourceItem.ForFolder(new AuditClientGroupFolderNode(folder, folder.Name), "Top level"));

        SelectedGroupFolder = FindGroupFolderNode(selectedId) ?? GroupFolderRoots.FirstOrDefault();
        if (SelectedGroupSourceItem is not null)
        {
            var clientId = SelectedGroupSourceItem.Client?.Id ?? 0;
            var folderId = SelectedGroupSourceItem.Folder?.Id ?? string.Empty;
            SelectedGroupSourceItem = clientId > 0
                ? GroupSourceItems.FirstOrDefault(x => x.Client?.Id == clientId)
                : GroupSourceItems.FirstOrDefault(x => x.Folder?.Id.Equals(folderId, StringComparison.OrdinalIgnoreCase) == true);
        }
    }

    private AuditClientGroupFolderNode? FindGroupFolderNode(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var stack = new Stack<AuditClientGroupFolderNode>(GroupFolderRoots.Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!node.IsClient && node.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return node;
            for (var i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
        return null;
    }

    private void CreateGroup()
    {
        var name = GroupNameDraft.Trim();
        if (name.Length == 0)
        {
            StatusText = "Enter a Group name first.";
            return;
        }
        if (_groupCatalog.Folders.Any(folder => folder.ParentId.Equals(CurrentGroupId, StringComparison.OrdinalIgnoreCase) && folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = $"A Group named '{name}' already exists here.";
            return;
        }

        var type = ClientGroupFolderTypes.Normalize(SelectedGroupType);
        var parentId = type.Equals(ClientGroupFolderTypes.Firm, StringComparison.OrdinalIgnoreCase) ? string.Empty : CurrentGroupId;
        var folder = new ClientGroupFolderRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Type = type,
            ParentId = parentId
        };
        _groupCatalog.Folders.Add(folder);
        GroupNameDraft = string.Empty;
        SaveGroupCatalog();
        SelectedGroup = GroupRows.FirstOrDefault(row => row.Id.Equals(folder.Id, StringComparison.OrdinalIgnoreCase));
        StatusText = $"Audit Client Group '{name}' created.";
        DirectoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RenameSelectedGroup()
    {
        if (SelectedGroup is null) return;
        var name = GroupNameDraft.Trim();
        if (name.Length == 0)
        {
            StatusText = "Enter the new Group name first.";
            return;
        }
        var folder = _groupCatalog.Folders.FirstOrDefault(item => item.Id.Equals(SelectedGroup.Id, StringComparison.OrdinalIgnoreCase));
        if (folder is null) return;
        folder.Name = name;
        GroupNameDraft = string.Empty;
        SaveGroupCatalog();
        SelectedGroup = GroupRows.FirstOrDefault(row => row.Id.Equals(folder.Id, StringComparison.OrdinalIgnoreCase));
        StatusText = $"Audit Client Group renamed to '{name}'.";
        DirectoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteSelectedGroup()
    {
        if (SelectedGroup is null) return;
        var folder = _groupCatalog.Folders.FirstOrDefault(item => item.Id.Equals(SelectedGroup.Id, StringComparison.OrdinalIgnoreCase));
        if (folder is null) return;
        if (MessageBox.Show(
                $"Delete Audit Client Group '{folder.Name}'?\n\nClients and child Groups will move to its parent. Audit client data is not deleted.",
                "Delete Audit Client Group",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        foreach (var child in _groupCatalog.Folders.Where(item => item.ParentId.Equals(folder.Id, StringComparison.OrdinalIgnoreCase)))
            child.ParentId = folder.ParentId;
        foreach (var assignment in _groupCatalog.Assignments.Where(item => item.FolderId.Equals(folder.Id, StringComparison.OrdinalIgnoreCase)))
            assignment.FolderId = folder.ParentId;
        _groupCatalog.Assignments.RemoveAll(item => string.IsNullOrWhiteSpace(item.FolderId));
        _groupCatalog.Folders.Remove(folder);
        SaveGroupCatalog();
        StatusText = $"Audit Client Group '{folder.Name}' deleted; clients were preserved.";
        DirectoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenSelectedGroup()
    {
        if (SelectedGroup is null) return;
        CurrentGroupId = SelectedGroup.Id;
        SelectedGroup = null;
        RefreshGroupDirectory();
        ApplyFilter();
        StatusText = $"Audit Group: {CurrentGroupPath}.";
    }

    private void NavigateGroupBack()
    {
        if (CurrentGroupId.Length == 0) return;
        var current = _groupCatalog.Folders.FirstOrDefault(folder => folder.Id.Equals(CurrentGroupId, StringComparison.OrdinalIgnoreCase));
        CurrentGroupId = current?.ParentId ?? string.Empty;
        SelectedGroup = null;
        RefreshGroupDirectory();
        ApplyFilter();
        StatusText = CurrentGroupId.Length == 0 ? "Audit Client Groups · top level." : $"Audit Group: {CurrentGroupPath}.";
    }

    private void MoveSelectedClientHere()
    {
        if (SelectedClient is null || !IsGroupWiseDirectory) return;
        _groupCatalog.Assignments.RemoveAll(item => item.ClientId == SelectedClient.Id);
        if (CurrentGroupId.Length > 0)
            _groupCatalog.Assignments.Add(new ClientGroupAssignmentRecord { ClientId = SelectedClient.Id, FolderId = CurrentGroupId });
        var name = SelectedClient.ClientName;
        SaveGroupCatalog();
        StatusText = CurrentGroupId.Length == 0
            ? $"{name} moved to the top level."
            : $"{name} moved to {CurrentGroupPath}.";
        DirectoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UngroupSelectedClient()
    {
        if (SelectedClient is null) return;
        var name = SelectedClient.ClientName;
        _groupCatalog.Assignments.RemoveAll(item => item.ClientId == SelectedClient.Id);
        SaveGroupCatalog();
        StatusText = $"{name} moved to the top level.";
        DirectoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private string BuildCurrentGroupPath()
    {
        if (CurrentGroupId.Length == 0) return "Top Level";
        var byId = _groupCatalog.Folders.ToDictionary(folder => folder.Id, StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        var id = CurrentGroupId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (id.Length > 0 && visited.Add(id) && byId.TryGetValue(id, out var folder))
        {
            names.Add(folder.Name);
            id = folder.ParentId;
        }
        names.Reverse();
        return names.Count == 0 ? "Top Level" : string.Join(" / ", names);
    }

    private string GenerateNextClientCode()
    {
        var usedCodes = _allClients.Select(x => x.ClientCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = 1; number <= 99999; number++)
        {
            var candidate = $"CL{number:000}";
            if (!usedCodes.Contains(candidate)) return candidate;
        }
        return $"CL{DateTime.Now:HHmmss}";
    }

    private void RebuildFinancialYearFilters()
    {
        var old = SelectedFinancialYearFilter;
        FinancialYearFilters.Clear();
        FinancialYearFilters.Add(AllFinancialYearsFilter);
        foreach (var fy in _allClients.Select(x => x.FinancialYear).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
            FinancialYearFilters.Add(fy);
        SelectedFinancialYearFilter = FinancialYearFilters.Contains(old) ? old : AllFinancialYearsFilter;
    }

    private void BuildFinancialYearOptions()
    {
        var start = DateTime.Today.Month >= 4 ? DateTime.Today.Year : DateTime.Today.Year - 1;
        for (var year = start + 2; year >= start - 10; year--) FinancialYearOptions.Add($"{year}-{(year + 1) % 100:00}");
        FinancialYear = DefaultFinancialYear();
    }

    private static string DefaultFinancialYear()
    {
        var start = DateTime.Today.Month >= 4 ? DateTime.Today.Year : DateTime.Today.Year - 1;
        return $"{start}-{(start + 1) % 100:00}";
    }

    private static string NormalizeFinancialYear(string? value)
    {
        var text = (value ?? string.Empty).Trim().Replace("/", "-");
        if (text.Length == 7 && int.TryParse(text[..4], out var start) && text[4] == '-' && int.TryParse(text[5..], out var end) && end == (start + 1) % 100) return text;
        if (text.Length == 9 && int.TryParse(text[..4], out start) && text[4] == '-' && int.TryParse(text[5..], out var fullEnd) && fullEnd == start + 1) return $"{start}-{fullEnd % 100:00}";
        return string.Empty;
    }

    private void RaiseCountProperties()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(ArchivedCount));
        OnPropertyChanged(nameof(GstinCount));
        OnPropertyChanged(nameof(NeedsProfileCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(GroupCount));
    }

    private void RaiseCommands()
    {
        SetCurrentCommand.RaiseCanExecuteChanged();
        EditSelectedCommand.RaiseCanExecuteChanged();
        ArchiveCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        MoveClientHereCommand.RaiseCanExecuteChanged();
        UngroupClientCommand.RaiseCanExecuteChanged();
        RenameGroupCommand.RaiseCanExecuteChanged();
        DeleteGroupCommand.RaiseCanExecuteChanged();
        OpenGroupCommand.RaiseCanExecuteChanged();
        BackGroupCommand.RaiseCanExecuteChanged();
        DeleteGroupFolderCommand.RaiseCanExecuteChanged();
        MoveSelectedGroupSourceCommand.RaiseCanExecuteChanged();
    }

    private string SortIndicator(string column) => _sortColumn.Equals(column, StringComparison.OrdinalIgnoreCase) ? (_sortAscending ? " ↑" : " ↓") : string.Empty;
    private static string SortCaption(string column) => column switch
    {
        "Name" => "Name",
        "Gstin" => "GSTIN",
        "State" => "State",
        "FinancialYear" => "Financial Year",
        "Updated" => "Updated",
        _ => "Client Code"
    };

    public sealed record AuditClientGroupRow(string Id, string Name, string Type, int DirectClientCount, int ChildGroupCount)
    {
        public string Summary => $"{Type} · {DirectClientCount:N0} client(s) · {ChildGroupCount:N0} group(s)";
    }

    private static readonly IReadOnlyDictionary<string, string> StateByCode = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["01"]="Jammu and Kashmir",["02"]="Himachal Pradesh",["03"]="Punjab",["04"]="Chandigarh",["05"]="Uttarakhand",["06"]="Haryana",["07"]="Delhi",["08"]="Rajasthan",["09"]="Uttar Pradesh",["10"]="Bihar",["11"]="Sikkim",["12"]="Arunachal Pradesh",["13"]="Nagaland",["14"]="Manipur",["15"]="Mizoram",["16"]="Tripura",["17"]="Meghalaya",["18"]="Assam",["19"]="West Bengal",["20"]="Jharkhand",["21"]="Odisha",["22"]="Chhattisgarh",["23"]="Madhya Pradesh",["24"]="Gujarat",["25"]="Daman and Diu",["26"]="Dadra and Nagar Haveli and Daman and Diu",["27"]="Maharashtra",["28"]="Andhra Pradesh (Old)",["29"]="Karnataka",["30"]="Goa",["31"]="Lakshadweep",["32"]="Kerala",["33"]="Tamil Nadu",["34"]="Puducherry",["35"]="Andaman and Nicobar Islands",["36"]="Telangana",["37"]="Andhra Pradesh",["38"]="Ladakh",["97"]="Other Territory",["99"]="Centre Jurisdiction"
    };
}

public sealed class AuditClientGroupSourceItem
{
    private AuditClientGroupSourceItem() { }
    public AuditClientProfile? Client { get; private init; }
    public AuditClientGroupFolderNode? Folder { get; private init; }
    public string PrimaryText { get; private init; } = string.Empty;
    public string SecondaryText { get; private init; } = string.Empty;
    public string KindText { get; private init; } = string.Empty;

    public static AuditClientGroupSourceItem ForClient(AuditClientProfile client, string currentPath) => new()
    {
        Client = client,
        PrimaryText = client.ClientName,
        SecondaryText = $"{client.ClientCode} · {client.FinancialYear} · {currentPath}",
        KindText = "Client"
    };

    public static AuditClientGroupSourceItem ForFolder(AuditClientGroupFolderNode folder, string parentPath) => new()
    {
        Folder = folder,
        PrimaryText = folder.DisplayText,
        SecondaryText = $"Current parent: {parentPath}",
        KindText = "Group"
    };
}

public sealed class AuditClientGroupFolderNode : ViewModelBase
{
    public AuditClientGroupFolderNode(ClientGroupFolderRecord record, string path)
    {
        Id = record.Id; Name = record.Name; Type = record.Type; Path = path;
    }

    private AuditClientGroupFolderNode(AuditClientProfile client, string path)
    {
        Client = client; Id = $"client:{client.Id}"; Name = client.ClientName; Type = "Client"; Path = path;
    }

    public AuditClientProfile? Client { get; }
    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public string Path { get; }
    public bool IsClient => Client is not null;
    public string DisplayText => IsClient ? Name : $"{Type} - {Name}";
    public string SecondaryText => IsClient ? $"{Client!.ClientCode} · {Client.FinancialYear}" : string.Empty;
    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public ObservableCollection<AuditClientGroupFolderNode> Children { get; } = [];
    public static AuditClientGroupFolderNode ForClient(AuditClientProfile client, string path) => new(client, path);
}

