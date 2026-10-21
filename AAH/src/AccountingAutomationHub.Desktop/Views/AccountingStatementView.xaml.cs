using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AccountingStatementView : UserControl
{
    private AccountingStatementLine? _selectedLedger;
    private DataGrid? _dragSelectionGrid;
    private int _dragSelectionAnchor = -1;
    private Point _dragSelectionStart;
    private bool _dragSelectionActive;

    public AccountingStatementView() => InitializeComponent();

    private AccountingStatementViewModel? ViewModel => DataContext as AccountingStatementViewModel;
    private MainWindowViewModel? MainViewModel => Window.GetWindow(this)?.DataContext as MainWindowViewModel;

    private void StatementGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null) return;
        _dragSelectionGrid = grid;
        _dragSelectionAnchor = row.GetIndex();
        _dragSelectionStart = e.GetPosition(grid);
        _dragSelectionActive = false;
    }

    private void StatementGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not DataGrid grid || _dragSelectionGrid != grid || e.LeftButton != MouseButtonState.Pressed || _dragSelectionAnchor < 0) return;
        var point = e.GetPosition(grid);
        if (!_dragSelectionActive)
        {
            if (Math.Abs(point.X - _dragSelectionStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - _dragSelectionStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragSelectionActive = true;
        }

        var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null) return;
        var current = row.GetIndex();
        if (current < 0) return;
        var addToExisting = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (!addToExisting) grid.SelectedItems.Clear();
        var from = Math.Min(_dragSelectionAnchor, current);
        var to = Math.Max(_dragSelectionAnchor, current);
        for (var index = from; index <= to && index < grid.Items.Count; index++)
        {
            if (grid.Items[index] is AccountingStatementLine { RowType: "Ledger" } item && !grid.SelectedItems.Contains(item))
                grid.SelectedItems.Add(item);
        }
        e.Handled = true;
    }

    private void StatementGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragSelectionGrid = null;
        _dragSelectionAnchor = -1;
        _dragSelectionActive = false;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void StatementGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        _selectedLedger = grid.SelectedItem as AccountingStatementLine is { RowType: "Ledger" } line ? line : null;
    }

    private void StatementGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not AccountingStatementLine line) return;
        OpenStatementLine(line);
    }

    private void StatementGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not DataGrid grid || grid.SelectedItem is not AccountingStatementLine line) return;
        OpenStatementLine(line);
        e.Handled = true;
    }

    private void OpenStatementLine(AccountingStatementLine line)
    {
        if (ViewModel is null) return;
        if (line.RowType is "Root" or "Group") ViewModel.SetGroupFilter(line.Label);
        else if (line.RowType == "Ledger")
        {
            _selectedLedger = line;
            ViewModel.OpenLedger(line.Label);
        }
    }

    private void Groups_Click(object sender, RoutedEventArgs e) => ViewModel?.ShowGroups();

    private void GroupsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GroupsGrid.SelectedItem is AccountingStatementLine line) ViewModel?.SetGroupFilter(line.Label);
    }

    private void GroupsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || GroupsGrid.SelectedItem is not AccountingStatementLine line) return;
        ViewModel?.SetGroupFilter(line.Label);
        e.Handled = true;
    }

    private void DrilldownGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedLedger = DrilldownGrid.SelectedItem as AccountingStatementLine is { RowType: "Ledger" } line ? line : null;
    }

    private void DrilldownGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DrilldownGrid.SelectedItem is AccountingStatementLine line) OpenDrilldownLine(line);
    }

    private void DrilldownGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DrilldownGrid.SelectedItem is not AccountingStatementLine line) return;
        OpenDrilldownLine(line);
        e.Handled = true;
    }

    private void OpenDrilldownLine(AccountingStatementLine line)
    {
        if (ViewModel is null) return;
        if (line.RowType is "Root" or "Group") ViewModel.SetGroupFilter(line.Label);
        else if (line.RowType == "Ledger")
        {
            _selectedLedger = line;
            ViewModel.OpenLedger(line.Label);
        }
    }

    private void DeleteLedgerEntries_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var selected = LedgerTransactionsGrid.SelectedItems
            .Cast<object>()
            .OfType<AccountingAutomationHub.Domain.Accounting.LedgerMovementDetail>()
            .ToArray();
        ViewModel.DeleteSelectedLedgerTransactions(selected);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.GoBack();
        _selectedLedger = null;
    }

    private void OpenOtherStatement_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || MainViewModel is null) return;
        MainViewModel.OpenOppositeAccountingStatement(ViewModel.IsBalanceSheet);
    }

    private void Reports_Click(object sender, RoutedEventArgs e) => MainViewModel?.OpenAccountingReports();

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu();
        foreach (var format in new[] { "Excel", "PDF", "CSV" })
        {
            var item = new MenuItem { Header = format };
            item.Click += (_, _) => ExportStatement(format);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void ExportStatement(string format)
    {
        if (ViewModel is null) return;
        var extension = format switch { "Excel" => "xlsx", "PDF" => "pdf", _ => "csv" };
        var dialog = new SaveFileDialog
        {
            Title = $"Export {ViewModel.StatementName}",
            FileName = $"{ViewModel.StatementName.Replace("&", "and").Replace(" ", "-")}-{DateTime.Now:yyyyMMdd-HHmm}.{extension}",
            DefaultExt = extension,
            AddExtension = true,
            Filter = extension switch { "xlsx" => "Excel Workbook (*.xlsx)|*.xlsx", "pdf" => "PDF Document (*.pdf)|*.pdf", _ => "CSV File (*.csv)|*.csv" }
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var rows = new List<AccountingReportRow>();
        if (ViewModel.IsLedgerScreen)
        {
            foreach (var movement in LedgerTransactionsGrid.Items.Cast<object>().OfType<AccountingAutomationHub.Domain.Accounting.LedgerMovementDetail>())
            {
                rows.Add(new AccountingReportRow
                {
                    Group = "Ledger",
                    Particulars = $"{movement.Date:dd.MM.yyyy} | {movement.VoucherType} | {movement.VoucherOrReference} | {movement.Particulars} | {movement.Narration}",
                    Debit = movement.Debit,
                    Credit = movement.Credit,
                    Closing = movement.RunningBalance,
                    Count = 1
                });
            }
        }
        else if (ViewModel.IsGroupDetailScreen)
        {
            foreach (var line in DrilldownGrid.Items.Cast<object>().OfType<AccountingStatementLine>()) rows.Add(MapLine(line, ViewModel.SelectedGroupFilter));
        }
        else if (ViewModel.IsGroupsScreen)
        {
            foreach (var line in GroupsGrid.Items.Cast<object>().OfType<AccountingStatementLine>()) rows.Add(MapLine(line, "Groups"));
        }
        else if (ViewModel.IsScheduleIIIView)
        {
            foreach (var line in ScheduleStatementGrid.Items.Cast<object>().OfType<AccountingStatementLine>()) rows.Add(MapLine(line, string.Empty));
        }
        else
        {
            foreach (var line in LeftStatementGrid.Items.Cast<object>().OfType<AccountingStatementLine>()) rows.Add(MapLine(line, ViewModel.LeftTitle));
            foreach (var line in RightStatementGrid.Items.Cast<object>().OfType<AccountingStatementLine>()) rows.Add(MapLine(line, ViewModel.RightTitle));
        }
        var columns = new[]
        {
            new AccountingReportExportService.Column("Side", nameof(AccountingReportRow.Group)),
            new AccountingReportExportService.Column("Particulars", nameof(AccountingReportRow.Particulars)),
            new AccountingReportExportService.Column("Opening", nameof(AccountingReportRow.Opening)),
            new AccountingReportExportService.Column("Debit", nameof(AccountingReportRow.Debit)),
            new AccountingReportExportService.Column("Credit", nameof(AccountingReportRow.Credit)),
            new AccountingReportExportService.Column("Closing", nameof(AccountingReportRow.Closing)),
            new AccountingReportExportService.Column("Entries", nameof(AccountingReportRow.Count))
        };
        var title = $"{ViewModel.StatementName} - {ViewModel.CompanyCaption} - {ViewModel.PeriodCaption} - {ViewModel.NavigationCaption}";
        var data = rows.Cast<object>().ToArray();
        if (extension == "xlsx") AccountingReportExportService.ExportXlsx(dialog.FileName, title, data, columns);
        else if (extension == "pdf") AccountingReportExportService.ExportPdf(dialog.FileName, title, data, columns);
        else AccountingReportExportService.ExportCsv(dialog.FileName, data, columns);
    }

    private static AccountingReportRow MapLine(AccountingStatementLine line, string side) => new()
    {
        Group = side,
        Particulars = new string(' ', Math.Max(0, line.IndentLevel) * 2) + line.Label,
        Opening = line.OpeningBalance,
        Debit = line.TotalDebit,
        Credit = line.TotalCredit,
        Closing = line.ClosingBalance,
        Count = line.MovementCount
    };

    private void ClosingEntry_Click(object sender, RoutedEventArgs e)
    {
        if (MainViewModel is null) return;

        // Fix27: the Balance Sheet selection is the Closure scope. An already-open
        // Ledger supplies exactly one Ledger. On statement/group screens, Ctrl/Shift/
        // range selection can supply many Ledgers. Advanced Closure Entry must show
        // only that scoped set; with no selection it may show the full Balance Sheet.
        if (ViewModel?.IsLedgerScreen == true && !string.IsNullOrWhiteSpace(ViewModel.SelectedLedgerName))
        {
            MainViewModel.OpenAdvancedBalanceSheetClosingEntry(new[] { ViewModel.SelectedLedgerName.Trim() });
            return;
        }

        var selected = CollectSelectedLedgerNames();
        if (selected.Count == 0 && _selectedLedger is not null && !string.IsNullOrWhiteSpace(_selectedLedger.Label))
            selected.Add(_selectedLedger.Label.Trim());
        MainViewModel.OpenAdvancedBalanceSheetClosingEntry(selected.Count == 0 ? null : selected);
    }

    private List<string> CollectSelectedLedgerNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grid in new[] { LeftStatementGrid, RightStatementGrid, ScheduleStatementGrid, DrilldownGrid })
        {
            if (grid is null) continue;
            foreach (var item in grid.SelectedItems.Cast<object>().OfType<AccountingStatementLine>())
            {
                if (!string.Equals(item.RowType, "Ledger", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(item.Label)) continue;
                names.Add(item.Label.Trim());
            }
        }
        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
