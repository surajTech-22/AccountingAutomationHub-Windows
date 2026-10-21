using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Collections;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Desktop.Views;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Infrastructure.Gst;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Governance;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class MasterCentreViewModel : PageViewModel
{
    private readonly record struct MasterCentreViewState(
        long ClientId,
        string FinancialYear,
        string NodeKind,
        string FilterValue,
        long SelectedRecordId);

    private static readonly string[] AccountingTypes = ["Group", "Ledger", "Voucher Type", "State", "Currency", "IncomeTaxClassification", "IncomeTaxSlab"];
    private static readonly HashSet<string> StockTypes = new(StringComparer.OrdinalIgnoreCase) { "Stock Group", "Stock Item", "Stock Category", "Unit", "Godown", "Cost Category", "Cost Centre", "Tax Classification", "TaxUnit" };

    private readonly SqliteMasterRepository _repository;
    private readonly TallyMasterImportService _importService;
    private readonly AccountingRefreshCoordinator _refreshCoordinator;
    private readonly ActiveClientContext _clientContext;
    private readonly AccountingLiveChangeHub _liveChangeHub;
    private readonly BackgroundAutomationService? _automationService;
    private readonly SqliteGovernanceRepository _governanceRepository;
    private readonly TallyLiveConnectionCoordinator _tallyCoordinator;
    private readonly PreviousFinancialYearMasterImportService _previousFyImportService;
    private readonly List<MasterRecord> _allRecords = [];
    private readonly List<MasterRecord> _gridSelectedRecords = [];

    private MasterRecord? _selectedRecord;
    private MasterTreeNode? _selectedTreeNode;
    private string _searchText = string.Empty;
    private bool _includeArchived;
    private bool _showDuplicatesOnly;
    private bool _isBusy;
    private string _statusText = "Master Centre ready.";
    private long _preparedClientId;
    private string _preparedFinancialYear = string.Empty;

    public MasterCentreViewModel(
        SqliteMasterRepository repository,
        TallyMasterImportService importService,
        AccountingRefreshCoordinator refreshCoordinator,
        ActiveClientContext clientContext,
        AccountingLiveChangeHub liveChangeHub,
        SqliteGovernanceRepository governanceRepository,
        TallyLiveConnectionCoordinator tallyCoordinator,
        PreviousFinancialYearMasterImportService previousFyImportService,
        BackgroundAutomationService? automationService = null)
        : base(
            "Ledger Master",
            "Tally-style accounting masters: Groups, Ledgers, Voucher Types and statutory accounting masters")
    {
        _repository = repository;
        _importService = importService;
        _refreshCoordinator = refreshCoordinator;
        _clientContext = clientContext;
        _liveChangeHub = liveChangeHub;
        _automationService = automationService;
        _governanceRepository = governanceRepository;
        _tallyCoordinator = tallyCoordinator;
        _previousFyImportService = previousFyImportService;
        _clientContext.PropertyChanged += OnClientContextPropertyChanged;
        _liveChangeHub.Changed += OnLiveDataChanged;

        RefreshCommand = new RelayCommand(Refresh);
        ImportCommand = new RelayCommand(Import, () => !IsBusy && CanImportMasters);
        ImportPreviousFyCommand = new RelayCommand(ImportPreviousFinancialYear, () => !IsBusy && CanImportMasters && CurrentClientId > 0);
        NewLedgerCommand = new RelayCommand(CreateNewLedger, () => CurrentClientId > 0 && CanCreateMaster);
        NewGroupCommand = new RelayCommand(CreateNewGroup, () => CurrentClientId > 0 && CanCreateMaster);
        EditManualMasterCommand = new RelayCommand(EditManualMaster, CanEditSelectedManualMaster);
        MergeUpdateCommand = new AsyncRelayCommand(MergeAndUpdateAsync, CanMergeSelectedMaster);
        ArchiveCommand = new RelayCommand(
            Archive,
            () => CanDeleteMaster && SelectedRecord is { PersistentId: > 0 } record &&
                  (IsEditableLocalRecord(record) ||
                   (record.IsArchived && _repository.CanRestoreMergedArchive(record.ClientId, record.FinancialYear, record.PersistentId))));
        MakeActiveCommand = new RelayCommand(
            MakeArchivedMasterActive,
            () => CanDeleteMaster && SelectedRecord is { PersistentId: > 0, IsArchived: true } record &&
                  (record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ||
                   record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase)));
        DeleteCommand = new RelayCommand(Delete, () => CanDeleteMaster && SelectedRecord is not null && SelectedRecord.PersistentId > 0 && IsEditableLocalRecord(SelectedRecord));
        SynchronizeCommand = new AsyncRelayCommand(SynchronizeAsync, () => CurrentClientId > 0 && CanImportMasters);
        StatusText = "Ledger Master is preparing in the background.";
    }

    public BulkObservableCollection<MasterRecord> Records { get; } = [];
    public ObservableCollection<MasterTreeNode> MasterTree { get; } = [];

    public IReadOnlyList<string> MasterTypes { get; } =
    [
        "Group", "Ledger", "Voucher Type", "State", "Currency",
        "IncomeTaxClassification", "IncomeTaxSlab"
    ];

    public bool CanImportMasters => DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.Import) &&
        (!NetworkClientRuntime.IsNetworkClient || !NetworkClientRuntime.AccountingAccessSuspended);
    public bool CanCreateMaster => DesktopPermissionService.CanMutateAccounting(_governanceRepository, GovernancePermissions.CreateMaster);
    public bool CanEditMaster => CanCreateMaster;
    public bool CanMergeMaster => DesktopPermissionService.CanMutateAccounting(_governanceRepository, GovernancePermissions.MasterMerge);
    public bool CanDeleteMaster => DesktopPermissionService.CanMutateAccounting(_governanceRepository, GovernancePermissions.DeleteAccounting);
    public bool IsMasterReadOnly => !CanEditMaster;
    public string MasterPermissionSummary => $"Role: {DesktopPermissionService.EffectiveRoleName(_governanceRepository)} • {(NetworkClientRuntime.IsNetworkClient ? "Windows processing node • " : string.Empty)}Import {(CanImportMasters ? "allowed" : "denied")} • Create/Edit {(CanEditMaster ? "allowed" : "denied")} • Merge {(CanMergeMaster ? "allowed" : "denied")} • Delete {(CanDeleteMaster ? "allowed" : "denied")}";

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand ImportPreviousFyCommand { get; }
    public RelayCommand NewLedgerCommand { get; }
    public RelayCommand NewGroupCommand { get; }
    public RelayCommand EditManualMasterCommand { get; }
    public AsyncRelayCommand MergeUpdateCommand { get; }
    public RelayCommand ArchiveCommand { get; }
    public RelayCommand MakeActiveCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public AsyncRelayCommand SynchronizeCommand { get; }

    public MasterRecord? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (SetProperty(ref _selectedRecord, value))
            {
                EditManualMasterCommand.RaiseCanExecuteChanged();
                MergeUpdateCommand.RaiseCanExecuteChanged();
                ArchiveCommand.RaiseCanExecuteChanged();
                MakeActiveCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public void SetGridSelection(IEnumerable<MasterRecord> selectedRecords)
    {
        var selected = selectedRecords
            .Where(record => record.PersistentId > 0)
            .GroupBy(record => record.PersistentId)
            .Select(group => group.First())
            .ToArray();

        if (_gridSelectedRecords.Count == selected.Length &&
            _gridSelectedRecords.Select(record => record.PersistentId).OrderBy(id => id)
                .SequenceEqual(selected.Select(record => record.PersistentId).OrderBy(id => id)))
            return;

        _gridSelectedRecords.Clear();
        _gridSelectedRecords.AddRange(selected);
        if (selected.Length > 0 &&
            (SelectedRecord is null || selected.All(record => record.PersistentId != SelectedRecord.PersistentId)))
            SelectedRecord = selected[^1];

        MergeUpdateCommand.RaiseCanExecuteChanged();
        if (selected.Length == 2)
            StatusText = $"Two Masters selected: '{selected[0].Name}' and '{selected[1].Name}'. Merge + Update will use only these two; choose direction in the dialog.";
    }

    public MasterTreeNode? SelectedTreeNode
    {
        get => _selectedTreeNode;
        set
        {
            if (SetProperty(ref _selectedTreeNode, value))
            {
                ApplyRecordFilter();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyRecordFilter();
            }
        }
    }

    public bool IncludeArchived
    {
        get => _includeArchived;
        set
        {
            if (SetProperty(ref _includeArchived, value))
                Refresh();
        }
    }


    public bool ShowDuplicatesOnly
    {
        get => _showDuplicatesOnly;
        set
        {
            if (SetProperty(ref _showDuplicatesOnly, value))
            {
                ApplyRecordFilter();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ImportCommand.RaiseCanExecuteChanged();
                ImportPreviousFyCommand.RaiseCanExecuteChanged();
                MergeUpdateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public int ActiveCount => _allRecords.Count(record => !record.IsArchived);
    public int GroupCount => _allRecords.Count(record =>
        record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase));
    public int LedgerCount => _allRecords.Count(record =>
        record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase));
    public int DuplicateMasterCount => _allRecords
        .Where(record => !record.IsArchived && !StockTypes.Contains(record.Type))
        .GroupBy(MasterDuplicateIdentity, StringComparer.OrdinalIgnoreCase)
        .Count(group => group.Count() > 1);

    private long CurrentClientId => _clientContext.CurrentClient?.Id ?? 0;
    private string CurrentFinancialYear => _clientContext.CurrentClient?.FinancialYear ?? string.Empty;

    public void PrepareForDisplay()
    {
        var client = _clientContext.CurrentClient;
        if (client is not null && (_preparedClientId != client.Id ||
            !_preparedFinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)))
            StatusText = "Master stream is preparing in the background…";
    }

    public void WarmFromPreparedSnapshot(ClientWorkspaceSnapshot snapshot)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != snapshot.ClientId ||
            !client.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;

        // Fix45: live/cache Masters refresh must never throw the operator back to
        // the Chart of Accounts root. Capture the exact selected tree scope and row
        // before replacing the prepared snapshot, then restore it against the new tree.
        var viewState = CaptureViewState();
        _allRecords.Clear(); Records.Clear(); MasterTree.Clear();
        _allRecords.AddRange(snapshot.Masters
            .Where(record => !StockTypes.Contains(record.Type))
            .Where(record => IncludeArchived || !record.IsArchived));
        // Completion 61: usage counts are populated by the Masters cache worker.
        // Opening Ledger Master must not issue another SQLite usage scan on the UI thread.
        ApplyDuplicateReviewState();
        BuildTallyTree();
        RestoreViewState(viewState);
        _preparedClientId = client.Id;
        _preparedFinancialYear = client.FinancialYear;
        StatusText = $"Ledger Master ready • {_allRecords.Count:N0} master(s) prepared in background.";
        NotifyCounts();
    }

    public void Refresh()
    {
        // Fix45: ordinary/live refreshes preserve the exact Ledger Master page.
        // Client switches are excluded by RestoreViewState's Client + FY guard.
        var viewState = CaptureViewState();
        _allRecords.Clear();
        Records.Clear();
        MasterTree.Clear();

        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            _preparedClientId = 0;
            _preparedFinancialYear = string.Empty;
            StatusText = "Open a client to use its Master Centre.";
            NotifyCounts();
            return;
        }

        // Remove only invalid placeholder rows left by older UI versions. Valid
        // duplicate and archived Manual Masters are deliberately preserved for the
        // review-first duplicate workflow; Tally/imported/system Masters are untouched.
        var removedManualRows = _repository.CleanupManualInvalidAndDuplicateRecords(client.Id, client.FinancialYear);

        _allRecords.AddRange(_repository.Get(
            client.Id,
            client.FinancialYear,
            IncludeArchived,
            string.Empty)
            .Where(record => !StockTypes.Contains(record.Type)));

        _repository.PopulateUsageCounts(client.Id, client.FinancialYear, _allRecords);
        ApplyDuplicateReviewState();
        BuildTallyTree();
        RestoreViewState(viewState);
        _preparedClientId = client.Id;
        _preparedFinancialYear = client.FinancialYear;
        NotifyCounts();
        if (removedManualRows > 0)
            StatusText = $"Removed {removedManualRows:N0} invalid placeholder Manual Master row(s). Valid duplicates and archived Masters were preserved for review.";
    }

    private MasterCentreViewState CaptureViewState()
    {
        // The tree belongs to the last prepared Client + FY, not necessarily to the
        // newly assigned ActiveClientContext during a client switch. This ownership
        // stamp prevents a page from the old client being restored into the new one.
        return new MasterCentreViewState(
            _preparedClientId,
            _preparedFinancialYear,
            SelectedTreeNode?.NodeKind ?? string.Empty,
            SelectedTreeNode?.FilterValue ?? string.Empty,
            SelectedRecord?.PersistentId ?? 0);
    }

    private void RestoreViewState(
        MasterCentreViewState state,
        long preferredRecordId = 0,
        string fallbackType = "")
    {
        var client = _clientContext.CurrentClient;
        MasterTreeNode? restoredNode = null;
        if (client is not null &&
            state.ClientId == client.Id &&
            state.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase) &&
            state.NodeKind.Length > 0)
        {
            restoredNode = FindNode(state.NodeKind, state.FilterValue);
        }

        if (restoredNode is null && fallbackType.Length > 0)
            restoredNode = FindNode("Type", fallbackType);
        restoredNode ??= MasterTree.FirstOrDefault();

        _selectedTreeNode = restoredNode;
        OnPropertyChanged(nameof(SelectedTreeNode));
        ApplyRecordFilter();

        var recordId = preferredRecordId > 0 ? preferredRecordId : state.SelectedRecordId;
        if (recordId > 0)
        {
            var restoredRecord = Records.FirstOrDefault(record => record.PersistentId == recordId);
            if (restoredRecord is not null) SelectedRecord = restoredRecord;
        }
    }

    private void BuildTallyTree()
    {
        var chart = new MasterTreeNode
        {
            Title = "Chart of Accounts",
            NodeKind = "TypeSet",
            FilterValue = string.Join('|', AccountingTypes),
            RecordCount = _allRecords.Count(record => AccountingTypes.Contains(record.Type, StringComparer.OrdinalIgnoreCase)),
            IsExpanded = true
        };

        var groups = new MasterTreeNode
        {
            Title = "Groups",
            NodeKind = "Type",
            FilterValue = "Group",
            RecordCount = GroupCount,
            IsExpanded = true
        };
        BuildGroupHierarchy(groups);
        chart.Children.Add(groups);
        chart.Children.Add(TypeNode("Ledgers", "Ledger"));
        chart.Children.Add(TypeNode("Voucher Types", "Voucher Type"));

        var otherTypes = MasterTypes
            .Where(type => !new[] { "Group", "Ledger", "Voucher Type" }.Contains(type, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var statutory = new MasterTreeNode
        {
            Title = "Statutory & Other Masters",
            NodeKind = "TypeSet",
            FilterValue = string.Join('|', otherTypes),
            RecordCount = _allRecords.Count(record => otherTypes.Contains(record.Type, StringComparer.OrdinalIgnoreCase)),
            IsExpanded = false
        };
        foreach (var type in otherTypes
                     .Where(type => _allRecords.Any(record => record.Type.Equals(type, StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(type => type, StringComparer.OrdinalIgnoreCase))
        {
            statutory.Children.Add(TypeNode(type, type));
        }

        MasterTree.Add(chart);
        MasterTree.Add(statutory);
    }

    private MasterTreeNode TypeNode(string title, string type) =>
        new()
        {
            Title = title,
            NodeKind = "Type",
            FilterValue = type,
            RecordCount = _allRecords.Count(record => record.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
        };

    private void BuildGroupHierarchy(MasterTreeNode groupRoot)
    {
        var groups = _allRecords
            .Where(record => record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            .GroupBy(record => record.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(record => record.Name.Trim(), StringComparer.OrdinalIgnoreCase);

        var childMap = groups.Values
            .GroupBy(record => string.IsNullOrWhiteSpace(record.Parent) ? "Primary" : record.Parent.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);

        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootGroup in groups.Values
                     .Where(record => IsPrimaryParent(record.Parent) || !groups.ContainsKey(record.Parent.Trim()))
                     .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase))
        {
            groupRoot.Children.Add(BuildGroupNode(rootGroup, childMap, added, 0));
        }

        foreach (var orphan in groups.Values
                     .Where(record => !added.Contains(record.Name))
                     .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase))
        {
            groupRoot.Children.Add(BuildGroupNode(orphan, childMap, added, 0));
        }
    }

    private MasterTreeNode BuildGroupNode(
        MasterRecord group,
        IReadOnlyDictionary<string, MasterRecord[]> childMap,
        ISet<string> added,
        int depth)
    {
        added.Add(group.Name);
        var directCount = _allRecords.Count(record =>
            record.Parent.Equals(group.Name, StringComparison.OrdinalIgnoreCase));
        var node = new MasterTreeNode
        {
            Title = group.Name,
            NodeKind = "GroupContents",
            FilterValue = group.Name,
            RecordCount = directCount,
            IsExpanded = depth < 1
        };

        if (depth < 20 && childMap.TryGetValue(group.Name, out var children))
        {
            foreach (var child in children.Where(child => !added.Contains(child.Name)))
            {
                node.Children.Add(BuildGroupNode(child, childMap, added, depth + 1));
            }
        }
        return node;
    }

    private static bool IsPrimaryParent(string parent) =>
        string.IsNullOrWhiteSpace(parent) ||
        parent.Equals("Primary", StringComparison.OrdinalIgnoreCase) ||
        parent.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase);

    private void ApplyRecordFilter()
    {
        IEnumerable<MasterRecord> query = _allRecords;
        var node = SelectedTreeNode;
        if (node is not null)
        {
            query = node.NodeKind switch
            {
                "Type" => query.Where(record => record.Type.Equals(node.FilterValue, StringComparison.OrdinalIgnoreCase)),
                "TypeSet" => FilterTypeSet(query, node.FilterValue),
                "GroupContents" => query.Where(record => record.Parent.Equals(node.FilterValue, StringComparison.OrdinalIgnoreCase)),
                _ => query
            };
        }

        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            query = query.Where(record =>
                record.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                record.Parent.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                record.Type.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                record.Company.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (ShowDuplicatesOnly)
        {
            var issueIds = _allRecords
                .Where(record => record.DuplicateCount > 1)
                .Select(record => record.PersistentId)
                .ToHashSet();
            query = query.Where(record => issueIds.Contains(record.PersistentId));
        }

        var visible = query
            .OrderBy(record => record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(record => record.Parent, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Records.ReplaceRange(visible);

        SelectedRecord = Records.FirstOrDefault();
        var scope = node?.Title ?? "All Masters";
        StatusText = $"{Records.Count:N0} records shown in {scope}; {_allRecords.Count:N0} total client masters" +
                     (ShowDuplicatesOnly ? "; duplicate-only review is active." : ".");
    }

    private static IEnumerable<MasterRecord> FilterTypeSet(
        IEnumerable<MasterRecord> records,
        string typeSet)
    {
        var types = typeSet.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return records.Where(record => types.Contains(record.Type, StringComparer.OrdinalIgnoreCase));
    }

    public void ApplyMetricFilter(string metric)
    {
        var normalized = (metric ?? string.Empty).Trim();
        if (normalized.Length == 0) return;

        if (_includeArchived)
        {
            _includeArchived = false;
            OnPropertyChanged(nameof(IncludeArchived));
            Refresh();
        }

        _showDuplicatesOnly = normalized.Equals("Duplicates", StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(ShowDuplicatesOnly));

        var target = normalized switch
        {
            var value when value.Equals("Groups", StringComparison.OrdinalIgnoreCase) => FindNode("Type", "Group"),
            var value when value.Equals("Ledgers", StringComparison.OrdinalIgnoreCase) => FindNode("Type", "Ledger"),
            _ => MasterTree.FirstOrDefault(node => node.Title.Equals("Chart of Accounts", StringComparison.OrdinalIgnoreCase))
        };

        if (target is not null)
        {
            _selectedTreeNode = target;
            OnPropertyChanged(nameof(SelectedTreeNode));
        }
        SearchText = string.Empty;
        ApplyRecordFilter();

        StatusText = normalized switch
        {
            "Groups" => $"Groups filter active • {Records.Count:N0} record(s).",
            "Ledgers" => $"Ledgers filter active • {Records.Count:N0} record(s).",
            "Duplicates" => $"Duplicate Masters filter active • {Records.Count:N0} record(s). Select the Master to keep, review its entry count, then use Merge + Update. Local/non-Tally Masters are editable; Tally-origin Masters remain protected.",
            _ => $"Active Ledger Masters filter active • {Records.Count:N0} record(s)."
        };
    }

    private void Import()
    {
        if (!CanImportMasters)
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "import Master data", accountingMutation: true);
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import Tally Masters",
            Filter = "Tally master files (*.xml;*.json)|*.xml;*.json|XML files (*.xml)|*.xml|JSON files (*.json)|*.json",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var company = string.IsNullOrWhiteSpace(client.LegalName)
                ? client.ClientName
                : client.LegalName;
            var result = _importService.Parse(
                dialog.FileName,
                client.Id,
                client.FinancialYear,
                company);
            var ledgerRecords = result.Records.Where(record => !StockTypes.Contains(record.Type)).ToArray();
            _repository.Upsert(client.Id, client.FinancialYear, ledgerRecords);
            if (_automationService is not null)
            {
                _automationService.PublishCommittedAccountingImport(
                    client.Id,
                    client.FinancialYear,
                    "Masters",
                    ledgerRecords.Length,
                    dialog.FileName,
                    "Manual Master Import");
            }
            else
            {
                _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, "Ledger Master import");
            }
            Refresh();

            StatusText =
                $"Imported {ledgerRecords.Length:N0} Ledger Master object(s) from {Path.GetFileName(dialog.FileName)}; " +
                $"inventory objects are intentionally handled by the separate Stock Master. " +
                $"Normalized duplicate groups currently detected: {DuplicateMasterCount:N0}.";
        }
        catch (Exception exception)
        {
            StatusText = $"Master import failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ImportPreviousFinancialYear()
    {
        if (!CanImportMasters)
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "import Previous FY Master data", accountingMutation: true);
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var confirmation = System.Windows.MessageBox.Show(
            $"Import reusable Masters and related Stock configuration from the immediate Previous FY into {client.ClientCode} · FY {client.FinancialYear}?\n\n" +
            "This carries the chart/custom Masters, Stock Items, Groups/Categories, Units, Godowns, Cost Centres/Categories, tax classifications, item aliases, unit mappings, quantity conversions and Stock Item Unit Profiles.\n\n" +
            "Previous-year Banking, Purchase, Sales, Journal, GST and Tally voucher transactions are NOT copied. Previous-year opening balances and live Tally MASTERID bindings are also not transplanted.",
            "Import Masters + Related Data from PY",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirmation != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var result = _previousFyImportService.Import(client);
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, "Previous FY Master carry-forward");
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.StockMasters, "Previous FY Stock Master carry-forward");
            Refresh();
            StatusText =
                $"Imported Previous FY structure from {result.SourceClient.ClientCode} · FY {result.SourceClient.FinancialYear}: " +
                $"{result.AccountingMasters:N0} accounting Masters, {result.StockItems:N0} Stock Items, {result.Units:N0} Units, " +
                $"{result.SimpleMasters:N0} Stock/Cost/Tax Masters, {result.ItemAliases:N0} item aliases, " +
                $"{result.UnitMappings:N0} unit mappings, {result.QuantityConversions:N0} quantity conversions and {result.UnitProfiles:N0} unit profiles. " +
                "Transactions were not copied; current-FY Tally identity will be re-verified by the normal live sync.";
            System.Windows.MessageBox.Show(StatusText, "Previous FY Masters Imported", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            StatusText = $"Previous FY Master import failed: {exception.Message}";
            System.Windows.MessageBox.Show(StatusText, "Previous FY Master Import", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CreateNewLedger()
    {
        if (!DemandPermission(GovernancePermissions.CreateMaster, "create a Ledger Master")) return;
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var groups = _allRecords
            .Where(record => !record.IsArchived &&
                             record.DuplicateCount <= 1 &&
                             record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                             !record.Name.Trim().Equals("Primary", StringComparison.OrdinalIgnoreCase))
            .GroupBy(record => record.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (groups.Length == 0)
        {
            StatusText = "No active accounting Group is available. Create a Group first, then create the Ledger.";
            System.Windows.MessageBox.Show(StatusText, "New Ledger", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var defaultGroup = SelectedTreeNode?.NodeKind.Equals("GroupContents", StringComparison.OrdinalIgnoreCase) == true
            ? SelectedTreeNode.FilterValue
            : SelectedRecord?.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) == true
                ? SelectedRecord.Name
                : groups.Any(group => group.Name.Equals("Sundry Creditors", StringComparison.OrdinalIgnoreCase))
                    ? "Sundry Creditors"
                    : groups[0].Name;

        var company = string.IsNullOrWhiteSpace(client.LegalName) ? client.ClientName : client.LegalName;
        var window = new NewLedgerMasterWindow(groups, company, defaultGroup)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (window.ShowDialog() != true) return;

        var duplicate = _repository.FindNormalizedDuplicate(
            client.Id, client.FinancialYear, "Ledger", window.LedgerName, ignoreArchivedTally: true);
        if (duplicate is not null)
        {
            HandleCreateDuplicate(duplicate, "Ledger", window.LedgerName);
            return;
        }
        var record = new MasterRecord
        {
            ClientId = client.Id,
            FinancialYear = client.FinancialYear,
            Company = company,
            Name = window.LedgerName,
            Parent = window.LedgerGroup,
            Type = "Ledger",
            Source = "Manual",
            OpeningBalance = window.OpeningBalance,
            IsUserEdited = true
        };

        try
        {
            _repository.Save(record);
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, "Manual Ledger created");
            Refresh();
            SelectedTreeNode = FindNode("Type", "Ledger") ?? SelectedTreeNode;
            ApplyRecordFilter();
            SelectedRecord = Records.FirstOrDefault(item =>
                item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(record.Name, StringComparison.OrdinalIgnoreCase));
            StatusText = $"Ledger '{record.Name}' created under '{record.Parent}'.";
        }
        catch (InvalidOperationException exception)
        {
            StatusText = exception.Message;
            System.Windows.MessageBox.Show(StatusText, "New Ledger", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void CreateNewGroup()
    {
        if (!DemandPermission(GovernancePermissions.CreateMaster, "create a Group Master")) return;
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var groups = _allRecords
            .Where(record => !record.IsArchived &&
                             record.DuplicateCount <= 1 &&
                             record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            .GroupBy(record => record.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var defaultParent = SelectedTreeNode?.NodeKind.Equals("GroupContents", StringComparison.OrdinalIgnoreCase) == true
            ? SelectedTreeNode.FilterValue
            : SelectedRecord?.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) == true
                ? SelectedRecord.Name
                : "Primary";

        var company = string.IsNullOrWhiteSpace(client.LegalName) ? client.ClientName : client.LegalName;
        var window = new NewGroupMasterWindow(groups, company, defaultParent)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (window.ShowDialog() != true) return;

        var duplicate = _repository.FindNormalizedDuplicate(client.Id, client.FinancialYear, "Group", window.GroupName);
        if (duplicate is not null)
        {
            HandleCreateDuplicate(duplicate, "Group", window.GroupName);
            return;
        }
        var record = new MasterRecord
        {
            ClientId = client.Id,
            FinancialYear = client.FinancialYear,
            Company = company,
            Name = window.GroupName,
            Parent = window.ParentGroup,
            Type = "Group",
            Source = "Manual",
            IsUserEdited = true
        };

        try
        {
            _repository.Save(record);
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, "Manual Group created");
            Refresh();
            SelectedTreeNode = FindNode("Type", "Group") ?? SelectedTreeNode;
            ApplyRecordFilter();
            SelectedRecord = Records.FirstOrDefault(item =>
                item.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(record.Name, StringComparison.OrdinalIgnoreCase));
            StatusText = $"Group '{record.Name}' created under '{record.Parent}'. It is now available in the New Ledger Group selector.";
        }
        catch (InvalidOperationException exception)
        {
            StatusText = exception.Message;
            System.Windows.MessageBox.Show(StatusText, "New Group", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private MasterTreeNode? FindNode(string nodeKind, string filterValue) =>
        MasterTree.SelectMany(Flatten)
            .FirstOrDefault(node =>
                node.NodeKind.Equals(nodeKind, StringComparison.OrdinalIgnoreCase) &&
                node.FilterValue.Equals(filterValue, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<MasterTreeNode> Flatten(MasterTreeNode node)
    {
        yield return node;
        foreach (var child in node.Children.SelectMany(Flatten))
        {
            yield return child;
        }
    }

    private bool CanEditSelectedManualMaster() =>
        CanEditMaster &&
        SelectedRecord is { PersistentId: > 0, IsArchived: false } record &&
        IsEditableLocalRecord(record) &&
        (record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ||
         record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase));

    private void EditManualMaster()
    {
        if (!DemandPermission(GovernancePermissions.CreateMaster, "edit a local Master")) return;
        var selected = SelectedRecord;
        var client = _clientContext.CurrentClient;
        if (selected is null || client is null) return;

        if (!CanEditSelectedManualMaster())
        {
            StatusText = selected.IsArchived
                ? $"'{selected.Name}' is archived. Restore it before editing."
                : $"'{selected.Name}' is sourced from '{selected.Source}'. Only local/non-Tally Ledger/Group Masters are editable in this phase; Tally-origin Master editing will be added separately.";
            System.Windows.MessageBox.Show(StatusText, "Edit Master", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var before = CloneMaster(selected);
        var edited = CloneMaster(selected);
        var groups = _allRecords
            .Where(record => !record.IsArchived &&
                             record.DuplicateCount <= 1 &&
                             record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            .GroupBy(record => record.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var company = string.IsNullOrWhiteSpace(client.LegalName) ? client.ClientName : client.LegalName;

        if (selected.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
        {
            var window = new NewLedgerMasterWindow(groups, company, selected.Parent, edited)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (window.ShowDialog() != true) return;
            edited.Name = window.LedgerName;
            edited.Parent = window.LedgerGroup;
            edited.OpeningBalance = window.OpeningBalance;
        }
        else
        {
            var window = new NewGroupMasterWindow(groups, company, selected.Parent, edited)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (window.ShowDialog() != true) return;
            edited.Name = window.GroupName;
            edited.Parent = window.ParentGroup;
        }

        edited.Company = company;
        edited.Source = selected.Source;
        edited.Type = selected.Type;
        edited.IsUserEdited = true;

        var duplicate = _repository.FindNormalizedDuplicate(
            edited.ClientId, edited.FinancialYear, edited.Type, edited.Name, edited.PersistentId);
        if (duplicate is not null)
        {
            StatusText =
                $"Update blocked: {edited.Type} '{edited.Name}' duplicates existing '{duplicate.Name}' from {duplicate.Source}" +
                (duplicate.IsArchived ? " (archived)." : ".") +
                " The original local Master was not changed or deleted.";
            System.Windows.MessageBox.Show(StatusText, "Duplicate Master", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            SelectMaster(duplicate.PersistentId, duplicate.Type);
            return;
        }
        try
        {
            var updateResult = _repository.UpdateLocalRecord(edited, before.Name);
            _governanceRepository.RecordVersion(new EntityVersionRecord
            {
                ClientId = edited.ClientId,
                FinancialYear = edited.FinancialYear,
                Module = "Masters",
                EntityType = $"Local{edited.Type.Replace(" ", string.Empty)}",
                EntityKey = edited.PersistentId.ToString(),
                Operation = "Update",
                BeforeJson = JsonSerializer.Serialize(before),
                AfterJson = JsonSerializer.Serialize(edited),
                ChangedBy = Environment.UserName,
                Reason = "Local Master edit/update. Tally-origin Master was not altered by this screen.",
                ChangedUtc = DateTime.UtcNow
            });

            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, $"Local {edited.Type} updated");
            var nameChanged = !before.Name.Equals(edited.Name, StringComparison.OrdinalIgnoreCase);
            Refresh();
            SelectedTreeNode = FindNode("Type", edited.Type) ?? SelectedTreeNode;
            ApplyRecordFilter();
            SelectedRecord = Records.FirstOrDefault(item => item.PersistentId == edited.PersistentId);
            StatusText =
                $"Local {edited.Type} '{before.Name}' updated" +
                (nameChanged ? $" to '{edited.Name}'" : string.Empty) +
                $". Local references updated: {updateResult.ReferencesUpdated:N0}; child Masters updated: {updateResult.ChildMastersUpdated:N0}. Tally was not altered.";
        }
        catch (InvalidOperationException exception)
        {
            StatusText = exception.Message + " The original local Master was retained unchanged.";
            System.Windows.MessageBox.Show(StatusText, "Edit Local Master", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            Refresh();
            SelectedTreeNode = FindNode("Type", before.Type) ?? SelectedTreeNode;
            ApplyRecordFilter();
            SelectedRecord = Records.FirstOrDefault(item => item.PersistentId == before.PersistentId);
        }
    }

    private bool CanMergeSelectedMaster()
    {
        if (!CanMergeMaster || IsBusy) return false;

        var selectedPair = _gridSelectedRecords
            .Where(record => record.PersistentId > 0 && !record.IsArchived)
            .GroupBy(record => record.PersistentId)
            .Select(group => group.First())
            .ToArray();
        if (selectedPair.Length == 2)
        {
            var first = selectedPair[0];
            var second = selectedPair[1];
            return IsMergeCandidate(first) &&
                   IsMergeCandidate(second) &&
                   first.Type.Equals(second.Type, StringComparison.OrdinalIgnoreCase);
        }
        if (selectedPair.Length > 2) return false;

        var selected = SelectedRecord;
        if (selected is null || selected.PersistentId <= 0 || selected.IsArchived ||
            (!selected.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
             !selected.Type.Equals("Group", StringComparison.OrdinalIgnoreCase)) ||
            MasterAuthority.IsSystemProtectedSource(selected.Source))
            return false;

        var sameType = _allRecords.Where(record =>
            record.PersistentId != selected.PersistentId &&
            IsMergeCandidate(record) &&
            record.Type.Equals(selected.Type, StringComparison.OrdinalIgnoreCase)).ToArray();
        return IsMergeCandidate(selected) && sameType.Length > 0;
    }

    private async Task MergeAndUpdateAsync()
    {
        if (!DemandPermission(GovernancePermissions.MasterMerge, "merge and update Masters")) return;
        var client = _clientContext.CurrentClient;
        var selected = SelectedRecord;
        if (client is null || selected is null || !CanMergeSelectedMaster()) return;
        var mergeViewState = CaptureViewState();

        var selectedPair = _gridSelectedRecords
            .Where(record => record.PersistentId > 0 && !record.IsArchived)
            .GroupBy(record => record.PersistentId)
            .Select(group => group.First())
            .ToArray();

        MasterRecord[] candidates;
        if (selectedPair.Length == 2)
        {
            if (!selectedPair[0].Type.Equals(selectedPair[1].Type, StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "Select two Masters of the same type (Ledger + Ledger or Group + Group).";
                return;
            }
            candidates = selectedPair;
        }
        else
        {
            candidates = _allRecords
                .Where(record => !record.IsArchived &&
                                 record.Type.Equals(selected.Type, StringComparison.OrdinalIgnoreCase) &&
                                 !MasterAuthority.IsSystemProtectedSource(record.Source))
                .OrderByDescending(record => record.PersistentId == selected.PersistentId)
                .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.PersistentId)
                .ToArray();
        }

        var sourceCandidates = candidates.Where(IsMergeCandidate).ToArray();
        var targetCandidates = candidates.Where(IsMergeCandidate).ToArray();
        if (sourceCandidates.Length < 2 || targetCandidates.Length < 2)
        {
            StatusText = "Merge + Update needs two active same-type Ledger/Group Masters.";
            return;
        }

        var preferredSourceId = selectedPair.Length == 2
            ? selectedPair.OrderBy(record => MergeSourcePreference(record)).ThenBy(record => record.UsageCount).ThenBy(record => record.PersistentId).First().PersistentId
            : selected.PersistentId;
        var dialog = new MasterMergeWindow(sourceCandidates, targetCandidates, preferredSourceId, selectedPair.Length == 2)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true || dialog.SourceMaster is not { } source || dialog.TargetMaster is not { } target) return;
        if (!IsMergeCandidate(source))
        {
            StatusText = $"Merge stopped because FROM '{source.Name}' is protected or no longer active.";
            return;
        }
        if (MasterAuthority.IsSystemProtectedSource(target.Source))
        {
            StatusText = $"Merge stopped because INTO '{target.Name}' is a protected system hierarchy Master.";
            return;
        }
        if (!source.Type.Equals(target.Type, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "Merge stopped because FROM and INTO are not the same Master type.";
            return;
        }
        if (source.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
        {
            var protectedChildren = _allRecords.Count(record =>
                !record.IsArchived && TallyMasterIdentity.Equivalent(record.Parent, source.Name) &&
                !IsEditableLocalRecord(record));
            if (protectedChildren > 0)
            {
                StatusText = $"Group merge stopped because '{source.Name}' still has {protectedChildren:N0} Tally/system-protected child Master(s).";
                System.Windows.MessageBox.Show(StatusText, "Merge + Update", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }
        }

        decimal? mergedOpeningBalance = target.OpeningBalance;
        if (target.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
        {
            var anyOpening = target.OpeningBalance.HasValue || source.OpeningBalance.HasValue;
            mergedOpeningBalance = anyOpening ? (target.OpeningBalance ?? 0m) + (source.OpeningBalance ?? 0m) : null;
        }
        var before = new[] { CloneMaster(source), CloneMaster(target) };
        IsBusy = true;
        MergeUpdateCommand.RaiseCanExecuteChanged();
        TallyMasterMergeUpdateResult? tallyUpdate = null;
        try
        {
            tallyUpdate = await _tallyCoordinator.PrepareMasterMergeTargetAsync(source, target, mergedOpeningBalance);
            var result = _repository.MergeLocalDuplicatePair(
                client.Id, client.FinancialYear, source.PersistentId, target.PersistentId,
                allowTallySourceArchive: tallyUpdate.AllowTallySourceArchive);
            _governanceRepository.RecordVersion(new EntityVersionRecord
            {
                ClientId = client.Id, FinancialYear = client.FinancialYear, Module = "Masters",
                EntityType = $"{target.Type.Replace(" ", string.Empty)}MergeUpdate", EntityKey = target.PersistentId.ToString(),
                Operation = "Merge", BeforeJson = JsonSerializer.Serialize(before),
                AfterJson = JsonSerializer.Serialize(new { SourceMasterId = source.PersistentId, SourceName = source.Name, CanonicalMasterId = target.PersistentId, CanonicalName = target.Name, result.ReferencesUpdated, result.ChildMastersUpdated, result.MastersArchived, result.SourceUsageCount, result.TargetUsageCount, result.MergedOpeningBalance, TallyUpdated = tallyUpdate.TallyUpdated, TallyTargetAlreadyExisted = tallyUpdate.TargetAlreadyExisted }),
                ChangedBy = Environment.UserName,
                Reason = tallyUpdate.Deferred
                    ? "User-directed Master Merge + Update. Local references were consolidated immediately and the durable FROM -> INTO Tally rule is applied unattended from the shared active-FY authority during normal synchronization."
                    : "User-directed Master Merge + Update. Local references and Ledger opening balance were merged; linked/open Tally Master handling completed.",
                ChangedUtc = DateTime.UtcNow
            });
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, $"{target.Type} Merge + Update completed");
            _tallyCoordinator.QueueCurrentClientMasterSynchronization("Master Merge + Update completed");
            Refresh();
            RestoreViewState(mergeViewState, target.PersistentId, target.Type);
            var openingText = target.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)
                ? $" Merged opening balance: {(result.MergedOpeningBalance ?? 0m):N2} (+Dr / -Cr)."
                : string.Empty;
            StatusText = $"Merged FROM '{source.Name}' INTO '{target.Name}'. References updated: {result.ReferencesUpdated:N0}; child Masters updated: {result.ChildMastersUpdated:N0}; source archived: {result.MastersArchived:N0}." + openingText + " " + tallyUpdate.Status;
        }
        catch (InvalidOperationException exception)
        {
            if (tallyUpdate is { Deferred: true, PendingMergeId: > 0 })
                _repository.DeletePendingTallyMasterMerge(tallyUpdate.PendingMergeId);
            StatusText = exception.Message + (tallyUpdate is { TallyUpdated: true }
                ? " Tally INTO was already updated; local merge did not complete. Refresh Masters/Tally before retrying."
                : " No local Master was intentionally merged after this failure.");
            System.Windows.MessageBox.Show(StatusText, "Merge + Update", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            Refresh();
        }
        catch (Exception exception)
        {
            // Fix43 hardening: a deferred Tally operation must never survive a local
            // merge failure, otherwise it could mutate Tally without the local merge.
            if (tallyUpdate is { Deferred: true, PendingMergeId: > 0 })
                _repository.DeletePendingTallyMasterMerge(tallyUpdate.PendingMergeId);
            StatusText = $"Merge + Update failed: {exception.Message}. No deferred Tally merge was retained after the local failure.";
            System.Windows.MessageBox.Show(StatusText, "Merge + Update", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            Refresh();
        }
        finally
        {
            IsBusy = false;
            MergeUpdateCommand.RaiseCanExecuteChanged();
        }
    }

    private void HandleCreateDuplicate(MasterRecord duplicate, string type, string requestedName)
    {
        if (duplicate.IsArchived && IsEditableLocalRecord(duplicate))
        {
            var restore = System.Windows.MessageBox.Show(
                $"A local {type} already exists as '{duplicate.Name}', but it is archived.\n\nRestore that existing Master instead of creating a duplicate?",
                $"Existing Archived {type}",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question,
                System.Windows.MessageBoxResult.Yes);
            if (restore == System.Windows.MessageBoxResult.Yes)
            {
                _repository.Archive(duplicate.PersistentId, archived: false);
                var client = _clientContext.CurrentClient;
                if (client is not null)
                    _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, $"Local {type} restored instead of duplicate create");
                SelectMaster(duplicate.PersistentId, type);
                StatusText = $"Restored existing local {type} '{duplicate.Name}'. No duplicate '{requestedName}' was created.";
                return;
            }
        }

        StatusText =
            $"{type} '{requestedName}' duplicates existing '{duplicate.Name}' from {duplicate.Source}" +
            (duplicate.IsArchived ? " (archived)." : ".") +
            " No duplicate Master was created. Use the existing Master, restore it, edit the local Master, or use Merge + Update for active local duplicates.";
        System.Windows.MessageBox.Show(StatusText, $"Duplicate {type}", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        SelectMaster(duplicate.PersistentId, type);
    }


    private void SelectMaster(long persistentId, string type)
    {
        if (_showDuplicatesOnly)
        {
            _showDuplicatesOnly = false;
            OnPropertyChanged(nameof(ShowDuplicatesOnly));
        }
        var includeArchivedChanged = false;
        if (!_includeArchived && _repository.Get(CurrentClientId, CurrentFinancialYear, true, string.Empty)
                .Any(item => item.PersistentId == persistentId && item.IsArchived))
        {
            _includeArchived = true;
            OnPropertyChanged(nameof(IncludeArchived));
            includeArchivedChanged = true;
        }
        Refresh();
        SelectedTreeNode = FindNode("Type", type) ?? SelectedTreeNode;
        ApplyRecordFilter();
        SelectedRecord = Records.FirstOrDefault(item => item.PersistentId == persistentId);
        if (includeArchivedChanged)
            StatusText = $"Include archived was enabled to show the existing {type}.";
    }

    private void ApplyDuplicateReviewState()
    {
        foreach (var record in _allRecords)
        {
            record.DuplicateCount = 1;
            record.DuplicateStatus = record.IsArchived ? "Archived" : "Unique";
        }

        foreach (var group in _allRecords.GroupBy(MasterDuplicateIdentity, StringComparer.OrdinalIgnoreCase))
        {
            var activeItems = group.Where(item => !item.IsArchived).ToArray();
            if (activeItems.Length > 1)
            {
                foreach (var item in activeItems)
                {
                    item.DuplicateCount = activeItems.Length;
                    var counterpartSource = string.Join(", ", activeItems
                        .Where(other => other.PersistentId != item.PersistentId)
                        .Select(other => other.Source.Trim())
                        .Where(source => source.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                    item.DuplicateStatus = counterpartSource.Length > 0
                        ? $"Duplicate ({activeItems.Length}) • {counterpartSource}"
                        : $"Duplicate ({activeItems.Length})";
                }
            }

            var activeCanonical = activeItems.OrderByDescending(item => item.IsUserEdited)
                .ThenByDescending(item => item.UsageCount)
                .ThenBy(item => item.PersistentId)
                .FirstOrDefault();
            foreach (var archived in group.Where(item => item.IsArchived))
            {
                archived.DuplicateStatus = activeCanonical is null
                    ? "Archived"
                    : $"Archived history • active: {activeCanonical.Name}";
            }
        }

        foreach (var nameGroup in _allRecords
                     .Where(record => !record.IsArchived &&
                                      (record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ||
                                       record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase)))
                     .GroupBy(record => TallyMasterIdentity.Key(record.Name), StringComparer.OrdinalIgnoreCase))
        {
            var items = nameGroup.ToArray();
            if (!items.Any(record => record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)) ||
                !items.Any(record => record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var item in items)
            {
                var otherTypes = string.Join(", ", items
                    .Where(other => !other.Type.Equals(item.Type, StringComparison.OrdinalIgnoreCase))
                    .Select(other => other.Type)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                item.DuplicateStatus = item.DuplicateCount > 1
                    ? item.DuplicateStatus + $" • Name also used by {otherTypes}"
                    : $"Name also used by {otherTypes} • allowed cross-type";
            }
        }
    }

    private static MasterRecord CloneMaster(MasterRecord source) =>
        new()
        {
            PersistentId = source.PersistentId,
            ClientId = source.ClientId,
            FinancialYear = source.FinancialYear,
            Name = source.Name,
            Parent = source.Parent,
            Type = source.Type,
            Company = source.Company,
            Source = source.Source,
            OpeningBalance = source.OpeningBalance,
            IsArchived = source.IsArchived,
            IsUserEdited = source.IsUserEdited,
            CreatedUtc = source.CreatedUtc,
            UpdatedUtc = source.UpdatedUtc,
            DuplicateCount = source.DuplicateCount,
            DuplicateStatus = source.DuplicateStatus,
            UsageCount = source.UsageCount
        };

    private static string MasterDuplicateIdentity(MasterRecord record)
    {
        var typeKey = AccountingText.DuplicateKey(record.Type);
        var nameKey = typeKey.Equals("LEDGER", StringComparison.OrdinalIgnoreCase) ||
                      typeKey.Equals("GROUP", StringComparison.OrdinalIgnoreCase)
            ? TallyMasterIdentity.Key(record.Name)
            : AccountingText.DuplicateKey(record.Name);
        return $"{typeKey}\u001f{nameKey}";
    }

    private static bool IsEditableLocalRecord(MasterRecord record) =>
        MasterAuthority.IsLocalEditableSource(record.Source);

    private static bool IsMergeCandidate(MasterRecord record) =>
        record.PersistentId > 0 &&
        !record.IsArchived &&
        (record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ||
         record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase)) &&
        !MasterAuthority.IsSystemProtectedSource(record.Source);

    private static int MergeSourcePreference(MasterRecord record)
    {
        // Local/manual is the cheapest/safest FROM. A zero-use Tally Master is the
        // next best default because it can be live-verified/deleted without voucher migration.
        if (IsEditableLocalRecord(record)) return 0;
        if (MasterAuthority.IsTallySource(record.Source) && record.UsageCount == 0) return 1;
        return 2;
    }

    private void Delete()
    {
        if (!DemandPermission(GovernancePermissions.DeleteAccounting, "delete a local Master")) return;
        var record = SelectedRecord;
        if (record is null || record.PersistentId <= 0) return;

        if (!IsEditableLocalRecord(record))
        {
            StatusText = $"'{record.Name}' cannot be deleted because its source is '{record.Source}'. Only local/non-Tally Masters can be deleted in this phase; Tally-origin Masters remain protected.";
            System.Windows.MessageBox.Show(
                StatusText + " Tally-origin Master edit/archive/delete will be added in the later Tally-authority phase.",
                "Delete Master",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        if (record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
        {
            var dependents = _repository.Get(record.ClientId, record.FinancialYear, true, string.Empty)
                .Where(item => item.PersistentId != record.PersistentId &&
                               TallyMasterIdentity.Equivalent(item.Parent, record.Name))
                .ToArray();
            if (dependents.Length > 0)
            {
                var ledgerCount = dependents.Count(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase));
                var groupCount = dependents.Count(item => item.Type.Equals("Group", StringComparison.OrdinalIgnoreCase));
                var archivedCount = dependents.Count(item => item.IsArchived);
                StatusText = $"Group '{record.Name}' was not deleted because {dependents.Length:N0} Master(s) still use it ({groupCount:N0} child Group(s), {ledgerCount:N0} Ledger(s), {archivedCount:N0} archived). Move or delete those Masters first.";
                System.Windows.MessageBox.Show(StatusText, "Delete Group", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
        }

        var confirmation = System.Windows.MessageBox.Show(
            $"Delete the local {record.Type} '{record.Name}' permanently from this client/FY?\n\nThis does not delete or modify any Master in Tally.",
            $"Delete {record.Type}",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirmation != System.Windows.MessageBoxResult.Yes) return;

        if (!_repository.DeleteLocalRecord(record.PersistentId))
        {
            StatusText = $"'{record.Name}' was not deleted. The row is no longer a deletable local Master or no longer exists.";
            return;
        }

        var deletedName = record.Name;
        var deletedType = record.Type;
        var client = _clientContext.CurrentClient;
        if (client is not null)
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, $"Local {deletedType} deleted");
        Refresh();
        StatusText = $"Local {deletedType} '{deletedName}' deleted. Tally-origin Masters were not changed.";
    }

    private bool DemandPermission(string permission, string action)
    {
        if (DesktopPermissionService.CanMutateAccounting(_governanceRepository, permission)) return true;
        StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, action, accountingMutation: true);
        System.Windows.MessageBox.Show(StatusText, "Permissions", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        return false;
    }

    private void MakeArchivedMasterActive()
    {
        var record = SelectedRecord;
        if (record is null || record.PersistentId <= 0 || !record.IsArchived ||
            (!record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
             !record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase)))
            return;

        if (!DemandPermission(GovernancePermissions.DeleteAccounting, "make an archived Master active")) return;

        var client = _clientContext.CurrentClient;
        var canRestoreMerged = _repository.CanRestoreMergedArchive(record.ClientId, record.FinancialYear, record.PersistentId);
        if (canRestoreMerged)
        {
            var restored = _repository.RestoreMergedArchive(
                record.ClientId, record.FinancialYear, record.PersistentId,
                "User made the archived Master active for a new manual Merge + Update.");
            if (!restored.Restored)
            {
                StatusText = restored.Status;
                System.Windows.MessageBox.Show(StatusText, "Make Master Active", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                Refresh();
                return;
            }

            if (client is not null)
            {
                _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, "Archived Master made active");
                _tallyCoordinator.QueueCurrentClientMasterSynchronization("Archived Master made active");
            }
            Refresh();
            SelectMaster(record.PersistentId, record.Type);
            StatusText = restored.Status + " The Master is active again and can be selected for Merge + Update.";
            return;
        }

        var hasUnresolvedMerge = _repository.HasUnresolvedPendingTallyMasterMerge(
            record.ClientId, record.FinancialYear, record.PersistentId);
        if (hasUnresolvedMerge)
        {
            var answer = System.Windows.MessageBox.Show(
                $"'{record.Name}' is archived by an older unresolved Merge + Update.\n\n" +
                "Making it active will cancel that old pending merge/alias in AAH so it is not automatically archived again. " +
                "It will NOT undo references that were already moved and it will NOT change Tally.\n\n" +
                "Continue so you can select this Master for a new Merge + Update?",
                "Make Archived Master Active",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);
            if (answer != System.Windows.MessageBoxResult.Yes) return;
        }

        var activated = _repository.MakeArchivedLedgerOrGroupActiveForManualMerge(
            record.ClientId, record.FinancialYear, record.PersistentId, hasUnresolvedMerge,
            "User cancelled the older pending merge and made this archived Master active for a new Merge + Update.");
        if (!activated)
        {
            StatusText = $"'{record.Name}' could not be made active because its archive state changed or it no longer exists.";
            Refresh();
            return;
        }

        if (client is not null)
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, "Archived Master made active");

        Refresh();
        SelectMaster(record.PersistentId, record.Type);
        StatusText = $"{record.Type} '{record.Name}' is active again in AAH and can now be selected for Merge + Update. Tally was not changed.";
    }

    private void Archive()
    {
        var record = SelectedRecord;
        if (record is null || record.PersistentId <= 0) return;
        var client = _clientContext.CurrentClient;
        var canRestoreMerged = record.IsArchived &&
                               _repository.CanRestoreMergedArchive(record.ClientId, record.FinancialYear, record.PersistentId);

        // Fix46: an archived FROM Master created by a deferred Merge + Update is a
        // special rollback case. It may be Tally-backed, so the ordinary local-only
        // archive rule must not hide the only safe Restore path from the operator.
        if (canRestoreMerged)
        {
            if (!DemandPermission(GovernancePermissions.DeleteAccounting, "restore a merged Master")) return;
            var before = CloneMaster(record);
            var restored = _repository.RestoreMergedArchive(
                record.ClientId, record.FinancialYear, record.PersistentId,
                "User restored the deferred merged Master from Master Centre.");
            if (!restored.Restored)
            {
                StatusText = restored.Status;
                System.Windows.MessageBox.Show(StatusText, "Restore Merged Master", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                Refresh();
                return;
            }

            _governanceRepository.RecordVersion(new EntityVersionRecord
            {
                ClientId = record.ClientId, FinancialYear = record.FinancialYear, Module = "Masters",
                EntityType = $"{record.Type.Replace(" ", string.Empty)}MergeRestore", EntityKey = record.PersistentId.ToString(),
                Operation = "Restore", BeforeJson = JsonSerializer.Serialize(before),
                AfterJson = JsonSerializer.Serialize(restored), ChangedBy = Environment.UserName,
                Reason = "Deferred Master merge was rolled back locally because the source must remain available in Tally/local authority.",
                ChangedUtc = DateTime.UtcNow
            });
            if (client is not null)
            {
                _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, "Merged Master restored");
                _tallyCoordinator.QueueCurrentClientMasterSynchronization("Merged Master restored");
            }
            Refresh();
            SelectMaster(record.PersistentId, record.Type);
            StatusText = restored.Status + " The deferred Tally merge was closed and will not retry.";
            return;
        }

        if (!IsEditableLocalRecord(record))
        {
            StatusText = $"'{record.Name}' is sourced from '{record.Source}'. Active Tally-origin Masters remain read-only; only an archived FROM Master with an unresolved deferred merge can be restored here.";
            System.Windows.MessageBox.Show(StatusText, "Archive Master", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        if (!DemandPermission(GovernancePermissions.DeleteAccounting, record.IsArchived ? "restore a local Master" : "archive a local Master")) return;

        if (!record.IsArchived)
        {
            var protection = _repository.GetArchiveProtection(record.PersistentId);
            if (protection.IsProtected)
            {
                StatusText = $"{record.Type} '{record.Name}' cannot be archived because it still has {protection.Reason}. Remove/transfer that accounting usage first.";
                System.Windows.MessageBox.Show(StatusText, "Archive Master", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                Refresh();
                SelectMaster(record.PersistentId, record.Type);
                return;
            }
        }

        if (!_repository.TryArchive(record.PersistentId, !record.IsArchived))
        {
            var protection = _repository.GetArchiveProtection(record.PersistentId);
            StatusText = protection.IsProtected
                ? $"{record.Type} '{record.Name}' cannot be archived because it still has {protection.Reason}."
                : $"{record.Type} '{record.Name}' archive state could not be changed. Refresh and try again.";
            System.Windows.MessageBox.Show(StatusText, "Archive Master", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            Refresh();
            return;
        }
        if (client is not null)
            _liveChangeHub.Publish(client.Id, client.FinancialYear, AccountingLiveArea.Masters, "Local Master archive/restore");
        StatusText = record.IsArchived
            ? $"Local {record.Type} '{record.Name}' restored. Tally-origin Masters were not changed."
            : $"Local {record.Type} '{record.Name}' archived. Tally-origin Masters were not changed.";
        Refresh();
    }

    private async Task SynchronizeAsync()
    {
        if (!CanImportMasters)
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, "synchronize Masters", accountingMutation: true);
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Force synchronizing live Tally Master + Voucher authority now…";
            await _tallyCoordinator.RefreshNowAsync(forceLinkedSynchronization: true);
            Refresh();
            StatusText = $"Force Sync completed from live Tally authority for {client.ClientName} · FY {client.FinancialYear}.";
        }
        catch (Exception exception)
        {
            StatusText = $"Master synchronization failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnLiveDataChanged(object? sender, AccountingLiveChange change)
    {
        if (change.Area != AccountingLiveArea.Masters ||
            change.ClientId != CurrentClientId ||
            !change.FinancialYear.Equals(CurrentFinancialYear, StringComparison.OrdinalIgnoreCase)) return;
        // The shared cache worker owns the authoritative read. MainWindow applies
        // ModuleReady only to the current page; never scan SQLite on a live UI event.
    }

    private void OnClientContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveClientContext.CurrentClient))
        {
            _preparedClientId = 0;
            _preparedFinancialYear = string.Empty;
            _allRecords.Clear();
            Records.Clear();
            MasterTree.Clear();
            _gridSelectedRecords.Clear();
            SelectedRecord = null;
            NotifyCounts();
            StatusText = _clientContext.CurrentClient is null
                ? "Open a client to use Ledger Master."
                : "Ledger Master is preparing in the background.";
            OnPropertyChanged(nameof(CanImportMasters));
            OnPropertyChanged(nameof(CanCreateMaster));
            OnPropertyChanged(nameof(CanEditMaster));
            OnPropertyChanged(nameof(CanMergeMaster));
            OnPropertyChanged(nameof(CanDeleteMaster));
            OnPropertyChanged(nameof(IsMasterReadOnly));
            OnPropertyChanged(nameof(MasterPermissionSummary));
            ImportPreviousFyCommand.RaiseCanExecuteChanged();
            NewLedgerCommand.RaiseCanExecuteChanged();
            NewGroupCommand.RaiseCanExecuteChanged();
            EditManualMasterCommand.RaiseCanExecuteChanged();
            MergeUpdateCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            SynchronizeCommand.RaiseCanExecuteChanged();
        }
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(GroupCount));
        OnPropertyChanged(nameof(LedgerCount));
        OnPropertyChanged(nameof(DuplicateMasterCount));
    }
}
