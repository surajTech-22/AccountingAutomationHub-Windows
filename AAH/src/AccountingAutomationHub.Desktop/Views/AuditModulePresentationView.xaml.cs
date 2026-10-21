using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Desktop.Services;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AuditModulePresentationView : UserControl
{
    public AuditModulePresentationView()
    {
        InitializeComponent();
        // R14: the shared advanced filter is module-aware. Do not attach every hidden Audit grid,
        // because that leaks Sales/GST-only columns into Debtors/Creditors suggestions.
        DataContextChanged += AuditModulePresentationView_DataContextChanged;
        Loaded += (_, _) => BindAuditViewModel(DataContext as AuditWorkspaceViewModel);
    }

    private AuditWorkspaceViewModel? _boundViewModel;

    private void AuditModulePresentationView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        BindAuditViewModel(e.NewValue as AuditWorkspaceViewModel);
    }

    private void BindAuditViewModel(AuditWorkspaceViewModel? viewModel)
    {
        if (_boundViewModel is not null) _boundViewModel.PropertyChanged -= BoundViewModel_PropertyChanged;
        _boundViewModel = viewModel;
        if (_boundViewModel is not null) _boundViewModel.PropertyChanged += BoundViewModel_PropertyChanged;
        RefreshAuditSearchTargets();
        ApplyDebtorCreditorPresentationSettings();
    }

    private void BoundViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AuditWorkspaceViewModel.SelectedModule) or nameof(AuditWorkspaceViewModel.SelectedClientModule)
            or nameof(AuditWorkspaceViewModel.IsDebtorCreditorLedgerDrilldownVisible))
            RefreshAuditSearchTargets();
        if (e.PropertyName is not null && e.PropertyName.StartsWith("DebtorCreditor", StringComparison.Ordinal))
            ApplyDebtorCreditorPresentationSettings();
    }

    private void RefreshAuditSearchTargets()
    {
        var vm = _boundViewModel;
        if (vm is null) return;
        if (vm.IsDebtors || vm.IsCreditors)
        {
            AuditSearchFilterBar.Attach(vm.IsDebtorCreditorLedgerDrilldownVisible ? DebtorCreditorLedgerGrid : StandardRecordsGrid);
            return;
        }
        if (vm.IsCommerceModule)
        {
            AuditSearchFilterBar.Attach(CommerceOverviewGroupGrid, CommerceOverviewPartyGrid, CommerceGroupGrid, CommerceLedgerGrid,
                CommerceTaxAmountGrid, CommerceSupplyGrid, CommerceExtraVoucherGrid, CommerceBooksVsGstr1Grid, CommerceBooksVsTwoAGrid,
                CommerceBooksVsTwoBGrid, CommerceVoucherTypeGrid, CommerceGstRateGrid, CommerceItemGrid, CommerceHsnGrid,
                CommerceAdjustmentGrid, CommerceMonthGrid, CommerceVoucherRegisterGrid, CommerceAlertGrid, CommerceGroupPartyGrid, CommerceTransactionsGrid);
            return;
        }
        if (vm.IsBankingFinance)
        {
            AuditSearchFilterBar.Attach(PaymentGroupGrid, ReceiptGroupGrid, CashBankMovementGrid, FinanceGroupPartyGrid, RecordsGrid);
            return;
        }
        AuditSearchFilterBar.Attach(RecordsGrid);
    }

    private void ApplyDebtorCreditorPresentationSettings()
    {
        var vm = _boundViewModel;
        if (vm is null) return;
        DcOpeningColumn.Visibility = vm.DebtorCreditorShowOpeningBalance ? Visibility.Visible : Visibility.Collapsed;
        var showTransactions = vm.DebtorCreditorShowTransactions;
        DcDebitColumn.Visibility = showTransactions && !vm.DebtorCreditorNetTransactionsOnly ? Visibility.Visible : Visibility.Collapsed;
        DcCreditColumn.Visibility = showTransactions && !vm.DebtorCreditorNetTransactionsOnly ? Visibility.Visible : Visibility.Collapsed;
        DcNetColumn.Visibility = showTransactions && vm.DebtorCreditorNetTransactionsOnly ? Visibility.Visible : Visibility.Collapsed;
        DcTransactionCountColumn.Visibility = showTransactions ? Visibility.Visible : Visibility.Collapsed;
        DcClosingColumn.Visibility = vm.DebtorCreditorShowClosingBalance ? Visibility.Visible : Visibility.Collapsed;
        DcPercentageColumn.Visibility = vm.DebtorCreditorShowPercentages ? Visibility.Visible : Visibility.Collapsed;
        var detailed = vm.DebtorCreditorReportFormat.Equals("Detailed", StringComparison.OrdinalIgnoreCase);
        DcGstinColumn.Visibility = detailed ? Visibility.Visible : Visibility.Collapsed;
        DcDetailsColumn.Visibility = detailed && showTransactions ? Visibility.Visible : Visibility.Collapsed;
        DcOpeningColumn.Header = vm.DebtorCreditorShowBaseCurrency ? "Opening Balance (₹)" : "Opening Balance";
        DcDebitColumn.Header = vm.DebtorCreditorShowBaseCurrency ? "Debit (₹)" : "Debit";
        DcCreditColumn.Header = vm.DebtorCreditorShowBaseCurrency ? "Credit (₹)" : "Credit";
        DcNetColumn.Header = vm.DebtorCreditorShowBaseCurrency ? "Net Transactions (₹)" : "Net Transactions";
        DcClosingColumn.Header = vm.DebtorCreditorShowBaseCurrency ? "Closing Balance (₹)" : "Closing Balance";
        StandardRecordsGrid.AlternationCount = vm.DebtorCreditorStripeView ? 2 : 0;
        StandardRecordsGrid.AlternatingRowBackground = vm.DebtorCreditorStripeView
            ? (TryFindResource("AppBackgroundBrush") as Brush ?? Brushes.Gainsboro)
            : Brushes.Transparent;
    }

    private void DebtorCreditorInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target) return;
        TransientInfoPopupService.Show(target, "Debtors / Creditors",
            "Double-click a Ledger to open its FY voucher drill-down.\n" +
            "Filter cycles through Show All, Hide 0 Balance and Hide 0 Transactions.\n" +
            "F12 opens this module's Settings page. Ctrl+S remains the global Save Current Page shortcut.");
    }

    private void AuditModulePresentationView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Fix104: when the pointer is over the module tabs/toolbar or a dashboard DataGrid
        // whose vertical scroll is intentionally disabled, keep the selected dashboard page
        // moving. Nested controls with their own usable vertical range are left untouched by
        // the workspace-level handoff before this handler runs.
        if (e.Handled || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var target = CommerceDashboardPageScroll.IsVisible
            ? CommerceDashboardPageScroll
            : FinanceDashboardPageScroll.IsVisible
                ? FinanceDashboardPageScroll
                : BankingFinancePageScroll.IsVisible
                    ? BankingFinancePageScroll
                    : null;
        if (target is null || target.ScrollableHeight <= 0.5) return;

        var next = Math.Clamp(target.VerticalOffset - e.Delta, 0d, target.ScrollableHeight);
        if (Math.Abs(next - target.VerticalOffset) < 0.5) return;
        target.ScrollToVerticalOffset(next);
        e.Handled = true;
    }

    private void ClearSortAndFilter_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AuditWorkspaceViewModel viewModel && viewModel.ClearModuleFiltersCommand.CanExecute(null))
            viewModel.ClearModuleFiltersCommand.Execute(null);
        RecordsGrid.Items.SortDescriptions.Clear();
        foreach (var column in RecordsGrid.Columns) column.SortDirection = null;
        StandardRecordsGrid.Items.SortDescriptions.Clear();
        foreach (var column in StandardRecordsGrid.Columns) column.SortDirection = null;
    }


    private void PaymentGroupGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AuditWorkspaceViewModel viewModel || PaymentGroupGrid.SelectedItem is not AuditGroupMovementSummary summary) return;
        if (viewModel.OpenPaymentGroupCommand.CanExecute(summary)) viewModel.OpenPaymentGroupCommand.Execute(summary);
    }

    private void ReceiptGroupGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AuditWorkspaceViewModel viewModel || ReceiptGroupGrid.SelectedItem is not AuditGroupMovementSummary summary) return;
        if (viewModel.OpenReceiptGroupCommand.CanExecute(summary)) viewModel.OpenReceiptGroupCommand.Execute(summary);
    }

    private void FinanceGroupPartyGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AuditWorkspaceViewModel viewModel || FinanceGroupPartyGrid.SelectedItem is not AuditPartyMovementSummary summary) return;
        if (viewModel.OpenPartyTransactionsCommand.CanExecute(summary)) viewModel.OpenPartyTransactionsCommand.Execute(summary);
    }

    private void CashBankMovementGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AuditWorkspaceViewModel viewModel || CashBankMovementGrid.SelectedItem is not AuditBankCashMovementSummary summary) return;
        if (viewModel.OpenCashMovementCommand.CanExecute(summary)) viewModel.OpenCashMovementCommand.Execute(summary);
    }

    // Fix107: resolve the DataGridRow actually under the pointer instead of trusting a stale
    // SelectedItem. This is especially important after the first summary drill-down replaces
    // the ItemsSource and is the reason the old second double-click could appear to do nothing.
    private static bool TryGetClickedRowItem<T>(object sender, MouseButtonEventArgs e, out T? item) where T : class
    {
        item = null;
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject current) return false;
        while (current is not null && current is not DataGridRow)
            current = VisualTreeHelper.GetParent(current);
        if (current is not DataGridRow row || row.DataContext is not T typed) return false;
        grid.SelectedItem = typed;
        item = typed;
        return true;
    }

    private void CommerceGroupGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AuditWorkspaceViewModel viewModel || !TryGetClickedRowItem<AuditCommerceGroupSummary>(sender, e, out var summary) || summary is null) return;
        if (viewModel.OpenCommerceGroupCommand.CanExecute(summary)) viewModel.OpenCommerceGroupCommand.Execute(summary);
    }

    private void CommerceVoucherTypeGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AuditWorkspaceViewModel viewModel || !TryGetClickedRowItem<AuditCommerceGroupSummary>(sender, e, out var summary) || summary is null) return;
        if (viewModel.OpenCommerceVoucherTypeCommand.CanExecute(summary)) viewModel.OpenCommerceVoucherTypeCommand.Execute(summary);
    }

    private void CommercePartyGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AuditWorkspaceViewModel viewModel || !TryGetClickedRowItem<AuditCommercePartySummary>(sender, e, out var summary) || summary is null) return;
        if (viewModel.OpenCommercePartyCommand.CanExecute(summary)) viewModel.OpenCommercePartyCommand.Execute(summary);
    }

    private void CommerceGstRateGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AuditWorkspaceViewModel viewModel || !TryGetClickedRowItem<AuditCommerceGstRateSummary>(sender, e, out var summary) || summary is null) return;
        if (viewModel.OpenCommerceRateCommand.CanExecute(summary)) viewModel.OpenCommerceRateCommand.Execute(summary);
    }

    private void CommerceItemGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AuditWorkspaceViewModel viewModel || !TryGetClickedRowItem<AuditCommerceItemSummary>(sender, e, out var summary) || summary is null) return;
        if (viewModel.OpenCommerceItemCommand.CanExecute(summary)) viewModel.OpenCommerceItemCommand.Execute(summary);
    }

    private void CommerceTaxAmountGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AuditWorkspaceViewModel viewModel || !TryGetClickedRowItem<AuditCommerceTaxAmountSummary>(sender, e, out var summary) || summary?.Rate is null) return;
        var rate = new AuditCommerceGstRateSummary
        {
            Rate = summary.Rate.Value,
            RateLabel = summary.Rate.Value.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("en-IN")) + "%"
        };
        if (viewModel.OpenCommerceRateCommand.CanExecute(rate)) viewModel.OpenCommerceRateCommand.Execute(rate);
    }

    private void CommerceMonthGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AuditWorkspaceViewModel viewModel || !TryGetClickedRowItem<AuditCommerceMonthSummary>(sender, e, out var summary) || summary is null) return;
        if (viewModel.OpenCommerceMonthCommand.CanExecute(summary)) viewModel.OpenCommerceMonthCommand.Execute(summary);
    }

    private void CommerceSupplyGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AuditWorkspaceViewModel viewModel || !TryGetClickedRowItem<AuditCommerceSupplySummary>(sender, e, out var summary) || summary is null) return;
        if (viewModel.OpenCommerceSupplyCommand.CanExecute(summary)) viewModel.OpenCommerceSupplyCommand.Execute(summary);
    }

    private void RecordsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AuditWorkspaceViewModel viewModel) return;
        if ((viewModel.IsDebtors || viewModel.IsCreditors) && ReferenceEquals(sender, StandardRecordsGrid))
        {
            if (!TryGetClickedRowItem<AuditRecordRow>(sender, e, out var ledger) || ledger is null) return;
            if (viewModel.OpenDebtorCreditorLedgerCommand.CanExecute(ledger)) viewModel.OpenDebtorCreditorLedgerCommand.Execute(ledger);
            return;
        }
        if (viewModel.SelectedRecord is null) return;
        if (viewModel.OpenRecordLookupCommand.CanExecute(null)) viewModel.OpenRecordLookupCommand.Execute(null);
    }
}
