using System.Collections.ObjectModel;
using System.Windows;
using AccountingAutomationHub.Desktop.Commands;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Domain.Accounting;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Infrastructure.Accounting;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Governance;

namespace AccountingAutomationHub.Desktop.ViewModels;

/// <summary>
/// On-demand actual-format Balance Sheet / Profit & Loss presentation. The report
/// consumes LedgerTotalService as the single accounting authority, then rolls ledger
/// balances through the Tally Group hierarchy instead of displaying a flat ledger list.
/// Completion 61 prepares each Client/FY statement read model in its own module worker.
/// Opening the page renders prepared memory; it does not start the accounting-book read.
/// </summary>
public sealed class AccountingStatementViewModel : PageViewModel
{
    private const decimal Tolerance = 0.005m;

    private static readonly HashSet<string> ProfitAndLossRoots = Keys(
        "Sales Accounts", "Purchase Accounts", "Direct Incomes", "Direct Income",
        "Indirect Incomes", "Indirect Income", "Direct Expenses", "Direct Expense",
        "Indirect Expenses", "Indirect Expense");

    private static readonly HashSet<string> ExpenseRoots = Keys(
        "Purchase Accounts", "Direct Expenses", "Direct Expense", "Indirect Expenses", "Indirect Expense");

    private static readonly HashSet<string> DirectExpenseRoots = Keys(
        "Purchase Accounts", "Direct Expenses", "Direct Expense");

    private static readonly HashSet<string> DirectIncomeRoots = Keys(
        "Sales Accounts", "Direct Incomes", "Direct Income");

    private static readonly HashSet<string> IndirectExpenseRoots = Keys(
        "Indirect Expenses", "Indirect Expense");

    private static readonly HashSet<string> IndirectIncomeRoots = Keys(
        "Indirect Incomes", "Indirect Income");

    private static readonly HashSet<string> LiabilityRoots = Keys(
        "Capital Account", "Loans (Liability)", "Current Liabilities");

    private static readonly HashSet<string> AssetRoots = Keys(
        "Fixed Assets", "Investments", "Current Assets", "Misc. Expenses (ASSET)", "Misc. Expenses (Asset)");

    private readonly bool _profitAndLoss;
    private readonly ActiveClientContext _clientContext;
    private readonly IMasterRepository _masterRepository;
    private readonly LedgerTotalService _ledgerTotalService;
    private readonly SqliteBankingRepository _bankingRepository;
    private readonly SqliteJournalRepository _journalRepository;
    private readonly ClientWorkspaceStateCache _workspaceStateCache;
    private readonly AccountingLiveChangeHub _liveChangeHub;
    private readonly SqliteGovernanceRepository _governanceRepository;
    private string _statusText = "Open this report from Dashboard when you want to generate it.";
    private decimal _leftTotal;
    private decimal _rightTotal;
    private decimal _grossProfit;
    private decimal _netProfit;
    private decimal _balanceDifference;
    private int _ledgerCount;
    private DateTime? _generatedAt;
    private string _companyCaption = string.Empty;
    private string _periodCaption = string.Empty;
    private string _selectedViewMode = "Tally View";
    private string _selectedGroupFilter = string.Empty;
    private string _navigationMode = "Statement";
    private string _ledgerReturnMode = "Statement";
    private string _ledgerReturnGroup = string.Empty;
    private string _selectedLedgerName = string.Empty;
    private decimal _selectedLedgerOpening;
    private decimal _selectedLedgerDebit;
    private decimal _selectedLedgerCredit;
    private decimal _selectedLedgerClosing;
    private bool _hasLedgerDetail;
    private long _preparedClientId;
    private string _preparedFinancialYear = string.Empty;
    private IReadOnlyList<MasterRecord> _preparedMasters = [];
    private IReadOnlyList<LedgerTotalSnapshot> _preparedLedgerTotals = [];

    public AccountingStatementViewModel(
        bool profitAndLoss,
        ActiveClientContext clientContext,
        IMasterRepository masterRepository,
        LedgerTotalService ledgerTotalService,
        SqliteBankingRepository bankingRepository,
        SqliteJournalRepository journalRepository,
        ClientWorkspaceStateCache workspaceStateCache,
        AccountingLiveChangeHub liveChangeHub)
        : base(
            profitAndLoss ? "P&L" : "Balance Sheet",
            profitAndLoss ? "Actual-format Expenses vs Income" : "Actual-format Liabilities & Equity vs Assets")
    {
        _profitAndLoss = profitAndLoss;
        _clientContext = clientContext;
        _masterRepository = masterRepository;
        _ledgerTotalService = ledgerTotalService;
        _bankingRepository = bankingRepository;
        _journalRepository = journalRepository;
        _workspaceStateCache = workspaceStateCache;
        _liveChangeHub = liveChangeHub;
        _governanceRepository = new SqliteGovernanceRepository();
        _governanceRepository.Initialize();
        RefreshCommand = new RelayCommand(RefreshRequested);
        // Reports remain on-demand; the hub is retained only so explicit accounting
        // mutations from the Balance Sheet can invalidate other prepared module streams.
    }

    public ObservableCollection<AccountingStatementLine> LeftRows { get; } = [];
    public ObservableCollection<AccountingStatementLine> RightRows { get; } = [];
    public ObservableCollection<AccountingStatementLine> ScheduleRows { get; } = [];
    public ObservableCollection<AccountingStatementLine> GroupRows { get; } = [];
    public ObservableCollection<AccountingStatementLine> DrilldownRows { get; } = [];
    public ObservableCollection<LedgerMovementDetail> LedgerTransactions { get; } = [];
    public RelayCommand RefreshCommand { get; }
    public IReadOnlyList<string> ViewModeOptions { get; } = ["Tally View", "Schedule III View"];
    public bool CanCreateClosingEntry => !_profitAndLoss && DesktopPermissionService.CanMutateAccounting(_governanceRepository, GovernancePermissions.EditAccounting);
    public bool CanDeleteLedgerEntries => !_profitAndLoss && DesktopPermissionService.CanMutateAccounting(_governanceRepository, GovernancePermissions.DeleteAccounting);
    public string SelectedViewMode
    {
        get => _selectedViewMode;
        set
        {
            if (!SetProperty(ref _selectedViewMode, value)) return;
            OnPropertyChanged(nameof(ViewModeCaption));
            OnPropertyChanged(nameof(IsScheduleIIIView));
            OnPropertyChanged(nameof(IsTallyView));
            OnPropertyChanged(nameof(ShowTallyStatement));
            OnPropertyChanged(nameof(ShowScheduleStatement));
            if (_generatedAt is not null) Refresh();
        }
    }
    public string ViewModeCaption => IsScheduleIIIView
        ? "Schedule III vertical presentation mapped from the actual Tally Group hierarchy"
        : "Tally-style Balance Sheet / Profit & Loss statement. Use Groups to drill down without changing the statement layout.";
    public bool IsScheduleIIIView => SelectedViewMode.Equals("Schedule III View", StringComparison.OrdinalIgnoreCase);
    public bool IsTallyView => !IsScheduleIIIView;
    public bool IsStatementScreen => _navigationMode.Equals("Statement", StringComparison.OrdinalIgnoreCase);
    public bool IsGroupsScreen => _navigationMode.Equals("Groups", StringComparison.OrdinalIgnoreCase);
    public bool IsGroupDetailScreen => _navigationMode.Equals("Group", StringComparison.OrdinalIgnoreCase);
    public bool IsLedgerScreen => _navigationMode.Equals("Ledger", StringComparison.OrdinalIgnoreCase);
    public bool IsDrilldownScreen => !IsStatementScreen;
    public bool ShowTallyStatement => IsStatementScreen && IsTallyView;
    public bool ShowScheduleStatement => IsStatementScreen && IsScheduleIIIView;
    public string DrilldownTitle => IsLedgerScreen
        ? SelectedLedgerName
        : IsGroupDetailScreen
            ? SelectedGroupFilter
            : IsGroupsScreen ? "Groups" : StatementName;
    public string NavigationCaption => IsLedgerScreen
        ? $"{StatementName}  >  {(string.IsNullOrWhiteSpace(_ledgerReturnGroup) ? "Ledger" : _ledgerReturnGroup)}  >  {SelectedLedgerName}"
        : IsGroupDetailScreen
            ? $"{StatementName}  >  Groups  >  {SelectedGroupFilter}"
            : IsGroupsScreen ? $"{StatementName}  >  Groups" : StatementName;

    public string StatementName => _profitAndLoss ? "Profit & Loss" : "Balance Sheet";
    public string LeftTitle => _profitAndLoss ? "Expenses" : "Liabilities & Equity";
    public string RightTitle => _profitAndLoss ? "Income" : "Assets";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public decimal LeftTotal { get => _leftTotal; private set => SetProperty(ref _leftTotal, value); }
    public decimal RightTotal { get => _rightTotal; private set => SetProperty(ref _rightTotal, value); }
    public decimal GrossProfit { get => _grossProfit; private set => SetProperty(ref _grossProfit, value); }
    public decimal NetProfit { get => _netProfit; private set => SetProperty(ref _netProfit, value); }
    public decimal BalanceDifference { get => _balanceDifference; private set => SetProperty(ref _balanceDifference, value); }
    public int LedgerCount { get => _ledgerCount; private set => SetProperty(ref _ledgerCount, value); }
    public string GrossProfitText => FormatResult(GrossProfit, "Profit", "Loss");
    public string NetProfitText => FormatResult(NetProfit, "Profit", "Loss");
    public string GeneratedText => _generatedAt is null ? "Not generated" : $"Generated {_generatedAt:dd.MM.yyyy HH:mm}";
    public string CompanyCaption { get => _companyCaption; private set => SetProperty(ref _companyCaption, value); }
    public string PeriodCaption { get => _periodCaption; private set => SetProperty(ref _periodCaption, value); }
    public bool IsProfitAndLoss => _profitAndLoss;
    public bool IsBalanceSheet => !_profitAndLoss;
    public string SelectedGroupFilter { get => _selectedGroupFilter; private set { if (SetProperty(ref _selectedGroupFilter, value)) OnPropertyChanged(nameof(GroupFilterCaption)); } }
    public string GroupFilterCaption => string.IsNullOrWhiteSpace(SelectedGroupFilter) ? "All Groups" : $"Group: {SelectedGroupFilter}";
    public string SelectedLedgerName { get => _selectedLedgerName; private set => SetProperty(ref _selectedLedgerName, value); }
    public decimal SelectedLedgerOpening { get => _selectedLedgerOpening; private set => SetProperty(ref _selectedLedgerOpening, value); }
    public decimal SelectedLedgerDebit { get => _selectedLedgerDebit; private set => SetProperty(ref _selectedLedgerDebit, value); }
    public decimal SelectedLedgerCredit { get => _selectedLedgerCredit; private set => SetProperty(ref _selectedLedgerCredit, value); }
    public decimal SelectedLedgerClosing { get => _selectedLedgerClosing; private set => SetProperty(ref _selectedLedgerClosing, value); }
    public bool HasLedgerDetail { get => _hasLedgerDetail; private set => SetProperty(ref _hasLedgerDetail, value); }
    public string SelectedLedgerOpeningText => FormatSignedBalance(SelectedLedgerOpening);
    public string SelectedLedgerClosingText => FormatSignedBalance(SelectedLedgerClosing);

    public void PrepareForDisplay()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        if (_preparedClientId == client.Id &&
            _preparedFinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
        {
            // Rebuild only the presentation rows from the already prepared in-memory payload.
            Refresh();
            return;
        }
        StatusText = $"{StatementName} stream is preparing for this Client/FY…";
        _workspaceStateCache.EnsureModulePrepared(client.Id, client.FinancialYear, ClientWorkspaceModule.Statements);
    }

    public void WarmFromPreparedSnapshot(ClientWorkspaceSnapshot snapshot)
    {
        var client = _clientContext.CurrentClient;
        if (client is null || client.Id != snapshot.ClientId ||
            !client.FinancialYear.Equals(snapshot.FinancialYear, StringComparison.OrdinalIgnoreCase)) return;
        _preparedMasters = snapshot.StatementMasters ?? [];
        _preparedLedgerTotals = (snapshot.LedgerTotals ?? []).Where(HasFinancialValue).ToArray();
        _preparedClientId = snapshot.ClientId;
        _preparedFinancialYear = snapshot.FinancialYear;
        Refresh();
    }

    public void QueueBackgroundPrepare(long clientId, string financialYear)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear)) return;
        _workspaceStateCache.QueueModuleRefresh(clientId, financialYear, ClientWorkspaceModule.Statements);
    }

    /// <summary>
    /// Fix27 immediate statement refresh after a committed accounting mutation such
    /// as Advanced Closure Entry. This deliberately bypasses the older prepared
    /// Statement snapshot once, repopulates it from the live LedgerTotalService book,
    /// and preserves the current statement/group/ledger navigation state.
    /// </summary>
    public void RefreshFromLiveLedgerTotals()
    {
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        try
        {
            _preparedMasters = _masterRepository.Get(client.Id, client.FinancialYear, false, string.Empty) ?? [];
            _preparedLedgerTotals = (_ledgerTotalService.GetAll(client.Id, client.FinancialYear) ?? [])
                .Where(HasFinancialValue)
                .ToArray();
            _preparedClientId = client.Id;
            _preparedFinancialYear = client.FinancialYear;
            Refresh();
        }
        catch (Exception exception)
        {
            StatusText = $"{StatementName} live refresh could not complete: {exception.Message}";
            _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Statements);
        }
    }

    private void RefreshRequested()
    {
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            Refresh();
            return;
        }
        _ledgerTotalService.Invalidate(client.Id, client.FinancialYear);
        _preparedLedgerTotals = [];
        _workspaceStateCache.Invalidate(client.Id, client.FinancialYear, ClientWorkspaceModule.Statements);
        _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Statements);
    }

    public void ShowStatement()
    {
        _navigationMode = "Statement";
        SelectedGroupFilter = string.Empty;
        ClearLedgerDetail();
        RaiseNavigationProperties();
        Refresh();
    }

    public void ShowGroups()
    {
        _navigationMode = "Groups";
        SelectedGroupFilter = string.Empty;
        ClearLedgerDetail();
        RaiseNavigationProperties();
        if (_generatedAt is null) Refresh();
        else StatusText = $"{StatementName} groups • choose a Group to open it in this workspace.";
    }

    public void SetGroupFilter(string? groupName)
    {
        var requested = groupName?.Trim() ?? string.Empty;
        if (requested.Length == 0)
        {
            ShowGroups();
            return;
        }
        _navigationMode = "Group";
        SelectedGroupFilter = requested;
        ClearLedgerDetail();
        RaiseNavigationProperties();
        Refresh();
    }

    public void OpenLedger(string? ledgerName)
    {
        var requested = ledgerName?.Trim() ?? string.Empty;
        if (requested.Length == 0) return;

        _ledgerReturnMode = IsGroupDetailScreen ? "Group" : "Statement";
        _ledgerReturnGroup = IsGroupDetailScreen ? SelectedGroupFilter : string.Empty;
        _navigationMode = "Ledger";
        LedgerTransactions.Clear();
        SelectedLedgerName = requested;
        HasLedgerDetail = false;
        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            RaiseNavigationProperties();
            return;
        }
        var snapshot = _ledgerTotalService.Get(client.Id, client.FinancialYear, SelectedLedgerName);
        if (snapshot is null)
        {
            RaiseNavigationProperties();
            return;
        }
        SelectedLedgerOpening = snapshot.OpeningBalance;
        SelectedLedgerDebit = snapshot.TotalDebit;
        SelectedLedgerCredit = snapshot.TotalCredit;
        SelectedLedgerClosing = snapshot.ClosingBalance;
        OnPropertyChanged(nameof(SelectedLedgerOpeningText));
        OnPropertyChanged(nameof(SelectedLedgerClosingText));
        foreach (var movement in _ledgerTotalService.GetMovements(client.Id, client.FinancialYear, SelectedLedgerName)) LedgerTransactions.Add(movement);
        HasLedgerDetail = true;
        StatusText = $"Ledger account • {SelectedLedgerName} • {LedgerTransactions.Count:N0} transaction(s).";
        RaiseNavigationProperties();
    }

    public void DeleteSelectedLedgerTransactions(IReadOnlyCollection<LedgerMovementDetail> selectedRows)
    {
        if (!IsBalanceSheet || !IsLedgerScreen) return;
        if (!CanDeleteLedgerEntries)
        {
            StatusText = "Your role does not have permission to delete accounting entries.";
            return;
        }

        var selected = (selectedRows ?? Array.Empty<LedgerMovementDetail>())
            .Where(item => !string.IsNullOrWhiteSpace(item.Source))
            .GroupBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (selected.Length == 0)
        {
            StatusText = "Select one or more Ledger transaction rows first. Ctrl/Shift multi-selection is supported.";
            return;
        }

        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var sources = selected.Select(item => item.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cashRows = (_bankingRepository.GetCashEntries(client.Id, client.FinancialYear) ?? [])
            .Where(row => row.Id > 0 && sources.Any(source => source.StartsWith(CashSourcePrefix(row), StringComparison.OrdinalIgnoreCase)))
            .GroupBy(row => row.Id)
            .Select(group => group.First())
            .ToArray();

        var journalRows = (_journalRepository.GetVouchers(client.Id, client.FinancialYear) ?? [])
            .Where(voucher => voucher.PersistentId > 0 && sources.Any(source => source.StartsWith(JournalSourcePrefix(voucher), StringComparison.OrdinalIgnoreCase)))
            .GroupBy(voucher => voucher.PersistentId)
            .Select(group => group.First())
            .ToArray();

        var matchedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in cashRows)
            foreach (var source in sources.Where(source => source.StartsWith(CashSourcePrefix(row), StringComparison.OrdinalIgnoreCase)))
                matchedSources.Add(source);
        foreach (var voucher in journalRows)
            foreach (var source in sources.Where(source => source.StartsWith(JournalSourcePrefix(voucher), StringComparison.OrdinalIgnoreCase)))
                matchedSources.Add(source);

        var protectedCount = selected.Length - matchedSources.Count;
        if (cashRows.Length == 0 && journalRows.Length == 0)
        {
            StatusText = "The selected rows are Bank/Sales/Purchase or another protected source. Delete those from their source module so import/reconciliation history stays intact.";
            return;
        }

        var detail = $"Delete {cashRows.Length:N0} Cash entr{(cashRows.Length == 1 ? "y" : "ies")} and {journalRows.Length:N0} Journal voucher(s) selected from this Ledger?";
        if (protectedCount > 0)
            detail += $"\n\n{protectedCount:N0} selected Bank/Sales/Purchase/other source row(s) are protected and will NOT be deleted here.";
        detail += "\n\nOnly records belonging to the current Client + Financial Year are eligible.";
        if (MessageBox.Show(detail, "Delete Selected Ledger Entries", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var deletedCash = _bankingRepository.DeleteCashEntries(client.Id, client.FinancialYear, cashRows.Select(row => row.Id).ToArray());
        var deletedJournal = 0;
        foreach (var voucher in journalRows.Where(item => item.ClientId == client.Id && item.FinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase)))
        {
            _journalRepository.DeleteVoucher(voucher.PersistentId);
            deletedJournal++;
        }

        if (deletedCash + deletedJournal <= 0)
        {
            StatusText = "No selected Cash/Journal records were deleted.";
            return;
        }

        _ledgerTotalService.Invalidate(client.Id, client.FinancialYear);
        _preparedLedgerTotals = [];
        _workspaceStateCache.Invalidate(client.Id, client.FinancialYear, ClientWorkspaceModule.Statements, ClientWorkspaceModule.Cash, ClientWorkspaceModule.Journal);
        _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Statements);
        _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Cash);
        _workspaceStateCache.QueueModuleRefresh(client.Id, client.FinancialYear, ClientWorkspaceModule.Journal);
        _liveChangeHub.Publish(client.Id, client.FinancialYear, deletedJournal > 0 ? AccountingLiveArea.Journal : AccountingLiveArea.Cash, "Balance Sheet Ledger deleted selected Cash/Journal entries");

        // Explicit user mutation: rebuild the central LedgerTotal cache once, then repaint
        // the already-open Ledger from that cache so Debit/Credit/Closing update immediately.
        RefreshFromLiveLedgerTotals();
        StatusText = $"Deleted {deletedCash:N0} Cash entr{(deletedCash == 1 ? "y" : "ies")} and {deletedJournal:N0} Journal voucher(s)." +
                     (protectedCount > 0 ? $" {protectedCount:N0} protected source row(s) were retained." : string.Empty);
    }

    private static string CashSourcePrefix(AccountingAutomationHub.Domain.Banking.CashEntryRecord row)
    {
        var identity = string.IsNullOrWhiteSpace(row.EntryId) ? row.Id.ToString() : row.EntryId.Trim();
        return $"CASH|{identity}|{row.EntryDate:yyyyMMdd}|";
    }

    private static string JournalSourcePrefix(AccountingAutomationHub.Domain.Journals.JournalVoucher voucher)
    {
        var identity = voucher.GenerationMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase)
            ? $"AUTO|{voucher.PeriodKey}"
            : $"MANUAL|{(voucher.VoucherNumber.Trim().Length > 0 ? voucher.VoucherNumber.Trim() : voucher.PersistentId.ToString())}";
        return $"JOURNAL|{identity}|{voucher.VoucherDate:yyyyMMdd}|";
    }

    public void GoBack()
    {
        if (IsLedgerScreen)
        {
            var returnToGroup = _ledgerReturnMode.Equals("Group", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_ledgerReturnGroup);
            ClearLedgerDetail();
            if (returnToGroup)
            {
                _navigationMode = "Group";
                SelectedGroupFilter = _ledgerReturnGroup;
                RaiseNavigationProperties();
                Refresh();
            }
            else
            {
                _navigationMode = "Statement";
                SelectedGroupFilter = string.Empty;
                RaiseNavigationProperties();
                Refresh();
            }
            return;
        }
        if (IsGroupDetailScreen)
        {
            _navigationMode = "Groups";
            SelectedGroupFilter = string.Empty;
            RaiseNavigationProperties();
            StatusText = $"{StatementName} groups • choose a Group to open it in this workspace.";
            return;
        }
        if (IsGroupsScreen) ShowStatement();
    }

    public void CloseLedger() => GoBack();

    private void OpenLedgerAfterRefresh(string ledgerName)
    {
        LedgerTransactions.Clear();
        var client = _clientContext.CurrentClient;
        if (client is null) return;
        var snapshot = _ledgerTotalService.Get(client.Id, client.FinancialYear, ledgerName);
        if (snapshot is null) return;
        SelectedLedgerName = ledgerName;
        SelectedLedgerOpening = snapshot.OpeningBalance;
        SelectedLedgerDebit = snapshot.TotalDebit;
        SelectedLedgerCredit = snapshot.TotalCredit;
        SelectedLedgerClosing = snapshot.ClosingBalance;
        OnPropertyChanged(nameof(SelectedLedgerOpeningText));
        OnPropertyChanged(nameof(SelectedLedgerClosingText));
        foreach (var movement in _ledgerTotalService.GetMovements(client.Id, client.FinancialYear, ledgerName)) LedgerTransactions.Add(movement);
        HasLedgerDetail = true;
        StatusText = $"Ledger account • {SelectedLedgerName} • {LedgerTransactions.Count:N0} transaction(s).";
        RaiseNavigationProperties();
    }

    private void ClearLedgerDetail()
    {
        LedgerTransactions.Clear();
        SelectedLedgerName = string.Empty;
        SelectedLedgerOpening = SelectedLedgerDebit = SelectedLedgerCredit = SelectedLedgerClosing = 0m;
        OnPropertyChanged(nameof(SelectedLedgerOpeningText));
        OnPropertyChanged(nameof(SelectedLedgerClosingText));
        HasLedgerDetail = false;
    }

    private void RaiseNavigationProperties()
    {
        OnPropertyChanged(nameof(IsStatementScreen));
        OnPropertyChanged(nameof(IsGroupsScreen));
        OnPropertyChanged(nameof(IsGroupDetailScreen));
        OnPropertyChanged(nameof(IsLedgerScreen));
        OnPropertyChanged(nameof(IsDrilldownScreen));
        OnPropertyChanged(nameof(ShowTallyStatement));
        OnPropertyChanged(nameof(ShowScheduleStatement));
        OnPropertyChanged(nameof(DrilldownTitle));
        OnPropertyChanged(nameof(NavigationCaption));
        OnPropertyChanged(nameof(GroupFilterCaption));
    }

    public void Refresh()
    {
        LeftRows.Clear();
        RightRows.Clear();
        ScheduleRows.Clear();
        GroupRows.Clear();
        DrilldownRows.Clear();
        LeftTotal = RightTotal = GrossProfit = NetProfit = BalanceDifference = 0m;
        LedgerCount = 0;
        _generatedAt = null;
        OnPropertyChanged(nameof(GrossProfitText));
        OnPropertyChanged(nameof(NetProfitText));
        OnPropertyChanged(nameof(GeneratedText));

        var client = _clientContext.CurrentClient;
        if (client is null)
        {
            _preparedClientId = 0;
            _preparedFinancialYear = string.Empty;
            CompanyCaption = string.Empty;
            PeriodCaption = string.Empty;
            StatusText = $"Open a client before generating {StatementName}.";
            return;
        }

        CompanyCaption = string.IsNullOrWhiteSpace(client.LegalName)
            ? (string.IsNullOrWhiteSpace(client.ClientName) ? client.ClientCode : client.ClientName)
            : client.LegalName;
        PeriodCaption = $"Financial Year {client.FinancialYear}";

        try
        {
            if (_preparedClientId != client.Id ||
                !_preparedFinancialYear.Equals(client.FinancialYear, StringComparison.OrdinalIgnoreCase))
            {
                QueueBackgroundPrepare(client.Id, client.FinancialYear);
                return;
            }

            var groupIndex = BuildGroupIndex(_preparedMasters);
            var allSnapshots = _preparedLedgerTotals
                .Where(HasFinancialValue)
                .Select(snapshot => new LedgerPlacement(snapshot, ResolveRootGroup(snapshot.ParentGroup, groupIndex)))
                .ToArray();

            LedgerCount = allSnapshots.Length;
            BuildGroupRows(allSnapshots, groupIndex);
            if (SelectedGroupFilter.Length > 0 && GroupRows.All(row => Key(row.Label) != Key(SelectedGroupFilter)))
            {
                SelectedGroupFilter = string.Empty;
                if (IsGroupDetailScreen) _navigationMode = "Groups";
                RaiseNavigationProperties();
            }

            var scheduleIII = SelectedViewMode.Equals("Schedule III View", StringComparison.OrdinalIgnoreCase);
            if (_profitAndLoss)
            {
                if (scheduleIII) BuildScheduleIIIProfitAndLoss(allSnapshots, groupIndex);
                else BuildProfitAndLoss(allSnapshots, groupIndex);
            }
            else
            {
                if (scheduleIII) BuildScheduleIIIBalanceSheet(allSnapshots, groupIndex);
                else BuildBalanceSheet(allSnapshots, groupIndex);
            }

            if (IsGroupDetailScreen && SelectedGroupFilter.Length > 0)
            {
                var groupSnapshots = ApplyGroupFilter(allSnapshots, groupIndex);
                BuildGroupDrilldown(groupSnapshots, groupIndex);
            }
            else if (IsGroupsScreen)
            {
                StatusText = $"{StatementName} groups • {GroupRows.Count:N0} group(s). Choose a Group to open its details in this workspace.";
            }
            else if (IsLedgerScreen && SelectedLedgerName.Length > 0)
            {
                OpenLedgerAfterRefresh(SelectedLedgerName);
            }

            _generatedAt = DateTime.Now;
            _preparedClientId = client.Id;
            _preparedFinancialYear = client.FinancialYear;
            OnPropertyChanged(nameof(GrossProfitText));
            OnPropertyChanged(nameof(NetProfitText));
            OnPropertyChanged(nameof(GeneratedText));
        }
        catch (Exception exception)
        {
            StatusText = $"{StatementName} could not be generated: {exception.Message}";
        }
    }

    private LedgerPlacement[] ApplyGroupFilter(IReadOnlyList<LedgerPlacement> all, IReadOnlyDictionary<string, GroupInfo> groups)
    {
        if (string.IsNullOrWhiteSpace(SelectedGroupFilter)) return all.ToArray();
        return all.Where(item =>
            Key(item.RootGroup) == Key(SelectedGroupFilter) ||
            Key(item.Snapshot.ParentGroup) == Key(SelectedGroupFilter) ||
            IsDescendantOrDirect(item.Snapshot.ParentGroup, SelectedGroupFilter, groups)).ToArray();
    }

    private void BuildGroupRows(IReadOnlyList<LedgerPlacement> all, IReadOnlyDictionary<string, GroupInfo> groups)
    {
        var applicable = _profitAndLoss
            ? all.Where(item => ProfitAndLossRoots.Contains(Key(item.RootGroup))).ToArray()
            : all.Where(item => !ProfitAndLossRoots.Contains(Key(item.RootGroup))).ToArray();
        foreach (var root in applicable.GroupBy(item => item.RootGroup, StringComparer.OrdinalIgnoreCase).OrderBy(group => RootRank(group.Key)).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            GroupRows.Add(AggregateLine(root.Key, root.ToArray(), 0, "Root"));
        foreach (var group in groups.Values.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase))
        {
            var placements = applicable.Where(item => IsDescendantOrDirect(item.Snapshot.ParentGroup, group.Name, groups)).ToArray();
            if (placements.Length == 0 || GroupRows.Any(row => Key(row.Label) == Key(group.Name))) continue;
            GroupRows.Add(AggregateLine(group.Name, placements, 0, "Group"));
        }
    }


    private void BuildGroupDrilldown(IReadOnlyList<LedgerPlacement> placements, IReadOnlyDictionary<string, GroupInfo> groups)
    {
        if (placements.Count == 0)
        {
            StatusText = $"No accounting entries are available under {SelectedGroupFilter}.";
            return;
        }

        var selected = placements.ToArray();
        var root = selected.First().RootGroup;
        var leftSide = _profitAndLoss ? ExpenseRoots.Contains(Key(root)) : LiabilityRoots.Contains(Key(root));
        DrilldownRows.Add(AggregateLine(SelectedGroupFilter, selected, 0, "Group"));
        AddChildGroups(DrilldownRows, SelectedGroupFilter, selected, groups, 1, "Drilldown");
        StatusText = $"{StatementName} group detail • {SelectedGroupFilter} • {selected.Length:N0} ledger(s). Open a Sub-group or Ledger to continue the drill-down.";
    }

    private void BuildScheduleIIIProfitAndLoss(IReadOnlyList<LedgerPlacement> all, IReadOnlyDictionary<string, GroupInfo> groups)
    {
        var profitLoss = all.Where(item => ProfitAndLossRoots.Contains(Key(item.RootGroup))).ToArray();
        var purchases = profitLoss.Where(item => Key(item.RootGroup) == Key("Purchase Accounts")).ToArray();
        var directExpenses = profitLoss.Where(item => DirectExpenseRoots.Contains(Key(item.RootGroup)) && Key(item.RootGroup) != Key("Purchase Accounts")).ToArray();
        var otherExpenses = profitLoss.Where(item => IndirectExpenseRoots.Contains(Key(item.RootGroup))).ToArray();
        var operations = profitLoss.Where(item => Key(item.RootGroup) == Key("Sales Accounts")).ToArray();
        var otherIncome = profitLoss.Where(item =>
            (DirectIncomeRoots.Contains(Key(item.RootGroup)) && Key(item.RootGroup) != Key("Sales Accounts")) ||
            IndirectIncomeRoots.Contains(Key(item.RootGroup))).ToArray();

        var directExpense = SignedDebitValue(profitLoss.Where(item => DirectExpenseRoots.Contains(Key(item.RootGroup))));
        var directIncome = SignedCreditValue(profitLoss.Where(item => DirectIncomeRoots.Contains(Key(item.RootGroup))));
        var indirectExpense = SignedDebitValue(profitLoss.Where(item => IndirectExpenseRoots.Contains(Key(item.RootGroup))));
        var indirectIncome = SignedCreditValue(profitLoss.Where(item => IndirectIncomeRoots.Contains(Key(item.RootGroup))));
        GrossProfit = Round(directIncome - directExpense);
        NetProfit = Round(GrossProfit + indirectIncome - indirectExpense);
        var expenseValue = SignedDebitValue(profitLoss.Where(item => ExpenseRoots.Contains(Key(item.RootGroup))));
        var incomeValue = SignedCreditValue(profitLoss.Where(item => !ExpenseRoots.Contains(Key(item.RootGroup))));

        ScheduleRows.Add(HeadingLine("INCOME"));
        AddScheduleSection(ScheduleRows, "Revenue from Operations", operations, groups);
        AddScheduleSection(ScheduleRows, "Other Income", otherIncome, groups);
        ScheduleRows.Add(ScheduleTotalLine("Total Income", incomeValue, creditNature: true));

        ScheduleRows.Add(HeadingLine("EXPENSES"));
        AddScheduleSection(ScheduleRows, "Cost of Purchases / Materials", purchases, groups);
        AddScheduleSection(ScheduleRows, "Direct Expenses", directExpenses, groups);
        AddScheduleSection(ScheduleRows, "Other Expenses", otherExpenses, groups);
        ScheduleRows.Add(ScheduleTotalLine("Total Expenses", expenseValue, creditNature: false));
        ScheduleRows.Add(NetProfit >= 0m
            ? ScheduleTotalLine("Profit for the Period", NetProfit, creditNature: true)
            : ScheduleTotalLine("Loss for the Period", Math.Abs(NetProfit), creditNature: false));

        LeftTotal = Round(expenseValue);
        RightTotal = Round(incomeValue);
        BalanceDifference = Round(incomeValue - expenseValue - NetProfit);
        StatusText = $"Schedule III vertical Profit & Loss generated on demand • {LedgerCount:N0} active ledger(s) • Gross {GrossProfitText} • Net {NetProfitText}.";
    }

    private void BuildScheduleIIIBalanceSheet(IReadOnlyList<LedgerPlacement> all, IReadOnlyDictionary<string, GroupInfo> groups)
    {
        var profitLoss = all.Where(item => ProfitAndLossRoots.Contains(Key(item.RootGroup))).ToArray();
        var balance = all.Where(item => !ProfitAndLossRoots.Contains(Key(item.RootGroup))).ToArray();
        var directExpense = SignedDebitValue(profitLoss.Where(item => DirectExpenseRoots.Contains(Key(item.RootGroup))));
        var directIncome = SignedCreditValue(profitLoss.Where(item => DirectIncomeRoots.Contains(Key(item.RootGroup))));
        var indirectExpense = SignedDebitValue(profitLoss.Where(item => IndirectExpenseRoots.Contains(Key(item.RootGroup))));
        var indirectIncome = SignedCreditValue(profitLoss.Where(item => IndirectIncomeRoots.Contains(Key(item.RootGroup))));
        GrossProfit = Round(directIncome - directExpense);
        NetProfit = Round(GrossProfit + indirectIncome - indirectExpense);

        var equity = balance.Where(item => Key(item.RootGroup) == Key("Capital Account")).ToArray();
        var nonCurrentLiabilities = balance.Where(item => Key(item.RootGroup) == Key("Loans (Liability)")).ToArray();
        var currentLiabilities = balance.Where(item => Key(item.RootGroup) == Key("Current Liabilities")).ToArray();
        var fixedAssets = balance.Where(item => Key(item.RootGroup) == Key("Fixed Assets")).ToArray();
        var investments = balance.Where(item => Key(item.RootGroup) == Key("Investments")).ToArray();
        var currentAssets = balance.Where(item => Key(item.RootGroup) == Key("Current Assets")).ToArray();
        var miscAssets = balance.Where(item => Key(item.RootGroup).Contains("MISC. EXPENSES")).ToArray();
        var known = equity.Concat(nonCurrentLiabilities).Concat(currentLiabilities).Concat(fixedAssets).Concat(investments).Concat(currentAssets).Concat(miscAssets)
            .Select(item => item.Snapshot.LedgerName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var otherLiabilities = balance.Where(item => !known.Contains(item.Snapshot.LedgerName) && item.Snapshot.ClosingBalance < -Tolerance).ToArray();
        var residualAssets = balance.Where(item => !known.Contains(item.Snapshot.LedgerName) && item.Snapshot.ClosingBalance >= -Tolerance).ToArray();

        var liabilities = SignedCreditValue(equity.Concat(nonCurrentLiabilities).Concat(currentLiabilities).Concat(otherLiabilities));
        var assets = SignedDebitValue(fixedAssets.Concat(investments).Concat(currentAssets).Concat(miscAssets).Concat(residualAssets));
        if (NetProfit > Tolerance) liabilities += NetProfit;
        else if (NetProfit < -Tolerance) assets += Math.Abs(NetProfit);
        var rawDifference = Round(liabilities - assets);
        BalanceDifference = rawDifference;

        ScheduleRows.Add(HeadingLine("EQUITY AND LIABILITIES"));
        AddScheduleSection(ScheduleRows, "Equity", equity, groups);
        AddScheduleSection(ScheduleRows, "Non-current Liabilities", nonCurrentLiabilities, groups);
        AddScheduleSection(ScheduleRows, "Current Liabilities", currentLiabilities, groups);
        AddScheduleSection(ScheduleRows, "Other Liabilities", otherLiabilities, groups);
        if (NetProfit > Tolerance) ScheduleRows.Add(ScheduleTotalLine("Profit for the Period", NetProfit, creditNature: true));
        var adjustedLiabilities = liabilities;
        if (rawDifference < -Tolerance)
        {
            ScheduleRows.Add(ScheduleTotalLine("Unreconciled Difference", Math.Abs(rawDifference), creditNature: true, rowType: "Difference"));
            adjustedLiabilities += Math.Abs(rawDifference);
        }
        ScheduleRows.Add(ScheduleTotalLine("Total Equity & Liabilities", adjustedLiabilities, creditNature: true));

        ScheduleRows.Add(HeadingLine("ASSETS"));
        AddScheduleSection(ScheduleRows, "Property, Plant & Equipment", fixedAssets, groups);
        AddScheduleSection(ScheduleRows, "Non-current Investments", investments, groups);
        AddScheduleSection(ScheduleRows, "Current Assets", currentAssets, groups);
        AddScheduleSection(ScheduleRows, "Other Assets", miscAssets.Concat(residualAssets).ToArray(), groups);
        if (NetProfit < -Tolerance) ScheduleRows.Add(ScheduleTotalLine("Loss for the Period", Math.Abs(NetProfit), creditNature: false));
        var adjustedAssets = assets;
        if (rawDifference > Tolerance)
        {
            ScheduleRows.Add(ScheduleTotalLine("Unreconciled Difference", rawDifference, creditNature: false, rowType: "Difference"));
            adjustedAssets += rawDifference;
        }
        ScheduleRows.Add(ScheduleTotalLine("Total Assets", adjustedAssets, creditNature: false));

        LeftTotal = Round(adjustedLiabilities);
        RightTotal = Round(adjustedAssets);
        StatusText = Math.Abs(rawDifference) <= 0.01m
            ? $"Schedule III vertical Balance Sheet generated on demand • balanced • {LedgerCount:N0} active ledger(s)."
            : $"Schedule III vertical Balance Sheet generated with disclosed difference {Math.Abs(rawDifference):N2}.";
    }

    private static AccountingStatementLine HeadingLine(string label) => new()
    {
        Label = label,
        RowType = "ScheduleHeader",
        IndentLevel = 0
    };

    private static AccountingStatementLine ScheduleTotalLine(string label, decimal amount, bool creditNature, string rowType = "Result") => new()
    {
        Label = label,
        RowType = rowType,
        IndentLevel = 0,
        ClosingBalance = creditNature ? -Round(Math.Abs(amount)) : Round(Math.Abs(amount))
    };

    private static void AddScheduleSection(ObservableCollection<AccountingStatementLine> destination, string heading, IReadOnlyCollection<LedgerPlacement> placements, IReadOnlyDictionary<string, GroupInfo> groups)
    {
        if (placements.Count == 0) return;
        destination.Add(AggregateLine(heading, placements, 0, "Schedule"));
        foreach (var root in placements.GroupBy(item => item.RootGroup, StringComparer.OrdinalIgnoreCase).OrderBy(group => RootRank(group.Key)).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rows = root.ToArray();
            destination.Add(AggregateLine(root.Key, rows, 1, "Group"));
            AddChildGroups(destination, root.Key, rows, groups, 2, "Schedule");
        }
    }

    private void BuildProfitAndLoss(IReadOnlyList<LedgerPlacement> all, IReadOnlyDictionary<string, GroupInfo> groups)
    {
        var profitLoss = all.Where(item => ProfitAndLossRoots.Contains(Key(item.RootGroup))).ToArray();
        var expenses = profitLoss.Where(item => ExpenseRoots.Contains(Key(item.RootGroup))).ToArray();
        var income = profitLoss.Where(item => !ExpenseRoots.Contains(Key(item.RootGroup))).ToArray();

        AddHierarchy(LeftRows, expenses, groups, rowTypePrefix: "Expense");
        AddHierarchy(RightRows, income, groups, rowTypePrefix: "Income");

        var directExpense = SignedDebitValue(profitLoss.Where(item => DirectExpenseRoots.Contains(Key(item.RootGroup))));
        var directIncome = SignedCreditValue(profitLoss.Where(item => DirectIncomeRoots.Contains(Key(item.RootGroup))));
        var indirectExpense = SignedDebitValue(profitLoss.Where(item => IndirectExpenseRoots.Contains(Key(item.RootGroup))));
        var indirectIncome = SignedCreditValue(profitLoss.Where(item => IndirectIncomeRoots.Contains(Key(item.RootGroup))));

        GrossProfit = Round(directIncome - directExpense);
        NetProfit = Round(GrossProfit + indirectIncome - indirectExpense);

        var expenseValue = SignedDebitValue(expenses);
        var incomeValue = SignedCreditValue(income);
        if (NetProfit > Tolerance)
        {
            LeftRows.Add(ResultLine("Net Profit", NetProfit, debitSide: true));
            expenseValue += NetProfit;
        }
        else if (NetProfit < -Tolerance)
        {
            RightRows.Add(ResultLine("Net Loss", Math.Abs(NetProfit), debitSide: false));
            incomeValue += Math.Abs(NetProfit);
        }

        LeftTotal = Round(expenseValue);
        RightTotal = Round(incomeValue);
        BalanceDifference = Round(LeftTotal - RightTotal);
        StatusText = Math.Abs(BalanceDifference) <= 0.01m
            ? $"Profit & Loss generated on demand • {LedgerCount:N0} active ledger(s) • Gross {GrossProfitText} • Net {NetProfitText}."
            : $"Profit & Loss generated, but the two sides differ by {Math.Abs(BalanceDifference):N2}. Review opening balances / imported postings.";
    }

    private void BuildBalanceSheet(IReadOnlyList<LedgerPlacement> all, IReadOnlyDictionary<string, GroupInfo> groups)
    {
        var profitLoss = all.Where(item => ProfitAndLossRoots.Contains(Key(item.RootGroup))).ToArray();
        var balance = all.Where(item => !ProfitAndLossRoots.Contains(Key(item.RootGroup))).ToArray();

        var directExpense = SignedDebitValue(profitLoss.Where(item => DirectExpenseRoots.Contains(Key(item.RootGroup))));
        var directIncome = SignedCreditValue(profitLoss.Where(item => DirectIncomeRoots.Contains(Key(item.RootGroup))));
        var indirectExpense = SignedDebitValue(profitLoss.Where(item => IndirectExpenseRoots.Contains(Key(item.RootGroup))));
        var indirectIncome = SignedCreditValue(profitLoss.Where(item => IndirectIncomeRoots.Contains(Key(item.RootGroup))));
        GrossProfit = Round(directIncome - directExpense);
        NetProfit = Round(GrossProfit + indirectIncome - indirectExpense);

        var left = new List<LedgerPlacement>();
        var right = new List<LedgerPlacement>();
        foreach (var item in balance)
        {
            var rootKey = Key(item.RootGroup);
            if (LiabilityRoots.Contains(rootKey)) left.Add(item);
            else if (AssetRoots.Contains(rootKey)) right.Add(item);
            else if (item.Snapshot.ClosingBalance < -Tolerance) left.Add(item);
            else right.Add(item);
        }

        AddHierarchy(LeftRows, left, groups, rowTypePrefix: "Liability");
        AddHierarchy(RightRows, right, groups, rowTypePrefix: "Asset");

        var liabilities = SignedCreditValue(left);
        var assets = SignedDebitValue(right);
        if (NetProfit > Tolerance)
        {
            LeftRows.Add(ResultLine("Current Period Profit", NetProfit, debitSide: false));
            liabilities += NetProfit;
        }
        else if (NetProfit < -Tolerance)
        {
            RightRows.Add(ResultLine("Current Period Loss", Math.Abs(NetProfit), debitSide: true));
            assets += Math.Abs(NetProfit);
        }

        var rawDifference = Round(liabilities - assets);
        BalanceDifference = rawDifference;
        if (rawDifference > Tolerance)
        {
            RightRows.Add(ResultLine("Difference in Opening / Imported Postings", rawDifference, debitSide: true, rowType: "Difference"));
            assets += rawDifference;
        }
        else if (rawDifference < -Tolerance)
        {
            LeftRows.Add(ResultLine("Difference in Opening / Imported Postings", Math.Abs(rawDifference), debitSide: false, rowType: "Difference"));
            liabilities += Math.Abs(rawDifference);
        }

        LeftTotal = Round(liabilities);
        RightTotal = Round(assets);
        StatusText = Math.Abs(rawDifference) <= 0.01m
            ? $"Balance Sheet generated on demand • balanced • {LedgerCount:N0} active ledger(s) • Current {NetProfitText}."
            : $"Balance Sheet generated with a disclosed difference of {Math.Abs(rawDifference):N2}. Review Tally opening balances or incomplete AAH postings; the difference is shown as a separate line and is not silently absorbed.";
    }

    private static void AddHierarchy(
        ObservableCollection<AccountingStatementLine> destination,
        IReadOnlyCollection<LedgerPlacement> placements,
        IReadOnlyDictionary<string, GroupInfo> groups,
        string rowTypePrefix)
    {
        foreach (var rootGroup in placements
                     .GroupBy(item => item.RootGroup, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => RootRank(group.Key))
                     .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rootPlacements = rootGroup.ToArray();
            destination.Add(AggregateLine(rootGroup.Key, rootPlacements, 0, "Root"));
            AddChildGroups(destination, rootGroup.Key, rootPlacements, groups, 1, rowTypePrefix);
        }
    }

    private static void AddChildGroups(
        ObservableCollection<AccountingStatementLine> destination,
        string groupName,
        IReadOnlyCollection<LedgerPlacement> placements,
        IReadOnlyDictionary<string, GroupInfo> groups,
        int indent,
        string rowTypePrefix)
    {
        var directLedgers = placements
            .Where(item => Key(item.Snapshot.ParentGroup) == Key(groupName))
            .OrderBy(item => item.Snapshot.LedgerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var item in directLedgers)
            destination.Add(LedgerLine(item.Snapshot, indent));

        var childGroups = groups.Values
            .Where(group => Key(group.Parent) == Key(groupName))
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var child in childGroups)
        {
            var childPlacements = placements
                .Where(item => IsDescendantOrDirect(item.Snapshot.ParentGroup, child.Name, groups))
                .ToArray();
            if (childPlacements.Length == 0) continue;
            destination.Add(AggregateLine(child.Name, childPlacements, indent, "Group"));
            AddChildGroups(destination, child.Name, childPlacements, groups, indent + 1, rowTypePrefix);
        }

        // Ledgers whose parent Group is missing from Master are still shown under the
        // root rather than disappearing from the statement.
        if (indent == 1 || (rowTypePrefix == "Schedule" && indent == 2))
        {
            var represented = destination
                .Where(line => line.RowType == "Ledger")
                .Select(line => Key(line.Label))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in placements
                         .Where(item => !represented.Contains(Key(item.Snapshot.LedgerName)))
                         .OrderBy(item => item.Snapshot.ParentGroup, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.Snapshot.LedgerName, StringComparer.OrdinalIgnoreCase))
            {
                destination.Add(LedgerLine(item.Snapshot, indent));
            }
        }
    }

    private static bool IsDescendantOrDirect(
        string ledgerParent,
        string candidateGroup,
        IReadOnlyDictionary<string, GroupInfo> groups)
    {
        var current = ledgerParent?.Trim() ?? string.Empty;
        var target = Key(candidateGroup);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; depth < 64 && current.Length > 0; depth++)
        {
            var key = Key(current);
            if (key == target) return true;
            if (!visited.Add(key) || !groups.TryGetValue(key, out var group)) return false;
            current = group.Parent;
        }
        return false;
    }

    private static AccountingStatementLine LedgerLine(LedgerTotalSnapshot snapshot, int indent) => new()
    {
        Label = snapshot.LedgerName,
        RowType = "Ledger",
        IndentLevel = indent,
        OpeningBalance = Round(snapshot.OpeningBalance),
        TotalDebit = Round(snapshot.TotalDebit),
        TotalCredit = Round(snapshot.TotalCredit),
        ClosingBalance = Round(snapshot.ClosingBalance),
        MovementCount = snapshot.MovementCount
    };

    private static AccountingStatementLine AggregateLine(
        string label,
        IReadOnlyCollection<LedgerPlacement> placements,
        int indent,
        string rowType) => new()
    {
        Label = label,
        RowType = rowType,
        IndentLevel = indent,
        OpeningBalance = Round(placements.Sum(item => item.Snapshot.OpeningBalance)),
        TotalDebit = Round(placements.Sum(item => item.Snapshot.TotalDebit)),
        TotalCredit = Round(placements.Sum(item => item.Snapshot.TotalCredit)),
        ClosingBalance = Round(placements.Sum(item => item.Snapshot.ClosingBalance)),
        MovementCount = placements.Sum(item => item.Snapshot.MovementCount)
    };

    private static AccountingStatementLine ResultLine(
        string label,
        decimal amount,
        bool debitSide,
        string rowType = "Result") => new()
    {
        Label = label,
        RowType = rowType,
        IndentLevel = 0,
        ClosingBalance = debitSide ? Round(amount) : -Round(amount)
    };

    private static IReadOnlyDictionary<string, GroupInfo> BuildGroupIndex(IEnumerable<MasterRecord> masters) =>
        masters
            .Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && !master.IsArchived)
            .Where(master => !string.IsNullOrWhiteSpace(master.Name))
            .GroupBy(master => Key(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new GroupInfo(group.First().Name.Trim(), group.First().Parent?.Trim() ?? "Primary"),
                StringComparer.OrdinalIgnoreCase);

    private static string ResolveRootGroup(string? parent, IReadOnlyDictionary<string, GroupInfo> groups)
    {
        var current = string.IsNullOrWhiteSpace(parent) ? "Primary" : parent.Trim();
        var last = current;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; depth < 64; depth++)
        {
            var key = Key(current);
            if (key.Length == 0 || !visited.Add(key)) break;
            if (!groups.TryGetValue(key, out var group)) return last;
            last = group.Name;
            if (group.Parent.Equals("Primary", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(group.Parent))
                return group.Name;
            current = group.Parent;
        }
        return last;
    }


    private static int RootRank(string? root)
    {
        var key = Key(root);
        if (key == Key("Capital Account") || key == Key("Fixed Assets") || key == Key("Purchase Accounts") || key == Key("Sales Accounts")) return 10;
        if (key == Key("Loans (Liability)") || key == Key("Investments") || key == Key("Direct Expenses") || key == Key("Direct Expense") || key == Key("Direct Incomes") || key == Key("Direct Income")) return 20;
        if (key == Key("Current Liabilities") || key == Key("Current Assets") || key == Key("Indirect Expenses") || key == Key("Indirect Expense") || key == Key("Indirect Incomes") || key == Key("Indirect Income")) return 30;
        if (key == Key("Misc. Expenses (ASSET)") || key == Key("Misc. Expenses (Asset)")) return 40;
        return 100;
    }

    private static decimal SignedDebitValue(IEnumerable<LedgerPlacement> placements) =>
        Round(placements.Sum(item => item.Snapshot.ClosingBalance));

    private static decimal SignedCreditValue(IEnumerable<LedgerPlacement> placements) =>
        Round(-placements.Sum(item => item.Snapshot.ClosingBalance));

    private static bool HasFinancialValue(LedgerTotalSnapshot snapshot) =>
        snapshot.MovementCount > 0 || Math.Abs(snapshot.OpeningBalance) > Tolerance ||
        Math.Abs(snapshot.TotalDebit) > Tolerance || Math.Abs(snapshot.TotalCredit) > Tolerance ||
        Math.Abs(snapshot.ClosingBalance) > Tolerance;

    private static string FormatSignedBalance(decimal value)
    {
        if (Math.Abs(value) <= Tolerance) return "0.00";
        return $"{Math.Abs(value):N2} {(value > 0m ? "Dr" : "Cr")}";
    }

    private static string FormatResult(decimal value, string positiveLabel, string negativeLabel)
    {
        if (Math.Abs(value) <= Tolerance) return "0.00";
        return $"{Math.Abs(value):N2} {(value > 0m ? positiveLabel : negativeLabel)}";
    }

    private static HashSet<string> Keys(params string[] names) =>
        names.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Key(string? value) => TallyMasterIdentity.Key(value ?? string.Empty);
    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private sealed record GroupInfo(string Name, string Parent);
    private sealed record LedgerPlacement(LedgerTotalSnapshot Snapshot, string RootGroup);
}
