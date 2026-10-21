using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Collections;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Gst;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Infrastructure.Gst;
using AccountingAutomationHub.Infrastructure.Governance;
using AccountingAutomationHub.Infrastructure.Journals;

namespace AccountingAutomationHub.Desktop.ViewModels;

public sealed class GstReconciliationViewModel : PageViewModel
{
    private readonly SqliteGstRepository _repository;
    private readonly SqliteJournalRepository _journalRepository;
    private readonly Gstr3bPdfImportService _gstr3bImportService;
    private readonly GstLedgerImportService _ledgerImportService;
    private readonly GstPortalAutoImportService _autoImportService;
    private readonly AccountingRefreshCoordinator _refreshCoordinator;
    private readonly GstReconciliationService _reconciliationService;
    private readonly EReturnsRootContext _eReturnsRootContext;
    private readonly ActiveClientContext _clientContext;
    private readonly AccountingLiveChangeHub _liveChangeHub;
    private readonly BackgroundAutomationService? _automationService;
    private readonly SqliteGovernanceRepository _governanceRepository;

    private string _statusText = "GSTR-3B reconciliation ready.";
    private int _selectedTabIndex;
    private bool _isBusy;
    private string _reportingFrequency = "Monthly";
    private Gstr3bReturnRecord? _selectedReturn;
    private Gstr3bReturnRecord? _selectedSourceReturn;
    private string _selectedRawText = string.Empty;
    private bool _isPageActive;
    private long _preparedClientId;
    private string _preparedFinancialYear = string.Empty;

    public GstReconciliationViewModel(
        SqliteGstRepository repository,
        SqliteJournalRepository journalRepository,
        Gstr3bPdfImportService gstr3bImportService,
        GstLedgerImportService ledgerImportService,
        GstPortalAutoImportService autoImportService,
        AccountingRefreshCoordinator refreshCoordinator,
        GstReconciliationService reconciliationService,
        EReturnsRootContext eReturnsRootContext,
        ActiveClientContext clientContext,
        AccountingLiveChangeHub liveChangeHub,
        BackgroundAutomationService? automationService = null)
        : base(
            "GSTR-3B Reconciliation",
            "Workbook-parity reconciliation for GSTR-3B, Table 5/5.1, Table 6.1 and portal ledgers")
    {
        _repository = repository;
        _journalRepository = journalRepository;
        _gstr3bImportService = gstr3bImportService;
        _ledgerImportService = ledgerImportService;
        _autoImportService = autoImportService;
        _refreshCoordinator = refreshCoordinator;
        _reconciliationService = reconciliationService;
        _eReturnsRootContext = eReturnsRootContext;
        _clientContext = clientContext;
        _liveChangeHub = liveChangeHub;
        _automationService = automationService;
        _governanceRepository = new SqliteGovernanceRepository();
        _governanceRepository.Initialize();
        _clientContext.PropertyChanged += OnClientContextPropertyChanged;
        _eReturnsRootContext.PropertyChanged += OnEReturnsRootContextPropertyChanged;

        RefreshCommand = new AsyncRelayCommand(RefreshAccountingAsync, () => CurrentClientId > 0 && CanViewGst);
        AutoImportCommand = new AsyncRelayCommand(
            () => AutoImportPortalAsync(true),
            () => CurrentClientId > 0 && CanImportGst);
        ImportGstr3bCommand = new AsyncRelayCommand(ImportGstr3bAsync, () => CurrentClientId > 0 && CanImportGst);
        ImportCreditLedgerCommand = new AsyncRelayCommand(
            () => ImportLedgerAsync("Electronic Credit Ledger"),
            () => CurrentClientId > 0 && CanImportGst);
        ImportCashLedgerCommand = new AsyncRelayCommand(
            () => ImportLedgerAsync("Electronic Cash Ledger"),
            () => CurrentClientId > 0 && CanImportGst);
        ReconcileCommand = new AsyncRelayCommand(
            () => AutoImportPortalAsync(false),
            () => CurrentClientId > 0 && CanImportGst);
        ApplyNilSalesCommand = new AsyncRelayCommand(
            ApplyNilSalesSelectionsAsync,
            () => CurrentClientId > 0 && !IsBusy && CanControlGst);
        EditPeriodCommand = new AsyncRelayCommand(
            EditSelectedPeriodAsync,
            () => SelectedSourceReturn is not null && CurrentClientId > 0 && !IsBusy && CanControlGst);
        // Hidden module construction must be passive. BackgroundAutomationService
        // owns automatic folder/Downloads processing; this ViewModel imports only
        // from explicit GST commands while the page is active.
    }

    public void PrepareForDisplay()
    {
        _isPageActive = true;
        var client = _clientContext.CurrentClient;
        if (client is not null && (_preparedClientId != client.Id ||
            !_preparedFinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)))
            StatusText = "GST stream is preparing in the background…";
    }

    public void WarmFromPreparedSnapshot(ClientWorkspaceSnapshot snapshot)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != snapshot.ClientId ||
            !client.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;

        PaymentRows.Clear(); LiabilityBreakupRows.Clear(); RawFields.Clear();

        var detectedFrequency = snapshot.GstReturns.Any(record =>
                record.FilingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase) ||
                record.PeriodLabel.Contains(" to ", StringComparison.OrdinalIgnoreCase) ||
                record.PeriodLabel.Contains("-", StringComparison.OrdinalIgnoreCase))
            ? "Quarterly"
            : snapshot.GstReturns.Any(record => record.FilingFrequency.Contains("Month", StringComparison.OrdinalIgnoreCase))
                ? "Monthly"
                : string.Empty;
        var settingsFrequency = snapshot.GstJournalSettings?.FilingFrequency ?? string.Empty;
        ReportingFrequency = detectedFrequency.Length > 0
            ? detectedFrequency
            : string.IsNullOrWhiteSpace(settingsFrequency) ? "Monthly" : settingsFrequency;

        Rows.ReplaceRange(snapshot.GstReconciliation);
        SourceReturns.ReplaceRange(snapshot.GstReturns);
        Returns.ReplaceRange(GstReconciliationService.AggregateReturns(snapshot.GstReturns, ReportingFrequency));
        NilSalesOptions.ReplaceRange(snapshot.GstNilSalesOptions);
        LedgerPeriods.ReplaceRange(GstReconciliationService.AggregateLedgerPeriods(snapshot.GstLedgerPeriods, ReportingFrequency));
        SelectedReturn = Returns.FirstOrDefault(); SelectedSourceReturn = SourceReturns.FirstOrDefault();
        _preparedClientId = client.Id; _preparedFinancialYear = client.FinancialYear;
        StatusText = $"GSTR-3B ready • {Rows.Count:N0} reconciliation period(s) prepared in its own cache stream.";
        NotifySummary();
    }

    public void ReleaseForNavigation()
    {
        _isPageActive = false;
        // Completion 59: keep the last reconciled snapshot visible and warm.
    }

    public BulkObservableCollection<GstReconciliationRow> Rows { get; } = [];
    public BulkObservableCollection<Gstr3bReturnRecord> Returns { get; } = [];
    public BulkObservableCollection<Gstr3bReturnRecord> SourceReturns { get; } = [];
    public BulkObservableCollection<GstLedgerPeriodBalance> LedgerPeriods { get; } = [];
    public BulkObservableCollection<Gstr3bPaymentRow> PaymentRows { get; } = [];
    public BulkObservableCollection<Gstr3bLiabilityBreakupRow> LiabilityBreakupRows { get; } = [];
    public BulkObservableCollection<GstRawFieldRow> RawFields { get; } = [];
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, Math.Max(0, value));
    }

    public BulkObservableCollection<GstNilSalesPeriodOption> NilSalesOptions { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand AutoImportCommand { get; }
    public AsyncRelayCommand ImportGstr3bCommand { get; }
    public AsyncRelayCommand ImportCreditLedgerCommand { get; }
    public AsyncRelayCommand ImportCashLedgerCommand { get; }
    public AsyncRelayCommand ReconcileCommand { get; }
    public AsyncRelayCommand ApplyNilSalesCommand { get; }
    public AsyncRelayCommand EditPeriodCommand { get; }

    public bool CanViewGst => DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.GstView);
    public bool CanImportGst => (!NetworkClientRuntime.IsNetworkClient || !NetworkClientRuntime.AccountingAccessSuspended)
        && CanViewGst
        && DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.Import)
        && DesktopPermissionService.HasPermission(_governanceRepository, GovernancePermissions.GstImport);
    public bool CanControlGst => DesktopPermissionService.CanMutateAccounting(_governanceRepository, GovernancePermissions.GstControl);
    public bool IsGstReadOnly => !CanControlGst;
    public string GstPermissionSummary => $"{DesktopPermissionService.EffectiveRoleName(_governanceRepository)} · {(NetworkClientRuntime.IsNetworkClient ? "Windows processing node · " : string.Empty)}GST view { (CanViewGst ? "allowed" : "denied") } · import { (CanImportGst ? "allowed" : "denied") } · controls { (CanControlGst ? "allowed" : "denied") }";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ApplyNilSalesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ReportingFrequency
    {
        get => _reportingFrequency;
        private set
        {
            if (SetProperty(ref _reportingFrequency, value))
            {
                OnPropertyChanged(nameof(ReportingPeriodDescription));
            }
        }
    }

    public string ReportingPeriodDescription => ReportingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase)
        ? "Quarterly view · return, books, payment and ledger evidence is grouped to the quarter-end period"
        : "Monthly view · each source month is reconciled separately";

    public Gstr3bReturnRecord? SelectedReturn
    {
        get => _selectedReturn;
        set
        {
            if (SetProperty(ref _selectedReturn, value))
            {
                RebuildSelectedReturnDetails();
            }
        }
    }

    public Gstr3bReturnRecord? SelectedSourceReturn
    {
        get => _selectedSourceReturn;
        set
        {
            if (SetProperty(ref _selectedSourceReturn, value))
            {
                RebuildSelectedSourceDetails();
                EditPeriodCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedRawText
    {
        get => _selectedRawText;
        private set => SetProperty(ref _selectedRawText, value);
    }

    public int MatchedCount => Rows.Count(row => row.Status.StartsWith("Matched", StringComparison.OrdinalIgnoreCase));
    public int DifferenceCount => Rows.Count(row => row.Status == "Mismatch");
    public int MissingCount => Rows.Count(row =>
        !row.HasReturn || !row.HasCreditLedger || !row.HasCashLedger);
    public int NilSalesSelectedCount => NilSalesOptions.Count(row => row.IsSelected && !row.IsConflict);
    public int NilSalesConflictCount => NilSalesOptions.Count(row => row.IsConflict);

    private long CurrentClientId => _clientContext.CurrentClient?.Id ?? 0;

    public void Refresh()
    {
        Rows.Clear();
        Returns.Clear();
        SourceReturns.Clear();
        LedgerPeriods.Clear();
        PaymentRows.Clear();
        LiabilityBreakupRows.Clear();
        RawFields.Clear();
        NilSalesOptions.Clear();
        SelectedReturn = null;
        SelectedSourceReturn = null;

        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            _preparedClientId = 0;
            _preparedFinancialYear = string.Empty;
            StatusText = "Open a client to use GSTR-3B reconciliation.";
            NotifySummary();
            return;
        }

        var detectedFrequency = _refreshCoordinator.DetectFilingFrequency(client);
        var settings = _journalRepository.GetSettings(client.Id, client.FinancialYear);
        ReportingFrequency = detectedFrequency.Length > 0
            ? detectedFrequency
            : string.IsNullOrWhiteSpace(settings.FilingFrequency)
                ? "Monthly"
                : settings.FilingFrequency;

        var frequencyChanged = detectedFrequency.Length > 0 &&
            !settings.FilingFrequency.Equals(detectedFrequency, StringComparison.OrdinalIgnoreCase);
        if (frequencyChanged)
        {
            settings.FilingFrequency = detectedFrequency;
            _journalRepository.SaveSettings(settings);
        }

        foreach (var option in _refreshCoordinator.GetNilSalesOptions(client))
        {
            NilSalesOptions.Add(option);
        }

        var storedRows = _repository.GetReconciliation(client.Id, client.FinancialYear);
        if (frequencyChanged || NeedsRegrouping(storedRows, ReportingFrequency))
        {
            _reconciliationService.Reconcile(client.Id, client.FinancialYear);
            storedRows = _repository.GetReconciliation(client.Id, client.FinancialYear);
        }

        foreach (var row in storedRows)
        {
            Rows.Add(row);
        }

        var sourceReturns = _repository.GetReturns(client.Id, client.FinancialYear);
        foreach (var row in sourceReturns)
        {
            SourceReturns.Add(row);
        }
        foreach (var row in GstReconciliationService.AggregateReturns(sourceReturns, ReportingFrequency))
        {
            Returns.Add(row);
        }

        var rawLedgers = _repository.GetLedgerPeriods(client.Id, client.FinancialYear);
        foreach (var row in GstReconciliationService.AggregateLedgerPeriods(rawLedgers, ReportingFrequency))
        {
            LedgerPeriods.Add(row);
        }

        SelectedReturn = Returns.FirstOrDefault();
        SelectedSourceReturn = SourceReturns.FirstOrDefault();
        StatusText =
            $"{ReportingFrequency} workbook-parity reconciliation: {Rows.Count:N0} periods, " +
            $"{MatchedCount:N0} matched, {DifferenceCount:N0} mismatches and " +
            $"{MissingCount:N0} missing-return/ledger periods. " +
            "Tables 3.1, 3.1.1, 3.2, all Table 4 rows, Tables 5/5.1, the full Table 6.1 matrix and liability breakup are retained from each imported PDF.";
        _preparedClientId = client.Id;
        _preparedFinancialYear = client.FinancialYear;
        NotifySummary();
    }

    public void SelectPeriod(string monthPeriodKey)
    {
        if (string.IsNullOrWhiteSpace(monthPeriodKey)) return;
        var target = monthPeriodKey.Trim();
        var aggregateKey = target;
        if (target.Length == 6 && DateTime.TryParseExact(target + "01", "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var month))
            aggregateKey = GstJournalGenerationService.PeriodKey(month, ReportingFrequency);

        var source = SourceReturns.FirstOrDefault(row => row.PeriodKey.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (source is null && ReportingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase))
        {
            source = SourceReturns
                .Where(row => row.PeriodKey.Length == 6 && DateTime.TryParseExact(
                    row.PeriodKey + "01", "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _))
                .Where(row =>
                {
                    _ = DateTime.TryParseExact(row.PeriodKey + "01", "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var sourceMonth);
                    return GstJournalGenerationService.PeriodKey(sourceMonth, ReportingFrequency)
                        .Equals(aggregateKey, StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(row => row.PeriodKey, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        if (source is not null) SelectedSourceReturn = source;

        var aggregate = Returns.FirstOrDefault(row => row.PeriodKey.Equals(aggregateKey, StringComparison.OrdinalIgnoreCase));
        if (aggregate is not null) SelectedReturn = aggregate;
    }

    private static bool NeedsRegrouping(
        IReadOnlyCollection<GstReconciliationRow> rows,
        string filingFrequency)
    {
        if (rows.Count == 0)
        {
            return false;
        }

        var quarterly = filingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase);
        return quarterly
            ? rows.Any(row => row.PeriodKey.Length == 6 &&
                int.TryParse(row.PeriodKey.AsSpan(4, 2), out var month) &&
                month is not (3 or 6 or 9 or 12))
            : false;
    }

    private void RebuildSelectedReturnDetails()
    {
        PaymentRows.Clear();
        LiabilityBreakupRows.Clear();
        var row = SelectedReturn;
        if (row is null)
        {
            return;
        }

        foreach (var payment in row.PaymentRows)
        {
            PaymentRows.Add(payment);
        }
        foreach (var liability in row.LiabilityBreakup)
        {
            LiabilityBreakupRows.Add(liability);
        }
    }

    private void RebuildSelectedSourceDetails()
    {
        RawFields.Clear();
        var row = SelectedSourceReturn;
        if (row is null)
        {
            SelectedRawText = string.Empty;
            return;
        }

        if (row.RawFields.Count > 0)
        {
            foreach (var field in row.RawFields)
            {
                AddRaw(SectionForRawField(field.Key), field.Key, field.Value);
            }
        }
        else
        {
            // Compatibility fallback for records imported before v3.0.5.
            AddRaw("Identity", "period", row.PeriodLabel);
            AddRaw("Identity", "month_key", row.PeriodKey);
            AddRaw("Identity", "gstin", row.Gstin);
            AddRaw("Identity", "legal_name", row.LegalName);
            AddRaw("Identity", "trade_name", row.TradeName);
            AddRaw("Identity", "year", row.FinancialYear);
            AddRaw("Identity", "arn", row.Arn);
            AddRaw("Identity", "arn_date", row.ArnDate?.ToString("dd-MM-yyyy") ?? string.Empty);
            AddRaw("Identity", "file_path", row.SourcePath);
            AddRaw("Table 3.1(a)", "table_3_1.outward_taxable.taxable_value", row.OutwardTaxable);
            AddRaw("Table 3.1(a)", "table_3_1.outward_taxable.igst", row.OutwardIgst);
            AddRaw("Table 3.1(a)", "table_3_1.outward_taxable.cgst", row.OutwardCgst);
            AddRaw("Table 3.1(a)", "table_3_1.outward_taxable.sgst", row.OutwardSgst);
            AddRaw("Table 3.1(a)", "table_3_1.outward_taxable.cess", row.OutwardCess);
            AddRaw("Table 3.1(d)", "table_3_1.inward_rcm.taxable_value", row.RcmTaxable);
            AddRaw("Table 3.1(d)", "table_3_1.inward_rcm.igst", row.RcmIgst);
            AddRaw("Table 3.1(d)", "table_3_1.inward_rcm.cgst", row.RcmCgst);
            AddRaw("Table 3.1(d)", "table_3_1.inward_rcm.sgst", row.RcmSgst);
            AddRaw("Table 3.1(d)", "table_3_1.inward_rcm.cess", row.RcmCess);
            AddRaw("Table 4(C)", "table_4_itc.net_itc.igst", row.NetItcIgst);
            AddRaw("Table 4(C)", "table_4_itc.net_itc.cgst", row.NetItcCgst);
            AddRaw("Table 4(C)", "table_4_itc.net_itc.sgst", row.NetItcSgst);
            AddRaw("Table 4(C)", "table_4_itc.net_itc.cess", row.NetItcCess);
        }

        AddRaw("Parser", "parse_valid", row.ParseValid ? "Yes" : "No");
        AddRaw("Parser", "parse_notes", row.ParseNotes);
        SelectedRawText = row.RawText;
    }

    private static string SectionForRawField(string key)
    {
        if (key.StartsWith("table_3_1_1", StringComparison.OrdinalIgnoreCase))
        {
            return "Table 3.1.1";
        }
        if (key.StartsWith("table_3_1", StringComparison.OrdinalIgnoreCase))
        {
            return "Table 3.1";
        }
        if (key.StartsWith("table_3_2", StringComparison.OrdinalIgnoreCase))
        {
            return "Table 3.2";
        }
        if (key.StartsWith("table_4", StringComparison.OrdinalIgnoreCase))
        {
            return "Table 4";
        }
        if (key.StartsWith("table_5_1", StringComparison.OrdinalIgnoreCase))
        {
            return "Table 5.1";
        }
        if (key.StartsWith("table_5", StringComparison.OrdinalIgnoreCase))
        {
            return "Table 5";
        }
        if (key.StartsWith("table_6_1", StringComparison.OrdinalIgnoreCase))
        {
            return "Table 6.1";
        }
        if (key.StartsWith("liability_breakup", StringComparison.OrdinalIgnoreCase))
        {
            return "Liability Breakup";
        }
        if (key.StartsWith("raw_", StringComparison.OrdinalIgnoreCase))
        {
            return "Parser";
        }
        return "Identity";
    }

    private void AddRaw(string section, string field, decimal value) =>
        AddRaw(section, field, value.ToString("N2"));

    private void AddRaw(string section, string field, string value) =>
        RawFields.Add(new GstRawFieldRow
        {
            Section = section,
            Field = field,
            Value = value
        });

    private async Task RefreshAccountingAsync()
    {
        var client = _clientContext.CurrentClient;
        if (client is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Rebuilding GST Journal and GSTR-3B books reconciliation from current Purchase/Sales data...";
            var refreshStatus = await _refreshCoordinator.RefreshAsync(client);
            if (_clientContext.CurrentClient?.Id != client.Id || !_isPageActive)
            {
                return;
            }
            Refresh();
            StatusText = refreshStatus;
        }
        catch (Exception exception)
        {
            StatusText = $"GST books refresh failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AutoImportPortalAsync(bool force)
    {
        if (!CanImportGst)
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, GovernancePermissions.GstImport, "import or refresh GST portal evidence");
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Scanning E-returns and Windows Downloads for GSTR-3B, Credit Ledger and Cash Ledger files…";
            var result = await Task.Run(() => _autoImportService.ImportAvailable(
                client,
                _eReturnsRootContext.RootPath,
                force));

            if (_clientContext.CurrentClient?.Id != client.Id)
            {
                return;
            }

            if (!force && result.CacheHit)
            {
                StatusText = "Portal source cache checked. No source version changed, so nothing was re-imported.";
                return;
            }

            // Current Excel applies automatic filing-frequency / Nil-Sales controls
            // when fresh GSTR-3B store rows are loaded, not on every ordinary refresh.
            _refreshCoordinator.ApplyAutomaticNilSalesControls(client);
            var refreshStatus = await _refreshCoordinator.RefreshAsync(client);
            PublishCommittedGstImport(
                client.Id,
                client.FinancialYear,
                result.Gstr3bPeriods + result.CreditLedgerPeriods + result.CashLedgerPeriods,
                _eReturnsRootContext.RootPath,
                force ? "Manual Portal Rescan" : "Automatic GST Import");
            if (!_isPageActive) return;
            Refresh();
            StatusText =
                $"Automatic portal import completed: {result.Gstr3bPeriods:N0} GSTR-3B periods, " +
                $"{result.CreditLedgerPeriods:N0} Credit Ledger periods and " +
                $"{result.CashLedgerPeriods:N0} Cash Ledger periods. " +
                $"{result.Warnings.Count:N0} files require review. {refreshStatus}";
        }
        catch (Exception exception)
        {
            StatusText = $"Automatic GST portal import failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportGstr3bAsync()
    {
        if (!CanImportGst)
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, GovernancePermissions.GstImport, "import GSTR-3B");
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Manual fallback: import GSTR-3B PDF files",
            Filter = "PDF files (*.pdf)|*.pdf",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = _gstr3bImportService.Import(
                dialog.FileNames,
                client.Id,
                client.FinancialYear,
                client.Gstin);
            _refreshCoordinator.ApplyAutomaticNilSalesControls(client);
            await _refreshCoordinator.RefreshAsync(client);
            if (result.PeriodsImported > 0)
                PublishCommittedGstImport(client.Id, client.FinancialYear, result.PeriodsImported, dialog.FileNames.FirstOrDefault() ?? string.Empty, "Manual Import");
            if (_isPageActive) Refresh();
            StatusText =
                $"Manual fallback imported {result.PeriodsImported:N0} GSTR-3B periods; " +
                $"{result.Warnings.Count:N0} files require review.";
        }
        catch (Exception exception)
        {
            StatusText = $"GSTR-3B import failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EditSelectedPeriodAsync()
    {
        if (!CanControlGst)
        {
            StatusText = DesktopPermissionService.DeniedMessage(
                _governanceRepository,
                GovernancePermissions.GstControl,
                "edit a GSTR-3B period");
            return;
        }

        var client = _clientContext.CurrentClient;
        var row = SelectedSourceReturn;
        if (client is null || row is null || row.PersistentId <= 0) return;

        var periodBox = new TextBox
        {
            Text = row.PeriodKey,
            MinWidth = 180,
            Margin = new Thickness(0, 4, 0, 10)
        };
        var labelBox = new TextBox
        {
            Text = row.PeriodLabel,
            MinWidth = 260,
            Margin = new Thickness(0, 4, 0, 10)
        };
        var frequencyBox = new ComboBox
        {
            ItemsSource = new[] { "Monthly", "Quarterly" },
            SelectedItem = row.FilingFrequency.Contains("Quarter", StringComparison.OrdinalIgnoreCase)
                ? "Quarterly"
                : "Monthly",
            MinWidth = 180,
            Margin = new Thickness(0, 4, 0, 10)
        };

        var saveButton = new Button
        {
            Content = "Save",
            MinWidth = 90,
            Padding = new Thickness(12, 6, 12, 6),
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            Padding = new Thickness(12, 6, 12, 6),
            IsCancel = true
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(saveButton);
        buttons.Children.Add(cancelButton);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Month Key (YYYYMM)",
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(periodBox);
        panel.Children.Add(new TextBlock
        {
            Text = "Period Label",
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(labelBox);
        panel.Children.Add(new TextBlock
        {
            Text = "Filing Frequency",
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(frequencyBox);
        panel.Children.Add(new TextBlock
        {
            Text = $"Last Edited: {row.LastEditedDisplay}",
            Margin = new Thickness(0, 2, 0, 14),
            Foreground = System.Windows.Media.Brushes.DimGray
        });
        panel.Children.Add(buttons);

        var editor = new Window
        {
            Title = $"Edit GST Period - {row.SourceFileName}",
            Content = panel,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = System.Windows.Application.Current?.MainWindow
        };
        saveButton.Click += (_, _) => editor.DialogResult = true;

        if (editor.ShowDialog() != true) return;

        var periodKey = periodBox.Text.Trim();
        if (!IsValidPeriodForFinancialYear(periodKey, client.FinancialYear))
        {
            MessageBox.Show(
                editor.Owner,
                $"Enter a valid YYYYMM period inside FY {client.FinancialYear}.",
                "Invalid GST Period",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var label = labelBox.Text.Trim();
        if (label.Length == 0 &&
            DateTime.TryParseExact(
                periodKey + "01",
                "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var month))
        {
            label = month.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        }

        var frequency = frequencyBox.SelectedItem?.ToString() ?? "Monthly";
        IsBusy = true;
        try
        {
            var editedUtc = await Task.Run(() =>
                _repository.UpdateReturnPeriod(
                    row.PersistentId,
                    periodKey,
                    label,
                    frequency));

            if (!editedUtc.HasValue)
            {
                StatusText = "Period information is unchanged. Nothing was written and the cached source remains valid.";
                return;
            }

            // Manual edits do not invalidate the source fingerprint. Background checking
            // therefore preserves the edit until a genuinely newer/different source version
            // appears or the user explicitly chooses Reimport Portal Files.
            await Task.Run(() => _reconciliationService.Reconcile(client.Id, client.FinancialYear));
            var refreshed = await Task.Run(() => _repository.GetReturns(client.Id, client.FinancialYear));
            SourceReturns.ReplaceRange(refreshed);
            Returns.ReplaceRange(GstReconciliationService.AggregateReturns(refreshed, ReportingFrequency));
            SelectedSourceReturn = SourceReturns.FirstOrDefault(item => item.PersistentId == row.PersistentId)
                ?? SourceReturns.FirstOrDefault(item => item.PeriodKey.Equals(periodKey, StringComparison.OrdinalIgnoreCase));
            SelectedReturn = Returns.FirstOrDefault(item =>
                item.PeriodKey.Equals(
                    GstJournalGenerationService.PeriodKey(
                        new DateTime(int.Parse(periodKey[..4]), int.Parse(periodKey[4..]), 1),
                        ReportingFrequency),
                    StringComparison.OrdinalIgnoreCase));

            _liveChangeHub.Publish(
                client.Id,
                client.FinancialYear,
                AccountingLiveArea.Gst,
                $"GSTR-3B period edited to {periodKey}; Last Edited {editedUtc.Value.ToLocalTime():dd-MM-yyyy HH:mm:ss}");

            StatusText =
                $"GST period saved. Last Edited: {editedUtc.Value.ToLocalTime():dd-MM-yyyy HH:mm:ss}. " +
                "The same cached source will not re-import; a newer/different source version will auto-import.";
        }
        catch (Exception exception)
        {
            StatusText = $"GST period edit failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            EditPeriodCommand.RaiseCanExecuteChanged();
        }
    }

    private static bool IsValidPeriodForFinancialYear(string periodKey, string financialYear)
    {
        if (periodKey.Length != 6 ||
            !int.TryParse(periodKey[..4], out var year) ||
            !int.TryParse(periodKey[4..], out var month) ||
            month is < 1 or > 12)
            return false;

        var startText = new string(financialYear
            .TakeWhile(character => character != '-')
            .Where(char.IsDigit)
            .ToArray());
        if (!int.TryParse(startText, out var startYear)) return false;
        return month >= 4 ? year == startYear : year == startYear + 1;
    }

    public async Task AutoSaveNilSalesSelectionsAsync()
    {
        if (IsBusy) return;
        await ApplyNilSalesSelectionsAsync(autoTriggered: true);
    }

    private Task ApplyNilSalesSelectionsAsync() => ApplyNilSalesSelectionsAsync(autoTriggered: false);

    private async Task ApplyNilSalesSelectionsAsync(bool autoTriggered)
    {
        if (!CanControlGst)
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, GovernancePermissions.GstControl, "change GST Nil Sales controls");
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null) return;

        IsBusy = true;
        try
        {
            var selected = NilSalesOptions
                .Where(row => row.IsSelected && !row.IsConflict)
                .Select(row => row.PeriodKey)
                .ToArray();
            var refreshStatus = await _refreshCoordinator.SaveNilSalesSelectionsAsync(client, selected);
            if (_isPageActive) Refresh();
            StatusText = autoTriggered
                ? $"Nil Return/Nil Sales selection auto-saved and applied. Selected: {selected.Length:N0}. " +
                  $"Portal/books conflicts: {NilSalesConflictCount:N0}. {refreshStatus}"
                : $"Nil Sales periods saved: {selected.Length:N0}. " +
                  $"Portal/books conflicts: {NilSalesConflictCount:N0}. {refreshStatus}";
        }
        catch (Exception exception)
        {
            StatusText = $"Nil Sales periods were not saved: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportLedgerAsync(string ledgerType)
    {
        if (!CanImportGst)
        {
            StatusText = DesktopPermissionService.DeniedMessage(_governanceRepository, GovernancePermissions.GstImport, $"import {ledgerType}");
            return;
        }
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"Manual fallback: import {ledgerType}",
            Filter = "GST ledger files (*.xlsx;*.xlsm;*.csv)|*.xlsx;*.xlsm;*.csv|Excel files (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|CSV files (*.csv)|*.csv",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = _ledgerImportService.Import(
                dialog.FileName,
                ledgerType,
                client.Id,
                client.FinancialYear,
                client.Gstin);
            await _refreshCoordinator.RefreshAsync(client);
            if (result.PeriodsImported > 0)
                PublishCommittedGstImport(client.Id, client.FinancialYear, result.PeriodsImported, dialog.FileName, "Manual Import");
            if (_isPageActive) Refresh();
            StatusText = $"Manual fallback imported {result.PeriodsImported:N0} {ledgerType} periods.";
        }
        catch (Exception exception)
        {
            StatusText = $"{ledgerType} import failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void PublishCommittedGstImport(
        long clientId,
        string financialYear,
        int recordCount,
        string sourcePath,
        string importMethod)
    {
        if (recordCount <= 0) return;
        if (_automationService is not null)
        {
            _automationService.PublishCommittedAccountingImport(
                clientId, financialYear, "GST", recordCount, sourcePath, importMethod);
            return;
        }

        _liveChangeHub.Publish(clientId, financialYear, AccountingLiveArea.Gst, $"{importMethod} committed GST");
    }

    private void OnClientContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveClientContext.CurrentClient))
        {
            _preparedClientId = 0;
            _preparedFinancialYear = string.Empty;
            if (_isPageActive) StatusText = "GSTR-3B is preparing in the background.";
            OnPropertyChanged(nameof(CanViewGst));
            OnPropertyChanged(nameof(CanImportGst));
            OnPropertyChanged(nameof(CanControlGst));
            OnPropertyChanged(nameof(IsGstReadOnly));
            OnPropertyChanged(nameof(GstPermissionSummary));
            RefreshCommand.RaiseCanExecuteChanged();
            AutoImportCommand.RaiseCanExecuteChanged();
            ImportGstr3bCommand.RaiseCanExecuteChanged();
            ImportCreditLedgerCommand.RaiseCanExecuteChanged();
            ImportCashLedgerCommand.RaiseCanExecuteChanged();
            ReconcileCommand.RaiseCanExecuteChanged();
            ApplyNilSalesCommand.RaiseCanExecuteChanged();
        }
    }

    private void OnEReturnsRootContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EReturnsRootContext.RootPath))
        {
            if (_isPageActive) Refresh();
        }
    }

    private void NotifySummary()
    {
        OnPropertyChanged(nameof(MatchedCount));
        OnPropertyChanged(nameof(DifferenceCount));
        OnPropertyChanged(nameof(MissingCount));
        OnPropertyChanged(nameof(NilSalesSelectedCount));
        OnPropertyChanged(nameof(NilSalesConflictCount));
    }
}
