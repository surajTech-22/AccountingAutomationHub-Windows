using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccountingAutomationHub.Desktop.Controls;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class BulkLedgerMappingWindow : Window
{
    private BankingViewModel? _viewModel;
    private bool _closingFromViewModel;
    private WindowState _ownerWindowStateBeforeMapping = WindowState.Normal;

    public BulkLedgerMappingWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => CaptureOwnerWindowState();
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModelPropertyChanged;
        _viewModel = e.NewValue as BankingViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += ViewModelPropertyChanged;
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BankingViewModel.StatusText) &&
            (_viewModel?.StatusText.StartsWith("Bulk Mapping applied", StringComparison.OrdinalIgnoreCase) == true ||
             _viewModel?.StatusText.StartsWith("Removed Final Ledger", StringComparison.OrdinalIgnoreCase) == true))
        {
            Dispatcher.BeginInvoke(new Action(RestoreOwnerAndMappingWindowAfterApply));
            return;
        }

        if (e.PropertyName != nameof(BankingViewModel.IsBulkLedgerMappingOpen) ||
            _viewModel?.IsBulkLedgerMappingOpen != false || !IsVisible)
            return;

        _closingFromViewModel = true;
        Close();
    }

    private void BulkMappingHeaderAction_Click(object sender, RoutedEventArgs e)
    {
        CaptureOwnerWindowState();
    }

    private void CaptureOwnerWindowState()
    {
        if (Owner is null || Owner.WindowState == WindowState.Minimized) return;
        _ownerWindowStateBeforeMapping = Owner.WindowState;
    }

    private void RestoreOwnerAndMappingWindowAfterApply()
    {
        if (Owner is not null)
        {
            if (!Owner.IsVisible) Owner.Show();
            if (Owner.WindowState == WindowState.Minimized)
                Owner.WindowState = _ownerWindowStateBeforeMapping == WindowState.Minimized
                    ? WindowState.Normal
                    : _ownerWindowStateBeforeMapping;
        }

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        Activate();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        _viewModel?.CloseBulkSelectedMappingCommand.Execute(null);
        e.Handled = true;
    }

    private void VoucherTypeMappingSelector_SuggestionChosen(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is BulkVoucherTypeMappingRow row)
            _viewModel?.StageBulkVoucherTypeMappingRow(row);
    }



    private void VoucherTypeMappingSelector_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is SuggestTextBox editor && editor.IsSuggestionListFocus(e.NewFocus)) return;
        if (sender is FrameworkElement element && element.DataContext is BulkVoucherTypeMappingRow row)
            _viewModel?.StageBulkVoucherTypeMappingRow(row);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModelPropertyChanged;
            if (!_closingFromViewModel && _viewModel.IsBulkLedgerMappingOpen)
                _viewModel.CloseBulkSelectedMappingCommand.Execute(null);
        }
    }
}
