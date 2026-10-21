using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Finance;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Infrastructure.Finance;
using AccountingAutomationHub.Infrastructure.Journals;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class FixedAssetsViewModel : PageViewModel
{
    private readonly ActiveClientContext _clientContext;
    private readonly SqliteFinanceControlRepository _repository;
    private readonly FixedAssetDiscoveryService _discoveryService;
    private readonly AccountingLiveChangeHub _liveChangeHub;
    private readonly SqliteJournalRepository _journalRepository = new();
    private readonly Dictionary<string, FixedAssetLedgerSnapshot> _ledgerSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FixedAssetLedgerOption> _ledgerOptions = new(StringComparer.OrdinalIgnoreCase);

    private FixedAssetRecord _editingAsset = new();
    private FixedAssetRecord? _selectedAsset;
    private FixedAssetDiscoveryCandidate? _selectedDiscoveryCandidate;
    private FixedAssetPostingMappingRow? _selectedPostingMapping;
    private FixedAssetDepreciationSettingsRecord _settings = new();
    private string _statusText = "Open a client to manage Fixed Assets.";
    private string _discoveryStatusText = "Open a client to inspect cached Tally Fixed Asset authority.";
    private string _settingsStatusText = string.Empty;
    private string _depreciationDetailText = string.Empty;
    private bool _discoveryBusy;
    private bool _isEditorOpen;
    private bool _isDepreciationDetailOpen;
    private bool _loadingSettings;
    private int _discoveryGeneration;
    private int _fixedAssetLedgerCount;
    private int _completeAuthorityCount;
    private decimal _editingPreviewDepreciation;
    private decimal _editingPreviewClosing;
    private string _newExpenseLedgerName = string.Empty;
    private string _newAccumulatedLedgerName = string.Empty;

    public FixedAssetsViewModel(
        ActiveClientContext clientContext,
        SqliteFinanceControlRepository repository,
        FixedAssetDiscoveryService discoveryService,
        AccountingLiveChangeHub liveChangeHub)
        : base("Fixed Assets", "Fixed asset register, live depreciation and Tally-backed movement authority")
    {
        _clientContext = clientContext;
        _repository = repository;
        _discoveryService = discoveryService;
        _liveChangeHub = liveChangeHub;
        _repository.Initialize();
        _journalRepository.Initialize();
        _liveChangeHub.Changed += OnLiveAccountingChanged;

        RefreshCommand = new RelayCommand(Refresh, HasClient);
        NewAssetCommand = new RelayCommand(NewAsset, HasClient);
        EditAssetCommand = new RelayCommand(EditSelectedAsset, () => SelectedAsset is not null);
        SaveAssetCommand = new RelayCommand(SaveAsset, HasClient);
        DeleteAssetCommand = new RelayCommand(DeleteAsset, () => HasClient() && SelectedAsset is { Id: > 0 });
        CloseEditorCommand = new RelayCommand(CloseEditor);
        CloseDepreciationDetailsCommand = new RelayCommand(() => IsDepreciationDetailOpen = false);
        SaveDepreciationSettingsCommand = new RelayCommand(SaveDepreciationSettings, HasClient);
        GenerateVoucherCommand = new RelayCommand(GenerateDepreciationVoucher, () => HasClient() && FixedAssets.Any(row => row.CurrentDepreciation - row.PostedDepreciation > 0.005m));
        CreateExpenseLedgerCommand = new RelayCommand(CreateExpenseLedger, HasClient);
        CreateAccumulatedLedgerCommand = new RelayCommand(CreateAccumulatedLedger, () => HasClient() && SelectedPostingMapping is not null);

        DepreciationMethods = new ObservableCollection<string>(new[] { "SLM", "WDV", "Other" });
        DepreciationPolicies = new ObservableCollection<string>(new[] { "Fixed/Common", "Per Fixed Asset" });
        CalculationBases = new ObservableCollection<string>(new[] { "Yearly", "Day-wise", "Income Tax 180-Day" });
        PostingMethods = new ObservableCollection<string>(new[] { "Direct to Fixed Asset", "Accumulated Depreciation" });
        OpeningSources = new ObservableCollection<string>(new[] { "Tally", "Previous AAH FY", "Imported Register", "Financial Statements", "Manual", "Other" });
    }

    // FIXED30/FIXED38 compatibility: the existing writable register remains FixedAssets.
    public ObservableCollection<FixedAssetRecord> FixedAssets { get; } = [];
    public ObservableCollection<FixedAssetDiscoveryCandidate> DiscoveryCandidates { get; } = [];
    public ObservableCollection<string> DepreciationMethods { get; }
    public ObservableCollection<string> DepreciationPolicies { get; }
    public ObservableCollection<string> CalculationBases { get; }
    public ObservableCollection<string> PostingMethods { get; }
    public ObservableCollection<string> OpeningSources { get; }
    public ObservableCollection<string> AvailableLedgerNames { get; } = [];
    public ObservableCollection<string> ExpenseLedgerNames { get; } = [];
    public ObservableCollection<FixedAssetPostingMappingRow> PostingMappings { get; } = [];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand NewAssetCommand { get; }
    public RelayCommand EditAssetCommand { get; }
    public RelayCommand SaveAssetCommand { get; }
    public RelayCommand DeleteAssetCommand { get; }
    public RelayCommand CloseEditorCommand { get; }
    public RelayCommand CloseDepreciationDetailsCommand { get; }
    public RelayCommand SaveDepreciationSettingsCommand { get; }
    public RelayCommand GenerateVoucherCommand { get; }
    public RelayCommand CreateExpenseLedgerCommand { get; }
    public RelayCommand CreateAccumulatedLedgerCommand { get; }

    public FixedAssetRecord EditingAsset
    {
        get => _editingAsset;
        set
        {
            if (ReferenceEquals(_editingAsset, value)) return;
            if (_editingAsset is not null) _editingAsset.PropertyChanged -= EditingAsset_PropertyChanged;
            _editingAsset = value ?? new FixedAssetRecord();
            _editingAsset.PropertyChanged += EditingAsset_PropertyChanged;
            OnPropertyChanged();
            RecalculateEditingPreview();
        }
    }

    public FixedAssetRecord? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (!SetProperty(ref _selectedAsset, value)) return;
            EditAssetCommand.RaiseCanExecuteChanged();
            DeleteAssetCommand.RaiseCanExecuteChanged();
        }
    }

    public FixedAssetDiscoveryCandidate? SelectedDiscoveryCandidate
    {
        get => _selectedDiscoveryCandidate;
        set => SetProperty(ref _selectedDiscoveryCandidate, value);
    }

    public FixedAssetPostingMappingRow? SelectedPostingMapping
    {
        get => _selectedPostingMapping;
        set
        {
            if (!SetProperty(ref _selectedPostingMapping, value)) return;
            CreateAccumulatedLedgerCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DiscoveryStatusText
    {
        get => _discoveryStatusText;
        private set => SetProperty(ref _discoveryStatusText, value);
    }

    public string SettingsStatusText
    {
        get => _settingsStatusText;
        private set => SetProperty(ref _settingsStatusText, value);
    }

    public string DepreciationDetailText
    {
        get => _depreciationDetailText;
        private set => SetProperty(ref _depreciationDetailText, value);
    }

    public bool DiscoveryBusy
    {
        get => _discoveryBusy;
        private set => SetProperty(ref _discoveryBusy, value);
    }

    public bool IsEditorOpen
    {
        get => _isEditorOpen;
        private set
        {
            if (!SetProperty(ref _isEditorOpen, value)) return;
            if (value) IsDepreciationDetailOpen = false;
        }
    }

    public bool IsDepreciationDetailOpen
    {
        get => _isDepreciationDetailOpen;
        private set
        {
            if (!SetProperty(ref _isDepreciationDetailOpen, value)) return;
            if (value) IsEditorOpen = false;
        }
    }

    public decimal EditingPreviewDepreciation
    {
        get => _editingPreviewDepreciation;
        private set => SetProperty(ref _editingPreviewDepreciation, value);
    }

    public decimal EditingPreviewClosing
    {
        get => _editingPreviewClosing;
        private set => SetProperty(ref _editingPreviewClosing, value);
    }

    public string NewExpenseLedgerName
    {
        get => _newExpenseLedgerName;
        set => SetProperty(ref _newExpenseLedgerName, value);
    }

    public string NewAccumulatedLedgerName
    {
        get => _newAccumulatedLedgerName;
        set => SetProperty(ref _newAccumulatedLedgerName, value);
    }

    public string DepreciationPolicy
    {
        get => _settings.DepreciationPolicy;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Fixed/Common" : value.Trim();
            if (_settings.DepreciationPolicy.Equals(normalized, StringComparison.Ordinal)) return;
            _settings.DepreciationPolicy = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFixedCommonPolicy));
            OnPropertyChanged(nameof(IsPerAssetPolicy));
            RecalculateAndPersistSettings();
        }
    }

    public bool IsFixedCommonPolicy
    {
        get => DepreciationPolicy.Equals("Fixed/Common", StringComparison.OrdinalIgnoreCase);
        set { if (value) DepreciationPolicy = "Fixed/Common"; }
    }
    public bool IsPerAssetPolicy
    {
        get => !IsFixedCommonPolicy;
        set { if (value) DepreciationPolicy = "Per Fixed Asset"; }
    }

    public string DepreciationMethod
    {
        get => _settings.DepreciationMethod;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "SLM" : value.Trim();
            if (_settings.DepreciationMethod.Equals(normalized, StringComparison.Ordinal)) return;
            _settings.DepreciationMethod = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMethodSlm));
            OnPropertyChanged(nameof(IsMethodWdv));
            OnPropertyChanged(nameof(IsMethodOther));
            RecalculateAndPersistSettings();
        }
    }

    public bool IsMethodSlm { get => DepreciationMethod.Equals("SLM", StringComparison.OrdinalIgnoreCase); set { if (value) DepreciationMethod = "SLM"; } }
    public bool IsMethodWdv { get => DepreciationMethod.Equals("WDV", StringComparison.OrdinalIgnoreCase); set { if (value) DepreciationMethod = "WDV"; } }
    public bool IsMethodOther { get => DepreciationMethod.Equals("Other", StringComparison.OrdinalIgnoreCase); set { if (value) DepreciationMethod = "Other"; } }

    public decimal CommonDepreciationRate
    {
        get => _settings.CommonDepreciationRate;
        set
        {
            var normalized = Math.Max(0m, value);
            if (_settings.CommonDepreciationRate == normalized) return;
            _settings.CommonDepreciationRate = normalized;
            OnPropertyChanged();
            RecalculateAndPersistSettings();
        }
    }

    public string DepreciationCalculation
    {
        get => string.IsNullOrWhiteSpace(_settings.ExistingAssetCalculation) ? "Yearly" : _settings.ExistingAssetCalculation;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Yearly" : value.Trim();
            if (_settings.ExistingAssetCalculation.Equals(normalized, StringComparison.Ordinal) &&
                _settings.AdditionCalculation.Equals(normalized, StringComparison.Ordinal)) return;
            _settings.ExistingAssetCalculation = normalized;
            _settings.AdditionCalculation = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExistingAssetCalculation));
            OnPropertyChanged(nameof(AdditionCalculation));
            OnPropertyChanged(nameof(IsCalculationYearly));
            OnPropertyChanged(nameof(IsCalculationDayWise));
            OnPropertyChanged(nameof(IsCalculationIncomeTax180));
            RecalculateAndPersistSettings();
        }
    }

    // Compatibility aliases: both now always follow the single selected calculation rule.
    public string ExistingAssetCalculation
    {
        get => DepreciationCalculation;
        set => DepreciationCalculation = value;
    }

    public string AdditionCalculation
    {
        get => DepreciationCalculation;
        set => DepreciationCalculation = value;
    }

    public bool IsCalculationYearly { get => DepreciationCalculation.Equals("Yearly", StringComparison.OrdinalIgnoreCase); set { if (value) DepreciationCalculation = "Yearly"; } }
    public bool IsCalculationDayWise { get => DepreciationCalculation.Equals("Day-wise", StringComparison.OrdinalIgnoreCase); set { if (value) DepreciationCalculation = "Day-wise"; } }
    public bool IsCalculationIncomeTax180 { get => DepreciationCalculation.Equals("Income Tax 180-Day", StringComparison.OrdinalIgnoreCase); set { if (value) DepreciationCalculation = "Income Tax 180-Day"; } }

    public bool AdditionalDepreciationEnabled
    {
        get => _settings.AdditionalDepreciationEnabled;
        set
        {
            if (_settings.AdditionalDepreciationEnabled == value) return;
            _settings.AdditionalDepreciationEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAdditionalDepreciationEnabled));
            RecalculateAndPersistSettings();
        }
    }

    public bool IsAdditionalDepreciationEnabled => AdditionalDepreciationEnabled;

    public decimal AdditionalDepreciationRate
    {
        get => _settings.AdditionalDepreciationRate;
        set
        {
            var normalized = Math.Max(0m, value);
            if (_settings.AdditionalDepreciationRate == normalized) return;
            _settings.AdditionalDepreciationRate = normalized;
            OnPropertyChanged();
            RecalculateAndPersistSettings();
        }
    }

    public string DepreciationPosting
    {
        get => _settings.DepreciationPosting;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Direct to Fixed Asset" : value.Trim();
            if (_settings.DepreciationPosting.Equals(normalized, StringComparison.Ordinal)) return;
            _settings.DepreciationPosting = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAccumulatedDepreciationPosting));
            OnPropertyChanged(nameof(IsDirectDepreciationPosting));
            RefreshAccumulatedOpeningAuthority();
            RecalculateAndPersistSettings();
        }
    }

    public bool IsAccumulatedDepreciationPosting
    {
        get => DepreciationPosting.Equals("Accumulated Depreciation", StringComparison.OrdinalIgnoreCase);
        set { if (value) DepreciationPosting = "Accumulated Depreciation"; }
    }
    public bool IsDirectDepreciationPosting
    {
        get => !IsAccumulatedDepreciationPosting;
        set { if (value) DepreciationPosting = "Direct to Fixed Asset"; }
    }

    public bool VoucherRoundOffEnabled
    {
        get => !string.IsNullOrWhiteSpace(_settings.VoucherRoundOffMode) &&
               !_settings.VoucherRoundOffMode.Equals("No Round Off", StringComparison.OrdinalIgnoreCase) &&
               !_settings.VoucherRoundOffMode.Equals("Off", StringComparison.OrdinalIgnoreCase);
        set
        {
            var normalized = value ? "Nearest ₹1" : "No Round Off";
            if (_settings.VoucherRoundOffMode.Equals(normalized, StringComparison.Ordinal)) return;
            _settings.VoucherRoundOffMode = normalized;
            OnPropertyChanged();
            RecalculateAndPersistSettings(recalculate: false);
        }
    }

    public string ExpenseLedgerName
    {
        get => _settings.ExpenseLedgerName;
        set
        {
            var normalized = value ?? string.Empty;
            if (_settings.ExpenseLedgerName.Equals(normalized, StringComparison.Ordinal)) return;
            _settings.ExpenseLedgerName = normalized;
            _settings.ExpenseLedgerMasterId = ResolveLedgerMasterId(normalized);
            OnPropertyChanged();
            RecalculateAndPersistSettings(recalculate: false);
        }
    }

    public int AssetCount => FixedAssets.Count;
    public decimal TotalOpeningBalance => FixedAssets.Sum(asset => asset.OpeningBalance);
    public decimal TotalAdditions => FixedAssets.Sum(asset => asset.CurrentAdditions);
    public decimal TotalDeletions => FixedAssets.Sum(asset => asset.CurrentDeletions);
    public decimal TotalDepreciation => FixedAssets.Sum(asset => asset.CurrentDepreciation);
    public decimal TotalBookValue => FixedAssets.Sum(asset => asset.CurrentBookValue);
    public decimal TotalPurchaseCost => FixedAssets.Sum(asset => asset.PurchaseCost);
    public int DiscoveryCandidateCount => DiscoveryCandidates.Count;

    public int FixedAssetLedgerCount
    {
        get => _fixedAssetLedgerCount;
        private set => SetProperty(ref _fixedAssetLedgerCount, value);
    }

    public int CompleteAuthorityCount
    {
        get => _completeAuthorityCount;
        private set => SetProperty(ref _completeAuthorityCount, value);
    }

    public void PrepareForDisplay()
    {
        IsEditorOpen = false;
        IsDepreciationDetailOpen = false;
        Refresh();
    }

    public void ShowDepreciationDetails(FixedAssetRecord asset)
    {
        if (asset is null) return;
        SelectedAsset = asset;
        var (method, rate) = ResolveMethodAndRate(asset);
        var lines = new List<string>
        {
            $"{asset.AssetName}",
            $"Opening balance: {asset.OpeningBalance:N2} ({asset.OpeningSource})",
            $"Additions: {asset.CurrentAdditions:N2}",
            $"Deletions: {asset.CurrentDeletions:N2}",
            $"Method / Rate: {method} / {rate:0.####}%",
            $"Depreciation calculation: {DepreciationCalculation}",
            $"Normal depreciation: {asset.NormalDepreciation:N2}"
        };

        if (AdditionalDepreciationEnabled)
            lines.Add($"Additional depreciation: {asset.AdditionalDepreciation:N2} @ {AdditionalDepreciationRate:0.####}%");
        lines.Add($"Total depreciation: {asset.CurrentDepreciation:N2}");
        if (ResolveDepreciationRoundingDigits() == 0)
            lines.Add("Depreciation rounding: nearest rupee, proved from the linked Tally depreciation postings.");
        if (asset.PostedDepreciation > 0.005m)
            lines.Add($"Tally depreciation found: {asset.PostedDepreciation:N2} · Difference: {(asset.CurrentDepreciation - asset.PostedDepreciation):N2}");
        lines.Add($"Posting: {DepreciationPosting}");

        if (IsAccumulatedDepreciationPosting)
        {
            var mapping = FindPostingMapping(asset);
            var grossOpening = asset.GrossOpeningBalance > 0m ? asset.GrossOpeningBalance : asset.OpeningBalance + asset.OpeningAccumulatedDepreciation;
            var grossClosing = Math.Max(0m, grossOpening + asset.CurrentAdditions - asset.CurrentDeletions);
            var closingAccumulated = Math.Max(0m, asset.OpeningAccumulatedDepreciation + asset.CurrentDepreciation);
            lines.Add($"Gross opening: {grossOpening:N2}");
            lines.Add($"Opening accumulated depreciation: {asset.OpeningAccumulatedDepreciation:N2}");
            lines.Add($"Gross closing before accumulated depreciation: {grossClosing:N2}");
            lines.Add($"Closing accumulated depreciation: {closingAccumulated:N2}");
            lines.Add($"Accumulated depreciation ledger: {mapping?.AccumulatedLedgerName ?? "Not mapped"}");
        }

        lines.Add($"Closing balance / NBV: {asset.CurrentBookValue:N2}");
        if (!string.IsNullOrWhiteSpace(asset.DetectionStatus))
            lines.Add($"Historical detection: {asset.DetectionStatus}");

        DepreciationDetailText = string.Join(Environment.NewLine, lines);
        IsDepreciationDetailOpen = true;
    }

    private void OnLiveAccountingChanged(object? sender, AccountingLiveChange change)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || change.ClientId != client.Id ||
            !change.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase) ||
            (change.Area != AccountingLiveArea.Masters && change.Area != AccountingLiveArea.Journal)) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _ = dispatcher.BeginInvoke(() =>
        {
            var current = _clientContext.CurrentClient;
            if (current is null || current.Id != change.ClientId ||
                !current.FinancialYear.Equals(change.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;
            Refresh();
            SettingsStatusText = "Live Tally Master/Voucher authority changed; Fixed Assets refreshed automatically.";
        });
    }

    private bool HasClient() => _clientContext.CurrentClient is not null;

    private void Refresh()
    {
        var client = _clientContext.CurrentClient;
        FixedAssets.Clear();
        _ledgerSnapshots.Clear();
        PostingMappings.Clear();
        AvailableLedgerNames.Clear();
        ExpenseLedgerNames.Clear();

        if (client is null)
        {
            EditingAsset = new FixedAssetRecord();
            SelectedAsset = null;
            SelectedDiscoveryCandidate = null;
            DiscoveryCandidates.Clear();
            FixedAssetLedgerCount = 0;
            CompleteAuthorityCount = 0;
            StatusText = "Open a client to manage Fixed Assets.";
            DiscoveryStatusText = "Open a client to inspect cached Tally Fixed Asset authority.";
            SettingsStatusText = string.Empty;
            IsEditorOpen = false;
            IsDepreciationDetailOpen = false;
            RaiseSummaryChanged();
            RaiseCommandStates();
            return;
        }

        try
        {
            LoadSettings(client.Id, client.FinancialYear);
            RefreshLedgerOptions(client.Id, client.FinancialYear);
            foreach (var asset in _repository.GetFixedAssets(client.Id, client.FinancialYear))
                FixedAssets.Add(asset);
            LoadPostingMappings(client.Id, client.FinancialYear);
            StatusText = $"{FixedAssets.Count:N0} saved fixed asset row(s) · FY {client.FinancialYear}. Cached Tally rows will merge below.";
            if (EditingAsset.ClientId != client.Id || !EditingAsset.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
                PrepareBlankEditor(client.Id, client.FinancialYear);
        }
        catch (Exception exception)
        {
            StatusText = $"Fixed Assets refresh failed: {exception.Message}";
        }

        RecalculateAll();
        RaiseSummaryChanged();
        RaiseCommandStates();
        BeginDiscovery(client.Id, client.FinancialYear);
    }

    private void LoadSettings(long clientId, string financialYear)
    {
        _loadingSettings = true;
        try
        {
            _settings = _repository.GetFixedAssetDepreciationSettings(clientId, financialYear);
            if (string.IsNullOrWhiteSpace(_settings.DepreciationPolicy)) _settings.DepreciationPolicy = "Fixed/Common";
            if (string.IsNullOrWhiteSpace(_settings.DepreciationMethod)) _settings.DepreciationMethod = "SLM";
            if (string.IsNullOrWhiteSpace(_settings.ExistingAssetCalculation)) _settings.ExistingAssetCalculation = "Yearly";
            if (string.IsNullOrWhiteSpace(_settings.AdditionCalculation)) _settings.AdditionCalculation = _settings.ExistingAssetCalculation;
            if (!_settings.AdditionCalculation.Equals(_settings.ExistingAssetCalculation, StringComparison.OrdinalIgnoreCase)) _settings.ExistingAssetCalculation = _settings.AdditionCalculation;
            if (string.IsNullOrWhiteSpace(_settings.VoucherRoundOffMode))
                _settings.VoucherRoundOffMode = "No Round Off";
            else if (!_settings.VoucherRoundOffMode.Equals("No Round Off", StringComparison.OrdinalIgnoreCase) &&
                     !_settings.VoucherRoundOffMode.Equals("Off", StringComparison.OrdinalIgnoreCase))
                _settings.VoucherRoundOffMode = "Nearest ₹1";
            if (string.IsNullOrWhiteSpace(_settings.DepreciationPosting)) _settings.DepreciationPosting = "Direct to Fixed Asset";
            foreach (var property in new[]
                     {
                         nameof(DepreciationPolicy), nameof(IsFixedCommonPolicy), nameof(IsPerAssetPolicy), nameof(DepreciationMethod),
                         nameof(CommonDepreciationRate), nameof(DepreciationCalculation), nameof(ExistingAssetCalculation), nameof(AdditionCalculation),
                         nameof(IsCalculationYearly), nameof(IsCalculationDayWise), nameof(IsCalculationIncomeTax180),
                         nameof(AdditionalDepreciationEnabled), nameof(IsAdditionalDepreciationEnabled),
                         nameof(AdditionalDepreciationRate), nameof(DepreciationPosting),
                         nameof(IsAccumulatedDepreciationPosting), nameof(IsDirectDepreciationPosting), nameof(ExpenseLedgerName), nameof(VoucherRoundOffEnabled),
                         nameof(IsMethodSlm), nameof(IsMethodWdv), nameof(IsMethodOther)
                     })
                OnPropertyChanged(property);
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void RefreshLedgerOptions(long clientId, string financialYear)
    {
        _ledgerOptions.Clear();
        AvailableLedgerNames.Clear();
        ExpenseLedgerNames.Clear();
        foreach (var option in _discoveryService.GetLedgerOptions(clientId, financialYear))
        {
            var key = LedgerNameKey(option.Name);
            if (key.Length == 0 || _ledgerOptions.ContainsKey(key)) continue;
            _ledgerOptions[key] = option;
            AvailableLedgerNames.Add(option.Name);
            if (option.Parent.Equals("Indirect Expenses", StringComparison.OrdinalIgnoreCase))
                ExpenseLedgerNames.Add(option.Name);
        }
    }

    private void LoadPostingMappings(long clientId, string financialYear)
    {
        PostingMappings.Clear();
        foreach (var row in _repository.GetFixedAssetPostingLedgerMappings(clientId, financialYear))
        {
            PostingMappings.Add(new FixedAssetPostingMappingRow(
                row.AssetLedgerName, row.AssetLedgerMasterId,
                row.AccumulatedLedgerName, row.AccumulatedLedgerMasterId));
        }
    }

    private void BeginDiscovery(long clientId, string financialYear)
    {
        var generation = Interlocked.Increment(ref _discoveryGeneration);
        DiscoveryCandidates.Clear();
        SelectedDiscoveryCandidate = null;
        FixedAssetLedgerCount = 0;
        CompleteAuthorityCount = 0;
        RaiseSummaryChanged();
        DiscoveryBusy = true;
        DiscoveryStatusText = "Reading prepared Tally Fixed Asset authority from the local cache…";

        _ = Task.Run(() => _discoveryService.Discover(clientId, financialYear))
            .ContinueWith(task =>
            {
                if (generation != _discoveryGeneration) return;
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is null) return;
                _ = dispatcher.BeginInvoke(() =>
                {
                    if (generation != _discoveryGeneration) return;
                    DiscoveryCandidates.Clear();
                    SelectedDiscoveryCandidate = null;

                    if (task.IsFaulted)
                    {
                        DiscoveryStatusText = $"Fixed Asset source discovery failed: {task.Exception?.GetBaseException().Message}";
                        FixedAssetLedgerCount = 0;
                        CompleteAuthorityCount = 0;
                    }
                    else if (task.IsCanceled)
                    {
                        DiscoveryStatusText = "Fixed Asset source discovery cancelled.";
                        FixedAssetLedgerCount = 0;
                        CompleteAuthorityCount = 0;
                    }
                    else
                    {
                        var snapshot = task.Result;
                        foreach (var candidate in snapshot.Candidates)
                            DiscoveryCandidates.Add(candidate);
                        FixedAssetLedgerCount = snapshot.FixedAssetLedgerCount;
                        CompleteAuthorityCount = snapshot.CompleteAuthorityCount;
                        DiscoveryStatusText = snapshot.Status;
                        ApplyDiscoveryToRegister(snapshot);
                    }

                    DiscoveryBusy = false;
                    RaiseSummaryChanged();
                    RaiseCommandStates();
                });
            }, TaskScheduler.Default);
    }

    private void ApplyDiscoveryToRegister(FixedAssetDiscoverySnapshot snapshot)
    {
        foreach (var transient in FixedAssets.Where(row => row.IsTransientTallyRow).ToArray())
            FixedAssets.Remove(transient);
        _ledgerSnapshots.Clear();

        foreach (var source in snapshot.LedgerSnapshots)
        {
            _ledgerSnapshots[LedgerIdentityKey(source.LedgerMasterId, source.LedgerName)] = source;
            var asset = FindSavedAssetForLedger(source);
            if (asset is null)
            {
                asset = new FixedAssetRecord
                {
                    ClientId = _clientContext.CurrentClient?.Id ?? 0,
                    FinancialYear = _clientContext.CurrentClient?.FinancialYear ?? string.Empty,
                    AssetName = source.LedgerName,
                    AssetGroup = "Fixed Assets",
                    TallyLedgerName = source.LedgerName,
                    TallyLedgerMasterId = source.LedgerMasterId,
                    OpeningBalance = source.TallyOpeningBalance,
                    GrossOpeningBalance = source.TallyOpeningBalance,
                    OpeningSource = "Tally",
                    PurchaseDate = source.FirstAdditionDate,
                    PutToUseDate = source.FirstAdditionDate,
                    SupplierName = source.SupplierName,
                    PurchaseCost = source.TallyOpeningBalance + source.Additions,
                    DepreciationMethod = string.IsNullOrWhiteSpace(source.DetectedMethod) ? "SLM" : source.DetectedMethod,
                    DepreciationRate = source.DetectedRate ?? 0m,
                    PolicySource = source.DetectionSource.Length > 0 ? $"Tally history · {source.DetectionSource}" : "Tally current FY",
                    DetectionStatus = source.DetectionStatus,
                    IsTransientTallyRow = true
                };
                FixedAssets.Add(asset);
            }
            else
            {
                asset.TallyLedgerName = source.LedgerName;
                asset.TallyLedgerMasterId = source.LedgerMasterId;
                asset.GrossOpeningBalance = source.TallyOpeningBalance;
                if (string.IsNullOrWhiteSpace(asset.OpeningSource) ||
                    asset.OpeningSource.Equals("Tally", StringComparison.OrdinalIgnoreCase))
                {
                    asset.OpeningSource = "Tally";
                    asset.OpeningBalance = source.TallyOpeningBalance;
                }
                if (!asset.PurchaseDate.HasValue) asset.PurchaseDate = source.FirstAdditionDate;
                if (!asset.PutToUseDate.HasValue) asset.PutToUseDate = source.FirstAdditionDate;
                if (string.IsNullOrWhiteSpace(asset.SupplierName)) asset.SupplierName = source.SupplierName;
                asset.DetectionStatus = source.DetectionStatus;
                if (source.DetectionSource.Length > 0 && string.IsNullOrWhiteSpace(asset.PolicySource))
                    asset.PolicySource = $"Tally history · {source.DetectionSource}";
                var mayAutoApplyAssetPolicy = asset.DepreciationRate <= 0m &&
                    (string.IsNullOrWhiteSpace(asset.PolicySource) || asset.PolicySource.StartsWith("Tally", StringComparison.OrdinalIgnoreCase));
                if (mayAutoApplyAssetPolicy && source.DetectedRate.HasValue)
                {
                    asset.DepreciationRate = source.DetectedRate.Value;
                    if (!string.IsNullOrWhiteSpace(source.DetectedMethod))
                        asset.DepreciationMethod = source.DetectedMethod;
                    asset.PolicySource = source.DetectionSource.Length > 0 ? $"Tally history · {source.DetectionSource}" : "Tally history";
                }
            }

            asset.CurrentAdditions = source.Additions;
            asset.CurrentDeletions = source.Deletions;
            asset.PostedDepreciation = source.PostedDepreciation;
        }

        EnsurePostingMappingRows(snapshot.LedgerSnapshots);
        AutoApplyCommonHistoricalPolicy(snapshot.LedgerSnapshots);
        RefreshAccumulatedOpeningAuthority();
        RecalculateAll();

        StatusText = $"{FixedAssets.Count:N0} register row(s) · {snapshot.FixedAssetLedgerCount:N0} Tally Fixed Asset ledger(s) · FY {_clientContext.CurrentClient?.FinancialYear}.";
    }

    private FixedAssetRecord? FindSavedAssetForLedger(FixedAssetLedgerSnapshot source)
    {
        var id = source.LedgerMasterId.Trim();
        if (id.Length > 0)
        {
            var byId = FixedAssets.Where(row => !row.IsTransientTallyRow &&
                                                row.TallyLedgerMasterId.Trim().Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byId.Length == 1) return byId[0];
        }

        var byExplicitName = FixedAssets.Where(row => !row.IsTransientTallyRow &&
            (Equivalent(row.TallyLedgerName, source.LedgerName) ||
             (row.TallyLedgerName.Trim().Length == 0 && Equivalent(row.AssetName, source.LedgerName)))).ToArray();
        return byExplicitName.Length == 1 ? byExplicitName[0] : null;
    }

    private void EnsurePostingMappingRows(IEnumerable<FixedAssetLedgerSnapshot> snapshots)
    {
        foreach (var source in snapshots)
        {
            if (PostingMappings.Any(row => LedgerIdentityKey(row.AssetLedgerMasterId, row.AssetLedgerName)
                .Equals(LedgerIdentityKey(source.LedgerMasterId, source.LedgerName), StringComparison.OrdinalIgnoreCase)))
                continue;
            PostingMappings.Add(new FixedAssetPostingMappingRow(source.LedgerName, source.LedgerMasterId, string.Empty, string.Empty));
        }
    }

    private void AutoApplyCommonHistoricalPolicy(IEnumerable<FixedAssetLedgerSnapshot> snapshots)
    {
        if (!IsFixedCommonPolicy) return;

        var detected = snapshots.Where(row => row.DetectedRate is > 0m).ToArray();
        if (detected.Length == 0) return;

        var min = detected.Min(row => row.DetectedRate!.Value);
        var max = detected.Max(row => row.DetectedRate!.Value);
        var commonRateProved = max - min <= 0.50m;
        var methods = detected.Select(row => row.DetectedMethod)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        _loadingSettings = true;
        try
        {
            // Fixed/Common is a common method/rule. Different statutory asset blocks
            // may still carry different proved percentages; never average them.
            if (CommonDepreciationRate <= 0m && commonRateProved)
                _settings.CommonDepreciationRate = Math.Round(detected.Average(row => row.DetectedRate!.Value), 4, MidpointRounding.AwayFromZero);
            if (methods.Length == 1) _settings.DepreciationMethod = methods[0];
            if (detected.Any(row => row.DetectedAdditionCalculation.Equals("Income Tax 180-Day", StringComparison.OrdinalIgnoreCase)))
            {
                _settings.ExistingAssetCalculation = "Income Tax 180-Day";
                _settings.AdditionCalculation = "Income Tax 180-Day";
            }
            OnPropertyChanged(nameof(CommonDepreciationRate));
            OnPropertyChanged(nameof(DepreciationMethod));
            OnPropertyChanged(nameof(DepreciationCalculation));
            OnPropertyChanged(nameof(ExistingAssetCalculation));
            OnPropertyChanged(nameof(AdditionCalculation));
            OnPropertyChanged(nameof(IsCalculationYearly));
            OnPropertyChanged(nameof(IsCalculationDayWise));
            OnPropertyChanged(nameof(IsCalculationIncomeTax180));
        }
        finally
        {
            _loadingSettings = false;
        }

        SaveDepreciationSettings();
        SettingsStatusText = methods.Length == 1
            ? commonRateProved
                ? $"Tally movements applied common {DepreciationMethod} rule; common rate {CommonDepreciationRate:0.####}% is proved."
                : $"Tally movements applied common {DepreciationMethod} rule with asset/block-specific rates."
            : commonRateProved
                ? $"Tally movements applied common fallback rate {CommonDepreciationRate:0.####}%; method is not uniquely proved."
                : "Tally movements proved different asset/block rates; rates were applied per Fixed Asset ledger and no false common percentage was created.";
    }

    private void NewAsset()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        SelectedAsset = null;
        EditingAsset = new FixedAssetRecord
        {
            ClientId = client.Id,
            FinancialYear = client.FinancialYear,
            AssetGroup = "Fixed Assets",
            PurchaseDate = DateTime.Today,
            PutToUseDate = DateTime.Today,
            DepreciationMethod = IsFixedCommonPolicy ? DepreciationMethod : "SLM",
            DepreciationRate = IsFixedCommonPolicy ? CommonDepreciationRate : 0m,
            OpeningSource = "Manual"
        };
        IsEditorOpen = true;
        StatusText = "New fixed asset ready.";
    }

    private void PrepareBlankEditor(long clientId, string financialYear)
    {
        EditingAsset = new FixedAssetRecord
        {
            ClientId = clientId,
            FinancialYear = financialYear,
            AssetGroup = "Fixed Assets",
            DepreciationMethod = "SLM",
            OpeningSource = "Manual"
        };
    }

    private void EditSelectedAsset()
    {
        if (SelectedAsset is null) return;
        EditingAsset = CloneAsset(SelectedAsset);
        IsEditorOpen = true;
        StatusText = $"Editing {SelectedAsset.AssetName}.";
    }

    private void CloseEditor()
    {
        IsEditorOpen = false;
        var client = _clientContext.CurrentClient;
        if (client is not null) PrepareBlankEditor(client.Id, client.FinancialYear);
    }

    private void SaveAsset()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        try
        {
            EditingAsset.ClientId = client.Id;
            EditingAsset.FinancialYear = client.FinancialYear;
            EditingAsset.AssetCode = AccountingText.DisplayName(EditingAsset.AssetCode);
            EditingAsset.AssetName = AccountingText.DisplayName(EditingAsset.AssetName);
            EditingAsset.AssetGroup = AccountingText.DisplayName(EditingAsset.AssetGroup);
            EditingAsset.Location = AccountingText.DisplayName(EditingAsset.Location);
            EditingAsset.SerialNumber = AccountingText.DisplayName(EditingAsset.SerialNumber);
            EditingAsset.SupplierName = AccountingText.DisplayName(EditingAsset.SupplierName);
            EditingAsset.TallyLedgerName = AccountingText.DisplayName(EditingAsset.TallyLedgerName);
            if (EditingAsset.TallyLedgerName.Length > 0)
                EditingAsset.TallyLedgerMasterId = ResolveLedgerMasterId(EditingAsset.TallyLedgerName);
            if (EditingAsset.OpeningSource.Equals("Tally", StringComparison.OrdinalIgnoreCase) &&
                EditingAsset.TallyLedgerName.Length > 0)
            {
                var opening = _discoveryService.GetLedgerOpeningBalance(
                    client.Id, client.FinancialYear, EditingAsset.TallyLedgerName, EditingAsset.TallyLedgerMasterId);
                if (opening.HasValue)
                {
                    EditingAsset.GrossOpeningBalance = Math.Abs(opening.Value);
                    EditingAsset.OpeningBalance = Math.Abs(opening.Value);
                }
            }

            var savedName = EditingAsset.AssetName;
            _repository.SaveFixedAsset(EditingAsset);
            _repository.RecordAudit(client.Id, client.FinancialYear, "Fixed Assets", "Fixed Asset",
                EditingAsset.AssetCode.Length > 0 ? EditingAsset.AssetCode : EditingAsset.AssetName,
                "Save", string.Empty, EditingAsset.AssetName);
            IsEditorOpen = false;
            Refresh();
            StatusText = $"Fixed asset '{savedName}' saved.";
        }
        catch (Exception exception)
        {
            StatusText = $"Fixed asset save failed: {exception.Message}";
        }
    }

    private void DeleteAsset()
    {
        var client = _clientContext.CurrentClient;
        var asset = SelectedAsset;
        if (client is null || asset is null || asset.Id <= 0) return;
        if (MessageBox.Show($"Delete fixed asset '{asset.AssetName}' from the AAH register? Tally source data is not deleted.", "Delete Fixed Asset",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            _repository.DeleteFixedAsset(asset.Id, client.Id, client.FinancialYear);
            _repository.RecordAudit(client.Id, client.FinancialYear, "Fixed Assets", "Fixed Asset",
                asset.AssetCode.Length > 0 ? asset.AssetCode : asset.AssetName, "Delete", asset.AssetName, string.Empty);
            Refresh();
        }
        catch (Exception exception)
        {
            StatusText = $"Fixed asset delete failed: {exception.Message}";
        }
    }

    private void SaveDepreciationSettings()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        try
        {
            _settings.ClientId = client.Id;
            _settings.FinancialYear = client.FinancialYear;
            _settings.ExpenseLedgerMasterId = ResolveLedgerMasterId(_settings.ExpenseLedgerName);
            _repository.SaveFixedAssetDepreciationSettings(_settings);

            foreach (var mapping in PostingMappings)
            {
                mapping.AccumulatedLedgerMasterId = ResolveLedgerMasterId(mapping.AccumulatedLedgerName);
                _repository.SaveFixedAssetPostingLedgerMapping(new FixedAssetPostingLedgerMappingRecord
                {
                    ClientId = client.Id,
                    FinancialYear = client.FinancialYear,
                    AssetLedgerName = mapping.AssetLedgerName,
                    AssetLedgerMasterId = mapping.AssetLedgerMasterId,
                    AccumulatedLedgerName = mapping.AccumulatedLedgerName,
                    AccumulatedLedgerMasterId = mapping.AccumulatedLedgerMasterId
                });
            }
            RefreshAccumulatedOpeningAuthority();
            RecalculateAll();
            SettingsStatusText = "Depreciation settings saved.";
        }
        catch (Exception exception)
        {
            SettingsStatusText = $"Settings save failed: {exception.Message}";
        }
    }

    private void RecalculateAndPersistSettings(bool recalculate = true)
    {
        if (recalculate)
        {
            RefreshAccumulatedOpeningAuthority();
            RecalculateAll();
            RecalculateEditingPreview();
        }
        if (!_loadingSettings) SaveDepreciationSettings();
    }

    private void CreateExpenseLedger()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        try
        {
            var option = _discoveryService.CreateLocalLedger(client.Id, client.FinancialYear, NewExpenseLedgerName, "Indirect Expenses");
            RefreshLedgerOptions(client.Id, client.FinancialYear);
            ExpenseLedgerName = option.Name;
            NewExpenseLedgerName = string.Empty;
            SettingsStatusText = option.IsLiveTally
                ? $"Existing Tally ledger '{option.Name}' selected."
                : $"Local ledger '{option.Name}' created; normal Master export will obtain its Tally MASTERID before voucher export.";
        }
        catch (Exception exception)
        {
            SettingsStatusText = $"Expense ledger create/select failed: {exception.Message}";
        }
    }

    private void CreateAccumulatedLedger()
    {
        var client = _clientContext.CurrentClient;
        var mapping = SelectedPostingMapping;
        if (client is null || mapping is null) return;
        try
        {
            var option = _discoveryService.CreateLocalLedger(client.Id, client.FinancialYear, NewAccumulatedLedgerName, "Fixed Assets");
            RefreshLedgerOptions(client.Id, client.FinancialYear);
            mapping.AccumulatedLedgerName = option.Name;
            mapping.AccumulatedLedgerMasterId = option.MasterId;
            NewAccumulatedLedgerName = string.Empty;
            SaveDepreciationSettings();
            RefreshAccumulatedOpeningAuthority();
            RecalculateAll();
            SettingsStatusText = option.IsLiveTally
                ? $"Existing Tally ledger '{option.Name}' mapped."
                : $"Local accumulated-depreciation ledger '{option.Name}' created; normal Master export will bind its Tally MASTERID.";
        }
        catch (Exception exception)
        {
            SettingsStatusText = $"Accumulated ledger create/select failed: {exception.Message}";
        }
    }

    private void GenerateDepreciationVoucher()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        try
        {
            RecalculateAll();
            if (string.IsNullOrWhiteSpace(ExpenseLedgerName))
                throw new InvalidOperationException("Select the Depreciation Expense Ledger inside Depreciation Posting first.");

            var creditByLedger = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in FixedAssets.Where(row => row.CurrentDepreciation - row.PostedDepreciation > 0.005m))
            {
                string targetLedger;
                if (IsAccumulatedDepreciationPosting)
                {
                    var mapping = FindPostingMapping(asset);
                    targetLedger = mapping?.AccumulatedLedgerName?.Trim() ?? string.Empty;
                    if (targetLedger.Length == 0)
                        throw new InvalidOperationException($"Accumulated Depreciation ledger is not mapped for '{asset.AssetName}'.");
                }
                else
                {
                    targetLedger = (asset.TallyLedgerName.Length > 0 ? asset.TallyLedgerName : asset.AssetName).Trim();
                    if (targetLedger.Length == 0)
                        throw new InvalidOperationException($"Fixed Asset posting ledger is missing for '{asset.AssetName}'.");
                }

                var adjustment = ApplyVoucherRoundOff(RoundDepreciationAmount(
                    Math.Max(0m, asset.CurrentDepreciation - asset.PostedDepreciation),
                    ResolveDepreciationRoundingDigits()));
                if (adjustment <= 0.005m) continue;
                creditByLedger[targetLedger] = creditByLedger.GetValueOrDefault(targetLedger) + adjustment;
            }

            var total = Math.Round(creditByLedger.Values.Sum(), 2, MidpointRounding.AwayFromZero);
            if (total <= 0.005m) throw new InvalidOperationException("Calculated depreciation already matches the depreciation proved in Tally; there is no adjustment to generate.");

            var preview = string.Join(Environment.NewLine, creditByLedger.OrderBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                .Select(row => $"Cr {row.Key}: {row.Value:N2}"));
            var answer = System.Windows.MessageBox.Show(
                $"One depreciation Journal will be created/updated for the remaining adjustment only.\n\nDr {ExpenseLedgerName.Trim()}: {total:N2}\n{preview}\n\nContinue?",
                "Generate Depreciation Voucher",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            var existing = _journalRepository.GetVouchers(client.Id, client.FinancialYear)
                .Where(voucher => voucher.SourceJobId.Equals("FIXEDASSET-DEPRECIATION", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(voucher => voucher.PersistentId)
                .FirstOrDefault();

            var (_, fyEnd) = FinancialYearBounds(client.FinancialYear);
            var voucher = new JournalVoucher
            {
                PersistentId = existing?.PersistentId ?? 0,
                ClientId = client.Id,
                FinancialYear = client.FinancialYear,
                PeriodKey = $"FA-DEP-{client.FinancialYear}",
                PeriodLabel = $"Fixed Asset Depreciation {client.FinancialYear}",
                VoucherDate = fyEnd,
                VoucherNumber = existing?.VoucherNumber ?? string.Empty,
                VoucherType = "Journal",
                Narration = $"Depreciation for FY {client.FinancialYear}",
                GenerationMode = "Manual",
                WorkflowStatus = "Draft",
                ReferenceNumber = $"FA-DEP-{client.FinancialYear}",
                SourceRunId = $"FIXEDASSET|{client.Id}|{client.FinancialYear}",
                SourceJobId = "FIXEDASSET-DEPRECIATION",
                CreatedBy = Environment.UserName,
                CreatedUtc = existing?.CreatedUtc ?? DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                IsUserEdited = true
            };

            var lineNumber = 1;
            voucher.Lines.Add(new JournalLine
            {
                LineNumber = lineNumber++,
                LedgerName = ExpenseLedgerName.Trim(),
                Amount = total,
                DrCr = "Dr",
                LineRole = "Fixed Asset Depreciation Expense"
            });
            foreach (var credit in creditByLedger.OrderBy(row => row.Key, StringComparer.OrdinalIgnoreCase))
            {
                voucher.Lines.Add(new JournalLine
                {
                    LineNumber = lineNumber++,
                    LedgerName = credit.Key,
                    Amount = Math.Round(credit.Value, 2, MidpointRounding.AwayFromZero),
                    DrCr = "Cr",
                    LineRole = IsAccumulatedDepreciationPosting ? "Accumulated Depreciation" : "Fixed Asset Depreciation"
                });
            }

            _journalRepository.SaveVoucher(voucher);
            StatusText = existing is null
                ? $"Generated one depreciation Journal voucher for {total:N2}. It will use the normal MASTERID-first Journal/Tally export path."
                : $"Updated the existing single depreciation Journal voucher to {total:N2}; no second depreciation voucher was created.";
        }
        catch (Exception exception)
        {
            StatusText = $"Depreciation voucher generation failed: {exception.Message}";
        }
    }

    private void RefreshAccumulatedOpeningAuthority()
    {
        if (!IsAccumulatedDepreciationPosting)
        {
            foreach (var asset in FixedAssets)
            {
                if (asset.GrossOpeningBalance > 0m &&
                    (asset.OpeningSource.Length == 0 || asset.OpeningSource.Equals("Tally", StringComparison.OrdinalIgnoreCase)))
                    asset.OpeningBalance = asset.GrossOpeningBalance;
            }
            return;
        }

        var client = _clientContext.CurrentClient;
        if (client is null) return;
        foreach (var asset in FixedAssets)
        {
            var mapping = FindPostingMapping(asset);
            if (mapping is null || string.IsNullOrWhiteSpace(mapping.AccumulatedLedgerName)) continue;
            var accumulatedOpening = _discoveryService.GetLedgerOpeningBalance(
                client.Id, client.FinancialYear, mapping.AccumulatedLedgerName, mapping.AccumulatedLedgerMasterId);
            if (!accumulatedOpening.HasValue) continue;

            asset.OpeningAccumulatedDepreciation = Math.Abs(accumulatedOpening.Value);
            if (asset.OpeningSource.Length == 0 || asset.OpeningSource.Equals("Tally", StringComparison.OrdinalIgnoreCase))
            {
                var gross = asset.GrossOpeningBalance > 0m ? asset.GrossOpeningBalance : asset.OpeningBalance;
                asset.GrossOpeningBalance = gross;
                asset.OpeningBalance = Math.Max(0m, gross - asset.OpeningAccumulatedDepreciation);
                asset.OpeningSource = "Tally";
            }
        }
    }

    private void RecalculateAll()
    {
        foreach (var asset in FixedAssets)
            Recalculate(asset);
        RaiseSummaryChanged();
        GenerateVoucherCommand.RaiseCanExecuteChanged();
        if (SelectedAsset is not null && IsDepreciationDetailOpen)
            ShowDepreciationDetails(SelectedAsset);
    }

    private void Recalculate(FixedAssetRecord asset)
    {
        var (method, rate) = ResolveMethodAndRate(asset);
        if (rate < 0m) rate = 0m;
        var (fyStart, fyEnd) = FinancialYearBounds(asset.FinancialYear);
        var yearDays = Math.Max(1, (fyEnd - fyStart).Days + 1);

        var opening = Math.Max(0m, asset.OpeningBalance);
        var additions = Math.Max(0m, asset.CurrentAdditions);
        var deletions = Math.Max(0m, asset.CurrentDeletions);

        decimal existingFactor = 1m;
        if (ExistingAssetCalculation.Equals("Day-wise", StringComparison.OrdinalIgnoreCase) &&
            asset.DisposalDate.HasValue && asset.DisposalDate.Value.Date >= fyStart && asset.DisposalDate.Value.Date <= fyEnd)
        {
            existingFactor = DecimalDays(fyStart, asset.DisposalDate.Value.Date, yearDays);
        }

        var existingBase = method.Equals("SLM", StringComparison.OrdinalIgnoreCase) && asset.PurchaseCost > 0m && !asset.IsTransientTallyRow
            ? Math.Max(0m, asset.PurchaseCost - asset.ResidualValue)
            : Math.Max(0m, opening - Math.Min(asset.ResidualValue, opening));
        var existingDep = existingBase * rate / 100m * existingFactor;

        decimal additionDep = 0m;
        var snapshot = FindSnapshot(asset);
        if (additions > 0m)
        {
            if (AdditionCalculation.Equals("Day-wise", StringComparison.OrdinalIgnoreCase))
            {
                var movements = snapshot?.AdditionMovements ?? Array.Empty<FixedAssetTimedAmount>();
                if (movements.Count > 0)
                {
                    foreach (var movement in movements)
                    {
                        var start = movement.Date < fyStart ? fyStart : movement.Date > fyEnd ? fyEnd : movement.Date;
                        additionDep += movement.Amount * rate / 100m * DecimalDays(start, fyEnd, yearDays);
                    }
                }
                else
                {
                    var start = asset.PutToUseDate ?? asset.PurchaseDate ?? fyStart;
                    if (start < fyStart) start = fyStart;
                    if (start > fyEnd) start = fyEnd;
                    additionDep = additions * rate / 100m * DecimalDays(start.Date, fyEnd, yearDays);
                }
            }
            else if (AdditionCalculation.Equals("Income Tax 180-Day", StringComparison.OrdinalIgnoreCase))
            {
                var movements = snapshot?.AdditionMovements ?? Array.Empty<FixedAssetTimedAmount>();
                if (movements.Count > 0)
                {
                    foreach (var movement in movements)
                    {
                        var daysUsed = movement.Date.Date > fyEnd ? 0 : (fyEnd - movement.Date.Date).Days + 1;
                        additionDep += movement.Amount * rate / 100m * (daysUsed >= 180 ? 1m : 0.5m);
                    }
                }
                else
                {
                    var start = (asset.PutToUseDate ?? asset.PurchaseDate ?? fyStart).Date;
                    var daysUsed = start > fyEnd ? 0 : (fyEnd - start).Days + 1;
                    additionDep = additions * rate / 100m * (daysUsed >= 180 ? 1m : 0.5m);
                }
            }
            else
            {
                additionDep = additions * rate / 100m;
            }
        }

        var normal = Math.Max(0m, existingDep + additionDep);
        decimal additional = 0m;
        if (AdditionalDepreciationEnabled && additions > 0m && AdditionalDepreciationRate > 0m)
        {
            if (AdditionCalculation.Equals("Day-wise", StringComparison.OrdinalIgnoreCase))
            {
                var movements = snapshot?.AdditionMovements ?? Array.Empty<FixedAssetTimedAmount>();
                if (movements.Count > 0)
                {
                    foreach (var movement in movements)
                    {
                        var start = movement.Date < fyStart ? fyStart : movement.Date > fyEnd ? fyEnd : movement.Date;
                        additional += movement.Amount * AdditionalDepreciationRate / 100m * DecimalDays(start, fyEnd, yearDays);
                    }
                }
                else
                {
                    var start = asset.PutToUseDate ?? asset.PurchaseDate ?? fyStart;
                    if (start < fyStart) start = fyStart;
                    if (start > fyEnd) start = fyEnd;
                    additional = additions * AdditionalDepreciationRate / 100m * DecimalDays(start.Date, fyEnd, yearDays);
                }
            }
            else if (AdditionCalculation.Equals("Income Tax 180-Day", StringComparison.OrdinalIgnoreCase))
            {
                var movements = snapshot?.AdditionMovements ?? Array.Empty<FixedAssetTimedAmount>();
                if (movements.Count > 0)
                {
                    foreach (var movement in movements)
                    {
                        var daysUsed = movement.Date.Date > fyEnd ? 0 : (fyEnd - movement.Date.Date).Days + 1;
                        additional += movement.Amount * AdditionalDepreciationRate / 100m * (daysUsed >= 180 ? 1m : 0.5m);
                    }
                }
                else
                {
                    var start = (asset.PutToUseDate ?? asset.PurchaseDate ?? fyStart).Date;
                    var daysUsed = start > fyEnd ? 0 : (fyEnd - start).Days + 1;
                    additional = additions * AdditionalDepreciationRate / 100m * (daysUsed >= 180 ? 1m : 0.5m);
                }
            }
            else
            {
                additional = additions * AdditionalDepreciationRate / 100m;
            }
        }

        var maximumDepreciable = Math.Max(0m, opening + additions - deletions - Math.Max(0m, asset.ResidualValue));
        var depreciationDigits = ResolveDepreciationRoundingDigits();
        var normalCapped = Math.Min(normal, maximumDepreciable);
        var roundedNormal = RoundDepreciationAmount(normalCapped, depreciationDigits);
        if (roundedNormal > maximumDepreciable) roundedNormal = maximumDepreciable;

        var additionalCapacity = Math.Max(0m, maximumDepreciable - roundedNormal);
        var additionalCapped = Math.Min(additional, additionalCapacity);
        var roundedAdditional = RoundDepreciationAmount(additionalCapped, depreciationDigits);
        if (roundedAdditional > additionalCapacity) roundedAdditional = additionalCapacity;

        asset.NormalDepreciation = roundedNormal;
        asset.AdditionalDepreciation = roundedAdditional;
        asset.CurrentDepreciation = Math.Min(maximumDepreciable, roundedNormal + roundedAdditional);
        asset.CurrentBookValue = Math.Round(Math.Max(0m, opening + additions - deletions - asset.CurrentDepreciation), 2, MidpointRounding.AwayFromZero);

        if (IsFixedCommonPolicy)
        {
            asset.DepreciationMethod = DepreciationMethod;
            if (asset.DepreciationRate <= 0m && CommonDepreciationRate > 0m)
                asset.DepreciationRate = CommonDepreciationRate;
            asset.PolicySource = asset.PolicySource.Length > 0 ? asset.PolicySource : "Fixed/Common method policy";
        }
    }

    private decimal ApplyVoucherRoundOff(decimal amount)
    {
        // FIXED41 R8: user-facing Round Off is intentionally binary. When enabled,
        // the new depreciation adjustment is rounded to the nearest rupee only.
        if (!VoucherRoundOffEnabled) return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        return Math.Round(amount, 0, MidpointRounding.AwayFromZero);
    }

    private int ResolveDepreciationRoundingDigits()
    {
        var provedTallyDepreciation = _ledgerSnapshots.Values
            .Select(snapshot => snapshot.PostedDepreciation)
            .Where(amount => amount > 0.005m)
            .ToArray();
        if (provedTallyDepreciation.Length == 0) return 2;

        // Match the linked company's proved depreciation posting precision instead of
        // creating paise-only differences from reverse-calculated rates. If every proved
        // Tally depreciation posting is a whole rupee, calculate/post each Fixed Asset
        // ledger to the nearest rupee before totals and closing balances are derived.
        return provedTallyDepreciation.All(amount =>
            Math.Abs(amount - Math.Round(amount, 0, MidpointRounding.AwayFromZero)) <= 0.0001m)
            ? 0
            : 2;
    }

    private static decimal RoundDepreciationAmount(decimal amount, int digits) =>
        Math.Round(Math.Max(0m, amount), digits, MidpointRounding.AwayFromZero);

    private void RecalculateEditingPreview()
    {
        var clone = CloneAsset(EditingAsset);
        if (clone.OpeningBalance <= 0m && clone.PurchaseCost > 0m)
            clone.OpeningBalance = clone.PurchaseCost;
        Recalculate(clone);
        EditingPreviewDepreciation = clone.CurrentDepreciation;
        EditingPreviewClosing = clone.CurrentBookValue;
    }

    private (string Method, decimal Rate) ResolveMethodAndRate(FixedAssetRecord asset)
    {
        if (IsFixedCommonPolicy)
        {
            var assetRate = Math.Max(0m, asset.DepreciationRate);
            return (string.IsNullOrWhiteSpace(DepreciationMethod) ? "SLM" : DepreciationMethod,
                assetRate > 0m ? assetRate : CommonDepreciationRate);
        }
        return (string.IsNullOrWhiteSpace(asset.DepreciationMethod) ? "SLM" : asset.DepreciationMethod,
            Math.Max(0m, asset.DepreciationRate));
    }

    private FixedAssetLedgerSnapshot? FindSnapshot(FixedAssetRecord asset)
    {
        _ledgerSnapshots.TryGetValue(LedgerIdentityKey(asset.TallyLedgerMasterId, asset.TallyLedgerName.Length > 0 ? asset.TallyLedgerName : asset.AssetName), out var snapshot);
        return snapshot;
    }

    private FixedAssetPostingMappingRow? FindPostingMapping(FixedAssetRecord asset)
    {
        var key = LedgerIdentityKey(asset.TallyLedgerMasterId, asset.TallyLedgerName.Length > 0 ? asset.TallyLedgerName : asset.AssetName);
        return PostingMappings.FirstOrDefault(row => LedgerIdentityKey(row.AssetLedgerMasterId, row.AssetLedgerName)
            .Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private void EditingAsset_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RecalculateEditingPreview();

    private string ResolveLedgerMasterId(string ledgerName)
    {
        var key = LedgerNameKey(ledgerName);
        return key.Length > 0 && _ledgerOptions.TryGetValue(key, out var option) ? option.MasterId : string.Empty;
    }

    private static bool Equivalent(string left, string right) =>
        LedgerNameKey(left).Equals(LedgerNameKey(right), StringComparison.OrdinalIgnoreCase);

    private static string LedgerNameKey(string value) =>
        new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string LedgerIdentityKey(string masterId, string name)
    {
        var id = (masterId ?? string.Empty).Trim();
        return id.Length > 0 ? $"MID:{id.ToUpperInvariant()}" : $"NAME:{LedgerNameKey(name)}";
    }

    private static decimal DecimalDays(DateTime start, DateTime end, int yearDays)
    {
        if (end < start) return 0m;
        return Math.Clamp(((decimal)((end - start).Days + 1)) / yearDays, 0m, 1m);
    }

    private static (DateTime From, DateTime To) FinancialYearBounds(string financialYear)
    {
        var parts = (financialYear ?? string.Empty)
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startYear) &&
            startYear is >= 1900 and <= 9998)
            return (new DateTime(startYear, 4, 1), new DateTime(startYear + 1, 3, 31));
        var today = DateTime.Today;
        var fallbackStartYear = today.Month >= 4 ? today.Year : today.Year - 1;
        return (new DateTime(fallbackStartYear, 4, 1), new DateTime(fallbackStartYear + 1, 3, 31));
    }

    private void RaiseSummaryChanged()
    {
        OnPropertyChanged(nameof(AssetCount));
        OnPropertyChanged(nameof(TotalOpeningBalance));
        OnPropertyChanged(nameof(TotalAdditions));
        OnPropertyChanged(nameof(TotalDeletions));
        OnPropertyChanged(nameof(TotalDepreciation));
        OnPropertyChanged(nameof(TotalPurchaseCost));
        OnPropertyChanged(nameof(TotalBookValue));
        OnPropertyChanged(nameof(DiscoveryCandidateCount));
        OnPropertyChanged(nameof(FixedAssetLedgerCount));
        OnPropertyChanged(nameof(CompleteAuthorityCount));
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        NewAssetCommand.RaiseCanExecuteChanged();
        SaveAssetCommand.RaiseCanExecuteChanged();
        EditAssetCommand.RaiseCanExecuteChanged();
        DeleteAssetCommand.RaiseCanExecuteChanged();
        SaveDepreciationSettingsCommand.RaiseCanExecuteChanged();
        GenerateVoucherCommand.RaiseCanExecuteChanged();
        CreateExpenseLedgerCommand.RaiseCanExecuteChanged();
        CreateAccumulatedLedgerCommand.RaiseCanExecuteChanged();
    }

    private static FixedAssetRecord CloneAsset(FixedAssetRecord asset) => new()
    {
        Id = asset.Id,
        ClientId = asset.ClientId,
        FinancialYear = asset.FinancialYear,
        AssetCode = asset.AssetCode,
        AssetName = asset.AssetName,
        AssetGroup = asset.AssetGroup,
        PurchaseDate = asset.PurchaseDate,
        PutToUseDate = asset.PutToUseDate,
        PurchaseCost = asset.PurchaseCost,
        ResidualValue = asset.ResidualValue,
        DepreciationMethod = asset.DepreciationMethod,
        DepreciationRate = asset.DepreciationRate,
        OpeningAccumulatedDepreciation = asset.OpeningAccumulatedDepreciation,
        DisposalDate = asset.DisposalDate,
        DisposalValue = asset.DisposalValue,
        Location = asset.Location,
        SerialNumber = asset.SerialNumber,
        Notes = asset.Notes,
        UpdatedUtc = asset.UpdatedUtc,
        CurrentBookValue = asset.CurrentBookValue,
        TallyLedgerName = asset.TallyLedgerName,
        TallyLedgerMasterId = asset.TallyLedgerMasterId,
        SupplierName = asset.SupplierName,
        OpeningBalance = asset.OpeningBalance,
        GrossOpeningBalance = asset.GrossOpeningBalance,
        OpeningSource = asset.OpeningSource,
        CurrentAdditions = asset.CurrentAdditions,
        CurrentDeletions = asset.CurrentDeletions,
        NormalDepreciation = asset.NormalDepreciation,
        AdditionalDepreciation = asset.AdditionalDepreciation,
        CurrentDepreciation = asset.CurrentDepreciation,
        PostedDepreciation = asset.PostedDepreciation,
        PolicySource = asset.PolicySource,
        DetectionStatus = asset.DetectionStatus,
        IsTransientTallyRow = asset.IsTransientTallyRow
    };
}

public sealed class FixedAssetPostingMappingRow : ViewModelBase
{
    private string _accumulatedLedgerName;
    private string _accumulatedLedgerMasterId;

    public FixedAssetPostingMappingRow(
        string assetLedgerName,
        string assetLedgerMasterId,
        string accumulatedLedgerName,
        string accumulatedLedgerMasterId)
    {
        AssetLedgerName = assetLedgerName ?? string.Empty;
        AssetLedgerMasterId = assetLedgerMasterId ?? string.Empty;
        _accumulatedLedgerName = accumulatedLedgerName ?? string.Empty;
        _accumulatedLedgerMasterId = accumulatedLedgerMasterId ?? string.Empty;
    }

    public string AssetLedgerName { get; }
    public string AssetLedgerMasterId { get; }
    public string AssetDisplay => string.IsNullOrWhiteSpace(AssetLedgerMasterId)
        ? AssetLedgerName
        : $"{AssetLedgerName} · MID {AssetLedgerMasterId}";

    public string AccumulatedLedgerName
    {
        get => _accumulatedLedgerName;
        set => SetProperty(ref _accumulatedLedgerName, value ?? string.Empty);
    }

    public string AccumulatedLedgerMasterId
    {
        get => _accumulatedLedgerMasterId;
        set => SetProperty(ref _accumulatedLedgerMasterId, value ?? string.Empty);
    }
}
