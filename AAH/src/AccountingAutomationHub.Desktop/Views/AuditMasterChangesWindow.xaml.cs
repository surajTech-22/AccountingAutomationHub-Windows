using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.Models;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AuditMasterChangesWindow : Window
{
    private readonly AuditAdvancedVoucherWorkbenchModel _model;
    private ICollectionView? _view;
    private readonly DispatcherTimer _ledgerPreviewCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool _ledgerPreviewPinned;
    private AuditMasterUsageRow? _ledgerPreviewRow;
    private FrameworkElement? _ledgerPreviewTrigger;

    public AuditMasterChangesWindow(AuditAdvancedVoucherWorkbenchModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        Loaded += (_, _) => RefreshIndex();
        Deactivated += (_, _) => HideLedgerVoucherPreview();
        _ledgerPreviewCloseTimer.Tick += (_, _) =>
        {
            _ledgerPreviewCloseTimer.Stop();
            if (!_ledgerPreviewPinned && !LedgerVoucherPreviewLayer.IsMouseOver && !(_ledgerPreviewTrigger?.IsMouseOver ?? false))
                HideLedgerVoucherPreview();
        };
    }

    private static bool IsLedgerUsage(AuditMasterUsageRow row) =>
        row.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)
        || row.Type.Equals("Party", StringComparison.OrdinalIgnoreCase)
        || row.Type.Equals("GST Ledger", StringComparison.OrdinalIgnoreCase);

    private void RefreshIndex()
    {
        _model.RebuildMasterIndex();
        _view = CollectionViewSource.GetDefaultView(_model.MasterUsages);
        _view.Filter = FilterUsage;
        UsageGrid.ItemsSource = _view;
        _view.Refresh();
        UpdatePreview();
    }

    private bool FilterUsage(object item)
    {
        if (item is not AuditMasterUsageRow row || !IsLedgerUsage(row)) return false;
        var search = (SearchBox.Text ?? string.Empty).Trim();
        if (search.Length == 0) return true;
        return row.ExistingMaster.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.Group.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.VoucherNumbers.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void FilterChanged(object sender, EventArgs e) => _view?.Refresh();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshIndex();

    private void UpdatePreview()
    {
        var ledgerCount = _model.MasterUsages.Count(IsLedgerUsage);
        PreviewText.Text = $"Selected vouchers: {_model.SelectedCount:N0} | Ledger Masters: {ledgerCount:N0} | Group is context only; rename directly on the required Ledger row.";
    }

    private void RenameRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not AuditMasterUsageRow row) return;
        var replacement = (row.ReplacementMaster ?? string.Empty).Trim();
        if (replacement.Length == 0)
        {
            MessageBox.Show(this, "Select or enter the replacement Ledger on this row.", "Ledger Rename", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (AuditAdvancedVoucherWorkbenchModel.MasterEquals(row.ExistingMaster, replacement))
        {
            MessageBox.Show(this, "The replacement is the same Ledger.", "Ledger Rename", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var affected = _model.ReplaceMaster(row.Type, row.ExistingMaster, replacement, _model.CurrentVoucher, row.Group);
        if (affected <= 0)
        {
            MessageBox.Show(this, "No exact matching Ledger usage remains in the active rename scope.", "Ledger Rename", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshIndex();
            return;
        }

        RefreshIndex();
        MessageBox.Show(this, $"Renamed {affected:N0} exact matching Ledger usage(s). Unselected vouchers and non-matching Ledgers were not changed.", "Ledger Rename", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void PreviewVouchers_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not AuditMasterUsageRow row) return;
        _ledgerPreviewCloseTimer.Stop();
        if (LedgerVoucherPreviewLayer.Visibility == Visibility.Visible && _ledgerPreviewPinned && ReferenceEquals(_ledgerPreviewRow, row))
        {
            HideLedgerVoucherPreview();
            return;
        }

        _ledgerPreviewPinned = true;
        ShowLedgerVoucherPreview(element, row);
    }

    private void PreviewVouchers_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not AuditMasterUsageRow row) return;
        _ledgerPreviewCloseTimer.Stop();
        if (_ledgerPreviewPinned) return;
        ShowLedgerVoucherPreview(element, row);
    }

    private void PreviewVouchers_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_ledgerPreviewPinned) _ledgerPreviewCloseTimer.Start();
    }

    private void ShowLedgerVoucherPreview(FrameworkElement element, AuditMasterUsageRow row)
    {
        var vouchers = _model.GetMatchingMasterVouchers(row.Type, row.ExistingMaster);
        _ledgerPreviewRow = row;
        _ledgerPreviewTrigger = element;
        LedgerVoucherPreviewItems.ItemsSource = vouchers;
        LedgerVoucherPreviewTitle.Text = $"{row.ExistingMaster} · {vouchers.Count:N0} voucher(s)";
        LedgerVoucherPreviewLayer.Visibility = Visibility.Visible;
    }

    private void LedgerVoucherPreviewHost_MouseEnter(object sender, MouseEventArgs e) => _ledgerPreviewCloseTimer.Stop();

    private void LedgerVoucherPreviewHost_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_ledgerPreviewPinned) _ledgerPreviewCloseTimer.Start();
    }

    private void HideLedgerVoucherPreview()
    {
        _ledgerPreviewCloseTimer.Stop();
        _ledgerPreviewPinned = false;
        _ledgerPreviewRow = null;
        _ledgerPreviewTrigger = null;
        LedgerVoucherPreviewLayer.Visibility = Visibility.Collapsed;
    }

    private void OpenVoucherFromPreview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string sessionId && _model.SelectVoucherBySessionId(sessionId))
        {
            HideLedgerVoucherPreview();
            Close();
        }
    }

    private void CloseVoucherPreview_Click(object sender, RoutedEventArgs e) => HideLedgerVoucherPreview();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
