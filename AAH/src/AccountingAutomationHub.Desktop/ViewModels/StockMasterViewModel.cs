using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Collections;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Masters;
using System.Diagnostics;
using AccountingAutomationHub.Infrastructure.Governance;
using Microsoft.Win32;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class StockMasterViewModel : PageViewModel
{
    private static readonly string[] SimpleTypes =
    [
        "Stock Group", "Stock Category", "Godown", "Cost Category", "Cost Centre", "Tax Classification", "TaxUnit"
    ];

    private readonly IStockMasterRepository _repository;
    private readonly TallyMasterImportService _importService;
    private readonly ActiveClientContext _clientContext;
    private readonly AccountingLiveChangeHub _liveChangeHub;
    private readonly TallyLiveConnectionCoordinator _tallyCoordinator;
    private readonly BackgroundAutomationService? _automationService;
    private readonly SqliteGovernanceRepository _governanceRepository;
    private readonly GstHsnSacDirectoryService _gstDirectoryService = new();
    private MasterTreeNode? _selectedSection;
    private StockItemRecord? _selectedStockItem;
    private StockUnitRecord? _selectedUnit;
    private StockSimpleMasterRecord? _selectedSimpleMaster;
    private StockItemAliasRecord? _selectedAlias;
    private StockUnitMappingRecord? _selectedUnitMapping;
    private QuantityConversionRule? _selectedConversion;
    private StockItemUnitProfileRecord? _selectedUnitProfile;
    private GstHsnSacDirectoryRecord? _selectedGstDirectoryRow;
    private string _gstDirectorySearchText = string.Empty;
    private string _searchText = string.Empty;
    private bool _includeArchived;
    private string _statusText = "Stock Master ready.";
    private long _preparedClientId;
    private string _preparedFinancialYear = string.Empty;
    private string _fix123ImportedUnitAuthorityScope = string.Empty;
    private IReadOnlyList<StockItemRecord> _cachedStockItems = [];
    private IReadOnlyList<StockUnitRecord> _cachedUnits = [];
    private IReadOnlyList<StockSimpleMasterRecord> _cachedSimpleMasters = [];
    private IReadOnlyList<StockItemAliasRecord> _cachedAliases = [];
    private IReadOnlyList<StockUnitMappingRecord> _cachedUnitMappings = [];
    private IReadOnlyList<QuantityConversionRule> _cachedConversions = [];
    private IReadOnlyList<StockItemUnitProfileRecord> _cachedUnitProfiles = [];

    public StockMasterViewModel(
        IStockMasterRepository repository,
        TallyMasterImportService importService,
        ActiveClientContext clientContext,
        AccountingLiveChangeHub liveChangeHub,
        TallyLiveConnectionCoordinator tallyCoordinator,
        BackgroundAutomationService? automationService = null)
        : base("Stock Master", "Tally-style Stock Items, Groups, Units, Godowns and item/unit/conversion mappings")
    {
        _repository = repository;
        _importService = importService;
        _clientContext = clientContext;
        _liveChangeHub = liveChangeHub;
        _tallyCoordinator = tallyCoordinator;
        _automationService = automationService;
        _governanceRepository = new SqliteGovernanceRepository();
        _governanceRepository.Initialize();
        _clientContext.PropertyChanged += OnClientChanged;
        _liveChangeHub.Changed += OnLiveChange;
        RefreshCommand = new RelayCommand(Refresh);
        ImportCommand = new RelayCommand(Import, () => CurrentClientId > 0 && CanImportStockMaster);
        ImportFromTallyCommand = new AsyncRelayCommand(ImportFromTallyAsync, () => CurrentClientId > 0 && CanImportStockMaster);
        AddCommand = new RelayCommand(Add, () => CurrentClientId > 0 && CanEditStockMaster);
        SaveCommand = new RelayCommand(Save, () => CurrentClientId > 0 && CanEditStockMaster);
        ArchiveCommand = new RelayCommand(Archive, () => CurrentClientId > 0 && CanEditStockMaster);
        SaveAliasCommand = new RelayCommand(SaveAlias, () => SelectedAlias is not null && CanEditStockMaster);
        SaveUnitMappingCommand = new RelayCommand(SaveUnitMapping, () => SelectedUnitMapping is not null && CanEditStockMaster);
        SaveConversionCommand = new RelayCommand(SaveConversion, () => SelectedConversion is not null && CanEditStockMaster);
        AddAliasCommand = new RelayCommand(AddAlias, () => CurrentClientId > 0 && CanEditStockMaster);
        AddUnitMappingCommand = new RelayCommand(AddUnitMapping, () => CurrentClientId > 0 && CanEditStockMaster);
        AddConversionCommand = new RelayCommand(AddConversion, () => CurrentClientId > 0 && CanEditStockMaster);
        AddUnitProfileCommand = new RelayCommand(AddUnitProfile, () => CurrentClientId > 0 && CanEditStockMaster && SelectedStockItem is { PersistentId: > 0 });
        SaveUnitProfileCommand = new RelayCommand(SaveUnitProfile, () => SelectedUnitProfile is not null && CanEditStockMaster);
        DeleteUnitProfileCommand = new RelayCommand(DeleteUnitProfile, () => SelectedUnitProfile is { PersistentId: > 0 } && CanEditStockMaster);
        ImportGstDirectoryCommand = new RelayCommand(ImportGstDirectory, () => CanImportStockMaster);
        RefreshOfficialGstDirectoryCommand = new AsyncRelayCommand(RefreshOfficialGstDirectoryAsync, () => CanImportStockMaster);
        ApplyGstCodeCommand = new RelayCommand(ApplySelectedGstCode, () => SelectedStockItem is not null && SelectedGstDirectoryRow is not null && CanEditStockMaster);
        OpenGstSearchCommand = new RelayCommand(OpenGstSearch);
        BuildSections();
        StatusText = "Stock Master is preparing in the background.";
        RefreshGstDirectory();
    }

    public ObservableCollection<MasterTreeNode> Sections { get; } = [];
    public BulkObservableCollection<StockItemRecord> StockItems { get; } = [];
    public BulkObservableCollection<StockUnitRecord> Units { get; } = [];
    public BulkObservableCollection<StockSimpleMasterRecord> SimpleMasters { get; } = [];
    public BulkObservableCollection<StockItemAliasRecord> ItemAliases { get; } = [];
    public BulkObservableCollection<StockUnitMappingRecord> UnitMappings { get; } = [];
    public BulkObservableCollection<QuantityConversionRule> QuantityConversions { get; } = [];
    public BulkObservableCollection<StockItemUnitProfileRecord> StockItemUnitProfiles { get; } = [];
    public BulkObservableCollection<GstHsnSacDirectoryRecord> GstHsnSacDirectory { get; } = [];
    public IReadOnlyList<int> DecimalPlaceOptions { get; } = [0,1,2,3,4];
    public IReadOnlyList<string> StandardUnitOptions => AahUnitCatalog.CanonicalUnitCodes;
    // Fix117: Stock Item masters may use the AAH standard UQC catalogue or an explicit
    // custom Unit Master synchronized/created in Tally. Transaction mapping stays on the
    // controlled standard catalogue; Stock Item unit policy can additionally reference
    // these saved custom masters.
    public IReadOnlyList<string> StockMasterUnitOptions => AahUnitCatalog.CanonicalUnitCodes
        .Concat(_cachedUnits.Where(unit => !unit.IsArchived).Select(unit => unit.Name.Trim()).Where(name => name.Length > 0))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ImportCommand { get; }
    public AsyncRelayCommand ImportFromTallyCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ArchiveCommand { get; }
    public RelayCommand SaveAliasCommand { get; }
    public RelayCommand SaveUnitMappingCommand { get; }
    public RelayCommand SaveConversionCommand { get; }
    public RelayCommand AddAliasCommand { get; }
    public RelayCommand AddUnitMappingCommand { get; }
    public RelayCommand AddConversionCommand { get; }
    public RelayCommand AddUnitProfileCommand { get; }
    public RelayCommand SaveUnitProfileCommand { get; }
    public RelayCommand DeleteUnitProfileCommand { get; }
    public RelayCommand ImportGstDirectoryCommand { get; }
    public AsyncRelayCommand RefreshOfficialGstDirectoryCommand { get; }
    public RelayCommand ApplyGstCodeCommand { get; }
    public RelayCommand OpenGstSearchCommand { get; }

    public MasterTreeNode? SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (SetProperty(ref _selectedSection, value))
            {
                OnPropertyChanged(nameof(IsStockItemsSection));
                OnPropertyChanged(nameof(IsUnitsSection));
                OnPropertyChanged(nameof(IsSimpleSection));
                RefreshMainRecords();
            }
        }
    }

    public StockItemRecord? SelectedStockItem
    {
        get => _selectedStockItem;
        set
        {
            if(SetProperty(ref _selectedStockItem,value))
            {
                AddUnitProfileCommand.RaiseCanExecuteChanged();
                ApplyGstCodeCommand.RaiseCanExecuteChanged();
                if(value is { PersistentId: > 0 }) SelectedUnitProfile=StockItemUnitProfiles.FirstOrDefault(row=>row.StockItemId==value.PersistentId);
            }
        }
    }
    public StockUnitRecord? SelectedUnit { get => _selectedUnit; set => SetProperty(ref _selectedUnit, value); }
    public StockSimpleMasterRecord? SelectedSimpleMaster { get => _selectedSimpleMaster; set => SetProperty(ref _selectedSimpleMaster, value); }
    public StockItemAliasRecord? SelectedAlias { get => _selectedAlias; set { if(SetProperty(ref _selectedAlias,value)) SaveAliasCommand.RaiseCanExecuteChanged(); } }
    public StockUnitMappingRecord? SelectedUnitMapping { get => _selectedUnitMapping; set { if(SetProperty(ref _selectedUnitMapping,value)) SaveUnitMappingCommand.RaiseCanExecuteChanged(); } }
    public QuantityConversionRule? SelectedConversion { get => _selectedConversion; set { if(SetProperty(ref _selectedConversion,value)) SaveConversionCommand.RaiseCanExecuteChanged(); } }
    public StockItemUnitProfileRecord? SelectedUnitProfile { get => _selectedUnitProfile; set { if(SetProperty(ref _selectedUnitProfile,value)){SaveUnitProfileCommand.RaiseCanExecuteChanged();DeleteUnitProfileCommand.RaiseCanExecuteChanged();} } }

    public GstHsnSacDirectoryRecord? SelectedGstDirectoryRow
    {
        get => _selectedGstDirectoryRow;
        set
        {
            if (SetProperty(ref _selectedGstDirectoryRow, value))
                ApplyGstCodeCommand.RaiseCanExecuteChanged();
        }
    }

    public string GstDirectorySearchText
    {
        get => _gstDirectorySearchText;
        set
        {
            if (SetProperty(ref _gstDirectorySearchText, value))
                RefreshGstDirectory();
        }
    }

    public string GstDirectorySummary => GstHsnSacDirectory.Count == 0
        ? "GST HSN/SAC directory not loaded. Download from GSTN or import HSN_SAC.xlsx."
        : $"{GstHsnSacDirectory.Count:N0} GSTN HSN/SAC row(s) shown.";

    public string SearchText { get => _searchText; set { if(SetProperty(ref _searchText,value)) RefreshMainRecords(); } }
    public bool IncludeArchived { get => _includeArchived; set { if(SetProperty(ref _includeArchived,value)) RefreshMainRecords(); } }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText,value); }

    public bool IsStockItemsSection => SelectedSection?.FilterValue.StartsWith("Stock Item",StringComparison.OrdinalIgnoreCase) == true;
    public bool IsUnitsSection => SelectedSection?.FilterValue.Equals("Unit",StringComparison.OrdinalIgnoreCase) == true;
    public bool IsSimpleSection => !IsStockItemsSection && !IsUnitsSection;
    // Fix118: summary cards are whole-module counts, not counts of whichever tree section
    // happens to be visible. Selecting Stock Items must not make the Units card show zero.
    public int StockItemCount => _cachedStockItems.Count(row => IncludeArchived || !row.IsArchived);
    public int UnitCount => _cachedUnits.Count(row => IncludeArchived || !row.IsArchived);
    public int MappingCount => _cachedAliases.Count + _cachedUnitMappings.Count + _cachedConversions.Count + _cachedUnitProfiles.Count;
    public bool CanViewStockMaster => DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.StockView);
    public bool CanImportStockMaster => CanViewStockMaster && DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.Import) &&
        (!NetworkClientRuntime.IsNetworkClient || !NetworkClientRuntime.AccountingAccessSuspended);
    public bool CanEditStockMaster => DesktopPermissionService.CanMutateAccounting(_governanceRepository, GovernancePermissions.StockEdit);
    public bool IsStockMasterReadOnly => !CanEditStockMaster;
    public string StockMasterPermissionSummary => $"{DesktopPermissionService.EffectiveRoleName(_governanceRepository)} · {(NetworkClientRuntime.IsNetworkClient ? "Windows processing node · " : string.Empty)}Stock Master view { (CanViewStockMaster ? "allowed" : "denied") } · import { (CanImportStockMaster ? "allowed" : "denied") } · edit { (CanEditStockMaster ? "allowed" : "denied") }";
    private long CurrentClientId => _clientContext.CurrentClient?.Id ?? 0;

    private bool RequireStockEdit(string action)
    {
        if (CanEditStockMaster) return true;
        StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, GovernancePermissions.StockEdit, action);
        return false;
    }

    private void BuildSections()
    {
        var preferredFilter = SelectedSection?.FilterValue ?? "Stock Item";
        Sections.Clear();
        var root = new MasterTreeNode { Title="Inventory Masters",NodeKind="Root",FilterValue="Stock Item",IsExpanded=true };
        var stockItemsNode = Node("Stock Items","Stock Item");
        stockItemsNode.IsExpanded = true;
        root.Children.Add(stockItemsNode);

        var client = _clientContext.CurrentClient;
        if (client is not null)
        {
            var usePrepared = _preparedClientId == client.Id &&
                _preparedFinancialYear.Equals(client.FinancialYear,StringComparison.OrdinalIgnoreCase);
            var items = usePrepared
                ? _cachedStockItems.Where(row => !row.IsArchived).ToArray()
                : Array.Empty<StockItemRecord>();
            stockItemsNode.RecordCount = items.Length;
            var groups = usePrepared
                ? _cachedSimpleMasters.Where(row => !row.IsArchived && row.Type.Equals("Stock Group",StringComparison.OrdinalIgnoreCase)).ToArray()
                : Array.Empty<StockSimpleMasterRecord>();
            var groupNames = groups.Select(group => group.Name.Trim())
                .Concat(items.Select(item => item.StockGroup.Trim()))
                .Where(name => name.Length > 0 && !name.Equals("Primary",StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name,StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var parentByName = groups
                .Where(group => group.Name.Trim().Length > 0)
                .GroupBy(group => group.Name.Trim(),StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,group => group.First().Parent.Trim(),StringComparer.OrdinalIgnoreCase);
            var nodes = groupNames.ToDictionary(
                name => name,
                name => new MasterTreeNode
                {
                    Title=name,
                    NodeKind="StockItemGroup",
                    FilterValue=$"Stock Item|{name}",
                    RecordCount=items.Count(item => item.StockGroup.Equals(name,StringComparison.OrdinalIgnoreCase))
                },
                StringComparer.OrdinalIgnoreCase);
            foreach (var name in groupNames)
            {
                var parent = parentByName.GetValueOrDefault(name) ?? string.Empty;
                if (parent.Length > 0 && !parent.Equals("Primary",StringComparison.OrdinalIgnoreCase) && nodes.TryGetValue(parent,out var parentNode))
                {
                    parentNode.Children.Add(nodes[name]);
                    parentNode.IsExpanded = true;
                }
                else
                {
                    stockItemsNode.Children.Add(nodes[name]);
                }
            }
        }

        foreach (var definition in new[]
        {
            ("Stock Groups","Stock Group"), ("Stock Categories","Stock Category"), ("Units","Unit"),
            ("Godowns / Locations","Godown"), ("Cost Categories","Cost Category"), ("Cost Centres","Cost Centre"),
            ("Tax Classifications","Tax Classification"), ("Tax Units","TaxUnit")
        })
        {
            var node=Node(definition.Item1,definition.Item2);
            if(client is not null)
            {
                var usePrepared = _preparedClientId == client.Id &&
                    _preparedFinancialYear.Equals(client.FinancialYear,StringComparison.OrdinalIgnoreCase);
                node.RecordCount=definition.Item2.Equals("Unit",StringComparison.OrdinalIgnoreCase)
                    ? (usePrepared ? _cachedUnits.Count(row => !row.IsArchived) : 0)
                    : (usePrepared
                        ? _cachedSimpleMasters.Count(row => !row.IsArchived && row.Type.Equals(definition.Item2,StringComparison.OrdinalIgnoreCase))
                        : 0);
            }
            root.Children.Add(node);
        }
        Sections.Add(root);
        SelectedSection = FindSection(root,preferredFilter) ?? stockItemsNode;
    }

    private static MasterTreeNode? FindSection(MasterTreeNode node,string filter)
    {
        if(node.FilterValue.Equals(filter,StringComparison.OrdinalIgnoreCase)) return node;
        foreach(var child in node.Children)
        {
            var match=FindSection(child,filter);
            if(match is not null) return match;
        }
        return null;
    }

    private static MasterTreeNode Node(string title,string type)=>new(){Title=title,NodeKind="Type",FilterValue=type};

    public void PrepareForDisplay()
    {
        var client = _clientContext.CurrentClient;
        if (client is not null && (_preparedClientId != client.Id ||
            !_preparedFinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)))
            StatusText = "Stock stream is preparing in the background…";
        // Startup and post-commit verification belong to the shared Stock authority.
        // Opening this page must never launch a new normalization/import cycle.
    }

    public void WarmFromPreparedSnapshot(ClientWorkspaceSnapshot snapshot)
    {
        var client=_clientContext.CurrentClient;
        if(client is null || client.Id!=snapshot.ClientId ||
           !client.FinancialYear.Equals(snapshot.FinancialYear,StringComparison.OrdinalIgnoreCase)) return;

        _cachedStockItems = snapshot.StockItems;
        _cachedUnits = snapshot.StockUnits;
        _cachedSimpleMasters = snapshot.StockSimpleMasters;
        _cachedAliases = snapshot.StockItemAliases;
        _cachedUnitMappings = snapshot.StockUnitMappings;
        _cachedConversions = snapshot.QuantityConversions;
        _cachedUnitProfiles = snapshot.StockItemUnitProfiles;
        OnPropertyChanged(nameof(StockMasterUnitOptions));
        _preparedClientId=client.Id;
        _preparedFinancialYear=client.FinancialYear;

        var selectedItemId = SelectedStockItem?.PersistentId ?? 0;
        var selectedUnitId = SelectedUnit?.PersistentId ?? 0;
        var selectedSimpleId = SelectedSimpleMaster?.PersistentId ?? 0;
        ItemAliases.ReplaceRange(_cachedAliases);
        UnitMappings.ReplaceRange(_cachedUnitMappings);
        QuantityConversions.ReplaceRange(_cachedConversions.Where(IsActualConversion));
        StockItemUnitProfiles.ReplaceRange(_cachedUnitProfiles);
        BuildSections();
        RefreshMainRecords();
        if (selectedItemId > 0) SelectedStockItem = StockItems.FirstOrDefault(row => row.PersistentId == selectedItemId) ?? StockItems.FirstOrDefault();
        if (selectedUnitId > 0) SelectedUnit = Units.FirstOrDefault(row => row.PersistentId == selectedUnitId) ?? Units.FirstOrDefault();
        if (selectedSimpleId > 0) SelectedSimpleMaster = SimpleMasters.FirstOrDefault(row => row.PersistentId == selectedSimpleId) ?? SimpleMasters.FirstOrDefault();
        StatusText=$"Stock Master ready • {_cachedStockItems.Count:N0} item(s), {_cachedUnits.Count:N0} unit(s) prepared in its own cache stream.";
        NotifyCounts();
    }

    public void Refresh()
    {
        var client=_clientContext.CurrentClient;
        if(client is null)
        {
            _preparedClientId=0; _preparedFinancialYear=string.Empty;
            _cachedStockItems=[]; _cachedUnits=[]; _cachedSimpleMasters=[];
            _cachedAliases=[]; _cachedUnitMappings=[]; _cachedConversions=[]; _cachedUnitProfiles=[];
            OnPropertyChanged(nameof(StockMasterUnitOptions));
            BuildSections(); StockItems.Clear(); Units.Clear(); SimpleMasters.Clear();
            ItemAliases.Clear(); UnitMappings.Clear(); QuantityConversions.Clear(); StockItemUnitProfiles.Clear();
            StatusText="Open a client to use Stock Master."; NotifyCounts(); return;
        }

        _cachedStockItems=_repository.GetStockItems(client.Id,client.FinancialYear,true,string.Empty).ToArray();
        _cachedUnits=_repository.GetUnits(client.Id,client.FinancialYear,true,string.Empty).ToArray();
        _cachedSimpleMasters=SimpleTypes.SelectMany(type => _repository.GetSimpleMasters(client.Id,client.FinancialYear,type,true,string.Empty)).ToArray();
        _cachedAliases=_repository.GetItemAliases(client.Id,client.FinancialYear).ToArray();
        _cachedUnitMappings=_repository.GetUnitMappings(client.Id,client.FinancialYear).ToArray();
        _cachedConversions=_repository.GetQuantityConversionRules(client.Id,client.FinancialYear).ToArray();
        _cachedUnitProfiles=_repository.GetStockItemUnitProfiles(client.Id,client.FinancialYear).ToArray();
        OnPropertyChanged(nameof(StockMasterUnitOptions));
        _preparedClientId=client.Id; _preparedFinancialYear=client.FinancialYear;

        ItemAliases.ReplaceRange(_cachedAliases);
        UnitMappings.ReplaceRange(_cachedUnitMappings);
        QuantityConversions.ReplaceRange(_cachedConversions.Where(IsActualConversion));
        StockItemUnitProfiles.ReplaceRange(_cachedUnitProfiles);
        BuildSections(); RefreshMainRecords(); NotifyCounts();
    }

    private static bool IsActualConversion(QuantityConversionRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.ImportedUnit) || string.IsNullOrWhiteSpace(rule.FinalUnit)) return true;
        return !UnitEquivalent(rule.ImportedUnit, rule.FinalUnit);
    }

    private static bool UnitEquivalent(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        if (left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return AahUnitCatalog.TryResolvePreferred(left, out var leftCanonical, out var leftFactor) &&
               AahUnitCatalog.TryResolvePreferred(right, out var rightCanonical, out var rightFactor) &&
               leftFactor == 1m && rightFactor == 1m &&
               leftCanonical.Equals(rightCanonical, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshMainRecords()
    {
        var client=_clientContext.CurrentClient;
        if(client is null)
        {
            StockItems.ReplaceRange(Array.Empty<StockItemRecord>());
            Units.ReplaceRange(Array.Empty<StockUnitRecord>());
            SimpleMasters.ReplaceRange(Array.Empty<StockSimpleMasterRecord>());
            return;
        }
        var prepared = _preparedClientId==client.Id && _preparedFinancialYear.Equals(client.FinancialYear,StringComparison.OrdinalIgnoreCase);
        var type=SelectedSection?.FilterValue ?? "Stock Item";
        var search=SearchText.Trim();
        if(type.StartsWith("Stock Item",StringComparison.OrdinalIgnoreCase))
        {
            var groupFilter=type.Contains('|')?type[(type.IndexOf('|')+1)..].Trim():string.Empty;
            IEnumerable<StockItemRecord> rows = prepared ? _cachedStockItems : [];
            rows=rows.Where(row => IncludeArchived || !row.IsArchived);
            if(groupFilter.Length>0) rows=rows.Where(row => row.StockGroup.Equals(groupFilter,StringComparison.OrdinalIgnoreCase));
            if(search.Length>0) rows=rows.Where(row => MatchesStockSearch(row,search));
            StockItems.ReplaceRange(rows.ToArray());
            Units.ReplaceRange(Array.Empty<StockUnitRecord>());
            SimpleMasters.ReplaceRange(Array.Empty<StockSimpleMasterRecord>());
            SelectedStockItem=StockItems.FirstOrDefault(); StatusText=groupFilter.Length>0
                ? $"{StockItems.Count:N0} Stock Item(s) under '{groupFilter}'."
                : $"{StockItems.Count:N0} Stock Item(s) shown from prepared Stock cache.";
        }
        else if(type.Equals("Unit",StringComparison.OrdinalIgnoreCase))
        {
            IEnumerable<StockUnitRecord> rows = prepared ? _cachedUnits : [];
            rows=rows.Where(row => IncludeArchived || !row.IsArchived);
            if(search.Length>0) rows=rows.Where(row => MatchesUnitSearch(row,search));
            Units.ReplaceRange(rows.ToArray());
            StockItems.ReplaceRange(Array.Empty<StockItemRecord>());
            SimpleMasters.ReplaceRange(Array.Empty<StockSimpleMasterRecord>());
            SelectedUnit=Units.FirstOrDefault(); StatusText=$"{Units.Count:N0} Unit(s) shown from prepared Stock cache.";
        }
        else
        {
            IEnumerable<StockSimpleMasterRecord> rows = prepared
                ? _cachedSimpleMasters.Where(row => row.Type.Equals(type,StringComparison.OrdinalIgnoreCase)) : [];
            rows=rows.Where(row => IncludeArchived || !row.IsArchived);
            if(search.Length>0) rows=rows.Where(row => MatchesSimpleSearch(row,search));
            SimpleMasters.ReplaceRange(rows.ToArray());
            StockItems.ReplaceRange(Array.Empty<StockItemRecord>());
            Units.ReplaceRange(Array.Empty<StockUnitRecord>());
            SelectedSimpleMaster=SimpleMasters.FirstOrDefault(); StatusText=$"{SimpleMasters.Count:N0} {type} master(s) shown from prepared Stock cache.";
        }
        NotifyCounts();
    }

    private static bool MatchesStockSearch(StockItemRecord row,string search) =>
        row.Name.Contains(search,StringComparison.OrdinalIgnoreCase) ||
        row.StockGroup.Contains(search,StringComparison.OrdinalIgnoreCase) ||
        row.StockCategory.Contains(search,StringComparison.OrdinalIgnoreCase) ||
        row.BaseUnit.Contains(search,StringComparison.OrdinalIgnoreCase) ||
        row.PartNumberSku.Contains(search,StringComparison.OrdinalIgnoreCase) ||
        row.DefaultPurchaseUnit.Contains(search,StringComparison.OrdinalIgnoreCase) || row.DefaultSalesUnit.Contains(search,StringComparison.OrdinalIgnoreCase) || row.RateUnit.Contains(search,StringComparison.OrdinalIgnoreCase) ||
        row.TallyMasterId.Contains(search,StringComparison.OrdinalIgnoreCase) ||
        row.HsnSac.Contains(search,StringComparison.OrdinalIgnoreCase) ||
        row.Source.Contains(search,StringComparison.OrdinalIgnoreCase);

    private static bool MatchesUnitSearch(StockUnitRecord row,string search) =>
        row.Name.Contains(search,StringComparison.OrdinalIgnoreCase) ||
        row.FormalName.Contains(search,StringComparison.OrdinalIgnoreCase) ||
        row.ReportingUqc.Contains(search,StringComparison.OrdinalIgnoreCase) ||
        row.Source.Contains(search,StringComparison.OrdinalIgnoreCase);

    private static bool MatchesSimpleSearch(StockSimpleMasterRecord row,string search) =>
        row.Name.Contains(search,StringComparison.OrdinalIgnoreCase) ||
        row.Parent.Contains(search,StringComparison.OrdinalIgnoreCase) ||
        row.Source.Contains(search,StringComparison.OrdinalIgnoreCase);

    private void Add()
    {
        if (!RequireStockEdit("add Stock Master")) return;
        var client=_clientContext.CurrentClient; if(client is null) return; var company=string.IsNullOrWhiteSpace(client.LegalName)?client.ClientName:client.LegalName; var type=SelectedSection?.FilterValue??"Stock Item";
        if(type.StartsWith("Stock Item",StringComparison.OrdinalIgnoreCase))
        {
            var selectedGroup=type.Contains('|')?type[(type.IndexOf('|')+1)..].Trim():"Primary";
            var row=new StockItemRecord{ClientId=client.Id,FinancialYear=client.FinancialYear,Name="New Stock Item",StockGroup=string.IsNullOrWhiteSpace(selectedGroup)?"Primary":selectedGroup,Company=company,Source="Manual",IsUserEdited=true};StockItems.Insert(0,row);SelectedStockItem=row;
        }
        else if(type.Equals("Unit",StringComparison.OrdinalIgnoreCase))
        {
            var row=new StockUnitRecord{ClientId=client.Id,FinancialYear=client.FinancialYear,Name="New Unit",Company=company,Source="Manual",IsUserEdited=true};Units.Insert(0,row);SelectedUnit=row;
        }
        else
        {
            var row=new StockSimpleMasterRecord{ClientId=client.Id,FinancialYear=client.FinancialYear,Type=type,Name=$"New {type}",Parent="Primary",Company=company,Source="Manual",IsUserEdited=true};SimpleMasters.Insert(0,row);SelectedSimpleMaster=row;
        }
        StatusText=$"New {type} row added. Edit it and press Save.";
    }

    private void Save()
    {
        if (!RequireStockEdit("save Stock Master")) return;
        try
        {
            if(IsStockItemsSection && SelectedStockItem is not null)
            {
                var before = SelectedStockItem.PersistentId > 0
                    ? _repository.GetStockItems(SelectedStockItem.ClientId, SelectedStockItem.FinancialYear, includeArchived:true)
                        .FirstOrDefault(item => item.PersistentId == SelectedStockItem.PersistentId)
                    : null;
                var unitChanged = before is not null && !UnitEquivalent(before.BaseUnit, SelectedStockItem.BaseUnit);

                // Fix123: BaseUnit is the one Purchase/Sales Inventory/Transaction Unit.
                // Keep legacy compatibility fields aligned. A direct change saves the
                // desired Final Unit, but the repository disables old non-base profiles
                // until Quantity Conversion is explicitly confirmed; no factor is guessed.
                SelectedStockItem.DefaultPurchaseUnit = SelectedStockItem.BaseUnit;
                SelectedStockItem.DefaultSalesUnit = SelectedStockItem.BaseUnit;
                if (string.IsNullOrWhiteSpace(SelectedStockItem.RateUnit) ||
                    (before is not null && UnitEquivalent(SelectedStockItem.RateUnit, before.BaseUnit)))
                    SelectedStockItem.RateUnit = SelectedStockItem.BaseUnit;
                _repository.SaveStockItem(SelectedStockItem);
                StatusText = unitChanged
                    ? $"Stock Item '{SelectedStockItem.Name}' Final Unit changed to '{SelectedStockItem.BaseUnit}'. Imported rows are not converted until the Unit difference is confirmed in Quantity Conversion; deterministic factors are prefilled, package factors require your input."
                    : $"Stock Item '{SelectedStockItem.Name}' saved. Purchase and Sales will reproject live from the same Stock Master identity.";
            }
            else if(IsUnitsSection && SelectedUnit is not null){_repository.SaveUnit(SelectedUnit);StatusText=$"Unit '{SelectedUnit.Name}' saved.";}
            else if(SelectedSimpleMaster is not null){_repository.SaveSimpleMaster(SelectedSimpleMaster);StatusText=$"{SelectedSimpleMaster.Type} '{SelectedSimpleMaster.Name}' saved.";}
            var client=_clientContext.CurrentClient;if(client is not null)_liveChangeHub.Publish(client.Id,client.FinancialYear,AccountingLiveArea.StockMasters,"Manual Stock Master save");
            Refresh();
        }
        catch(InvalidOperationException ex)
        {
            StatusText=ex.Message+" Stock Master save was blocked.";
        }
    }

    private void Archive()
    {
        if (!RequireStockEdit("archive or restore Stock Master")) return;
        if(IsStockItemsSection && SelectedStockItem is { PersistentId: >0 } item){_repository.ArchiveStockItem(item.PersistentId,!item.IsArchived);}
        else if(IsUnitsSection && SelectedUnit is { PersistentId: >0 } unit){_repository.ArchiveUnit(unit.PersistentId,!unit.IsArchived);}
        else if(SelectedSimpleMaster is { PersistentId: >0 } simple){_repository.ArchiveSimpleMaster(simple.PersistentId,!simple.IsArchived);}
        var client=_clientContext.CurrentClient;if(client is not null)_liveChangeHub.Publish(client.Id,client.FinancialYear,AccountingLiveArea.StockMasters,"Stock Master archive/restore");
        Refresh();
    }

    private async Task ImportFromTallyAsync()
    {
        if (!CanImportStockMaster)
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, GovernancePermissions.Import, "import Stock Masters from Tally");
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            StatusText = "Open a client before importing Stock Masters from Tally.";
            return;
        }

        try
        {
            StatusText = "Import Stock From Tally: requesting one fresh Stock Master authority response…";
            var result = await _tallyCoordinator.ImportCurrentClientStockMastersAsync();
            Refresh();
            var missing = Math.Max(0, result.ReturnedStockItems - result.LiveTallyItemsAfter);
            StatusText = $"Tally Stock import • returned {result.ReturnedStockItems:N0} item(s), {result.ReturnedUnits:N0} unit(s), {result.ReturnedGodowns:N0} godown(s) • AAH Live Tally items {result.LiveTallyItemsBefore:N0} → {result.LiveTallyItemsAfter:N0} • new MASTERIDs {result.NewMasterIds:N0} • unresolved {missing:N0}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Import Stock From Tally failed: {ex.Message}";
        }
    }

    private void Import()
    {
        if (!CanImportStockMaster) { StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, GovernancePermissions.Import, "import Stock Masters"); return; }
        var client=_clientContext.CurrentClient; if(client is null)return;
        var dialog=new OpenFileDialog{Title="Import Stock Masters from Tally",Filter="Tally master files (*.xml;*.json)|*.xml;*.json|XML files (*.xml)|*.xml|JSON files (*.json)|*.json"};
        if(dialog.ShowDialog()!=true)return;
        try
        {
            var company=string.IsNullOrWhiteSpace(client.LegalName)?client.ClientName:client.LegalName;
            var parsed=_importService.Parse(dialog.FileName,client.Id,client.FinancialYear,company);
            var stockItems=parsed.Records.Where(r=>r.Type.Equals("Stock Item",StringComparison.OrdinalIgnoreCase)).Select(r=>new StockItemRecord{ClientId=client.Id,FinancialYear=client.FinancialYear,Name=r.Name,StockGroup=r.Parent,StockCategory=r.StockCategoryName,BaseUnit=r.BaseUnit,AdditionalUnit=r.AdditionalUnit,PartNumberSku=r.PartNumberSku,TallyMasterId=r.TallyMasterId,TallyGuid=r.TallyGuid,TallyAlterId=r.TallyAlterId,HsnSac=r.HsnSac,GstRate=r.GstRate,GstTypeOfSupply=r.GstTypeOfSupply,BatchWise=r.BatchWise,Perishable=r.Perishable,HasManufacturingDate=r.HasManufacturingDate,CostingMethod=r.CostingMethod,ValuationMethod=r.ValuationMethod,Company=company,Source="Tally Import"}).ToArray();
            var units=parsed.Records.Where(r=>r.Type.Equals("Unit",StringComparison.OrdinalIgnoreCase)).Select(r=>new StockUnitRecord{ClientId=client.Id,FinancialYear=client.FinancialYear,Name=r.Name,FormalName=r.FormalName,DecimalPlaces=Math.Clamp(r.DecimalPlaces,0,4),ReportingUqc=r.ReportingUqc,Conversion=r.Conversion,Company=company,Source="Tally Import"}).ToArray();
            var simple=parsed.Records.Where(r=>SimpleTypes.Contains(r.Type,StringComparer.OrdinalIgnoreCase)).Select(r=>new StockSimpleMasterRecord{ClientId=client.Id,FinancialYear=client.FinancialYear,Type=r.Type,Name=r.Name,Parent=r.Parent,Company=company,Source="Tally Import"}).ToArray();
            var count=_repository.UpsertStockItems(client.Id,client.FinancialYear,stockItems)+_repository.UpsertUnits(client.Id,client.FinancialYear,units)+_repository.UpsertSimpleMasters(client.Id,client.FinancialYear,simple);
            // Fix117: a manual Tally Master import must establish the same item-owned unit
            // authority as live Tally synchronization. Base/alternate relationships are not
            // left as loose text fields on the Stock Item.
            var savedItems=_repository.GetStockItems(client.Id,client.FinancialYear,includeArchived:true).ToArray();
            foreach(var source in parsed.Records.Where(r=>r.Type.Equals("Stock Item",StringComparison.OrdinalIgnoreCase)))
            {
                var saved=source.TallyMasterId.Trim().Length>0
                    ? savedItems.FirstOrDefault(item=>item.TallyMasterId.Equals(source.TallyMasterId,StringComparison.OrdinalIgnoreCase))
                    : savedItems.FirstOrDefault(item=>item.Name.Equals(source.Name,StringComparison.OrdinalIgnoreCase));
                if(saved is null||saved.PersistentId<=0||saved.BaseUnit.Trim().Length==0) continue;
                _repository.SaveStockItemUnitProfile(new StockItemUnitProfileRecord{ClientId=client.Id,FinancialYear=client.FinancialYear,StockItemId=saved.PersistentId,StockItemName=saved.Name,UnitCode=saved.BaseUnit,FactorToBase=1m,IsAllowed=true,Source="Tally Import Base Unit",UpdatedUtc=DateTime.UtcNow});
                var importedAlternateFactor = source.Denominator is > 0m && source.Conversion is > 0m
                    ? source.Denominator.Value / source.Conversion.Value
                    : source.Conversion is > 0m ? source.Conversion.Value : 0m;
                if(source.AdditionalUnit.Trim().Length>0 && importedAlternateFactor>0m)
                    _repository.SaveStockItemUnitProfile(new StockItemUnitProfileRecord{ClientId=client.Id,FinancialYear=client.FinancialYear,StockItemId=saved.PersistentId,StockItemName=saved.Name,UnitCode=source.AdditionalUnit,FactorToBase=importedAlternateFactor,IsAllowed=true,IsTallyAlternateUnit=true,Source="Tally Import Alternate Unit",UpdatedUtc=DateTime.UtcNow});
            }
            if (_automationService is not null)
                _automationService.PublishCommittedAccountingImport(client.Id,client.FinancialYear,"Stock",count,dialog.FileName,"Manual Stock Master Import");
            else
                _liveChangeHub.Publish(client.Id,client.FinancialYear,AccountingLiveArea.StockMasters,"Stock Master import");
            Refresh(); StatusText=$"Imported/synchronized {count:N0} Stock Master object(s) from {Path.GetFileName(dialog.FileName)}.";
        }
        catch(Exception ex){StatusText=$"Stock Master import failed: {ex.Message}";}
    }

    private void AddAlias(){if(!RequireStockEdit("add Stock Item mapping"))return;var c=_clientContext.CurrentClient;if(c is null)return;var row=new StockItemAliasRecord{ClientId=c.Id,FinancialYear=c.FinancialYear};ItemAliases.Insert(0,row);SelectedAlias=row;}
    private void SaveAlias(){if(!RequireStockEdit("save Stock Item mapping"))return;var c=_clientContext.CurrentClient;var row=SelectedAlias;if(c is null||row is null)return;_repository.SaveItemAlias(c.Id,c.FinancialYear,row.ImportedAlias,row.StockItemName,"Manual Stock Master Mapping");_liveChangeHub.Publish(c.Id,c.FinancialYear,AccountingLiveArea.StockMasters,"Item Name mapping changed");Refresh();StatusText="Item Name alias mapping saved.";}
    private void AddUnitMapping(){if(!RequireStockEdit("add Unit mapping"))return;var c=_clientContext.CurrentClient;if(c is null)return;var row=new StockUnitMappingRecord{ClientId=c.Id,FinancialYear=c.FinancialYear};UnitMappings.Insert(0,row);SelectedUnitMapping=row;}
    private void SaveUnitMapping()
    {
        if(!RequireStockEdit("save Unit mapping"))return;
        var c=_clientContext.CurrentClient;var row=SelectedUnitMapping;if(c is null||row is null)return;
        if(!AahUnitCatalog.TryGetDefinition(row.FinalUnit,out var definition)){StatusText="Select the Final Unit from the AAH standard Unit list.";return;}
        row.FinalUnit=definition.Code;
        if(!AahUnitCatalog.TryGetFactor(row.ImportedUnit,row.FinalUnit,out var unitAliasFactor) || unitAliasFactor != 1m)
        {
            StatusText="Units only saves equivalent/canonical names with factor 1. This choice changes quantity, so use Quantity Conversion and confirm its factor instead.";
            return;
        }
        _repository.SaveUnitMapping(c.Id,c.FinancialYear,row.StockItemName,row.ImportedUnit,row.FinalUnit);
        _liveChangeHub.Publish(c.Id,c.FinancialYear,AccountingLiveArea.StockMasters,"Item-specific Unit mapping changed");Refresh();StatusText="Equivalent Unit mapping saved. It only canonicalizes the Unit name (factor 1); quantity and rate are unchanged.";
    }
    private void AddUnitProfile()
    {
        if(!RequireStockEdit("add Stock Item Unit Profile"))return; var c=_clientContext.CurrentClient; var item=SelectedStockItem; if(c is null||item is null||item.PersistentId<=0)return;
        var row=new StockItemUnitProfileRecord{ClientId=c.Id,FinancialYear=c.FinancialYear,StockItemId=item.PersistentId,StockItemName=item.Name,UnitCode=string.Empty,FactorToBase=1m,IsAllowed=true,Source="Manual",UpdatedUtc=DateTime.UtcNow};
        StockItemUnitProfiles.Insert(0,row); SelectedUnitProfile=row; StatusText=$"Add an allowed Unit for '{item.Name}'. Factor means 1 selected Unit = Factor × Base Unit '{item.BaseUnit}'.";
    }

    private void SaveUnitProfile()
    {
        if(!RequireStockEdit("save Stock Item Unit Profile"))return; var row=SelectedUnitProfile; if(row is null)return;
        try{_repository.SaveStockItemUnitProfile(row);var c=_clientContext.CurrentClient;if(c is not null)_liveChangeHub.Publish(c.Id,c.FinancialYear,AccountingLiveArea.StockMasters,"Stock Item Unit Profile changed");Refresh();StatusText="Stock Item Unit Profile saved. Physical factors are AAH-authoritative; package factors remain item-specific.";}catch(Exception ex){StatusText=ex.Message;}
    }

    private void DeleteUnitProfile()
    {
        if(!RequireStockEdit("delete Stock Item Unit Profile"))return; var row=SelectedUnitProfile;if(row is null||row.PersistentId<=0)return;
        _repository.DeleteStockItemUnitProfile(row.PersistentId);
        var c=_clientContext.CurrentClient;if(c is not null)_liveChangeHub.Publish(c.Id,c.FinancialYear,AccountingLiveArea.StockMasters,"Stock Item Unit Profile removed");
        Refresh();StatusText="Stock Item Unit Profile removed and existing Purchase/Sales inventory rows are being updated live.";
    }

    private void AddConversion(){if(!RequireStockEdit("add Quantity Conversion"))return;var c=_clientContext.CurrentClient;if(c is null)return;var row=new QuantityConversionRule{ClientId=c.Id,FinancialYear=c.FinancialYear,Factor=0m,RememberForClient=true};QuantityConversions.Insert(0,row);SelectedConversion=row;}
    private void SaveConversion()
    {
        if(!RequireStockEdit("save Quantity Conversion"))return;
        var row=SelectedConversion;if(row is null)return;
        if(!AahUnitCatalog.TryGetDefinition(row.FinalUnit,out var definition)){StatusText="Select the Final Unit from the AAH standard Unit list.";return;}
        row.FinalUnit=definition.Code;
        if (!IsActualConversion(row))
        {
            StatusText="Imported Unit and Final Unit are the same. No Quantity Conversion is required or saved.";
            return;
        }
        var deterministic=AahUnitCatalog.TryGetFactor(row.ImportedUnit,row.FinalUnit,out var factor);
        if(deterministic){row.Factor=factor;row.IsUniversal=true;}
        else
        {
            row.IsUniversal=false;
            if(string.IsNullOrWhiteSpace(row.StockItemName)){StatusText="Non-deterministic package conversion must be item-specific. Select the Stock Item first.";return;}
            if(row.Factor<=0m){StatusText="Enter a factor greater than zero for this item-specific package conversion.";return;}
        }
        // Fix123: a confirmed conversion is always durable. The old Remember checkbox
        // is removed because forgetting a confirmed factor would make refresh/reimport
        // mathematically inconsistent.
        row.RememberForClient=true;
        try
        {
            if(!string.IsNullOrWhiteSpace(row.StockItemName))
            {
                // Fix123: Quantity Conversion is NOT a Unit Mapping. Saving a package
                // relationship such as QTL -> PCS into StockUnitMappings made the normalizer
                // treat it as a factor-1 spelling substitution and could bypass the saved
                // conversion factor. Persist the factor on the Stock Item Unit Profile instead.
                // Do this before saving the rule so a protected Tally Base-Unit rejection
                // cannot leave behind a conversion that was never actually activated.
                _repository.ChangeStockItemInventoryUnit(
                    row.ClientId, row.FinancialYear, row.StockItemName, row.FinalUnit,
                    row.ImportedUnit, row.Factor, "Manual Stock Master Quantity Conversion");
            }
            _repository.SaveQuantityConversionRule(row);
        }
        catch(InvalidOperationException ex)
        {
            StatusText=ex.Message;
            return;
        }
        var c=_clientContext.CurrentClient;if(c is not null)_liveChangeHub.Publish(c.Id,c.FinancialYear,AccountingLiveArea.StockMasters,"Quantity conversion rule changed");Refresh();StatusText=deterministic?"AAH deterministic quantity conversion confirmed and saved permanently for this Client + FY.":"Item-specific package conversion confirmed and saved permanently for this Client + FY. Factor means 1 imported unit = Factor final units.";
    }


    private void RefreshGstDirectory()
    {
        try
        {
            GstHsnSacDirectory.ReplaceRange(_repository.GetGstHsnSacDirectory(GstDirectorySearchText, 10000));
            OnPropertyChanged(nameof(GstDirectorySummary));
        }
        catch
        {
            GstHsnSacDirectory.Clear();
            OnPropertyChanged(nameof(GstDirectorySummary));
        }
    }

    private async Task RefreshOfficialGstDirectoryAsync()
    {
        StatusText = "Downloading the official GSTN HSN/SAC directory…";
        try
        {
            var rows = await _gstDirectoryService.DownloadOfficialAsync();
            var count = _repository.ReplaceGstHsnSacDirectory(rows, "GSTN official HSN_SAC.xlsx");
            RefreshGstDirectory();
            StatusText = $"Official GSTN HSN/SAC directory refreshed • {count:N0} code(s) stored globally in AAH.";
        }
        catch (Exception ex)
        {
            StatusText = $"GSTN directory download was unavailable: {ex.Message} Download HSN_SAC.xlsx from GST Portal > Services > User Services > Search HSN Code, then use Import GST Directory.";
        }
    }

    private void ImportGstDirectory()
    {
        if (!CanImportStockMaster) return;
        var dialog = new OpenFileDialog
        {
            Title = "Select official GSTN HSN_SAC.xlsx",
            Filter = "GST HSN/SAC Excel (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var rows = _gstDirectoryService.ReadDirectory(dialog.FileName, "GSTN HSN/SAC directory import");
            var count = _repository.ReplaceGstHsnSacDirectory(rows, Path.GetFileName(dialog.FileName));
            RefreshGstDirectory();
            StatusText = $"GST HSN/SAC directory imported • {count:N0} code(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"GST HSN/SAC directory import failed: {ex.Message}";
        }
    }

    private void ApplySelectedGstCode()
    {
        if (!RequireStockEdit("apply GST HSN/SAC code")) return;
        var item = SelectedStockItem;
        var directory = SelectedGstDirectoryRow;
        if (item is null || directory is null) return;
        item.HsnSac = directory.Code;
        if (string.IsNullOrWhiteSpace(item.GstTypeOfSupply))
            item.GstTypeOfSupply = directory.Kind.Equals("SAC", StringComparison.OrdinalIgnoreCase) ? "Services" : "Goods";
        _repository.SaveStockItem(item);
        var client = _clientContext.CurrentClient;
        if (client is not null)
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.StockMasters, "GST HSN/SAC applied to Stock Item");
        Refresh();
        StatusText = $"GST {directory.Kind} {directory.Code} applied to '{item.Name}'. Description: {directory.Description}";
    }

    private static void OpenGstSearch()
    {
        try
        {
            Process.Start(new ProcessStartInfo(GstHsnSacDirectoryService.OfficialSearchUrl) { UseShellExecute = true });
        }
        catch { }
    }

    private void NotifyCounts(){OnPropertyChanged(nameof(StockItemCount));OnPropertyChanged(nameof(UnitCount));OnPropertyChanged(nameof(MappingCount));}
    private void OnLiveChange(object? sender, AccountingLiveChange change)
    {
        // MainWindow's module cache owns authoritative reloads. Directly calling
        // Refresh here used to perform seven SQLite reads on the WPF dispatcher,
        // including while Stock Master was hidden or a different client was selected.
        // Preserve the current grid and let ModuleReady publish the replacement.
    }
    private void OnClientChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName!=nameof(ActiveClientContext.CurrentClient)) return;
        _preparedClientId=0;_preparedFinancialYear=string.Empty;
        _cachedStockItems=[];_cachedUnits=[];_cachedSimpleMasters=[];
        _cachedAliases=[];_cachedUnitMappings=[];_cachedConversions=[];_cachedUnitProfiles=[];
        StockItems.Clear();Units.Clear();SimpleMasters.Clear();
        ItemAliases.Clear();UnitMappings.Clear();QuantityConversions.Clear();StockItemUnitProfiles.Clear();
        SelectedStockItem=null;SelectedUnit=null;SelectedSimpleMaster=null;
        OnPropertyChanged(nameof(StockMasterUnitOptions));
        NotifyCounts();
        StatusText=_clientContext.CurrentClient is null?"Open a client to use Stock Master.":"Stock Master is preparing in the background.";
        OnPropertyChanged(nameof(CanViewStockMaster));
        OnPropertyChanged(nameof(CanImportStockMaster));
        OnPropertyChanged(nameof(CanEditStockMaster));
        OnPropertyChanged(nameof(IsStockMasterReadOnly));
        OnPropertyChanged(nameof(StockMasterPermissionSummary));
        ImportCommand.RaiseCanExecuteChanged();
        ImportFromTallyCommand.RaiseCanExecuteChanged();
        AddCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        ArchiveCommand.RaiseCanExecuteChanged();
        SaveAliasCommand.RaiseCanExecuteChanged();
        SaveUnitMappingCommand.RaiseCanExecuteChanged();
        SaveConversionCommand.RaiseCanExecuteChanged();
        AddAliasCommand.RaiseCanExecuteChanged();
        AddUnitMappingCommand.RaiseCanExecuteChanged();
        AddConversionCommand.RaiseCanExecuteChanged();
        AddUnitProfileCommand.RaiseCanExecuteChanged();
        SaveUnitProfileCommand.RaiseCanExecuteChanged();
        DeleteUnitProfileCommand.RaiseCanExecuteChanged();
    }
}
