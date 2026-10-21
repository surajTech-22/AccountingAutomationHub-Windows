using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Application.Journals;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Application.Purchases;
using AccountingAutomationHub.Application.Sales;
using AccountingAutomationHub.Desktop.Behaviors;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Accounting;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Accounting;
using AccountingAutomationHub.Infrastructure.Tally;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AccountingReportsWindow : Window
{
    private static readonly string[] AahReports =
    [
        "Trial Balance", "Opening Balances", "Day Book", "Cash Book", "Bank Book", "Ledger Vouchers",
        "Sales Register", "Purchase Register", "Journal Register", "Debit Note Register", "Credit Note Register",
        "Outstanding Receivables", "Outstanding Payables", "Cash Flow", "Funds Flow", "Ratio Analysis", "Statistics", "Stock Summary"
    ];

    private readonly ClientProfile _client;
    private readonly LedgerTotalService _ledgerTotals;
    private readonly IBankingRepository _banking;
    private readonly IPurchaseRecordRepository _purchases;
    private readonly ISalesRecordRepository _sales;
    private readonly IJournalRepository _journals;
    private readonly IMasterRepository _masters;
    private readonly IStockMasterRepository _stock;
    private readonly TallyLiveConnectionCoordinator _tally;
    private readonly ObservableCollection<AccountingReportRow> _rows = [];
    private ICollectionView? _view;
    private bool _loading;

    public AccountingReportsWindow(
        ClientProfile client,
        LedgerTotalService ledgerTotals,
        IBankingRepository banking,
        IPurchaseRecordRepository purchases,
        ISalesRecordRepository sales,
        IJournalRepository journals,
        IMasterRepository masters,
        IStockMasterRepository stock,
        TallyLiveConnectionCoordinator tally,
        string initialReport = "Trial Balance")
    {
        InitializeComponent();
        _client = client;
        _ledgerTotals = ledgerTotals;
        _banking = banking;
        _purchases = purchases;
        _sales = sales;
        _journals = journals;
        _masters = masters;
        _stock = stock;
        _tally = tally;

        CompanyText.Text = $"{DisplayCompany(client)}  •  FY {client.FinancialYear}";
        // Fix123: ERP 9 keeps normal live-report access. The five-second background
        // refresh is what yields when busy; report functionality itself is never removed.
        SourceCombo.ItemsSource = new[] { "AAH Books", "Tally Live" };
        SourceCombo.SelectedIndex = 0;
        if (_tally.IsErp9CompatibilityMode)
            SourceCombo.ToolTip = "ERP 9 Live: supported Tally reports remain available; the five-second background refresh is low-priority and non-blocking.";
        var (from, to) = FinancialYearRange(client.FinancialYear);
        FromDatePicker.SelectedDate = from;
        ToDatePicker.SelectedDate = to;
        LedgerCombo.ItemsSource = _ledgerTotals.GetAll(client.Id, client.FinancialYear).Select(x => x.LedgerName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        ReportGrid.ItemsSource = _rows;
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = SearchFilter;
        ConfigureReportList(initialReport);
        Loaded += async (_, _) => await RefreshReportAsync();
    }

    private static string DisplayCompany(ClientProfile client) =>
        !string.IsNullOrWhiteSpace(client.LegalName) ? client.LegalName :
        !string.IsNullOrWhiteSpace(client.ClientName) ? client.ClientName : client.ClientCode;

    private void ConfigureReportList(string? preferred = null)
    {
        var tally = SourceCombo.SelectedItem?.ToString()?.Equals("Tally Live", StringComparison.OrdinalIgnoreCase) == true;
        var options = !tally
            ? AahReports
            : _tally.IsErp9CompatibilityMode
                ? TallyReportCatalog.Erp9DisplayNames
                : TallyReportCatalog.DisplayNames;
        ReportCombo.ItemsSource = options;
        var match = options.FirstOrDefault(x => x.Equals(preferred ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        ReportCombo.SelectedItem = match ?? options.FirstOrDefault();
        LedgerCombo.IsEnabled = !tally && ReportCombo.SelectedItem?.ToString()?.Equals("Ledger Vouchers", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ConfigureReportList();
        await RefreshReportAsync();
    }

    private async void ReportCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        LedgerCombo.IsEnabled = SourceCombo.SelectedItem?.ToString()?.Equals("AAH Books", StringComparison.OrdinalIgnoreCase) == true &&
                                ReportCombo.SelectedItem?.ToString()?.Equals("Ledger Vouchers", StringComparison.OrdinalIgnoreCase) == true;
        await RefreshReportAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshReportAsync();

    private async Task RefreshReportAsync()
    {
        if (_loading || ReportCombo.SelectedItem is null) return;
        _loading = true;
        try
        {
            var report = ReportCombo.SelectedItem.ToString() ?? string.Empty;
            var from = FromDatePicker.SelectedDate ?? FinancialYearRange(_client.FinancialYear).From;
            var to = ToDatePicker.SelectedDate ?? FinancialYearRange(_client.FinancialYear).To;
            if (to < from) (from, to) = (to, from);
            ReportCaptionText.Text = $"{report}  •  {from:dd.MM.yyyy} to {to:dd.MM.yyyy}";
            StatusText.Text = "Loading report…";
            _rows.Clear();

            if (SourceCombo.SelectedItem?.ToString()?.Equals("Tally Live", StringComparison.OrdinalIgnoreCase) == true)
                await LoadTallyReport(report, from, to);
            else
                LoadAahReport(report, from, to);

            _view?.Refresh();
            ConfigureColumns();
            UpdateRowCount();
            if (string.IsNullOrWhiteSpace(StatusText.Text)) StatusText.Text = $"{report} ready from Accounting Automation Hub books.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Report could not be generated: {ex.Message}";
        }
        finally { _loading = false; }
    }

    private async Task LoadTallyReport(string report, DateTime from, DateTime to)
    {
        var snapshot = await _tally.GetReportAsync(report, from, to);
        foreach (var row in snapshot.Rows)
            _rows.Add(new AccountingReportRow { Particulars = row.Particulars, Detail = row.Detail, Debit = row.Debit, Credit = row.Credit, Amount = row.Amount });
        StatusText.Text = snapshot.IsAvailable
            ? $"Live Tally report: {snapshot.ReportId} • {snapshot.Rows.Count:N0} row(s)."
            : $"Tally report is unavailable for the linked company: {snapshot.Status} {snapshot.Error}".Trim();
    }

    private void LoadAahReport(string report, DateTime from, DateTime to)
    {
        _ledgerTotals.Invalidate(_client.Id, _client.FinancialYear);
        var snapshots = _ledgerTotals.GetAllForPeriod(_client.Id, _client.FinancialYear, from, to);
        var masters = _masters.Get(_client.Id, _client.FinancialYear, includeArchived: false, string.Empty);
        var groupIndex = BuildGroupIndex(masters);
        switch (report)
        {
            case "Trial Balance": LoadTrialBalance(snapshots); break;
            case "Opening Balances": LoadOpeningBalances(snapshots); break;
            case "Day Book": LoadDayBook(snapshots, from, to); break;
            case "Cash Book": LoadBookByGroup(snapshots, groupIndex, "Cash-in-Hand", from, to); break;
            case "Bank Book": LoadBookByGroup(snapshots, groupIndex, "Bank Accounts", from, to); break;
            case "Ledger Vouchers": LoadLedgerVouchers(from, to); break;
            case "Sales Register": LoadSalesRegister(from, to, creditNotesOnly: false); break;
            case "Credit Note Register": LoadSalesRegister(from, to, creditNotesOnly: true); break;
            case "Purchase Register": LoadPurchaseRegister(from, to, debitNotesOnly: false); break;
            case "Debit Note Register": LoadPurchaseRegister(from, to, debitNotesOnly: true); break;
            case "Journal Register": LoadJournalRegister(from, to); break;
            case "Outstanding Receivables": LoadOutstanding(snapshots, groupIndex, "Sundry Debtors", receivable: true); break;
            case "Outstanding Payables": LoadOutstanding(snapshots, groupIndex, "Sundry Creditors", receivable: false); break;
            case "Cash Flow": LoadCashFlow(snapshots, groupIndex, from, to); break;
            case "Funds Flow": LoadFundsFlow(snapshots, groupIndex); break;
            case "Ratio Analysis": LoadRatioAnalysis(snapshots, groupIndex); break;
            case "Statistics": LoadStatistics(snapshots, from, to); break;
            case "Stock Summary": LoadStockSummary(); break;
            default: throw new InvalidOperationException($"AAH report '{report}' is not implemented.");
        }
    }

    private void LoadTrialBalance(IEnumerable<LedgerTotalSnapshot> snapshots)
    {
        foreach (var s in snapshots.Where(HasFinancialValue).OrderBy(x => x.ParentGroup).ThenBy(x => x.LedgerName))
            _rows.Add(new AccountingReportRow { Particulars = s.LedgerName, Group = s.ParentGroup, Opening = s.OpeningBalance, Debit = s.TotalDebit, Credit = s.TotalCredit, Closing = s.ClosingBalance, Count = s.MovementCount });
    }

    private void LoadOpeningBalances(IEnumerable<LedgerTotalSnapshot> snapshots)
    {
        foreach (var s in snapshots.Where(x => Math.Abs(x.OpeningBalance) > 0.005m).OrderBy(x => x.ParentGroup).ThenBy(x => x.LedgerName))
            _rows.Add(new AccountingReportRow { Particulars = s.LedgerName, Group = s.ParentGroup, Opening = s.OpeningBalance });
    }

    private void LoadDayBook(IEnumerable<LedgerTotalSnapshot> snapshots, DateTime from, DateTime to)
    {
        foreach (var ledger in snapshots.OrderBy(x => x.LedgerName))
            foreach (var m in _ledgerTotals.GetMovements(_client.Id, _client.FinancialYear, ledger.LedgerName, from, to))
                _rows.Add(MovementRow(ledger.LedgerName, ledger.ParentGroup, m));
        SortRowsByDate();
    }

    private void LoadBookByGroup(IEnumerable<LedgerTotalSnapshot> snapshots, IReadOnlyDictionary<string, GroupInfo> groups, string targetGroup, DateTime from, DateTime to)
    {
        foreach (var ledger in snapshots.Where(s => IsInGroup(s.ParentGroup, targetGroup, groups)).OrderBy(x => x.LedgerName))
            foreach (var m in _ledgerTotals.GetMovements(_client.Id, _client.FinancialYear, ledger.LedgerName, from, to))
                _rows.Add(MovementRow(ledger.LedgerName, ledger.ParentGroup, m));
        SortRowsByDate();
    }

    private void LoadLedgerVouchers(DateTime from, DateTime to)
    {
        var ledgerName = LedgerCombo.Text?.Trim() ?? string.Empty;
        if (ledgerName.Length == 0) ledgerName = LedgerCombo.SelectedItem?.ToString()?.Trim() ?? string.Empty;
        if (ledgerName.Length == 0) throw new InvalidOperationException("Select a Ledger first.");
        var snapshot = _ledgerTotals.Get(_client.Id, _client.FinancialYear, ledgerName);
        foreach (var m in _ledgerTotals.GetMovements(_client.Id, _client.FinancialYear, ledgerName, from, to))
            _rows.Add(MovementRow(ledgerName, snapshot?.ParentGroup ?? string.Empty, m));
        StatusText.Text = snapshot is null ? $"Ledger '{ledgerName}' has no AAH book balance." : $"Opening {BalanceText(snapshot.OpeningBalance)} • Closing {BalanceText(snapshot.ClosingBalance)}";
    }

    private void LoadSalesRegister(DateTime from, DateTime to, bool creditNotesOnly)
    {
        var groups = _sales.GetRecords(_client.Id, _client.FinancialYear)
            .Where(r => (r.GstDate ?? r.DocumentDate) is DateTime d && d.Date >= from.Date && d.Date <= to.Date)
            .Where(r => !creditNotesOnly || IsCreditNote(r.Destination, r.DocumentType))
            .Where(r => creditNotesOnly || !IsCreditNote(r.Destination, r.DocumentType))
            .GroupBy(r => new { Date = (r.GstDate ?? r.DocumentDate)!.Value.Date, r.DocumentNumber, r.CustomerName, r.Destination, r.DocumentType });
        foreach (var g in groups.OrderBy(x => x.Key.Date).ThenBy(x => x.Key.DocumentNumber))
        {
            var rows = g.ToArray();
            _rows.Add(new AccountingReportRow
            {
                Date = g.Key.Date, VoucherType = creditNotesOnly ? "Credit Note" : "Sales", VoucherNumber = g.Key.DocumentNumber,
                Particulars = g.Key.CustomerName, Detail = string.Join("; ", rows.Select(r => r.Narration).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(2)),
                Amount = Round(rows.Sum(r => r.DocumentValue)), Debit = creditNotesOnly ? 0m : Round(rows.Sum(r => r.DocumentValue)),
                Credit = creditNotesOnly ? Round(rows.Sum(r => r.DocumentValue)) : 0m, Quantity = Round(rows.Sum(r => r.BilledQuantity))
            });
        }
    }

    private void LoadPurchaseRegister(DateTime from, DateTime to, bool debitNotesOnly)
    {
        var groups = _purchases.GetRecords(_client.Id, _client.FinancialYear)
            .Where(r => r.GstDate is DateTime d && d.Date >= from.Date && d.Date <= to.Date)
            .Where(r => !debitNotesOnly || IsDebitNote(r.Destination, r.DocumentType))
            .Where(r => debitNotesOnly || !IsDebitNote(r.Destination, r.DocumentType))
            .GroupBy(r => new { Date = r.GstDate!.Value.Date, r.DocumentNumber, r.SupplierName, r.Destination, r.DocumentType });
        foreach (var g in groups.OrderBy(x => x.Key.Date).ThenBy(x => x.Key.DocumentNumber))
        {
            var rows = g.ToArray();
            var amount = Round(rows.Sum(r => r.DocumentValue));
            var debitNoteReverse = rows.Any(r =>
                r.Destination.Equals("Debit Note Reverse", StringComparison.OrdinalIgnoreCase));
            _rows.Add(new AccountingReportRow
            {
                Date = g.Key.Date, VoucherType = debitNotesOnly ? "Debit Note" : "Purchase", VoucherNumber = g.Key.DocumentNumber,
                Particulars = g.Key.SupplierName, Detail = string.Join("; ", rows.Select(r => r.GstApplicability).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()),
                Amount = amount,
                Debit = debitNotesOnly ? (debitNoteReverse ? amount : 0m) : amount,
                Credit = debitNotesOnly ? (debitNoteReverse ? 0m : amount) : 0m
            });
        }
    }

    private void LoadJournalRegister(DateTime from, DateTime to)
    {
        foreach (var v in _journals.GetVouchers(_client.Id, _client.FinancialYear).Where(v => v.VoucherDate.Date >= from.Date && v.VoucherDate.Date <= to.Date).OrderBy(v => v.VoucherDate))
        {
            var debit = Round(v.Lines.Where(l => l.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase)).Sum(l => Math.Abs(l.Amount)));
            var credit = Round(v.Lines.Where(l => l.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase)).Sum(l => Math.Abs(l.Amount)));
            _rows.Add(new AccountingReportRow { Date = v.VoucherDate.Date, VoucherType = v.VoucherType, VoucherNumber = v.VoucherNumber, Particulars = string.Join(", ", v.Lines.Select(l => l.LedgerName).Where(x => x.Length > 0).Distinct().Take(4)), Detail = v.Narration, Debit = debit, Credit = credit, Amount = debit });
        }
    }

    private void LoadOutstanding(IEnumerable<LedgerTotalSnapshot> snapshots, IReadOnlyDictionary<string, GroupInfo> groups, string targetGroup, bool receivable)
    {
        foreach (var s in snapshots.Where(s => IsInGroup(s.ParentGroup, targetGroup, groups)).OrderBy(x => x.LedgerName))
        {
            var amount = receivable ? Math.Max(0m, s.ClosingBalance) : Math.Max(0m, -s.ClosingBalance);
            if (amount < 0.005m) continue;
            _rows.Add(new AccountingReportRow { Particulars = s.LedgerName, Group = s.ParentGroup, Opening = s.OpeningBalance, Debit = s.TotalDebit, Credit = s.TotalCredit, Closing = s.ClosingBalance, Amount = amount, Count = s.MovementCount });
        }
    }

    private void LoadCashFlow(IEnumerable<LedgerTotalSnapshot> snapshots, IReadOnlyDictionary<string, GroupInfo> groups, DateTime from, DateTime to)
    {
        var ledgers = snapshots.Where(s => IsInGroup(s.ParentGroup, "Cash-in-Hand", groups) || IsInGroup(s.ParentGroup, "Bank Accounts", groups)).ToArray();
        var all = ledgers.SelectMany(l => _ledgerTotals.GetMovements(_client.Id, _client.FinancialYear, l.LedgerName, from, to).Select(m => (Ledger: l.LedgerName, Movement: m))).ToArray();
        foreach (var month in all.GroupBy(x => new DateTime(x.Movement.Date.Year, x.Movement.Date.Month, 1)).OrderBy(x => x.Key))
        {
            var inflow = Round(month.Sum(x => x.Movement.Debit));
            var outflow = Round(month.Sum(x => x.Movement.Credit));
            _rows.Add(new AccountingReportRow { Date = month.Key, Particulars = month.Key.ToString("MMMM yyyy"), Debit = inflow, Credit = outflow, Amount = Round(inflow - outflow), Count = month.Count() });
        }
    }

    private void LoadFundsFlow(IEnumerable<LedgerTotalSnapshot> snapshots, IReadOnlyDictionary<string, GroupInfo> groups)
    {
        foreach (var g in snapshots.Where(HasFinancialValue).GroupBy(s => ResolveRootGroup(s.ParentGroup, groups)).OrderBy(g => g.Key))
        {
            var opening = Round(g.Sum(x => x.OpeningBalance));
            var closing = Round(g.Sum(x => x.ClosingBalance));
            _rows.Add(new AccountingReportRow { Particulars = g.Key, Opening = opening, Debit = Round(g.Sum(x => x.TotalDebit)), Credit = Round(g.Sum(x => x.TotalCredit)), Closing = closing, Amount = Round(closing - opening), Count = g.Sum(x => x.MovementCount) });
        }
    }

    private void LoadRatioAnalysis(IEnumerable<LedgerTotalSnapshot> snapshots, IReadOnlyDictionary<string, GroupInfo> groups)
    {
        var list = snapshots.ToArray();
        decimal Root(string name) => Round(list.Where(s => ResolveRootGroup(s.ParentGroup, groups).Equals(name, StringComparison.OrdinalIgnoreCase)).Sum(s => s.ClosingBalance));
        var currentAssets = Math.Max(0m, Root("Current Assets"));
        var currentLiabilities = Math.Max(0m, -Root("Current Liabilities"));
        var loans = Math.Max(0m, -Root("Loans (Liability)"));
        var capital = Math.Max(0m, -Root("Capital Account"));
        var sales = Math.Max(0m, -Root("Sales Accounts"));
        var purchases = Math.Max(0m, Root("Purchase Accounts"));
        var directExpenses = Math.Max(0m, Root("Direct Expenses")) + Math.Max(0m, Root("Direct Expense"));
        var directIncome = Math.Max(0m, -Root("Direct Incomes")) + Math.Max(0m, -Root("Direct Income"));
        var indirectExpenses = Math.Max(0m, Root("Indirect Expenses")) + Math.Max(0m, Root("Indirect Expense"));
        var indirectIncome = Math.Max(0m, -Root("Indirect Incomes")) + Math.Max(0m, -Root("Indirect Income"));
        var grossProfit = sales + directIncome - purchases - directExpenses;
        var netProfit = grossProfit + indirectIncome - indirectExpenses;
        AddRatio("Current Ratio", currentLiabilities == 0m ? 0m : currentAssets / currentLiabilities, "Current Assets / Current Liabilities");
        AddRatio("Debt / Equity", capital == 0m ? 0m : loans / capital, "Loans / Capital");
        AddRatio("Gross Profit %", sales == 0m ? 0m : grossProfit / sales * 100m, "Gross Profit / Sales × 100");
        AddRatio("Net Profit %", sales == 0m ? 0m : netProfit / sales * 100m, "Net Profit / Sales × 100");
        AddRatio("Working Capital", currentAssets - currentLiabilities, "Current Assets - Current Liabilities");
    }

    private void AddRatio(string name, decimal value, string detail) => _rows.Add(new AccountingReportRow { Particulars = name, Amount = Round(value), Detail = detail });

    private void LoadStatistics(IEnumerable<LedgerTotalSnapshot> snapshots, DateTime from, DateTime to)
    {
        var salesDocs = _sales.GetRecords(_client.Id, _client.FinancialYear).Where(r => (r.GstDate ?? r.DocumentDate) is DateTime d && d >= from && d <= to).Select(r => r.DocumentNumber).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var purchaseDocs = _purchases.GetRecords(_client.Id, _client.FinancialYear).Where(r => r.GstDate is DateTime d && d >= from && d <= to).Select(r => r.DocumentNumber).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var bank = _banking.GetTransactions(_client.Id, _client.FinancialYear, includeArchived: false).Count(r => r.TransactionDate >= from && r.TransactionDate <= to);
        var cash = _banking.GetCashEntries(_client.Id, _client.FinancialYear).Count(r => r.EntryDate >= from && r.EntryDate <= to);
        var journal = _journals.GetVouchers(_client.Id, _client.FinancialYear).Count(r => r.VoucherDate >= from && r.VoucherDate <= to);
        var stock = _stock.GetStockItems(_client.Id, _client.FinancialYear, includeArchived: false).Count;
        AddStat("Ledgers with balance/movement", snapshots.Count(HasFinancialValue));
        AddStat("Bank transactions", bank); AddStat("Cash entries", cash); AddStat("Sales documents", salesDocs); AddStat("Purchase documents", purchaseDocs); AddStat("Journal vouchers", journal); AddStat("Stock items", stock);
    }

    private void AddStat(string name, int count) => _rows.Add(new AccountingReportRow { Particulars = name, Count = count });

    private void LoadStockSummary()
    {
        foreach (var item in _stock.GetStockItems(_client.Id, _client.FinancialYear, includeArchived: false).OrderBy(x => x.StockGroup).ThenBy(x => x.Name))
            _rows.Add(new AccountingReportRow { Particulars = item.Name, Group = item.StockGroup, Detail = $"Unit: {item.BaseUnit}  HSN: {item.HsnSac}  GST: {(item.GstRate.HasValue ? item.GstRate.Value.ToString("0.##") + "%" : string.Empty)}" });
        StatusText.Text = "Stock Summary lists AAH Stock Masters. Quantitative closing stock remains authoritative from Tally unless inventory movements are fully available in AAH.";
    }

    private static AccountingReportRow MovementRow(string ledgerName, string group, LedgerMovementDetail m) => new()
    {
        Date = m.Date, VoucherType = m.VoucherType, VoucherNumber = m.VoucherNumber.Length > 0 ? m.VoucherNumber : m.ReferenceNumber,
        Particulars = ledgerName, Group = group, Detail = string.Join(" • ", new[] { m.Particulars, m.Narration }.Where(x => !string.IsNullOrWhiteSpace(x))),
        Debit = m.Debit, Credit = m.Credit, Closing = m.RunningBalance
    };

    private void SortRowsByDate()
    {
        var ordered = _rows.OrderBy(x => x.Date ?? DateTime.MinValue).ThenBy(x => x.VoucherNumber, StringComparer.OrdinalIgnoreCase).ToArray();
        _rows.Clear(); foreach (var row in ordered) _rows.Add(row);
    }

    private bool SearchFilter(object item)
    {
        if (item is not AccountingReportRow row) return false;
        var q = SearchBox?.Text?.Trim() ?? string.Empty;
        if (q.Length == 0) return true;
        return new[] { row.Particulars, row.Group, row.Detail, row.VoucherType, row.VoucherNumber, row.Date?.ToString("dd.MM.yyyy") ?? string.Empty }
            .Any(text => text.Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { _view?.Refresh(); UpdateRowCount(); }
    private void UpdateRowCount() => RowCountText.Text = $"{ReportGrid.Items.Cast<object>().Count(x => x is AccountingReportRow):N0} visible row(s)";

    private void ConfigureColumns()
    {
        var report = ReportCombo.SelectedItem?.ToString() ?? string.Empty;
        DataGridColumnFilterBehavior.SetShowTotals(ReportGrid, !report.Equals("Ratio Analysis", StringComparison.OrdinalIgnoreCase));
        var rows = _rows.ToArray();
        DateColumn.Visibility = rows.Any(r => r.Date.HasValue) ? Visibility.Visible : Visibility.Collapsed;
        VoucherTypeColumn.Visibility = rows.Any(r => r.VoucherType.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        VoucherNumberColumn.Visibility = rows.Any(r => r.VoucherNumber.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        GroupColumn.Visibility = rows.Any(r => r.Group.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        DetailColumn.Visibility = rows.Any(r => r.Detail.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        OpeningColumn.Visibility = rows.Any(r => Math.Abs(r.Opening) > 0.0000005m) ? Visibility.Visible : Visibility.Collapsed;
        DebitColumn.Visibility = rows.Any(r => Math.Abs(r.Debit) > 0.0000005m) ? Visibility.Visible : Visibility.Collapsed;
        CreditColumn.Visibility = rows.Any(r => Math.Abs(r.Credit) > 0.0000005m) ? Visibility.Visible : Visibility.Collapsed;
        AmountColumn.Visibility = rows.Any(r => Math.Abs(r.Amount) > 0.0000005m) ? Visibility.Visible : Visibility.Collapsed;
        ClosingColumn.Visibility = rows.Any(r => Math.Abs(r.Closing) > 0.0000005m) ? Visibility.Visible : Visibility.Collapsed;
        QuantityColumn.Visibility = rows.Any(r => Math.Abs(r.Quantity) > 0.0000005m) ? Visibility.Visible : Visibility.Collapsed;
        CountColumn.Visibility = rows.Any(r => r.Count != 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ExportExcel_Click(object sender, RoutedEventArgs e) => await Export("xlsx");
    private async void ExportPdf_Click(object sender, RoutedEventArgs e) => await Export("pdf");
    private async void ExportCsv_Click(object sender, RoutedEventArgs e) => await Export("csv");

    private Task Export(string extension)
    {
        var report = ReportCombo.SelectedItem?.ToString() ?? "Report";
        var dialog = new SaveFileDialog
        {
            Title = $"Export {report}",
            FileName = $"{SafeFilePart(report)}-{DateTime.Now:yyyyMMdd-HHmm}.{extension}",
            DefaultExt = extension,
            AddExtension = true,
            Filter = extension switch { "xlsx" => "Excel Workbook (*.xlsx)|*.xlsx", "pdf" => "PDF Document (*.pdf)|*.pdf", _ => "CSV File (*.csv)|*.csv" }
        };
        if (dialog.ShowDialog(this) != true) return Task.CompletedTask;
        var rows = ReportGrid.Items.Cast<object>().OfType<AccountingReportRow>().Cast<object>().ToArray();
        var columns = VisibleExportColumns();
        if (extension == "xlsx") AccountingReportExportService.ExportXlsx(dialog.FileName, ReportCaptionText.Text, rows, columns);
        else if (extension == "pdf") AccountingReportExportService.ExportPdf(dialog.FileName, ReportCaptionText.Text, rows, columns);
        else AccountingReportExportService.ExportCsv(dialog.FileName, rows, columns);
        StatusText.Text = $"Exported {rows.Length:N0} visible row(s) to {dialog.FileName}";
        return Task.CompletedTask;
    }

    private IReadOnlyList<AccountingReportExportService.Column> VisibleExportColumns()
    {
        var result = new List<AccountingReportExportService.Column>();
        foreach (var column in ReportGrid.Columns.Where(c => c.Visibility == Visibility.Visible))
        {
            if (column is not DataGridBoundColumn bound || bound.Binding is not Binding binding || binding.Path?.Path is not { Length: > 0 } path) continue;
            result.Add(new AccountingReportExportService.Column(column.Header?.ToString() ?? path, path));
        }
        return result;
    }

    private static bool IsCreditNote(string destination, string documentType) => destination.Contains("Credit", StringComparison.OrdinalIgnoreCase) || documentType.Contains("Credit", StringComparison.OrdinalIgnoreCase);
    private static bool IsDebitNote(string destination, string documentType) => destination.Contains("Debit", StringComparison.OrdinalIgnoreCase) || documentType.Contains("Debit", StringComparison.OrdinalIgnoreCase);
    private static bool HasFinancialValue(LedgerTotalSnapshot s) => s.MovementCount > 0 || Math.Abs(s.OpeningBalance) > 0.005m || Math.Abs(s.TotalDebit) > 0.005m || Math.Abs(s.TotalCredit) > 0.005m || Math.Abs(s.ClosingBalance) > 0.005m;
    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
    private static string BalanceText(decimal value) => Math.Abs(value) < 0.005m ? "0.00" : $"{Math.Abs(value):N2} {(value > 0m ? "Dr" : "Cr")}";
    private static string SafeFilePart(string text) => string.Concat(text.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    private static (DateTime From, DateTime To) FinancialYearRange(string financialYear)
    {
        var digits = new string((financialYear ?? string.Empty).Where(ch => char.IsDigit(ch) || ch == '-').ToArray());
        var parts = digits.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && int.TryParse(parts[0], out var year) && year is > 1900 and < 2200) return (new DateTime(year, 4, 1), new DateTime(year + 1, 3, 31));
        return (new DateTime(DateTime.Today.Month >= 4 ? DateTime.Today.Year : DateTime.Today.Year - 1, 4, 1), new DateTime(DateTime.Today.Month >= 4 ? DateTime.Today.Year + 1 : DateTime.Today.Year, 3, 31));
    }

    private static IReadOnlyDictionary<string, GroupInfo> BuildGroupIndex(IEnumerable<MasterRecord> masters) => masters
        .Where(m => m.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && !m.IsArchived && !string.IsNullOrWhiteSpace(m.Name))
        .GroupBy(m => Key(m.Name), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => new GroupInfo(g.First().Name.Trim(), g.First().Parent?.Trim() ?? "Primary"), StringComparer.OrdinalIgnoreCase);

    private static bool IsInGroup(string parent, string target, IReadOnlyDictionary<string, GroupInfo> groups)
    {
        var current = parent?.Trim() ?? string.Empty; var keyTarget = Key(target); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 64 && current.Length > 0; i++)
        {
            var key = Key(current); if (key == keyTarget) return true; if (!seen.Add(key) || !groups.TryGetValue(key, out var g)) return false; current = g.Parent;
        }
        return false;
    }

    private static string ResolveRootGroup(string parent, IReadOnlyDictionary<string, GroupInfo> groups)
    {
        var current = string.IsNullOrWhiteSpace(parent) ? "Primary" : parent.Trim(); var last = current; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 64; i++)
        {
            var key = Key(current); if (key.Length == 0 || !seen.Add(key)) break; if (!groups.TryGetValue(key, out var g)) return last; last = g.Name; if (string.IsNullOrWhiteSpace(g.Parent) || g.Parent.Equals("Primary", StringComparison.OrdinalIgnoreCase)) return g.Name; current = g.Parent;
        }
        return last;
    }

    private static string Key(string? value) => TallyMasterIdentity.Key(value ?? string.Empty);
    private sealed record GroupInfo(string Name, string Parent);
}
