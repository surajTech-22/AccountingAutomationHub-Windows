using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using AccountingAutomationHub.Desktop.Controls;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Desktop.Behaviors;
using AccountingAutomationHub.Domain.Banking;

namespace AccountingAutomationHub.Desktop.Views;

public partial class BankingView : UserControl
{
    private bool _rowSavePending;
    private BankingViewModel? _observedViewModel;
    private LedgerMappingPreviewWindow? _ledgerMappingPreviewWindow;
    private BulkLedgerMappingWindow? _bulkLedgerMappingWindow;
    private EmptyLedgerExportWindow? _emptyLedgerExportWindow;
    private LedgerMappingWorkbenchRow? _ledgerMappingEditRow;
    private string _ledgerMappingEditOriginalFinalLedger = string.Empty;
    private string _ledgerMappingEditOriginalLedgerGroup = string.Empty;
    private string _ledgerMappingEditOriginalStatus = string.Empty;
    private bool _ledgerMappingEscapeInProgress;
    private LedgerMappingWorkbenchRow? _ledgerMappingLastStagedRow;
    private string _ledgerMappingLastStagedFinalLedger = string.Empty;
    private string _ledgerMappingLastStagedGroup = string.Empty;

    public BankingView()
    {
        InitializeComponent();
        AddHandler(SuggestTextBox.EscapePressedEvent, new RoutedEventHandler(SuggestTextBox_EscapePressed), true);
        AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(BankingView_PreviewKeyDown), true);
        Unloaded += BankingView_Unloaded;
        DataContextChanged += BankingView_DataContextChanged;
    }

    private void BankingView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ViewModel?.SelectedBankingTabIndex != 4) return;

        SuggestTextBox? editor = null;
        if (Keyboard.FocusedElement is DependencyObject focused)
            editor = focused as SuggestTextBox ?? FindVisualParent<SuggestTextBox>(focused);

        if (editor is null && _ledgerMappingEditRow is null) return;
        ExitLedgerMappingEditor(editor);
        e.Handled = true;
    }

    private void SuggestTextBox_EscapePressed(object sender, RoutedEventArgs e)
    {
        var editor = e.OriginalSource as SuggestTextBox ?? e.Source as SuggestTextBox;
        ExitLedgerMappingEditor(editor);
        e.Handled = true;
    }

    private void LedgerMappingEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ExitLedgerMappingEditor(sender as SuggestTextBox);
        e.Handled = true;
    }

    private void ExitLedgerMappingEditor(SuggestTextBox? editor)
    {
        // Escape is an EDIT cancel only. It must not jump the current row/cell back
        // to Count/Select or collapse the user's workbench selection. Preserve the
        // exact current cell, restore the pre-edit value, close suggestions and
        // return keyboard navigation to the same cell.
        if (_ledgerMappingEscapeInProgress) return;
        _ledgerMappingEscapeInProgress = true;
        try
        {
            var row = _ledgerMappingEditRow ?? LedgerMappingGrid.CurrentItem as LedgerMappingWorkbenchRow;
            var currentColumn = LedgerMappingGrid.CurrentColumn;

            editor?.CloseSuggestions();
            if (_ledgerMappingEditRow is not null)
            {
                _ledgerMappingEditRow.FinalLedger = _ledgerMappingEditOriginalFinalLedger;
                _ledgerMappingEditRow.LedgerGroup = _ledgerMappingEditOriginalLedgerGroup;
                _ledgerMappingEditRow.Status = _ledgerMappingEditOriginalStatus;
                editor?.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            }

            ViewModel?.EndLedgerMappingEditor(row);
            ClearLedgerMappingEditSnapshot();
            editor?.DeactivateEditor();
            Keyboard.ClearFocus();

            if (row is not null && currentColumn is not null)
            {
                LedgerMappingGrid.ScrollIntoView(row, currentColumn);
                LedgerMappingGrid.CurrentCell = new DataGridCellInfo(row, currentColumn);
            }
            LedgerMappingGrid.Focus();
            Keyboard.Focus(LedgerMappingGrid);

            // Let WPF finish routing Escape before reaffirming the same cell. This
            // prevents popup focus from reactivating the editor without changing
            // the user's row/cell selection.
            LedgerMappingGrid.Dispatcher.BeginInvoke(() =>
            {
                if (row is not null && currentColumn is not null)
                {
                    LedgerMappingGrid.ScrollIntoView(row, currentColumn);
                    LedgerMappingGrid.CurrentCell = new DataGridCellInfo(row, currentColumn);
                }
                LedgerMappingGrid.Focus();
            }, DispatcherPriority.Input);
        }
        finally
        {
            _ledgerMappingEscapeInProgress = false;
        }
    }

    private void LedgerMappingSelector_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is BankingViewModel permissionVm && !permissionVm.CanEditLedgerMapping) { e.Handled = true; return; }
        if (sender is not SuggestTextBox { DataContext: LedgerMappingWorkbenchRow row } editor) return;
        if (ViewModel is not null)
        {
            ViewModel.SelectedLedgerMappingRow = row;
            ViewModel.BeginLedgerMappingEditor(row);
        }
        var columnIndex = GetLedgerMappingColumnIndex(string.Equals(editor.Tag?.ToString(), "LedgerGroup", StringComparison.Ordinal) ? "Ledger Group" : "Final Ledger");
        ActivateLedgerMappingCell(row, columnIndex);
        if (editor.IsExplicitEditorActive)
        {
            // A second click on an already-active cell must reopen the popup.
            // Previously this returned immediately, leaving Ledger/Group dropdowns
            // looking disabled after the popup had closed once.
            editor.ShowAllSuggestions();
            var activeIndex = editor.GetCharacterIndexFromPoint(e.GetPosition(editor), true);
            var textLength = editor.Text?.Length ?? 0;
            editor.CaretIndex = activeIndex >= Math.Max(0, textLength - 1) ? textLength : Math.Max(0, activeIndex);
            editor.SelectionLength = 0;
            e.Handled = true;
            return;
        }

        editor.ActivateEditor(showAllSuggestions: true);
        // First activation is Excel-style append/edit-at-end. GetCharacterIndexFromPoint
        // returns the last character index at the right edge, which placed the caret before
        // the final letter. Put the insertion point after the complete existing value.
        editor.CaretIndex = editor.Text?.Length ?? 0;
        editor.SelectionLength = 0;
        e.Handled = true;
    }

    private void LedgerMappingSelector_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_ledgerMappingEscapeInProgress ||
            sender is not SuggestTextBox { DataContext: LedgerMappingWorkbenchRow row } editor)
            return;

        if (!ReferenceEquals(_ledgerMappingEditRow, row))
        {
            _ledgerMappingEditRow = row;
            _ledgerMappingEditOriginalFinalLedger = row.FinalLedger;
            _ledgerMappingEditOriginalLedgerGroup = row.LedgerGroup;
            _ledgerMappingEditOriginalStatus = row.Status;
        }

        if (ViewModel is not null)
        {
            ViewModel.SelectedLedgerMappingRow = row;
            ViewModel.BeginLedgerMappingEditor(row);
        }
        var columnIndex = GetLedgerMappingColumnIndex(string.Equals(editor.Tag?.ToString(), "LedgerGroup", StringComparison.Ordinal) ? "Ledger Group" : "Final Ledger");
        ActivateLedgerMappingCell(row, columnIndex);
    }

    private void ClearLedgerMappingEditSnapshot()
    {
        _ledgerMappingEditRow = null;
        _ledgerMappingEditOriginalFinalLedger = string.Empty;
        _ledgerMappingEditOriginalLedgerGroup = string.Empty;
        _ledgerMappingEditOriginalStatus = string.Empty;
    }

    private void BankStatementGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        // Bank Statement is evidence-first: only Ledger is user-editable. Keep this
        // routed guard in addition to per-column IsReadOnly so template/theme changes
        // can never accidentally open Date/Narration/Amount/Mode/Bank editors.
        var header = e.Column?.Header?.ToString()?.Trim() ?? string.Empty;
        if (!header.Equals("Ledger", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
    }

    private void BankStatementGrid_RowEditEnding(
        object sender,
        DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit ||
            _rowSavePending ||
            e.Row.Item is not BankTransactionRecord transaction)
        {
            return;
        }

        QueueAfterCommit(() =>
        {
            ViewModel?.SaveTransactionRecord(transaction);
        });
    }

    private void ObVerify_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ObVerificationRow row })
        {
            ViewModel?.VerifyOpeningBalanceRow(row);
        }
    }

    private void ObSkip_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ObVerificationRow row })
        {
            ViewModel?.SkipOpeningBalanceRow(row);
        }
    }

    private void BankBalancePrevious_Click(object sender, RoutedEventArgs e)
    {
        MoveBankBalanceAccount(-1);
    }

    private void BankBalanceNext_Click(object sender, RoutedEventArgs e)
    {
        MoveBankBalanceAccount(1);
    }

    private void BankBalanceSnapshot_Loaded(object sender, RoutedEventArgs e)
    {
        if (_observedViewModel is null && DataContext is BankingViewModel viewModel)
        {
            _observedViewModel = viewModel;
            _observedViewModel.PropertyChanged += BankingViewModel_PropertyChanged;
        }
        UpdateBankBalanceNavigator();
    }

    private void BankBalanceSnapshot_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                MoveBankBalanceAccount(-1);
                break;
            case Key.Right:
                MoveBankBalanceAccount(1);
                break;
            case Key.Home:
                SelectBankBalanceAccountAt(0);
                break;
            case Key.End:
                SelectBankBalanceAccountAt((ViewModel?.Accounts.Count ?? 0) - 1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void BankBalanceSnapshot_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((ViewModel?.Accounts.Count ?? 0) <= 1) return;
        MoveBankBalanceAccount(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private void MoveBankBalanceAccount(int offset)
    {
        var viewModel = ViewModel;
        if (viewModel is null || viewModel.Accounts.Count == 0)
        {
            UpdateBankBalanceNavigator();
            return;
        }

        var currentIndex = viewModel.SelectedAccount is null
            ? (offset >= 0 ? 0 : viewModel.Accounts.Count - 1)
            : viewModel.Accounts.IndexOf(viewModel.SelectedAccount);
        if (currentIndex < 0) currentIndex = 0;
        SelectBankBalanceAccountAt(Math.Clamp(currentIndex + offset, 0, viewModel.Accounts.Count - 1));
    }

    private void SelectBankBalanceAccountAt(int index)
    {
        var viewModel = ViewModel;
        if (viewModel is null || viewModel.Accounts.Count == 0)
        {
            UpdateBankBalanceNavigator();
            return;
        }

        var targetIndex = Math.Clamp(index, 0, viewModel.Accounts.Count - 1);
        viewModel.SelectedAccount = viewModel.Accounts[targetIndex];
        BankBalanceSnapshotBorder.Focus();
        UpdateBankBalanceNavigator();
    }

    private void UpdateBankBalanceNavigator()
    {
        var viewModel = ViewModel;
        var count = viewModel?.Accounts.Count ?? 0;
        if (count == 0)
        {
            BankBalancePreviousButton.IsEnabled = false;
            BankBalanceNextButton.IsEnabled = false;
            BankBalanceAccountPositionText.Text = "0/0";
            return;
        }

        var index = viewModel!.SelectedAccount is null
            ? 0
            : viewModel.Accounts.IndexOf(viewModel.SelectedAccount);
        if (index < 0) index = 0;
        BankBalancePreviousButton.IsEnabled = index > 0;
        BankBalanceNextButton.IsEnabled = index < count - 1;
        BankBalanceAccountPositionText.Text = $"{index + 1:N0}/{count:N0}";
    }

    private void FilterComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.IsDropDownOpen) return;

        // Closed Banking filter dropdowns must never change selection merely
        // because the user scrolls the workbench. Open dropdowns retain their
        // normal list scrolling behaviour.
        e.Handled = true;
    }

    private void FilterComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.Items.Count == 0) return;

        // When the popup is open, let the normal ComboBox list process keys.
        if (comboBox.IsDropDownOpen) return;

        switch (e.Key)
        {
            case Key.Down:
                MoveFilterSelection(comboBox, 1);
                break;
            case Key.Up:
                MoveFilterSelection(comboBox, -1);
                break;
            case Key.PageDown:
                MoveFilterSelection(comboBox, 8);
                break;
            case Key.PageUp:
                MoveFilterSelection(comboBox, -8);
                break;
            case Key.Home:
                comboBox.SelectedIndex = 0;
                break;
            case Key.End:
                comboBox.SelectedIndex = comboBox.Items.Count - 1;
                break;
            case Key.Enter:
            case Key.Escape:
                comboBox.IsDropDownOpen = false;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static void MoveFilterSelection(ComboBox comboBox, int delta)
    {
        var start = comboBox.SelectedIndex < 0 ? (delta > 0 ? -1 : comboBox.Items.Count) : comboBox.SelectedIndex;
        comboBox.SelectedIndex = Math.Clamp(start + delta, 0, comboBox.Items.Count - 1);
        (comboBox.ItemContainerGenerator.ContainerFromIndex(comboBox.SelectedIndex) as FrameworkElement)?.BringIntoView();
    }

    private void BankStatementGrid_Loaded(object sender, RoutedEventArgs e) => ApplyBankStatementColumnLayout();

    private void ApplyBankStatementColumnLayout()
    {
        if (BankStatementGrid.Columns.Count != 7) return;

        // Keep a stable, virtualization-friendly Excel-style layout. Measuring
        // every transaction on every resize/page switch was a major navigation
        // cost. Narration remains flexible and absorbs available screen width.
        BankStatementGrid.Columns[0].Width = new DataGridLength(110);
        BankStatementGrid.Columns[1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        BankStatementGrid.Columns[2].Width = new DataGridLength(125);
        BankStatementGrid.Columns[3].Width = new DataGridLength(125);
        BankStatementGrid.Columns[4].Width = new DataGridLength(125);
        BankStatementGrid.Columns[5].Width = new DataGridLength(240);
        BankStatementGrid.Columns[6].Width = new DataGridLength(300);
    }

    private int GetLedgerMappingColumnIndex(string header)
    {
        for (var index = 0; index < LedgerMappingGrid.Columns.Count; index++)
        {
            if (string.Equals(LedgerMappingGrid.Columns[index].Header?.ToString(), header, StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return 0;
    }

    private void LedgerMappingRowSelection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BankingViewModel permissionVm && !permissionVm.CanEditLedgerMapping) { e.Handled = true; return; }
        // Row action checkboxes are independent of DataGrid cell selection. Refresh
        // the header checkbox state after the TwoWay IsSelected binding updates.
        Dispatcher.BeginInvoke(() => ViewModel?.RefreshLedgerMappingActionSelectionState(), DispatcherPriority.DataBind);
    }

    private void ActivateLedgerMappingCell(LedgerMappingWorkbenchRow row, int columnIndex)
    {
        if (ViewModel is not null) ViewModel.SelectedLedgerMappingRow = row;
        if (LedgerMappingGrid.Columns.Count == 0) return;

        var safeColumnIndex = Math.Clamp(columnIndex, 0, LedgerMappingGrid.Columns.Count - 1);
        var column = LedgerMappingGrid.Columns[safeColumnIndex];
        LedgerMappingGrid.ScrollIntoView(row, column);
        LedgerMappingGrid.CurrentCell = new DataGridCellInfo(row, column);
    }

    private void FocusLedgerMappingEditor(LedgerMappingWorkbenchRow row, int columnIndex)
    {
        ActivateLedgerMappingCell(row, columnIndex);
        LedgerMappingGrid.UpdateLayout();
        var column = LedgerMappingGrid.Columns[Math.Clamp(columnIndex, 0, LedgerMappingGrid.Columns.Count - 1)];
        var content = column.GetCellContent(row);
        var editor = content as SuggestTextBox ?? FindVisualChild<SuggestTextBox>(content);
        if (editor is not null)
        {
            editor.ActivateEditor(showAllSuggestions: true);
            return;
        }

        LedgerMappingGrid.Dispatcher.BeginInvoke(() =>
        {
            LedgerMappingGrid.ScrollIntoView(row, column);
            LedgerMappingGrid.UpdateLayout();
            var deferredContent = column.GetCellContent(row);
            var deferredEditor = deferredContent as SuggestTextBox ?? FindVisualChild<SuggestTextBox>(deferredContent);
            if (deferredEditor is not null)
            {
                deferredEditor.ActivateEditor(showAllSuggestions: true);
            }
        }, DispatcherPriority.Input);
    }

    private void SuggestedMatch_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is BankingViewModel permissionVm && !permissionVm.CanEditLedgerMapping) { e.Handled = true; return; }
        if (e.ClickCount != 2 ||
            sender is not FrameworkElement { DataContext: LedgerMappingWorkbenchRow row } ||
            string.IsNullOrWhiteSpace(row.SuggestedMatch))
        {
            return;
        }

        ViewModel?.UseSuggestedLedger(row);
        if (string.IsNullOrWhiteSpace(row.LedgerGroup))
            FocusLedgerMappingEditor(row, GetLedgerMappingColumnIndex("Ledger Group"));
        else
            ActivateLedgerMappingCell(row, GetLedgerMappingColumnIndex("Final Ledger"));
        e.Handled = true;
    }

    private void LedgerMappingSelector_SuggestionChosen(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LedgerMappingWorkbenchRow row }) return;

        // A mouse selection from SuggestTextBox already updated its TwoWay Text binding.
        // Stage immediately so an existing Master fills Ledger Group/status at once
        // instead of waiting for the cell to lose focus.
        ViewModel?.StageLedgerMappingRow(row);
        _ledgerMappingLastStagedRow = row;
        _ledgerMappingLastStagedFinalLedger = row.FinalLedger;
        _ledgerMappingLastStagedGroup = row.LedgerGroup;
    }

    private void LedgerMappingSelector_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_ledgerMappingEscapeInProgress) return;
        if (sender is not SuggestTextBox editor || editor.DataContext is not LedgerMappingWorkbenchRow row) return;

        // Clicking the suggestion popup briefly crosses a separate WPF visual tree.
        // Do not stage/commit the old cell text while the user is choosing an item.
        if (editor.IsSuggestionListFocus(e.NewFocus)) return;

        // A suggestion click already staged this exact Final Ledger/Group pair. Do not
        // stage it again on the focus transition generated by the popup selection. Typed
        // edits still stage once here when their value differs from the last chosen item.
        var alreadyStaged = ReferenceEquals(_ledgerMappingLastStagedRow, row) &&
                            string.Equals(_ledgerMappingLastStagedFinalLedger, row.FinalLedger, StringComparison.Ordinal) &&
                            string.Equals(_ledgerMappingLastStagedGroup, row.LedgerGroup, StringComparison.Ordinal);
        if (!alreadyStaged) ViewModel?.StageLedgerMappingRow(row);
        _ledgerMappingLastStagedRow = null;
        _ledgerMappingLastStagedFinalLedger = string.Empty;
        _ledgerMappingLastStagedGroup = string.Empty;
        ViewModel?.EndLedgerMappingEditor(row);
        ClearLedgerMappingEditSnapshot();
        editor.DeactivateEditor();
    }

    // Explicit duplicate-Master recovery. The Bank statement currently stores its
    // counterpart as a ledger display name; selecting one of two live MASTERIDs with
    // the same display name would not survive persistence/export. Do NOT offer a
    // fictitious name-only selection. Show identities and allow a unique alternative.
    private void LedgerMappingResolveMasterMatches_Click(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || !vm.CanEditLedgerMapping) return;
        var row = LedgerMappingGrid.CurrentItem as LedgerMappingWorkbenchRow ?? vm.SelectedLedgerMappingRow;
        if (row is null)
        {
            System.Windows.MessageBox.Show("Select a Ledger Mapping row first.", "Resolve Master Matches");
            return;
        }
        vm.SelectedLedgerMappingRow = row;
        var masters = vm.GetLedgerMappingActiveMasters();
        var requested = string.IsNullOrWhiteSpace(row.FinalLedger) ? row.SummaryName : row.FinalLedger;
        var key = AccountingAutomationHub.Infrastructure.Banking.BankNarrationParser.NormalizeLedgerExactMatchKey(requested);
        var ambiguous = masters.Where(master => AccountingAutomationHub.Infrastructure.Banking.BankNarrationParser.NormalizeLedgerExactMatchKey(master.Name).Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
        var counts = masters.GroupBy(master => AccountingAutomationHub.Infrastructure.Banking.BankNarrationParser.NormalizeLedgerExactMatchKey(master.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var safeAlternatives = masters.Where(master => counts[AccountingAutomationHub.Infrastructure.Banking.BankNarrationParser.NormalizeLedgerExactMatchKey(master.Name)] == 1).ToArray();
        var dialog = new Window { Title = "Resolve Bank Ledger Mapping Master", Width = 750, Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = System.Windows.Window.GetWindow(this), MinWidth = 650, MinHeight = 400 };
        var content = new DockPanel { Margin = new Thickness(18) };
        dialog.Content = content;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(footer, Dock.Bottom); content.Children.Add(footer);
        var panel = new StackPanel(); content.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = $"Mapping: {row.SummaryName}  |  Transactions: {row.Count}", FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) });
        panel.Children.Add(new TextBlock { Text = ambiguous.Length > 1
            ? "Multiple independent Masters have this name. Tally MASTERID is the identity, so mapping to one by name would be unsafe. Rename one in Tally/AAH Master and sync, or choose a different uniquely named ledger below."
            : "Select a unique active ledger for this Bank mapping. No duplicate Master will be merged or altered.",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkRed, Margin = new Thickness(0,0,0,10) });
        foreach (var master in ambiguous)
            panel.Children.Add(new TextBlock { Text = $"{master.Name}  |  Tally MASTERID: {(string.IsNullOrWhiteSpace(master.TallyMasterId) ? "Unbound" : master.TallyMasterId)}  |  AAH row: {master.PersistentId}  |  Group: {master.Parent}",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4,2,4,2) });
        panel.Children.Add(new TextBlock { Text = "Choose a uniquely named existing ledger:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,14,0,6) });
        var selection = new ComboBox { Height = 35, ItemsSource = safeAlternatives, DisplayMemberPath = "Name" };
        panel.Children.Add(selection);
        var use = new Button { Content = "Use Selected Unique Ledger", Margin = new Thickness(8), Padding = new Thickness(12,8,12,8), IsEnabled = safeAlternatives.Length > 0 };
        use.Click += (_, _) => {
            if (selection.SelectedItem is not AccountingAutomationHub.Domain.Masters.MasterRecord master) return;
            row.FinalLedger = master.Name;
            row.LedgerGroup = master.Parent;
            vm.StageLedgerMappingRow(row);
            if (row.Status.StartsWith("Ready", StringComparison.OrdinalIgnoreCase)) dialog.DialogResult = true;
            else System.Windows.MessageBox.Show(row.Status, "Ledger Mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        footer.Children.Add(use);
        var close = new Button { Content = "Close / Resolve in Ledger Master", Padding = new Thickness(12,8,12,8), Margin = new Thickness(8) };
        close.Click += (_, _) => dialog.Close(); footer.Children.Add(close);
        dialog.ShowDialog();
    }

    private void LedgerMappingGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        var cell = FindVisualParent<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell?.DataContext is LedgerMappingWorkbenchRow row)
        {
            if (ViewModel is not null) ViewModel.SelectedLedgerMappingRow = row;
        }

        if (cell?.DataContext is LedgerMappingWorkbenchRow summaryRow &&
            string.Equals(cell.Column.Header?.ToString(), "Summary Name", StringComparison.OrdinalIgnoreCase))
        {
            ViewModel?.OpenLedgerMappingPreview(summaryRow);
            ShowLedgerMappingPreviewWindow();
            return;
        }

        CloseLedgerMappingPreviewWindow();
    }

    private void LedgerMappingGrid_CurrentCellChanged(object? sender, EventArgs e)
    {
        if (sender is not DataGrid grid || !grid.IsKeyboardFocusWithin || Mouse.LeftButton == MouseButtonState.Pressed) return;

        if (grid.CurrentItem is LedgerMappingWorkbenchRow currentRow && ViewModel is not null)
            ViewModel.SelectedLedgerMappingRow = currentRow;

        if (grid.CurrentItem is LedgerMappingWorkbenchRow row &&
            string.Equals(grid.CurrentColumn?.Header?.ToString(), "Summary Name", StringComparison.OrdinalIgnoreCase))
        {
            ViewModel?.OpenLedgerMappingPreview(row);
            ShowLedgerMappingPreviewWindow();
            return;
        }

        CloseLedgerMappingPreviewWindow();
    }

    private void BankingTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, BankingTabs)) return;
        if (BankingTabs.SelectedIndex != 4)
        {
            CloseLedgerMappingPreviewWindow();
            CloseBulkLedgerMappingWindow();
        }
    }

    private void BulkMapSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BankingViewModel permissionVm && !permissionVm.CanEditLedgerMapping) { e.Handled = true; return; }
        var viewModel = ViewModel;
        if (viewModel is null) return;
        if (viewModel.OpenBulkSelectedMappingCommand.CanExecute(null))
            viewModel.OpenBulkSelectedMappingCommand.Execute(null);
        ShowBulkLedgerMappingWindow();
    }

    private void ShowBulkLedgerMappingWindow()
    {
        var viewModel = ViewModel;
        if (viewModel is null || !viewModel.IsBulkLedgerMappingOpen) return;

        if (_bulkLedgerMappingWindow is null)
        {
            _bulkLedgerMappingWindow = new BulkLedgerMappingWindow
            {
                Owner = Window.GetWindow(this),
                DataContext = viewModel
            };
            _bulkLedgerMappingWindow.Closed += (_, _) => _bulkLedgerMappingWindow = null;
            _bulkLedgerMappingWindow.Show();
        }
        else
        {
            _bulkLedgerMappingWindow.DataContext = viewModel;
            if (!_bulkLedgerMappingWindow.IsVisible) _bulkLedgerMappingWindow.Show();
            _bulkLedgerMappingWindow.Activate();
        }
    }

    private void CloseBulkLedgerMappingWindow()
    {
        if (ViewModel?.IsBulkLedgerMappingOpen == true)
            ViewModel.CloseBulkSelectedMappingCommand.Execute(null);
        if (_bulkLedgerMappingWindow?.IsVisible == true) _bulkLedgerMappingWindow.Close();
        _bulkLedgerMappingWindow = null;
    }

    private void ShowLedgerMappingPreviewWindow()
    {
        var viewModel = ViewModel;
        if (viewModel is null || !viewModel.IsLedgerMappingDetailOpen) return;

        if (_ledgerMappingPreviewWindow is null)
        {
            _ledgerMappingPreviewWindow = new LedgerMappingPreviewWindow
            {
                Owner = Window.GetWindow(this),
                DataContext = viewModel
            };
            _ledgerMappingPreviewWindow.Closed += (_, _) => _ledgerMappingPreviewWindow = null;
            _ledgerMappingPreviewWindow.Show();
        }
        else
        {
            _ledgerMappingPreviewWindow.DataContext = viewModel;
            if (!_ledgerMappingPreviewWindow.IsVisible) _ledgerMappingPreviewWindow.Show();
            _ledgerMappingPreviewWindow.Activate();
        }
    }

    private void CloseLedgerMappingPreviewWindow()
    {
        ViewModel?.CloseLedgerMappingPreview();
        if (_ledgerMappingPreviewWindow?.IsVisible == true) _ledgerMappingPreviewWindow.Close();
        _ledgerMappingPreviewWindow = null;
    }

    private void BankingView_Unloaded(object sender, RoutedEventArgs e)
    {
        CloseLedgerMappingPreviewWindow();
        CloseBulkLedgerMappingWindow();
        if (_emptyLedgerExportWindow?.IsVisible == true) _emptyLedgerExportWindow.Close();
        _emptyLedgerExportWindow = null;
        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged -= BankingViewModel_PropertyChanged;
        _observedViewModel = null;
    }

    private void BankingView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        CloseLedgerMappingPreviewWindow();
        if (_emptyLedgerExportWindow?.IsVisible == true) _emptyLedgerExportWindow.Close();
        _emptyLedgerExportWindow = null;
        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged -= BankingViewModel_PropertyChanged;
        _observedViewModel = e.NewValue as BankingViewModel;
        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged += BankingViewModel_PropertyChanged;
        if (IsLoaded)
        {
            ApplyBankStatementColumnLayout();
            Dispatcher.BeginInvoke(() => UpdateBankBalanceNavigator(), DispatcherPriority.Loaded);
        }
    }

    private void BankingViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(BankingViewModel.SelectedAccount) or nameof(BankingViewModel.AccountCount))) return;
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(() => UpdateBankBalanceNavigator(), DispatcherPriority.Background);
    }

    private void EmptyLedgerExport_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null) return;
        viewModel.PrepareEmptyLedgerExportPanel();

        if (_emptyLedgerExportWindow is null)
        {
            _emptyLedgerExportWindow = new EmptyLedgerExportWindow
            {
                Owner = Window.GetWindow(this),
                DataContext = viewModel
            };
            _emptyLedgerExportWindow.Closed += (_, _) => _emptyLedgerExportWindow = null;
            _emptyLedgerExportWindow.Show();
        }
        else
        {
            _emptyLedgerExportWindow.DataContext = viewModel;
            _emptyLedgerExportWindow.Activate();
        }
    }

    private void ManualCashContra_Click(object sender, RoutedEventArgs e)
    {
        var rows = BankStatementGrid.SelectedItems.OfType<BankTransactionRecord>().ToArray();
        if (rows.Length == 0 && ViewModel?.SelectedTransaction is BankTransactionRecord selected) rows = [selected];
        ViewModel?.OpenManualCashContra(rows, "Bank Statement");
    }

    private void LedgerMappingManualCashContra_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenManualCashContraFromLedgerMapping();
    }

    private void ManualInterbankLink_Click(object sender, RoutedEventArgs e)
    {
        var rows = BankStatementGrid.SelectedItems.OfType<BankTransactionRecord>().ToArray();
        ViewModel?.ManualLinkInterbank(rows);
    }

    private void AdvancedReconciliation_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null) return;
        var rows = BankStatementGrid.SelectedItems.OfType<BankTransactionRecord>().ToArray();
        if (rows.Length == 0)
        {
            MessageBox.Show("Select one or more Bank Statement rows first.", "Advanced Reconciliation", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var window = new AdvancedReconciliationWindow(viewModel, rows) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }

    private void GridHeaderFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var header = FindVisualParent<DataGridColumnHeader>(button);
        var grid = FindVisualParent<DataGrid>(header);
        var columnName = header?.Column?.Header?.ToString()?.Trim() ?? string.Empty;
        if (grid is null || columnName.Length == 0 || ViewModel is null) return;

        var values = ReferenceEquals(grid, BankStatementGrid)
            ? ViewModel.GetBankStatementHeaderValues(columnName)
            : ReferenceEquals(grid, LedgerMappingGrid)
                ? ViewModel.GetLedgerMappingHeaderValues(columnName)
                : Array.Empty<string>();
        if (values.Count == 0) return;

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            MaxHeight = 560,
            MaxWidth = 600
        };
        var allItem = new MenuItem { Header = "(All values)", FontWeight = FontWeights.SemiBold };
        allItem.Click += (_, _) =>
        {
            if (ReferenceEquals(grid, BankStatementGrid)) ViewModel.ApplyBankStatementHeaderFilter("ALL COLUMNS", string.Empty);
            else ViewModel.ApplyLedgerMappingHeaderFilter("ALL COLUMNS", string.Empty);
        };
        menu.Items.Add(allItem);
        menu.Items.Add(new Separator());

        // Date filters are grouped Year -> Month -> exact date. This keeps long
        // statements usable and avoids the old alphabetic date list (01, 02, 03...).
        if (ReferenceEquals(grid, BankStatementGrid) &&
            columnName.Equals("Date", StringComparison.OrdinalIgnoreCase))
        {
            var dates = values
                .Select(value => DateTime.TryParseExact(value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? (Value: value, Date: (DateTime?)date)
                    : (Value: value, Date: (DateTime?)null))
                .Where(item => item.Date.HasValue)
                .Select(item => (item.Value, Date: item.Date!.Value))
                .ToArray();

            foreach (var yearGroup in dates.GroupBy(item => item.Date.Year).OrderByDescending(group => group.Key))
            {
                var yearItem = new MenuItem { Header = yearGroup.Key.ToString(CultureInfo.InvariantCulture) };
                foreach (var monthGroup in yearGroup.GroupBy(item => item.Date.Month).OrderBy(group => group.Key))
                {
                    var monthName = CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(monthGroup.Key);
                    var monthItem = new MenuItem { Header = monthName };
                    foreach (var dateItemValue in monthGroup.OrderBy(item => item.Date))
                    {
                        var dateMenuItem = new MenuItem { Header = dateItemValue.Date.ToString("dd.MM.yyyy"), Tag = dateItemValue.Value };
                        dateMenuItem.Click += (_, _) =>
                            ViewModel.ApplyBankStatementHeaderFilter(columnName, "=" + (dateMenuItem.Tag?.ToString() ?? string.Empty));
                        monthItem.Items.Add(dateMenuItem);
                    }
                    yearItem.Items.Add(monthItem);
                }
                menu.Items.Add(yearItem);
            }
        }
        else
        {
            IEnumerable<string> ordered = values;
            if (columnName.Equals("Count", StringComparison.OrdinalIgnoreCase) ||
                columnName.Equals("Debit", StringComparison.OrdinalIgnoreCase) ||
                columnName.Equals("Credit", StringComparison.OrdinalIgnoreCase))
            {
                ordered = values.OrderBy(value =>
                {
                    return decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var number) ||
                           decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out number)
                        ? number
                        : decimal.MaxValue;
                }).ThenBy(value => value, StringComparer.CurrentCultureIgnoreCase);
            }

            foreach (var value in ordered)
            {
                var display = value.Length > 100 ? value[..100] + "…" : value;
                var item = new MenuItem { Header = display, ToolTip = value, Tag = value };
                item.Click += (_, _) =>
                {
                    var selected = item.Tag?.ToString() ?? string.Empty;
                    if (ReferenceEquals(grid, BankStatementGrid)) ViewModel.ApplyBankStatementHeaderFilter(columnName, "=" + selected);
                    else ViewModel.ApplyLedgerMappingHeaderFilter(columnName, "=" + selected);
                };
                menu.Items.Add(item);
            }
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void BankStatementClearFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.ClearBankStatementFilter();
    }

    private void LedgerMappingClearFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.ClearLedgerMappingTextFilter();
        CloseLedgerMappingPreviewWindow();
    }

    private void BankStatementClearSort_Click(object sender, RoutedEventArgs e)
    {
        ClearDataGridSort(BankStatementGrid);
    }

    private void LedgerMappingClearSort_Click(object sender, RoutedEventArgs e)
    {
        ClearDataGridSort(LedgerMappingGrid);
    }

    private void BankStatementClearSortFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.ClearBankStatementFilter();
        DataGridColumnFilterBehavior.ClearAllSortAndFilters(BankStatementGrid);
    }

    private void LedgerMappingClearSortFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.ClearLedgerMappingTextFilter();
        CloseLedgerMappingPreviewWindow();
        DataGridColumnFilterBehavior.ClearAllSortAndFilters(LedgerMappingGrid);
    }

    private static void ClearDataGridSort(DataGrid grid)
    {
        // Finish any active editor before changing the view's sort descriptors.
        // This avoids WPF's "Refresh is not allowed during an edit transaction"
        // failure when Clear Sort is pressed immediately after editing a cell.
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        grid.Items.SortDescriptions.Clear();
        foreach (var column in grid.Columns) column.SortDirection = null;
        grid.Items.Refresh();
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void LedgerMappingGrid_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        if (e.Key == Key.Escape)
        {
            SuggestTextBox? editor = null;
            if (Keyboard.FocusedElement is DependencyObject escapeFocus)
                editor = escapeFocus as SuggestTextBox ?? FindVisualParent<SuggestTextBox>(escapeFocus);
            if (editor is null && _ledgerMappingEditRow is null) return;
            ExitLedgerMappingEditor(editor);
            e.Handled = true;
            return;
        }

        var row = grid.CurrentItem as LedgerMappingWorkbenchRow ?? ViewModel?.SelectedLedgerMappingRow;
        if (row is null) return;

        SuggestTextBox? activeEditor = null;
        if (Keyboard.FocusedElement is DependencyObject focusedElement)
            activeEditor = focusedElement as SuggestTextBox ?? FindVisualParent<SuggestTextBox>(focusedElement);

        if (e.Key == Key.Enter && activeEditor?.IsExplicitEditorActive == true)
        {
            activeEditor.CommitHighlightedSuggestion();
            activeEditor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ViewModel?.StageLedgerMappingRow(row);
            var editorRole = activeEditor.Tag?.ToString() ?? string.Empty;
            ClearLedgerMappingEditSnapshot();
            activeEditor.DeactivateEditor();

            if (editorRole.Equals("FinalLedger", StringComparison.OrdinalIgnoreCase))
                MoveLedgerMappingGridCell(grid, row, GetLedgerMappingColumnIndex("Ledger Group"), 0);
            else
                MoveLedgerMappingGridCell(grid, row, GetLedgerMappingColumnIndex("Final Ledger"), 1);

            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2)
        {
            var header = grid.CurrentColumn?.Header?.ToString() ?? string.Empty;
            if (header.Equals("Final Ledger", StringComparison.OrdinalIgnoreCase))
            {
                FocusLedgerMappingEditor(row, GetLedgerMappingColumnIndex("Final Ledger"));
                e.Handled = true;
                return;
            }
            if (header.Equals("Ledger Group", StringComparison.OrdinalIgnoreCase))
            {
                FocusLedgerMappingEditor(row, GetLedgerMappingColumnIndex("Ledger Group"));
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Delete)
        {
            if (activeEditor?.IsExplicitEditorActive == true) return;
            ViewModel?.ClearFinalLedger(row);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;

        if (string.Equals(grid.CurrentColumn?.Header?.ToString(), "Summary Name", StringComparison.OrdinalIgnoreCase))
        {
            ViewModel?.OpenLedgerMappingPreview(row);
            ShowLedgerMappingPreviewWindow();
            e.Handled = true;
            return;
        }

        // Enter is navigation unless the user deliberately activated an editor.  This
        // keeps normal DataGrid arrow-key navigation available and prevents Enter from
        // trapping the workbench inside Final Ledger/Ledger Group edit mode.
        var currentColumnIndex = Math.Max(0, grid.Columns.IndexOf(grid.CurrentColumn));
        MoveLedgerMappingGridCell(grid, row, currentColumnIndex, 1);
        e.Handled = true;
    }

    private static void MoveLedgerMappingGridCell(DataGrid grid, LedgerMappingWorkbenchRow currentRow, int columnIndex, int rowOffset)
    {
        if (grid.Columns.Count == 0) return;
        var currentIndex = grid.Items.IndexOf(currentRow);
        var targetIndex = Math.Clamp(currentIndex + rowOffset, 0, Math.Max(0, grid.Items.Count - 1));
        if (targetIndex < 0 || targetIndex >= grid.Items.Count || grid.Items[targetIndex] is not LedgerMappingWorkbenchRow targetRow) return;
        var targetColumn = grid.Columns[Math.Clamp(columnIndex, 0, grid.Columns.Count - 1)];
        grid.ScrollIntoView(targetRow, targetColumn);
        grid.CurrentCell = new DataGridCellInfo(targetRow, targetColumn);
        grid.Focus();
        Keyboard.Focus(grid);
    }

    private void LedgerMappingGrid_PreviewTextInput(
        object sender,
        TextCompositionEventArgs e)
    {
        if (sender is not DataGrid grid || string.IsNullOrEmpty(e.Text)) return;

        // If the editable TextBox already owns focus, WPF will deliver the text
        // normally. The special path below is only for the first character typed
        // while a Final Ledger / Ledger Group cell itself still owns focus.
        if (Keyboard.FocusedElement is DependencyObject focused &&
            FindVisualParent<SuggestTextBox>(focused) is not null)
        {
            return;
        }

        var header = grid.CurrentColumn?.Header?.ToString() ?? string.Empty;
        if (!header.Equals("Final Ledger", StringComparison.OrdinalIgnoreCase) &&
            !header.Equals("Ledger Group", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (grid.CurrentItem is not LedgerMappingWorkbenchRow row) return;
        grid.ScrollIntoView(row, grid.CurrentColumn);
        grid.UpdateLayout();

        var firstText = e.Text;
        var content = grid.CurrentColumn?.GetCellContent(row);
        var editor = content as SuggestTextBox ?? FindVisualChild<SuggestTextBox>(content);
        if (editor is not null)
        {
            editor.BeginTyping(firstText);
            if (ViewModel is not null) ViewModel.SelectedLedgerMappingRow = row;
            e.Handled = true;
            return;
        }

        grid.Dispatcher.BeginInvoke(() =>
        {
            var deferredContent = grid.CurrentColumn?.GetCellContent(row);
            var deferredEditor = deferredContent as SuggestTextBox ?? FindVisualChild<SuggestTextBox>(deferredContent);
            if (deferredEditor is null) return;
            deferredEditor.BeginTyping(firstText);
            if (ViewModel is not null) ViewModel.SelectedLedgerMappingRow = row;
        }, DispatcherPriority.Send);
        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject? source) where T : DependencyObject
    {
        if (source is null) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private BankingViewModel? ViewModel =>
        DataContext as BankingViewModel;

    private void QueueAfterCommit(Action action)
    {
        _rowSavePending = true;

        Dispatcher.BeginInvoke(
            () =>
            {
                try
                {
                    action();
                }
                finally
                {
                    _rowSavePending = false;
                }
            },
            // Fix72: run after WPF binding has committed the edited Ledger value but
            // before the next input command (for example Dashboard -> Tally Export).
            // Background priority allowed the export click to read the previous saved
            // Bank Ledger before this row-save callback executed.
            DispatcherPriority.DataBind);
    }
}
