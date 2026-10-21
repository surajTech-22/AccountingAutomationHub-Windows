using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Application.Clients;
using AccountingAutomationHub.Desktop.Services;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Clients;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Infrastructure.Accounting;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Gst;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Masters;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AdvancedClosingEntryWindow : Window
{
    private readonly ClientProfile _client;
    private readonly LedgerTotalService _ledgerTotals;
    private readonly SqliteBankingRepository _banking;
    private readonly SqliteJournalRepository _journals;
    private readonly SqliteMasterRepository _masters;
    private readonly IClientPreferenceRepository _clientPreferences;
    private readonly AccountingLiveChangeHub _liveChangeHub;
    private readonly Action _refreshAfterCreate;
    private readonly AdvancedClosingEntryPlanner _planner;
    private IReadOnlyList<string> _allLedgers = [];
    private bool _allMasterLedgersLoaded;
    private readonly List<AdvancedClosingLedgerRow> _rows;
    private readonly ICollectionView _view;
    private readonly Dictionary<string, int?> _manualSplitOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedSplitPreferenceKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedOppositeLedgerPreferences = new(StringComparer.OrdinalIgnoreCase);
    private AdvancedClosingExistingSnapshot? _existingGeneratedSnapshot;
    private bool _loading;

    public AdvancedClosingEntryWindow(
        ClientProfile client,
        LedgerTotalService ledgerTotals,
        SqliteBankingRepository banking,
        SqliteJournalRepository journals,
        SqliteMasterRepository masters,
        IClientPreferenceRepository clientPreferences,
        AccountingLiveChangeHub liveChangeHub,
        Action refreshAfterCreate,
        IReadOnlyCollection<string>? eligibleLedgerNames = null,
        IReadOnlyCollection<string>? preselectedLedgerNames = null)
    {
        InitializeComponent();
        _client = client;
        _ledgerTotals = ledgerTotals;
        _banking = banking;
        _journals = journals;
        _masters = masters;
        _clientPreferences = clientPreferences;
        _liveChangeHub = liveChangeHub;
        _refreshAfterCreate = refreshAfterCreate;
        _planner = new AdvancedClosingEntryPlanner(ledgerTotals, banking, journals);
        _loading = true;

        ClientCaption.Text = $"{(string.IsNullOrWhiteSpace(client.TradeName) ? client.ClientName : client.TradeName)}  •  FY {client.FinancialYear}";
        MethodBox.SelectedIndex = 0;
        var (_, fyTo) = AdvancedClosingEntryPlanner.FinancialYearRange(client.FinancialYear);
        ManualDatePicker.SelectedDate = fyTo;

        // Fix32: do not query the full Master repository just to paint a Cash-mode
        // Closure window. The active ledger list comes from the prepared LedgerTotal cache;
        // the complete Master ledger list is loaded only if Journal mode actually needs it.
        LoadClientSpecificSettings(fyTo);

        // Fix32: historical ACLOSE repair no longer runs synchronously while the Closure
        // window is opening. Cash workspace owns that durable background repair. Closure
        // opening therefore paints from the prepared LedgerTotal cache without mutation work.

        var eligible = eligibleLedgerNames is null
            ? null
            : eligibleLedgerNames.Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _rows = _planner.BuildLedgerRows(client.Id, client.FinancialYear)
            .Where(row => eligible is null || eligible.Contains(row.LedgerName))
            .ToList();
        _allLedgers = _rows.Select(row => row.LedgerName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        DefaultOppositeLedgerBox.ItemsSource = _allLedgers;
        var preselected = preselectedLedgerNames is null
            ? null
            : preselectedLedgerNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            // Fix26 R3: apply constructor-time preselection before subscribing to
            // PropertyChanged. In Fix26/R1/R2, IsSelected fired Row_PropertyChanged
            // while _view was still uninitialized; UpdateSelectionCaption then
            // called LINQ Cast on that null view and threw ArgumentNullException
            // (Parameter 'source') only when a ledger was preselected.
            if (preselected?.Contains(row.LedgerName) == true) row.IsSelected = true;
            row.PropertyChanged += Row_PropertyChanged;
        }
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        LedgerGrid.ItemsSource = _view;

        var groups = new[] { "All Groups" }
            .Concat(_rows.Select(row => row.GroupName).Where(group => group.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(group => group, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        GroupFilterBox.ItemsSource = groups;
        GroupFilterBox.SelectedIndex = 0;
        // Use the same application-wide Search / Advanced Filter authority as the
        // other accounting grids. This restores field suggestions, symbol operators
        // (> < >= <= = !=), comma-AND search, sort commands and Clear Sort & Filter.
        SearchFilterBar.Attach(LedgerGrid);
        SearchFilterBar.RefreshFields();
        SearchFilterBar.SearchApplied += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateSelectionCaption), System.Windows.Threading.DispatcherPriority.Background);
        StatusText.Text = "Cache-first Closure ready. Cash mode is closability-gated (Debtor Dr -> Sales Receipt; Creditor Cr -> Purchase Payment); Journal mode remains source-voucher driven irrespective of closing balance. Space toggles highlighted rows. Maximum Amount is per day; blank means unlimited.";
        _loading = false;
        UpdateMethodUi();
        JournalDefaultPanel.Visibility = IsJournalMode ? Visibility.Visible : Visibility.Collapsed;
        CashModeHint.Visibility = IsJournalMode ? Visibility.Collapsed : Visibility.Visible;
        if (IsJournalMode) EnsureAllLedgersLoaded();
        UpdateSelectionCaption();
        var firstPreselected = _rows.FirstOrDefault(row => row.IsSelected);
        if (firstPreselected is not null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LedgerGrid.SelectedItem = firstPreselected;
                LedgerGrid.ScrollIntoView(firstPreselected);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private string PreferenceKey(string suffix) =>
        $"AdvancedClosure:{_client.FinancialYear.Trim()}:{suffix}";

    private string LedgerPreferenceKey(string ledgerName, string suffix) =>
        PreferenceKey($"Ledger:{ledgerName.Trim()}:{suffix}");

    private string SplitPreferenceKey(string identity) => PreferenceKey($"Split:{identity}");

    private string ReadPreference(string key)
    {
        try { return _clientPreferences.Get(_client.Id, key)?.Trim() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private void WritePreference(string key, string value)
    {
        try { _clientPreferences.Set(_client.Id, key, value ?? string.Empty); }
        catch { /* A preference write must never block accounting work. */ }
    }

    private void LoadClientSpecificSettings(DateTime fyTo)
    {
        // Fix27: every Advanced Closure setting is scoped by ClientId in the
        // ClientPreferences table and by Financial Year in the preference key.
        var savedMethod = ReadPreference(PreferenceKey("Method"));
        if (savedMethod.Length > 0)
        {
            foreach (var item in MethodBox.Items.OfType<ComboBoxItem>())
            {
                if (!string.Equals(item.Content?.ToString()?.Trim(), savedMethod, StringComparison.OrdinalIgnoreCase)) continue;
                MethodBox.SelectedItem = item;
                break;
            }
        }

        var savedDate = ReadPreference(PreferenceKey("ManualDate"));
        var (fyFrom, rangeTo) = AdvancedClosingEntryPlanner.FinancialYearRange(_client.FinancialYear);
        if (DateTime.TryParseExact(savedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate) &&
            parsedDate.Date >= fyFrom && parsedDate.Date <= rangeTo)
            ManualDatePicker.SelectedDate = parsedDate.Date;
        else
            ManualDatePicker.SelectedDate = fyTo;

        MaximumAmountBox.Text = ReadPreference(PreferenceKey("MaximumPerDay"));
        var entryMode = ReadPreference(PreferenceKey("EntryMode"));
        if (entryMode.Equals("Journal", StringComparison.OrdinalIgnoreCase)) JournalMode.IsChecked = true;
        else CashMode.IsChecked = true;
        DefaultOppositeLedgerBox.Text = ReadPreference(PreferenceKey("DefaultOppositeLedger"));
    }

    private void PersistCommonSettings()
    {
        if (_loading) return;
        WritePreference(PreferenceKey("Method"), SelectedMethod);
        WritePreference(PreferenceKey("ManualDate"), (ManualDatePicker.SelectedDate ?? AdvancedClosingEntryPlanner.FinancialYearRange(_client.FinancialYear).To).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        WritePreference(PreferenceKey("MaximumPerDay"), MaximumAmountBox.Text?.Trim() ?? string.Empty);
        WritePreference(PreferenceKey("EntryMode"), IsJournalMode ? "Journal" : "Cash");
        WritePreference(PreferenceKey("DefaultOppositeLedger"), DefaultOppositeLedgerBox.Text?.Trim() ?? string.Empty);
    }

    private void PersistLedgerSettings(AdvancedClosingLedgerRow row)
    {
        WritePreference(LedgerPreferenceKey(row.LedgerName, "OppositeLedger"), row.OppositeLedger);
        foreach (var bucket in row.Buckets)
        {
            if (!_manualSplitOverrides.ContainsKey(bucket.Identity)) continue;
            WritePreference(SplitPreferenceKey(bucket.Identity), bucket.ManualSplitCount.HasValue ? bucket.ManualSplitCount.Value.ToString(CultureInfo.InvariantCulture) : "AUTO");
        }
    }

    private void LoadPersistedSplitOverrides(AdvancedClosingLedgerRow row)
    {
        foreach (var bucket in row.Buckets)
        {
            if (_manualSplitOverrides.ContainsKey(bucket.Identity) || !_loadedSplitPreferenceKeys.Add(bucket.Identity)) continue;
            var saved = ReadPreference(SplitPreferenceKey(bucket.Identity));
            if (saved.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
            {
                _manualSplitOverrides[bucket.Identity] = null;
                continue;
            }
            if (int.TryParse(saved, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0)
                _manualSplitOverrides[bucket.Identity] = count;
        }
    }

    private string SelectedMethod => (MethodBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim() ?? "Year End";
    private bool IsJournalMode => JournalMode.IsChecked == true;

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Constructor-time population/preselection must never touch _view before
        // CollectionViewSource has been created. Normal user selection updates run
        // after _loading is cleared.
        if (_loading) return;
        if (e.PropertyName == nameof(AdvancedClosingLedgerRow.IsSelected)) UpdateSelectionCaption();
    }

    private bool FilterRow(object value)
    {
        if (value is not AdvancedClosingLedgerRow row) return false;
        var group = GroupFilterBox?.SelectedItem?.ToString()?.Trim() ?? "All Groups";
        return group.Equals("All Groups", StringComparison.OrdinalIgnoreCase) || row.GroupName.Equals(group, StringComparison.OrdinalIgnoreCase);
    }

    private void GroupFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        RefreshLedgerViewSafely();
        UpdateSelectionCaption();
    }

    private void CommitPendingGridEdit()
    {
        if (LedgerGrid is null) return;
        try { LedgerGrid.CommitEdit(DataGridEditingUnit.Cell, true); } catch { }
        try { LedgerGrid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }
        if (_view is IEditableCollectionView editable)
        {
            try { if (editable.IsAddingNew) editable.CommitNew(); } catch { }
            try { if (editable.IsEditingItem) editable.CommitEdit(); } catch { }
        }
    }

    private void RefreshLedgerViewSafely()
    {
        CommitPendingGridEdit();
        try { _view.Refresh(); }
        catch (InvalidOperationException)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                CommitPendingGridEdit();
                try { _view.Refresh(); } catch { }
                UpdateSelectionCaption();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void PlanSetting_Changed(object sender, EventArgs e)
    {
        if (_loading) return;
        UpdateMethodUi();
        PersistCommonSettings();
        MarkPlansForRefresh();
    }

    private void EntryMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || JournalDefaultPanel is null) return;
        JournalDefaultPanel.Visibility = IsJournalMode ? Visibility.Visible : Visibility.Collapsed;
        CashModeHint.Visibility = IsJournalMode ? Visibility.Collapsed : Visibility.Visible;
        if (IsJournalMode)
        {
            EnsureAllLedgersLoaded();
            foreach (var row in _rows.Where(item => item.IsSelected)) EnsureOppositeLedgerPreferenceLoaded(row);
        }
        PersistCommonSettings();
    }

    private void UpdateMethodUi()
    {
        if (ManualDatePicker is null) return;
        ManualDatePicker.IsEnabled = SelectedMethod.Equals("Manual Date", StringComparison.OrdinalIgnoreCase);
    }

    private void MaximumAmountBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!TryMaximumAmount(out _, showError: true)) return;
        PersistCommonSettings();
        MarkPlansForRefresh();
    }

    private void RefreshExistingGeneratedSnapshot() =>
        _existingGeneratedSnapshot = _planner.LoadExistingGeneratedSnapshot(_client.Id, _client.FinancialYear);

    private AdvancedClosingExistingSnapshot EnsureExistingGeneratedSnapshot()
    {
        _existingGeneratedSnapshot ??= _planner.LoadExistingGeneratedSnapshot(_client.Id, _client.FinancialYear);
        return _existingGeneratedSnapshot;
    }

    private void EnsureAllLedgersLoaded()
    {
        if (_allMasterLedgersLoaded) return;
        var masterLedgers = (_masters.Get(_client.Id, _client.FinancialYear, includeArchived: false) ?? [])
            .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !item.IsArchived && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => item.Name.Trim());
        _allLedgers = masterLedgers.Concat(_allLedgers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        DefaultOppositeLedgerBox.ItemsSource = _allLedgers;
        _allMasterLedgersLoaded = true;
    }

    private void EnsureOppositeLedgerPreferenceLoaded(AdvancedClosingLedgerRow row)
    {
        if (!_loadedOppositeLedgerPreferences.Add(row.LedgerName)) return;
        row.OppositeLedger = ReadPreference(LedgerPreferenceKey(row.LedgerName, "OppositeLedger"));
    }

    private bool TryMaximumAmount(out decimal? maximum, bool showError)
    {
        maximum = null;
        var text = MaximumAmountBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 0) return true;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) &&
            !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            if (showError) MessageBox.Show(this, "Enter a valid Maximum Amount or leave it blank for no limit.", "Closure Entry", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        value = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        if (value < 0.01m)
        {
            if (showError) MessageBox.Show(this, "Maximum Amount must be at least 0.01, or blank for no limit.", "Closure Entry", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        maximum = value;
        return true;
    }

    private DateTime? SelectedManualDate()
    {
        if (!SelectedMethod.Equals("Manual Date", StringComparison.OrdinalIgnoreCase)) return null;
        return ManualDatePicker.SelectedDate?.Date;
    }

    private void LedgerGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Space || sender is not DataGrid grid) return;
        var highlighted = grid.SelectedItems.Cast<object>().OfType<AdvancedClosingLedgerRow>().ToArray();
        if (highlighted.Length == 0 && grid.CurrentItem is AdvancedClosingLedgerRow current) highlighted = [current];
        if (highlighted.Length == 0) return;
        var select = highlighted.Any(row => !row.IsSelected);
        foreach (var row in highlighted) row.IsSelected = select;
        UpdateSelectionCaption();
        e.Handled = true;
    }

    private void SelectFiltered_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _view.Cast<object>().OfType<AdvancedClosingLedgerRow>()) item.IsSelected = true;
        UpdateSelectionCaption();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _rows) item.IsSelected = false;
        LedgerGrid.UnselectAll();
        UpdateSelectionCaption();
    }

    private void UpdateSelectionCaption()
    {
        if (SelectionCaption is null) return;
        SelectionCaption.Text = $"{_rows.Count(row => row.IsSelected):N0} selected • {_view.Cast<object>().Count():N0} visible";
    }

    private void DefaultOppositeLedgerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        PersistCommonSettings();
    }

    private void DefaultOppositeLedgerBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        PersistCommonSettings();
    }

    private void ApplyOppositeToSelected_Click(object sender, RoutedEventArgs e)
    {
        var ledger = DefaultOppositeLedgerBox.Text?.Trim() ?? string.Empty;
        if (ledger.Length == 0)
        {
            MessageBox.Show(this, "Select or enter a Journal opposite ledger first.", "Closure Entry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var row in _rows.Where(row => row.IsSelected && !row.LedgerName.Equals(ledger, StringComparison.OrdinalIgnoreCase)))
        {
            row.OppositeLedger = ledger;
            PersistLedgerSettings(row);
        }
        PersistCommonSettings();
    }

    private void PreviewSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidatePlanningInputs()) return;
        var selected = _rows.Where(row => row.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Select one or more ledgers first.", "Closure Entry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RefreshExistingGeneratedSnapshot();
        foreach (var row in selected) ReplanRow(row);
        var entries = selected.Sum(row => row.PlannedEntryCount);
        var amount = selected.Sum(row => row.PlannedAmount);
        StatusText.Text = $"Preview ready: {selected.Length:N0} ledger(s), {entries:N0} new reverse entr{(entries == 1 ? "y" : "ies")}, {amount:N2} total planned. Use Details on any ledger to inspect or change its split count.";
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AdvancedClosingLedgerRow row) return;
        if (!ValidatePlanningInputs()) return;
        EnsureExistingGeneratedSnapshot();
        if (IsJournalMode)
        {
            EnsureAllLedgersLoaded();
            EnsureOppositeLedgerPreferenceLoaded(row);
        }
        ReplanRow(row);
        var popup = new AdvancedClosingEntryDetailsWindow(row, IsJournalMode, _allLedgers, ReplanRow) { Owner = this };
        popup.ShowDialog();
        PersistLedgerSettings(row);
    }

    private void ReplanRow(AdvancedClosingLedgerRow row)
    {
        if (!TryMaximumAmount(out var maximum, showError: false)) return;

        // Preserve any manual change made in the Details popup before rebuilding the
        // bucket collection, then merge Client+FY persisted split settings for buckets
        // that have not yet been seen in this window.
        foreach (var bucket in row.Buckets)
            if (bucket.ManualSplitCount.HasValue || _manualSplitOverrides.ContainsKey(bucket.Identity))
                _manualSplitOverrides[bucket.Identity] = bucket.ManualSplitCount;

        var existingSnapshot = EnsureExistingGeneratedSnapshot();
        _planner.PlanLedger(_client.Id, _client.FinancialYear, row, SelectedMethod, SelectedManualDate(), maximum, existingSnapshot, cashMode: !IsJournalMode);
        LoadPersistedSplitOverrides(row);
        if (row.Buckets.Any(bucket => _manualSplitOverrides.ContainsKey(bucket.Identity)))
            _planner.ReplanBucketCounts(_client.Id, _client.FinancialYear, row, SelectedMethod, SelectedManualDate(), maximum, _manualSplitOverrides, existingSnapshot, cashMode: !IsJournalMode);

        foreach (var bucket in row.Buckets)
            if (bucket.ManualSplitCount.HasValue || _manualSplitOverrides.ContainsKey(bucket.Identity))
                _manualSplitOverrides[bucket.Identity] = bucket.ManualSplitCount;
    }

    private bool ValidatePlanningInputs()
    {
        if (!TryMaximumAmount(out _, showError: true)) return false;
        if (SelectedMethod.Equals("Manual Date", StringComparison.OrdinalIgnoreCase))
        {
            if (!ManualDatePicker.SelectedDate.HasValue)
            {
                MessageBox.Show(this, "Select the Manual Closing Date.", "Closure Entry", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            var (from, to) = AdvancedClosingEntryPlanner.FinancialYearRange(_client.FinancialYear);
            var date = ManualDatePicker.SelectedDate.Value.Date;
            if (date < from || date > to)
            {
                MessageBox.Show(this, "The Manual Closing Date is outside the selected Financial Year.", "Closure Entry", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
        }
        return true;
    }

    private void MarkPlansForRefresh()
    {
        foreach (var row in _rows)
        {
            row.Buckets.Clear();
            row.PlannedAmount = 0m;
            row.PlannedEntryCount = 0;
            row.Status = Math.Abs(row.ClosingBalance) < 0.005m ? "Zero closing" : "Ready";
            row.NotifyPlanChanged();
        }
    }

    private void CreateSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidatePlanningInputs()) return;
        PersistCommonSettings();
        var selected = _rows.Where(row => row.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Select one or more ledgers first.", "Closure Entry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RefreshExistingGeneratedSnapshot();
        foreach (var row in selected) ReplanRow(row);
        // Fix36: one ledger/source planning conflict must not block every other safe selected
        // ledger. Unsafe rows are skipped from this Create pass; their Details source panel can
        // be used to uncheck the problem source voucher and replan independently.
        var planningConflictRows = selected
            .Where(row => row.Status.StartsWith("Review", StringComparison.OrdinalIgnoreCase) ||
                          row.Buckets.Any(bucket => bucket.Status.StartsWith("Cannot", StringComparison.OrdinalIgnoreCase) ||
                                                    bucket.Status.StartsWith("Review", StringComparison.OrdinalIgnoreCase)))
            .ToHashSet();
        foreach (var row in planningConflictRows)
            row.Status = "Skipped - Closure planning conflict; review Details/source selection.";

        if (IsJournalMode)
        {
            var defaultOpposite = DefaultOppositeLedgerBox.Text?.Trim() ?? string.Empty;
            foreach (var row in selected.Where(row => !planningConflictRows.Contains(row) && !row.Status.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase)))
            {
                EnsureOppositeLedgerPreferenceLoaded(row);
                if (string.IsNullOrWhiteSpace(row.OppositeLedger)) row.OppositeLedger = defaultOpposite;
                if (string.IsNullOrWhiteSpace(row.OppositeLedger))
                {
                    MessageBox.Show(this, $"Select an opposite Journal Ledger for '{row.LedgerName}'. You can set a default at the top or override it from Details.", "Closure Entry", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (row.OppositeLedger.Equals(row.LedgerName, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, $"The opposite Journal Ledger for '{row.LedgerName}' must be different from the ledger being reversed.", "Closure Entry", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
        }

        var newEntries = selected
            .Where(row => !planningConflictRows.Contains(row) && !row.Status.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase))
            .SelectMany(row => row.Buckets.SelectMany(bucket => bucket.Entries.Where(entry => !entry.Existing).Select(entry => (Row: row, Bucket: bucket, Entry: entry))))
            .ToArray();
        if (newEntries.Length == 0)
        {
            MessageBox.Show(this, "There are no safe new reverse entries to create. Review Details/source selection for skipped ledgers, or the selected closure buckets are already generated/have nothing to close.", "Closure Entry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var plannedTotal = Math.Round(newEntries.Sum(item => item.Entry.Amount), 2, MidpointRounding.AwayFromZero);
        var activeLedgerCount = newEntries.Select(item => item.Row.LedgerName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var skippedLedgerCount = selected.Count(row => row.Status.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase));
        var confirmation = MessageBox.Show(this,
            $"Create {newEntries.Length:N0} reverse entr{(newEntries.Length == 1 ? "y" : "ies")} for {activeLedgerCount:N0} closable ledger(s)?\n\nMethod: {SelectedMethod}\nEntry Type: {(IsJournalMode ? "Journal" : "Cash")}\nPlanned Amount: {plannedTotal:N2}\nSkipped by safety: {skippedLedgerCount:N0} ledger(s)\nMaximum / Day: {(string.IsNullOrWhiteSpace(MaximumAmountBox.Text) ? "No limit" : MaximumAmountBox.Text.Trim())}",
            "Create Closure Entries", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;

        try
        {
            var created = IsJournalMode ? CreateJournalEntries(newEntries) : CreateCashEntries(newEntries);
            _ledgerTotals.Invalidate(_client.Id, _client.FinancialYear);
            _refreshAfterCreate();
            RefreshExistingGeneratedSnapshot();
            foreach (var row in selected) ReplanRow(row);
            StatusText.Text = $"Created {created:N0} Advanced Closure reverse entr{(created == 1 ? "y" : "ies")}. Existing deterministic split identities were skipped.";
            MessageBox.Show(this, $"Created {created:N0} closure entr{(created == 1 ? "y" : "ies")} successfully.", "Closure Entry", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Advanced Closure Entry stopped.\n\n{exception.Message}", "Closure Entry", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private int CreateCashEntries((AdvancedClosingLedgerRow Row, AdvancedClosingVoucherBucket Bucket, AdvancedClosingPlannedEntry Entry)[] items)
    {
        var service = new CashEntryService(_banking, _masters);
        var settings = service.EnsureSettings(_client.Id, _client.FinancialYear);
        var cashLedger = settings.DefaultCommonLedger.Trim();
        if (cashLedger.Length == 0) throw new InvalidOperationException("Cash Ledger could not be resolved from Cash Entry settings.");
        var existingJobs = (_banking.GetCashEntries(_client.Id, _client.FinancialYear) ?? [])
            .Where(item => item.SourceRunId.StartsWith(AdvancedClosingEntryPlanner.SourcePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.SourceRunId + "|" + item.SourceJobId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (fyFrom, fyTo) = AdvancedClosingEntryPlanner.FinancialYearRange(_client.FinancialYear);
        var transactionDateMode = SelectedMethod.Equals("As Per Transaction Dates", StringComparison.OrdinalIgnoreCase);
        var created = 0;

        // Fix34 final-create safety gate. Planning already applies the same rule, but creation
        // re-reads the live cached ledger immediately before saving so a stale/intervening change
        // cannot turn a valid Receipt into a same-side Credit-to-Credit or Payment into Debit-to-Debit.
        foreach (var ledgerItems in items.GroupBy(item => item.Row.LedgerName, StringComparer.OrdinalIgnoreCase))
        {
            var groupItems = ledgerItems.ToArray();
            var row = groupItems[0].Row;
            if (IsCashOrBankGroup(row.GroupName))
            {
                row.Status = $"Skipped - Cash mode cannot close {row.GroupName} against Cash.";
                continue;
            }

            var live = _ledgerTotals.Get(_client.Id, _client.FinancialYear, row.LedgerName);
            var liveClosing = Math.Round(live?.ClosingBalance ?? row.ClosingBalance, 2, MidpointRounding.AwayFromZero);
            if (!AdvancedClosingEntryPlanner.TryGetCashClosureSafety(
                    row.GroupName, liveClosing, out var requiredSourceType, out var requiredReverseDrCr, out var safetyReason))
            {
                row.Status = safetyReason;
                continue;
            }

            if (transactionDateMode && groupItems.Any(item => !item.Bucket.VoucherType.Equals(requiredSourceType, StringComparison.OrdinalIgnoreCase)))
            {
                row.Status = $"Skipped - Closure safety conflict: Cash mode requires {requiredSourceType} source vouchers for this ledger.";
                continue;
            }
            if (groupItems.Any(item => !item.Entry.DrCr.Equals(requiredReverseDrCr, StringComparison.OrdinalIgnoreCase)))
            {
                row.Status = $"Skipped - Closure safety conflict: live {row.GroupName} balance permits {requiredReverseDrCr} reverse only.";
                continue;
            }
            if (transactionDateMode && groupItems.Any(item => item.Entry.SourceDate.HasValue && item.Entry.Date.Date < item.Entry.SourceDate.Value.Date))
            {
                row.Status = "Skipped - Closure safety conflict: a planned Cash reverse predates its own source voucher.";
                continue;
            }
            if (groupItems.Any(item => item.Entry.Date.Date < fyFrom || item.Entry.Date.Date > fyTo))
            {
                row.Status = "Skipped - Closure safety conflict: planned Cash date is outside the Financial Year.";
                continue;
            }

            var groupTotal = Math.Round(groupItems.Sum(item => item.Entry.Amount), 2, MidpointRounding.AwayFromZero);
            if (groupTotal > Math.Abs(liveClosing) + 0.005m)
            {
                row.Status = $"Skipped - Closure safety conflict: planned Cash {groupTotal:N2} exceeds live closing {Math.Abs(liveClosing):N2}.";
                continue;
            }

            foreach (var item in groupItems)
            {
                var jobId = $"SPLIT-{item.Entry.Sequence:D3}";
                if (!existingJobs.Add(item.Bucket.Identity + "|" + jobId)) continue;
                var amount = Math.Round(item.Entry.Amount, 2, MidpointRounding.AwayFromZero);
                var dr = item.Entry.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase);
                var entry = new CashEntryRecord
                {
                    ClientId = _client.Id,
                    FinancialYear = _client.FinancialYear,
                    EntryDate = item.Entry.Date.Date,
                    EntryLedger = item.Row.LedgerName,
                    CommonLedger = cashLedger,
                    MappedMasterLedger = item.Row.LedgerName,
                    Narration = AdvancedClosingEntryPlanner.BuildSimpleCashNarration(item.Entry.DrCr),
                    Debit = dr ? amount : 0m,
                    Credit = dr ? 0m : amount,
                    SingleAmount = amount,
                    SingleDrCr = dr ? "Dr" : "Cr",
                    WorkflowStatus = "Ready",
                    Status = "Ready",
                    EntryId = $"ACLOSE-{item.Bucket.Token}-{item.Entry.Sequence:D3}",
                    ReferenceNumber = AdvancedClosingEntryPlanner.BuildCompactCashReference(item.Bucket, item.Entry.Sequence, item.Entry.DrCr),
                    SourceRunId = item.Bucket.Identity,
                    SourceJobId = jobId,
                    CreatedBy = Environment.UserName,
                    IsUserEdited = true,
                    UpdatedUtc = DateTime.UtcNow
                };
                var saved = service.ValidateAndSave(entry, settings);
                if (!saved.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{item.Row.LedgerName}: {saved.Status}");
                created++;
            }
        }
        if (created > 0) _liveChangeHub.Publish(_client.Id, _client.FinancialYear, AccountingLiveArea.Cash, "Advanced Closure Entry cash reverse entries");
        return created;
    }

    private int CreateJournalEntries((AdvancedClosingLedgerRow Row, AdvancedClosingVoucherBucket Bucket, AdvancedClosingPlannedEntry Entry)[] items)
    {
        var existingJobs = (_journals.GetVouchers(_client.Id, _client.FinancialYear) ?? [])
            .Where(item => item.SourceRunId.StartsWith(AdvancedClosingEntryPlanner.SourcePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.SourceRunId + "|" + item.SourceJobId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = 0;
        var (fyFrom, fyTo) = AdvancedClosingEntryPlanner.FinancialYearRange(_client.FinancialYear);
        var transactionDateMode = SelectedMethod.Equals("As Per Transaction Dates", StringComparison.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (transactionDateMode &&
                !item.Bucket.VoucherType.Equals("Sales", StringComparison.OrdinalIgnoreCase) &&
                !item.Bucket.VoucherType.Equals("Purchase", StringComparison.OrdinalIgnoreCase))
            {
                item.Row.Status = "Skipped - Journal transaction-date closure accepts Sales/Purchase source vouchers only.";
                continue;
            }
            if (item.Entry.SourceDate.HasValue && item.Entry.Date.Date < item.Entry.SourceDate.Value.Date)
            {
                item.Row.Status = "Skipped - Closure safety conflict: planned Journal reverse predates its own source voucher.";
                continue;
            }
            if (item.Entry.Date.Date < fyFrom || item.Entry.Date.Date > fyTo)
            {
                item.Row.Status = "Skipped - Closure safety conflict: planned Journal date is outside the Financial Year.";
                continue;
            }
            var jobId = $"SPLIT-{item.Entry.Sequence:D3}";
            if (!existingJobs.Add(item.Bucket.Identity + "|" + jobId)) continue;
            var amount = Math.Round(item.Entry.Amount, 2, MidpointRounding.AwayFromZero);
            var selectedDr = item.Entry.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase);
            var date = item.Entry.Date.Date;
            var periodKey = GstJournalGenerationService.PeriodKey(date, "Monthly");
            var voucher = new JournalVoucher
            {
                ClientId = _client.Id,
                FinancialYear = _client.FinancialYear,
                PeriodKey = periodKey,
                PeriodLabel = GstJournalGenerationService.PeriodLabel(periodKey, "Monthly"),
                VoucherDate = date,
                VoucherNumber = $"ACLOSE-{item.Bucket.Token}-{item.Entry.Sequence:D3}",
                VoucherType = "Journal",
                Narration = BuildNarration(item.Row, item.Bucket),
                GenerationMode = "Manual",
                WorkflowStatus = "Ready",
                ReferenceNumber = item.Bucket.VoucherType,
                SourceRunId = item.Bucket.Identity,
                SourceJobId = jobId,
                CreatedBy = Environment.UserName,
                IsUserEdited = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                Lines =
                [
                    new JournalLine { LineNumber = 1, LedgerName = item.Row.LedgerName, Amount = amount, DrCr = selectedDr ? "Dr" : "Cr", LineRole = "Advanced Closing" },
                    new JournalLine { LineNumber = 2, LedgerName = item.Row.OppositeLedger, Amount = amount, DrCr = selectedDr ? "Cr" : "Dr", LineRole = "Advanced Closing Opposite" }
                ]
            };
            _journals.SaveVoucher(voucher);
            created++;
        }
        if (created > 0) _liveChangeHub.Publish(_client.Id, _client.FinancialYear, AccountingLiveArea.Journal, "Advanced Closure Entry journal reverse entries");
        return created;
    }

    private string BuildNarration(AdvancedClosingLedgerRow row, AdvancedClosingVoucherBucket bucket) =>
        $"Advanced closure - {SelectedMethod} - {row.LedgerName} - {bucket.VoucherType}";

    private static bool IsCashOrBankGroup(string? group) =>
        group is not null && (group.Equals("Cash-in-Hand", StringComparison.OrdinalIgnoreCase) || group.Equals("Cash-in-hand", StringComparison.OrdinalIgnoreCase) || group.Equals("Bank Accounts", StringComparison.OrdinalIgnoreCase));
}
