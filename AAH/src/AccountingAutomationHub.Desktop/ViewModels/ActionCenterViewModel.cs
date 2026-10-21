using System.Collections.ObjectModel;
using System.Diagnostics;
using AccountingAutomationHub.Application.Clients;
using AccountingAutomationHub.Application.Imports;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Imports;
using AccountingAutomationHub.Infrastructure.Spreadsheets;
using AccountingAutomationHub.Infrastructure.Imports;
using AccountingAutomationHub.Infrastructure.Sales;
using AccountingAutomationHub.Infrastructure.Automation;
using AccountingAutomationHub.Infrastructure.Banking;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class ActionCenterViewModel : PageViewModel
{
    private readonly ActiveClientContext _clientContext;
    private readonly IActionRequiredRepository _repository;
    private readonly IClientRepository _clientRepository;
    private readonly BackgroundAutomationService _automationService;
    private readonly List<ActionRequiredItem> _allOpen = [];

    private ActionRequiredItem? _selectedAction;
    private string _selectedModule = "All Modules";
        private string _searchText = string.Empty;
    private string _statusText = "No open actions.";
    private long _preparedClientId;
    private string _preparedFinancialYear = string.Empty;
    private readonly ImportHeaderInspectionService _headerInspectionService = new();
    private string _selectedHeaderMappingModule = ImportHeaderMappingCatalog.Banking;
    private string _headerMappingStatus = "Select a spreadsheet Action Required item to inspect its headers.";
    private string _actionHeaderSearchText = string.Empty;
    private bool _hasHeaderMappingPanel;
    private bool _headerAutoRetryBusy;
    private string _lastAutoRetryPath = string.Empty;

    public ActionCenterViewModel(
        ActiveClientContext clientContext,
        IActionRequiredRepository repository,
        IClientRepository clientRepository,
        BackgroundAutomationService automationService)
        : base("Action Center", "See exactly what requires attention for the current client and Financial Year")
    {
        _clientContext = clientContext;
        _repository = repository;
        _clientRepository = clientRepository;
        _automationService = automationService;

        RefreshCommand = new RelayCommand(Refresh);
        OpenModuleCommand = new RelayCommand(OpenSelectedModule, () => SelectedAction is not null);
        OpenSourceCommand = new RelayCommand(OpenSelectedSource, () => HasSelectedSource);
        RetryCommand = new AsyncRelayCommand(RetrySelectedAsync, () => CanRetrySelected);
        RetryAsBankCommand = new AsyncRelayCommand(() => RetrySelectedAsAsync("Bank"), () => CanRetrySelected);
        RetryAsSalesCommand = new AsyncRelayCommand(() => RetrySelectedAsAsync("Sales"), () => CanRetrySelected);
        RetryAsPurchaseCommand = new AsyncRelayCommand(() => RetrySelectedAsAsync("Purchase"), () => CanRetrySelected);
        ReimportAllCommand = new AsyncRelayCommand(ReimportAllAsync, () => _allOpen.Count > 0);
        ClearAllCommand = new RelayCommand(ClearAll, () => _clientContext.CurrentClient is not null && _allOpen.Count > 0);
        ResolveCommand = new RelayCommand(ResolveSelected, () => SelectedAction is not null);

        _clientContext.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActiveClientContext.CurrentClient))
            {
                OnPropertyChanged(nameof(CurrentClientScopeText));
                var client = _clientContext.CurrentClient;
                if (client is null)
                {
                    _preparedClientId = 0; _preparedFinancialYear = string.Empty;
                    _allOpen.Clear(); Actions.Clear(); ModuleOptions.Clear(); ModuleOptions.Add("All Modules");
                    StatusText = "Open a client workspace to view its failed/blocked imports.";
                }
                else if (_preparedClientId != client.Id || !_preparedFinancialYear.Equals(client.FinancialYear,StringComparison.OrdinalIgnoreCase))
                {
                    _allOpen.Clear(); Actions.Clear(); ModuleOptions.Clear(); ModuleOptions.Add("All Modules");
                    StatusText = "Action Center cache is preparing in the background…";
                }
            }
        };
        Refresh();
    }

    public event EventHandler<ActionRequiredItem>? OpenActionRequested;
    public event EventHandler? ActionAvailabilityChanged;

    public ObservableCollection<ActionRequiredItem> Actions { get; } = [];
    public ObservableCollection<string> ModuleOptions { get; } = ["All Modules"];
    public ObservableCollection<string> HeaderMappingModuleOptions { get; } = new(ImportHeaderMappingCatalog.Modules);
    public ObservableCollection<ActionHeaderCanonicalOption> HeaderCanonicalOptions { get; } = [];
    public ObservableCollection<ActionMatchedHeaderRow> MatchedHeaders { get; } = [];
    public ObservableCollection<ActionUnmatchedHeaderRow> UnmatchedHeaders { get; } = [];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenModuleCommand { get; }
    public RelayCommand OpenSourceCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public AsyncRelayCommand RetryAsBankCommand { get; }
    public AsyncRelayCommand RetryAsSalesCommand { get; }
    public AsyncRelayCommand RetryAsPurchaseCommand { get; }
    public AsyncRelayCommand ReimportAllCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public RelayCommand ResolveCommand { get; }

    public string SelectedModule
    {
        get => _selectedModule;
        set
        {
            if (SetProperty(ref _selectedModule, value ?? "All Modules")) ApplyFilter();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty)) ApplyFilter();
        }
    }

    public ActionRequiredItem? SelectedAction
    {
        get => _selectedAction;
        set
        {
            if (!SetProperty(ref _selectedAction, value)) return;
            OnPropertyChanged(nameof(SelectedDetail));
            OnPropertyChanged(nameof(SelectedSourcePath));
            OnPropertyChanged(nameof(SelectedClientText));
            OnPropertyChanged(nameof(HasSelectedSource));
            OnPropertyChanged(nameof(CanRetrySelected));
            OpenModuleCommand.RaiseCanExecuteChanged();
            OpenSourceCommand.RaiseCanExecuteChanged();
            RetryCommand.RaiseCanExecuteChanged();
            RetryAsBankCommand.RaiseCanExecuteChanged();
            RetryAsSalesCommand.RaiseCanExecuteChanged();
            RetryAsPurchaseCommand.RaiseCanExecuteChanged();
            ResolveCommand.RaiseCanExecuteChanged();
            PrepareHeaderMappingForSelectedAction();
        }
    }

    public string SelectedHeaderMappingModule
    {
        get => _selectedHeaderMappingModule;
        set
        {
            var normalized = ImportHeaderMappingCatalog.NormalizeModule(value);
            if (!ImportHeaderMappingCatalog.Modules.Contains(normalized, StringComparer.OrdinalIgnoreCase)) normalized = ImportHeaderMappingCatalog.Banking;
            if (!SetProperty(ref _selectedHeaderMappingModule, normalized)) return;
            RebuildHeaderCanonicalOptions();
            InspectSelectedHeaders();
        }
    }

    public string ActionHeaderSearchText
    {
        get => _actionHeaderSearchText;
        set
        {
            if (SetProperty(ref _actionHeaderSearchText, value ?? string.Empty)) RebuildHeaderCanonicalOptions();
        }
    }

    public string HeaderMappingStatus
    {
        get => _headerMappingStatus;
        private set => SetProperty(ref _headerMappingStatus, value ?? string.Empty);
    }

    public bool HasHeaderMappingPanel
    {
        get => _hasHeaderMappingPanel;
        private set => SetProperty(ref _hasHeaderMappingPanel, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SelectedDetail => SelectedAction?.Detail ?? "Select a failed/blocked import to see why the file was not imported.";
    public string SelectedSourcePath => ExtractSourcePath(SelectedAction?.Detail);
    public string SelectedClientText => SelectedAction is null
        ? string.Empty
        : $"{SelectedAction.ClientName} • FY {SelectedAction.FinancialYear}";
    public string CurrentClientScopeText => _clientContext.CurrentClient is null
        ? "Open a client workspace to view its Action Required items."
        : $"Client-specific queue: {_clientContext.CurrentClient.ClientName} • FY {_clientContext.CurrentClient.FinancialYear}.";
    public bool HasSelectedSource => SelectedSourcePath.Length > 0 && (File.Exists(SelectedSourcePath) || Directory.Exists(SelectedSourcePath));
    public bool CanRetrySelected => SelectedAction is not null && SelectedSourcePath.Length > 0 && File.Exists(SelectedSourcePath);
    public int OpenBlockingCount => _allOpen.Count;

    public void SelectModule(string? module)
    {
        if (string.IsNullOrWhiteSpace(module)) return;
        var match = ModuleOptions.FirstOrDefault(option => option.Equals(module, StringComparison.OrdinalIgnoreCase));
        SelectedModule = match ?? "All Modules";
    }

    public void WarmFromPreparedSnapshot(ClientWorkspaceSnapshot snapshot)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != snapshot.ClientId ||
            !client.FinancialYear.Equals(snapshot.FinancialYear,StringComparison.OrdinalIgnoreCase)) return;

        var selectedId = SelectedAction?.PersistentId;
        _allOpen.Clear();
        foreach (var item in snapshot.OpenActions
                     .Where(ActionRequiredPolicy.IsImportBlocking)
                     .Where(item => !IsPipelineGeneratedPdfArtifact(ExtractSourcePath(item.Detail)))
                     .Where(item => !ClientDiagnosticExportService.IsHubGeneratedExportPath(ExtractSourcePath(item.Detail)))
                     .Where(item => !ClientDiagnosticExportService.IsHubGeneratedExportPath(item.Title))
                     .Where(item => !LocalPasswordCandidateDiscovery.IsLikelyPasswordCompanionFile(ExtractSourcePath(item.Detail))))
        {
            item.ClientName = string.IsNullOrWhiteSpace(client.ClientName) ? client.ClientCode : client.ClientName;
            _allOpen.Add(item);
        }

        ModuleOptions.Clear(); ModuleOptions.Add("All Modules");
        foreach (var module in _allOpen.Select(item => item.Module).Where(value => value.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value)) ModuleOptions.Add(module);
        if (!ModuleOptions.Contains(SelectedModule)) _selectedModule = "All Modules";
        OnPropertyChanged(nameof(SelectedModule));
        _preparedClientId = client.Id; _preparedFinancialYear = client.FinancialYear;
        ApplyFilter(selectedId);
    }

    // R94 R1: recover legacy Bank OCR failures without modifying the hash-protected
    // BackgroundAutomationService or its live Tally/ALTERID synchronization authority.
    // A successful Bank import, a changed source or an unrelated Client/FY is excluded.
    private void RestoreMissingBankOcrActions(long clientId, string financialYear)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear)) return;
        try
        {
            var history = _automationService.GetClientHistory(clientId, financialYear, 5000)
                .Where(record => record.FileType.Equals("Bank OCR", StringComparison.OrdinalIgnoreCase) &&
                                 record.Status.Equals("Needs Action", StringComparison.OrdinalIgnoreCase) &&
                                 !string.IsNullOrWhiteSpace(record.FileHash))
                .ToArray();
            if (history.Length == 0) return;

            var folders = _automationService.GetFolders()
                .Where(folder => folder.ClientId == clientId &&
                        folder.FinancialYear.Equals(financialYear, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(folder => folder.FolderId, StringComparer.OrdinalIgnoreCase);
            if (folders.Count == 0) return;

            var automationRepository = new SqliteAutomationRepository();
            automationRepository.Initialize();
            var bankingRepository = new SqliteBankingRepository();
            bankingRepository.Initialize();
            var importedPaths = bankingRepository.GetStatements(clientId, financialYear)
                .Select(statement => NormalizeBankOcrSourcePath(statement.SourcePath))
                .Where(path => path.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var openKeys = _repository.GetOpen(clientId, financialYear, "Banking")
                .Select(action => action.SourceKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var record in history)
            {
                if (!folders.TryGetValue(record.FolderId, out var folder)) continue;
                var source = NormalizeBankOcrSourcePath(record.FilePath);
                var linkedPath = NormalizeBankOcrSourcePath(folder.FolderPath);
                if (source.Length == 0 || linkedPath.Length == 0 || !File.Exists(source) ||
                    !source.StartsWith(linkedPath.TrimEnd(Path.DirectorySeparatorChar) +
                                       Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    ClientDiagnosticExportService.IsHubGeneratedExportPath(source) ||
                    importedPaths.Contains(source) ||
                    !BankOcrSourceMatchesHistory(source, record.FileHash)) continue;

                // A later completed import/duplicate for the same verified hash must win.
                var processed = automationRepository.GetProcessedFile(folder.FolderId, clientId, record.FileHash);
                if (processed is not null &&
                    (processed.Status.Equals("Imported", StringComparison.OrdinalIgnoreCase) ||
                     processed.Status.Equals("Duplicate", StringComparison.OrdinalIgnoreCase))) continue;
                var sourceKey = $"AUTOMATION:{record.FileHash}";
                if (openKeys.Contains(sourceKey) ||
                    !ActionRequiredPolicy.IsImportBlockingType("Bank OCR Review")) continue;

                _repository.Upsert(new ActionRequiredItem
                {
                    ClientId = clientId,
                    FinancialYear = financialYear,
                    Module = "Banking",
                    ActionType = "Bank OCR Review",
                    Title = Path.GetFileName(source),
                    Detail = $"{record.Detail}\nSource: {source}",
                    SourceKey = sourceKey
                });
                openKeys.Add(sourceKey);
            }
        }
        catch (Exception exception)
        {
            // A review list failure never blocks application startup or Bank import.
            Debug.WriteLine("Bank OCR Action Required recovery failed: " + exception);
        }
    }

    // R94 R2: a historical Needs Action row must refer to the same physical
    // document bytes. An edited/replaced PDF never revives an old OCR review.
    private static bool BankOcrSourceMatchesHistory(string source, string recordedHash)
    {
        if (string.IsNullOrWhiteSpace(recordedHash) || recordedHash.Length != 64)
            return false;
        try
        {
            using var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var currentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            return currentHash.Equals(recordedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string NormalizeBankOcrSourcePath(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path.Trim());
        }
        catch { return string.Empty; }
    }

    public void Refresh()
    {
        var selectedId = SelectedAction?.PersistentId;
        var client = _clientContext.CurrentClient;
        _allOpen.Clear();

        if (client is null)
        {
            ModuleOptions.Clear();
            ModuleOptions.Add("All Modules");
            Actions.Clear();
            SelectedAction = null;
            StatusText = "Open a client workspace to view its failed/blocked imports.";
            ReimportAllCommand.RaiseCanExecuteChanged();
            ClearAllCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CurrentClientScopeText));
            return;
        }

        // Restore previously hidden Bank OCR failures from exact same-source history.
        RestoreMissingBankOcrActions(client.Id, client.FinancialYear);
        var openItems = _repository.GetOpen(client.Id, client.FinancialYear);

        // Self-heal old rows that are not failed/blocked imports.
        foreach (var legacyReview in openItems.Where(item => !ActionRequiredPolicy.IsImportBlocking(item)))
        {
            try { _repository.Resolve(legacyReview.PersistentId); }
            catch { }
        }

        // The external PDF pipeline may create helper/confirmation PDFs. These are
        // never separate accounting sources and must not appear in Action Center.
        foreach (var generatedPdf in openItems.Where(item => IsPipelineGeneratedPdfArtifact(ExtractSourcePath(item.Detail))))
        {
            try { _repository.Resolve(generatedPdf.PersistentId); }
            catch { }
        }

        // Password companion files are helper inputs consumed locally when a protected
        // workbook is opened. Older builds could classify those helpers as Unknown/Unsupported;
        // self-heal those stale rows so they disappear from Action Center automatically.
        foreach (var passwordHelper in openItems.Where(item =>
                     LocalPasswordCandidateDiscovery.IsLikelyPasswordCompanionFile(ExtractSourcePath(item.Detail))))
        {
            try { _repository.Resolve(passwordHelper.PersistentId); }
            catch { }
        }

        // Hub-generated review/diagnostic Excel and CSV files are outputs, not import
        // sources. Self-heal any stale Action Required rows created by older builds.
        foreach (var generatedExport in openItems.Where(item =>
                     ClientDiagnosticExportService.IsHubGeneratedExportPath(ExtractSourcePath(item.Detail)) ||
                     ClientDiagnosticExportService.IsHubGeneratedExportPath(item.Title)))
        {
            try { _repository.Resolve(generatedExport.PersistentId); }
            catch { }
        }

        foreach (var item in openItems
                     .Where(ActionRequiredPolicy.IsImportBlocking)
                     .Where(item => !IsPipelineGeneratedPdfArtifact(ExtractSourcePath(item.Detail)))
                     .Where(item => !ClientDiagnosticExportService.IsHubGeneratedExportPath(ExtractSourcePath(item.Detail)))
                     .Where(item => !ClientDiagnosticExportService.IsHubGeneratedExportPath(item.Title))
                     .Where(item => !LocalPasswordCandidateDiscovery.IsLikelyPasswordCompanionFile(ExtractSourcePath(item.Detail))))
        {
            item.ClientName = string.IsNullOrWhiteSpace(client.ClientName) ? client.ClientCode : client.ClientName;
            _allOpen.Add(item);
        }

        ModuleOptions.Clear();
        ModuleOptions.Add("All Modules");
        foreach (var module in _allOpen.Select(item => item.Module).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value))
            ModuleOptions.Add(module);

        if (!ModuleOptions.Contains(SelectedModule)) _selectedModule = "All Modules";
        OnPropertyChanged(nameof(SelectedModule));
        ApplyFilter(selectedId);
    }

    private void ApplyFilter(long? preferredSelectedId = null)
    {
        var selectedId = preferredSelectedId ?? SelectedAction?.PersistentId;
        var module = SelectedModule;
        var search = SearchText.Trim();

        var filtered = _allOpen.Where(item =>
            (module.Equals("All Modules", StringComparison.OrdinalIgnoreCase) || item.Module.Equals(module, StringComparison.OrdinalIgnoreCase)) &&
            (search.Length == 0 ||
             item.ClientName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             item.FinancialYear.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             item.Module.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             item.ActionType.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             item.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             item.Detail.Contains(search, StringComparison.OrdinalIgnoreCase)));

        Actions.Clear();
        foreach (var item in filtered.OrderBy(item => item.CreatedUtc).ThenBy(item => item.PersistentId)) Actions.Add(item);
        SelectedAction = selectedId.HasValue
            ? Actions.FirstOrDefault(item => item.PersistentId == selectedId.Value) ?? Actions.FirstOrDefault()
            : Actions.FirstOrDefault();

        StatusText = $"{Actions.Count:N0} visible failed/blocked import(s) • {_allOpen.Count:N0} total for this client/FY.";
        ReimportAllCommand.RaiseCanExecuteChanged();
        ClearAllCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CurrentClientScopeText));
    }

    private void PrepareHeaderMappingForSelectedAction()
    {
        // A completed/failed retry may reopen the same physical source as a fresh Action Required row.
        // Keep the anti-double-queue guard scoped to the current selection session, not permanently to the file path.
        _lastAutoRetryPath = string.Empty;
        MatchedHeaders.Clear();
        UnmatchedHeaders.Clear();
        HeaderCanonicalOptions.Clear();
        var path = SelectedSourcePath;
        var canInspect = SelectedAction is not null && path.Length > 0 && File.Exists(path) && IsSpreadsheetLike(path) && IsHeaderMappingAction(SelectedAction);
        HasHeaderMappingPanel = canInspect;
        if (!canInspect)
        {
            HeaderMappingStatus = "Header mapping is available for spreadsheet / delimited-text import actions.";
            return;
        }

        var suggestedModule = ImportHeaderMappingCatalog.NormalizeModule(SelectedAction?.Module);
        if (!ImportHeaderMappingCatalog.Modules.Contains(suggestedModule, StringComparer.OrdinalIgnoreCase))
        {
            var title = (SelectedAction?.Title ?? string.Empty) + " " + (SelectedAction?.Detail ?? string.Empty);
            suggestedModule = ImportHeaderMappingCatalog.Modules.FirstOrDefault(module => title.Contains(module, StringComparison.OrdinalIgnoreCase))
                              ?? ImportHeaderMappingCatalog.Banking;
        }
        _selectedHeaderMappingModule = suggestedModule;
        OnPropertyChanged(nameof(SelectedHeaderMappingModule));

        // R9 FIXED16 R13: the supplied Crystal/Tally Sales Register summaries are
        // direct date + voucher-range GST-rate evidence. They are not generic Sales
        // header-mapping sources, so never present an empty temporary mapping grid.
        if (suggestedModule.Equals(ImportHeaderMappingCatalog.Sales, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var salesInspection = new SalesManualImportService().Inspect(path);
                if (salesInspection.IsTallyRateSummaryMapping)
                {
                    HasHeaderMappingPanel = false;
                    HeaderMappingStatus =
                        $"Direct Tally Sales Register mapping recognized ({salesInspection.MappingSummary}). Manual Header Mapping is not required. " +
                        "Retry/Reimport All sends this file straight to the Sales rate-evidence importer without creating duplicate Sales vouchers.";
                    return;
                }
            }
            catch
            {
                // Keep the normal one-file Header Mapping UI for every non-summary or
                // unreadable workbook. The actual retry remains fail-closed.
            }
        }

        RebuildHeaderCanonicalOptions();
        InspectSelectedHeaders();
    }

    private void RebuildHeaderCanonicalOptions()
    {
        HeaderCanonicalOptions.Clear();
        var search = ActionHeaderSearchText.Trim();
        foreach (var field in ImportHeaderMappingCatalog.GetFields(SelectedHeaderMappingModule)
                     .Where(field => search.Length == 0 || field.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) || field.Key.Contains(search, StringComparison.OrdinalIgnoreCase)))
            HeaderCanonicalOptions.Add(new ActionHeaderCanonicalOption(field.Key, field.DisplayName, field.Required));
    }

    private void InspectSelectedHeaders()
    {
        MatchedHeaders.Clear();
        UnmatchedHeaders.Clear();
        if (!HasHeaderMappingPanel || SelectedSourcePath.Length == 0) return;
        try
        {
            var inspection = _headerInspectionService.Inspect(SelectedSourcePath, SelectedHeaderMappingModule);
            foreach (var column in inspection.Columns)
            {
                if (column.IsMatched)
                {
                    MatchedHeaders.Add(new ActionMatchedHeaderRow
                    {
                        ColumnIndex = column.ColumnIndex,
                        ColumnLabel = column.ColumnLabel,
                        SourceHeader = column.SourceHeader,
                        CanonicalField = column.CanonicalField,
                        CanonicalDisplay = column.CanonicalDisplay,
                        MatchType = column.MatchType
                    });
                }
                else
                {
                    UnmatchedHeaders.Add(new ActionUnmatchedHeaderRow
                    {
                        ColumnIndex = column.ColumnIndex,
                        ColumnLabel = column.ColumnLabel,
                        SourceHeader = column.SourceHeader,
                        SelectionChanged = EvaluateTemporaryHeaderMappings
                    });
                }
            }
            HeaderMappingStatus = inspection.Detail + (inspection.MissingRequired.Count == 0
                ? " Required headers are already complete."
                : $" Missing required: {string.Join(", ", inspection.MissingRequired)}.");
            EvaluateTemporaryHeaderMappings();
        }
        catch (Exception exception)
        {
            HeaderMappingStatus = $"Header inspection could not read this source: {exception.Message}";
        }
    }

    private void EvaluateTemporaryHeaderMappings()
    {
        if (_headerAutoRetryBusy || !HasHeaderMappingPanel || SelectedAction is null) return;
        var canonical = MatchedHeaders.Select(item => item.CanonicalField)
            .Concat(UnmatchedHeaders.Where(item => item.SelectedCanonical is not null).Select(item => item.SelectedCanonical!.Key))
            .ToArray();
        var missing = ImportHeaderMappingCatalog.GetMissingRequiredDescriptions(SelectedHeaderMappingModule, canonical);
        if (missing.Count > 0)
        {
            HeaderMappingStatus = $"Temporary mapping for this file only. Resolve required header(s): {string.Join(", ", missing)}.";
            return;
        }

        var duplicateTarget = UnmatchedHeaders.Where(item => item.SelectedCanonical is not null)
            .GroupBy(item => item.SelectedCanonical!.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTarget is not null)
        {
            HeaderMappingStatus = $"Two source columns are assigned to {duplicateTarget.First().SelectedCanonical!.DisplayName}. Choose one source column for that AAH header.";
            return;
        }

        var path = SelectedSourcePath;
        if (path.Length == 0 || _lastAutoRetryPath.Equals(path, StringComparison.OrdinalIgnoreCase)) return;
        var temporary = UnmatchedHeaders.Where(item => item.SelectedCanonical is not null)
            .ToDictionary(item => item.SourceHeader, item => item.SelectedCanonical!.Key, StringComparer.OrdinalIgnoreCase);
        _headerAutoRetryBusy = true;
        try
        {
            var queued = _automationService.RetryFileWithTemporaryHeaders(
                SelectedAction.ClientId,
                SelectedAction.FinancialYear,
                path,
                SelectedHeaderMappingModule,
                temporary);
            if (queued)
            {
                _lastAutoRetryPath = path;
                HeaderMappingStatus = "All required headers are matched. AAH automatically queued the normal importer with this one-file mapping; the temporary mapping is disposed after this import attempt.";
            }
            else HeaderMappingStatus = "All required headers are matched, but the file could not be queued from its linked Automation Folder.";
        }
        finally { _headerAutoRetryBusy = false; }
    }

    private static bool IsHeaderMappingAction(ActionRequiredItem action)
    {
        var text = $"{action.ActionType} {action.Title} {action.Detail}";
        return text.Contains("header", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("column", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("no usable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("could not be mapped", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unknown format", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("required field", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSpreadsheetLike(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".xlsx" or ".xlsm" or ".xls" or ".csv" or ".tsv" or ".txt";
    }

    private void ClearAll()
    {
        var client = _clientContext.CurrentClient;
        if (client is null || _allOpen.Count == 0) return;

        var answer = System.Windows.MessageBox.Show(
            $"Clear all {_allOpen.Count:N0} Action Required item(s) for {client.ClientName} • FY {client.FinancialYear}?\n\nThis marks the Action Required queue resolved only. It does not delete accounting data, imported files, mappings or notification history.",
            "Clear All Action Required",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        var cleared = _repository.ResolveAll(client.Id, client.FinancialYear);
        Refresh();
        StatusText = $"Cleared {cleared:N0} Action Required item(s) for {client.ClientName} • FY {client.FinancialYear}. Accounting data and source files were retained.";
        ActionAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenSelectedModule()
    {
        if (SelectedAction is null) return;
        OpenActionRequested?.Invoke(this, SelectedAction);
    }

    private void OpenSelectedSource()
    {
        var path = SelectedSourcePath;
        if (path.Length == 0) return;
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            StatusText = "The source path could not be opened.";
        }
    }


    private async Task RetrySelectedAsync()
    {
        if (SelectedAction is null || !File.Exists(SelectedSourcePath)) return;

        var selected = SelectedAction;
        var sourcePath = SelectedSourcePath;
        if (Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
            selected.Module.Equals("Banking", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = $"Running the external Python pipeline interactively for {Path.GetFileName(sourcePath)}. Complete any Python mapping/password/line-confirmation window that appears…";
            var result = await _automationService.ResolvePdfAndImportInteractivelyAsync(
                selected.ClientId,
                selected.FinancialYear,
                sourcePath);
            StatusText = result.WasSuccessful ? result.Message : $"PDF retry did not complete: {result.Message}";
            Refresh();
            ActionAvailabilityChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var queued = _automationService.RetryFile(selected.ClientId, selected.FinancialYear, sourcePath);
        StatusText = queued
            ? $"Retry queued for {Path.GetFileName(sourcePath)}. The action closes automatically after a successful import."
            : "The source is not inside a currently linked Automation Folder for this client/FY.";
    }

    private async Task RetrySelectedAsAsync(string accountingType)
    {
        if (SelectedAction is null || !File.Exists(SelectedSourcePath)) return;

        var selected = SelectedAction;
        var sourcePath = SelectedSourcePath;
        if (accountingType.Equals("Bank", StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = $"Running the external Python pipeline interactively for {Path.GetFileName(sourcePath)}. Complete any Python mapping/password/line-confirmation window that appears…";
            var result = await _automationService.ResolvePdfAndImportInteractivelyAsync(
                selected.ClientId,
                selected.FinancialYear,
                sourcePath);
            StatusText = result.WasSuccessful ? result.Message : $"PDF retry did not complete: {result.Message}";
            Refresh();
            ActionAvailabilityChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var queued = _automationService.RetryFileAs(selected.ClientId, selected.FinancialYear, sourcePath, accountingType);
        StatusText = queued
            ? $"Saved this format as {accountingType} and queued Retry Import. Future files with the same learned header signature use this type automatically."
            : "The type override could not be saved or the file is not inside a linked Automation Folder.";
    }

    private async Task ReimportAllAsync()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        var pending = _allOpen
            .Where(item => ActionRequiredPolicy.IsImportBlocking(item))
            .Select(item => (Item: item, Source: ExtractSourcePath(item.Detail)))
            .Where(entry => entry.Source.Length > 0 && File.Exists(entry.Source))
            .ToList();

        if (pending.Count == 0)
        {
            StatusText = "There are no available source files to reimport for this client/FY.";
            return;
        }

        var queued = 0;
        var completed = 0;
        var failed = 0;
        foreach (var entry in pending)
        {
            var sourcePath = entry.Source;
            if (Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
                entry.Item.Module.Equals("Banking", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = $"Reimport All: running {Path.GetFileName(sourcePath)} ({completed + failed + 1:N0}/{pending.Count:N0})…";
                var result = await _automationService.ResolvePdfAndImportInteractivelyAsync(
                    client.Id,
                    client.FinancialYear,
                    sourcePath);
                if (result.WasSuccessful) completed++; else failed++;
            }
            else
            {
                if (_automationService.RetryFile(client.Id, client.FinancialYear, sourcePath)) queued++; else failed++;
            }
        }

        Refresh();
        StatusText = $"Reimport All requested for {pending.Count:N0} file(s): {completed:N0} completed immediately, {queued:N0} queued, {failed:N0} could not be queued/imported.";
        ActionAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResolveSelected()
    {
        if (SelectedAction is null) return;
        _repository.Resolve(SelectedAction.PersistentId);
        Refresh();
        ActionAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }


    private static bool IsPipelineGeneratedPdfArtifact(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return false;
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.EndsWith("_CONFIRMED_LINED", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith("_CONFIRMED_LINE", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith("_CONFIRMED", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractSourcePath(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return string.Empty;
        foreach (var rawLine in detail.Replace("\r", string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("Source:", StringComparison.OrdinalIgnoreCase)) continue;
            return line["Source:".Length..].Trim().Trim('"');
        }
        return string.Empty;
    }
}
