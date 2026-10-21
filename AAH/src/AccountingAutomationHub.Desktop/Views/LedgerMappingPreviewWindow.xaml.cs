using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccountingAutomationHub.Desktop.Behaviors;
using AccountingAutomationHub.Desktop.Controls;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class LedgerMappingPreviewWindow : Window
{
    private BankingViewModel? _viewModel;
    private bool _closingFromViewModel;

    public LedgerMappingPreviewWindow()
    {
        InitializeComponent();
        AddHandler(SuggestTextBox.EscapePressedEvent, new RoutedEventHandler(PreviewEditor_EscapePressed), true);
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void PreviewEditor_EscapePressed(object sender, RoutedEventArgs e)
    {
        var editor = e.OriginalSource as SuggestTextBox ?? e.Source as SuggestTextBox;
        if (editor is not null)
        {
            ExitPreviewEditor(editor);
            e.Handled = true;
            return;
        }
        RequestClose();
        e.Handled = true;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModelPropertyChanged;
        _viewModel = e.NewValue as BankingViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += ViewModelPropertyChanged;
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BankingViewModel.IsLedgerMappingDetailOpen) ||
            _viewModel?.IsLedgerMappingDetailOpen != false ||
            !IsVisible)
        {
            return;
        }

        _closingFromViewModel = true;
        Close();
    }

    private void SuggestedMatch_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        _viewModel?.UseSuggestedLedger(_viewModel.SelectedLedgerMappingRow);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        var editor = Keyboard.FocusedElement as SuggestTextBox;
        if (editor is null && Keyboard.FocusedElement is DependencyObject focused)
            editor = FindVisualParent<SuggestTextBox>(focused);
        if (editor is not null)
        {
            ExitPreviewEditor(editor);
            e.Handled = true;
            return;
        }

        RequestClose();
        e.Handled = true;
    }

    private void ExitPreviewEditor(SuggestTextBox editor)
    {
        editor.CloseSuggestions();
        editor.DeactivateEditor();
        Keyboard.ClearFocus();
        Focus();
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void MappingSelector_SuggestionChosen(object sender, RoutedEventArgs e)
    {
        _viewModel?.StageLedgerMappingRow(_viewModel.SelectedLedgerMappingRow);
    }

    private void MappingSelector_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is SuggestTextBox editor && editor.IsSuggestionListFocus(e.NewFocus)) return;
        _viewModel?.StageLedgerMappingRow(_viewModel.SelectedLedgerMappingRow);
    }

    private void LedgerMappingDetailSelection_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.RefreshLedgerMappingDetailSelectionState();
    }


    private void ClearDetailFilter_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ClearLedgerMappingDetailFilter();
    }

    private void ClearDetailSortFilter_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ClearLedgerMappingDetailFilter();
        DataGridColumnFilterBehavior.ClearAllSortAndFilters(LedgerMappingDetailGrid);
    }

    private void ClearVisibleSelection_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ClearLedgerMappingDetailSelection(visibleOnly: true);
    }

    private void ClearAllSelection_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ClearLedgerMappingDetailSelection(visibleOnly: false);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void RequestClose()
    {
        if (_viewModel?.IsLedgerMappingDetailOpen == true)
        {
            // The view-model notification closes this window synchronously.
            _viewModel.CloseLedgerMappingPreview();
            return;
        }

        if (IsVisible) Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModelPropertyChanged;
            if (!_closingFromViewModel) _viewModel.CloseLedgerMappingPreview();
        }
    }
}
