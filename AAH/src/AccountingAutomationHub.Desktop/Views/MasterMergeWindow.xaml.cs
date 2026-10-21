using System.Windows;
using System.Windows.Controls;
using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Desktop.Views;

public partial class MasterMergeWindow : Window
{
    private readonly bool _fixedPair;

    public MasterMergeWindow(
        IReadOnlyList<MasterRecord> sourceCandidates,
        IReadOnlyList<MasterRecord> targetCandidates,
        long preferredPersistentId = 0,
        bool fixedPair = false)
    {
        InitializeComponent();
        _fixedPair = fixedPair;
        FromBox.ItemsSource = sourceCandidates;
        IntoBox.ItemsSource = targetCandidates;

        var preferredSource = sourceCandidates.FirstOrDefault(item => item.PersistentId == preferredPersistentId)
                              ?? sourceCandidates.OrderBy(item => item.UsageCount).ThenBy(item => item.PersistentId).FirstOrDefault();
        if (preferredSource is not null)
        {
            FromBox.SelectedItem = preferredSource;
            IntoBox.SelectedItem = targetCandidates.FirstOrDefault(item => item.PersistentId != preferredSource.PersistentId);
        }

        if (_fixedPair)
        {
            FromBox.IsDropDownOpen = false;
            IntoBox.IsDropDownOpen = false;
            FromBox.IsHitTestVisible = false;
            IntoBox.IsHitTestVisible = false;
            FromBox.Focusable = false;
            IntoBox.Focusable = false;
        }

        UpdateReview();
    }

    public MasterRecord? SourceMaster => FromBox.SelectedItem as MasterRecord;
    public MasterRecord? TargetMaster => IntoBox.SelectedItem as MasterRecord;

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateReview();

    private void UpdateReview()
    {
        if (ReviewText is null) return;
        if (SourceMaster is null || TargetMaster is null)
        {
            ReviewText.Text = "Choose FROM and INTO.";
            return;
        }
        if (SourceMaster.PersistentId == TargetMaster.PersistentId)
        {
            ReviewText.Text = "FROM and INTO must be different Masters.";
            return;
        }

        var opening = SourceMaster.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)
            ? $"  Opening: {(SourceMaster.OpeningBalance ?? 0m):N2} + {(TargetMaster.OpeningBalance ?? 0m):N2} = {((SourceMaster.OpeningBalance ?? 0m) + (TargetMaster.OpeningBalance ?? 0m)):N2} (+Dr / -Cr)."
            : string.Empty;
        ReviewText.Text = $"{SourceMaster.Name}  ->  {TargetMaster.Name}.{opening}";
    }

    private void Interchange_Click(object sender, RoutedEventArgs e)
    {
        if (SourceMaster is not { } source || TargetMaster is not { } target) return;

        var swappedSource = FromBox.Items.OfType<MasterRecord>()
            .FirstOrDefault(item => item.PersistentId == target.PersistentId);
        var swappedTarget = IntoBox.Items.OfType<MasterRecord>()
            .FirstOrDefault(item => item.PersistentId == source.PersistentId);
        if (swappedSource is null || swappedTarget is null) return;

        FromBox.SelectedItem = swappedSource;
        IntoBox.SelectedItem = swappedTarget;
        UpdateReview();
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (SourceMaster is null || TargetMaster is null || SourceMaster.PersistentId == TargetMaster.PersistentId)
        {
            MessageBox.Show(this, "Choose different FROM and INTO Masters.", "Merge Masters", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(
            this,
            $"Merge FROM '{SourceMaster.Name}' INTO '{TargetMaster.Name}'?\n\nLedger opening balances will be combined. If the current client has a linked/open Tally company, AAH will update and live-verify Tally before the local merge.",
            "Confirm Merge + Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        DialogResult = true;
    }
}
