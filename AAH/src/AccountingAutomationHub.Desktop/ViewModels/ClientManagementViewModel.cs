using System.Collections.ObjectModel;
using System.IO;
using System.Net.Mail;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using AccountingAutomationHub.Application.Clients;
using AccountingAutomationHub.Application.Imports;
using AccountingAutomationHub.Application.Settings;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Masters;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed partial class ClientManagementViewModel :
    PageViewModel
{
    private readonly IClientRepository _repository;
    private readonly IGstinProfileLookupService _gstinLookupService;
    private readonly EReturnsRootContext _eReturnsRootContext;
    private readonly ActiveClientContext _clientContext;
    private const string DisplayNameSettingKey =
        "DashboardClientNameMode";
    private const string ClientDirectoryViewModeSettingKey = "ClientDirectoryViewMode";
    private const string AllFinancialYearsFilter = "All Financial Years";

    private readonly ClientProfileSyncContext _profileSyncContext;
    private readonly IImportActivityRepository _importActivityRepository;
    private readonly IApplicationPreferenceRepository _preferenceRepository;
    private readonly SqliteGovernanceRepository _governanceRepository;
    private readonly SqliteMasterRepository _masterRepository;
    private readonly SecureLanServerService? _secureLanServerService;
    private readonly BackgroundAutomationService? _automationService;
    private readonly DispatcherTimer? _mobileQrRefreshTimer;
    private readonly List<ClientProfile> _allClients = [];

    private ClientProfile? _selectedClient;
    private string _searchText = string.Empty;
    private bool _includeArchived;
    private bool _isEditorOpen;
    private string _selectedFinancialYearFilter = AllFinancialYearsFilter;
    private string _displayNameMode = ClientDisplayNameModes.ClientName;
    private string _sortColumn = "ClientCode";
    private bool _sortAscending = true;
    private string _statusText =
        "Create a client or select an existing client.";
    private string _validationText = string.Empty;
    private string _lookupStatusText = string.Empty;
    private bool _isGroupManagerOpen;
    private ClientGroupCatalogData _clientGroupData = new();
    private ClientGroupFolderNode? _selectedGroupFolder;
    private ClientProfile? _selectedGroupAvailableClient;
    private ClientProfile? _selectedGroupAssignedClient;
    private ClientGroupFolderNode? _selectedGroupChildFolder;
    private CaProfileRow? _selectedGroupCaProfile;
    private string _groupFolderName = string.Empty;
    private string _groupFolderType = string.Empty;
    private string _groupFolderEditorParentId = string.Empty;
    private string _groupFolderEditorParentPath = string.Empty;
    private string _groupManagerStatus = "Create a Group, then drag it from the left into Firm, Company or another Group to build the hierarchy.";
    private bool _isGroupFolderEditorOpen;
    private bool _isGroupFolderRenameMode;
    private bool _isGroupClientPickerOpen;
    private bool _isGroupMoveFolderOpen;
    private string _groupClientSearch = string.Empty;
    private string _groupBrowserSearch = string.Empty;
    private ClientGroupBrowserItem? _selectedGroupBrowserItem;
    private CaProfileRow? _groupFolderCaProfile;
    private ClientGroupFolderNode? _selectedGroupMoveDestination;
    private ClientGroupCreateParentChoice? _selectedGroupCreateParent;
    private string _groupRootScope = ClientGroupFolderTypes.RootFirm;
    private readonly List<ClientGroupClientChoice> _groupClientChoiceCache = [];
    private ClientGroupSourceItem? _selectedGroupSourceItem;
    private bool _isGroupWiseDirectory;
    private ClientDirectoryDisplayItem? _selectedDirectoryItem;
    private string _directoryGroupFolderId = string.Empty;

    private long _editingId;
    private string _clientCode = string.Empty;
    private string _clientName = string.Empty;
    private string _legalName = string.Empty;
    private string _tradeName = string.Empty;
    private string _gstin = string.Empty;
    private string _additionalGstinsText = string.Empty;
    private string _pan = string.Empty;
    private string _state = string.Empty;
    private string _financialYear = string.Empty;
    private string _address = string.Empty;
    private string _phone = string.Empty;
    private string _email = string.Empty;
    private string _notes = string.Empty;
    private string _clientEditorGroupId = string.Empty;
    private string _clientEditorGroupPath = "No Group";
    private bool _groupCreateForClientPicker;
    private string _lastCreatedGroupId = string.Empty;
    private bool _editingArchived;
    private bool _importFromPreviousFy;
    private ClientProfile? _selectedPreviousFyClient;
    private DateTime _createdUtc;
    private DateTime _updatedUtc;

    private bool _isApplyingGstinData;
    private bool _panAutoFilled;
    private bool _stateAutoFilled;
    private bool _legalNameAutoFilled;
    private bool _tradeNameAutoFilled;
    private bool _legalNameIsManual;
    private bool _tradeNameIsManual;
    private string _profileSourceType = string.Empty;
    private string _profileSourcePath = string.Empty;
    private string _latestGstr3bPeriod = string.Empty;
    private string _latestGstr3bPeriodEndKey = string.Empty;
    private string _latestGstr3bArn = string.Empty;
    private string _latestGstr3bArnDate = string.Empty;
    private DateTime _gstProfileUpdatedUtc;
    private CancellationTokenSource? _lookupCancellation;
    private bool _isLanAccessOpen;
    private ClientProfile? _lanAccessClient;
    private string _lanAccessStatusText = string.Empty;
    private ClientUserAccessRow? _selectedLanAccessUser;
    private System.Windows.Media.ImageSource? _mobileUserQrImage;
    private string _mobileUserQrText = "Select an AAH user to view the live mobile code.";
    private System.Windows.Media.ImageSource? _homeMobileQrImage;
    private string _homeMobileQrText = "Loading your live Mobile Access code...";
    private string _homeMobileCode = string.Empty;
    private string _homeMobileUserCaption = "AAH user";

    public ClientManagementViewModel(
        IClientRepository repository,
        IGstinProfileLookupService gstinLookupService,
        EReturnsRootContext eReturnsRootContext,
        ActiveClientContext clientContext,
        ClientProfileSyncContext profileSyncContext,
        IImportActivityRepository importActivityRepository,
        IApplicationPreferenceRepository preferenceRepository,
        SqliteGovernanceRepository governanceRepository,
        SqliteMasterRepository masterRepository,
        SecureLanServerService? secureLanServerService = null,
        BackgroundAutomationService? automationService = null)
        : base(
            "Clients",
            NetworkClientRuntime.IsNetworkClient
                ? "Main Server authorised Client Directory"
                : "Client Code-first management and GSTIN profile lookup")
    {
        _repository = repository;
        _gstinLookupService = gstinLookupService;
        _eReturnsRootContext = eReturnsRootContext;
        _clientContext = clientContext;
        _profileSyncContext = profileSyncContext;
        _importActivityRepository = importActivityRepository;
        _preferenceRepository = preferenceRepository;
        _governanceRepository = governanceRepository;
        _masterRepository = masterRepository;
        _secureLanServerService = secureLanServerService;
        _automationService = automationService;
        if (_secureLanServerService is not null)
        {
            _secureLanServerService.AndroidUserQrRotated += OnAndroidUserQrRotated;
            _secureLanServerService.RunningStateChanged += OnSecureLanServerRunningStateChanged;
        }
        if (_secureLanServerService is not null || NetworkClientRuntime.IsNetworkClient)
        {
            _mobileQrRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
            _mobileQrRefreshTimer.Tick += async (_, _) =>
            {
                await RefreshHomeMobileQrAsync().ConfigureAwait(true);
                if (IsLanAccessOpen) RefreshMobileUserCodes();
            };
            _mobileQrRefreshTimer.Start();
        }
        _displayNameMode = ClientDisplayNameModes.Normalize(
            _preferenceRepository.Get(DisplayNameSettingKey));
        _clientContext.SetDisplayNameMode(_displayNameMode);

        NewCommand = new RelayCommand(BeginNew, () => !NetworkClientRuntime.IsNetworkClient);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !NetworkClientRuntime.IsNetworkClient);
        SetCurrentCommand = new RelayCommand(
            SetCurrent,
            () => SelectedClient is not null && !SelectedClient.IsArchived);
        EditSelectedCommand = new RelayCommand(
            EditSelected,
            () => !NetworkClientRuntime.IsNetworkClient && SelectedClient is not null);
        CloseEditorCommand = new RelayCommand(CloseEditor);
        CycleDisplayNameModeCommand = new RelayCommand(CycleDisplayNameMode);
        SortAscendingCommand = new ParameterRelayCommand(
            parameter => SetSort(parameter?.ToString(), true));
        SortDescendingCommand = new ParameterRelayCommand(
            parameter => SetSort(parameter?.ToString(), false));
        ToggleSortCommand = new ParameterRelayCommand(
            parameter => ToggleSort(parameter?.ToString()));
        ArchiveCommand = new RelayCommand(
            Archive,
            () => !NetworkClientRuntime.IsNetworkClient && EditingId > 0 && !EditingArchived);
        RestoreCommand = new RelayCommand(
            Restore,
            () => !NetworkClientRuntime.IsNetworkClient && EditingId > 0 && EditingArchived);
        DeleteClientCommand = new RelayCommand(
            DeleteClient,
            () => !NetworkClientRuntime.IsNetworkClient && EditingId > 0 && EditingArchived);
        ReloadCommand = new RelayCommand(LoadClients);
        OpenGroupManagerCommand = new RelayCommand(OpenGroupManager, () => !NetworkClientRuntime.IsNetworkClient);
        CloseGroupManagerCommand = new RelayCommand(CloseGroupManager);
        AddGroupFolderCommand = new RelayCommand(AddGroupFolder, () => !NetworkClientRuntime.IsNetworkClient && SelectedGroupFolder is not null);
        RenameGroupFolderCommand = new RelayCommand(RenameGroupFolder, () => !NetworkClientRuntime.IsNetworkClient && SelectedGroupFolder is { IsRoot: false });
        DeleteGroupFolderCommand = new RelayCommand(DeleteGroupFolder, () => !NetworkClientRuntime.IsNetworkClient && SelectedGroupFolder is { IsRoot: false });
        AssignClientToGroupCommand = new RelayCommand(AssignClientToGroup, () => !NetworkClientRuntime.IsNetworkClient && SelectedGroupFolder is not null && SelectedGroupAvailableClient is not null);
        UnassignClientFromGroupCommand = new RelayCommand(UnassignClientFromGroup, () => !NetworkClientRuntime.IsNetworkClient && SelectedGroupAssignedClient is not null);
        SaveGroupCaLinkCommand = new RelayCommand(SaveGroupCaLink, () => !NetworkClientRuntime.IsNetworkClient && SelectedGroupFolder?.Type.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase) == true);
        OpenSelectedGroupChildFolderCommand = new RelayCommand(OpenSelectedGroupChildFolder, () => SelectedGroupChildFolder is not null);
        OpenSelectedGroupBrowserItemCommand = new RelayCommand(OpenSelectedGroupBrowserItem, () => SelectedGroupBrowserItem is not null);
        RemoveSelectedGroupBrowserClientCommand = new RelayCommand(RemoveSelectedGroupBrowserClient, () => !NetworkClientRuntime.IsNetworkClient && SelectedGroupBrowserItem?.Client is not null);
        BeginAddGroupFolderCommand = new RelayCommand(BeginAddGroupFolder, () => !NetworkClientRuntime.IsNetworkClient);
        BeginRenameGroupFolderCommand = new RelayCommand(BeginRenameGroupFolder, () => !NetworkClientRuntime.IsNetworkClient && SelectedGroupFolder is { IsRoot: false });
        SaveGroupFolderEditorCommand = new RelayCommand(SaveGroupFolderEditor, () => !NetworkClientRuntime.IsNetworkClient);
        CancelGroupFolderEditorCommand = new RelayCommand(() => IsGroupFolderEditorOpen = false);
        OpenGroupClientPickerCommand = new RelayCommand(OpenGroupClientPicker, () => !NetworkClientRuntime.IsNetworkClient && SelectedGroupFolder is not null);
        AddSelectedClientsToGroupCommand = new RelayCommand(AddSelectedClientsToGroup, () => !NetworkClientRuntime.IsNetworkClient && IsGroupClientPickerOpen);
        CancelGroupClientPickerCommand = new RelayCommand(() => IsGroupClientPickerOpen = false);
        OpenGroupMoveFolderCommand = new RelayCommand(OpenGroupMoveFolder, () => !NetworkClientRuntime.IsNetworkClient && SelectedGroupFolder is { IsRoot: false });
        MoveGroupFolderCommand = new RelayCommand(MoveGroupFolder, () => !NetworkClientRuntime.IsNetworkClient && IsGroupMoveFolderOpen && SelectedGroupMoveDestination is not null);
        CancelGroupMoveFolderCommand = new RelayCommand(() => IsGroupMoveFolderOpen = false);
        MoveSelectedGroupSourceCommand = new RelayCommand(MoveSelectedGroupSource, () => !NetworkClientRuntime.IsNetworkClient && SelectedGroupSourceItem is not null && SelectedGroupFolder is not null);
        MoveSelectedGroupSourceToRootCommand = new ParameterRelayCommand(
            parameter => MoveSelectedGroupSourceToRoot(parameter?.ToString()),
            _ => !NetworkClientRuntime.IsNetworkClient && SelectedGroupSourceItem?.Folder is { IsRoot: false });
        ToggleClientDirectoryViewCommand = new RelayCommand(ToggleClientDirectoryView);
        SetClientDirectoryViewCommand = new ParameterRelayCommand(parameter => SetClientDirectoryView(parameter?.ToString()));
        SetGroupRootScopeCommand = new ParameterRelayCommand(parameter => SetGroupRootScope(parameter?.ToString()));
        RefreshProfileCommand = new AsyncRelayCommand(RefreshProfileAsync, () => !NetworkClientRuntime.IsNetworkClient);
        ToggleClientLanAvailabilityCommand = new ParameterRelayCommand(ToggleClientLanAvailability, CanManageClientLan);
        OpenClientLanAccessCommand = new ParameterRelayCommand(OpenClientLanAccess, CanManageClientLan);
        SaveClientLanAccessCommand = new RelayCommand(SaveClientLanAccess, () => IsLanAccessOpen && _lanAccessClient is not null);
        CloseClientLanAccessCommand = new RelayCommand(CloseClientLanAccess);

        _profileSyncContext.Changed += OnProfileSyncChanged;

        BuildFinancialYearOptions();
        _repository.Initialize();
        _gstinLookupService.Initialize();

        _isGroupWiseDirectory = _preferenceRepository.Get(ClientDirectoryViewModeSettingKey)
            .Equals("Group", StringComparison.OrdinalIgnoreCase);

        LoadClients();
        _ = RefreshHomeMobileQrAsync();
        if (!NetworkClientRuntime.IsNetworkClient) BeginNew();
        IsEditorOpen = false;
        if (NetworkClientRuntime.IsNetworkClient)
            StatusText = "Main Server authorised clients only. Client profile maintenance and user-access administration are disabled on this remote application.";
    }

    public event EventHandler? WorkspaceOpenRequested;

    public ObservableCollection<ClientProfile> Clients { get; } = [];
    public ObservableCollection<ClientGroupFolderNode> GroupFolderRoots { get; } = [];
    public ObservableCollection<ClientProfile> GroupAvailableClients { get; } = [];
    public ObservableCollection<ClientProfile> GroupAssignedClients { get; } = [];
    public ObservableCollection<ClientGroupClientChoice> GroupClientChoices { get; } = [];
    public ObservableCollection<ClientGroupBrowserItem> GroupFolderContents { get; } = [];
    public ObservableCollection<ClientGroupFolderNode> GroupMoveDestinations { get; } = [];
    public ObservableCollection<ClientGroupCreateParentChoice> GroupCreateParentChoices { get; } = [];
    public ObservableCollection<CaProfileRow> GroupCaProfiles { get; } = [];
    public ObservableCollection<string> GroupFolderTypes { get; } = new(ClientGroupFolderTypes.CreatableTypes);
    public ObservableCollection<ClientGroupSourceItem> GroupSourceItems { get; } = [];
    public ObservableCollection<ClientDirectoryGroupNode> ClientDirectoryGroupRoots { get; } = [];
    public ObservableCollection<ClientDirectoryDisplayItem> ClientDirectoryItems { get; } = [];

    public ObservableCollection<string> FinancialYearOptions { get; } = [];

    public ClientProfile? SelectedClient
    {
        get => _selectedClient;
        set
        {
            if (!SetProperty(ref _selectedClient, value))
            {
                return;
            }

            RefreshCommands();
        }
    }

    public ClientDirectoryDisplayItem? SelectedDirectoryItem
    {
        get => _selectedDirectoryItem;
        set
        {
            if (!SetProperty(ref _selectedDirectoryItem, value)) return;
            SelectedClient = value?.Client;
        }
    }

    public bool IsGroupManagerOpen
    {
        get => _isGroupManagerOpen;
        private set => SetProperty(ref _isGroupManagerOpen, value);
    }

    public ClientGroupFolderNode? SelectedGroupFolder
    {
        get => _selectedGroupFolder;
        set
        {
            if (!SetProperty(ref _selectedGroupFolder, value)) return;
            // R20: right-side selection is destination/navigation only. Group creation is
            // independent and starts topmost; drag/move is used only to create nesting.
            SelectedGroupCaProfile = value is null ? null : GroupCaProfiles.FirstOrDefault(x => x.ProfileId.Equals(value.LinkedCaProfileId, StringComparison.OrdinalIgnoreCase));
            SelectedGroupChildFolder = null;
            RefreshGroupClientLists();
            RefreshGroupCommands();
            MoveSelectedGroupSourceCommand?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsSelectedGroupCaFolder));
            OnPropertyChanged(nameof(SelectedGroupPath));
        }
    }

    public ClientProfile? SelectedGroupAvailableClient
    {
        get => _selectedGroupAvailableClient;
        set { if (SetProperty(ref _selectedGroupAvailableClient, value)) RefreshGroupCommands(); }
    }
    public ClientProfile? SelectedGroupAssignedClient
    {
        get => _selectedGroupAssignedClient;
        set { if (SetProperty(ref _selectedGroupAssignedClient, value)) RefreshGroupCommands(); }
    }
    public ClientGroupFolderNode? SelectedGroupChildFolder
    {
        get => _selectedGroupChildFolder;
        set { if (SetProperty(ref _selectedGroupChildFolder, value)) RefreshGroupCommands(); }
    }
    public CaProfileRow? SelectedGroupCaProfile { get => _selectedGroupCaProfile; set => SetProperty(ref _selectedGroupCaProfile, value); }
    public CaProfileRow? GroupFolderCaProfile
    {
        get => _groupFolderCaProfile;
        set
        {
            if (!SetProperty(ref _groupFolderCaProfile, value)) return;
            if (IsGroupFolderCreateMode && IsGroupFolderCaType)
                GroupFolderName = value?.CaName?.Trim() ?? string.Empty;
        }
    }
    public string GroupFolderName { get => _groupFolderName; set => SetProperty(ref _groupFolderName, value); }
    public bool CanEditGroupFolderName => !IsGroupFolderCaType;
    public string GroupFolderNameLabel => IsGroupFolderCaType ? "Group Name (from Linked CA)" : "Group Name";
    public string GroupFolderType
    {
        get => _groupFolderType;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : ClientGroupFolderTypes.Normalize(value);
            if (!SetProperty(ref _groupFolderType, normalized)) return;
            OnPropertyChanged(nameof(IsGroupFolderCaType));
            OnPropertyChanged(nameof(ShowGroupFolderCaLinkEditor));
            OnPropertyChanged(nameof(CanEditGroupFolderName));
            OnPropertyChanged(nameof(GroupFolderNameLabel));
            if (IsGroupFolderCreateMode && IsGroupFolderCaType)
                GroupFolderName = GroupFolderCaProfile?.CaName?.Trim() ?? string.Empty;
        }
    }
    public string GroupManagerStatus { get => _groupManagerStatus; private set => SetProperty(ref _groupManagerStatus, value); }
    public bool IsSelectedGroupCaFolder => SelectedGroupFolder?.Type.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase) == true;
    public bool IsGroupFolderCaType => GroupFolderType.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase);
    public bool ShowGroupFolderCaLinkEditor => IsGroupFolderCreateMode && IsGroupFolderCaType;
    public string SelectedGroupPath => SelectedGroupFolder?.Path ?? "Select a destination Group";
    public string GroupRootScope => _groupRootScope;
    public bool IsGroupRootFirmSelected => GroupRootScope.Equals(ClientGroupFolderTypes.RootFirm, StringComparison.OrdinalIgnoreCase);
    public bool IsGroupRootCompanySelected => GroupRootScope.Equals(ClientGroupFolderTypes.RootCompany, StringComparison.OrdinalIgnoreCase);
    public string GroupBrowserSearch
    {
        get => _groupBrowserSearch;
        set { if (SetProperty(ref _groupBrowserSearch, value)) RefreshGroupBrowserContents(); }
    }
    public ClientGroupBrowserItem? SelectedGroupBrowserItem
    {
        get => _selectedGroupBrowserItem;
        set
        {
            if (!SetProperty(ref _selectedGroupBrowserItem, value)) return;
            SelectedGroupChildFolder = value?.Folder;
            SelectedGroupAssignedClient = value?.Client;
            RefreshGroupCommands();
        }
    }
    public bool IsGroupFolderEditorOpen { get => _isGroupFolderEditorOpen; private set => SetProperty(ref _isGroupFolderEditorOpen, value); }
    public bool IsGroupFolderRenameMode { get => _isGroupFolderRenameMode; private set { if (SetProperty(ref _isGroupFolderRenameMode, value)) { OnPropertyChanged(nameof(IsGroupFolderCreateMode)); OnPropertyChanged(nameof(GroupFolderEditorTitle)); OnPropertyChanged(nameof(ShowGroupFolderCaLinkEditor)); } } }
    public bool IsGroupFolderCreateMode => !IsGroupFolderRenameMode;
    public string GroupFolderEditorTitle => IsGroupFolderRenameMode ? "Rename Group" : "Make Group";
    public string GroupFolderEditorParentPath => _groupFolderEditorParentPath;
    public bool IsGroupClientPickerOpen { get => _isGroupClientPickerOpen; private set => SetProperty(ref _isGroupClientPickerOpen, value); }
    public bool IsGroupMoveFolderOpen { get => _isGroupMoveFolderOpen; private set => SetProperty(ref _isGroupMoveFolderOpen, value); }
    public string GroupClientSearch
    {
        get => _groupClientSearch;
        set { if (SetProperty(ref _groupClientSearch, value)) ApplyGroupClientChoiceFilter(); }
    }
    public ClientGroupCreateParentChoice? SelectedGroupCreateParent
    {
        get => _selectedGroupCreateParent;
        set
        {
            if (!SetProperty(ref _selectedGroupCreateParent, value)) return;
            _groupFolderEditorParentId = value?.Id ?? string.Empty;
            _groupFolderEditorParentPath = value?.Path ?? string.Empty;
            OnPropertyChanged(nameof(GroupFolderEditorParentPath));
        }
    }

    public ClientGroupFolderNode? SelectedGroupMoveDestination
    {
        get => _selectedGroupMoveDestination;
        set { if (SetProperty(ref _selectedGroupMoveDestination, value)) MoveGroupFolderCommand?.RaiseCanExecuteChanged(); }
    }
    public ClientGroupSourceItem? SelectedGroupSourceItem
    {
        get => _selectedGroupSourceItem;
        set
        {
            if (!SetProperty(ref _selectedGroupSourceItem, value)) return;
            MoveSelectedGroupSourceCommand?.RaiseCanExecuteChanged();
            MoveSelectedGroupSourceToRootCommand?.RaiseCanExecuteChanged();
        }
    }

    public string ClientEditorGroupId
    {
        get => _clientEditorGroupId;
        private set => SetProperty(ref _clientEditorGroupId, value ?? string.Empty);
    }

    public string ClientEditorGroupPath
    {
        get => _clientEditorGroupPath;
        private set => SetProperty(ref _clientEditorGroupPath, string.IsNullOrWhiteSpace(value) ? "No Group" : value);
    }

    public IReadOnlyList<ClientGroupFolderRecord> ClientEditorGroupFolders
    {
        get
        {
            var data = IsGroupManagerOpen
                ? _clientGroupData
                : ClientGroupingCatalog.Load(_preferenceRepository.Get(ClientGroupingCatalog.PreferenceKey));
            return data.Folders.Select(x => new ClientGroupFolderRecord
            {
                Id = x.Id, Name = x.Name, Type = x.Type, ParentId = x.ParentId, LinkedCaProfileId = x.LinkedCaProfileId
            }).ToArray();
        }
    }

    public void SelectClientEditorGroup(string? folderId)
    {
        var id = (folderId ?? string.Empty).Trim();
        if (id.Length == 0)
        {
            ClientEditorGroupId = string.Empty;
            ClientEditorGroupPath = "No Group";
            return;
        }
        var data = IsGroupManagerOpen ? _clientGroupData : ClientGroupingCatalog.Load(_preferenceRepository.Get(ClientGroupingCatalog.PreferenceKey));
        var path = ClientGroupPath(data, id);
        if (path.Length == 0) return;
        ClientEditorGroupId = id;
        ClientEditorGroupPath = path;
    }

    public void BeginCreateGroupFromClientPicker(string? parentId)
    {
        if (NetworkClientRuntime.IsNetworkClient) return;
        if (!IsGroupManagerOpen)
        {
            _clientGroupData = ClientGroupingCatalog.Load(_preferenceRepository.Get(ClientGroupingCatalog.PreferenceKey));
            RebuildGroupCreateParents();
        }
        IsGroupFolderRenameMode = false;
        _groupCreateForClientPicker = true;
        _lastCreatedGroupId = string.Empty;
        _groupFolderEditorParentId = (parentId ?? string.Empty).Trim();
        _groupFolderEditorParentPath = _groupFolderEditorParentId.Length == 0 ? "Top level" : ClientGroupPath(_clientGroupData, _groupFolderEditorParentId);
        OnPropertyChanged(nameof(GroupFolderEditorParentPath));
        GroupFolderName = string.Empty;
        GroupFolderType = string.Empty;
        GroupFolderCaProfile = null;
        GroupManagerStatus = _groupFolderEditorParentId.Length == 0
            ? "Create the new top-level Group. It will be selected for this Client automatically."
            : $"Create the new Group inside {_groupFolderEditorParentPath}. It will be selected for this Client automatically.";
        IsGroupFolderEditorOpen = true;
        SaveGroupFolderEditorCommand.RaiseCanExecuteChanged();
    }

    public bool IsGroupWiseDirectory
    {
        get => _isGroupWiseDirectory;
        private set
        {
            if (!SetProperty(ref _isGroupWiseDirectory, value)) return;
            _preferenceRepository.Set(ClientDirectoryViewModeSettingKey, value ? "Group" : "Client");
            _directoryGroupFolderId = string.Empty;
            OnPropertyChanged(nameof(IsClientWiseDirectory));
            OnPropertyChanged(nameof(ClientDirectoryViewModeText));
            OnPropertyChanged(nameof(ClientWiseDirectoryVisibility));
            OnPropertyChanged(nameof(GroupWiseDirectoryVisibility));
            RebuildClientDirectoryRows();
        }
    }
    public bool IsClientWiseDirectory => !IsGroupWiseDirectory;
    public string ClientDirectoryViewModeText => IsGroupWiseDirectory ? "Group Wise" : "Client Wise";
    public Visibility ClientWiseDirectoryVisibility => IsGroupWiseDirectory ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GroupWiseDirectoryVisibility => IsGroupWiseDirectory ? Visibility.Visible : Visibility.Collapsed;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool IncludeArchived
    {
        get => _includeArchived;
        set
        {
            if (SetProperty(ref _includeArchived, value))
            {
                LoadClients();
            }
        }
    }

    public bool IsEditorOpen
    {
        get => _isEditorOpen;
        private set => SetProperty(ref _isEditorOpen, value);
    }

    public bool IsLanAccessOpen
    {
        get => _isLanAccessOpen;
        private set
        {
            if (!SetProperty(ref _isLanAccessOpen, value)) return;
            SaveClientLanAccessCommand?.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<ClientUserAccessRow> LanAccessUsers { get; } = [];
    public ClientUserAccessRow? SelectedLanAccessUser
    {
        get => _selectedLanAccessUser;
        set { if (SetProperty(ref _selectedLanAccessUser, value)) RefreshSelectedMobileQr(); }
    }
    public System.Windows.Media.ImageSource? MobileUserQrImage { get => _mobileUserQrImage; private set => SetProperty(ref _mobileUserQrImage, value); }
    public string MobileUserQrText { get => _mobileUserQrText; private set => SetProperty(ref _mobileUserQrText, value); }
    public System.Windows.Media.ImageSource? HomeMobileQrImage { get => _homeMobileQrImage; private set => SetProperty(ref _homeMobileQrImage, value); }
    public string HomeMobileQrText { get => _homeMobileQrText; private set => SetProperty(ref _homeMobileQrText, value); }
    public string HomeMobileCode { get => _homeMobileCode; private set => SetProperty(ref _homeMobileCode, value); }
    public string HomeMobileUserCaption { get => _homeMobileUserCaption; private set => SetProperty(ref _homeMobileUserCaption, value); }

    public string LanAccessClientCaption => _lanAccessClient is null
        ? "Client User Access"
        : $"{_lanAccessClient.ClientCode} · {_lanAccessClient.ClientName}";

    public string LanAccessStatusText
    {
        get => _lanAccessStatusText;
        private set => SetProperty(ref _lanAccessStatusText, value);
    }

    public ObservableCollection<string> FinancialYearFilters { get; } = [];

    public string SelectedFinancialYearFilter
    {
        get => _selectedFinancialYearFilter;
        set
        {
            // WPF ComboBox can briefly push null/blank while FinancialYearFilters is
            // rebuilt. Never let that transient state blank the Client Directory filter.
            var normalized = string.IsNullOrWhiteSpace(value)
                ? AllFinancialYearsFilter
                : value.Trim();
            if (SetProperty(ref _selectedFinancialYearFilter, normalized))
            {
                ApplyFilter();
            }
        }
    }

    public string DisplayNameMode
    {
        get => _displayNameMode;
        private set
        {
            var normalized = ClientDisplayNameModes.Normalize(value);

            if (!SetProperty(ref _displayNameMode, normalized))
            {
                return;
            }

            _preferenceRepository.Set(
                DisplayNameSettingKey,
                normalized);
            _clientContext.SetDisplayNameMode(normalized);
            OnPropertyChanged(nameof(NameHeaderText));
            ApplyFilter();
        }
    }

    public string NameHeaderText =>
        DisplayNameMode;

    public string SortDescriptionText =>
        $"Sorted by {SortColumnLabel(_sortColumn)} " +
        (_sortAscending ? "ascending" : "descending");

    public string ClientCodeSortIndicator => SortIndicator("ClientCode");
    public string NameSortIndicator => SortIndicator("Name");
    public string GstinSortIndicator => SortIndicator("Gstin");
    public string StateSortIndicator => SortIndicator("State");
    public string FinancialYearSortIndicator => SortIndicator("FinancialYear");
    public string UpdatedSortIndicator => SortIndicator("Updated");

    public long EditingId
    {
        get => _editingId;
        private set
        {
            if (SetProperty(ref _editingId, value))
            {
                OnPropertyChanged(nameof(EditorModeText));
                OnPropertyChanged(nameof(CanImportPreviousFy));
                RefreshCommands();
            }
        }
    }

    public string ClientCode
    {
        get => _clientCode;
        set => SetProperty(ref _clientCode, value);
    }

    public string ClientName
    {
        get => _clientName;
        set => SetProperty(ref _clientName, value);
    }

    public string LegalName
    {
        get => _legalName;
        set
        {
            if (!SetProperty(ref _legalName, value))
            {
                return;
            }

            if (!_isApplyingGstinData)
            {
                _legalNameAutoFilled = false;
                _legalNameIsManual = true;
            }
        }
    }

    public string TradeName
    {
        get => _tradeName;
        set
        {
            if (!SetProperty(ref _tradeName, value))
            {
                return;
            }

            if (!_isApplyingGstinData)
            {
                _tradeNameAutoFilled = false;
                _tradeNameIsManual = true;
            }
        }
    }

    public string Gstin
    {
        get => _gstin;
        set
        {
            var normalized = NormalizeTaxNumber(value);

            if (!SetProperty(ref _gstin, normalized))
            {
                return;
            }

            ApplyDerivedGstinFields(normalized);
            ScheduleGstinLookup(normalized);
        }
    }

    // One secondary GSTIN per line. The primary GSTIN remains the existing
    // identity and compatibility authority for older accounting workflows.
    public string AdditionalGstinsText
    {
        get => _additionalGstinsText;
        set => SetProperty(ref _additionalGstinsText, value ?? string.Empty);
    }

    public string Pan
    {
        get => _pan;
        set
        {
            if (!SetProperty(ref _pan, NormalizeTaxNumber(value)))
            {
                return;
            }

            if (!_isApplyingGstinData)
            {
                _panAutoFilled = false;
            }
        }
    }

    public string State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value))
            {
                return;
            }

            if (!_isApplyingGstinData)
            {
                _stateAutoFilled = false;
            }
        }
    }

    public string FinancialYear
    {
        get => _financialYear;
        set => SetProperty(ref _financialYear, value);
    }

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public string Phone
    {
        get => _phone;
        set => SetProperty(ref _phone, value);
    }

    public string Email
    {
        get => _email;
        set => SetProperty(ref _email, value);
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    public ObservableCollection<ClientProfile> PreviousFyClientOptions { get; } = [];

    public bool CanImportPreviousFy => EditingId == 0 && !NetworkClientRuntime.IsNetworkClient;

    public bool ImportFromPreviousFy
    {
        get => _importFromPreviousFy;
        set
        {
            if (!SetProperty(ref _importFromPreviousFy, value)) return;
            OnPropertyChanged(nameof(PreviousFyImportStatus));
            if (value && SelectedPreviousFyClient is not null) ApplyPreviousFyClient(SelectedPreviousFyClient);
        }
    }

    public ClientProfile? SelectedPreviousFyClient
    {
        get => _selectedPreviousFyClient;
        set
        {
            if (!SetProperty(ref _selectedPreviousFyClient, value)) return;
            OnPropertyChanged(nameof(PreviousFyImportStatus));
            if (ImportFromPreviousFy && value is not null) ApplyPreviousFyClient(value);
        }
    }

    public string PreviousFyImportStatus => SelectedPreviousFyClient is null
        ? "Choose an existing Client/FY. AAH copies the client profile and advances the Financial Year; prior-year vouchers are not copied."
        : $"Source: {SelectedPreviousFyClient.ClientCode} · {SelectedPreviousFyClient.ClientName} · FY {SelectedPreviousFyClient.FinancialYear}. New FY: {NextFinancialYear(SelectedPreviousFyClient.FinancialYear)}.";

    public bool EditingArchived
    {
        get => _editingArchived;
        private set
        {
            if (!SetProperty(ref _editingArchived, value))
            {
                return;
            }

            OnPropertyChanged(nameof(EditorStatusText));
            RefreshCommands();
        }
    }

    public string Gstr3bProfileText
    {
        get
        {
            if (_latestGstr3bPeriod.Length == 0)
            {
                return "No GSTR-3B profile has been imported for this client.";
            }

            var parts = new List<string>
            {
                $"Latest GSTR-3B: {_latestGstr3bPeriod}"
            };

            if (_latestGstr3bArn.Length > 0)
            {
                parts.Add($"ARN {_latestGstr3bArn}");
            }

            if (_latestGstr3bArnDate.Length > 0)
            {
                parts.Add(_latestGstr3bArnDate);
            }

            return string.Join(" • ", parts);
        }
    }

    public string GstProfileSourceText
    {
        get
        {
            if (_profileSourcePath.Length == 0)
            {
                return "Automatic profile source: not found in current GST sources.";
            }

            var sourceName = Path.GetFileName(_profileSourcePath)
                ?? _profileSourcePath;
            var sourceType = _profileSourceType.Length > 0
                ? _profileSourceType
                : "GST source";
            return $"{sourceType}: {sourceName}";
        }
    }

    public bool HasGstr3bProfile =>
        _latestGstr3bPeriod.Length > 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ValidationText
    {
        get => _validationText;
        private set
        {
            if (!SetProperty(ref _validationText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasValidationError));
        }
    }

    public string LookupStatusText
    {
        get => _lookupStatusText;
        private set
        {
            if (!SetProperty(ref _lookupStatusText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasLookupStatus));
        }
    }

    public bool HasValidationError => ValidationText.Length > 0;

    public bool HasLookupStatus => LookupStatusText.Length > 0;

    public int TotalCount => _allClients.Count;

    public int ActiveCount => _allClients.Count(client => !client.IsArchived);

    public int ArchivedCount => _allClients.Count(client => client.IsArchived);

    public int VisibleCount => Clients.Count;

    public int GstinCount => _allClients.Where(client => !client.IsArchived)
        .Sum(client => (client.Gstin.Length == 15 ? 1 : 0) +
            (client.AdditionalGstins?.Count ?? 0));

    public int NeedsProfileCount => _allClients.Count(client =>
        !client.IsArchived &&
        (client.LegalName.Length == 0 || client.TradeName.Length == 0));

    public string EditorModeText => EditingId == 0 ? "Add new client" : "Edit client";

    public string EditorStatusText =>
        EditingArchived
            ? "Archived"
            : EditingId == 0
                ? "Unsaved"
                : "Active record";

    public RelayCommand NewCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand SetCurrentCommand { get; }

    public RelayCommand EditSelectedCommand { get; }

    public RelayCommand CloseEditorCommand { get; }

    public RelayCommand CycleDisplayNameModeCommand { get; }

    public ParameterRelayCommand SortAscendingCommand { get; }

    public ParameterRelayCommand SortDescendingCommand { get; }

    public ParameterRelayCommand ToggleSortCommand { get; }

    public RelayCommand ArchiveCommand { get; }

    public RelayCommand RestoreCommand { get; }

    public RelayCommand DeleteClientCommand { get; }

    public RelayCommand ReloadCommand { get; }
    public RelayCommand OpenGroupManagerCommand { get; }
    public RelayCommand CloseGroupManagerCommand { get; }

    public void CloseGroupManager()
    {
        IsGroupFolderEditorOpen = false;
        IsGroupManagerOpen = false;
    }
    public RelayCommand AddGroupFolderCommand { get; }
    public RelayCommand RenameGroupFolderCommand { get; }
    public RelayCommand DeleteGroupFolderCommand { get; }
    public RelayCommand AssignClientToGroupCommand { get; }
    public RelayCommand UnassignClientFromGroupCommand { get; }
    public RelayCommand SaveGroupCaLinkCommand { get; }
    public RelayCommand OpenSelectedGroupChildFolderCommand { get; }
    public RelayCommand OpenSelectedGroupBrowserItemCommand { get; }
    public RelayCommand RemoveSelectedGroupBrowserClientCommand { get; }
    public RelayCommand BeginAddGroupFolderCommand { get; }
    public RelayCommand BeginRenameGroupFolderCommand { get; }
    public RelayCommand SaveGroupFolderEditorCommand { get; }
    public RelayCommand CancelGroupFolderEditorCommand { get; }
    public RelayCommand OpenGroupClientPickerCommand { get; }
    public RelayCommand AddSelectedClientsToGroupCommand { get; }
    public RelayCommand CancelGroupClientPickerCommand { get; }
    public RelayCommand OpenGroupMoveFolderCommand { get; }
    public RelayCommand MoveGroupFolderCommand { get; }
    public RelayCommand CancelGroupMoveFolderCommand { get; }
    public RelayCommand MoveSelectedGroupSourceCommand { get; }
    public ParameterRelayCommand MoveSelectedGroupSourceToRootCommand { get; }
    public RelayCommand ToggleClientDirectoryViewCommand { get; }
    public ParameterRelayCommand SetClientDirectoryViewCommand { get; }
    public ParameterRelayCommand SetGroupRootScopeCommand { get; }

    public AsyncRelayCommand RefreshProfileCommand { get; }

    public ParameterRelayCommand ToggleClientLanAvailabilityCommand { get; }

    public ParameterRelayCommand OpenClientLanAccessCommand { get; }

    public RelayCommand SaveClientLanAccessCommand { get; }

    public RelayCommand CloseClientLanAccessCommand { get; }

    public void RefreshFromExternalChange()
    {
        // Automation Folder linking/profile bootstrap can change the Client registry
        // without opening this page. Preserve search/sort/filter and reload live data.
        LoadClients();
    }

    private void OpenGroupManager()
    {
        IsGroupFolderEditorOpen = false;
        IsGroupClientPickerOpen = false;
        IsGroupMoveFolderOpen = false;
        LoadGroupManager();
        IsGroupManagerOpen = true;
    }

    private void LoadGroupManager()
    {
        _clientGroupData = ClientGroupingCatalog.Load(_preferenceRepository.Get(ClientGroupingCatalog.PreferenceKey));
        GroupCaProfiles.Clear();
        try
        {
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<CaProfileRow>>(_preferenceRepository.Get(SettingsViewModel.CaProfilesPreferenceKey)) ?? [];
            var migratedIdentity = false;
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.ProfileId)) { row.ProfileId = Guid.NewGuid().ToString("N"); migratedIdentity = true; }
                GroupCaProfiles.Add(row);
            }
            if (migratedIdentity) _preferenceRepository.Set(SettingsViewModel.CaProfilesPreferenceKey, System.Text.Json.JsonSerializer.Serialize(rows));
        }
        catch { }

        // R14: a CA Group's visible name is the linked saved CA name. Repair older
        // folders once on load so legacy manually-entered CA Group names cannot drift.
        var repairedCaNames = false;
        foreach (var folder in _clientGroupData.Folders.Where(x =>
                     x.Type.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(x.LinkedCaProfileId)))
        {
            var profile = GroupCaProfiles.FirstOrDefault(x => x.ProfileId.Equals(folder.LinkedCaProfileId, StringComparison.OrdinalIgnoreCase));
            if (profile is null || string.IsNullOrWhiteSpace(profile.CaName) ||
                folder.Name.Equals(profile.CaName.Trim(), StringComparison.Ordinal))
                continue;
            folder.Name = profile.CaName.Trim();
            repairedCaNames = true;
        }
        if (repairedCaNames) SaveGroupManager();

        RebuildGroupTree();
        SelectedGroupFolder ??= GroupFolderRoots.FirstOrDefault();
        RefreshGroupClientLists();
        RefreshGroupSourceItems();
        RebuildClientDirectoryGroupTree();
        GroupManagerStatus = "Create Groups at the top level, then drag/move a Group into another Group only when you want nesting. Clients can be dragged into any Group.";
    }

    private void SaveGroupManager()
        => _preferenceRepository.Set(ClientGroupingCatalog.PreferenceKey, ClientGroupingCatalog.Save(_clientGroupData));

    private void RebuildGroupTree()
    {
        var selectedId = SelectedGroupFolder?.Id;
        // R23: preserve only the user's current expand/collapse choices. Rebuilding after
        // a Client/Group move must never force the whole hierarchy open again.
        var expandedGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // R24: capture expansion iteratively. Expansion preservation runs during every
        // move, so it must not add another recursive walk on top of hierarchy rebuilds.
        var expansionStack = new Stack<ClientGroupFolderNode>(GroupFolderRoots.Reverse());
        while (expansionStack.Count > 0)
        {
            var existing = expansionStack.Pop();
            if (!existing.IsClient && existing.IsExpanded) expandedGroupIds.Add(existing.Id);
            for (var i = existing.Children.Count - 1; i >= 0; i--)
                expansionStack.Push(existing.Children[i]);
        }
        GroupFolderRoots.Clear();

        var assignedClientIds = _clientGroupData.Assignments
            .GroupBy(x => x.ClientId)
            .ToDictionary(x => x.Key, x => x.Last().FolderId);
        var clientsByFolder = assignedClientIds
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x.Select(y => _allClients.FirstOrDefault(c => c.Id == y.Key))
                    .Where(c => c is not null && !c.IsArchived)
                    .Cast<ClientProfile>()
                    .OrderBy(c => c.ClientName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(c => c.FinancialYear)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var byParent = _clientGroupData.Folders
            .GroupBy(x => x.ParentId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.Name, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);

        ClientGroupFolderNode Build(ClientGroupFolderRecord record, string parentPath, HashSet<string>? ancestry = null)
        {
            ancestry ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var node = new ClientGroupFolderNode(record, string.IsNullOrWhiteSpace(parentPath) ? record.Name : $"{parentPath} / {record.Name}")
            {
                IsExpanded = expandedGroupIds.Contains(record.Id)
            };
            if (!ancestry.Add(record.Id)) return node; // corrupt legacy cycle: stop at the repeated Group
            if (byParent.TryGetValue(record.Id, out var children))
            {
                foreach (var child in children)
                {
                    if (ancestry.Contains(child.Id)) continue;
                    node.Children.Add(Build(child, node.Path, new HashSet<string>(ancestry, StringComparer.OrdinalIgnoreCase)));
                }
            }
            if (clientsByFolder.TryGetValue(record.Id, out var clients))
                foreach (var client in clients)
                    node.Children.Add(ClientGroupFolderNode.ForClient(client, $"{node.Path} / {client.ClientName}"));
            ancestry.Remove(record.Id);
            return node;
        }

        // R20: every created Group is real. Any Group with blank ParentId is topmost and
        // therefore appears on the right immediately, including Firm/Company/CA/Auditor/
        // Company Group. Firm is optional; when used it remains topmost by catalog rule.
        foreach (var root in _clientGroupData.Folders
                     .Where(x => string.IsNullOrWhiteSpace(x.ParentId))
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            GroupFolderRoots.Add(Build(root, string.Empty));
        }

        RebuildGroupCreateParents();
        SelectedGroupFolder = FindGroupNode(selectedId) ?? GroupFolderRoots.FirstOrDefault();
        RebuildClientDirectoryGroupTree();
    }

    private void RebuildGroupCreateParents()
    {
        var selectedId = SelectedGroupCreateParent?.Id;
        GroupCreateParentChoices.Clear();
        var byId = _clientGroupData.Folders.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        string PathFor(ClientGroupFolderRecord folder)
        {
            var names = new List<string>();
            var current = folder;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (current is not null && visited.Add(current.Id))
            {
                names.Add(current.Name);
                if (string.IsNullOrWhiteSpace(current.ParentId) || !byId.TryGetValue(current.ParentId, out current))
                    break;
            }
            names.Reverse();
            return string.Join(" / ", names);
        }

        foreach (var folder in _clientGroupData.Folders
                     .OrderBy(x => PathFor(x), StringComparer.OrdinalIgnoreCase))
        {
            GroupCreateParentChoices.Add(new ClientGroupCreateParentChoice(folder.Id, PathFor(folder)));
        }

        SelectedGroupCreateParent = GroupCreateParentChoices.FirstOrDefault(x => x.Id.Equals(selectedId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            ?? GroupCreateParentChoices.FirstOrDefault(x => x.Id.Equals(SelectedGroupFolder?.Id ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            ?? GroupCreateParentChoices.FirstOrDefault();
    }

    private ClientGroupFolderNode? FindGroupNode(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        ClientGroupFolderNode? Find(IEnumerable<ClientGroupFolderNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return node;
                var child = Find(node.Children);
                if (child is not null) return child;
            }
            return null;
        }
        return Find(GroupFolderRoots);
    }

    private void RefreshGroupSourceItems()
    {
        GroupSourceItems.Clear();
        var assignedClientIds = _clientGroupData.Assignments
            .Select(x => x.ClientId)
            .ToHashSet();

        // The left pane is a source list, not a mirror of the destination tree.
        // Once a Client is assigned to any Group it disappears from this list immediately.
        foreach (var client in _allClients.Where(x => !x.IsArchived && !assignedClientIds.Contains(x.Id))
                     .OrderBy(x => x.ClientName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.FinancialYear))
        {
            GroupSourceItems.Add(ClientGroupSourceItem.ForClient(client, "Not grouped"));
        }

        // R20: topmost Groups are visible in both places: the left source list (so they
        // can be nested later) and the right complete hierarchy. Once nested they disappear
        // from the left source list but remain in the right tree under their saved parent.
        foreach (var folder in _clientGroupData.Folders
                     .Where(x => string.IsNullOrWhiteSpace(x.ParentId))
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var node = new ClientGroupFolderNode(folder, folder.Name);
            GroupSourceItems.Add(ClientGroupSourceItem.ForFolder(node, "Top level"));
        }

        SelectedGroupSourceItem = null;
    }

    public bool CanMoveGroupSourceTo(ClientGroupSourceItem? source, ClientGroupFolderNode? destination, out string reason)
    {
        reason = string.Empty;
        if (source is null || destination is null) { reason = "Select an existing Client/Group and a destination Group."; return false; }
        if (destination.IsClient) { reason = "Clients are leaves. Drop onto a Group, not onto another Client."; return false; }

        if (source.Client is not null)
        {
            var current = _clientGroupData.Assignments.LastOrDefault(x => x.ClientId == source.Client.Id);
            if (current is not null && current.FolderId.Equals(destination.Id, StringComparison.OrdinalIgnoreCase))
            {
                reason = "That Client is already inside the selected Group.";
                return false;
            }
            return true;
        }

        var sourceFolder = source.Folder;
        if (sourceFolder is null) { reason = "The selected Group no longer exists."; return false; }
        if (sourceFolder.Type.Equals(ClientGroupFolderTypes.Firm, StringComparison.OrdinalIgnoreCase)) { reason = "A Firm is already the highest hierarchy level and cannot be moved inside another Group."; return false; }
        if (sourceFolder.Id.Equals(destination.Id, StringComparison.OrdinalIgnoreCase))
        {
            reason = "A Group cannot be moved into itself.";
            return false;
        }

        var record = _clientGroupData.Folders.FirstOrDefault(x => x.Id.Equals(sourceFolder.Id, StringComparison.OrdinalIgnoreCase));
        if (record is null) { reason = "The selected Group no longer exists."; return false; }
        if (record.ParentId.Equals(destination.Id, StringComparison.OrdinalIgnoreCase))
        {
            reason = "That Group is already directly inside the selected Group.";
            return false;
        }

        var byId = _clientGroupData.Folders.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var cursor = destination.Id;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrWhiteSpace(cursor) && visited.Add(cursor) && byId.TryGetValue(cursor, out var currentFolder))
        {
            if (cursor.Equals(sourceFolder.Id, StringComparison.OrdinalIgnoreCase))
            {
                reason = "A Group cannot be moved into one of its own child Groups.";
                return false;
            }
            cursor = currentFolder.ParentId;
        }
        return true;
    }

    public void MoveGroupSourceTo(ClientGroupSourceItem? source, ClientGroupFolderNode? destination)
    {
        if (!CanMoveGroupSourceTo(source, destination, out var reason))
        {
            GroupManagerStatus = reason;
            return;
        }

        if (source!.Client is not null)
        {
            _clientGroupData.Assignments.RemoveAll(x => x.ClientId == source.Client.Id);
            _clientGroupData.Assignments.Add(new ClientGroupAssignmentRecord { ClientId = source.Client.Id, FolderId = destination!.Id });
            GroupManagerStatus = $"Moved {source.Client.ClientName} to {destination.Path}.";
        }
        else if (source.Folder is not null)
        {
            var record = _clientGroupData.Folders.First(x => x.Id.Equals(source.Folder.Id, StringComparison.OrdinalIgnoreCase));
            record.ParentId = destination!.Id;
            GroupManagerStatus = $"Moved {source.Folder.DisplayText} to {destination.Path}.";
        }

        SaveGroupManager();
        var destinationId = destination!.Id;
        RebuildGroupTree();
        SelectedGroupFolder = FindGroupNode(destinationId) ?? GroupFolderRoots.FirstOrDefault();
        RefreshGroupSourceItems();
        RebuildClientDirectoryGroupTree();
    }

    private void MoveSelectedGroupSource()
        => MoveGroupSourceTo(SelectedGroupSourceItem, SelectedGroupFolder);

    public bool CanMoveGroupSourceToRoot(ClientGroupSourceItem? source, string? rootScope, out string reason)
    {
        reason = string.Empty;
        if (source?.Folder is null)
        {
            reason = "Select a Group first.";
            return false;
        }
        var record = _clientGroupData.Folders.FirstOrDefault(x => x.Id.Equals(source.Folder.Id, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            reason = "The selected Group no longer exists.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(record.ParentId))
        {
            reason = "That Group is already at the top level.";
            return false;
        }
        return true;
    }

    public void MoveGroupSourceToRoot(ClientGroupSourceItem? source, string? rootScope)
    {
        if (!CanMoveGroupSourceToRoot(source, rootScope, out var reason))
        {
            GroupManagerStatus = reason;
            return;
        }
        var sourceFolder = source!.Folder!;
        var record = _clientGroupData.Folders.First(x => x.Id.Equals(sourceFolder.Id, StringComparison.OrdinalIgnoreCase));
        record.ParentId = string.Empty;
        SaveGroupManager();
        RebuildGroupTree();
        SelectedGroupFolder = FindGroupNode(record.Id);
        RefreshGroupSourceItems();
        RebuildClientDirectoryGroupTree();
        GroupManagerStatus = $"Moved {sourceFolder.DisplayText} to the top level.";
    }

    private void MoveSelectedGroupSourceToRoot(string? rootScope)
        => MoveGroupSourceToRoot(SelectedGroupSourceItem, rootScope);

    private void ToggleClientDirectoryView()
    {
        IsGroupWiseDirectory = !IsGroupWiseDirectory;
    }

    private void SetClientDirectoryView(string? mode)
    {
        IsGroupWiseDirectory = string.Equals(mode, "Group", StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildClientDirectoryGroupTree()
    {
        ClientDirectoryGroupRoots.Clear();
        var grouping = IsGroupManagerOpen
            ? _clientGroupData
            : ClientGroupingCatalog.Load(_preferenceRepository.Get(ClientGroupingCatalog.PreferenceKey));

        var visibleClients = Clients.ToDictionary(x => x.Id);
        var assignmentByClient = grouping.Assignments
            .Where(x => visibleClients.ContainsKey(x.ClientId))
            .GroupBy(x => x.ClientId)
            .ToDictionary(x => x.Key, x => x.Last().FolderId);
        var clientsByFolder = assignmentByClient
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => visibleClients[y.Key]).OrderBy(c => c.ClientName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.FinancialYear).ToArray(), StringComparer.OrdinalIgnoreCase);
        var foldersByParent = grouping.Folders
            .GroupBy(x => x.ParentId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.Name, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);

        ClientDirectoryGroupNode BuildFolder(ClientGroupFolderRecord record, HashSet<string>? ancestry = null)
        {
            ancestry ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var node = ClientDirectoryGroupNode.ForFolder(record);
            if (!ancestry.Add(record.Id)) return node;
            if (foldersByParent.TryGetValue(record.Id, out var children))
            {
                foreach (var child in children)
                {
                    if (ancestry.Contains(child.Id)) continue;
                    var childNode = BuildFolder(child, new HashSet<string>(ancestry, StringComparer.OrdinalIgnoreCase));
                    node.Children.Add(childNode);
                }
            }
            if (clientsByFolder.TryGetValue(record.Id, out var groupedClients))
            {
                foreach (var client in groupedClients) node.Children.Add(ClientDirectoryGroupNode.ForClient(client));
            }
            ancestry.Remove(record.Id);
            return node;
        }

        // R20: Client Directory Group Wise reads the same saved hierarchy. Every
        // parentless Group is topmost; no synthetic Firm/Company rows are invented.
        foreach (var topLevel in grouping.Folders
                     .Where(x => string.IsNullOrWhiteSpace(x.ParentId))
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            ClientDirectoryGroupRoots.Add(BuildFolder(topLevel));
        }

        var groupedClientIds = assignmentByClient.Keys.ToHashSet();
        foreach (var client in Clients.Where(x => !groupedClientIds.Contains(x.Id)))
            ClientDirectoryGroupRoots.Add(ClientDirectoryGroupNode.ForClient(client));

        RebuildClientDirectoryRows();
    }

    private void RebuildClientDirectoryRows()
    {
        var selectedClientId = SelectedClient?.Id ?? SelectedDirectoryItem?.Client?.Id ?? 0;
        ClientDirectoryItems.Clear();

        if (!IsGroupWiseDirectory)
        {
            _directoryGroupFolderId = string.Empty;
            foreach (var client in Clients)
                ClientDirectoryItems.Add(ClientDirectoryDisplayItem.ForClient(client));
        }
        else
        {
            var grouping = IsGroupManagerOpen
                ? _clientGroupData
                : ClientGroupingCatalog.Load(_preferenceRepository.Get(ClientGroupingCatalog.PreferenceKey));
            var visibleClients = Clients.ToDictionary(x => x.Id);
            var assignmentByClient = grouping.Assignments
                .Where(x => visibleClients.ContainsKey(x.ClientId))
                .GroupBy(x => x.ClientId)
                .ToDictionary(x => x.Key, x => x.Last().FolderId);
            var directClientsByFolder = assignmentByClient
                .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    x => x.Key,
                    x => x.Select(y => visibleClients[y.Key]).OrderBy(c => c.ClientName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.FinancialYear).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var foldersByParent = grouping.Folders
                .GroupBy(x => x.ParentId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
            var directClientCounts = directClientsByFolder.ToDictionary(x => x.Key, x => x.Value.Length, StringComparer.OrdinalIgnoreCase);
            var byId = grouping.Folders.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            var countCache = new Dictionary<string, (int Groups, int Clients)>(StringComparer.OrdinalIgnoreCase);
            var clientCache = new Dictionary<string, IReadOnlyList<ClientProfile>>(StringComparer.OrdinalIgnoreCase);

            (int Groups, int Clients) Totals(string folderId, HashSet<string>? ancestry = null)
            {
                if (countCache.TryGetValue(folderId, out var cached)) return cached;
                ancestry ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!ancestry.Add(folderId)) return (0, 0);
                var groups = 0;
                var clients = directClientCounts.TryGetValue(folderId, out var directClients) ? directClients : 0;
                if (foldersByParent.TryGetValue(folderId, out var children))
                {
                    foreach (var child in children)
                    {
                        if (ancestry.Contains(child.Id)) continue;
                        groups++;
                        var nested = Totals(child.Id, new HashSet<string>(ancestry, StringComparer.OrdinalIgnoreCase));
                        groups += nested.Groups;
                        clients += nested.Clients;
                    }
                }
                ancestry.Remove(folderId);
                return countCache[folderId] = (groups, clients);
            }

            IReadOnlyList<ClientProfile> ClientsInSubtree(string folderId, HashSet<string>? ancestry = null)
            {
                if (clientCache.TryGetValue(folderId, out var cached)) return cached;
                ancestry ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!ancestry.Add(folderId)) return Array.Empty<ClientProfile>();
                var rows = new List<ClientProfile>();
                if (directClientsByFolder.TryGetValue(folderId, out var direct)) rows.AddRange(direct);
                if (foldersByParent.TryGetValue(folderId, out var children))
                {
                    foreach (var child in children)
                    {
                        if (ancestry.Contains(child.Id)) continue;
                        rows.AddRange(ClientsInSubtree(child.Id, new HashSet<string>(ancestry, StringComparer.OrdinalIgnoreCase)));
                    }
                }
                var result = rows.GroupBy(x => x.Id).Select(x => x.First()).ToArray();
                clientCache[folderId] = result;
                return result;
            }

            string PathFor(ClientGroupFolderRecord folder)
            {
                var names = new List<string>();
                var current = folder;
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (current is not null && visited.Add(current.Id))
                {
                    names.Add(current.Name);
                    if (string.IsNullOrWhiteSpace(current.ParentId) || !byId.TryGetValue(current.ParentId, out current))
                        break;
                }
                names.Reverse();
                return string.Join(" / ", names);
            }

            var caProfiles = Array.Empty<CaProfileRow>();
            var firmProfiles = Array.Empty<FirmProfileRow>();
            try { caProfiles = (System.Text.Json.JsonSerializer.Deserialize<List<CaProfileRow>>(_preferenceRepository.Get(SettingsViewModel.CaProfilesPreferenceKey)) ?? []).ToArray(); }
            catch { }
            try { firmProfiles = (System.Text.Json.JsonSerializer.Deserialize<List<FirmProfileRow>>(_preferenceRepository.Get(SettingsViewModel.FirmProfilesPreferenceKey)) ?? []).ToArray(); }
            catch { }

            CaProfileRow? CaFor(ClientGroupFolderRecord folder)
                => folder.Type.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(folder.LinkedCaProfileId)
                    ? caProfiles.FirstOrDefault(x => x.ProfileId.Equals(folder.LinkedCaProfileId, StringComparison.OrdinalIgnoreCase))
                    : null;

            ClientDirectoryDisplayItem GroupItem(ClientGroupFolderRecord folder)
            {
                var total = Totals(folder.Id);
                var clients = ClientsInSubtree(folder.Id);
                var directGroups = foldersByParent.TryGetValue(folder.Id, out var childRows) ? childRows.Length : 0;
                var directClients = directClientCounts.TryGetValue(folder.Id, out var dc) ? dc : 0;
                var summary = $"{total.Groups:N0} Group{(total.Groups == 1 ? string.Empty : "s")} • {total.Clients:N0} Client{(total.Clients == 1 ? string.Empty : "s")}";

                var years = clients.Select(x => x.FinancialYear).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                var fyText = years.Length == 0 ? "No FY data" : string.Join(", ", years);
                var gstins = clients.Select(x => x.Gstin).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var gstinText = gstins.Length switch { 0 => "No GSTIN", 1 => gstins[0], _ => $"{gstins.Length:N0} GSTINs" };
                // Group Wise must aggregate the real statutory State, not count spelling/legacy-state variants
                // as separate States. Prefer the GSTIN State code when available, then fall back to the saved State.
                var states = clients
                    .Select(x =>
                    {
                        var gstinState = StateFromGstin(x.Gstin);
                        return !string.IsNullOrWhiteSpace(gstinState) ? gstinState : (x.State ?? string.Empty).Trim();
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var stateText = states.Length switch { 0 => "No State", 1 => states[0], _ => $"{states.Length} States" };
                var remoteEnabled = clients.Count(x => x.DirectoryLanAvailable);
                var remoteText = clients.Count switch
                {
                    0 => "No clients",
                    1 => clients[0].DirectoryLanAccessText,
                    _ => $"{remoteEnabled:N0}/{clients.Count:N0} enabled"
                };
                var dataModules = clients.SelectMany(x => (x.DirectoryDataSummary ?? string.Empty).Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    .Where(x => !x.Equals("No data", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                var dataText = dataModules.Length == 0 ? "No data" : string.Join(" • ", dataModules);
                var profileCount = clients.Count(x => !string.IsNullOrWhiteSpace(x.GstProfileSourceType) || !string.IsNullOrWhiteSpace(x.LatestGstr3bPeriod));
                var latestProfile = clients.Where(x => !string.IsNullOrWhiteSpace(x.LatestGstr3bPeriod))
                    .OrderByDescending(x => x.LatestGstr3bPeriodEndKey, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(x => x.LatestGstr3bPeriod, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                var latestActivity = clients.OrderByDescending(x => x.DirectoryLastActivityUtc == default ? x.UpdatedUtc : x.DirectoryLastActivityUtc).FirstOrDefault();
                var updatedText = latestActivity?.DirectoryLastActivityText ?? "No client activity";
                var profileText = clients.Count switch
                {
                    0 => "No clients",
                    1 => string.IsNullOrWhiteSpace(clients[0].GstProfileSourceType) ? "No GST profile" : clients[0].GstProfileSourceType,
                    _ => $"{profileCount:N0}/{clients.Count:N0} profiled"
                };

                var tooltip = $"Group Type: {folder.Type}\n" +
                              $"Path: {PathFor(folder)}\n" +
                              $"FY: {(years.Length == 0 ? "No client/FY data" : string.Join(", ", years))}\n" +
                              $"Direct Groups: {directGroups:N0}\n" +
                              $"Total Nested Groups: {total.Groups:N0}\n" +
                              $"Direct Clients: {directClients:N0}\n" +
                              $"Total Clients: {total.Clients:N0}\n" +
                              "Double-click to drill down.";
                var ca = CaFor(folder);
                if (ca is not null)
                {
                    tooltip += $"\nLinked CA: {ca.CaName}";
                    var firm = !string.IsNullOrWhiteSpace(ca.FirmProfileId)
                        ? firmProfiles.FirstOrDefault(x => x.FirmId.Equals(ca.FirmProfileId, StringComparison.OrdinalIgnoreCase))
                        : null;
                    var firmName = firm?.FirmName ?? ca.FirmName;
                    if (!string.IsNullOrWhiteSpace(firmName)) tooltip += $"\nLinked Firm: {firmName}";
                }
                return ClientDirectoryDisplayItem.ForGroup(folder, summary, tooltip, gstinText, stateText, fyText, remoteText,
                    dataText, latestProfile?.LatestGstr3bPeriod ?? "No 3B", profileText, updatedText);
            }

            var fyScoped = !SelectedFinancialYearFilter.Equals(AllFinancialYearsFilter, StringComparison.OrdinalIgnoreCase);
            bool HasVisibleFyData(ClientGroupFolderRecord folder) => !fyScoped || Totals(folder.Id).Clients > 0;

            if (string.IsNullOrWhiteSpace(_directoryGroupFolderId))
            {
                foreach (var topLevel in grouping.Folders
                             .Where(x => string.IsNullOrWhiteSpace(x.ParentId) && HasVisibleFyData(x))
                             .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    ClientDirectoryItems.Add(GroupItem(topLevel));
                }

                var groupedClientIds = assignmentByClient.Keys.ToHashSet();
                foreach (var client in Clients.Where(x => !groupedClientIds.Contains(x.Id)))
                    ClientDirectoryItems.Add(ClientDirectoryDisplayItem.ForClient(client));
            }
            else
            {
                var current = grouping.Folders.FirstOrDefault(x => x.Id.Equals(_directoryGroupFolderId, StringComparison.OrdinalIgnoreCase));
                if (current is null || !HasVisibleFyData(current))
                {
                    _directoryGroupFolderId = string.Empty;
                    RebuildClientDirectoryRows();
                    return;
                }

                ClientDirectoryItems.Add(ClientDirectoryDisplayItem.ForBack(current.ParentId, current.Name));

                foreach (var child in grouping.Folders
                             .Where(x => x.ParentId.Equals(current.Id, StringComparison.OrdinalIgnoreCase) && HasVisibleFyData(x))
                             .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    ClientDirectoryItems.Add(GroupItem(child));
                }

                var directClientIds = assignmentByClient
                    .Where(x => x.Value.Equals(current.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Key)
                    .ToHashSet();
                foreach (var client in Clients.Where(x => directClientIds.Contains(x.Id)))
                    ClientDirectoryItems.Add(ClientDirectoryDisplayItem.ForClient(client));
            }
        }

        SelectedDirectoryItem = selectedClientId > 0
            ? ClientDirectoryItems.FirstOrDefault(x => x.Client?.Id == selectedClientId)
            : null;
    }

    public bool NavigateClientDirectoryBack(bool allTheWay)
    {
        if (!IsGroupWiseDirectory || string.IsNullOrWhiteSpace(_directoryGroupFolderId)) return false;
        if (allTheWay)
        {
            _directoryGroupFolderId = string.Empty;
            RebuildClientDirectoryRows();
            return true;
        }

        var grouping = IsGroupManagerOpen
            ? _clientGroupData
            : ClientGroupingCatalog.Load(_preferenceRepository.Get(ClientGroupingCatalog.PreferenceKey));
        var current = grouping.Folders.FirstOrDefault(x => x.Id.Equals(_directoryGroupFolderId, StringComparison.OrdinalIgnoreCase));
        _directoryGroupFolderId = current?.ParentId ?? string.Empty;
        RebuildClientDirectoryRows();
        return true;
    }

    public bool OpenSelectedDirectoryItem()
    {
        var item = SelectedDirectoryItem;
        if (item is null) return false;

        if (item.IsNavigation)
            return NavigateClientDirectoryBack(allTheWay: false);

        if (item.Folder is not null)
        {
            _directoryGroupFolderId = item.Folder.Id;
            RebuildClientDirectoryRows();
            return true;
        }

        if (item.Client is not null)
        {
            SelectedClient = Clients.FirstOrDefault(x => x.Id == item.Client.Id) ?? _allClients.FirstOrDefault(x => x.Id == item.Client.Id);
            if (SelectedClient is not null && SetCurrentCommand.CanExecute(null)) SetCurrentCommand.Execute(null);
            return true;
        }

        return false;
    }

    public void SelectClientFromDirectoryGroupNode(ClientDirectoryGroupNode? node, bool open)
    {
        if (node?.Client is null) return;
        SelectedClient = Clients.FirstOrDefault(x => x.Id == node.Client.Id) ?? _allClients.FirstOrDefault(x => x.Id == node.Client.Id);
        if (open && SelectedClient is not null && SetCurrentCommand.CanExecute(null)) SetCurrentCommand.Execute(null);
    }

    private void OpenSelectedGroupChildFolder()
    {
        if (SelectedGroupChildFolder is null) return;
        SelectedGroupFolder = FindGroupNode(SelectedGroupChildFolder.Id);
    }

    private static string ClientGroupPath(ClientGroupCatalogData data, string folderId)
    {
        var byId = data.Folders.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        if (!byId.TryGetValue(folderId, out var current)) return string.Empty;
        var names = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (current is not null && visited.Add(current.Id))
        {
            names.Add(current.Name);
            if (string.IsNullOrWhiteSpace(current.ParentId) || !byId.TryGetValue(current.ParentId, out current)) break;
        }
        names.Reverse();
        return string.Join(" > ", names);
    }

    private static string RootIdForScope(string? scope) =>
        string.Equals(scope, ClientGroupFolderTypes.RootCompany, StringComparison.OrdinalIgnoreCase)
            ? ClientGroupingCatalog.RootCompanyId
            : ClientGroupingCatalog.RootFirmId;

    private void SetGroupRootScope(string? scope)
    {
        var normalized = ClientGroupFolderTypes.Normalize(scope);
        if (!ClientGroupFolderTypes.IsRoot(normalized)) return;
        if (!_groupRootScope.Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            _groupRootScope = normalized;
            OnPropertyChanged(nameof(GroupRootScope));
            OnPropertyChanged(nameof(IsGroupRootFirmSelected));
            OnPropertyChanged(nameof(IsGroupRootCompanySelected));
            OnPropertyChanged(nameof(SelectedGroupPath));
        }
        GroupManagerStatus = $"Hierarchy root: {_groupRootScope}. Drag an unplaced Group here to place it at the top level.";
    }

    private void SyncGroupRootScopeFromFolder(string folderId)
    {
        var byId = _clientGroupData.Folders.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        if (!byId.TryGetValue(folderId, out var current)) return;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (current is not null && visited.Add(current.Id))
        {
            if (current.Id.Equals(ClientGroupingCatalog.RootFirmId, StringComparison.OrdinalIgnoreCase))
            {
                _groupRootScope = ClientGroupFolderTypes.RootFirm;
                OnPropertyChanged(nameof(GroupRootScope));
                OnPropertyChanged(nameof(IsGroupRootFirmSelected));
                OnPropertyChanged(nameof(IsGroupRootCompanySelected));
                return;
            }
            if (current.Id.Equals(ClientGroupingCatalog.RootCompanyId, StringComparison.OrdinalIgnoreCase))
            {
                _groupRootScope = ClientGroupFolderTypes.RootCompany;
                OnPropertyChanged(nameof(GroupRootScope));
                OnPropertyChanged(nameof(IsGroupRootFirmSelected));
                OnPropertyChanged(nameof(IsGroupRootCompanySelected));
                return;
            }
            if (string.IsNullOrWhiteSpace(current.ParentId) || !byId.TryGetValue(current.ParentId, out current)) break;
        }
    }

    private void BeginAddGroupFolder()
    {
        IsGroupFolderRenameMode = false;
        _groupCreateForClientPicker = false;
        _lastCreatedGroupId = string.Empty;

        // R20: creation and hierarchy are separate operations. Every new Firm/Company/
        // CA/Auditor/Company Group starts topmost (blank ParentId) and appears in the
        // right hierarchy immediately. Drag/move is used only when nesting is wanted.
        _groupFolderEditorParentId = string.Empty;
        _groupFolderEditorParentPath = string.Empty;
        OnPropertyChanged(nameof(GroupFolderEditorParentPath));

        GroupFolderName = string.Empty;
        GroupFolderType = string.Empty;
        GroupFolderCaProfile = null;
        GroupManagerStatus = "Create the Group. It starts at the top level and appears immediately in the right hierarchy; drag/move it into another Group only when nesting is required.";
        IsGroupFolderEditorOpen = true;
        SaveGroupFolderEditorCommand.RaiseCanExecuteChanged();
    }

    private void BeginRenameGroupFolder()
    {
        if (SelectedGroupFolder is null || SelectedGroupFolder.IsRoot) return;
        IsGroupFolderRenameMode = true;
        _groupFolderEditorParentId = SelectedGroupFolder.Id;
        _groupFolderEditorParentPath = SelectedGroupFolder.Path;
        OnPropertyChanged(nameof(GroupFolderEditorParentPath));
        GroupFolderName = SelectedGroupFolder.Name;
        GroupFolderType = SelectedGroupFolder.Type;
        GroupFolderCaProfile = null;
        IsGroupFolderEditorOpen = true;
    }

    private void SaveGroupFolderEditor()
    {
        if (!IsGroupFolderEditorOpen) return;
        var selectForClient = _groupCreateForClientPicker && !IsGroupFolderRenameMode;
        var saved = IsGroupFolderRenameMode ? TryRenameGroupFolder() : TryAddGroupFolder();
        if (!saved) return;
        IsGroupFolderEditorOpen = false;
        if (selectForClient && _lastCreatedGroupId.Length > 0)
        {
            SelectClientEditorGroup(_lastCreatedGroupId);
            _groupCreateForClientPicker = false;
            _lastCreatedGroupId = string.Empty;
            OnPropertyChanged(nameof(ClientEditorGroupFolders));
        }
    }

    private void OpenGroupClientPicker()
    {
        if (SelectedGroupFolder is null) return;
        _groupClientChoiceCache.Clear();
        var assignmentByClient = _clientGroupData.Assignments
            .GroupBy(x => x.ClientId)
            .ToDictionary(x => x.Key, x => x.Last().FolderId);
        foreach (var client in _allClients.Where(x => !x.IsArchived).OrderBy(x => x.ClientName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.FinancialYear))
        {
            if (assignmentByClient.TryGetValue(client.Id, out var folderId) && folderId.Equals(SelectedGroupFolder.Id, StringComparison.OrdinalIgnoreCase))
                continue;
            var currentPath = assignmentByClient.TryGetValue(client.Id, out folderId)
                ? FindGroupNode(folderId)?.Path ?? "Another folder"
                : "Not grouped";
            _groupClientChoiceCache.Add(new ClientGroupClientChoice(client, currentPath));
        }
        GroupClientSearch = string.Empty;
        ApplyGroupClientChoiceFilter();
        IsGroupClientPickerOpen = true;
    }

    private void ApplyGroupClientChoiceFilter()
    {
        GroupClientChoices.Clear();
        var search = GroupClientSearch.Trim();
        foreach (var row in _groupClientChoiceCache)
        {
            if (search.Length == 0 || row.ClientName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.ClientCode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.FinancialYear.Contains(search, StringComparison.OrdinalIgnoreCase))
                GroupClientChoices.Add(row);
        }
    }

    private void AddSelectedClientsToGroup()
    {
        if (SelectedGroupFolder is null) return;
        var selected = _groupClientChoiceCache.Where(x => x.IsSelected).ToArray();
        if (selected.Length == 0) { GroupManagerStatus = "Select at least one Client."; return; }
        foreach (var choice in selected)
        {
            _clientGroupData.Assignments.RemoveAll(x => x.ClientId == choice.ClientId);
            _clientGroupData.Assignments.Add(new ClientGroupAssignmentRecord { ClientId = choice.ClientId, FolderId = SelectedGroupFolder.Id });
        }
        SaveGroupManager();
        RefreshGroupClientLists();
        IsGroupClientPickerOpen = false;
        GroupManagerStatus = $"Added {selected.Length} Client{(selected.Length == 1 ? string.Empty : "s")} to {SelectedGroupFolder.Path}. Existing folder assignments were moved here.";
    }

    private void OpenGroupMoveFolder()
    {
        if (SelectedGroupFolder is null || SelectedGroupFolder.IsRoot) return;
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SelectedGroupFolder.Id };
        void Collect(ClientGroupFolderNode node)
        {
            foreach (var child in node.Children) { blocked.Add(child.Id); Collect(child); }
        }
        Collect(SelectedGroupFolder);
        GroupMoveDestinations.Clear();
        void Add(IEnumerable<ClientGroupFolderNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (!blocked.Contains(node.Id)) GroupMoveDestinations.Add(node);
                Add(node.Children);
            }
        }
        Add(GroupFolderRoots);
        SelectedGroupMoveDestination = GroupMoveDestinations.FirstOrDefault(x => x.Id.Equals(_clientGroupData.Folders.FirstOrDefault(f => f.Id.Equals(SelectedGroupFolder.Id, StringComparison.OrdinalIgnoreCase))?.ParentId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            ?? GroupMoveDestinations.FirstOrDefault();
        IsGroupMoveFolderOpen = true;
    }

    private void MoveGroupFolder()
    {
        if (SelectedGroupFolder is null || SelectedGroupFolder.IsRoot || SelectedGroupMoveDestination is null) return;
        var record = _clientGroupData.Folders.FirstOrDefault(x => x.Id.Equals(SelectedGroupFolder.Id, StringComparison.OrdinalIgnoreCase));
        if (record is null) return;
        record.ParentId = SelectedGroupMoveDestination.Id;
        var movedId = record.Id;
        SaveGroupManager();
        RebuildGroupTree();
        SelectedGroupFolder = FindGroupNode(movedId);
        IsGroupMoveFolderOpen = false;
        GroupManagerStatus = $"Moved folder to {SelectedGroupFolder?.Path}.";
    }

    private void AddGroupFolder() => TryAddGroupFolder();

    private bool TryAddGroupFolder()
    {
        if (string.IsNullOrWhiteSpace(GroupFolderType) || !ClientGroupFolderTypes.CreatableTypes.Contains(GroupFolderType, StringComparer.OrdinalIgnoreCase))
        { GroupManagerStatus = "Select a Group Type first."; return false; }
        var type = ClientGroupFolderTypes.Normalize(GroupFolderType);
        if (type.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase) && GroupFolderCaProfile is null)
        { GroupManagerStatus = "Select the saved CA first. The linked CA name is the CA Group name."; return false; }

        var name = type.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase)
            ? GroupFolderCaProfile!.CaName.Trim()
            : GroupFolderName.Trim();
        if (name.Length == 0) { GroupManagerStatus = type.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase) ? "The selected CA has no saved name." : "Enter a Group Name first."; return false; }

        // Normal Client Groups creation retains the established top-level-first workflow.
        // The Windows-style Group picker is the one explicit exception: Create New Group
        // inside a submenu creates directly under that menu Group, exactly like Explorer.
        var requestedParent = _groupCreateForClientPicker ? _groupFolderEditorParentId : string.Empty;
        if (type.Equals(ClientGroupFolderTypes.Firm, StringComparison.OrdinalIgnoreCase)) requestedParent = string.Empty;
        var duplicate = _clientGroupData.Folders.Any(x =>
            x.ParentId.Equals(requestedParent, StringComparison.OrdinalIgnoreCase) &&
            x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            x.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (duplicate) { GroupManagerStatus = requestedParent.Length == 0 ? "That top-level Group already exists." : "That Group already exists at this menu level."; return false; }

        var record = new ClientGroupFolderRecord
        {
            Name = name,
            Type = type,
            ParentId = requestedParent,
            LinkedCaProfileId = type.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase) ? GroupFolderCaProfile?.ProfileId ?? string.Empty : string.Empty
        };
        _clientGroupData.Folders.Add(record);
        SaveGroupManager();
        RebuildGroupTree();
        RefreshGroupSourceItems();
        RebuildClientDirectoryGroupTree();
        SelectedGroupSourceItem = GroupSourceItems.FirstOrDefault(x => x.Folder?.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase) == true);
        _lastCreatedGroupId = record.Id;
        GroupManagerStatus = requestedParent.Length == 0
            ? $"Created {type} Group '{name}' at the top level."
            : $"Created {type} Group '{name}' inside {ClientGroupPath(_clientGroupData, requestedParent)}.";
        _groupFolderEditorParentId = string.Empty;
        _groupFolderEditorParentPath = string.Empty;
        OnPropertyChanged(nameof(GroupFolderEditorParentPath));
        return true;
    }

    private void RenameGroupFolder() => TryRenameGroupFolder();

    private bool TryRenameGroupFolder()
    {
        if (SelectedGroupFolder is null || SelectedGroupFolder.IsRoot || SelectedGroupFolder.IsClient) return false;
        if (SelectedGroupFolder.Type.Equals(ClientGroupFolderTypes.Ca, StringComparison.OrdinalIgnoreCase))
        {
            GroupManagerStatus = "CA Group names follow the linked saved CA. Change the Linked CA instead of renaming the Group.";
            return false;
        }
        var name = GroupFolderName.Trim();
        if (name.Length == 0) { GroupManagerStatus = "Enter the new folder name first."; return false; }
        var record = _clientGroupData.Folders.FirstOrDefault(x => x.Id.Equals(SelectedGroupFolder.Id, StringComparison.OrdinalIgnoreCase));
        if (record is null) return false;
        record.Name = name;
        SaveGroupManager();
        var id = record.Id;
        RebuildGroupTree();
        SelectedGroupFolder = FindGroupNode(id);
        RefreshGroupSourceItems();
        RebuildClientDirectoryGroupTree();
        GroupManagerStatus = $"Renamed Group to '{name}'.";
        return true;
    }

    private void DeleteGroupFolder()
    {
        if (SelectedGroupFolder is null || SelectedGroupFolder.IsRoot || SelectedGroupFolder.IsClient) return;
        var id = SelectedGroupFolder.Id;
        if (_clientGroupData.Folders.Any(x => x.ParentId.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            GroupManagerStatus = "This Group still contains child Groups. Move or delete the lower Groups first.";
            return;
        }

        // A lowest-level Group may be deleted directly even when it contains Clients.
        // Only the grouping assignment is removed; every Client immediately becomes
        // ungrouped and returns to the left source list / normal Client Directory.
        var releasedClients = _clientGroupData.Assignments.Count(x => x.FolderId.Equals(id, StringComparison.OrdinalIgnoreCase));
        _clientGroupData.Assignments.RemoveAll(x => x.FolderId.Equals(id, StringComparison.OrdinalIgnoreCase));
        _clientGroupData.Folders.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        SaveGroupManager();
        RebuildGroupTree();
        RefreshGroupSourceItems();
        RebuildClientDirectoryGroupTree();
        GroupManagerStatus = releasedClients == 0
            ? "Group deleted. No Client/accounting data was deleted."
            : $"Group deleted. {releasedClients:N0} Client(s) were returned to the ungrouped Client list; no Client/accounting data was deleted.";
    }

    private void AssignClientToGroup()
    {
        if (SelectedGroupFolder is null || SelectedGroupAvailableClient is null) return;
        _clientGroupData.Assignments.RemoveAll(x => x.ClientId == SelectedGroupAvailableClient.Id);
        _clientGroupData.Assignments.Add(new ClientGroupAssignmentRecord { ClientId = SelectedGroupAvailableClient.Id, FolderId = SelectedGroupFolder.Id });
        SaveGroupManager();
        GroupManagerStatus = $"Moved {SelectedGroupAvailableClient.ClientName} to {SelectedGroupFolder.Path}.";
        RefreshGroupClientLists();
        RefreshGroupSourceItems();
        RebuildClientDirectoryGroupTree();
    }

    private void UnassignClientFromGroup()
    {
        if (SelectedGroupAssignedClient is null) return;
        var name = SelectedGroupAssignedClient.ClientName;
        _clientGroupData.Assignments.RemoveAll(x => x.ClientId == SelectedGroupAssignedClient.Id);
        SaveGroupManager();
        GroupManagerStatus = $"Removed {name} from this Group. The Client itself is unchanged.";
        RefreshGroupClientLists();
        RefreshGroupSourceItems();
        RebuildClientDirectoryGroupTree();
    }

    private void SaveGroupCaLink()
    {
        if (SelectedGroupFolder is null || !IsSelectedGroupCaFolder) return;
        var record = _clientGroupData.Folders.FirstOrDefault(x => x.Id.Equals(SelectedGroupFolder.Id, StringComparison.OrdinalIgnoreCase));
        if (record is null) return;
        if (SelectedGroupCaProfile is null)
        {
            GroupManagerStatus = "Select a saved CA. A CA Group must stay linked and its Group name is the linked CA name.";
            return;
        }
        record.LinkedCaProfileId = SelectedGroupCaProfile.ProfileId;
        record.Name = SelectedGroupCaProfile.CaName.Trim();
        SaveGroupManager();
        RebuildGroupTree();
        SelectedGroupFolder = FindGroupNode(record.Id);
        RefreshGroupSourceItems();
        RebuildClientDirectoryGroupTree();
        GroupManagerStatus = $"CA Group linked to {SelectedGroupCaProfile.CaName}; the Group name now matches the saved CA name. Financial Statements Basic Info will use this CA profile for Clients under the Group.";
    }

    private void RefreshGroupClientLists()
    {
        GroupAvailableClients.Clear();
        GroupAssignedClients.Clear();
        if (SelectedGroupFolder is null) return;
        var assignedHere = _clientGroupData.Assignments.Where(x => x.FolderId.Equals(SelectedGroupFolder.Id, StringComparison.OrdinalIgnoreCase)).Select(x => x.ClientId).ToHashSet();
        foreach (var client in _allClients.Where(x => !x.IsArchived).OrderBy(x => x.ClientName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.FinancialYear))
        {
            if (assignedHere.Contains(client.Id)) GroupAssignedClients.Add(client);
            else GroupAvailableClients.Add(client);
        }
        SelectedGroupAssignedClient = null;
        SelectedGroupAvailableClient = null;
        SelectedGroupChildFolder = null;
        SelectedGroupBrowserItem = null;
        RefreshGroupBrowserContents();
    }

    private void RefreshGroupBrowserContents()
    {
        GroupFolderContents.Clear();
        if (SelectedGroupFolder is null) return;
        var search = GroupBrowserSearch.Trim();
        bool Match(string value) => search.Length == 0 || value.Contains(search, StringComparison.OrdinalIgnoreCase);

        foreach (var folder in SelectedGroupFolder.Children.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var item = ClientGroupBrowserItem.ForFolder(folder);
            if (Match(item.SearchText)) GroupFolderContents.Add(item);
        }
        foreach (var client in GroupAssignedClients.OrderBy(x => x.ClientName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.FinancialYear))
        {
            var item = ClientGroupBrowserItem.ForClient(client);
            if (Match(item.SearchText)) GroupFolderContents.Add(item);
        }
    }

    private void OpenSelectedGroupBrowserItem()
    {
        var item = SelectedGroupBrowserItem;
        if (item?.Folder is not null)
        {
            SelectedGroupFolder = FindGroupNode(item.Folder.Id);
            return;
        }
        if (item?.Client is null) return;
        SelectedClient = _allClients.FirstOrDefault(x => x.Id == item.Client.Id);
        if (SelectedClient is not null)
        {
            IsGroupManagerOpen = false;
            SetCurrent();
        }
    }

    private void RemoveSelectedGroupBrowserClient()
    {
        if (SelectedGroupBrowserItem?.Client is null) return;
        SelectedGroupAssignedClient = SelectedGroupBrowserItem.Client;
        UnassignClientFromGroup();
    }

    private void RefreshGroupCommands()
    {
        AddGroupFolderCommand?.RaiseCanExecuteChanged();
        RenameGroupFolderCommand?.RaiseCanExecuteChanged();
        DeleteGroupFolderCommand?.RaiseCanExecuteChanged();
        AssignClientToGroupCommand?.RaiseCanExecuteChanged();
        UnassignClientFromGroupCommand?.RaiseCanExecuteChanged();
        SaveGroupCaLinkCommand?.RaiseCanExecuteChanged();
        OpenSelectedGroupChildFolderCommand?.RaiseCanExecuteChanged();
        OpenSelectedGroupBrowserItemCommand?.RaiseCanExecuteChanged();
        RemoveSelectedGroupBrowserClientCommand?.RaiseCanExecuteChanged();
        BeginAddGroupFolderCommand?.RaiseCanExecuteChanged();
        BeginRenameGroupFolderCommand?.RaiseCanExecuteChanged();
        SaveGroupFolderEditorCommand?.RaiseCanExecuteChanged();
        OpenGroupClientPickerCommand?.RaiseCanExecuteChanged();
        AddSelectedClientsToGroupCommand?.RaiseCanExecuteChanged();
        OpenGroupMoveFolderCommand?.RaiseCanExecuteChanged();
        MoveGroupFolderCommand?.RaiseCanExecuteChanged();
        MoveSelectedGroupSourceCommand?.RaiseCanExecuteChanged();
    }

    private void LoadClients()
    {
        var selectedId = SelectedClient?.Id ?? EditingId;

        _allClients.Clear();
        _allClients.AddRange(
            _repository.GetAll(includeArchived: true)
                .Where(client => _governanceRepository.HasClientAccess(Environment.UserName, client.Id, client.FinancialYear)));

        foreach (var client in _allClients)
        {
            EnrichDirectoryClient(client);
        }

        RebuildPreviousFyClientOptions();
        RebuildFinancialYearFilters();
        ApplyFilter();

        if (selectedId > 0)
        {
            SelectedClient = Clients.FirstOrDefault(
                client => client.Id == selectedId);
        }

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(ArchivedCount));
        OnPropertyChanged(nameof(GstinCount));
        OnPropertyChanged(nameof(NeedsProfileCount));

        StatusText = _allClients.Count == 0
            ? "No clients have been created."
            : $"{ActiveCount:N0} active client(s), {ArchivedCount:N0} archived.";
        if (IsGroupManagerOpen) LoadGroupManager();
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();

        IEnumerable<ClientProfile> query = _allClients
            .Where(client =>
                (IncludeArchived || !client.IsArchived) &&
                (SelectedFinancialYearFilter == AllFinancialYearsFilter ||
                 client.FinancialYear.Equals(
                     SelectedFinancialYearFilter,
                     StringComparison.OrdinalIgnoreCase)) &&
                MatchesSearch(client, search));

        if (search.Length > 0)
        {
            query = query.OrderBy(client => SearchRank(client, search));
        }

        var rows = ApplySort(query).ToList();
        var selectedId = SelectedClient?.Id ?? 0;

        Clients.Clear();

        foreach (var client in rows)
        {
            Clients.Add(client);
        }

        SelectedClient = selectedId > 0
            ? Clients.FirstOrDefault(client => client.Id == selectedId)
            : null;

        if (search.Length > 0)
        {
            var exactCode = rows.FirstOrDefault(client =>
                client.ClientCode.Equals(
                    search,
                    StringComparison.OrdinalIgnoreCase));

            if (exactCode is not null)
            {
                SelectedClient = exactCode;
            }
        }

        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(GstinCount));
        OnPropertyChanged(nameof(NeedsProfileCount));
        OnPropertyChanged(nameof(SortDescriptionText));
        RebuildClientDirectoryGroupTree();
    }

    private IOrderedEnumerable<ClientProfile> ApplySort(
        IEnumerable<ClientProfile> query)
    {
        IOrderedEnumerable<ClientProfile> ordered = _sortColumn switch
        {
            "Name" => _sortAscending
                ? query.OrderBy(
                    client => ClientDisplayNameModes.Resolve(client, DisplayNameMode),
                    StringComparer.OrdinalIgnoreCase)
                : query.OrderByDescending(
                    client => ClientDisplayNameModes.Resolve(client, DisplayNameMode),
                    StringComparer.OrdinalIgnoreCase),
            "Gstin" => _sortAscending
                ? query.OrderBy(client => client.Gstin, StringComparer.OrdinalIgnoreCase)
                : query.OrderByDescending(client => client.Gstin, StringComparer.OrdinalIgnoreCase),
            "State" => _sortAscending
                ? query.OrderBy(client => client.State, StringComparer.OrdinalIgnoreCase)
                : query.OrderByDescending(client => client.State, StringComparer.OrdinalIgnoreCase),
            "FinancialYear" => _sortAscending
                ? query.OrderBy(client => client.FinancialYear, StringComparer.OrdinalIgnoreCase)
                : query.OrderByDescending(client => client.FinancialYear, StringComparer.OrdinalIgnoreCase),
            "Updated" => _sortAscending
                ? query.OrderBy(client =>
                    client.DirectoryLastActivityUtc == default
                        ? client.UpdatedUtc
                        : client.DirectoryLastActivityUtc)
                : query.OrderByDescending(client =>
                    client.DirectoryLastActivityUtc == default
                        ? client.UpdatedUtc
                        : client.DirectoryLastActivityUtc),
            _ => _sortAscending
                ? query.OrderBy(client => client.ClientCode, StringComparer.OrdinalIgnoreCase)
                : query.OrderByDescending(client => client.ClientCode, StringComparer.OrdinalIgnoreCase)
        };

        return ordered
            .ThenBy(client => client.IsArchived)
            .ThenBy(client => client.ClientCode, StringComparer.OrdinalIgnoreCase);
    }

    private static int SearchRank(
        ClientProfile client,
        string searchText)
    {
        if (searchText.Length == 0)
        {
            return 0;
        }

        if (client.ClientCode.Equals(
                searchText,
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (client.ClientCode.StartsWith(
                searchText,
                StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (Contains(client.ClientCode, searchText))
        {
            return 2;
        }

        return 3;
    }

    private static bool MatchesSearch(
        ClientProfile client,
        string searchText)
    {
        if (searchText.Length == 0)
        {
            return true;
        }

        return Contains(client.ClientCode, searchText) ||
               Contains(client.ClientName, searchText) ||
               Contains(client.LegalName, searchText) ||
               Contains(client.TradeName, searchText) ||
               Contains(client.Gstin, searchText) ||
               (client.AdditionalGstins?.Any(value => Contains(value, searchText)) ?? false) ||
               Contains(client.Pan, searchText) ||
               Contains(client.State, searchText);
    }

    private static bool Contains(
        string value,
        string searchText) =>
        value.Contains(
            searchText,
            StringComparison.OrdinalIgnoreCase);

    private void EnrichDirectoryClient(
        ClientProfile client)
    {
        var available = new List<string>();

        var purchase = _importActivityRepository.GetSnapshot(
            client.Id,
            "Purchase");
        var sales = _importActivityRepository.GetSnapshot(
            client.Id,
            "Sales");
        var bank = _importActivityRepository.GetSnapshot(
            client.Id,
            "Bank Statement");

        if (bank.IsImported)
        {
            available.Add("Banking");
        }

        if (purchase.IsImported)
        {
            available.Add("Purchase");
        }

        if (sales.IsImported)
        {
            available.Add("Sales");
        }

        if (client.LatestGstr3bPeriod.Length > 0)
        {
            available.Add("3B Profile");
        }

        client.DirectoryDataSummary = available.Count == 0
            ? "No data"
            : string.Join(" • ", available);

        var latest = _importActivityRepository
            .GetRecent(client.Id, 1)
            .FirstOrDefault();

        client.DirectoryLastActivityUtc = latest?.ImportedUtc ?? client.UpdatedUtc;
        client.DirectoryLastActivityText = latest is null
            ? $"Profile updated {client.UpdatedUtc.ToLocalTime():dd-MM-yyyy}"
            : $"{latest.ImportType} • {latest.ImportedUtc.ToLocalTime():dd-MM-yyyy HH:mm}";

        if (NetworkClientRuntime.IsNetworkClient)
        {
            client.DirectoryLanAvailable = false;
            client.DirectoryLanAccessText = string.Empty;
        }
        else
        {
            client.DirectoryLanAvailable = !client.IsArchived && _governanceRepository.IsClientLanAvailable(client.Id);
            client.DirectoryLanAccessText = client.DirectoryLanAvailable ? "Remote On" : "Remote Off";
        }
    }

    private bool CanManageClientLan(object? parameter)
    {
        if (NetworkClientRuntime.IsNetworkClient) return false;
        if (parameter is not ClientProfile client || client.Id <= 0 || client.IsArchived) return false;
        return _governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.ManageClientAccess) ||
               _governanceRepository.GetCurrentUser().RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase);
    }

    private void ToggleClientLanAvailability(object? parameter)
    {
        if (parameter is not ClientProfile client || !CanManageClientLan(client)) return;
        try
        {
            var enabled = !_governanceRepository.IsClientLanAvailable(client.Id);
            _governanceRepository.SetClientLanAvailable(client.Id, enabled);
            new EnterpriseControlRepository().BumpRevision("client");
            client.DirectoryLanAvailable = enabled;
            client.DirectoryLanAccessText = enabled ? "Remote On" : "Remote Off";
            StatusText = enabled
                ? $"Remote availability enabled for {client.ClientName}. Assign permitted AAH users from User Access."
                : $"Remote availability disabled for {client.ClientName}. Existing user assignments are retained but remote access is blocked.";
            LoadClients();
        }
        catch (Exception exception)
        {
            StatusText = $"Remote availability could not be changed: {exception.Message}";
        }
    }

    private void OpenClientLanAccess(object? parameter)
    {
        if (parameter is not ClientProfile client || !CanManageClientLan(client)) return;
        IsEditorOpen = false;
        _lanAccessClient = client;
        _governanceRepository.CleanupExpiredUserClientAccess();
        LanAccessUsers.Clear();
        var currentAdministrator = _governanceRepository.GetCurrentUser().RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase);
        foreach (var user in _governanceRepository.GetUsers()
                     .Where(x => x.IsActive)
                     .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.WindowsUserName, StringComparer.OrdinalIgnoreCase))
        {
            var isAdministrator = user.RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase);
            LanAccessUsers.Add(new ClientUserAccessRow
            {
                UserName = user.WindowsUserName,
                DisplayName = user.DisplayName,
                RoleName = user.RoleName,
                MobileVerificationIntervalDays = Math.Clamp(user.MobileVerificationIntervalDays <= 0 ? 30 : user.MobileVerificationIntervalDays, 1, 365),
                CanChangeMobileVerification = currentAdministrator,
                IsAdministrator = isAdministrator,
                CanChangeAccess = !isAdministrator,
                IsAllowed = isAdministrator || _governanceRepository.HasExplicitClientAccess(user.WindowsUserName, client.Id, client.FinancialYear)
            });
        }
        OnPropertyChanged(nameof(LanAccessClientCaption));
        LanAccessStatusText = LanAccessUsers.Count == 0
            ? "No active AAH users exist yet. Create the user from Administration → Users."
            : _governanceRepository.IsClientLanAvailable(client.Id)
                ? "Remote access is ON for non-admin users. Administrator is always allowed on every active Client/FY; tick only the non-admin AAH users who should open this client."
                : "Remote access is OFF for non-admin users. Administrator remains allowed; other users stay blocked until Remote Available is turned on.";
        IsLanAccessOpen = true;
        RefreshMobileUserCodes();
        SelectedLanAccessUser = LanAccessUsers.FirstOrDefault();
    }

    private void SaveClientLanAccess()
    {
        var client = _lanAccessClient;
        if (client is null) return;
        if (!_governanceRepository.HasPermission(Environment.UserName, GovernancePermissions.ManageClientAccess) &&
            !_governanceRepository.GetCurrentUser().RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase))
        {
            LanAccessStatusText = "Only an Administrator or user with Manage Client Access permission can change Client/FY user access.";
            return;
        }

        try
        {
            foreach (var row in LanAccessUsers.Where(x => x.CanChangeAccess))
                _governanceRepository.SetUserClientAccess(row.UserName, client.Id, client.FinancialYear, row.IsAllowed);

            if (_governanceRepository.GetCurrentUser().RoleName.Equals(GovernanceRoles.Administrator, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var row in LanAccessUsers)
                {
                    var target = _governanceRepository.GetUsers().FirstOrDefault(x => x.WindowsUserName.Equals(row.UserName, StringComparison.OrdinalIgnoreCase));
                    if (target is null) continue;
                    var days = Math.Clamp(row.MobileVerificationIntervalDays <= 0 ? 30 : row.MobileVerificationIntervalDays, 1, 365);
                    if (target.MobileVerificationIntervalDays == days) continue;
                    target.MobileVerificationIntervalDays = days;
                    target.UpdatedUtc = DateTime.UtcNow;
                    _governanceRepository.SaveUser(target);
                }
            }
            new EnterpriseControlRepository().BumpRevision("client");

            var allowed = LanAccessUsers.Count(x => x.IsAllowed);
            LanAccessStatusText = $"Saved user access for {client.ClientName}: {allowed:N0} AAH user(s) allowed on any trusted device.";
            StatusText = LanAccessStatusText;
            LoadClients();
        }
        catch (Exception exception)
        {
            LanAccessStatusText = $"Client user access could not be saved: {exception.Message}";
        }
    }

    public void StopMobileAccessRefresh()
    {
        if (_mobileQrRefreshTimer is not null)
        {
            try { _mobileQrRefreshTimer.Stop(); } catch { }
        }
        if (_secureLanServerService is not null)
        {
            _secureLanServerService.AndroidUserQrRotated -= OnAndroidUserQrRotated;
            _secureLanServerService.RunningStateChanged -= OnSecureLanServerRunningStateChanged;
        }
    }

    private void OnSecureLanServerRunningStateChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        _ = dispatcher.BeginInvoke(new Action(async () =>
        {
            await RefreshHomeMobileQrAsync().ConfigureAwait(true);
            if (IsLanAccessOpen) RefreshMobileUserCodes();
        }), DispatcherPriority.Background);
    }

    private void OnAndroidUserQrRotated(string userName)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _ = dispatcher.BeginInvoke(new Action(async () =>
        {
            await RefreshHomeMobileQrAsync().ConfigureAwait(true);
            if (IsLanAccessOpen) RefreshMobileUserCodes();
        }));
    }

    // R77: Manual refresh must rotate the actual one-time server grant. Repainting
    // the old bitmap would NOT refresh QR authentication.
    public async Task RefreshWorkspaceMobileQrAsync()
    {
        try
        {
            if (NetworkClientRuntime.IsNetworkClient)
            {
                if (string.IsNullOrWhiteSpace(NetworkClientRuntime.SessionToken))
                    throw new InvalidOperationException("Sign in to the Main Server before refreshing Mobile Access.");
                using var api = new FullUiNetworkClientApi
                {
                    Server = NetworkClientRuntime.ResolveServerEndpoint(),
                    DeviceId = NetworkClientRuntime.DeviceId,
                    DeviceToken = NetworkClientRuntime.DeviceToken,
                    SessionToken = NetworkClientRuntime.SessionToken
                };
                await api.RotateCurrentUserMobileCodeAsync().ConfigureAwait(true);
            }
            else
            {
                if (_secureLanServerService is null || !_secureLanServerService.IsRunning)
                    throw new InvalidOperationException("The AAH Main Server must be running to refresh Mobile Access.");
                var current = _governanceRepository.GetCurrentUser();
                _secureLanServerService.RotateAndroidUserQrCode(current.WindowsUserName);
            }
            await RefreshHomeMobileQrAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            HomeMobileQrText = "Mobile QR refresh failed. Existing access is unchanged: " + exception.Message;
        }
    }

    private async Task RefreshHomeMobileQrAsync()
    {
        try
        {
            if (NetworkClientRuntime.IsNetworkClient)
            {
                if (string.IsNullOrWhiteSpace(NetworkClientRuntime.SessionToken))
                    throw new InvalidOperationException("Sign in to the Main Server before loading your mobile code.");
                using var api = new FullUiNetworkClientApi
                {
                    Server = NetworkClientRuntime.ResolveServerEndpoint(),
                    DeviceId = NetworkClientRuntime.DeviceId,
                    DeviceToken = NetworkClientRuntime.DeviceToken,
                    SessionToken = NetworkClientRuntime.SessionToken
                };
                var qr = await api.GetCurrentUserMobileCodeAsync().ConfigureAwait(true);
                HomeMobileQrImage = LocalQrCodeService.CreateImage(qr.Payload, 4);
                HomeMobileCode = qr.Code;
                HomeMobileUserCaption = $"{NetworkClientRuntime.DisplayName} · {NetworkClientRuntime.RoleName}";
                HomeMobileQrText = $"User ID {qr.UserId} · refreshes by {qr.ExpiresUtc.ToLocalTime():HH:mm:ss} or immediately after use. First scan enrolls + signs in after Main Server approval; no readable password is stored in the QR.";
                return;
            }

            if (_secureLanServerService is null)
                throw new InvalidOperationException("Main Server mobile-code service is not available.");
            if (!_secureLanServerService.IsRunning)
            {
                HomeMobileQrImage = null;
                HomeMobileCode = string.Empty;
                HomeMobileQrText = _secureLanServerService.StatusText.StartsWith("AAH Main Server could not start", StringComparison.OrdinalIgnoreCase)
                    ? "Mobile Access code unavailable: " + _secureLanServerService.StatusText
                    : "Mobile Access is starting. Accounting/Audit remain available while the Main Server starts in the background.";
                return;
            }
            var current = _governanceRepository.GetCurrentUser();
            var localQr = _secureLanServerService.GetAndroidUserQrCode(current.WindowsUserName);
            HomeMobileQrImage = LocalQrCodeService.CreateImage(localQr.Payload, 4);
            HomeMobileCode = localQr.Code;
            HomeMobileUserCaption = $"{current.DisplayName} · {current.RoleName}";
            HomeMobileQrText = $"User ID {localQr.BoundUserName} · refreshes by {localQr.ExpiresUtc.ToLocalTime():HH:mm:ss} or immediately after use. First scan enrolls + signs in after Main Server approval; no readable password is stored in the QR.";
        }
        catch (Exception exception)
        {
            HomeMobileQrImage = null;
            HomeMobileCode = string.Empty;
            HomeMobileQrText = "Mobile Access code unavailable: " + exception.Message;
        }
    }

    private void RefreshMobileUserCodes()
    {
        if (_secureLanServerService is null) return;
        if (!_secureLanServerService.IsRunning)
        {
            MobileUserQrImage = null;
            MobileUserQrText = "Mobile code unavailable: " + _secureLanServerService.StatusText;
            return;
        }
        foreach (var row in LanAccessUsers)
        {
            try
            {
                var qr = _secureLanServerService.GetAndroidUserQrCode(row.UserName);
                row.MobileAccessCode = qr.Code;
                row.MobileCodeExpiryText = qr.ExpiresUtc.ToLocalTime().ToString("HH:mm:ss");
            }
            catch
            {
                row.MobileAccessCode = "Admin only";
                row.MobileCodeExpiryText = string.Empty;
            }
        }
        RefreshSelectedMobileQr();
    }

    private void RefreshSelectedMobileQr()
    {
        if (SelectedLanAccessUser is null || _secureLanServerService is null)
        {
            MobileUserQrImage = null;
            MobileUserQrText = "Select an AAH user to view the live mobile code.";
            return;
        }
        try
        {
            var selectedUser = SelectedLanAccessUser;
            if (!_secureLanServerService.IsRunning)
            {
                MobileUserQrImage = null;
                MobileUserQrText = "Mobile code unavailable: " + _secureLanServerService.StatusText;
                return;
            }
            if (!ReferenceEquals(selectedUser, SelectedLanAccessUser)) return;
            var qr = _secureLanServerService.GetAndroidUserQrCode(selectedUser.UserName);
            SelectedLanAccessUser.MobileAccessCode = qr.Code;
            SelectedLanAccessUser.MobileCodeExpiryText = qr.ExpiresUtc.ToLocalTime().ToString("HH:mm:ss");
            MobileUserQrImage = LocalQrCodeService.CreateImage(qr.Payload, 5);
            MobileUserQrText = $"{SelectedLanAccessUser.DisplayName} · User ID {qr.BoundUserName} · live code {qr.Code} · refreshes by {qr.ExpiresUtc.ToLocalTime():HH:mm:ss} or immediately after use. Same QR: first use enrolls + signs in after Main Server approval; enrolled phone signs in automatically. The QR carries a one-time authentication grant, not the readable account password.";
        }
        catch (Exception exception)
        {
            MobileUserQrImage = null;
            MobileUserQrText = "Mobile code unavailable: " + exception.Message;
        }
    }

    private void CloseClientLanAccess()
    {
        IsLanAccessOpen = false;
        _lanAccessClient = null;
        LanAccessUsers.Clear();
        SelectedLanAccessUser = null;
        MobileUserQrImage = null;
        MobileUserQrText = "Select an AAH user to view the live mobile code.";
        LanAccessStatusText = string.Empty;
        OnPropertyChanged(nameof(LanAccessClientCaption));
    }

    private void RebuildFinancialYearFilters()
    {
        // Capture the real selection before CollectionChanged causes WPF to briefly
        // clear ComboBox.SelectedItem. Restore it after the list has been rebuilt.
        var current = string.IsNullOrWhiteSpace(SelectedFinancialYearFilter)
            ? AllFinancialYearsFilter
            : SelectedFinancialYearFilter.Trim();

        FinancialYearFilters.Clear();
        FinancialYearFilters.Add(AllFinancialYearsFilter);

        foreach (var financialYear in _allClients
                     .Select(client => client.FinancialYear)
                     .Where(value => value.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase))
        {
            FinancialYearFilters.Add(financialYear);
        }

        var target = FinancialYearFilters.Any(value =>
            value.Equals(current, StringComparison.OrdinalIgnoreCase))
            ? FinancialYearFilters.First(value =>
                value.Equals(current, StringComparison.OrdinalIgnoreCase))
            : AllFinancialYearsFilter;

        if (!_selectedFinancialYearFilter.Equals(target, StringComparison.Ordinal))
        {
            _selectedFinancialYearFilter = target;
            OnPropertyChanged(nameof(SelectedFinancialYearFilter));
        }
    }

    private void SetSort(
        string? column,
        bool ascending)
    {
        var normalized = column switch
        {
            "Name" => "Name",
            "Gstin" => "Gstin",
            "State" => "State",
            "FinancialYear" => "FinancialYear",
            "Updated" => "Updated",
            _ => "ClientCode"
        };

        _sortColumn = normalized;
        _sortAscending = ascending;
        NotifySortIndicators();
        ApplyFilter();
    }

    private void ToggleSort(string? column)
    {
        var normalized = column switch
        {
            "Name" => "Name",
            "Gstin" => "Gstin",
            "State" => "State",
            "FinancialYear" => "FinancialYear",
            "Updated" => "Updated",
            _ => "ClientCode"
        };

        SetSort(
            normalized,
            !_sortColumn.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            !_sortAscending);
    }

    private string SortIndicator(string column) =>
        _sortColumn.Equals(column, StringComparison.OrdinalIgnoreCase)
            ? _sortAscending ? " ↑" : " ↓"
            : string.Empty;

    private void NotifySortIndicators()
    {
        OnPropertyChanged(nameof(ClientCodeSortIndicator));
        OnPropertyChanged(nameof(NameSortIndicator));
        OnPropertyChanged(nameof(GstinSortIndicator));
        OnPropertyChanged(nameof(StateSortIndicator));
        OnPropertyChanged(nameof(FinancialYearSortIndicator));
        OnPropertyChanged(nameof(UpdatedSortIndicator));
        OnPropertyChanged(nameof(SortDescriptionText));
    }

    private void CycleDisplayNameMode()
    {
        var modes = ClientDisplayNameModes.All;
        var currentIndex = Array.IndexOf(
            modes.ToArray(),
            DisplayNameMode);
        var nextIndex = currentIndex < 0
            ? 0
            : (currentIndex + 1) % modes.Count;

        DisplayNameMode = modes[nextIndex];
    }

    private static string SortColumnLabel(
        string column) =>
        column switch
        {
            "Name" => "name",
            "Gstin" => "GSTIN",
            "State" => "state",
            "FinancialYear" => "financial year",
            "Updated" => "latest update",
            _ => "client code"
        };

    private void EditSelected()
    {
        if (SelectedClient is null)
        {
            return;
        }

        LoadEditor(SelectedClient);
        IsEditorOpen = true;
    }

    private void CloseEditor()
    {
        CancelLookup();
        IsEditorOpen = false;
        ValidationText = string.Empty;
        LookupStatusText = string.Empty;
    }

    private void BeginNew()
    {
        CancelLookup();
        IsEditorOpen = true;
        SelectedClient = null;
        EditingId = 0;
        _importFromPreviousFy = false;
        _selectedPreviousFyClient = null;
        SelectClientEditorGroup(string.Empty);
        OnPropertyChanged(nameof(ClientEditorGroupFolders));
        OnPropertyChanged(nameof(ImportFromPreviousFy));
        OnPropertyChanged(nameof(SelectedPreviousFyClient));
        OnPropertyChanged(nameof(PreviousFyImportStatus));
        RebuildPreviousFyClientOptions();

        _isApplyingGstinData = true;
        try
        {
            ClientCode = GenerateNextClientCode();
            ClientName = string.Empty;
            LegalName = string.Empty;
            TradeName = string.Empty;
            Gstin = string.Empty;
            AdditionalGstinsText = string.Empty;
            Pan = string.Empty;
            State = string.Empty;
            FinancialYear = GetDefaultFinancialYear();
            Address = string.Empty;
            Phone = string.Empty;
            Email = string.Empty;
            Notes = string.Empty;
        }
        finally
        {
            _isApplyingGstinData = false;
        }

        _panAutoFilled = false;
        _stateAutoFilled = false;
        _legalNameAutoFilled = false;
        _tradeNameAutoFilled = false;
        _legalNameIsManual = false;
        _tradeNameIsManual = false;
        _profileSourceType = string.Empty;
        _profileSourcePath = string.Empty;
        _latestGstr3bPeriod = string.Empty;
        _latestGstr3bPeriodEndKey = string.Empty;
        _latestGstr3bArn = string.Empty;
        _latestGstr3bArnDate = string.Empty;
        _gstProfileUpdatedUtc = default;
        NotifyProfileStatusChanged();
        LookupStatusText = string.Empty;
        EditingArchived = false;
        _createdUtc = default;
        _updatedUtc = default;
        ValidationText = string.Empty;
        StatusText = "Enter the client details and save.";
    }

    private void LoadEditor(
        ClientProfile client)
    {
        CancelLookup();
        EditingId = client.Id;
        _importFromPreviousFy = false;
        _selectedPreviousFyClient = null;
        OnPropertyChanged(nameof(ImportFromPreviousFy));
        OnPropertyChanged(nameof(SelectedPreviousFyClient));
        OnPropertyChanged(nameof(PreviousFyImportStatus));

        _isApplyingGstinData = true;
        try
        {
            ClientCode = client.ClientCode;
            ClientName = client.ClientName;
            // A GSTIN persisted in a business-name field is a legacy fallback,
            // not a registered Legal/Trade Name. Leave it unresolved until
            // matching source evidence or the operator supplies a real name.
            LegalName = GstClientNameRecognitionPolicy.IsUnresolvedName(client.LegalName, client.Gstin)
                ? string.Empty : client.LegalName;
            TradeName = GstClientNameRecognitionPolicy.IsUnresolvedName(client.TradeName, client.Gstin)
                ? string.Empty : client.TradeName;
            Gstin = client.Gstin;
            AdditionalGstinsText = string.Join(Environment.NewLine, client.AdditionalGstins ?? []);
            Pan = client.Pan;
            State = client.State;
            FinancialYear = client.FinancialYear;
            Address = client.Address;
            Phone = client.Phone;
            Email = client.Email;
            Notes = client.Notes;
        }
        finally
        {
            _isApplyingGstinData = false;
        }

        var editorGrouping = ClientGroupingCatalog.Load(_preferenceRepository.Get(ClientGroupingCatalog.PreferenceKey));
        var editorAssignment = editorGrouping.Assignments.LastOrDefault(x => x.ClientId == client.Id);
        SelectClientEditorGroup(editorAssignment?.FolderId);
        OnPropertyChanged(nameof(ClientEditorGroupFolders));

        var derivedPan = DerivePan(client.Gstin);
        var derivedState = StateFromGstin(client.Gstin);
        _panAutoFilled = derivedPan.Length > 0 &&
                         client.Pan.Equals(
                             derivedPan,
                             StringComparison.OrdinalIgnoreCase);
        _stateAutoFilled = derivedState.Length > 0 &&
                           client.State.Equals(
                               derivedState,
                               StringComparison.OrdinalIgnoreCase);
        _legalNameAutoFilled = !client.LegalNameIsManual;
        _tradeNameAutoFilled = !client.TradeNameIsManual;
        _legalNameIsManual = client.LegalNameIsManual;
        _tradeNameIsManual = client.TradeNameIsManual;
        _profileSourceType = client.GstProfileSourceType;
        _profileSourcePath = client.GstProfileSourcePath;
        _latestGstr3bPeriod = client.LatestGstr3bPeriod;
        _latestGstr3bPeriodEndKey = client.LatestGstr3bPeriodEndKey;
        _latestGstr3bArn = client.LatestGstr3bArn;
        _latestGstr3bArnDate = client.LatestGstr3bArnDate;
        _gstProfileUpdatedUtc = client.GstProfileUpdatedUtc;
        NotifyProfileStatusChanged();

        EditingArchived = client.IsArchived;
        _createdUtc = client.CreatedUtc;
        _updatedUtc = client.UpdatedUtc;
        ValidationText = string.Empty;
        LookupStatusText = string.Empty;
        StatusText = $"Editing {client.ClientCode} — {client.ClientName}.";
    }

    private async Task SaveAsync()
    {
        var normalizedFinancialYear = NormalizeFinancialYear(FinancialYear);

        if (normalizedFinancialYear.Length > 0)
        {
            FinancialYear = normalizedFinancialYear;
        }

        ValidationText = ValidateEditor();

        if (ValidationText.Length > 0)
        {
            StatusText = "Correct the highlighted validation issue.";
            return;
        }

        var normalizedGstin = NormalizeTaxNumber(Gstin);

        if (normalizedGstin.Length == 15 &&
            (GstClientNameRecognitionPolicy.IsUnresolvedName(ClientName, normalizedGstin) ||
             (TradeName.Trim().Length > 0 &&
              GstClientNameRecognitionPolicy.IsUnresolvedName(TradeName, normalizedGstin)) ||
             (LegalName.Trim().Length > 0 &&
              GstClientNameRecognitionPolicy.IsUnresolvedName(LegalName, normalizedGstin))))
        {
            CancelLookup();
            try
            {
                LookupStatusText = "Reading Client Name from available GST JSON/profile sources…";
                _gstinLookupService.Invalidate(normalizedGstin);
                var profile = await _gstinLookupService.LookupAsync(
                    normalizedGstin,
                    _eReturnsRootContext.RootPath);
                var discoveredClientName = profile.TradeName.Length > 0
                    ? profile.TradeName
                    : profile.LegalName;

                _isApplyingGstinData = true;
                try
                {
                    if (GstClientNameRecognitionPolicy.HasSourceBusinessName(discoveredClientName, normalizedGstin))
                        ClientName = AccountingText.DisplayName(discoveredClientName);
                    if (GstClientNameRecognitionPolicy.HasSourceBusinessName(profile.LegalName, normalizedGstin) &&
                        (GstClientNameRecognitionPolicy.IsUnresolvedName(LegalName, normalizedGstin) ||
                         (!_legalNameIsManual && _legalNameAutoFilled)))
                    {
                        LegalName = profile.LegalName;
                        _legalNameAutoFilled = true;
                        _legalNameIsManual = false;
                    }
                    if (GstClientNameRecognitionPolicy.HasSourceBusinessName(profile.TradeName, normalizedGstin) &&
                        (GstClientNameRecognitionPolicy.IsUnresolvedName(TradeName, normalizedGstin) ||
                         (!_tradeNameIsManual && _tradeNameAutoFilled)))
                    {
                        TradeName = profile.TradeName;
                        _tradeNameAutoFilled = true;
                        _tradeNameIsManual = false;
                    }
                }
                finally
                {
                    _isApplyingGstinData = false;
                }
            }
            catch (Exception exception)
            {
                LookupStatusText = $"GST profile name lookup could not complete: {exception.Message}";
            }

            if (GstClientNameRecognitionPolicy.IsUnresolvedName(ClientName, normalizedGstin))
            {
                LookupStatusText = "No legal or trade name was found in the available GST source records. The GSTIN can remain as a temporary Client Name until verified name evidence or a manual name is provided.";
                if (ClientName.Trim().Length == 0)
                {
                    StatusText = "Client Name is required because GST source name recognition did not return a business name.";
                    return;
                }
                // Preserve the preexisting GSTIN-as-name client workflow if
                // the operator already has such a client. A real source name
                // can replace this placeholder later without altering vouchers.
            }
        }

        var normalizedPan = NormalizeTaxNumber(Pan);

        if (normalizedPan.Length == 0 && normalizedGstin.Length == 15)
        {
            normalizedPan = DerivePan(normalizedGstin);
        }

        try
        {
            // Editing the textbox is passive. Commit the registration list once on Save,
            // then discover historical sources only for genuinely new registrations.
            var priorAdditional = EditingId > 0
                ? _repository.GetById(EditingId)?.AdditionalGstins ?? Array.Empty<string>()
                : Array.Empty<string>();
            var enteredAdditional = ParseAdditionalGstins(AdditionalGstinsText);
            var newlyAdded = enteredAdditional
                .Except(priorAdditional, StringComparer.OrdinalIgnoreCase).ToArray();
            var isNewClientFinancialYear = EditingId == 0;
            var saved = _repository.Save(
                new ClientProfile
                {
                    Id = EditingId,
                    ClientCode = ClientCode,
                    ClientName = ClientName,
                    LegalName = LegalName,
                    TradeName = TradeName,
                    LegalNameIsManual = _legalNameIsManual,
                    TradeNameIsManual = _tradeNameIsManual,
                    Gstin = normalizedGstin,
                    AdditionalGstins = enteredAdditional,
                    Pan = normalizedPan,
                    State = State,
                    FinancialYear = normalizedFinancialYear,
                    Address = Address,
                    Phone = Phone,
                    Email = Email,
                    Notes = Notes,
                    GstProfileSourceType = _profileSourceType,
                    GstProfileSourcePath = _profileSourcePath,
                    LatestGstr3bPeriod = _latestGstr3bPeriod,
                    LatestGstr3bPeriodEndKey = _latestGstr3bPeriodEndKey,
                    LatestGstr3bArn = _latestGstr3bArn,
                    LatestGstr3bArnDate = _latestGstr3bArnDate,
                    GstProfileUpdatedUtc = _gstProfileUpdatedUtc,
                    IsArchived = EditingArchived,
                    CreatedUtc = _createdUtc,
                    UpdatedUtc = _updatedUtc
                });

            // Fix137 R41: every newly-created Client/FY starts with the canonical Tally
            // chart instead of an empty Master Centre. This includes Previous-FY profile
            // creation. Existing Client/FY edits never reseed or overwrite live masters.
            if (isNewClientFinancialYear)
            {
                var company = string.IsNullOrWhiteSpace(saved.LegalName)
                    ? saved.ClientName
                    : saved.LegalName;
                _masterRepository.Upsert(
                    saved.Id,
                    saved.FinancialYear,
                    MasterSynchronizationService.StandardStructure(company));
            }

            var grouping = ClientGroupingCatalog.Load(_preferenceRepository.Get(ClientGroupingCatalog.PreferenceKey));
            grouping.Assignments.RemoveAll(x => x.ClientId == saved.Id);
            if (!string.IsNullOrWhiteSpace(ClientEditorGroupId) && grouping.Folders.Any(x => x.Id.Equals(ClientEditorGroupId, StringComparison.OrdinalIgnoreCase)))
                grouping.Assignments.Add(new ClientGroupAssignmentRecord { ClientId = saved.Id, FolderId = ClientEditorGroupId });
            _preferenceRepository.Set(ClientGroupingCatalog.PreferenceKey, ClientGroupingCatalog.Save(grouping));
            _clientGroupData = grouping;
            RebuildClientDirectoryGroupTree();

            var currentClient = _clientContext.CurrentClient;

            if (currentClient?.Id == saved.Id)
            {
                _clientContext.SetCurrent(saved);
            }

            LoadClients();
            SelectedClient = Clients.FirstOrDefault(
                client => client.Id == saved.Id);
            IsEditorOpen = false;
            StatusText = $"Client {saved.ClientCode} saved successfully.";
            // Never start a source scan for an unchanged or removed GSTIN.
            // Removal already clears only the detached registration's imported rows
            // atomically in SqliteClientRepository.Save.
            if (newlyAdded.Length > 0)
                _automationService?.ScheduleNewClientGstinSourceDiscovery(
                    saved.Id, saved.FinancialYear, newlyAdded);
        }
        catch (Exception exception)
        {
            ValidationText = exception.Message;
            StatusText = "Client could not be saved.";
        }
    }

    private void SetCurrent()
    {
        var selected = SelectedClient;

        if (selected is null || selected.IsArchived)
        {
            return;
        }

        try
        {
            if (NetworkClientRuntime.IsNetworkClient && NetworkClientRuntime.AccountingAccessSuspended)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(NetworkClientRuntime.AccountingAccessSuspensionReason)
                    ? "Main Server policy currently suspends accounting access on this device."
                    : NetworkClientRuntime.AccountingAccessSuspensionReason);
            if (!_governanceRepository.HasClientAccess(Environment.UserName, selected.Id, selected.FinancialYear))
                throw new InvalidOperationException("Your application user does not have access to this Client / Financial Year.");
            _repository.SetCurrent(selected.Id);
            var current = _repository.GetCurrent();
            _clientContext.SetCurrent(current);
            ValidationText = string.Empty;
            StatusText = current is null
                ? "No active client is selected."
                : $"{current.ClientName} is now the active workspace.";

            if (current is not null)
            {
                // Defensive navigation signal: the MainWindow normally reacts to
                // ActiveClientContext.PropertyChanged, but this explicit event ensures
                // the Client Directory cannot remain visible with a client already active.
                WorkspaceOpenRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            ValidationText = exception.Message;
        }
    }

    private void ApplyPreviousFyClient(ClientProfile source)
    {
        if (EditingId != 0) return;
        _isApplyingGstinData = true;
        try
        {
            // Fix137 R41: selecting a Previous FY always obtains a fresh globally unique
            // Client Code from the current sequence. The Previous-FY code is never copied.
            // prior-year accounting/GST transaction rows are deliberately not cloned.
            ClientCode = GenerateNextClientCode();
            ClientName = source.ClientName;
            LegalName = source.LegalName;
            TradeName = source.TradeName;
            Gstin = source.Gstin;
            AdditionalGstinsText = string.Join(Environment.NewLine, source.AdditionalGstins ?? []);
            Pan = source.Pan;
            State = source.State;
            FinancialYear = NextFinancialYear(source.FinancialYear);
            Address = source.Address;
            Phone = source.Phone;
            Email = source.Email;
            Notes = source.Notes;
        }
        finally { _isApplyingGstinData = false; }
        _legalNameIsManual = source.LegalNameIsManual;
        _tradeNameIsManual = source.TradeNameIsManual;
        _legalNameAutoFilled = !source.LegalNameIsManual;
        _tradeNameAutoFilled = !source.TradeNameIsManual;
        _profileSourceType = string.Empty;
        _profileSourcePath = string.Empty;
        _latestGstr3bPeriod = string.Empty;
        _latestGstr3bPeriodEndKey = string.Empty;
        _latestGstr3bArn = string.Empty;
        _latestGstr3bArnDate = string.Empty;
        _gstProfileUpdatedUtc = default;
        var previousGrouping = ClientGroupingCatalog.Load(_preferenceRepository.Get(ClientGroupingCatalog.PreferenceKey));
        SelectClientEditorGroup(previousGrouping.Assignments.LastOrDefault(x => x.ClientId == source.Id)?.FolderId);
        OnPropertyChanged(nameof(ClientEditorGroupFolders));
        NotifyProfileStatusChanged();
        LookupStatusText = $"Previous FY profile loaded from {source.ClientCode} · FY {source.FinancialYear}. Review the preview fields, Group and then Save Client.";
        ValidationText = string.Empty;
    }

    private void RebuildPreviousFyClientOptions()
    {
        var selectedId = _selectedPreviousFyClient?.Id ?? 0;
        PreviousFyClientOptions.Clear();
        foreach (var client in _allClients
                     .OrderBy(client => client.IsArchived)
                     .ThenByDescending(client => client.FinancialYear, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(client => client.ClientName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(client => client.ClientCode, StringComparer.OrdinalIgnoreCase))
            PreviousFyClientOptions.Add(client);
        _selectedPreviousFyClient = PreviousFyClientOptions.FirstOrDefault(client => client.Id == selectedId);
        OnPropertyChanged(nameof(SelectedPreviousFyClient));
        OnPropertyChanged(nameof(PreviousFyImportStatus));
    }

    private static string NextFinancialYear(string financialYear)
    {
        var normalized = NormalizeFinancialYear(financialYear);
        if (normalized.Length != 7 || !int.TryParse(normalized[..4], out var start)) return GetDefaultFinancialYear();
        return FormatFinancialYear(start + 1);
    }

    private void DeleteClient()
    {
        if (EditingId <= 0 || !EditingArchived) return;
        var code = ClientCode.Trim();
        var answer = MessageBox.Show(
            $"Permanently delete archived Client/FY '{code}'?\n\nThis removes only this Client/FY's local AAH database rows and workspace settings/references. It does not delete anything from Tally. This cannot be undone.",
            "Delete Client Permanently",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var deletedId = EditingId;
            _repository.Delete(deletedId);
            if (_clientContext.CurrentClient?.Id == deletedId) _clientContext.SetCurrent(null);
            EditingId = 0;
            IsEditorOpen = false;
            LoadClients();
            StatusText = $"Client/FY {code} permanently deleted from local AAH data. Tally was not changed.";
        }
        catch (Exception exception)
        {
            ValidationText = exception.Message;
            StatusText = "Client could not be permanently deleted.";
        }
    }

    private void Archive()
    {
        if (EditingId <= 0 || EditingArchived)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Archive client '{ClientName}'?\n\n" +
            "The record will remain in the database and can be restored.",
            "Archive Client",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _repository.Archive(EditingId);
            _clientContext.SetCurrent(_repository.GetCurrent());
            IsEditorOpen = false;
            SelectedClient = null;
            EditingId = 0;
            LoadClients();
            ValidationText = string.Empty;
            StatusText = "Client archived.";
        }
        catch (Exception exception)
        {
            ValidationText = exception.Message;
            StatusText = "Client could not be archived.";
        }
    }

    private void Restore()
    {
        if (EditingId <= 0 || !EditingArchived)
        {
            return;
        }

        try
        {
            _repository.Restore(EditingId);
            var restoredId = EditingId;
            IsEditorOpen = false;
            EditingId = 0;
            LoadClients();
            SelectedClient = Clients.FirstOrDefault(
                client => client.Id == restoredId);
            ValidationText = string.Empty;
            StatusText = "Client restored.";
        }
        catch (Exception exception)
        {
            ValidationText = exception.Message;
            StatusText = "Client could not be restored.";
        }
    }

    private string ValidateEditor()
    {
        var normalizedCode = ClientCode.Trim().ToUpperInvariant();

        if (!ClientCodeRegex().IsMatch(normalizedCode))
        {
            return "Client Code is required and may contain letters, numbers, hyphen, or underscore.";
        }

        if (_repository.ClientCodeExists(normalizedCode, EditingId))
        {
            return $"Client Code '{normalizedCode}' already exists.";
        }

        if (ImportFromPreviousFy && SelectedPreviousFyClient is null)
        {
            return "Choose the Previous FY client to import before saving.";
        }

        if (ImportFromPreviousFy && SelectedPreviousFyClient is not null)
        {
            var expectedNextFy = NextFinancialYear(SelectedPreviousFyClient.FinancialYear);
            var requestedFy = NormalizeFinancialYear(FinancialYear);
            if (!requestedFy.Equals(expectedNextFy, StringComparison.OrdinalIgnoreCase))
                return $"Import from Previous FY must create the immediate next Financial Year {expectedNextFy}.";

            var sourceGstin = NormalizeTaxNumber(SelectedPreviousFyClient.Gstin);
            var duplicateNextFy = _allClients.Any(client => client.Id != EditingId &&
                NormalizeFinancialYear(client.FinancialYear).Equals(requestedFy, StringComparison.OrdinalIgnoreCase) &&
                ((sourceGstin.Length == 15 && NormalizeTaxNumber(client.Gstin).Equals(sourceGstin, StringComparison.OrdinalIgnoreCase)) ||
                 (sourceGstin.Length == 0 && client.ClientName.Trim().Equals(SelectedPreviousFyClient.ClientName.Trim(), StringComparison.OrdinalIgnoreCase))));
            if (duplicateNextFy)
                return "This client already has a record for the next Financial Year. Restore/edit that record instead of creating a duplicate.";
        }

        var normalizedGstin = NormalizeTaxNumber(Gstin);

        if (string.IsNullOrWhiteSpace(ClientName) && normalizedGstin.Length == 0)
        {
            return "Enter either Client Name or GSTIN.";
        }

        if (normalizedGstin.Length > 0 &&
            !GstinRegex().IsMatch(normalizedGstin))
        {
            return "GSTIN must contain exactly 15 letters or numbers and start with a two-digit State code.";
        }

        var normalizedPan = NormalizeTaxNumber(Pan);
        var additions = ParseAdditionalGstins(AdditionalGstinsText);
        if (additions.Count > 0 && normalizedGstin.Length != 15)
            return "Enter the primary GSTIN before adding secondary registrations.";
        if (additions.Count > 100)
            return "A client can have at most 100 additional GSTIN registrations.";
        var expectedPan = normalizedGstin.Length == 15 ? DerivePan(normalizedGstin) : normalizedPan;
        if (expectedPan.Length == 10 && normalizedPan.Length > 0 &&
            !normalizedPan.Equals(expectedPan, StringComparison.OrdinalIgnoreCase))
            return "Client PAN must match the PAN embedded in the primary GSTIN.";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extra in additions)
        {
            if (!GstinRegex().IsMatch(extra))
                return $"Invalid additional GSTIN: {extra}. Enter one 15-character GSTIN per line.";
            if (extra.Equals(normalizedGstin, StringComparison.OrdinalIgnoreCase) || !seen.Add(extra))
                return $"Duplicate registration: {extra}. Each GSTIN must be unique.";
            if (expectedPan.Length == 10 && !DerivePan(extra).Equals(expectedPan, StringComparison.OrdinalIgnoreCase))
                return $"Additional GSTIN {extra} belongs to a different PAN. Register it under the correct client.";
        }

        if (normalizedPan.Length > 0 &&
            !PanRegex().IsMatch(normalizedPan))
        {
            return "PAN must use the standard format, for example ABCDE1234F.";
        }

        var normalizedFinancialYear = NormalizeFinancialYear(FinancialYear);

        if (normalizedFinancialYear.Length == 0)
        {
            return "Financial Year may be entered as 2026-27, 26-27, 2627, 2026/27, or FY 26-27.";
        }

        if (!string.IsNullOrWhiteSpace(Email) &&
            !MailAddress.TryCreate(Email.Trim(), out _))
        {
            return "Enter a valid email address.";
        }

        return string.Empty;
    }

    private void ApplyDerivedGstinFields(
        string normalizedGstin)
    {
        if (normalizedGstin.Length != 15)
        {
            LookupStatusText = string.Empty;
            return;
        }

        var derivedPan = DerivePan(normalizedGstin);
        var derivedState = StateFromGstin(normalizedGstin);

        _isApplyingGstinData = true;
        try
        {
            if (Pan.Length == 0 || _panAutoFilled)
            {
                Pan = derivedPan;
                _panAutoFilled = true;
            }

            if (State.Length == 0 || _stateAutoFilled)
            {
                State = derivedState;
                _stateAutoFilled = true;
            }
        }
        finally
        {
            _isApplyingGstinData = false;
        }
    }

    private void ScheduleGstinLookup(
        string normalizedGstin)
    {
        CancelLookup();

        if (normalizedGstin.Length != 15 ||
            !GstinRegex().IsMatch(normalizedGstin))
        {
            return;
        }

        _lookupCancellation = new CancellationTokenSource();
        _ = LookupGstinProfileAsync(
            normalizedGstin,
            _lookupCancellation.Token);
    }

    private async Task LookupGstinProfileAsync(
        string normalizedGstin,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                350,
                cancellationToken);

            LookupStatusText = "Checking GSTR-3B and available GST sources for the client profile…";

            var result = await _gstinLookupService.LookupAsync(
                normalizedGstin,
                _eReturnsRootContext.RootPath,
                cancellationToken);

            if (!Gstin.Equals(
                    normalizedGstin,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _isApplyingGstinData = true;
            try
            {
                var discoveredClientName = result.TradeName.Length > 0
                    ? result.TradeName
                    : result.LegalName;
                // Existing linked clients may have their GSTIN saved as the
                // Client Name when a source contained no legal/trade name.
                // Treat that as unresolved, without replacing user names.
                if (GstClientNameRecognitionPolicy.HasSourceBusinessName(discoveredClientName, normalizedGstin) &&
                    GstClientNameRecognitionPolicy.IsUnresolvedName(ClientName, normalizedGstin))
                {
                    ClientName = AccountingText.DisplayName(discoveredClientName);
                }

                if (GstClientNameRecognitionPolicy.HasSourceBusinessName(result.LegalName, normalizedGstin) &&
                    (GstClientNameRecognitionPolicy.IsUnresolvedName(LegalName, normalizedGstin) ||
                     (!_legalNameIsManual && _legalNameAutoFilled)))
                {
                    LegalName = result.LegalName;
                    _legalNameAutoFilled = true;
                    _legalNameIsManual = false;
                }

                if (GstClientNameRecognitionPolicy.HasSourceBusinessName(result.TradeName, normalizedGstin) &&
                    (GstClientNameRecognitionPolicy.IsUnresolvedName(TradeName, normalizedGstin) ||
                     (!_tradeNameIsManual && _tradeNameAutoFilled)))
                {
                    TradeName = result.TradeName;
                    _tradeNameAutoFilled = true;
                    _tradeNameIsManual = false;
                }

                if (result.SourcePath.Length > 0)
                {
                    _profileSourceType = result.SourceType;
                    _profileSourcePath = result.SourcePath;
                    _latestGstr3bPeriod = result.Gstr3bPeriod;
                    _latestGstr3bPeriodEndKey = result.PeriodEndKey;
                    _latestGstr3bArn = result.Arn;
                    _latestGstr3bArnDate = result.ArnDate;
                    _gstProfileUpdatedUtc = DateTime.UtcNow;
                    NotifyProfileStatusChanged();
                }

                if (result.Pan.Length > 0 &&
                    (Pan.Length == 0 || _panAutoFilled))
                {
                    Pan = result.Pan;
                    _panAutoFilled = true;
                }

                if (result.State.Length > 0 &&
                    (State.Length == 0 || _stateAutoFilled))
                {
                    State = result.State;
                    _stateAutoFilled = true;
                }
            }
            finally
            {
                _isApplyingGstinData = false;
            }

            var sourceName = string.IsNullOrWhiteSpace(result.SourcePath)
                ? "saved GST data"
                : Path.GetFileName(result.SourcePath)
                    ?? "saved GST data";

            LookupStatusText = result.HasGstr3bMetadata
                ? $"GSTR-3B client profile imported from {sourceName}."
                : result.WasFound
                    ? $"GST profile found from {sourceName}."
                    : "PAN and State were derived from GSTIN; Legal and Trade Names were not found.";
        }
        catch (OperationCanceledException)
        {
            // A newer GSTIN replaced this lookup.
        }
        catch (Exception exception)
        {
            LookupStatusText =
                $"GST profile lookup could not complete: {exception.Message}";
        }
    }

    private async Task RefreshProfileAsync()
    {
        var normalizedGstin = NormalizeTaxNumber(Gstin);

        if (normalizedGstin.Length != 15 ||
            !GstinRegex().IsMatch(normalizedGstin))
        {
            LookupStatusText = "Enter a valid 15-character GSTIN before refreshing the profile.";
            return;
        }

        _gstinLookupService.Invalidate(normalizedGstin);
        CancelLookup();
        _lookupCancellation = new CancellationTokenSource();
        await LookupGstinProfileAsync(
            normalizedGstin,
            _lookupCancellation.Token);
    }

    private void OnProfileSyncChanged(object? sender, EventArgs e)
    {
        if (_profileSyncContext.ClientId <= 0)
        {
            return;
        }

        var editingId = EditingId;
        LoadClients();

        if (editingId > 0)
        {
            SelectedClient = Clients.FirstOrDefault(client => client.Id == editingId);

            if (IsEditorOpen && SelectedClient is not null)
            {
                LoadEditor(SelectedClient);
                IsEditorOpen = true;
            }
        }

        LookupStatusText = _profileSyncContext.StatusText;
    }

    private void NotifyProfileStatusChanged()
    {
        OnPropertyChanged(nameof(Gstr3bProfileText));
        OnPropertyChanged(nameof(GstProfileSourceText));
        OnPropertyChanged(nameof(HasGstr3bProfile));
    }

    private void CancelLookup()
    {
        if (_lookupCancellation is null)
        {
            return;
        }

        _lookupCancellation.Cancel();
        _lookupCancellation.Dispose();
        _lookupCancellation = null;
    }

    private string GenerateNextClientCode()
    {
        var usedCodes = _allClients
            .Select(client => client.ClientCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var number = 1; number <= 99999; number++)
        {
            var candidate = $"CL{number:000}";

            if (!usedCodes.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"CL{DateTime.Now:HHmmss}";
    }

    private void BuildFinancialYearOptions()
    {
        FinancialYearOptions.Clear();

        var currentStart = CurrentFinancialYearStart();

        for (var start = currentStart + 2;
             start >= currentStart - 6;
             start--)
        {
            FinancialYearOptions.Add(
                FormatFinancialYear(start));
        }
    }

    private static string GetDefaultFinancialYear() =>
        FormatFinancialYear(CurrentFinancialYearStart());

    private static int CurrentFinancialYearStart()
    {
        var today = DateTime.Today;
        return today.Month >= 4
            ? today.Year
            : today.Year - 1;
    }

    private static string NormalizeFinancialYear(
        string value)
    {
        var text = value
            .Trim()
            .ToUpperInvariant()
            .Replace("FY", string.Empty, StringComparison.Ordinal)
            .Replace("–", "-", StringComparison.Ordinal)
            .Replace("—", "-", StringComparison.Ordinal)
            .Replace("/", "-", StringComparison.Ordinal)
            .Replace(".", "-", StringComparison.Ordinal)
            .Replace("_", "-", StringComparison.Ordinal);

        text = Regex.Replace(text, @"\s+", string.Empty);

        var fullRange = Regex.Match(text, @"^(\d{4})-(\d{4})$");
        if (fullRange.Success)
        {
            return NormalizeYearPair(
                int.Parse(fullRange.Groups[1].Value),
                int.Parse(fullRange.Groups[2].Value));
        }

        var fullShortRange = Regex.Match(text, @"^(\d{4})-(\d{2})$");
        if (fullShortRange.Success)
        {
            var start = int.Parse(fullShortRange.Groups[1].Value);
            var end = int.Parse(fullShortRange.Groups[2].Value);
            return end == (start + 1) % 100
                ? FormatFinancialYear(start)
                : string.Empty;
        }

        var shortRange = Regex.Match(text, @"^(\d{2})-(\d{2})$");
        if (shortRange.Success)
        {
            var startShort = int.Parse(shortRange.Groups[1].Value);
            var endShort = int.Parse(shortRange.Groups[2].Value);
            return endShort == (startShort + 1) % 100
                ? FormatFinancialYear(2000 + startShort)
                : string.Empty;
        }

        if (Regex.IsMatch(text, @"^\d{6}$"))
        {
            var start = int.Parse(text[..4]);
            var end = int.Parse(text[4..]);
            return end == (start + 1) % 100
                ? FormatFinancialYear(start)
                : string.Empty;
        }

        if (Regex.IsMatch(text, @"^\d{4}$"))
        {
            var number = int.Parse(text);

            if (text.StartsWith(
                    "20",
                    StringComparison.Ordinal))
            {
                return FormatFinancialYear(number);
            }

            var startShort = number / 100;
            var endShort = number % 100;
            return endShort == (startShort + 1) % 100
                ? FormatFinancialYear(2000 + startShort)
                : string.Empty;
        }

        return string.Empty;
    }

    private static string NormalizeYearPair(
        int start,
        int end) =>
        end == start + 1
            ? FormatFinancialYear(start)
            : string.Empty;

    private static string FormatFinancialYear(
        int startYear) =>
        $"{startYear:0000}-{(startYear + 1) % 100:00}";

    private static string DerivePan(
        string gstin)
    {
        var normalized = NormalizeTaxNumber(gstin);
        return normalized.Length == 15
            ? normalized.Substring(2, 10)
            : string.Empty;
    }

    private static string StateFromGstin(
        string gstin)
    {
        var normalized = NormalizeTaxNumber(gstin);

        if (normalized.Length < 2)
        {
            return string.Empty;
        }

        return normalized[..2] switch
        {
            "01" => "Jammu and Kashmir",
            "02" => "Himachal Pradesh",
            "03" => "Punjab",
            "04" => "Chandigarh",
            "05" => "Uttarakhand",
            "06" => "Haryana",
            "07" => "Delhi",
            "08" => "Rajasthan",
            "09" => "Uttar Pradesh",
            "10" => "Bihar",
            "11" => "Sikkim",
            "12" => "Arunachal Pradesh",
            "13" => "Nagaland",
            "14" => "Manipur",
            "15" => "Mizoram",
            "16" => "Tripura",
            "17" => "Meghalaya",
            "18" => "Assam",
            "19" => "West Bengal",
            "20" => "Jharkhand",
            "21" => "Odisha",
            "22" => "Chhattisgarh",
            "23" => "Madhya Pradesh",
            "24" => "Gujarat",
            "25" => "Daman and Diu",
            "26" => "Dadra and Nagar Haveli and Daman and Diu",
            "27" => "Maharashtra",
            "28" => "Andhra Pradesh (Old)",
            "29" => "Karnataka",
            "30" => "Goa",
            "31" => "Lakshadweep",
            "32" => "Kerala",
            "33" => "Tamil Nadu",
            "34" => "Puducherry",
            "35" => "Andaman and Nicobar Islands",
            "36" => "Telangana",
            "37" => "Andhra Pradesh",
            "38" => "Ladakh",
            "97" => "Other Territory",
            "99" => "Centre Jurisdiction",
            _ => string.Empty
        };
    }

    private static IReadOnlyList<string> ParseAdditionalGstins(string text) =>
        (text ?? string.Empty)
            .Split(['\r', '\n', ',', ';', ' ', '\t'],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeTaxNumber)
            .Where(value => value.Length > 0)
            .ToArray();

    private static string NormalizeTaxNumber(
        string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit))
            .ToUpperInvariant();

    private void RefreshCommands()
    {
        SetCurrentCommand.RaiseCanExecuteChanged();
        EditSelectedCommand.RaiseCanExecuteChanged();
        ArchiveCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        DeleteClientCommand.RaiseCanExecuteChanged();
        ToggleClientLanAvailabilityCommand?.RaiseCanExecuteChanged();
        OpenClientLanAccessCommand?.RaiseCanExecuteChanged();
        SaveClientLanAccessCommand?.RaiseCanExecuteChanged();
    }

    [GeneratedRegex(
        @"^[A-Z0-9][A-Z0-9_-]{1,19}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ClientCodeRegex();

    [GeneratedRegex(
        @"^[0-9]{2}[A-Z0-9]{13}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex GstinRegex();

    [GeneratedRegex(
        @"^[A-Z]{5}[0-9]{4}[A-Z]$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PanRegex();
}

public sealed class ClientGroupSourceItem
{
    private ClientGroupSourceItem() { }
    public ClientProfile? Client { get; private init; }
    public ClientGroupFolderNode? Folder { get; private init; }
    public bool IsClient => Client is not null;
    public string PrimaryText { get; private init; } = string.Empty;
    public string SecondaryText { get; private init; } = string.Empty;
    public string KindText { get; private init; } = string.Empty;

    public static ClientGroupSourceItem ForClient(ClientProfile client, string currentPath) => new()
    {
        Client = client,
        PrimaryText = client.ClientName,
        SecondaryText = $"{client.ClientCode} · {client.FinancialYear} · {currentPath}",
        KindText = "Client"
    };

    public static ClientGroupSourceItem ForFolder(ClientGroupFolderNode folder, string parentPath) => new()
    {
        Folder = folder,
        PrimaryText = folder.DisplayText,
        SecondaryText = $"Current parent: {parentPath}",
        KindText = "Group"
    };

    public override string ToString() => PrimaryText;
}

public sealed class ClientDirectoryDisplayItem
{
    private ClientDirectoryDisplayItem() { }

    public ClientProfile? Client { get; private init; }
    public ClientGroupFolderRecord? Folder { get; private init; }
    public bool IsNavigation { get; private init; }
    public string NavigationFolderId { get; private init; } = string.Empty;
    public string GroupSummary { get; private init; } = string.Empty;
    public string DirectoryToolTip { get; private init; } = string.Empty;
    public bool IsGroup => Folder is not null || IsNavigation;
    public Visibility ClientOnlyVisibility => Client is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GroupOnlyVisibility => Client is null && !IsNavigation ? Visibility.Visible : Visibility.Collapsed;

    private string GroupGstin { get; init; } = string.Empty;
    private string GroupState { get; init; } = string.Empty;
    private string GroupFinancialYear { get; init; } = string.Empty;
    private string GroupRemoteSummary { get; init; } = string.Empty;
    private string GroupDataSummary { get; init; } = string.Empty;
    private string GroupLatestGstr3bPeriod { get; init; } = string.Empty;
    private string GroupGstProfileSummary { get; init; } = string.Empty;
    private string GroupUpdatedSummary { get; init; } = string.Empty;

    public string ClientCode => Client?.ClientCode ?? (IsNavigation ? "Back" : Folder?.Type ?? string.Empty);
    public string ClientName => Client is not null
        ? Client.ClientName
        : IsNavigation
            ? "← Back"
            : Folder?.Name ?? string.Empty;
    public string TradeName => Client?.TradeName ?? (IsGroup ? ClientName : string.Empty);
    public string LegalName => Client?.LegalName ?? (IsGroup ? ClientName : string.Empty);
    public string DirectorySecondaryText => Client is not null
        ? Client.LegalName
        : IsNavigation
            ? "Single-click: one level back • Double-click: all Groups"
            : GroupSummary;
    public string Gstin => Client?.Gstin ?? GroupGstin;
    public string State => Client?.State ?? GroupState;
    public string FinancialYear => Client?.FinancialYear ?? GroupFinancialYear;
    public bool DirectoryLanAvailable => Client?.DirectoryLanAvailable ?? false;
    public string DirectoryRemoteSummary => Client?.DirectoryLanAccessText ?? GroupRemoteSummary;
    public string DirectoryDataSummary => Client?.DirectoryDataSummary ?? GroupDataSummary;
    public string LatestGstr3bPeriod => Client?.LatestGstr3bPeriod ?? GroupLatestGstr3bPeriod;
    public string GstProfileSourceType => Client?.GstProfileSourceType ?? GroupGstProfileSummary;
    public string DirectoryLastActivityText => Client?.DirectoryLastActivityText ?? GroupUpdatedSummary;
    public bool IsArchived => Client?.IsArchived ?? false;

    public static ClientDirectoryDisplayItem ForClient(ClientProfile client) => new()
    {
        Client = client,
        DirectoryToolTip = $"{client.ClientName}\n{client.ClientCode} · {client.FinancialYear}"
    };

    public static ClientDirectoryDisplayItem ForGroup(
        ClientGroupFolderRecord folder,
        string summary,
        string tooltip,
        string gstin,
        string state,
        string financialYear,
        string remoteSummary,
        string dataSummary,
        string latestGstr3bPeriod,
        string gstProfileSummary,
        string updatedSummary) => new()
    {
        Folder = folder,
        GroupSummary = summary,
        DirectoryToolTip = tooltip,
        GroupGstin = gstin,
        GroupState = state,
        GroupFinancialYear = financialYear,
        GroupRemoteSummary = remoteSummary,
        GroupDataSummary = dataSummary,
        GroupLatestGstr3bPeriod = latestGstr3bPeriod,
        GroupGstProfileSummary = gstProfileSummary,
        GroupUpdatedSummary = updatedSummary
    };

    public static ClientDirectoryDisplayItem ForBack(string parentFolderId, string currentName) => new()
    {
        IsNavigation = true,
        NavigationFolderId = parentFolderId ?? string.Empty,
        GroupSummary = $"Back from {currentName}",
        DirectoryToolTip = $"Single-click to return one Group level from {currentName}. Double-click to return to the top Group Wise directory."
    };
}

public sealed class ClientDirectoryGroupNode
{
    private ClientDirectoryGroupNode() { }
    public ClientProfile? Client { get; private init; }
    public string DisplayText { get; private init; } = string.Empty;
    public string SecondaryText { get; private init; } = string.Empty;
    public bool IsClient => Client is not null;
    public ObservableCollection<ClientDirectoryGroupNode> Children { get; } = [];

    public static ClientDirectoryGroupNode ForFolder(ClientGroupFolderRecord folder) => new()
    {
        DisplayText = $"{folder.Type} - {folder.Name}",
        SecondaryText = "Group"
    };

    public static ClientDirectoryGroupNode ForClient(ClientProfile client) => new()
    {
        Client = client,
        DisplayText = client.ClientName,
        SecondaryText = $"{client.ClientCode} · {client.FinancialYear}"
    };

    public override string ToString() => DisplayText;
}

public sealed class ClientGroupFolderNode : ViewModelBase
{
    public ClientGroupFolderNode(ClientGroupFolderRecord record, string path)
    {
        Id = record.Id;
        Name = record.Name;
        Type = record.Type;
        LinkedCaProfileId = record.LinkedCaProfileId;
        Path = path;
    }

    private ClientGroupFolderNode(ClientProfile client, string path)
    {
        Client = client;
        Id = $"client:{client.Id}";
        Name = client.ClientName;
        Type = "Client";
        Path = path;
        LinkedCaProfileId = string.Empty;
    }

    public ClientProfile? Client { get; }
    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public string LinkedCaProfileId { get; }
    public string Path { get; }
    private bool _isExpanded;
    public bool IsClient => Client is not null;
    public bool IsRoot => false;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
    public string DisplayText => IsClient ? Name : $"{Type} - {Name}";
    public string SecondaryText => IsClient ? $"{Client!.ClientCode} · {Client.FinancialYear}" : string.Empty;
    public ObservableCollection<ClientGroupFolderNode> Children { get; } = [];

    public static ClientGroupFolderNode ForClient(ClientProfile client, string path) => new(client, path);

    public override string ToString() => DisplayText;
}

public sealed record ClientGroupCreateParentChoice(string Id, string Path)
{
    public override string ToString() => Path;
}

public sealed class ClientGroupBrowserItem
{
    private ClientGroupBrowserItem() { }
    public bool IsFolder => Folder is not null;
    public ClientGroupFolderNode? Folder { get; private init; }
    public ClientProfile? Client { get; private init; }
    public string Name { get; private init; } = string.Empty;
    public string Kind { get; private init; } = string.Empty;
    public string ClientCode { get; private init; } = string.Empty;
    public string FinancialYear { get; private init; } = string.Empty;
    public string SearchText => $"{Name} {Kind} {ClientCode} {FinancialYear}";
    public string PrimaryText => IsFolder ? $"{Kind} - {Name}" : Name;
    public string SecondaryText => IsFolder ? "Folder" : $"{ClientCode} · {FinancialYear}";

    public static ClientGroupBrowserItem ForFolder(ClientGroupFolderNode folder) => new()
    {
        Folder = folder, Name = folder.Name, Kind = folder.Type
    };

    public static ClientGroupBrowserItem ForClient(ClientProfile client) => new()
    {
        Client = client, Name = client.ClientName, Kind = "Client", ClientCode = client.ClientCode, FinancialYear = client.FinancialYear
    };
}

public sealed class ClientGroupClientChoice : ViewModelBase
{
    private bool _isSelected;
    public ClientGroupClientChoice(ClientProfile client, string currentPath)
    {
        ClientId = client.Id; ClientName = client.ClientName; ClientCode = client.ClientCode; FinancialYear = client.FinancialYear; CurrentPath = currentPath;
    }
    public long ClientId { get; }
    public string ClientName { get; }
    public string ClientCode { get; }
    public string FinancialYear { get; }
    public string CurrentPath { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed class ClientUserAccessRow : ViewModelBase
{
    private bool _isAllowed;
    private string _mobileAccessCode = string.Empty;
    private string _mobileCodeExpiryText = string.Empty;
    private int _mobileVerificationIntervalDays = 30;
    public string UserName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public bool IsAdministrator { get; init; }
    public bool CanChangeAccess { get; init; } = true;
    public bool CanChangeMobileVerification { get; init; }
    public int MobileVerificationIntervalDays { get => _mobileVerificationIntervalDays; set => SetProperty(ref _mobileVerificationIntervalDays, Math.Clamp(value <= 0 ? 30 : value, 1, 365)); }
    public bool IsAllowed { get => _isAllowed; set => SetProperty(ref _isAllowed, value); }
    public string MobileAccessCode { get => _mobileAccessCode; set => SetProperty(ref _mobileAccessCode, value); }
    public string MobileCodeExpiryText { get => _mobileCodeExpiryText; set => SetProperty(ref _mobileCodeExpiryText, value); }
}
