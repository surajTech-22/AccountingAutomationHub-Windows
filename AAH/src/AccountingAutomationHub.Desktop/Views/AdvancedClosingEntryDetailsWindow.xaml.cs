using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccountingAutomationHub.Desktop.Models;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AdvancedClosingEntryDetailsWindow : Window
{
    private readonly AdvancedClosingLedgerRow _row;
    private readonly bool _journalMode;
    private readonly Action<AdvancedClosingLedgerRow> _replan;
    private bool _loadingOpposite;

    public AdvancedClosingEntryDetailsWindow(
        AdvancedClosingLedgerRow row,
        bool journalMode,
        IReadOnlyList<string> allLedgers,
        Action<AdvancedClosingLedgerRow> replan)
    {
        InitializeComponent();
        _row = row;
        _journalMode = journalMode;
        _replan = replan;
        DataContext = row;
        LedgerCaption.Text = $"{row.LedgerName}  •  {row.GroupName}  •  Closing {row.ClosingText}";
        JournalLedgerPanel.Visibility = journalMode ? Visibility.Visible : Visibility.Collapsed;
        _loadingOpposite = true;
        OppositeLedgerBox.ItemsSource = allLedgers.Where(name => !name.Equals(row.LedgerName, StringComparison.OrdinalIgnoreCase)).ToArray();
        OppositeLedgerBox.Text = row.OppositeLedger;
        _loadingOpposite = false;
        if (row.Buckets.Count > 0) BucketGrid.SelectedIndex = 0;
        RefreshSourcePlanSummary();
    }

    private void SourceInclude_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AdvancedClosingSourceSelection source) return;
        if (sender is CheckBox check) source.IsIncluded = check.IsChecked == true;
        ReplanSourceChoices();
    }

    private void SelectAllSources_Click(object sender, RoutedEventArgs e)
    {
        foreach (var source in _row.SourceSelections) source.IsIncluded = true;
        ReplanSourceChoices();
    }

    private void ClearAllSources_Click(object sender, RoutedEventArgs e)
    {
        foreach (var source in _row.SourceSelections) source.IsIncluded = false;
        ReplanSourceChoices();
    }

    private void SourceGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        var selected = SourceGrid.SelectedItems.Cast<AdvancedClosingSourceSelection>().ToArray();
        if (selected.Length == 0 && SourceGrid.CurrentItem is AdvancedClosingSourceSelection current) selected = [current];
        if (selected.Length == 0) return;
        var include = selected.Any(item => !item.IsIncluded);
        foreach (var source in selected) source.IsIncluded = include;
        e.Handled = true;
        ReplanSourceChoices();
    }

    private void ReplanSourceChoices()
    {
        SaveOppositeLedger();
        _replan(_row);
        BucketGrid.ItemsSource = _row.Buckets;
        if (_row.Buckets.Count > 0)
        {
            BucketGrid.SelectedIndex = 0;
            EntryGrid.ItemsSource = _row.Buckets[0].Entries;
        }
        else
        {
            EntryGrid.ItemsSource = null;
        }
        RefreshSourcePlanSummary();
    }

    private void RefreshSourcePlanSummary()
    {
        var plannedBySource = _row.Buckets
            .SelectMany(bucket => bucket.Entries.Where(entry => !entry.Existing && !string.IsNullOrWhiteSpace(entry.SourceVoucherIdentity)))
            .GroupBy(entry => entry.SourceVoucherIdentity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Amount), StringComparer.OrdinalIgnoreCase);

        foreach (var source in _row.SourceSelections)
            source.PlannedReverseAmount = plannedBySource.TryGetValue(source.SourceVoucherIdentity, out var amount) ? amount : 0m;

        var selected = _row.SourceSelections.Count(item => item.IsIncluded);
        var ignored = _row.SourceSelections.Count - selected;
        var planned = _row.SourceSelections.Sum(item => item.PlannedReverseAmount);
        SourceSelectionCaption.Text = _row.SourceSelections.Count == 0
            ? "No selectable Sales/Purchase source vouchers are available for the current Transaction-Date plan."
            : $"{selected:N0} included • {ignored:N0} ignored • {planned:N2} currently planned from selected sources. Changes apply only to this Closure session.";
    }

    private void BucketGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EntryGrid.ItemsSource = BucketGrid.SelectedItem is AdvancedClosingVoucherBucket bucket ? bucket.Entries : null;
    }

    private void AutoSplit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AdvancedClosingVoucherBucket bucket) return;
        bucket.ManualSplitCount = null;
        ReplanAndRestore(bucket.Identity);
    }

    private void MinusSplit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AdvancedClosingVoucherBucket bucket) return;
        var current = bucket.ManualSplitCount ?? Math.Max(1, bucket.Entries.Count(item => !item.Existing));
        bucket.ManualSplitCount = Math.Max(1, current - 1);
        ReplanAndRestore(bucket.Identity);
    }

    private void PlusSplit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AdvancedClosingVoucherBucket bucket) return;
        var current = bucket.ManualSplitCount ?? Math.Max(1, bucket.Entries.Count(item => !item.Existing));
        bucket.ManualSplitCount = current + 1;
        ReplanAndRestore(bucket.Identity);
    }

    private void SplitText_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not AdvancedClosingVoucherBucket bucket) return;
        var text = box.Text.Trim();
        if (text.Equals("Auto", StringComparison.OrdinalIgnoreCase) || text.Length == 0)
            bucket.ManualSplitCount = null;
        else if (int.TryParse(text, out var count) && count > 0)
            bucket.ManualSplitCount = count;
        else
        {
            box.Text = bucket.SplitText;
            return;
        }
        ReplanAndRestore(bucket.Identity);
    }

    private void OppositeLedgerBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveOppositeLedger();
    private void OppositeLedgerBox_LostFocus(object sender, RoutedEventArgs e) => SaveOppositeLedger();

    private void SaveOppositeLedger()
    {
        if (_loadingOpposite || !_journalMode) return;
        _row.OppositeLedger = OppositeLedgerBox.Text?.Trim() ?? string.Empty;
    }

    private void ReplanAndRestore(string identity)
    {
        SaveOppositeLedger();
        _replan(_row);
        var selected = _row.Buckets.FirstOrDefault(item => item.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            BucketGrid.SelectedItem = selected;
            EntryGrid.ItemsSource = selected.Entries;
        }
        // Bucket/entry collections and their properties notify the UI directly.
        // Do not force-refresh while the split TextBox may still own keyboard focus.
        BucketGrid.ItemsSource = _row.Buckets;
        EntryGrid.ItemsSource = selected?.Entries;
        RefreshSourcePlanSummary();
    }
}
